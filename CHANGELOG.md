# 版本改动记录

> **本文件自 V1.0.0（Kaleidoscope 库抽取之日）起记，只记录 Kaleidoscope 仓库自身的改动。**
> 更早的 V2.14.26 及以前版本均为 CommandCenter 原项目的历史记录（窗口徽标、点位配置、
> 产品型号弹窗等界面功能，与 Kaleidoscope 库代码无关），**不随库迁移**；其中通讯相关的
> 血泪背景（PLC 从站释放 V2.14.23、相机判定即写 V2.13.7、PW 同程序号跳过 V2.14.19、
> 存图清理防误删 V2.14.12 等）已沉淀在 `AGENTS.md`「已知通讯关键点」（位于仓库根），需要
> 原始完整记录时查原 CommandCenter 项目的 CHANGELOG.md。

## V1.3.0（2026-08-16）可视化配置编辑器（ConfigEditor）+ 配置模型元数据

> 配置"可视化编辑器"规划的第一步（V1.2.4 已铺好 ConfigSerializer/Validator 地基）之后，
> 本版本把第二步交付：一个**独立工具项目 `ConfigEditor/`**（不进库、不启停设备、只产 `.kcfg`），
> 靠 `Models` 的 `System.ComponentModel` 元数据自动渲染参数界面。从此配设备不必手写 JSON，
> 库新增配置字段界面自动出现、编辑器代码一行不用改。

### 改动范围

- **`Models/*.cs` 全部补 `System.ComponentModel` 元数据**（可视化编辑器地基）：
  - 每个公开属性带 `[DisplayName(中文名)]` + `[Description(说明)]`；顶层聚合配置
    `DeviceHubConfig` 按业务语义加 `[Category]` 分组（通讯/型号/模拟/图像等）。
  - 覆盖：`PlcConfig`/`PlcMasterConfig`（含 `PlcPollItem`）/`CameraConfig`（含
    `StationProgramItem`/`ModelStationPrograms`）/`ScanConfig`/`BarometerConfig`/
    `IoConfig`/`IoOutputChannelRemap`/`FanConfig`/`ImageConfig`/`DeviceHubConfig`。
  - 行为零变化（纯特性）；后续新增字段须同步补特性（已列入 AGENTS.md 代码约定红线）。
- **新增独立工具工程 `ConfigEditor/`**（`KaleidoscopeConfigEditor.csproj`，net472，
  引用 Kaleidoscope bin 输出，与 Demo 同策略拷 NModbus.dll）：
  - `MainForm`：左侧设备树（全局/PLC 从站/主站/相机 N/扫码枪 N/气压表/IO/送风机/图像存储，
    相机与扫码枪支持增删）+ 右侧 PropertyGrid（按 Category 分组、中文名、带说明）+ 工具栏
    （新建/打开/保存/校验）+ 状态栏；关窗/打开/新建时未保存改动给确认。
  - 保存前走 `DeviceHubConfigValidator.Validate`：Errors 阻止保存并弹窗逐条列出、
    Warnings 确认后仍可保存；读写走 `ConfigSerializer`（.kcfg，UTF-8 无 BOM）。
  - `BrandPresets.cs`：内置品牌预设（按设备类型一键填充该品牌默认参数）——汇川 PLC 从站/主站、
    基恩士 IV4 相机、基恩士 SR(TCP)/Honeywell Xenon 1902(串口) 扫码枪、通用气压表(0x0001/0x0010)、
    三菱 GX-CL140 IO 耦合器、送风机现场实测映射 + 另一厂商示例映射；预设只收敛参数差异，
    协议差异仍需改库。品牌列表在数据层追加，编辑器代码不用改。
  - `Program` 支持命令行传 .kcfg 启动即打开。

### 为什么这么改

- 通用库定位：换新客户做新界面时，配置环节也不该重写表单。元数据 + PropertyGrid 让
  "配置界面"变成库模型的免费副产品；新增字段不再需要同步改编辑器。
- 编辑器保持独立工具（不引运行时代码、不启停设备），库的分层架构（DeviceHub 门面）不被污染；
  产出文件经 `ConfigSerializer.Load` + `ApplyConfig` 接入，与手写 .kcfg 完全等价。

### 验证

- MSBuild Debug/AnyCPU 构建 ConfigEditor 通过（无 error），构建后自动拷 Kaleidoscope.dll/NModbus.dll。
- 启动冒烟：无参数启动存活 4s 不崩；命令行传含 2 相机 + 2 扫码枪的 .kcfg 启动存活不崩
  （验证 Load → 树重建 → 属性网格绑定链路）。GUI 深度交互以人工验证为主。

### 文档同步

- `ConfigEditor/README.md`：编辑器用途/构建/操作/产出接入（新增）。
- `README.md`：技术栈序列化条目补元数据说明；仓库结构加 `ConfigEditor/`；新增
  「⭐ 可视化配置编辑器」小节。
- `AGENTS.md`：技术栈序列化条目补元数据；代码约定新增「配置模型元数据（V1.3.0 起）」；
  架构约束新增「ConfigEditor」条目；构建命令补编辑器命令。
- `CHANGELOG.md`：本版本（V1.3.0）。

## V1.2.4（2026-08-16）库内置配置持久化 + 校验（ConfigSerializer/Validator）

> 配置"可视化编辑器"规划的第一步地基：把设备配置（`DeviceHubConfig`）的**读写与校验**收进库内，
> 业务项目不再自己写 JSON 反序列化（旧 Demo 用 Newtonsoft 手写一套 Load/Save 已弃用），
> 也为后续独立的配置编辑器（第二步）提供统一的存取与校验入口。

### 改动范围

- **新增 `Configuration/ConfigSerializer.cs`**（命名空间 `Kaleidoscope.Configuration`）：
  - `Save/Load/ToJson/FromJson/EnsureSafe`：`DeviceHubConfig ⇄ .kcfg` JSON 文件；
  - 序列化用 .NET 内置 `DataContractJsonSerializer`（net472 自带，.NET Core 3.0+ 同名可用，
    **零第三方依赖**，遵守"不引 Newtonsoft"红线）；
  - 写盘 UTF-8 无 BOM、带缩进、`\uXXXX` 转义已解码回真实字符（中文直读、可手工编辑）；
  - **版本兼容**：缺字段自动补默认值、未知成员自动忽略；显式 null 会覆盖默认值，
    故读回后经 `EnsureSafe` 兜底（null 嵌套对象/列表替换为默认实例，防 NRE）；
  - `Load` 文件不存在返回默认配置（不抛），存在但损坏抛 `InvalidDataException`。
- **新增 `Configuration/DeviceHubConfigValidator.cs`**：`Validate(DeviceHubConfig)` 返回
  错误（必须修）+ 警告（建议修）；覆盖 PLC 从站（IP/端口/型号序号/地址重叠）、PLC 主站
  （IP/端口/轮询项）、相机（IP/端口/超时/点位程序号 0~127/取图来源）、扫码枪（Tcp/Serial
  各自参数）、气压表（总数/读寄存器数 1~125/串口参数）、IO 耦合器（总路数/备用通道 0~31）、
  送风机（状态区块偏移越界/启用段）、图像（保留天数/目录）、全局（PLC 角色/型号）；
  `UseMockCommunication=true` 时跳过网络类参数校验，避免误报。
- **csproj**：新增 `System.Runtime.Serialization`、`System.Xml` 引用 + 两个 Compile。
- **Demo 接入（示范标准接法）**：`DemoConfig` 拆两层——设备配置走 `ConfigSerializer`
  存 `Config/devices.kcfg`，界面记忆仍写 `Config/demo.json`；旧版内嵌 Devices 的 demo.json
  首次运行自动迁移成 devices.kcfg（现场参数不丢）。`MainForm` 对外接口不变。

### 为什么这么改

- 通用库定位：换新客户做新界面时底层服务一行不改。配置读写收进库内后，新界面
  `ConfigSerializer.Load(path)` 一行拿回强类型配置直接 `ApplyConfig`，不再各写各的序列化。
- 为配置编辑器（第二步）铺路：编辑器与业务项目共用同一套序列化/校验，职责单一。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope + KaleidoscopeDemo 通过（无 error）。
- PowerShell 冒烟（加载 bin 输出 dll）：Save/Load 往返（含中文/列表/点位表）、配置文件中无
  `\u` 转义且含中文、未知成员+缺字段容错、null 嵌套兜底、校验器默认配置无错误/坏配置有错误，
  全部通过。

### 文档同步

- `README.md`：技术栈"Newtonsoft.Json 不需要"更新为"内置 ConfigSerializer"；仓库结构加
  `Configuration/`；快速接入新增"配置读写（库内置）"小节。
- `AGENTS.md`：技术栈序列化条目更新；代码约定新增"配置持久化约定（V1.2.4 起）"；
  命名空间清单加 `Kaleidoscope.Configuration`。
- `CHANGELOG.md`：本版本（V1.2.4）。

## V1.2.3（2026-08-16）通讯寄存器地址全面配置化（送风机/气压表去写死）

> 通用性专项检查：全库通讯模块中，PLC 主站/从站、IO 耦合器、相机、扫码枪的地址早已配置化；
> 唯独**送风机（FanControllerClient）的寄存器映射与命令码**、**气压表（ModbusRtuBarometerReader）
> 的阈值寄存器地址与读寄存器数**仍写死在代码里。本次把这两处全部改为配置驱动（默认值
> 严格对齐现场实测，行为零变化），换厂商/换仪表只改配置不改库。

### 改动范围

- **`Models/FanConfig.cs` 新增寄存器映射配置段**（默认值=现场实测，向后兼容）：
  - 读状态区块：`FanStatusStartAddress`（0x0000）/ `FanStatusCount`（6）；
  - 字段偏移（相对区块起始）：`FanRunStateOffset`（1）/ `FanTemperatureOffset`（2）/
    `FanHumidityOffset`（3）/ `FanTempSetpointOffset`（4）/ `FanHumSetpointOffset`（5）；
  - 控制：`FanControlAddress`（0x0001）/ `FanStartCommand`（0x0003）/ `FanStopCommand`（0x0002）。
- **`Services/FanControllerClient.cs`**：`ReadStatus` 按配置区块批量读 + 按偏移取字段（偏移越界
  对应字段取 0，不崩）；状态解析**优先按配置命令码识别定值启停**（`==FanStartCommand`→定值启动、
  `==FanStopCommand`→定值停止），再回退 `FanRunState` 枚举强转——不同厂商命令码不同也能正确识别；
  `StartFixedValue`/`Stop`/`WriteCommand` 改读 `FanStartCommand`/`FanStopCommand`/`FanControlAddress`。
- **`Models/BarometerConfig.cs` 新增**：`BarometerThresholdRegisterAddress`（0x0010，原写死 const）、
  `BarometerReadRegisterCount`（2，读压力一次连续读的寄存器数）。
- **`Services/ModbusRtuBarometerReader.cs`**：删除 `private const ThresholdRegisterAddress = 0x0010`，
  `SetThreshold` 改写配置阈值地址；`ReadData` 读数量改用配置（运行时夹到 1~125，数量 1 也能工作，
  换算仍只信任第 1 个寄存器）。
- **注释同步**：`IFanController`/`IBarometerReader`/`FanData`/`BarometerConfig`/`FanConfig`
  类头与方法注释中"写死 0x0010 / 0x0001 / 0x0003"等描述改为"默认值 + 可配"口径。

### 为什么这么改

- 通用库定位：换新客户做新界面时底层服务一行不改。送风机/气压表若遇寄存器映射不同的设备，
  旧实现必须改库代码（写死 0x0000~0x0005、0x0010），违背通用库目标。全部配置化后，
  这些设备的协议差异都收敛到配置层。
- 默认值严格等于旧行为：不改任何配置的项目编译运行结果与旧版完全一致（无破坏性变更）。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope 通过（`Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`，无 error）。

### 文档同步

- `README.md`：仓库结构与对接要点补充送风机/气压表"寄存器地址/命令码可配"说明。
- `AGENTS.md`：「已知通讯关键点」送风机/气压表两段更新为"映射已配置化"口径；新增
  "PLC 从站不依赖主站轮询节奏（V1.2.3 审查结论）"关键点。
- `使用说明.md`：FAQ 新增 Q1c——PLC 轮询时间改大/改小均兼容 + 两条业务层边界
  （协调器等复位超时须 > PLC 最慢轮询周期；梯形图发下一拍前须确认结果已回 0）。
- `CHANGELOG.md`：本版本（V1.2.3）。

## V1.2.2（2026-08-15）库改名 Kaleidoscope

> 库名/程序集/命名空间/源码目录由 `CommonLib` 统一更名为 `Kaleidoscope`（万花筒）。
> 改名是全量替换（`CommonLib` → `Kaleidoscope`），不涉及任何行为变化，但**属破坏性变更**：
> 引用方需把 `using CommonLib.*` 改为 `using Kaleidoscope.*`、引用 `Kaleidoscope.dll`。

### 改动范围

- **目录/工程**：`CommonLib/` 目录 → `Kaleidoscope/`；`CommonLib.csproj` → `Kaleidoscope.csproj`，
  `<RootNamespace>`/`<AssemblyName>` 改 `Kaleidoscope`，程序集输出 `Kaleidoscope.dll`。
- **命名空间**：`CommonLib.Models` / `CommonLib.Services` / `CommonLib.Utils` →
  `Kaleidoscope.Models` / `Kaleidoscope.Services` / `Kaleidoscope.Utils`，全部 .cs 的
  `namespace`/`using` 同步替换（文件内容零逻辑改动，仅改名）。
- **Demo**：`CommonLibDemo.csproj` → `KaleidoscopeDemo.csproj`，程序集 `KaleidoscopeDemo.exe`；
  引用 `..\Kaleidoscope\bin\Debug\Kaleidoscope.dll`，构建后拷贝目标同步。
- **文档**：`README.md` / `使用说明.md` / `AGENTS.md` / `Demo/README.md` / 本文档品牌名统一为
  Kaleidoscope；`docs/通讯接入.md` 不含库名，未动。

### 为什么这么改

- 库定位为"换客户做新界面的通用设备通讯/图片存储库"，`CommonLib` 名字过泛且与具体业务
  无关联；更名 `Kaleidoscope`（万花筒，寓意多设备、多协议、多场景的多样性）作为独立品牌。

### 验证

- 构建 `Kaleidoscope/Kaleidoscope.csproj` → `Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`（无 error）。
- 构建 `Demo/KaleidoscopeDemo.csproj` → `KaleidoscopeDemo.exe`，输出目录含 `Kaleidoscope.dll`/
  `NModbus.dll`/`NModbus.Serial.dll`/`Newtonsoft.Json.dll`。
- 全库 `git grep CommonLib` 无残留。

### 文档同步

- 本版本（V1.2.2）；仓库结构/构建命令/命名空间约定等已在 README/使用说明/AGENTS.md 中随改名同步。

## V1.2.1（2026-08-15）通讯连接稳定性与鲁棒性加固

> 对全库 11 个通讯/工具类做一次连接稳定性审计后的加固：修复 3 处真实竞态/卡顿点，
> 统一连接状态 volatile 可见性，补齐"断连即释放坏连接 / 重连单飞 / 热更失败兜底 /
> 超长响应丢弃连接"等防御，日志写入改常驻流。

### 改动范围

- **`ModbusTcpMasterClient`**：① `Connect` 的 `_config` 赋值移入锁内——消除轮询线程"新配置
  + 旧连接"的竞态窗口；② 轮询结果缓存 `_lastPollData` 改 `ConcurrentDictionary`——`TryGetLastPollData`
  不再借网络锁，UI 高频取数不被 2s 超时读卡住；③ `_isConnected` 改 volatile；④ 通讯失败
  `MarkDisconnectedOnFailure` 断连标记的同时主动 Close 坏 TcpClient（不必等 Monitor 下轮重连）。
- **`ModbusTcpIoController`**：`_isConnected` 改 volatile；`MarkDisconnectedOnFailure` 断连即释放坏连接。
- **`ModbusRtuBarometerReader`**：`Connect`/`Disconnect` 加 `_syncRoot` 锁对齐全库——消除
  `SetAllThresholds` 断线重连并发 `Disconnect` 干扰采集线程锁内 `ReadData` 的无锁竞态；
  `_isConnected` 改 volatile。
- **`FanControllerClient`**：`Connect` 重构为**锁外逐个候选建连 + 成功一次性锁内 commit**——设备
  离线时不再把 `_syncRoot` 占住 候选数×FanTimeoutMs；`ReconnectNow()` 改后台异步执行（UI 按钮
  不再卡死）；`ReadStatus`/`WriteCommand` 自愈重连移出锁；断连即释放坏连接；`_isConnected`/
  `_activeIp`/`_activeIpLoadedFromDisk` 改 volatile。
- **`ScannerService`**：心跳 `CheckConnectionAlive` 的 WMI 搜索/系统串口列表查询移出 `_lock`
  （避免阻塞 `DataReceived` 收码）；`_wasConnected` 改 volatile；`Dispose` 去掉已过时的
  `Thread.Abort` 兜底（改等待心跳线程自然退出 + 告警）。
- **`KeyenceIV4Camera`**：`SendCommandAndReadLine` 响应超长（>1024 字符）时丢弃连接走重连——
  防残留字节污染下一次读取；`IsConnected` 改 volatile 字段。
- **`ConnectionMonitor`**：相机重连加**单飞标志**（`_cameraReconnecting`，防重连 Task 叠加成
  连接风暴）；构造 `cameras` 判空防御。
- **`DeviceHub`**：`ApplyConfig` 热更加失败兜底——`BuildServices` 异常时尽力释放半成品
  （防 FileSystemWatcher 句柄泄漏）、记 ERROR、仍触发 `ServicesRebuilt` 让上层感知设备层异常。
- **`PlcService`**：`IsConnected`/`HasMasterConnected` 改 volatile 字段（Monitor/UI 锁外读）。
- **`ScannerTcpService`**：`_connected` 改 volatile。
- **`Utils/LogHelper`**：写文件改**常驻 StreamWriter + 跨天滚动**（`AutoFlush=true` 即时落盘、
  UTF-8 无 BOM），相机触发等高频日志不再每次 Open/Close 文件。

### 为什么这么改

- 审计发现的中等风险点集中在"锁粒度/阻塞热点"（送风机锁内多候选建连、串口扫码枪锁内 WMI、
  主站缓存借网络锁）与"可见性/竞态"（`_config` 锁外赋值、连接状态非 volatile、气压表 Connect
  无锁）。逐项对齐后，连接建立、数据读写、缓存读取各走各的锁，UI 线程不受设备离线拖累。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope 通过（`Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`，无 error）。
- 未新增外部依赖（`ConcurrentDictionary`/`StreamWriter` 均 .NET Framework 4.7.2 内置）。

### 文档同步

- `README.md`：日志出口小节修正默认行为（写 Logs 文件，常驻流+跨天滚动）；热更小节补失败兜底；
  送风机对接要点补 `ReconnectNow` 异步说明。
- `CHANGELOG.md`：本版本（V1.2.1）。

## V1.2.0（2026-08-15）PLC 主站/从站两模式（通用 Modbus TCP 主站 + 自动轮询）

> 需求：有些项目上位机要作 **Modbus TCP 主站**主动读写 PLC（而非从站模式被动等 PLC 来读写）。
> 新增 `ModbusTcpMasterClient`（通用主站）+ `PlcMasterConfig` + `DeviceHubConfig.PlcRole` 角色开关，
> DeviceHub 按角色装配两套服务，业务层只认 `hub.Plc`（从站）或 `hub.PlcMaster`（主站）。

### 改动范围

- **新增 `Models/PlcMasterConfig.cs`**：主站连接与轮询配置（`IpAddress`/`Port`/`UnitId`/
  `TimeoutMs`/`ReconnectIntervalMs`/`PollIntervalMs`/`PollItems`）；寄存器地址一律填
  Modbus 协议地址（0x0000 起），与从站的 DataStore 索引约定区分。
- **新增 `Services/ModbusTcpMasterClient.cs`**：通用 Modbus TCP 主站（范式对齐
  `ModbusTcpIoController`：BeginConnect 手动超时 + `_syncRoot` 锁串行化 + 断连边沿标记），
  新增自动轮询（`StartPolling`/`StopPolling`，`PollDataUpdated` 事件 + `GetLastPollData` 缓存）、
  重连节流（`EnsureConnected` 内部自愈 + `ReconnectNow`）、通用读写 API（
  `ReadHoldingRegisters`/`ReadInputRegisters`/`ReadCoils`/`ReadDiscreteInputs`/
  `WriteSingleRegister`/`WriteMultipleRegisters`/`WriteSingleCoil`/`WriteMultipleCoils`）。
- **`DeviceHubConfig` 新增 `PlcRole` 枚举（Slave/Master）** + `PlcMaster` 配置段；默认 `Slave` 兼容老项目。
- **`DeviceHub` 扩展**：新增 `PlcMaster`/`IsPlcMaster` 属性；`BuildServices` 按 `PlcRole` 建
  `PlcService`（从站）或 `ModbusTcpMasterClient`（主站，主站模式 `Plc` 为 null、跳过
  `SetCurrentModel`/`SetCameraResultAddresses`）；`Start()` 自动启动主站轮询；
  `DisposeServices` 释放 `PlcMaster`；聚合事件两模式都归 `HubDeviceKind.Plc`。
- **`ConnectionMonitor` 扩展**：构造新增可选 `plcMaster`/`plcMasterConfig` 参数（从站/主站二选一，
  `plc` 可 null）；Tick 内新增 PLC 主站边沿检测 + 5s 节流后台重连 +
  `PlcMasterConnectionChanged` 边沿事件。
- **csproj**：新增 `PlcMasterConfig.cs`、`ModbusTcpMasterClient.cs` 两个 Compile 项。
- **适配 NModbus 3.0.83 API**：读离散输入用 `IModbusMaster.ReadInputs`（该版本无
  `ReadDiscreteInputs` 方法，接口方法名见反射清单）。

### 为什么这么改

- 库内已有"上位机作从站"的 `PlcService`，缺"上位机作主站"角色。Modbus 同一协议两种角色，
  底层都是 NModbus，补一个通用主站类 + 角色开关，DeviceHub 按 `PlcRole` 装配，业务层接入
  代码量不变（只是属性换 `hub.PlcMaster`）。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope 通过（`Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`，无 error）。

### 文档同步

- `README.md`：仓库结构补 `PlcMasterConfig.cs`/`ModbusTcpMasterClient.cs`；快速接入补
  "PLC 主站/从站两模式"小节；对接要点 PLC 条补主站角色说明。
- `CHANGELOG.md`：本版本（V1.2.0）。

## V1.1.0（2026-08-15）新增 Aging 三设备通讯（气压表 Modbus RTU / IO 耦合器 Modbus TCP / 送风机 Modbus TCP + Mock）

> 需求：把 AgingTestSystem（老化测试台）的通讯逻辑作为主干吸收进 Kaleidoscope，目标是——
> **换新客户做新界面时只写页面和业务编排，通讯接入代码一行不写**。DeviceHub 门面保留（薄壳），
> 内部通讯服务换成 Aging 更健壮实现（接口化 + Mock 三件套 + 自动识别 + 心跳静默重连）。

### 改动范围

- **新增 Models（独立强类型配置，不引入 Aging 的 DeviceConfig）**：
  - `BarometerConfig`（PortName 留空自动识别、BaudRate=19200、小数位=1、报警阈值 -95kPa）、
    `IoConfig`（DI 0x1000/DO 0x2000、备用通道映射 `IoBackupChannelMappings`）、
    `FanConfig`（端口 50000、IP 自动识别候选 `FanIpCandidates`）；
  - 数据模型：`BarometerData`（+`DeviceStatus` 枚举）、`FanData`（+`FanRunState` 枚举
    Unknown=-1/ProgramStopped/ProgramRunning/FixedValueStopped/FixedValueRunning）、
    `IoStatus`（+`IoType`/`IoFunction`/`ElectricalType`）、`IoPointDefinition`（+`DeviceIoMapping`）、
    `IoOutputChannelRemap`（`ParseAll` 支持 `;`/`；` 与 `->`/`→`、0x 前缀、通道 0~31）；
  - `DeviceHubConfig` 新增 `Barometer`/`Io`/`Fan` 三段 + **`UseMockCommunication`** 开关。
- **新增 Services 接口**（统一放 `Kaleidoscope.Services`，强类型配置签名 + `OnError` + `IDisposable`）：
  `IBarometerReader`（读压力/写阈值）、`IIoController`（读 DI/写 DO）、`IFanController`（定值启动/停止/读状态）。
- **新增 Services 实现（移植 Aging 健壮性）**：
  - `ModbusRtuBarometerReader`：CH340 WMI 自动识别 + `BarometerPort.cache` 端口记忆、
    单台离线/整线断开区分、未连接返回全 null 数组、SetAllThresholds 50ms 间隔；
  - `ModbusTcpIoController`：BeginConnect 手动超时、读-改-写单点输出、`MapOutputChannel` 备用通道映射、
    额外暴露原始寄存器方法；
  - `FanControllerClient`：`FanLastIp.cache` IP 记忆、10s 重连节流、`ReconnectNow`。
- **新增 Mock 三件套**（`UseMockCommunication=true` 时用，不接设备跑通 UI）：
  `MockBarometerReader`（85% 良好/15% 报警演示）、`MockIoController`（随机输入 + 翻转模拟）、
  `MockFanController`（温度波动模拟）。
- **新增 Utils**：`SerialPortHelper`（CH340 双重校验 WMI 识别 + 全串口枚举）、
  `IoMapBuilder`（Build/GetDeviceMapping/ToOctal 三菱八进制映射）。
- **扫码枪增强（ScannerService 重写，保留 IScanner 接口）**：WMI 按 `DeviceKeyword` 自动识别串口、
  3s 心跳断连检测（双信号判定 + 周期"关-重搜-重开"兜底）、后台静默重连、边沿日志、`DebugLog` 开关；
  去掉 WinForms/NativeWindow（纯库定位）。`ScanConfig` 新增 `DataBits`/`DeviceKeyword`/`DebugLog`，
  `PortName` 默认 `""`（走自动识别）。
- **ConnectionMonitor 扩展**：注入三类主站设备，Tick 内做状态边沿检测 + 节流（5s）后台重连，
  新增 `BarometerConnectionChanged`/`IoConnectionChanged`/`FanConnectionChanged` 边沿事件。
- **DeviceHub 扩展**：`HubDeviceKind` 增 Barometer/Io/Fan；公开 `Barometer`/`Io`/`Fan` 服务实例属性；
  `BuildServices` 按 `UseMockCommunication` 建真实/Mock；释放顺序扩为 监控→PLC→气压表/IO/送风机→扫码枪→相机→图像存储；
  聚合转发三类设备连接状态事件。
- **csproj**：新增 19 个 Compile 项、`System.Management` 引用、`libs/NModbus.Serial.dll` HintPath 引用（RTU 串口传输）。

### 为什么这么改

- Aging 的三类主站设备通讯（气压表/IO/送风机）比 Kaleidoscope 现有实现更健壮：接口化、自动识别、
  Mock 可跑通 UI、心跳静默重连。按用户诉求"后续只写页面"，把这些能力并入 DeviceHub 门面，
  业务层只需 `hub.Barometer`/`hub.Io`/`hub.Fan` 三属性，接入代码量降为接近零。
- 保留 DeviceHub 分层（业务层不建连接）与热更能力（新设备服务 Dispose 干净、支持重建）。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope 通过（`Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`，无 error）。
- MSBuild Debug/AnyCPU 构建 Demo 测试台通过（`KaleidoscopeDemo -> ...\bin\Debug\KaleidoscopeDemo.exe`）。

### 文档同步

- `README.md`：仓库结构/接入步骤/停建顺序/对接要点补充三类新设备与 UseMockCommunication。
- `AGENTS.md`：新增三类设备的"已知通讯关键点"与分层图说明。
- `使用说明.md`：同步配置项与接入示例（详见该文件）。
- `CHANGELOG.md`：本版本（V1.1.0）。

## V1.0.0（2026-08-15）新增 Kaleidoscope 通用设备通讯库（PLC/相机/扫码枪/图片存储抽取封装）

> 需求：把 CommandCenter 里四类底层通讯/存储服务（汇川 PLC Modbus TCP 从站、基恩士 IV4 相机、
> 基恩士 SR 扫码枪、图片 FTP 归档与定期清理）抽取成独立类库 `Kaleidoscope/`，目标是——
> **换新客户、做新界面时底层服务一行不改，只写 UI 和业务编排**；且所有通讯必须支持热更
> （与当前项目一致，改配置免重启）。

### 改动范围

- **新增 `Kaleidoscope/` 独立类库**（.NET Framework 4.7.2，LangVersion 7.3，不依赖 NuGet，离线可编译）：
  - `Kaleidoscope.csproj` + `libs/NModbus.dll`（本地引用）；
  - `Models/`：`PlcConfig`、`CameraConfig`（含点位→程序号/型号分表模型 + `DefaultCameras()` 默认相机）、
    `ScanConfig`、`ImageConfig`、`DeviceHubConfig`（四类配置 + 型号的聚合载体，DeviceHub 唯一入参）；
  - `Services/`：`PlcService`、`KeyenceIV4Camera`、`IScanner`/`ScannerService`（串口）/
    `ScannerTcpService`（TCP，连上自动发触发指令）、`ImageStore`（FTP 监听 + 双格式归档 + 定期清理）、
    `ConnectionMonitor`（心跳 + 断连自动重连 + 边沿日志）；
  - `Utils/`：`LogHelper`（可替换出口）、`TcpKeepAlive`。
- **★ `Services/DeviceHub.cs`（设备聚合门面，核心封装）**：把原 MainForm 手写的
  "建服务 + 启动 + 事件聚合 + 热更 + 释放"全链路编排收进库内，新界面只需四个固定方法：
  `new DeviceHub(config)` → `Start()` → `ApplyConfig(newCfg)`（热更）→ `Dispose()`；
  对外只暴露聚合事件（`SerialNumberScanned`/`DeviceConnectionChanged`/`FtpFileArrived`/
  `ServicesRebuilt`）与各服务实例（`Plc`/`Cameras`/`Scanners`/`ImageStore`）。
- **抽取过程中的适配**（原代码只读，不动 CommandCenter）：
  - `IScanner` 接口新增 `Name` 属性（串口返回串口名、TCP 返回 IP:端口，供连接指示灯/日志标识）；
  - `CameraConfig` 补 `DefaultCameras()` 静态方法（现场默认两台相机，改现场 IP 只改这一处）；
  - 命名空间统一为 `Kaleidoscope.Models`/`Kaleidoscope.Services`/`Kaleidoscope.Utils`。
- **新增 `README.md`**（接入四步 + 热更说明 + 通用红线）与 **`AGENTS.md`**
  （本库维护约定：分层架构、热更约束、**注释详实是第一红线**、通讯关键点、构建命令）。
- **新增 `使用说明.md`**（完整接入手册：配置逐项说明表、四步接入骨架、业务层
  协调器写法（PLC 三拍 / 相机判定即写 / 扫码枪 / 图片显示）、热更、事件清单与线程模型、
  日志出口、FAQ 排查、红线汇总）。
- **新增 `Demo/` WinForms 测试台**（.NET Framework 4.7.2）：现场界面未完成前，
  用它手动验证全链路——配置区（型号/PLC IP/端口，应用配置热更/保存/加载）、相机
  （T1 仅触发 / T2 触发+判定+取图存图 / 读程序 / 切 P001）、扫码枪（发触发指令 + 收码大字）、
  PLC 读写（读请求/写结果/写型号/任意寄存器读写）、**存图测试（不依赖相机，生成测试图
  验证 ImageStore 归档链路）**、右侧连接状态灯 + 图片预览 + 日志。Demo 严格走 DeviceHub
  四步标准接入、后台线程 IO + SafeInvoke 回 UI，是"标准接入方式的最小界面模板"，
  新界面可直接抄它的 MainForm 接入骨架。`DemoConfig.cs` 用 Newtonsoft.Json 持久化
  `Config/demo.json`（含 Kaleidoscope 全部强类型配置）。

### 为什么这么改

- CommandCenter 的设备编排（`BuildServices`/`ApplyRuntimeConfig`/`FormClosing` 释放、扫码枪按
  `Mode` 选实现、每相机 FTP 目录监听、存图定期清理等）全部手写在 `MainForm` 里，新客户接新界面
  就得重新抄一遍、还容易漏掉红线（UI 禁 IO、热更顺序、ImageStore 释放归属等）。DeviceHub 把
  "设备活着"这件事彻底封装，业务项目只写"业务流程 + UI"，底层通讯行为与坑（超时/重连/热更/并发
  混图）全部复用库内已验证实现。
- 所有服务保持惰性连接 + 自动重连 + Dispose 干净（限时抢锁 + 锁外强断网 + NModbus
  `_network?.Dispose()`），天然支持热更。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope 通过（`Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`）。
- 原 CommandCenter 工程未改动，仍按原样构建（两工程独立）。

### 文档同步

- `README.md`：新建（接入范式/热更/红线/配置项），补 Demo 测试台一节。
- `使用说明.md`：新建（完整接入手册，README 的详细版）。
- `AGENTS.md`：新建（库级维护约定，含注释详实红线 + 跨 .NET 版本兼容性评估）。
- `Demo/README.md`：新建（Demo 使用说明/验证清单/配置说明）。
- `CHANGELOG.md`：本版本（V1.0.0）。
