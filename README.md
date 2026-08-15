# CommonLib — 设备通讯与图片存储通用库

> 从 CommandCenter 项目抽取封装的 **PLC（Modbus TCP 从站）+ 基恩士 IV4 相机 + 基恩士 SR 扫码枪 + 图片存储** 四类底层服务。目标是：**换新客户、做新界面时，底层服务一行不改，只写 UI 和业务编排**。
>
> **📖 完整接入手册见 [`使用说明.md`](./使用说明.md)**（配置逐项、业务层写法、热更、FAQ）；本文是速查。GitHub 预览渲染不出中文文件名时直接点仓库里的 `使用说明.md`。

## 一、技术栈

- .NET Framework 4.7.2，C# LangVersion 7.3（WinForms 项目直接引用）
- NModbus 3.0.83（Modbus TCP 从站），`libs/NModbus.dll` 本地引用，**离线可编译**，不依赖 NuGet
- Newtonsoft.Json **不需要**（配置反序列化由业务项目负责，本库只吃强类型配置对象）

## 二、仓库结构

```
CommonLib/                       # 仓库根（本文档 + 库工程 + Demo 测试台）
├── README.md / 使用说明.md      #  本速查 + 完整接入手册
├── AGENTS.md                    #  AI/维护约定（分层架构、热更、注释红线、构建命令）
├── CHANGELOG.md                 #  版本改动记录（自 V1.0.0 起记）
├── CommonLib/                   # ★ 类库工程（.NET Framework 4.7.2）
│   ├── CommonLib.csproj         #   类库项目（Reference HintPath 引 libs\NModbus.dll）
│   ├── libs/NModbus.dll         #   第三方依赖（拷 dll 进 libs，离线可编译）
│   ├── Models/                  #   纯配置模型（强类型，序列化由业务侧决定）
│   │   ├── PlcConfig.cs         #     PLC 从站监听/寄存器地址/型号映射
│   │   ├── CameraConfig.cs      #     相机 IP/端口/指令/点位程序表/FTP 目录（含 DefaultCameras）
│   │   ├── ScanConfig.cs        #     扫码枪 TCP/串口参数 + 触发指令
│   │   ├── ImageConfig.cs       #     存图目录结构/文件名模板/保留天数
│   │   └── DeviceHubConfig.cs   #     上面四类 + 型号的聚合配置（DeviceHub 唯一入参）
│   ├── Services/                #   底层通讯服务（自持后台线程/惰性连接/自动重连）
│   │   ├── PlcService.cs        #     Modbus TCP 从站监听 + 寄存器读写 + 上电复位
│   │   ├── KeyenceIV4Camera.cs  #     相机触发/判定/切程序（PW/OF/T1/T2/RT 指令）
│   │   ├── ScannerService.cs    #     串口扫码枪（IScanner 实现）
│   │   ├── ScannerTcpService.cs #     TCP 扫码枪（IScanner 实现，自动发触发指令）
│   │   ├── ImageStore.cs        #     FTP 推图监听 + 双格式归档 + 定期清理
│   │   ├── ConnectionMonitor.cs #     心跳 + 断连自动重连 + 边沿日志
│   │   └── DeviceHub.cs         #     ★ 门面：建/启/事件聚合/热更/释放 全链路封装
│   └── Utils/
│       ├── LogHelper.cs         #     可替换出口的日志（默认 Debug + 控制台，可注入文件/界面）
│       └── TcpKeepAlive.cs      #     TCP KeepAlive 短间隔配置（拔网线/断电快速检测）
└── Demo/                        # WinForms 测试台（引用 CommonLib bin 输出）
    ├── CommonLibDemo.csproj     #   构建后自动拷 CommonLib/NModbus/Newtonsoft.Json 到输出目录
    ├── MainForm.cs              #   标准接入方式的最小界面模板（可直接抄接入骨架）
    ├── DemoConfig.cs            #   Newtonsoft.Json 持久化 Config/demo.json
    └── README.md                #   Demo 使用说明/验证清单/配置说明
```

## ⭐ Demo 测试台（`Demo/`）

现场**界面还没做好**时，用 `Demo/` 这个 WinForms 测试台手动验证全链路：
PLC 读写 / 相机触发判定取图存图 / 扫码枪收码 / 存图归档。它本身就是"按标准接入方式
写的最小界面"，新界面可直接抄它的接入骨架（`MainForm` 里 `DeviceHub` 四步调用）。

- **构建**：在仓库根执行——先构建 CommonLib（`CommonLib/CommonLib.csproj`），再构建
   `Demo/CommonLibDemo.csproj`（自动拷 CommonLib.dll/NModbus/Newtonsoft.Json 到输出目录）；
   或直接跑 `Demo/bin/Debug/CommonLibDemo.exe`。
- **使用**：见 [`Demo/README.md`](Demo/README.md)（验证清单/配置说明）。

## 三、快速接入（四步）

新界面/新客户项目只需：

```csharp
// ① 构造：传入聚合配置即建好全部服务（惰性连接，不碰网络）
var hub = new DeviceHub(LoadDeviceConfig());

// ② 启动：扫码枪连接 + 心跳监控 + 存图定期清理
hub.Start();

// ③ 订阅聚合事件（可选，事件在工作线程触发，UI 订阅方需 Invoke 回 UI 线程）
hub.SerialNumberScanned   += (s, code) => UpdateUi(code);            // 任意扫码枪读到条码
hub.DeviceConnectionChanged += (s, e) => UpdateStatusLamp(e);        // 任一设备连接状态变化
hub.ServicesRebuilt       += (s, e) => RebuildBusinessLayer(hub);    // 热更后重建你的业务编排
// 细粒度事件直接订阅服务实例：hub.Plc / hub.Cameras / hub.Scanners / hub.ImageStore

// ④ 关窗释放（顺序：监控→PLC→扫码枪→相机→图像存储，各步异常不中断）
hub.Dispose();
```

**你的业务层（协调器）**：持有 `hub.Plc` / `hub.Cameras` / `hub.Scanners` / `hub.ImageStore` 使用，
**不要**新建 TcpClient/串口/连接（服务已内部惰性建连 + 自动重连）。

## 四、热更（改配置免重启）

```csharp
hub.ApplyConfig(newConfig);
```

内部自动：按固定顺序释放旧服务 → 用新配置全量重建 → 重新订阅聚合事件 →
触发 `ServicesRebuilt`。你只需在 `ServicesRebuilt` 回调里**重建自己的业务协调器并重新订阅**（旧协调器
握着已释放的服务引用，必须换新）。

### 停/建顺序（已内置，勿改）

释放：监控 → PLC → 扫码枪 → 相机 → **图像存储**（ImageStore 归 DeviceHub 所有，必须最后显式释放，
否则 FileSystemWatcher 句柄泄漏）；重建后服务惰性连接、自动重连回设备。

## 五、通用红线（从 CommandCenter 沉淀）

1. **UI 线程禁做网络 IO**：所有连接/读写都在服务后台线程；TCP 连接用 `BeginConnect + WaitOne` 强制超时，绝不在 UI 线程同步 `TcpClient.Connect`（不可达 IP 会冻结界面）。
2. **事件在工作线程触发**，UI 订阅方必须 `Invoke`/`BeginInvoke` 回 UI 线程再操作控件。
3. **图片解码禁放 UI 线程**：基恩士原图 2592×1944，显示一律后台解码 + 等比降采样缩略图。
4. **日志只记边沿**：连上/断开各提示一次，连续失败中间过程静默节流，不刷屏。
5. **配置改 IP/目录优先改 `DefaultCameras()`**，让现场只改一处。

## 六、对接要点

- **PLC**：现场汇川 PLC 作**主站**，上位机作**从站**监听本机 502；"请求-结果-复位"三拍握手，寄存器 40001~40012，配置存 **DataStore 索引**（协议号 = 索引 + 40000）。完整协议见业务项目文档。
- **相机**：基恩士 IV4 无协议 TCP，触发 + 判定（T2），判定即写 PLC 结果、图异步归档；FTP 取图扫目录取最新 jpeg+iv4p 对，归档后删源。
- **扫码枪**：基恩士 SR 无协议 TCP，连上后需发触发指令（`ScanConfig.TriggerCommand`，默认 `LON`）才开始读码。
- **存图清理**：`ImageConfig.KeepDays`（默认 30）控制保留天数，`StartPeriodicCleanup` 在 DeviceHub.Start 里自动启动。

## 七、日志出口

`LogHelper.LogAction` 是全局可替换出口，默认写 Debug + 控制台。业务项目启动时注入文件/界面出口：

```csharp
LogHelper.LogAction = (level, msg) => File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}][{level}] {msg}\r\n", Encoding.UTF8);
```
