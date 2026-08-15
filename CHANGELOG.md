# 版本改动记录

> **本文件自 V1.0.0（CommonLib 库抽取之日）起记，只记录 CommonLib 仓库自身的改动。**
> 更早的 V2.14.26 及以前版本均为 CommandCenter 原项目的历史记录（窗口徽标、点位配置、
> 产品型号弹窗等界面功能，与 CommonLib 库代码无关），**不随库迁移**；其中通讯相关的
> 血泪背景（PLC 从站释放 V2.14.23、相机判定即写 V2.13.7、PW 同程序号跳过 V2.14.19、
> 存图清理防误删 V2.14.12 等）已沉淀在 `AGENTS.md`「已知通讯关键点」（位于仓库根），需要
> 原始完整记录时查原 CommandCenter 项目的 CHANGELOG.md。

## V1.0.0（2026-08-15）新增 CommonLib 通用设备通讯库（PLC/相机/扫码枪/图片存储抽取封装）

> 需求：把 CommandCenter 里四类底层通讯/存储服务（汇川 PLC Modbus TCP 从站、基恩士 IV4 相机、
> 基恩士 SR 扫码枪、图片 FTP 归档与定期清理）抽取成独立类库 `CommonLib/`，目标是——
> **换新客户、做新界面时底层服务一行不改，只写 UI 和业务编排**；且所有通讯必须支持热更
> （与当前项目一致，改配置免重启）。

### 改动范围

- **新增 `CommonLib/` 独立类库**（.NET Framework 4.7.2，LangVersion 7.3，不依赖 NuGet，离线可编译）：
  - `CommonLib.csproj` + `libs/NModbus.dll`（本地引用）；
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
  - 命名空间统一为 `CommonLib.Models`/`CommonLib.Services`/`CommonLib.Utils`。
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
  `Config/demo.json`（含 CommonLib 全部强类型配置）。

### 为什么这么改

- CommandCenter 的设备编排（`BuildServices`/`ApplyRuntimeConfig`/`FormClosing` 释放、扫码枪按
  `Mode` 选实现、每相机 FTP 目录监听、存图定期清理等）全部手写在 `MainForm` 里，新客户接新界面
  就得重新抄一遍、还容易漏掉红线（UI 禁 IO、热更顺序、ImageStore 释放归属等）。DeviceHub 把
  "设备活着"这件事彻底封装，业务项目只写"业务流程 + UI"，底层通讯行为与坑（超时/重连/热更/并发
  混图）全部复用库内已验证实现。
- 所有服务保持惰性连接 + 自动重连 + Dispose 干净（限时抢锁 + 锁外强断网 + NModbus
  `_network?.Dispose()`），天然支持热更。

### 验证

- MSBuild Debug/AnyCPU 构建 CommonLib 通过（`CommonLib -> ...\bin\Debug\CommonLib.dll`）。
- 原 CommandCenter 工程未改动，仍按原样构建（两工程独立）。

### 文档同步

- `README.md`：新建（接入范式/热更/红线/配置项），补 Demo 测试台一节。
- `使用说明.md`：新建（完整接入手册，README 的详细版）。
- `AGENTS.md`：新建（库级维护约定，含注释详实红线 + 跨 .NET 版本兼容性评估）。
- `Demo/README.md`：新建（Demo 使用说明/验证清单/配置说明）。
- `CHANGELOG.md`：本版本（V1.0.0）。
