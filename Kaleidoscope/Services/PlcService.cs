using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Kaleidoscope.Models;
using Kaleidoscope.Utils;
using NModbus;
using NModbus.Data;

namespace Kaleidoscope.Services
{
    /// <summary>
    /// PLC 通讯服务（从 CommandCenter/Services/PlcService.cs 抽取，Modbus TCP 从站）。
    ///
    /// 【角色反转】现场 PLC（如汇川）做 Modbus TCP 主站，上位机做从站——
    ///   上位机监听本机 502 端口，等汇川主站 TCP 连入并读写上位机的保持寄存器区。
    ///   原方案是上位机做主站主动 ReadHoldingRegisters/WriteSingleRegister 读写 PLC，
    ///   现全部改为读写上位机自己的 SlaveDataStore 寄存器区（不发起任何 Modbus 请求）。
    ///
    /// 【Modbus 协议约束】Modbus 是主从问答协议，从站不能主动给主站发消息；
    ///   所有"上位机→PLC"的数据都靠 PLC 主站轮询来读上位机寄存器区。
    ///
    /// 【三拍式握手协议（请求-结果-复位）】
    ///   PLC 只写（上位机读）：40001 扫码请求(0/1)；相机通道【每台相机一路】：
    ///   请求地址=相机表 PlcRequestAddress、结果地址=PlcResultAddress（DataStore 索引，
    ///   0=未配置通道、不参与轮询）；
    ///   PLC 只读（上位机写）：40004 扫码结果；相机结果按各自 PlcResultAddress；
    ///                         40007~40011 产品型号(10 字符 ASCII，每寄存器 2 字符，高字节在前)。
    ///   一次完整握手：PLC 写请求≠0 → 上位机处理完写结果≠0 → PLC 读结果并复位请求=0
    ///   → 上位机看到请求回 0 再复位结果=0，进入下一请求。扫码通道与各相机通道互斥串行处理。
    ///
    /// 【NModbus 3.0.83 API】TCP 从站用 ModbusTcpSlaveNetwork（不是 ModbusTcpSlave，此 fork 无此类），
    ///   构造 new ModbusTcpSlaveNetwork(TcpListener, IModbusFactory, IModbusLogger)；
    ///   从站实例 new ModbusSlave(unitId, SlaveDataStore, handlers)；network.AddSlave(slave) 挂载；
    ///   监听 network.ListenAsync(CancellationToken)（后台线程承载，Cancel 停止）；
    ///   DataStore 用 SlaveDataStore.HoldingRegisters(PointSource&lt;ushort&gt;)，读写走 ReadPoints/WritePoints。
    ///
    /// 【线程模型】从站监听在后台线程（ListenAsync 是异步 Task，在此 GetAwaiter().GetResult() 阻塞承载，
    ///   Cancel 退出）；DataStore 读写用 _lock 串行化；业务层用后台定时器轮询请求寄存器。
    ///
    /// 【对外接口】ReadScanRequest/ReadCameraRequest（读 PLC 请求）、
    ///   WriteScanResult/WriteCameraResult/WriteProductModel（写结果/型号）、
    ///   ReadRegister/WriteRegister（功能测试通用读写）。语义：IsConnected/EnsureConnected 表示
    ///   "从站监听是否已就绪"，HasMasterConnected 表示"PLC 主站是否已 TCP 连入"。
    ///
    /// 【接口对 CameraConfig 的依赖】本服务的方法签名引用 CameraConfig（读/写某台相机的
    ///   请求/结果地址来自相机配置），因此引用 Kaleidoscope.Models.CameraConfig。
    /// </summary>
    public class PlcService : IDisposable
    {
        private readonly PlcConfig _cfg;
        private readonly object _lock = new object();

        // 当前产品型号（建站即写/切型号即写用）：
        // ① 从站建站成功（EnsureConnected）后立即把本字段写入型号区（上电/断线重建/热更重建都覆盖）；
        // ② 业务层切型号时更新本字段并立即写一次。
        // 型号为空（配置缺/未设置）时建站即写跳过，避免覆盖 PLC 侧既有型号区。
        private string _currentModel = "";

        /// <summary>已释放标记：Dispose 后后台监听/轮询立即放弃</summary>
        private volatile bool _disposed;

        // ──────────────── Modbus TCP 从站资源（NModbus 3.0.83 API）────────────────
        private TcpListener _listener;
        private IModbusTcpSlaveNetwork _network; // 由 factory.CreateSlaveNetwork 创建（自带非 null logger）
        private IModbusSlave _slave;   // 由 factory.CreateSlave 创建（自带默认功能服务），不直接 new（见 EnsureConnected）
        private SlaveDataStore _dataStore;        // 直接持有 DataStore，便于业务层读写
        private CancellationTokenSource _cts;
        private Thread _listenThread;
        private volatile bool _listening;

        /// <summary>连接状态变化事件（UI 订阅刷新指示灯；语义=从站监听是否就绪）</summary>
        public event EventHandler<bool> ConnectionChanged;

        /// <summary>主站连入状态变化事件（三态灯数据源；语义=PLC 主站是否已 TCP 连入本机 502）</summary>
        public event EventHandler<bool> MasterConnectionChanged;

        /// <summary>当前是否已有 PLC 主站 TCP 会话连入（由后台轮询 Masters 维护，见 MasterPollTick）</summary>
        private volatile bool _hasMasterConnected;

        /// <summary>当前是否已有 PLC 主站 TCP 会话连入（volatile：UI/监控线程锁外读）</summary>
        public bool HasMasterConnected => _hasMasterConnected;

        /// <summary>轮询"主站是否连入"的后台定时器（1s；从站网络 Masters 列表随主站连接/断开变化）</summary>
        private System.Threading.Timer _masterPollTimer;

        /// <summary>当前是否已就绪（从站监听已启动）。volatile：ConnectionMonitor 心跳线程锁外读。</summary>
        private volatile bool _isConnected;

        /// <summary>当前是否已就绪（从站监听已启动）。语义等价于原来的"已连上 PLC"。</summary>
        public bool IsConnected => _isConnected;

        public PlcService(PlcConfig cfg) => _cfg = cfg;

        /// <summary>日志/界面区分用标签：监听 IP:端口（多设备时能分清是哪台）</summary>
        public string IpLabel => $"{_cfg.IpAddress}:{_cfg.Port}";

        private bool _lastFailed; // 上一次监听启动是否失败（日志降噪）

        // 各相机"结果寄存器"地址（DataStore 索引）缓存，供上电/从站重建初始化时统一清 0。
        // 地址来自各相机配置 PlcResultAddress（0=未配置跳过），由业务层注册。
        private readonly List<ushort> _cameraResultAddrs = new List<ushort>();

        /// <summary>
        /// 注册各相机结果寄存器地址（业务层建好相机后调用）：
        /// 供"上电/从站重建初始化"把上位机自己的相机结果寄存器清零（见 ResetResultRegisters）。
        /// 只收集 PlcResultAddress &gt; 0 的（0=未配置结果通道，跳过该台）。
        /// 热更时 PlcService 整体重建并重新注册（地址不残留）。
        /// </summary>
        public void SetCameraResultAddresses(IEnumerable<CameraConfig> cameras)
        {
            _cameraResultAddrs.Clear();
            if (cameras == null) return;
            foreach (var cam in cameras)
                if (cam != null && cam.PlcResultAddress > 0)
                    _cameraResultAddrs.Add((ushort)cam.PlcResultAddress);
        }

        /// <summary>
        /// 确保从站监听已启动（语义等价于原来的"确保连上 PLC"）。
        /// 监听已启动返回 true；监听启动失败(端口占用/权限等)返回 false，后台会重试。
        /// ★ 不在 UI 线程做阻塞网络 IO：监听启动是瞬时绑定，ListenAsync 在后台线程承载。
        /// </summary>
        public bool EnsureConnected()
        {
            if (_disposed) return false;
            lock (_lock)
            {
                if (_listening && _network != null) return true;

                // 先清旧资源
                // ★ 热更断连修复：必须调用 _network.Dispose() 而不能只 Stop listener——
                //   NModbus 3.0.83 的 ModbusTcpSlaveNetwork 实现了 IDisposable，其 Dispose() 会停止
                //   TcpListener 并逐个关闭所有已连入的 PLC 主站 TCP 会话（ModbusMasterTcpConnection）。
                //   旧代码只 _listener.Stop() + _cts.Cancel()：_cts.Cancel 只触发 NModbus 取消回调
                //   Stop 监听器，已 accept 的 master socket 不会被关闭 → PLC 主站认为 TCP 连接还活着、
                //   不会重连新从站 → 热更后黄灯常亮、PLC 发请求上位机收不到。补上 _network.Dispose()
                //   让旧主站 socket 真正关闭，PLC 立即感知断连并重新连入新从站。
                try { _cts?.Cancel(); } catch { }
                try { _network?.Dispose(); } catch { }    // 关旧从站网络（含全部 master 连接 + listener）
                try { _listener?.Stop(); } catch { }      // 双保险：network.Dispose 内部已 Stop listener
                StopMasterPoll();   // 停旧轮询，重建成功后重新启动（防旧 Timer 读半新 _network）
                _cts = null; _listener = null; _network = null; _slave = null; _dataStore = null;

                try
                {
                    // 监听绑定 IP：配置空/0.0.0.0 → 监听所有网卡；否则按配置 IP 绑定指定网卡
                    IPAddress ip = string.IsNullOrWhiteSpace(_cfg.IpAddress) || _cfg.IpAddress == "0.0.0.0"
                        ? IPAddress.Any
                        : IPAddress.Parse(_cfg.IpAddress);
                    _listener = new TcpListener(ip, _cfg.Port);

                    // 建从站数据区 + 从站实例
                    _dataStore = new SlaveDataStore();
                    // ★ 不能 new ModbusSlave(unitId, dataStore, null) 直接 new：
                    //   NModbus 3.0.83 构造函数第三参 handlers(IEnumerable<IModbusFunctionService>)
                    //   要求非 null，传 null 会抛 ArgumentNullException → 从站监听启动失败。
                    //   改用 factory.CreateSlave(unitId, dataStore)：工厂内部自动挂载全部默认
                    //   功能服务（03 读保持寄存器/06 写单个/10 写多个/15/16 等）。
                    var factory = new ModbusFactory();
                    _slave = factory.CreateSlave(_cfg.UnitId, _dataStore);

                    // 建从站网络（一个监听端口可挂多个 UnitId 从站，本方案单从站够用）。
                    // ★ 用 factory.CreateSlaveNetwork(listener) 创建：工厂内部自动带上非 null 的
                    //   IModbusLogger，避免直接 new ModbusTcpSlaveNetwork(listener, factory, null)
                    //   因 logger 为 null 抛 ArgumentNullException（与上方 handlers 同类的坑）。
                    _network = factory.CreateSlaveNetwork(_listener);
                    _network.AddSlave(_slave);

                    // 启动监听（后台线程承载 ListenAsync，Cancel 控制停止）
                    _cts = new CancellationTokenSource();
                    _listening = true;
                    _listenThread = new Thread(ListenLoop)
                    {
                        IsBackground = true,
                        Name = "PlcSlaveListen"
                    };
                    _listenThread.Start();

                    _lastFailed = false;
                    SetConnected(true);
                    StartMasterPoll();   // 启动"主站连入"轮询（界面三态灯的数据源）
                    // 上电/从站重建初始化：把自己的结果寄存器先写 0。
                    // 现场需求：PLC 与上位机断电重启后，结果寄存器不能残留上次的 1/2/3 被误当新结果。
                    // 从站 DataStore 虽是新创建的（默认 0），但为防御"监听重建/异常残留"等场景，
                    // 显式把扫码结果与各相机结果清 0，PLC 主站上电读到的一定是复位态。
                    ResetResultRegisters();
                    // 从站建站成功后立即把当前型号写进型号区（40007=序号 + 40008~40012=字符串）。
                    // 背景：PLC 若不触发扫码流程,上位机原本只在扫码通道推进时才写型号,PLC 读到的
                    // 型号区恒为 0；现在建站即写,PLC 随时能读到当前型号。型号为空时跳过。
                    if (_currentModel.Length > 0)
                        WriteProductModel(_currentModel);
                    LogHelper.Info($"PLC 从站监听已启动 {ip}:{_cfg.Port}（UnitId={_cfg.UnitId}），等待 PLC 主站连入");
                    return true;
                }
                catch (Exception ex)
                {
                    SetConnected(false);
                    StopMasterPoll();
                    try { _cts?.Cancel(); } catch { }
                    try { _network?.Dispose(); } catch { }    // 释放已创建的网络对象，防 master 残留
                    try { _listener?.Stop(); } catch { }
                    _cts = null; _listener = null; _network = null; _slave = null; _dataStore = null;
                    _listening = false;
                    if (!_lastFailed)
                    {
                        _lastFailed = true;
                        LogHelper.Warn($"PLC 从站监听启动失败 {_cfg.IpAddress}:{_cfg.Port}，原因：{ex.Message}");
                    }
                    return false;
                }
            }
        }

        /// <summary>
        /// 从站监听后台循环：NModbus 3 的 ListenAsync 返回 Task（内部 accept 主站连接并处理请求），
        /// 在后台线程阻塞等待；Cancel/Dispose 时令其退出。
        /// </summary>
        private void ListenLoop()
        {
            try
            {
                _network.ListenAsync(_cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { /* 正常停止：Dispose/重建触发 Cancel */ }
            catch (Exception ex)
            {
                if (!_disposed)
                    LogHelper.Warn($"PLC 从站监听异常退出：{ex.Message}");
            }
            finally
            {
                _listening = false;
                SetConnected(false);
            }
        }

        private void SetConnected(bool value)
        {
            if (_isConnected != value)
            {
                _isConnected = value;
                ConnectionChanged?.Invoke(this, value);
            }
        }

        // ════════════════ 主站连入检测（三态灯数据源）════════════════

        /// <summary>
        /// 轮询"PLC 主站是否已连入"（后台 1s）：从站网络内部维护已连入的 TCP 主站列表（Masters），
        /// 边沿变化时触发 MasterConnectionChanged 事件并记日志，UI 据此点亮三态灯。
        /// 【为什么需要它】从站模式下 IsConnected 只表示"监听已就绪"，主站连没连进来是另一回事；
        ///   没有这个检测，界面永远无法知道"PLC 主站是否真的在通讯"。
        /// 【KeepAlive】NModbus 从站对主站会话不设 keepalive、读循环阻塞等待请求——
        ///   PLC 主站拔网线/断电（静默断连，无 FIN/RST）时死会话不会自动从 Masters 清理，
        ///   三态灯会一直停在"主站已连"绿。这里遍历 Masters 给每个主站会话启用 TCP KeepAlive
        ///   （幂等），TCP 栈判死后会话读写异常、NModbus 会自动踢掉该会话 → 下一次轮询 Masters
        ///   Count 归零 → 三态灯转红，主站恢复连入后再转绿。
        /// </summary>
        private void MasterPollTick(object state)
        {
            if (_disposed) return;
            bool has = false;
            try
            {
                var nw = _network;
                if (nw != null && nw.Masters != null && nw.Masters.Count > 0)
                {
                    has = true;
                    // 给每个已连入的主站会话启用 KeepAlive（幂等，重复调用无害）
                    foreach (var master in nw.Masters)
                        TcpKeepAlive.Configure(master);
                }
            }
            catch { /* 网络对象可能正被重建，下个周期再读 */ }

            if (has != _hasMasterConnected)
            {
                _hasMasterConnected = has;
                MasterConnectionChanged?.Invoke(this, has);
                LogHelper.Info(has
                    ? $"PLC 主站已连入从站（{IpLabel}），通讯建立"
                    : $"PLC 主站连接已断开（{IpLabel}），等待主站重新连入");
            }
        }

        /// <summary>启动"主站连入"轮询（监听启动成功后调用；1s 周期）。</summary>
        private void StartMasterPoll()
        {
            if (_masterPollTimer == null)
                _masterPollTimer = new System.Threading.Timer(MasterPollTick, null, 0, 1000);
            else
                _masterPollTimer.Change(0, 1000);
        }

        /// <summary>停止"主站连入"轮询（资源重建/Dispose 时调用，防残留后台轮询读已释放对象）。</summary>
        private void StopMasterPoll()
        {
            try { _masterPollTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
        }

        // ════════════════ 协议业务方法（读写自己 DataStore，方向见类注释）════════════════

        /// <summary>
        /// 读取扫码请求：返回是否 PLC 请求扫码。
        /// 读到 true 表示 PLC 把 ScanRequestAddress 置 1、要求上位机触发扫码枪取 SN；
        /// 处理完成并写结果后由业务层等 PLC 复位请求回 0。
        /// </summary>
        public bool ReadScanRequest(out bool requested)
        {
            requested = false;
            lock (_lock)
            {
                if (_dataStore?.HoldingRegisters == null) return false;
                requested = ReadLocal(_cfg.ScanRequestAddress) != 0;
                return true;
            }
        }

        /// <summary>写扫码结果：0=默认/复位，1=扫码OK，2=扫码NG。</summary>
        public void WriteScanResult(int code) => WriteLocalSafe(_cfg.ScanResultAddress, (ushort)code);

        /// <summary>读某台相机的拍照请求：返回点位编号（1~255），0=无请求。
        /// 请求地址 = 相机配置 PlcRequestAddress（0=未配置该相机通道 → 按"无请求"返回、不误判）。
        /// 处理完成并写结果后由业务层等 PLC 复位请求回 0。</summary>
        /// <param name="cam">相机配置（携带 PLC 请求地址；null 安全）</param>
        public bool ReadCameraRequest(CameraConfig cam, out int stationNo)
        {
            stationNo = 0;
            ushort addr = (ushort)(cam?.PlcRequestAddress > 0 ? cam.PlcRequestAddress : 0);
            if (addr == 0) return true;   // 未配置通道：视为无请求，不占资源不误报
            lock (_lock)
            {
                if (_dataStore?.HoldingRegisters == null) return false;
                stationNo = ReadLocal(addr);
                return true;
            }
        }

        /// <summary>写某台相机的拍照结果：0=默认/复位，1=OK，2=NG，3=点位禁用跳过。
        /// 结果地址 = 相机配置 PlcResultAddress（0=未配置结果通道 → 跳过该台、不写、也不报错）。</summary>
        public void WriteCameraResult(CameraConfig cam, int code)
        {
            ushort addr = (ushort)(cam?.PlcResultAddress > 0 ? cam.PlcResultAddress : 0);
            if (addr > 0) WriteLocalSafe(addr, (ushort)code);
        }

        /// <summary>
        /// 设置当前产品型号：更新内部 `_currentModel`。
        /// 【调用时机】业务层组装服务时传入初始型号，切型号时更新并立即写型号区。
        /// 从站建站成功（EnsureConnected）时也会用本字段把当前型号写进型号区。
        /// </summary>
        /// <param name="model">当前产品型号（如 "U171"），为空则建站即写跳过（不覆盖 PLC 侧型号区）</param>
        public void SetCurrentModel(string model)
        {
            _currentModel = model ?? "";
        }

        /// <summary>
        /// 写产品型号：
        ///   ① 型号序号 → `ProductModelIndexAddress`（默认索引 7 = 协议 40007）：按型号名查
        ///      `_cfg.ModelIndexes` 映射表得序号，型号没配序号写 0；
        ///   ② 型号 ASCII 字符串 → `ProductModelAddress` 起（默认索引 8 = 协议 40008，连续
        ///      ProductModelLen 个寄存器），每寄存器存 2 个 ASCII 字符（高字节=前字符、低字节=后字符），
        ///      最多写 ProductModelLen×2 个字符、不足的尾部补 0x00（PLC 以 0x00 作字符串结束符）。
        /// 型号为空时序号与型号区都写 0（PLC 读到空型号），不崩。
        /// </summary>
        /// <param name="model">产品型号（如 "Z121"），超长自动截断</param>
        /// <returns>从站就绪(true)/未就绪(false)</returns>
        public bool WriteProductModel(string model)
        {
            lock (_lock)
            {
                if (_dataStore?.HoldingRegisters == null) return false;

                // ① 型号序号（协议 40007）：查"型号→序号"映射，命中写序号、未命中写 0
                int modelIndex = ResolveModelIndex(model);
                if (_cfg.ProductModelIndexAddress > 0)
                    WriteLocal(_cfg.ProductModelIndexAddress, (ushort)modelIndex);

                // ② 型号 ASCII（协议 40008 起）
                int len = Math.Max(1, Math.Min(20, _cfg.ProductModelLen)); // 寄存器数 1~20，防异常配置
                ushort[] regs = new ushort[len];
                byte[] bytes = Encoding.ASCII.GetBytes(model ?? "");       // 空型号→全 0
                int charCount = Math.Min(bytes.Length, len * 2);
                for (int i = 0; i < charCount; i++)
                {
                    ushort v = bytes[i]; // 单字节 ASCII，直接放进高字节；低字节留 0x00
                    if (i % 2 == 0)
                        regs[i / 2] = (ushort)(v << 8); // 高字节=前一字符
                    else
                        regs[i / 2] |= v;                // 低字节=后一字符
                }
                WriteLocalMulti(_cfg.ProductModelAddress, regs);
                return true;
            }
        }

        /// <summary>
        /// 按型号名查"型号→PLC 序号"映射：在 `_cfg.ModelIndexes` 里忽略大小写匹配
        /// 型号名，命中返回该型号序号（&gt;0）；型号为空/没配序号返回 0（PLC 端视为未配置）。
        /// </summary>
        private int ResolveModelIndex(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return 0;
            var item = _cfg?.ModelIndexes?.FirstOrDefault(m =>
                m != null && string.Equals(m.ModelName, model.Trim(), StringComparison.OrdinalIgnoreCase));
            return item != null && item.ModelIndex > 0 ? item.ModelIndex : 0;
        }

        // ──────────────── 通用 D 地址读写（功能测试用）────────────────
        // 从站模式下"读/写 PLC 任意寄存器"改为读写上位机自己 DataStore 寄存器区，
        // 验证从站数据存储读写正常（PLC 主站随后会读到这些值）。

        /// <summary>通用读：读取指定 D 地址的单个保持寄存器（自己 DataStore）。
        /// 从站未就绪（DataStore 为 null）返回 false，避免功能测试误报"读到 0"为成功。</summary>
        public bool ReadRegister(ushort dAddress, out ushort value)
        {
            lock (_lock)
            {
                if (_dataStore?.HoldingRegisters == null) { value = 0; return false; }
                value = ReadLocal(dAddress);
                return true;
            }
        }

        /// <summary>通用写：写入指定 D 地址的单个保持寄存器（自己 DataStore）。
        /// 从站未就绪（DataStore 为 null）返回 false，避免功能测试误报写入成功。</summary>
        public bool WriteRegister(ushort dAddress, ushort value)
        {
            lock (_lock)
            {
                if (_dataStore?.HoldingRegisters == null) return false;
                WriteLocal(dAddress, value);
                return true;
            }
        }

        // ════════════════ 本地 DataStore 读写（核心：不发起 Modbus 请求）════════════════

        /// <summary>
        /// 读自己 DataStore 的保持寄存器（单个）。
        /// ★ 地址说明：PLC 主站按【协议号】写/读（40001 扫码请求、40002 上相机请求…），
        ///   NModbus 从站 DataStore 的 ReadPoints(start) 的 start 是【DataStore 索引】，
        ///   现场实测 PLC 写协议 40002 → DataStore[2]。所以【配置里的地址字段直接存索引】
        ///   （协议号 = 索引 + 40000），这里拿到地址就是索引，直接用、不做任何换算。
        /// </summary>
        private ushort ReadLocal(ushort address)
        {
            var regs = _dataStore?.HoldingRegisters;
            if (regs == null) return 0;
            try
            {
                ushort[] arr = regs.ReadPoints(address, 1);
                return (arr != null && arr.Length > 0) ? arr[0] : (ushort)0;
            }
            catch
            {
                // 越界/未就绪：返回 0，不崩（PLC 可能尚未连入、DataStore 未就绪）
                return 0;
            }
        }

        private void WriteLocal(ushort address, ushort value)
        {
            var regs = _dataStore?.HoldingRegisters;
            if (regs == null) return;
            try
            {
                regs.WritePoints(address, new ushort[] { value });
            }
            catch
            {
                // 越界：吞掉，不崩（保持与 ReadLocal 一致的容错策略）
            }
        }

        private void WriteLocalMulti(ushort address, ushort[] values)
        {
            var regs = _dataStore?.HoldingRegisters;
            if (regs == null) return;
            try
            {
                regs.WritePoints(address, values);
            }
            catch
            {
                // 越界：吞掉
            }
        }

        /// <summary>带日志的本地写（业务关键寄存器写入失败时记一条，便于发现 DataStore 未就绪）。</summary>
        private void WriteLocalSafe(ushort address, ushort value)
        {
            try
            {
                lock (_lock)
                    WriteLocal(address, value);
            }
            catch (Exception ex)
            {
                LogHelper.Warn($"写本地寄存器 D{address} 失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 上电/从站重建初始化：把自己（上位机）的结果寄存器全部先写 0。
        /// 【背景】现场要求"PLC 与上位机都把自己的结果寄存器先写 0，防止断电重启后残留旧值
        ///   （上次的 1/2/3）被误当成新结果"。PLC 侧由 PLC 梯形图上电清 0；上位机侧就是本方法：
        ///   从站监听一就绪，把扫码结果（ScanResultAddress）与各相机结果（各 PlcResultAddress）
        ///   清 0，PLC 主站连入后读到的一定是复位态。
        /// 【调用时机】EnsureConnected 每次成功重建从站后调用（覆盖软件启动、断线重建、热更重建）。
        /// </summary>
        private void ResetResultRegisters()
        {
            if (_cfg != null && _cfg.ScanResultAddress > 0)
                WriteLocalSafe(_cfg.ScanResultAddress, 0);
            foreach (var addr in _cameraResultAddrs)
                WriteLocalSafe(addr, 0);
            LogHelper.Info("上电初始化：上位机结果寄存器已全部复位为 0（扫码结果 + " +
                _cameraResultAddrs.Count + " 个相机通道）");
        }

        /// <summary>
        /// 清掉从站监听资源，强制下次 EnsureConnected 完整重建。
        /// 【必须在 lock 内调用】
        /// ★ 热更断连修复：必须调用 _network.Dispose() 而不能只 Stop listener——
        ///   NModbus 3.0.83 的 ModbusTcpSlaveNetwork.Dispose() 会关闭所有已连入的 PLC 主站 TCP 会话
        ///   （ModbusMasterTcpConnection）。旧代码只 _listener.Stop() + _cts.Cancel()，已 accept 的
        ///   master socket 不会被关闭，PLC 主站误以为连接仍活着、不重连新从站 → 热更后黄灯常亮、
        ///   请求收不到（详见 EnsureConnected 顶部注释）。这里 Dispose 掉旧网络，PLC 立即断连重连。
        /// </summary>
        private void ResetConnection()
        {
            _listening = false;
            try { _cts?.Cancel(); } catch { }
            try { _network?.Dispose(); } catch { }    // 关旧从站网络（含全部 master 连接 + listener）
            try { _listener?.Stop(); } catch { }      // 双保险：network.Dispose 内部已 Stop listener
            StopMasterPoll();
            _cts = null; _listener = null; _network = null; _slave = null; _dataStore = null;
        }

        public void Dispose()
        {
            _disposed = true;
            // 限时抢锁：后台监听线程可能正阻塞在 ListenAsync 上，Cancel 会让其退出
            if (Monitor.TryEnter(_lock, TimeSpan.FromMilliseconds(300)))
            {
                try
                {
                    ResetConnection();
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }
            else
            {
                LogHelper.Warn("PLC Dispose 未能拿到锁（后台监听繁忙），改走锁外强停");
                try { _cts?.Cancel(); } catch { }
                try { _network?.Dispose(); } catch { }    // 锁外同样要关旧网络（含 master 连接）
                try { _listener?.Stop(); } catch { }
                StopMasterPoll();
            }
            try { _masterPollTimer?.Dispose(); } catch { }
        }
    }
}
