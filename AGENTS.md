# AGENTS.md — Kaleidoscope 设备通讯库

> 本文件是 AI 助手在维护本库前的**强制前置阅读**。Kaleidoscope 是从 CommandCenter 现场项目
> 抽取的通用通讯/图片存储库，目标：**换新客户做新界面时底层服务一行不改**。
> 优先级：本文件 > CommandCenter/AGENTS.md 中的通用红线 > 通用最佳实践。

## 角色与定位

你是本通讯库的**资深维护工程师**。改动必须**可编译、可运行、风格统一、注释详尽**。
改动涉及通讯行为时必须同步 `README.md`；沉淀新红线时同步本文件。

## 技术栈

- .NET Framework **4.7.2**，C# `LangVersion=7.3`（WinForms 业务项目引用，勿引入 .NET Core 语法/API）
- 通讯：**NModbus 3.0.83**（Modbus TCP 从站/主站，汇川 PLC）+ **NModbus.Serial 3.0.83**（Modbus RTU 主站，气压表）+ 基恩士 IV4 相机 TCP 无协议 + 基恩士 SR 扫码枪 TCP/串口 + IO 耦合器/送风机 Modbus TCP 主站
- **依赖策略**：第三方库拷 `libs/` 由 csproj `<Reference HintPath>` 引用，**离线可编译**，不依赖 NuGet restore
- 序列化 Newtonsoft.Json **不引入**本库；设备配置持久化走库内置 `Configuration/ConfigSerializer`（.NET 内置 `DataContractJsonSerializer`，零第三方依赖），业务项目无需再自己写反序列化（旧 Demo 的 Newtonsoft 写法已弃用）；`Models/*.cs` 已带 `System.ComponentModel` 中文元数据（DisplayName/Description/Category），供 `ConfigEditor/` 可视化编辑器自动渲染参数界面
- System.Management（WMI 串口自动识别：气压表 CH340 / 扫码枪按 DeviceKeyword），csproj 已引用

## 跨 .NET 版本兼容性（重要：用户项目可能后续迁 .NET Core/.NET 5+）

> 本库当前目标 **net472**，但迁移到 .NET Core/.NET 5+ 是明确的路。业务代码本身
> 不含 netfx 专属 API，真正风险只有一个点，迁库前先读这段。

| 依赖 | 现状 | .NET Core/.NET 5+ 情况 |
| --- | --- | --- |
| **NModbus 3.0.83** | 目标 **net46**（用 AssemblyName.GetAssemblyName 可查） | ⚠️ **唯一风险点**：老 netfx 库，.NET 5+ 引用可用（compat）但跨平台有隐患。**迁库时优先替换成 netstandard 版 NModbus（4.x）**，`libs/NModbus.dll`/`libs/NModbus.Serial.dll` 换新 + csproj 保持 HintPath 引用即可，本库调用方式（`ModbusTcpSlaveNetwork`/`CreateSlaveNetwork`/`SerialPortAdapter`）不变。 |
| System.IO.Ports（串口扫码枪） | csproj Reference | ✅ .NET Core 3.0+ 同名 API，Windows 可用；若跨平台需按目标框架条件引用 |
| System.Drawing（ImageStore 位图） | csproj Reference | ✅ .NET Core 3.0+ 为 System.Drawing.Common，**.NET 6+ 仅 Windows**（现场即 Windows，无碍）；跨平台需用 ImageSharp 等替代 |
| 其余（TcpClient/Timer/Task/FileSystemWatcher） | 标准 BCL | ✅ 全平台通用 |

**迁移方案建议**：csproj 改 SDK 风格并多目标（如 `net472;net6.0-windows`），System.IO.Ports 与
System.Drawing 按 `$(TargetFramework)` 条件引用；NModbus 换 netstandard 版。库内服务类
（PlcService/ModbusTcpMasterClient/KeyenceIV4Camera/ScannerXxx/ImageStore/ConnectionMonitor/DeviceHub/
ModbusRtuBarometerReader/ModbusTcpIoController/FanControllerClient/MockXxx）不涉及
具体框架 API，迁移时**业务代码不需要改**。

## 铁律（违反即返工）

1. **文件编码 UTF-8**。写文件用 write 工具，中文内容写后自查 `[IO.File]::ReadAllText(path, UTF8).Contains("预期中文")`。
2. **不提交运行时数据与机密**：`bin/`、`obj/`、日志一律 gitignore。
3. **改动后必须构建验证**：MSBuild 编译 Kaleidoscope.csproj，禁止提交编译不过的代码。
4. **不主动 commit/push**，除非用户明确要求；提交前先 `git status` + `git diff`。
5. **UI 线程禁做网络 IO**：连接/读写一律服务后台线程；TCP 连接必须 `BeginConnect + WaitOne` 强制超时。
6. **服务必须支持热更**：`Dispose` 干净（限时抢锁 + 锁外强断网）、惰性连接自动重连、状态集中在实例内无残留。`DeviceHub.ApplyConfig` 是热更唯一入口。

## 代码约定

- 类/方法/属性 PascalCase；私有字段 `_camelCase`；接口前缀 `I`；事件 `PascalCase` 命名。
- 命名空间：`Kaleidoscope.Models`（配置）、`Kaleidoscope.Services`（服务）、`Kaleidoscope.Utils`（工具）、`Kaleidoscope.Configuration`（配置持久化/校验）。
- **配置持久化约定（V1.2.4 起）**：`ConfigSerializer` 读写 `.kcfg` JSON（UTF-8 无 BOM、缩进、中文直读）；DataContractJsonSerializer 缺字段自动补默认值（版本兼容）、显式 null 会覆盖默认值（必须经 `EnsureSafe` 兜底）；`DeviceHubConfigValidator` 保存前必校验（IP/端口/寄存器越界，Errors 必须修 Warnings 建议修）。新增配置模型字段时无需迁移旧文件。
- **配置序列化约定**：串口停止位存字符串 `"1"/"15"/"2"`；校验位存枚举名 `None/Odd/Even/Mark/Space`；PLC 地址存 **DataStore 索引**（协议号 = 索引 + 40000）。读写两端大小写兼容。
- **配置模型元数据（V1.3.0 起，可视化编辑器地基）**：`Models/*.cs` 每个公开属性必须带 `[DisplayName(中文名)]` + `[Description(说明)]`；顶层聚合配置（如 `DeviceHubConfig`）按业务语义再加 `[Category]` 分组。**新增配置字段时同步补**，否则编辑器/业务界面只显示英文属性名，视为遗漏返工。
- 服务层事件一律**工作线程触发**，UI 订阅方自行 `Invoke`；库内不做 UI 线程跳转。
- 日志只记边沿（连上/断开各一次），连续失败中间静默节流，防刷屏。

## 注释要求（本库第一红线，必须遵守）

> 从 CommandCenter 沉淀：**注释要详细，让小白能看懂**。本库就是给别人快速接入用的，
> 注释就是"接口文档"，写不清楚等于没写。

- 每个**类**头部 XML 注释必须写清：**① 干什么（职责）② 为什么这么设计（现场背景/踩过的坑）③ 怎么接/怎么改（调用方注意点）**。
- 每个**公开方法/属性/事件**都要有 `///` 注释：**参数含义、返回值、在哪个线程触发、什么时候调、失败怎么处理**。
- 每个**边界/关键逻辑**（超时、重连节流、断连标记、线程退出）要有行内注释解释"为什么"。
- **禁止废话注释**（如 `i++ // 自增`），但**禁止关键逻辑无注释**——缺注释宁可多写两行。
- 参考基准：`Services/DeviceHub.cs`（门面类）、`Services/PlcService.cs`、`Models/CameraConfig.cs` 头部注释风格。

## 架构约束（DeviceHub 是门面，分层不可破坏）

```
业务界面（新项目，只写 UI 和业务编排）
   │  只依赖：DeviceHub + 各服务实例 + 强类型配置
   ▼
DeviceHub（门面：建/启/事件聚合/热更/释放 全链路编排）
   │  内部创建并持有全部服务实例
   ▼
PlcService / ModbusTcpMasterClient / KeyenceIV4Camera / IScanner(串口+TCP) / ImageStore / ConnectionMonitor
IBarometerReader(气压表 Modbus RTU) / IIoController(IO 耦合器 Modbus TCP) / IFanController(送风机 Modbus TCP)
```

- **业务层禁止新建 TcpClient/串口/连接**：服务内部惰性建连 + 自动重连，业务层只调用服务公开方法、订阅事件。
- **PLC 主站/从站两模式（V1.2.0）**：`DeviceHubConfig.PlcRole` 决定——`Slave`（默认）用
  `hub.Plc`（`PlcService` 从站监听）、`Master` 用 `hub.PlcMaster`（`ModbusTcpMasterClient` 主动读写，
  `Start()` 自动轮询 `PlcMasterConfig.PollItems`）。两模式连接状态都聚合为 `HubDeviceKind.Plc`；
  `ConnectionMonitor` 构造两参数二选一（另一传 null），主站模式下 `Plc` 为 null，
  从站模式 `PlcMaster` 为 null——改代码时勿忘判空。
- **热更**：`DeviceHub.ApplyConfig(newConfig)` → 释放（监控→PLC→气压表/IO/送风机→扫码枪→相机→图像存储）→ 重建 → 触发 `ServicesRebuilt`；上层在回调里重建自己的业务协调器。
- **ImageStore 归 DeviceHub 所有**：`DeviceHub.Dispose`/`ApplyConfig` 显式释放（FileSystemWatcher 句柄），其他对象不得代关。
- **新增设备类型**：先写服务类（独立后台线程 + 惰性连接 + Dispose 干净），再在 `DeviceHubConfig` 加配置段、`DeviceHub.BuildServices` 建实例、`SubscribeAggregateEvents` 聚合事件。
- **Mock 三件套**：气压表/IO/送风机各自有 `MockXxx` 实现（随机数据模拟），`DeviceHubConfig.UseMockCommunication=true` 时全部用 Mock，不接设备跑通 UI/业务；接真机改回 false，业务代码不动。扫码枪/PLC/相机不受此开关影响。
- **ConfigEditor（V1.3.0，独立工具不进库）**：`ConfigEditor/` 可视化配置编辑器只产 `.kcfg`、不启停设备；靠 Models 元数据自动渲染属性网格，**新增配置字段无需改编辑器代码**；`BrandPresets.cs` 品牌预设只收敛参数差异，协议差异仍需改库；产出文件经 `ConfigSerializer.Load` + `ApplyConfig` 接入。编辑器自身遵循"改动必须可编译"铁律（同库工程构建命令）。

## 已知通讯关键点（改之前先读对应文件注释）

- **PLC 从站网络释放（V2.14.23 血泪）**：重建/Dispose 时除 `_cts.Cancel()`/`_listener.Stop()` 外**必须 `_network?.Dispose()`**（NModbus `ModbusTcpSlaveNetwork` 实现了 IDisposable，会停止 TcpListener 并关闭所有已连入的 master TCP 会话；只 Stop listener 会让 PLC 主站认为旧连接还活着、不重连新从站 → 通讯假死）。三处清理点统一补。
- **PLC 从站不依赖主站轮询节奏（V1.2.3 审查结论）**：从站模式上位机是被动响应方，PLC 轮询时间改大/改小都兼容（改小握手快、改大握手慢，值"保持到对端响应才复位"，不丢不冲；`_lock` 串行化、`MasterPollTick` 1s 只查 TCP 会话、`TcpKeepAlive` 5s 只探活，均不受快慢影响）。改代码时勿引入"假设 PLC 每隔固定周期来读写"的逻辑（如按 PLC 轮询周期估时判活）。两条边界在业务层：① 协调器"等 PLC 复位请求"超时须 > PLC 最慢轮询周期；② PLC 梯形图发下一拍请求前必须确认上位机结果已回 0（快轮询下不合规梯形图混拍窗口更频繁）。
- **PLC 主站（V1.2.0）**：`ModbusTcpMasterClient` 是通用 Modbus TCP 主站（可连 PLC/远程 IO/仪表），
  范式对齐 `ModbusTcpIoController`——BeginConnect+WaitOne 强制超时、`_syncRoot` 锁串行化、读写失败
  断连标记；读离散输入用 `IModbusMaster.ReadInputs`（NModbus 3.0.83 无 `ReadDiscreteInputs` 方法）；
  轮询自愈：`PollTick` 每周期先 `EnsureConnected` 再读，断线会自动重连回传；`PlcMasterConfig`
  寄存器地址一律填 Modbus 协议地址（0x0000 起），**不要**混用从站的 DataStore 索引约定。
- **相机"判定即写"（V2.13.7）**：T2 判定一返回立即写 PLC 结果（1/2），不等 FTP 取图归档；通道释放必须等"PLC 已复位请求 **且** `_taskDone`"，否则下一拍请求进来开新 Task 造成同相机并发取图/删源混图。
- **扫码枪 TCP 非连上即回**：连上后必须发触发指令（`ScanConfig.TriggerCommand`，默认 `LON`）才读码，连接/重连成功自动发一次；串口上电即读码、`SendTrigger` 为空操作。
- **图片显示不等归档**：FTP 取图"jpeg 一到目录 → 后台解码缩略图 → 提前塞事件"与"归档复制+删源"解耦，UI 不等 iv4p 复制。
- **PW 同程序号跳过（V2.14.19）**：相机 `SwitchProgram` 缓存上次成功程序号，目标一致直接 return，省 200~390ms；**连接重建必须在 `EnsureConnected` 成功处把缓存重置 -1**，否则相机恢复默认程序后缓存骗过跳过、错拍。
- **存图清理防误删**：`RunCleanupOnce` 只扫存图根目录顶层；快速路径按日期目录名判定，通用路径递归查**所有文件**早于阈值才删；根目录是盘符（如 `E:\`）直接放弃并告警。
- **图片一律后台解码 + 缩略图**：禁止在 UI 线程"读盘 + GDI+ 解码 + 全尺寸大图赋值"（基恩士原图 2592×1944 会卡死界面）。
- **气压表 RTU（Aging 沉淀）**：压力读 `BarometerPressureRegisterAddress`（默认 0x04@0x0001）
  取 `BarometerReadRegisterCount`（默认 2）个寄存器、单位 kPa、小数位固定用配置（0x0002 不可靠）；
  写阈值 `BarometerThresholdRegisterAddress`（默认 0x06@0x0010，**V1.2.3 起可配，勿再写死**），
  SetAllThresholds 必须 50ms 间隔逐台写（串口共享会互相干扰）；串口 CH340 用 WMI 双重校验
  （Caption 含 CH340 **且** PNPDeviceID 含 VID_1A86/PID_7523），命中后写 `BarometerPort.cache`
  记忆端口，换台电脑自动找回。
- **IO 耦合器 TCP（Aging 沉淀）**：DI 0x04@0x1000、DO 0x03/0x06@0x2000，**16 点/寄存器**；
  单点写输出必须"读-改-写"（读整字回读原值 → 按位或 → 写回），否则会覆盖同寄存器其它输出；
  `MapOutputChannel` 走备用通道映射（业务输出号 → 物理输出号）。
- **送风机 TCP（Aging 沉淀，V1.2.3 起映射全配置化）**：端口 **50000** 不是 502；寄存器映射
  全部走 FanConfig（读区块 `FanStatusStartAddress`+`FanStatusCount`、字段偏移 `Fan*Offset`、
  控制寄存器 `FanControlAddress`、命令码 `FanStartCommand`/`FanStopCommand`），默认值对应
  "0x0001 控制字（0x0003=定值启动/0x0002=定值停止），0x0002~0x0005 是 温度/湿度/温度设定/
  湿度设定（**除以 100** 才是真实值）"，**换厂商改配置不改库**；状态解析优先按配置命令码
  识别定值启停，再回退枚举。IP 记忆到 `FanLastIp.cache`，`FanIpCandidates` 自动探测兜底；
  服务内部 10s 重连节流，与监控器 5s 节流叠加。
- **扫码枪自动识别（Aging 沉淀）**：`PortName` 留空 → WMI 按 `DeviceKeyword`（默认 "Xenon 1902"）查设备名定位串口；心跳 3s 双信号判定（WMI 搜索 + 系统串口列表）+ 每 4 次心跳"关-重搜-重开"兜底，拔枪几秒内变"未连接"、插回自动恢复。

## 构建命令

```powershell
# 在仓库根（E:\Project\CommonLib）执行：
& "D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
  Kaleidoscope/Kaleidoscope.csproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /nologo /v:m /m
```

- 成功标准：输出 `Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll` 且无 error。
- Demo 测试台同理（引用 Kaleidoscope bin 输出）：`Demo/KaleidoscopeDemo.csproj` 构建后再跑 `Demo\bin\Debug\KaleidoscopeDemo.exe`。
- 可视化配置编辑器同理：`ConfigEditor/KaleidoscopeConfigEditor.csproj` 构建后跑 `ConfigEditor\bin\Debug\KaleidoscopeConfigEditor.exe`（可命令行传 .kcfg 直接打开）。
- 无单元测试框架；以构建通过 + Demo 冒烟测试为验证手段。

## 文档同步（铁律：每次任务主动完成，不许等提醒）

- **`README.md`**（本目录）：目录结构、接入范式、红线、配置项有变化时同步更新。
- **本文件**：新增红线/约定/架构变化时同步更新。
- **`CHANGELOG.md`**（本目录，仓库根版本记录）：改动再小也记（本库虽独立，但改动源自 CommandCenter，需在版本演进中留痕）。
