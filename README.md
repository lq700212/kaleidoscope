# Kaleidoscope — 设备通讯与图片存储通用库

> 从 CommandCenter + AgingTestSystem 项目抽取封装的 **PLC（Modbus TCP 从站）+ 基恩士 IV4 相机 + 基恩士 SR 扫码枪 + 图片存储 + 气压表（Modbus RTU 主站）+ IO 耦合器（Modbus TCP 主站）+ 冷却送风机（Modbus TCP）** 七类底层服务。目标是：**换新客户、做新界面时，底层服务一行不改，只写 UI 和业务编排**。
>
> **📖 完整接入手册见 [`使用说明.md`](./使用说明.md)**（配置逐项、业务层写法、热更、FAQ）；本文是速查。GitHub 预览渲染不出中文文件名时直接点仓库里的 `使用说明.md`。

## 一、技术栈

- .NET Framework 4.7.2，C# LangVersion 7.3（WinForms 项目直接引用）
- NModbus 3.0.83（Modbus TCP 从站 + Modbus RTU 主站 + Modbus TCP 主站），`libs/NModbus.dll` + `libs/NModbus.Serial.dll` 本地引用，**离线可编译**，不依赖 NuGet
- System.Management（WMI 自动识别 CH340 串口 / 扫码枪串口，见 `Utils/SerialPortHelper.cs`）
- Newtonsoft.Json **不需要**：配置持久化走库内置 `Configuration/ConfigSerializer`（.NET 内置 `DataContractJsonSerializer`，**零第三方依赖**），业务项目读/写设备配置一行搞定，只吃强类型配置对象；`Models/*.cs` 已带 `System.ComponentModel` 中文元数据（DisplayName/Description/Category），可视化配置编辑器 `ConfigEditor/` 据此自动渲染参数界面（库加新字段界面自动出现）

## 二、仓库结构

```
Kaleidoscope/                       # 仓库根（本文档 + 库工程 + Demo 测试台）
├── README.md / 使用说明.md      #  本速查 + 完整接入手册
├── docs/通讯接入.md              #  唯一协议文档（寄存器映射/坑点/排障，来自 AgingTestSystem）
├── AGENTS.md                    #  AI/维护约定（分层架构、热更、注释红线、构建命令）
├── CHANGELOG.md                 #  版本改动记录（自 V1.0.0 起记）
├── Kaleidoscope/                   # ★ 类库工程（.NET Framework 4.7.2）
│   ├── Kaleidoscope.csproj         #   类库项目（Reference HintPath 引 libs\NModbus.dll 等）
│   ├── libs/NModbus.dll         #   第三方依赖（拷 dll 进 libs，离线可编译）
│   │   └── NModbus.Serial.dll   #    Modbus RTU 串口传输（气压表用）
│   ├── Models/                  #   纯配置模型（强类型，序列化由 Configuration 层负责）
│   │   ├── PlcConfig.cs         #     PLC 从站监听/寄存器地址/型号映射
│   │   ├── PlcMasterConfig.cs   #     PLC 主站连接/轮询配置（上位机主动读写 PLC）
│   │   ├── CameraConfig.cs      #     相机 IP/端口/指令/点位程序表/FTP 目录（含 DefaultCameras）
│   │   ├── ScanConfig.cs        #     扫码枪 TCP/串口参数 + 触发指令 + 自动识别关键词
│   │   ├── ImageConfig.cs       #     存图目录结构/文件名模板/保留天数
│   │   ├── BarometerConfig.cs   #     气压表串口/波特率/压力地址/阈值地址/读寄存器数
│   │   ├── IoConfig.cs          #     IO 耦合器 IP/寄存器地址/备用通道映射
│   │   ├── FanConfig.cs         #     送风机 IP/端口/自动识别候选/寄存器映射与命令码
│   │   └── DeviceHubConfig.cs   #     全部设备 + 型号 + PlcRole + UseMockCommunication 聚合配置
│   ├── Configuration/           #   配置持久化 + 校验 + 设备描述符（V1.2.4 / V1.4.0）
│   │   ├── ConfigSerializer.cs  #     DeviceHubConfig ⇄ .kcfg JSON 文件（缺字段兼容/中文直读/自动兜底）
│   │   ├── DeviceHubConfigValidator.cs # 保存前校验（IP/端口/寄存器/必填，返回错误与警告）
│   │   ├── DeviceDescriptor.cs  #     设备/字段描述符模型（中文名/说明/分组/类型/默认值）
│   │   ├── DeviceDescriptorRegistry.cs # 按 Models 元数据反射自动构建设备描述符（自文档化）
│   │   └── DeviceDescriptionExporter.cs # 导出 Markdown 设备配置说明书
│   ├── Services/                #   底层通讯服务（自持后台线程/惰性连接/自动重连）
│   │   ├── PlcService.cs        #     Modbus TCP 从站监听 + 寄存器读写 + 上电复位
│   │   ├── ModbusTcpMasterClient.cs #  通用 Modbus TCP 主站（主动读写 + 自动轮询，PLC 主站模式用）
│   │   ├── KeyenceIV4Camera.cs  #     相机触发/判定/切程序（PW/OF/T1/T2/RT 指令）
│   │   ├── ScannerService.cs    #     串口扫码枪（IScanner 实现，WMI 自动识别 + 心跳重连）
│   │   ├── ScannerTcpService.cs #     TCP 扫码枪（IScanner 实现，自动发触发指令）
│   │   ├── IBarometerReader.cs / ModbusRtuBarometerReader.cs / MockBarometerReader.cs  # 气压表主站（真实/Mock）
│   │   ├── IIoController.cs / ModbusTcpIoController.cs / MockIoController.cs          # IO 耦合器主站（真实/Mock）
│   │   ├── IFanController.cs / FanControllerClient.cs / MockFanController.cs          # 送风机主站（真实/Mock）
│   │   ├── ImageStore.cs        #     FTP 推图监听 + 双格式归档 + 定期清理
│   │   ├── ConnectionMonitor.cs #     心跳 + 断连自动重连 + 边沿日志（覆盖全部七类设备）
│   │   └── DeviceHub.cs         #     ★ 门面：建/启/事件聚合/热更/释放 全链路封装
│   └── Utils/
│       ├── LogHelper.cs         #     可替换出口的日志（默认写 Logs 文件，可注入文件/界面）
│       ├── TcpKeepAlive.cs      #     TCP KeepAlive 短间隔配置（拔网线/断电快速检测）
│       ├── SerialPortHelper.cs  #     CH340 串口 WMI 自动识别（气压表 RS485→USB）
│       └── IoMapBuilder.cs      #     IO 点位 → 三菱八进制物理地址映射（X000~X107/Y000~Y217）
└── Demo/                        # WinForms 测试台（引用 Kaleidoscope bin 输出）
    ├── KaleidoscopeDemo.csproj     #   构建后自动拷 Kaleidoscope/NModbus/Newtonsoft.Json 到输出目录
    ├── MainForm.cs              #   标准接入方式的最小界面模板（可直接抄接入骨架）
    ├── DemoConfig.cs            #   配置持久化：设备配置走 ConfigSerializer（devices.kcfg），界面记忆走 demo.json
    └── README.md                #   Demo 使用说明/验证清单/配置说明
└── ConfigEditor/                # 可视化配置编辑器（独立工具，不进库；引用 Kaleidoscope bin 输出）
    ├── KaleidoscopeConfigEditor.csproj # 构建后自动拷 Kaleidoscope/NModbus/SunnyUI 到输出目录
    ├── MainForm.cs              #   设备树 + 属性网格 + 品牌预设 + 校验保存 .kcfg
    ├── BrandPresets.cs          #   内置品牌预设（基恩士/汇川/霍尼韦尔/三菱等默认参数）
    └── README.md                #   编辑器使用说明
```

## ⭐ Demo 测试台（`Demo/`）

现场**界面还没做好**时，用 `Demo/` 这个 WinForms 测试台手动验证全链路：
PLC 读写 / 相机触发判定取图存图 / 扫码枪收码 / 存图归档。它本身就是"按标准接入方式
写的最小界面"，新界面可直接抄它的接入骨架（`MainForm` 里 `DeviceHub` 四步调用）。

- **构建**：在仓库根执行——先构建 Kaleidoscope（`Kaleidoscope/Kaleidoscope.csproj`），再构建
   `Demo/KaleidoscopeDemo.csproj`（自动拷 Kaleidoscope.dll/NModbus/Newtonsoft.Json 到输出目录）；
   或直接跑 `Demo/bin/Debug/KaleidoscopeDemo.exe`。
- **使用**：见 [`Demo/README.md`](Demo/README.md)（验证清单/配置说明）。

## ⭐ 可视化配置编辑器（`ConfigEditor/`）

不想手写 `.kcfg` JSON？`ConfigEditor/` 是独立的可视化配置工具（**不进库、不启停设备，只产配置**）：

- 左侧设备树选设备（全局/PLC 从站/主站/相机/扫码枪/气压表/IO/送风机/图像存储，相机与扫码枪支持增删）；
- 右侧属性网格直接改参数——中文名/说明来自 `Models/*.cs` 的 `System.ComponentModel` 元数据，
  **库新增配置字段界面自动出现**，编辑器代码不用改；
- 底部按设备选**品牌预设**一键填充该品牌默认参数（基恩士 IV4、Honeywell Xenon 1902、
  三菱 GX-CL140、现场实测送风机映射等），之后可继续微调；
- **保存前自动校验**：错误（IP/端口/寄存器越界）阻止保存，警告确认后仍可保存；
  产出的 `.kcfg` 直接给 `ConfigSerializer.Load` + `ApplyConfig` 用。
- **界面小清新**（V1.5.0 起）：SunnyUI（LayuiGreen 浅绿主题）无边框圆角窗体，按钮文本居中、
  宽度按文本实测、同排控件垂直对齐，`libs/` 离线引用，构建自动拷依赖。

详见 [`ConfigEditor/README.md`](ConfigEditor/README.md)。

## 三、快速接入（四步）

新界面/新客户项目只需：

```csharp
// ① 构造：传入聚合配置即建好全部服务（惰性连接，不碰网络）
var hub = new DeviceHub(LoadDeviceConfig());

// ② 启动：扫码枪连接 + 心跳监控 + 存图定期清理 + PLC 主站轮询（气压表/IO/送风机由监控心跳自动连接）
hub.Start();

// ③ 订阅聚合事件（可选，事件在工作线程触发，UI 订阅方需 Invoke 回 UI 线程）
hub.SerialNumberScanned   += (s, code) => UpdateUi(code);            // 任意扫码枪读到条码
hub.DeviceConnectionChanged += (s, e) => UpdateStatusLamp(e);        // 任一设备连接状态变化
hub.ServicesRebuilt       += (s, e) => RebuildBusinessLayer(hub);    // 热更后重建你的业务编排
// 细粒度事件直接订阅服务实例：hub.Plc / hub.Cameras / hub.Scanners / hub.ImageStore

// ④ 关窗释放（顺序：监控→PLC→气压表/IO/送风机→扫码枪→相机→图像存储，各步异常不中断）
hub.Dispose();
```

**你的业务层（协调器）**：持有 `hub.Plc` / `hub.Cameras` / `hub.Scanners` / `hub.ImageStore` /
`hub.Barometer` / `hub.Io` / `hub.Fan` 使用，**不要**新建 TcpClient/串口/连接（服务已内部惰性建连 + 自动重连）。

### 配置读写（库内置，业务项目不用自己写 JSON 逻辑）

设备配置（`DeviceHubConfig`）的持久化已收进库内（`Kaleidoscope.Configuration`，零第三方依赖）：

```csharp
var cfg = ConfigSerializer.Load(path);      // 读：文件不存在→默认配置；缺新版字段→默认值补齐；损坏→抛 InvalidDataException
ConfigSerializer.Save(cfg, path);           // 写：自动建目录，UTF-8 无 BOM，中文直读、可手工编辑
hub.ApplyConfig(cfg);                       // 转手给 DeviceHub 即可
```

保存前建议先用 `DeviceHubConfigValidator.Validate(cfg)` 拦截明显错误（IP/端口/寄存器越界等，
返回 `Errors` 必须修、`Warnings` 建议修），把坏配置挡在运行时之前。

### 设备自文档化（V1.4.0：字段说明 = 文档）

`Kaleidoscope.Configuration` 提供"配置自文档化"：描述符基于 `Models/*.cs` 的
`System.ComponentModel` 元数据反射自动构建，**新增字段自动出现在说明书里，不用手工维护**：

```csharp
DeviceDescriptionExporter.ExportToMarkdownFile("设备配置说明书.md");  // 导出全部设备段字段表
var d = DeviceDescriptorRegistry.Get(typeof(CameraConfig));           // 取单段描述符（智能渲染/日志自述用）
```

### PLC 主站 / 从站两模式

配置里 `DeviceHubConfig.PlcRole` 决定上位机跟 PLC 的关系（默认 `Slave` 兼容老项目）：

- `PlcRole.Slave`（默认）：上位机作 **Modbus TCP 从站**，监听本机 502 等 PLC 主站来读写，
  业务层用 `hub.Plc`（`PlcService`，三拍握手），协议细节见 `PlcConfig`。
- `PlcRole.Master`：上位机作 **Modbus TCP 主站**，主动连 PLC 读写寄存器，`Start()` 自动启动
  后台轮询（`PlcMasterConfig.PollItems`）；业务层用 `hub.PlcMaster`（`ModbusTcpMasterClient`），
  读用 `ReadHoldingRegisters/ReadCoils/...`，写用 `WriteSingleRegister/WriteMultipleCoils/...`。

业务层用 `hub.IsPlcMaster` 判断取哪个服务，两种模式连接状态都聚合成 `HubDeviceKind.Plc` 指示灯，
热更/释放行为一致。

### 模拟通讯（跑通 UI 用）

配置里 `DeviceHubConfig.UseMockCommunication = true` 时，气压表/IO 耦合器/送风机三个服务自动换成
Mock 实现（随机数模拟数据/翻转输出），**不接任何线也能把 UI、业务流程、界面布局全部跑通**；
接真机时改回 `false` 即可，业务代码一行不用动。

## 四、热更（改配置免重启）

```csharp
hub.ApplyConfig(newConfig);
```

内部自动：按固定顺序释放旧服务 → 用新配置全量重建 → 重新订阅聚合事件 →
触发 `ServicesRebuilt`。你只需在 `ServicesRebuilt` 回调里**重建自己的业务协调器并重新订阅**（旧协调器
握着已释放的服务引用，必须换新）。若重建过程异常，库内会尽力释放半成品（防文件句柄泄漏）并
记 ERROR 日志，但仍会触发 `ServicesRebuilt` 让上层感知设备层异常、自行决定是否提示重试。

### 停/建顺序（已内置，勿改）

释放：监控 → PLC → 气压表/IO/送风机 → 扫码枪 → 相机 → **图像存储**（ImageStore 归 DeviceHub 所有，必须最后显式释放，
否则 FileSystemWatcher 句柄泄漏）；重建后服务惰性连接、自动重连回设备。

## 五、通用红线（从 CommandCenter 沉淀）

1. **UI 线程禁做网络 IO**：所有连接/读写都在服务后台线程；TCP 连接用 `BeginConnect + WaitOne` 强制超时，绝不在 UI 线程同步 `TcpClient.Connect`（不可达 IP 会冻结界面）。
2. **事件在工作线程触发**，UI 订阅方必须 `Invoke`/`BeginInvoke` 回 UI 线程再操作控件。
3. **图片解码禁放 UI 线程**：基恩士原图 2592×1944，显示一律后台解码 + 等比降采样缩略图。
4. **日志只记边沿**：连上/断开各提示一次，连续失败中间过程静默节流，不刷屏。
5. **配置改 IP/目录优先改 `DefaultCameras()`**，让现场只改一处。

## 六、对接要点

- **PLC**：现场汇川 PLC 可作**主站**（上位机作从站，监听本机 502，"请求-结果-复位"三拍握手，寄存器 40001~40012，配置存 **DataStore 索引**，协议号 = 索引 + 40000）或**从站**（上位机作主站，`PlcRole=Master`，用 `hub.PlcMaster` 主动读写 + 自动轮询）。完整协议见业务项目文档。
- **相机**：基恩士 IV4 无协议 TCP，触发 + 判定（T2），判定即写 PLC 结果、图异步归档；FTP 取图扫目录取最新 jpeg+iv4p 对，归档后删源。
- **扫码枪**：基恩士 SR 无协议 TCP，连上后需发触发指令（`ScanConfig.TriggerCommand`，默认 `LON`）才开始读码；串口枪（Mode=Serial）PortName 留空时按 `DeviceKeyword` 用 WMI 自动识别串口。
- **气压表**（Modbus RTU 主站）：72 台真空负压表挂 RS485→USB（CH340），读压力（0x04 @ 0x0001，2 寄存器，kPa，压力地址/读数量可配）、写报警阈值（0x06 @ 0x0010，**阈值地址可配 `BarometerThresholdRegisterAddress`**）；串口自动识别见 `Utils/SerialPortHelper.cs`。
- **IO 耦合器**（Modbus TCP 主站）：读输入 DI（0x04 @ 0x1000）、写输出 DO（0x06 @ 0x2000），16 点/寄存器读-改-写，控制真空电磁阀/载台上电；物理地址与三菱八进制映射见 `Utils/IoMapBuilder.cs`。
- **送风机**（Modbus TCP）：端口 50000，定值启动/停止 + 读温湿度；**寄存器映射与命令码全部可配**（读区块 `FanStatusStartAddress`+`FanStatusCount`、字段偏移 `Fan*Offset`、控制寄存器 `FanControlAddress`、命令码 `FanStartCommand`/`FanStopCommand`，默认值=现场实测 0x0001~0x0005 + 0x0003/0x0002，换厂商改配置不改库）；IP 自动识别候选见 `FanConfig.FanIpCandidates`；`ReconnectNow()` 为后台异步重连（UI 按钮直接调用不卡死），断线后新配置自动生效。
- **存图清理**：`ImageConfig.KeepDays`（默认 30）控制保留天数，`StartPeriodicCleanup` 在 DeviceHub.Start 里自动启动。

## 七、日志出口

`LogHelper.LogAction` 是全局可替换出口，默认写 `Logs/运行日志_日期.log`（常驻流 + 跨天滚动，
UTF-8 无 BOM，`AutoFlush` 即时落盘，高频日志不反复开关文件）。业务项目启动时注入文件/界面出口：

```csharp
LogHelper.LogAction = (level, msg) => File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}][{level}] {msg}\r\n", Encoding.UTF8);
```
