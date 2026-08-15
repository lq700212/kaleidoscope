# AGENTS.md — CommonLib 设备通讯库

> 本文件是 AI 助手在维护本库前的**强制前置阅读**。CommonLib 是从 CommandCenter 现场项目
> 抽取的通用通讯/图片存储库，目标：**换新客户做新界面时底层服务一行不改**。
> 优先级：本文件 > CommandCenter/AGENTS.md 中的通用红线 > 通用最佳实践。

## 角色与定位

你是本通讯库的**资深维护工程师**。改动必须**可编译、可运行、风格统一、注释详尽**。
改动涉及通讯行为时必须同步 `README.md`；沉淀新红线时同步本文件。

## 技术栈

- .NET Framework **4.7.2**，C# `LangVersion=7.3`（WinForms 业务项目引用，勿引入 .NET Core 语法/API）
- 通讯：**NModbus 3.0.83**（Modbus TCP 从站，汇川 PLC 主站）+ 基恩士 IV4 相机 TCP 无协议 + 基恩士 SR 扫码枪 TCP/串口
- **依赖策略**：第三方库拷 `libs/` 由 csproj `<Reference HintPath>` 引用，**离线可编译**，不依赖 NuGet restore
- 序列化 Newtonsoft.Json **不引入**本库（配置反序列化是业务侧职责，本库只吃强类型对象）

## 跨 .NET 版本兼容性（重要：用户项目可能后续迁 .NET Core/.NET 5+）

> 本库当前目标 **net472**，但迁移到 .NET Core/.NET 5+ 是明确的路。业务代码本身
> 不含 netfx 专属 API，真正风险只有一个点，迁库前先读这段。

| 依赖 | 现状 | .NET Core/.NET 5+ 情况 |
| --- | --- | --- |
| **NModbus 3.0.83** | 目标 **net46**（用 AssemblyName.GetAssemblyName 可查） | ⚠️ **唯一风险点**：老 netfx 库，.NET 5+ 引用可用（compat）但跨平台有隐患。**迁库时优先替换成 netstandard 版 NModbus（4.x）**，`libs/NModbus.dll` 换新 + csproj 保持 HintPath 引用即可，本库调用方式（`ModbusTcpSlaveNetwork`/`CreateSlaveNetwork`）不变。 |
| System.IO.Ports（串口扫码枪） | csproj Reference | ✅ .NET Core 3.0+ 同名 API，Windows 可用；若跨平台需按目标框架条件引用 |
| System.Drawing（ImageStore 位图） | csproj Reference | ✅ .NET Core 3.0+ 为 System.Drawing.Common，**.NET 6+ 仅 Windows**（现场即 Windows，无碍）；跨平台需用 ImageSharp 等替代 |
| 其余（TcpClient/Timer/Task/FileSystemWatcher） | 标准 BCL | ✅ 全平台通用 |

**迁移方案建议**：csproj 改 SDK 风格并多目标（如 `net472;net6.0-windows`），System.IO.Ports 与
System.Drawing 按 `$(TargetFramework)` 条件引用；NModbus 换 netstandard 版。库内服务类
（PlcService/KeyenceIV4Camera/ScannerXxx/ImageStore/ConnectionMonitor/DeviceHub）不涉及
具体框架 API，迁移时**业务代码不需要改**。

## 铁律（违反即返工）

1. **文件编码 UTF-8**。写文件用 write 工具，中文内容写后自查 `[IO.File]::ReadAllText(path, UTF8).Contains("预期中文")`。
2. **不提交运行时数据与机密**：`bin/`、`obj/`、日志一律 gitignore。
3. **改动后必须构建验证**：MSBuild 编译 CommonLib.csproj，禁止提交编译不过的代码。
4. **不主动 commit/push**，除非用户明确要求；提交前先 `git status` + `git diff`。
5. **UI 线程禁做网络 IO**：连接/读写一律服务后台线程；TCP 连接必须 `BeginConnect + WaitOne` 强制超时。
6. **服务必须支持热更**：`Dispose` 干净（限时抢锁 + 锁外强断网）、惰性连接自动重连、状态集中在实例内无残留。`DeviceHub.ApplyConfig` 是热更唯一入口。

## 代码约定

- 类/方法/属性 PascalCase；私有字段 `_camelCase`；接口前缀 `I`；事件 `PascalCase` 命名。
- 命名空间：`CommonLib.Models`（配置）、`CommonLib.Services`（服务）、`CommonLib.Utils`（工具）。
- **配置序列化约定**：串口停止位存字符串 `"1"/"15"/"2"`；校验位存枚举名 `None/Odd/Even/Mark/Space`；PLC 地址存 **DataStore 索引**（协议号 = 索引 + 40000）。读写两端大小写兼容。
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
PlcService / KeyenceIV4Camera / IScanner(串口+TCP) / ImageStore / ConnectionMonitor
```

- **业务层禁止新建 TcpClient/串口/连接**：服务内部惰性建连 + 自动重连，业务层只调用服务公开方法、订阅事件。
- **热更**：`DeviceHub.ApplyConfig(newConfig)` → 释放（监控→PLC→扫码枪→相机→图像存储）→ 重建 → 触发 `ServicesRebuilt`；上层在回调里重建自己的业务协调器。
- **ImageStore 归 DeviceHub 所有**：`DeviceHub.Dispose`/`ApplyConfig` 显式释放（FileSystemWatcher 句柄），其他对象不得代关。
- 新增设备类型：先写服务类（独立后台线程 + 惰性连接 + Dispose 干净），再在 `DeviceHubConfig` 加配置段、`DeviceHub.BuildServices` 建实例、`SubscribeAggregateEvents` 聚合事件。

## 已知通讯关键点（改之前先读对应文件注释）

- **PLC 从站网络释放（V2.14.23 血泪）**：重建/Dispose 时除 `_cts.Cancel()`/`_listener.Stop()` 外**必须 `_network?.Dispose()`**（NModbus `ModbusTcpSlaveNetwork` 实现了 IDisposable，会停止 TcpListener 并关闭所有已连入的 master TCP 会话；只 Stop listener 会让 PLC 主站认为旧连接还活着、不重连新从站 → 通讯假死）。三处清理点统一补。
- **相机"判定即写"（V2.13.7）**：T2 判定一返回立即写 PLC 结果（1/2），不等 FTP 取图归档；通道释放必须等"PLC 已复位请求 **且** `_taskDone`"，否则下一拍请求进来开新 Task 造成同相机并发取图/删源混图。
- **扫码枪 TCP 非连上即回**：连上后必须发触发指令（`ScanConfig.TriggerCommand`，默认 `LON`）才读码，连接/重连成功自动发一次；串口上电即读码、`SendTrigger` 为空操作。
- **图片显示不等归档**：FTP 取图"jpeg 一到目录 → 后台解码缩略图 → 提前塞事件"与"归档复制+删源"解耦，UI 不等 iv4p 复制。
- **PW 同程序号跳过（V2.14.19）**：相机 `SwitchProgram` 缓存上次成功程序号，目标一致直接 return，省 200~390ms；**连接重建必须在 `EnsureConnected` 成功处把缓存重置 -1**，否则相机恢复默认程序后缓存骗过跳过、错拍。
- **存图清理防误删**：`RunCleanupOnce` 只扫存图根目录顶层；快速路径按日期目录名判定，通用路径递归查**所有文件**早于阈值才删；根目录是盘符（如 `E:\`）直接放弃并告警。
- **图片一律后台解码 + 缩略图**：禁止在 UI 线程"读盘 + GDI+ 解码 + 全尺寸大图赋值"（基恩士原图 2592×1944 会卡死界面）。

## 构建命令

```powershell
# 在仓库根（E:\Project\CommonLib）执行：
& "D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
  CommonLib/CommonLib.csproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /nologo /v:m /m
```

- 成功标准：输出 `CommonLib -> ...\bin\Debug\CommonLib.dll` 且无 error。
- Demo 测试台同理（引用 CommonLib bin 输出）：`Demo/CommonLibDemo.csproj` 构建后再跑 `Demo\bin\Debug\CommonLibDemo.exe`。
- 无单元测试框架；以构建通过 + Demo 冒烟测试为验证手段。

## 文档同步（铁律：每次任务主动完成，不许等提醒）

- **`README.md`**（本目录）：目录结构、接入范式、红线、配置项有变化时同步更新。
- **本文件**：新增红线/约定/架构变化时同步更新。
- **`CHANGELOG.md`**（本目录，仓库根版本记录）：改动再小也记（本库虽独立，但改动源自 CommandCenter，需在版本演进中留痕）。
