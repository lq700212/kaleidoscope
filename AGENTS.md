# AGENTS.md — Kaleidoscope 设备通讯库

> 本文件是 AI 助手在维护本库前的**强制前置阅读**。Kaleidoscope 是从 CommandCenter 现场项目
> 抽取的通用通讯/图片存储库，目标：**换新客户做新界面时底层服务一行不改**。
> 优先级：本文件 > CommandCenter/AGENTS.md 中的通用红线 > 通用最佳实践。

## 角色与定位

你是本通讯库的**资深维护工程师**。改动必须**可编译、可运行、风格统一、注释详尽**。
改动涉及通讯行为时必须同步 `README.md`；沉淀新红线时同步本文件。

## 技术栈

- .NET Framework **4.7.2**，C# `LangVersion=7.3`（WinForms 业务项目引用，勿引入 .NET Core 语法/API）
- 通讯：**NModbus 3.0.83**（Modbus TCP 从站/主站，汇川 PLC）+ **NModbus.Serial 3.0.83**（Modbus RTU 主站，气压表）+ **HslCommunication 12.6.0.0（V1.6.0 起集成的商业通讯库：多品牌 PLC 主站三菱/欧姆龙/西门子/汇川 + EtherNet/IP 扫码枪；商业授权、授权码需自行购买，统一放 exe 同目录 hsl.dat 文件由 Utils/HslAuthorization.cs 加载，仅 Services 层依赖，Models/Configuration 零 HSL 引用）**+ 基恩士 IV4 相机 TCP 无协议 + 基恩士 SR 扫码枪 TCP/串口/EIP + IO 耦合器/送风机 Modbus TCP 主站
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
| HslCommunication 12.6.0.0 | 目标 net45/netstandard2.0 双版本（libs 内为 netfx 版） | ✅ 官方提供 netstandard2.0 版，迁移时换 `libs/HslCommunication.dll` 为 netstandard 版 + 保持 HintPath 即可，调用方式不变 |
| 其余（TcpClient/Timer/Task/FileSystemWatcher） | 标准 BCL | ✅ 全平台通用 |

**迁移方案建议**：csproj 改 SDK 风格并多目标（如 `net472;net6.0-windows`），System.IO.Ports 与
System.Drawing 按 `$(TargetFramework)` 条件引用；NModbus 换 netstandard 版。库内服务类
（PlcService/ModbusTcpMasterClient/KeyenceIV4Camera/ScannerXxx/ImageStore/ConnectionMonitor/DeviceHub/
ModbusRtuBarometerReader/ModbusTcpIoController/FanControllerClient/MockXxx）不涉及
具体框架 API，迁移时**业务代码不需要改**。

## 铁律（违反即返工）

1. **文件编码 UTF-8**。写文件用 write 工具，中文内容写后自查 `[IO.File]::ReadAllText(path, UTF8).Contains("预期中文")`。
2. **不提交运行时数据与机密，且每次改动后主动复查 .gitignore**（不要等提醒）：
   - `bin/`、`obj/`、日志、`hsl.dat`（授权码）、`*.kcfg`（现场配置）一律 gitignore；
   - **新增会落盘的运行时产物/机密/本地状态时，同步补 .gitignore 规则并跑 `git check-ignore <路径>` 验证命中**；
   - **每轮任务收尾前跑一次 `git status --short`**：出现非预期的 `??`（未跟踪）文件 = 疑似遗漏忽略或垃圾文件，先甄别再决定忽略或删除；
   - 2026-08 复盘：HSL 授权码曾差点因"只加了规则没复查实体文件位置"产生遗漏——规则与实体要两头验证。
3. **改动后必须构建验证**：MSBuild 编译 Kaleidoscope.csproj，禁止提交编译不过的代码。
4. **不主动 commit/push**，除非用户明确要求；提交前先 `git status` + `git diff`。
5. **UI 线程禁做网络 IO**：连接/读写一律服务后台线程；TCP 连接必须 `BeginConnect + WaitOne` 强制超时。
6. **服务必须支持热更**：`Dispose` 干净（限时抢锁 + 锁外强断网）、惰性连接自动重连、状态集中在实例内无残留。`DeviceHub.ApplyConfig` 是热更唯一入口。

## 代码约定

- 类/方法/属性 PascalCase；私有字段 `_camelCase`；接口前缀 `I`；事件 `PascalCase` 命名。
- 命名空间：`Kaleidoscope.Models`（配置）、`Kaleidoscope.Services`（服务）、`Kaleidoscope.Utils`（工具）、`Kaleidoscope.Configuration`（配置持久化/校验）。
- **配置持久化约定（V1.2.4 起）**：`ConfigSerializer` 读写 `.kcfg` JSON（UTF-8 无 BOM、缩进、中文直读）；DataContractJsonSerializer 缺字段自动补默认值（版本兼容）、显式 null 会覆盖默认值（必须经 `EnsureSafe` 兜底）；`DeviceHubConfigValidator` 保存前必校验（IP/端口/寄存器越界，Errors 必须修 Warnings 建议修）。新增配置模型字段时无需迁移旧文件。
- **配置序列化约定**：串口停止位存字符串 `"1"/"15"/"2"`；校验位存枚举名 `None/Odd/Even/Mark/Space`；PLC 地址存 **DataStore 索引**（协议号 = 索引 + 40000）。读写两端大小写兼容。
- **配置模型元数据（V1.3.0 起，可视化编辑器地基）**：`Models/*.cs` 每个公开属性必须带 `[DisplayName(中文名)]` + `[Description(说明)]`；顶层聚合配置（如 `DeviceHubConfig`）按业务语义再加 `[Category]` 分组。**新增配置字段时同步补**，否则编辑器/业务界面/自动说明书只显示英文属性名，视为遗漏返工。
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
PlcService / ModbusTcpMasterClient / HslPlcClient / KeyenceIV4Camera / IScanner(串口+TCP+EIP) / ImageStore / ConnectionMonitor
IBarometerReader(气压表 Modbus RTU) / IIoController(IO 耦合器 Modbus TCP) / IFanController(送风机 Modbus TCP)
```

- **业务层禁止新建 TcpClient/串口/连接**：服务内部惰性建连 + 自动重连，业务层只调用服务公开方法、订阅事件。
- **PLC 三种模式（Slave/Master，V1.2.0；MultiBrand，V1.6.0）**：`DeviceHubConfig.PlcRole` 决定——
  `Slave`（默认）用 `hub.Plc`（`PlcService` 从站监听）、`Master` 用 `hub.PlcMaster`
  （`ModbusTcpMasterClient` 主动读写，`Start()` 自动轮询 `PlcMasterConfig.PollItems`）、
  `MultiBrand` 用 `hub.PlcMultiBrand`（`HslPlcClient` 经三菱/欧姆龙/西门子/汇川协议主动读写，
  服务**自带心跳自愈 Timer**，ConnectionMonitor 不参与该模式探活）。三模式连接状态都聚合为
  `HubDeviceKind.Plc`；`ConnectionMonitor` 构造参数在主站模式 `Plc` 为 null、从站模式
  `PlcMaster` 为 null、多品牌模式两者皆 null（Monitor 内部已判空安全）——改代码时勿忘判空。
- **热更**：`DeviceHub.ApplyConfig(newConfig)` → 释放（监控→PLC→气压表/IO/送风机→扫码枪→相机→图像存储）→ 重建 → 触发 `ServicesRebuilt`；上层在回调里重建自己的业务协调器。
- **ImageStore 归 DeviceHub 所有**：`DeviceHub.Dispose`/`ApplyConfig` 显式释放（FileSystemWatcher 句柄），其他对象不得代关。
- **新增设备类型**：先写服务类（独立后台线程 + 惰性连接 + Dispose 干净），再在 `DeviceHubConfig` 加配置段、`DeviceHub.BuildServices` 建实例、`SubscribeAggregateEvents` 聚合事件。
- **Mock 三件套**：气压表/IO/送风机各自有 `MockXxx` 实现（随机数据模拟），`DeviceHubConfig.UseMockCommunication=true` 时全部用 Mock，不接设备跑通 UI/业务；接真机改回 false，业务代码不动。扫码枪/PLC/相机不受此开关影响。
- **ConfigEditor（V1.3.0，独立工具不进库）**：`ConfigEditor/` 可视化配置编辑器只产 `.kcfg`、不启停设备；靠 Models 元数据自动渲染属性网格，**新增配置字段无需改编辑器代码**；`BrandPresets.cs` 品牌预设只收敛参数差异，协议差异仍需改库；产出文件经 `ConfigSerializer.Load` + `ApplyConfig` 接入。编辑器自身遵循"改动必须可编译"铁律（同库工程构建命令）。**界面（V1.5.0 起）用 SunnyUI 小清新**（`UIForm` + LayuiGreen 主题，`libs/` 离线引用 SunnyUI.dll + SunnyUI.Common.dll，构建自动拷输出目录；SunnyUI.dll 运行时依赖 Common，两个都要带）。
- **设备自文档化（V1.4.0）**：`DeviceDescriptorRegistry` 基于 Models 元数据反射自动构建描述符（设备中文名 + 字段：中文名/说明/分组/类型/默认值/是否集合），`DeviceDescriptionExporter` 导出 Markdown 说明书（编辑器「导出说明书…」按钮同源）。**新增配置模型时只需在 Registry 静态构造里登记一行设备中文名**；字段清单自动跟上。校验规则仍手写在 `DeviceHubConfigValidator`（地址重叠等跨字段逻辑自动推导不了），两者职责互补。

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
- **多品牌 PLC（HSL，V1.6.0 集成）**：`HslPlcClient` 一个 `IReadWriteNet` 接口吃掉所有品牌——**新增品牌只在 CreateDevice 工厂加一个 case + MultiBrandPlcBrand 枚举末尾追加值**（枚举按名序列化只许追加），读写/轮询/事件链路零改动。地址填**品牌语法字符串**（三菱 D100/欧姆龙 W10/西门子 DB1.DBW0），  与 Modbus 主站的纯数字协议地址、从站的 DataStore 索引是三套约定，勿混用；端口填 0=按品牌默认（三菱 6000/FINS 9600/CIP 44818/汇川 502/西门子 102）。读写失败一律标记断线（朴素策略：PLC 协议报错也会触发一次重连，代价小换来逻辑简单），由自带心跳 Timer 按 ReconnectIntervalMs 节流自愈。**HSL 授权码不入源码、不入 git**：商业授权、授权码需自行购买，经 ConfigEditor 的"HSL 授权"输入窗（或业务代码 `SaveToFile`）写入 exe 同目录 `hsl.dat` 文件——**AES 加密存储**（文件为密文，防现场顺手拷走直接使用；安全边界见类注释），统一由 `Utils/HslAuthorization.cs` 加载/验签/落盘，禁止在代码里硬编码。
- **扫码枪 EIP 保持型去重（V1.6.0）**：EtherNet/IP 的 Assembly 是保持型数据——扫到一条码会一直保留到下一条覆盖，必须做"条码变化或中间出现过空才上报"的去重；**读到空数据要清去重缓存**，否则"同码→空→同码"第二次扫不触发。EIP 无需触发指令（SendTrigger 为空操作返回 true）；IsOpen 以最近一次 CIP 通讯结果为准（无空闲判活手段）。
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

## 测试（验收测试用例集固化于 acceptance-test 技能）

- **54 个回归用例**固化在 `.opencode/skill/acceptance-test/scripts/SmokeTest.cs`，
  **一键运行**：`powershell -File .opencode/skill/acceptance-test/scripts/run-smoke.ps1`
  （自动 Roslyn 编译 → 拷依赖 → 运行，退出码 0=全过/1=有失败，可接 CI）。
- **判据**：`PASS=54 FAIL=0` 为基线（SKIP=HSL 授权用例无授权环境自动跳过，正常）；
  FAIL 的用例名即定位（组.序号+场景描述）。改动库代码后、提交前必须跑一遍全 PASS。
- **扩展用例**：对应分组加方法 + Main 注册 Run()；模拟设备复用现成基建
  （MockSlave 从站 / StartSim 扫码枪 / StartCamSim 相机，用法见技能 SKILL.md）。
- **编译坑**：必须用 VS 的 Roslyn csc（系统 csc 是 C# 5）——脚本已自动探测；
  详见技能 SKILL.md「编译注意」。

## 已知界面布局关键点（ConfigEditor，2026-08 预设条事件沉淀）

- **WinForms Dock 处理顺序（最高优先级红线）**：布局引擎按 Controls 集合【index 从大到小】依次扣减。
  **Fill 容器必须最先 Add（index 最小、最后处理、填剩余）**，Top/Bottom/Left/Right 边缘条后 Add
  （index 大、先处理、先扣边）。顺序反了症状极具迷惑性：**视觉可能完全正常**（边缘条靠 z-order 恰好
  盖在 Fill 上面），但 Fill 实际占满全客户区、内部全按全尺寸布局——底部内容被边缘条遮挡，平时难察觉
  （ConfigEditor 实测：split 后 Add 导致树/属性网格被状态条静默遮挡 30px 达数个版本，直到贴底的预设条
  出现才暴露）。
- **一行检查法**：跑 `.opencode/skill/ui-layout-debug/scripts/Get-ControlTree.ps1`（或 Python 版）枚举子窗口实际矩形——**Dock=Fill 容器的底边 ==
  父容器底边 = 没让位 = Add 顺序错误**。排查界面"显示不全/被遮挡/错位"问题，第一步就跑脚本拿数据，
  不要从表层症状开始推测（本次先怀疑布局算法/事件时序/控件库绕了三轮，最后 30 秒靠矩形数据定位）。
- **GUI 自动化调试脚本已固化为项目专属技能**（`.opencode/skill/ui-layout-debug/`，SKILL.md 含完整用法与判据，scripts/ 下 Python 与 PowerShell 双版本同功能）：
  `Get-ControlTree.py/.ps1`（枚举子窗口实际矩形=决定性证据，支持 --click/-ClickX/Y 先点树节点、
  --topmost/-Topmost、--attach/-Attach 附加已运行进程）、`Get-WindowShot.py/.ps1`（置顶+可选点击+
  真实屏幕截图）。Python 版依赖 pywin32（截图版另需 Pillow）；PowerShell 版零依赖（现场无 Python
  环境时用），**.ps1 必须 UTF-8 带 BOM** 否则 PowerShell 5.1 中文乱码解析失败。
- **截图验证两不要**：① 不要用 PrintWindow——SunnyUI 自绘控件在 WM_PRINT 下渲染不全（只画顶部一条），
  会产生"控件被裁"的假象；必须 SetWindowPos(TOPMOST) + CopyFromScreen 真实屏幕截图。② 不要用
  SetForegroundWindow——会被系统前台锁静默拒绝（截到别的窗口），用 TOPMOST 置顶。
- **SunnyUI 布局坑**：UIPanel 嵌套 UIPanel 时子控件定位可能偏移——**纯背景容器（工具条/状态条/预设条/
  中间白色层）一律用原生 Panel**，条内 SunnyUI 控件（UIButton/UILabel/UIComboBox）自绘不受父容器影响；
  SunnyUI 控件进 UIAutomation 树不全，控件定位一律用 Win32 EnumChildWindows。
- **条件显示容器（Visible 切换）的布局时序**：Visible=false 期间不参与 Dock 布局，BuildLayout 早期对
  其子控件的定位是错的且 Resize 补偿不保证触发——**每次 Visible=true 后必须显式重排一次**
  （见 MainForm.UpdateBrandCombo 末尾的 LayoutPresetBar 调用）。



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

## 文档同步（铁律：每次任务收尾自查，不许等提醒）

每轮任务结束前**自查以下三项**（自行判断有无变化，有就同步，没有就确认无需更新）：

1. **`README.md`**：目录结构、接入范式、红线、配置项、授权/安全机制有变化时同步更新——
   新增能力必须同步出现在 README（速查层），细节进 `使用说明.md`；
2. **`使用说明.md`**：面向使用者的配置逐项说明、业务写法、FAQ——行为变更（如文件格式、
   配置方式变化）必须同步修正，**过时的操作指引比没有文档更糟**；
3. **`.gitignore`**：新增运行时产物/机密/本地状态时同步补规则并 `git check-ignore` 验证
   （见铁律 2）。
- **本文件**：新增红线/约定/架构变化时同步更新。
- **`CHANGELOG.md`**（本目录，仓库根版本记录）：改动再小也记（本库虽独立，但改动源自 CommandCenter，需在版本演进中留痕）。
