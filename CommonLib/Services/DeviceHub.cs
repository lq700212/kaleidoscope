using System;
using System.Collections.Generic;
using System.Linq;
using CommonLib.Models;
using CommonLib.Utils;

namespace CommonLib.Services
{
    /// <summary>设备种类（DeviceHub 聚合连接状态事件用）</summary>
    public enum HubDeviceKind { Plc, Camera, Scanner, Barometer, Io, Fan }

    /// <summary>设备连接状态变化的聚合事件参数：新界面只要订阅一个事件就能更新所有设备指示灯。</summary>
    public class HubConnectionChangedEventArgs : EventArgs
    {
        /// <summary>设备种类（PLC/相机/扫码枪）</summary>
        public HubDeviceKind Kind { get; set; }

        /// <summary>设备标识（如 "PLC" / "上相机 192.168.0.213" / "扫码枪1"），用于 UI 灯位与日志</summary>
        public string Name { get; set; }

        /// <summary>是否已连接（true=已连接，false=断开）</summary>
        public bool Connected { get; set; }
    }

    /// <summary>
    /// 设备聚合门面（DeviceHub）：把 PLC（主站/从站两模式）/ 多相机 / 多扫码枪 / 图片存储 / 连接监控
    /// 的【创建、启动、事件聚合、热更、释放】全链路编排收进这一个类，屏蔽底层细节。
    ///
    /// 【为什么要有这个门面】
    /// 原项目（CommandCenter）这套"建服务 + 订阅事件 + 热更停旧建新 + 关窗按序释放"的编排
    /// 全部手写在 MainForm 里，新客户接新界面就得重新抄一遍、还容易漏。DeviceHub 把它封装成
    /// 四个固定方法，新界面只需：
    ///   var hub = new DeviceHub(config);   // ① 建：传入配置即建好全部服务（惰性连接，不碰网络）
    ///   hub.Start();                       // ② 启：扫码枪连接 + 心跳监控 + 存图清理 + PLC 主站轮询
    ///   hub.ApplyConfig(newCfg);           // ③ 热更：停旧服务 → 按新配置全量重建 → 触发重建事件
    ///   hub.Dispose();                     // ④ 关：按固定顺序释放全部服务
    /// 业务层（ProductionCoordinator 类角色）由新项目自己写，但可直接持有 hub.Plc / hub.Cameras /
    /// hub.Scanners / hub.ImageStore 这几个服务实例——DeviceHub 不关心业务，只负责"设备活着"。
    ///
    /// 【PLC 主站/从站两模式（V1.2.0）】
    /// 同一台汇川 PLC 两种角色都能接，由 DeviceHubConfig.PlcRole 决定：
    /// - PlcRole.Slave（默认）：hub.Plc 是 PlcService（从站，监听 502 等 PLC 主站来读写）；
    /// - PlcRole.Master：hub.Plc 为 null、hub.PlcMaster 是 ModbusTcpMasterClient（主站，
    ///   主动读写 PLC，Start() 自动启动后台轮询）。业务层用 hub.IsPlcMaster 判断取哪个，
    ///   两种模式下连接状态都聚合成 HubDeviceKind.Plc 指示灯，热更/释放行为一致。
    ///
    /// 【线程模型】
    /// - DeviceHub 的公开方法（构造/Start/ApplyConfig/Dispose）应在上层 UI 线程调用；
    /// - 内部各服务的事件在各自后台线程触发，DeviceHub 只负责转发聚合事件，
    ///   不在此处做 Invoke（UI 线程跳转是界面层职责，见各事件注释）；
    /// - 各服务的网络 IO 均在服务内部的后台线程完成（TCP 连接强制超时、读写失败断连标记），
    ///   符合"UI 线程禁做网络 IO"红线。
    ///
    /// 【热更语义（与原项目 ApplyRuntimeConfig 对齐）】
    /// ApplyConfig 内部先按"监控→PLC→气压表/IO/送风机→扫码枪→相机→图像存储"顺序释放旧服务
    /// （每步 try/catch，单步失败不中断后续），再用新配置重建并重新订阅聚合事件，最后触发
    /// ServicesRebuilt 事件通知上层"设备层已换新，请重建你的业务编排并重新订阅"。
    /// ImageStore 归本门面所有，热更必须显式释放旧的（否则旧 FileSystemWatcher 句柄泄漏、
    /// 事件发给废弃对象）。
    /// </summary>
    public class DeviceHub : IDisposable
    {
        // ============ 服务实例（新界面的业务层直接使用这些属性） ============

        /// <summary>
        /// PLC 服务（从站模式 PlcRole.Slave）：Modbus TCP 从站，监听 502 等 PLC 主站发起请求。
        /// 主站模式（PlcRole.Master）下为 null，请用 <see cref="PlcMaster"/>。
        /// </summary>
        public PlcService Plc { get; private set; }

        /// <summary>
        /// PLC 服务（主站模式 PlcRole.Master）：通用 Modbus TCP 主站，主动读写 PLC 寄存器，
        /// Start() 自动启动后台轮询（见 PlcMasterConfig.PollItems）。从站模式下为 null。
        /// </summary>
        public ModbusTcpMasterClient PlcMaster { get; private set; }

        /// <summary>当前 PLC 是否主站模式（true=用 PlcMaster；false=用 Plc 从站）。业务层据此取服务。</summary>
        public bool IsPlcMaster => Config.PlcRole == PlcRole.Master;

        /// <summary>基恩士 IV4 相机服务列表（每台相机独立连接/触发/判图）</summary>
        public List<KeyenceIV4Camera> Cameras { get; private set; }

        /// <summary>扫码枪服务列表（每台按 ScanConfig.Mode 选 TCP 或串口实现）</summary>
        public List<IScanner> Scanners { get; private set; }

        /// <summary>图像存储服务（FTP 推图监听 + 双格式归档 + 定期清理）</summary>
        public ImageStore ImageStore { get; private set; }

        /// <summary>
        /// 气压表服务（Modbus RTU 主站，读压力/写设备阈值）。UseMockCommunication=true 时为 Mock。
        /// 业务层定时调 ReadAllData() 采集，写设备阈值调 SetAllThresholds。
        /// </summary>
        public IBarometerReader Barometer { get; private set; }

        /// <summary>
        /// IO 耦合器服务（Modbus TCP 主站，读 DI/写 DO）。UseMockCommunication=true 时为 Mock。
        /// 业务层定时调 ReadAllInputs()/ReadAllOutputs()，控制输出调 WriteOutput。
        /// </summary>
        public IIoController Io { get; private set; }

        /// <summary>
        /// 冷却送风机服务（Modbus TCP，定值启动/停止 + 读温度湿度）。UseMockCommunication=true 时为 Mock。
        /// 业务层调 ReadStatus()/StartFixedValue()/Stop()。
        /// </summary>
        public IFanController Fan { get; private set; }

        /// <summary>连接健康监控器（后台心跳 + 断连自动重连 + 边沿日志）</summary>
        public ConnectionMonitor Monitor { get; private set; }

        /// <summary>当前生效的配置快照（热更后即新配置）</summary>
        public DeviceHubConfig Config { get; private set; }

        // ============ 聚合事件（新界面只需订阅这几个，不必逐服务挂线） ============

        /// <summary>
        /// 任意扫码枪读到条码（多台扫码枪聚合到一个出口）。参数 = 条码内容。
        /// 事件在扫码枪接收线程触发，UI 订阅方需自行 Invoke 回 UI 线程。
        /// </summary>
        public event EventHandler<string> SerialNumberScanned;

        /// <summary>
        /// 任意设备（PLC/相机/扫码枪）连接状态变化（多设备聚合到一个出口）。
        /// 新界面做一个统一的"设备指示灯区"只需订阅本事件按 Kind+Name 上色。
        /// 事件在工作线程触发，UI 订阅方需自行 Invoke 回 UI 线程。
        /// </summary>
        public event EventHandler<HubConnectionChangedEventArgs> DeviceConnectionChanged;

        /// <summary>
        /// 任一相机 FTP 取图目录有新图到达（原 ImageStore.FtpFileArrived 的透传）。
        /// 参数 = (相机下标, 图片路径)。业务层等图流程已内置等待，一般 UI 不必订阅；
        /// 仅当界面想"图一到就刷新预览"这类即时反应时使用。
        /// 事件在工作线程触发，UI 订阅方需自行 Invoke 回 UI 线程。
        /// </summary>
        public event Action<int, string> FtpFileArrived;

        /// <summary>
        /// 设备层热更完成（ApplyConfig 成功后触发）。触发时机：旧服务已全部释放、新服务已全部
        /// 建好并重新订阅聚合事件之后。上层在此回调里重建自己的业务编排（协调器）并重新订阅，
        /// 因为旧业务对象还握着已 Dispose 的旧服务引用（会崩）。回调里访问 hub.Plc/hub.Cameras
        /// 等属性拿到的都是新实例。
        /// </summary>
        public event EventHandler ServicesRebuilt;

        // ============ 生命周期 ============

        /// <summary>
        /// 构造：仅保存配置快照并按之创建全部底层服务（惰性连接，不立即碰网络）。
        /// 要真正开始工作请再调用 Start()。
        /// </summary>
        /// <param name="config">设备层总配置（PLC/相机/扫码枪/图像存储/型号）</param>
        public DeviceHub(DeviceHubConfig config)
        {
            Config = config ?? new DeviceHubConfig();
            BuildServices();
        }

        /// <summary>
        /// 启动全部设备：扫码枪打开连接（失败内部持续重连）+ 连接监控心跳点火 + 存图定期清理。
        /// PLC 主站模式（PlcRole.Master）时在此启动后台自动轮询（见 ModbusTcpMasterClient.StartPolling，
        /// 未配置轮询项则内部忽略）。幂等：重复调用不会重复建服务（服务已在构造时建好）。
        /// </summary>
        public void Start()
        {
            // PLC 主站模式：启动后台自动轮询（从站模式 PlcMaster 为 null，无操作）
            try { PlcMaster?.StartPolling(); }
            catch (Exception ex) { LogHelper.Warn("DeviceHub.Start：PLC 主站轮询启动异常 " + ex.Message); }

            // 扫码枪打开连接（TCP/串口各自内部持续重连，失败不影响主流程）
            foreach (var sc in Scanners)
            {
                try { sc.Open(); }
                catch (Exception ex) { LogHelper.Warn("DeviceHub.Start：扫码枪打开异常 " + ex.Message); }
            }

            // 连接监控：心跳 + 断连自动重连（原项目 ConnectionMonitor.Start，每 2s 后台轮询）
            Monitor?.Start();

            // 图像存储定期清理：启动 30 秒后首次、之后每天一次（仅扫存图目录，不碰 FTP 中转目录）
            try { ImageStore?.StartPeriodicCleanup(); }
            catch (Exception ex) { LogHelper.Warn("DeviceHub.Start：存图定期清理启动异常 " + ex.Message); }

            LogHelper.Info("DeviceHub 已启动：扫码枪/心跳监控/存图清理"
                + (PlcMaster != null ? "/PLC 主站轮询" : "") + "全部就绪");
        }

        /// <summary>
        /// 热更：停旧服务 → 按新配置全量重建 → 重新订阅聚合事件 → 触发 ServicesRebuilt。
        /// 这是"改配置免重启"的唯一入口（原项目 ApplyRuntimeConfig 的设备层部分）。
        /// 【失败兜底】若新配置非法导致 BuildServices 中途异常，尽力释放半成品（防 FileSystemWatcher
        /// 等句柄泄漏）、记 ERROR，并仍触发 ServicesRebuilt 通知上层设备层异常（此时 hub.Xxx 为 null，
        /// 上层需判空防御或决定回滚）——绝不让程序处于"旧服务已释放、新服务没建成"的静默状态。
        /// </summary>
        /// <param name="newConfig">新的设备层总配置</param>
        public void ApplyConfig(DeviceHubConfig newConfig)
        {
            if (newConfig == null) return;

            // ① 停旧服务：顺序同 Dispose，先停心跳/监控，再断设备；每步 try/catch 单步失败不中断
            DisposeServices();

            // ② 换配置快照并重建全部服务
            Config = newConfig;
            try
            {
                BuildServices();
            }
            catch (Exception ex)
            {
                // 尽力清理已部分构建的服务（DisposeServices 对 null 字段安全），防句柄泄漏
                try { DisposeServices(); } catch { }
                LogHelper.Error($"DeviceHub 热更失败，设备层已释放，请检查新配置：{ex.Message}", ex);
                ServicesRebuilt?.Invoke(this, EventArgs.Empty);
                return;
            }

            // ③ 通知上层"设备层已换新"：请重建业务编排（协调器）并重新订阅
            ServicesRebuilt?.Invoke(this, EventArgs.Empty);

            LogHelper.Info("DeviceHub 配置已热更：服务层已按新配置重建");
        }

        /// <summary>
        /// 释放全部服务（关窗/程序退出调用，与 ApplyConfig 共用的释放顺序）。
        /// 顺序固定：监控 → PLC → 扫码枪 → 相机 → 图像存储。每步 try/catch，
        /// 任何一步异常都不中断后续（否则进程退出不了）。
        /// </summary>
        public void Dispose()
        {
            DisposeServices();
            LogHelper.Info("DeviceHub 已释放");
        }

        // ============ 内部实现 ============

        /// <summary>
        /// 按当前配置快照创建全部底层服务（原项目 MainForm.BuildServices 的设备层部分）。
        /// 不订阅聚合事件（订阅在 SubscribeAggregateEvents 里统一做，这里只建实例）。
        /// </summary>
        private void BuildServices()
        {
            // ---------- PLC（主站/从站两模式，由 PlcRole 决定） ----------
            if (Config.PlcRole == PlcRole.Master)
            {
                // 主站模式：上位机主动读写 PLC（通用 Modbus TCP 主站）。
                // 惰性连接：不在此碰网络，由 Start() 的轮询/读写自动建连，Monitor 心跳兜底重连。
                Plc = null;
                PlcMaster = new ModbusTcpMasterClient();
            }
            else
            {
                // 从站模式（默认）：上位机监听 502 等 PLC 主站来读写（三拍握手，见 PlcService）。
                Plc = new PlcService(Config.Plc);
                PlcMaster = null;
                // 把当前型号交给 PLC：从站建站成功后立即写进型号区（40007=序号+40008~40012=字符串），
                // PLC 不触发扫码也能读到当前型号（见 PlcService.SetCurrentModel）。空串则跳过。
                if (!string.IsNullOrWhiteSpace(Config.ProductModel))
                    Plc.SetCurrentModel(Config.ProductModel);
            }

            // ---------- 多相机 ----------
            // 配置列表为空时兜底用通用默认相机（见 CameraConfig.DefaultCameras），新项目应按
            // 现场实际 IP/编号改配置或改 DefaultCameras。注意复制一份，不改用户传入的配置对象。
            List<CameraConfig> camCfg = (Config.Cameras != null && Config.Cameras.Count > 0)
                ? Config.Cameras
                : CameraConfig.DefaultCameras();

            Cameras = new List<KeyenceIV4Camera>();
            foreach (var c in camCfg)
                Cameras.Add(new KeyenceIV4Camera(c));

            // 把各相机结果寄存器地址注册给 PLC 服务——从站就绪时统一清 0（上电/断电重启后
            // 结果寄存器不残留旧值，见 PlcService.ResetResultRegisters）。仅从站模式需要。
            if (Plc != null)
                Plc.SetCameraResultAddresses(camCfg);

            // ---------- 图像存储 ----------
            ImageStore = new ImageStore(Config.Image);

            // 为每台相机启动其 FTP 取图目录的 FileSystemWatcher（AddMonitor 幂等去重，目录不存在
            // 自动建）。目录取相机配置 FtpUploadDir，留空回退全局 FtpRootDir（与协调器同规则）；
            // 监听线程只发事件，不做取图/归档（那些在业务层后台 Task 里）。
            for (int ci = 0; ci < camCfg.Count; ci++)
            {
                string dir = string.IsNullOrWhiteSpace(camCfg[ci]?.FtpUploadDir)
                    ? ImageStore.DefaultFtpDir
                    : camCfg[ci].FtpUploadDir.Trim();
                ImageStore.AddMonitor(dir, ci);
            }
            ImageStore.FtpFileArrived += OnFtpFileArrived; // 聚合透传给上层

            // ---------- 扫码枪 ----------
            // 每台按各自的 ScanConfig.Mode 选实现："Tcp"=基恩士 SR 以太网无协议，其余按串口兜底。
            Scanners = new List<IScanner>();
            foreach (var sc in Config.Scanners ?? new List<ScanConfig>())
                Scanners.Add(BuildScanner(sc));

            // ---------- 气压表 / IO 耦合器 / 送风机（Aging 型主站设备） ----------
            // UseMockCommunication=true 时全部用 Mock（不接设备也能跑通 UI/业务）；
            // false 时用真实通讯实现。连接是惰性的：不在构造时碰网络，由 Monitor 心跳
            // 按节流在后台自动连接（见 ConnectionMonitor.Tick），失败静默持续重连。
            if (Config.UseMockCommunication)
            {
                Barometer = new MockBarometerReader();
                Io = new MockIoController();
                Fan = new MockFanController();
            }
            else
            {
                Barometer = new ModbusRtuBarometerReader();
                Io = new ModbusTcpIoController();
                Fan = new FanControllerClient();
            }

            // ---------- 连接健康监控 ----------
            // 注入全部设备：PLC 从站/PLC 主站二选一（另一传 null）；相机必选；
            // 气压表/IO/送风机可选（传 null 则不监控该类）。
            Monitor = new ConnectionMonitor(Plc, Cameras,
                PlcMaster, Config.PlcMaster,
                Barometer, Config.Barometer,
                Io, Config.Io,
                Fan, Config.Fan);

            // 订阅聚合事件（在 BuildServices 末尾做，确保五个服务都建好）
            SubscribeAggregateEvents();
        }

        /// <summary>
        /// 按配置创建一台扫码枪实例（原项目 MainForm.BuildScanner）：
        /// "Tcp" → ScannerTcpService（基恩士 SR 系列 TCP/IP 无协议，上位机作客户端收条码行）；
        /// 其余 → ScannerService（串口 RS-232）。两者实现同一 IScanner 接口。
        /// 空安全比较：Mode 为 null/空时一律走串口分支，防配置手改 null 崩溃。
        /// </summary>
        private static IScanner BuildScanner(ScanConfig scan)
        {
            if (scan.Mode?.Trim().Equals("Tcp", StringComparison.OrdinalIgnoreCase) == true)
                return new ScannerTcpService(scan);
            return new ScannerService(scan);
        }

        /// <summary>
        /// 订阅各底层服务的原始事件 → 聚合成 DeviceHub 的对外事件。
        /// 注意：每次 BuildServices 都会新建服务实例，因此本方法也在每次 BuildServices 末尾
        /// 调用（旧实例已释放，不会叠加）。lambda 引用的是当前字段（Plc/Cameras/...），
        /// 热更后自动指向新实例。
        /// </summary>
        private void SubscribeAggregateEvents()
        {
            // ---------- 扫码枪 ----------
            foreach (var sc in Scanners)
            {
                // 条码：任何一台枪读到条码都转发（多枪聚合）
                sc.SerialNumberScanned += (s, code) => SerialNumberScanned?.Invoke(this, code);

                // 连接状态：聚合到统一事件
                sc.ConnectionChanged += (s, c) => RaiseConnection(HubDeviceKind.Scanner,
                    sc.Name, c);
            }

            // ---------- PLC ----------
            // 两种角色分开聚合，但都归为 HubDeviceKind.Plc（UI 同一个 PLC 灯位）：
            // - 从站模式（PlcRole.Slave）：Plc 监听就绪/失败（ConnectionChanged）+
            //   主站连入/断开（MasterConnectionChanged，即 PLC 主站连上/离开从站）；
            // - 主站模式（PlcRole.Master）：Plc 为 null，订阅 PlcMaster.ConnectionChanged，
            //   再由 Monitor.PlcMasterConnectionChanged 兜底边沿（其内部已节流重连，双保险不重复）。
            if (Plc != null)
            {
                Plc.ConnectionChanged += (s, c) => RaiseConnection(HubDeviceKind.Plc, "PLC", c);
                Plc.MasterConnectionChanged += (s, c) => RaiseConnection(HubDeviceKind.Plc, "PLC(主站)", c);
            }
            else
            {
                PlcMaster.ConnectionChanged += (s, c) => RaiseConnection(HubDeviceKind.Plc, "PLC", c);
                Monitor.PlcMasterConnectionChanged += (s, c) => RaiseConnection(HubDeviceKind.Plc, "PLC", c);
            }

            // ---------- 相机 ----------
            for (int i = 0; i < Cameras.Count; i++)
            {
                int idx = i; // 闭包锁定下标，避免循环变量被所有事件共享
                Cameras[i].ConnectionChanged += (s, c) => RaiseConnection(HubDeviceKind.Camera,
                    Cameras[idx].IpLabel, c);
            }

            // ---------- 气压表 / IO 耦合器 / 送风机 ----------
            // 三类设备没有 ConnectionChanged 事件（接口只暴露 IsConnected 属性），
            // 边沿已由 ConnectionMonitor 内部检测并广播，这里订阅监控器事件转发到聚合出口。
            Monitor.BarometerConnectionChanged += (s, c) => RaiseConnection(HubDeviceKind.Barometer, "气压表", c);
            Monitor.IoConnectionChanged += (s, c) => RaiseConnection(HubDeviceKind.Io, "IO 耦合器", c);
            Monitor.FanConnectionChanged += (s, c) => RaiseConnection(HubDeviceKind.Fan, "冷却送风机", c);
        }

        /// <summary>内部统一触发聚合连接状态事件。</summary>
        private void RaiseConnection(HubDeviceKind kind, string name, bool connected)
        {
            DeviceConnectionChanged?.Invoke(this, new HubConnectionChangedEventArgs
            {
                Kind = kind,
                Name = name,
                Connected = connected
            });
        }

        /// <summary>ImageStore 有新图到达 → 透传给上层（保留原相机下标语义）。</summary>
        private void OnFtpFileArrived(int cameraIndex, string filePath)
        {
            FtpFileArrived?.Invoke(cameraIndex, filePath);
        }

        /// <summary>
        /// 释放全部服务（ApplyConfig 与 Dispose 共用）：固定顺序
        /// 监控 → PLC → 气压表/IO 耦合器/送风机（主站设备） → 扫码枪 → 相机 → 图像存储。
        /// ImageStore 归本门面所有，必须显式释放（否则 FileSystemWatcher 句柄泄漏）。
        /// 每步 try/catch，单步失败不中断后续，保证进程能正常退出。
        /// </summary>
        private void DisposeServices()
        {
            try { Monitor?.Dispose(); }
            catch (Exception ex) { LogHelper.Warn("DeviceHub：监控器释放异常 " + ex.Message); }
            // PLC：从站或主站二选一，谁存在就释放谁（主站模式 Plc 为 null，PlcMaster 非 null）
            try { Plc?.Dispose(); }
            catch (Exception ex) { LogHelper.Warn("DeviceHub：PLC 释放异常 " + ex.Message); }
            try { PlcMaster?.Dispose(); }
            catch (Exception ex) { LogHelper.Warn("DeviceHub：PLC 主站释放异常 " + ex.Message); }

            // 三类主站设备：先断主站再放外围（扫码枪/相机），避免断开期间外围仍在读数据
            try { Barometer?.Dispose(); }
            catch (Exception ex) { LogHelper.Warn("DeviceHub：气压表释放异常 " + ex.Message); }
            try { Io?.Dispose(); }
            catch (Exception ex) { LogHelper.Warn("DeviceHub：IO 耦合器释放异常 " + ex.Message); }
            try { Fan?.Dispose(); }
            catch (Exception ex) { LogHelper.Warn("DeviceHub：送风机释放异常 " + ex.Message); }

            foreach (var sc in Scanners ?? new List<IScanner>())
            {
                try { sc?.Dispose(); }
                catch (Exception ex) { LogHelper.Warn("DeviceHub：扫码枪释放异常 " + ex.Message); }
            }

            foreach (var cam in Cameras ?? new List<KeyenceIV4Camera>())
            {
                try { cam?.Dispose(); }
                catch (Exception ex) { LogHelper.Warn("DeviceHub：相机释放异常 " + ex.Message); }
            }

            // 图像存储最后释放（业务层若还在异步取图会短暂依赖它，放最后最安全）
            try { ImageStore?.Dispose(); }
            catch (Exception ex) { LogHelper.Warn("DeviceHub：图像存储释放异常 " + ex.Message); }

            // 清空引用，防止热更后旧实例被误用
            Monitor = null; Plc = null; PlcMaster = null;
            Barometer = null; Io = null; Fan = null;
            Scanners = null; Cameras = null; ImageStore = null;
        }
    }
}
