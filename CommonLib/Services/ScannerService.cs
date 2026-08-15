using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CommonLib.Models;
using CommonLib.Utils;

namespace CommonLib.Services
{
    /// <summary>
    /// 扫码枪统一接口（从 CommandCenter/Services/ScannerService.cs 抽取）：
    /// 串口（ScannerService）与以太网 TCP/IP 无协议（ScannerTcpService）两种实现都暴露同一声明，
    /// 业务层只依赖接口，按 ScanConfig.Mode 决定实例化哪个，将来换扫码枪实现不影响上层。
    /// </summary>
    public interface IScanner : IDisposable
    {
        /// <summary>扫到一条完整条码的事件（参数为条码文本，在工作线程触发，UI 需 Invoke）</summary>
        event EventHandler<string> SerialNumberScanned;

        /// <summary>
        /// 连接状态变化事件：true=已连接/已打开，false=断开/已关闭。
        /// 边沿触发（状态没变不发事件），在工作线程触发，UI 订阅方需自行 Invoke。
        /// 串口实现只在 Open/Dispose 时触发；TCP 实现每次连接成功/断线时触发。
        /// </summary>
        event EventHandler<bool> ConnectionChanged;

        /// <summary>设备是否已连接/已打开</summary>
        bool IsOpen { get; }

        /// <summary>
        /// 设备名称（用于日志/连接指示灯标识）：串口实现返回串口名（如 "COM3"），
        /// TCP 实现返回 "IP:端口"。DeviceHub 聚合连接状态事件用它定位是哪台枪。
        /// </summary>
        string Name { get; }

        /// <summary>启动（打开串口 / 发起 TCP 连接与后台读取）。返回 false 表示启动失败
        /// （串口打不开等），不影响主流程（可手动输入序列号）；TCP 实现立即返回 true（连接在后台）。</summary>
        bool Open();

        /// <summary>
        /// 发送触发指令：基恩士 SR 系列扫码枪多数需先发一条"打开激光/开始读取"指令
        /// （如 LON）才开始读码。TCP 实现（ScannerTcpService）每次连接成功后会自动发送
        /// 一次；本方法供界面手动重发（如测试时扫码枪突然不读，可点一下重新触发）。
        /// 串口扫码枪上电即读码，无需触发，串口实现为空操作。返回 true 表示指令已发出。
        /// </summary>
        bool SendTrigger();
    }

    /// <summary>
    /// 扫码枪服务：封装串口扫码枪数据接收（从 CommandCenter/Services/ScannerService.cs 抽取，
    /// 健壮性按 AgingTestSystem.Services.ScannerService 增强——WMI 自动识别 + 心跳断连检测 +
    /// 后台静默重连 + 边沿日志，保留纯库定位，去掉 WinForms 依赖）。
    ///
    /// 【为什么增强（借鉴 Aging V1.16.3/1.16.6 血泪）】
    /// 原来只有"Open 一次 + DataReceived 收数据"，扫码枪拔掉后状态永远显示"已连接"、
    /// 重新插上也恢复不了。且固定串口写死 COM1，换电脑/USB 口就连不上。增强后：
    /// - WMI 自动识别：PortName 留空时按 DeviceKeyword（默认 "Xenon 1902"）查询 Windows 设备
    ///   名称定位扫码枪串口，现场免配；
    /// - 心跳断连检测：后台线程每 3 秒确认物理设备还在（WMI 搜索 + 系统串口列表双信号 +
    ///   周期"关-重搜-重开"兜底），拔掉立即变"未连接"；
    /// - 后台静默重连：未连接时按 3 秒周期持续重试，日志只记"连上/断开"边沿，失败过程静默
    ///   （不刷屏），设备插上几秒内自动连回；
    /// - 去掉 WinForms：不用 NativeWindow/WM_DEVICECHANGE 热插拔消息、不用 UI 定时器，
    ///   全部用后台线程（System.Threading），库不依赖 System.Windows.Forms，纯库可直接被
    ///   任意宿主/WPF/控制台引用。
    ///
    /// 【编译依赖】本类用 WMI 识别串口，需要 System.Management 引用（见 csproj）。
    ///
    /// 【线程安全】串口读写与心跳重连分布在多个后台线程，用 _lock 串行化串口对象访问；
    /// 事件在后台线程触发（DataReceived 线程 / 心跳线程），UI 订阅方需自行 Invoke。
    /// </summary>
    public class ScannerService : IScanner
    {
        private readonly ScanConfig _cfg;
        private readonly object _lock = new object();
        private SerialPort _port;
        private readonly StringBuilder _buffer = new StringBuilder();

        /// <summary>实际连接的串口名（WMI 识别或固定配置的结果；未连接时为空）。</summary>
        private string _currentPortName;

        /// <summary>连接状态缓存，用于 ConnectionChanged 边沿检测（状态没变不发事件）。volatile：心跳线程/收数据线程跨线程读写。</summary>
        private volatile bool _wasConnected;

        /// <summary>本次"未连接"是否已提示过（true=本次掉线已提示一次，后续后台静默重试不再刷日志）。</summary>
        private bool _disconnectReported;

        /// <summary>后台心跳线程（断连检测 + 静默重连），Open() 启动，Dispose() 退出。</summary>
        private Thread _heartbeatThread;

        /// <summary>后台心跳线程的退出标记。</summary>
        private volatile bool _disposed;

        /// <summary>心跳周期（毫秒）：断连检测 + 静默重连的决策节奏。</summary>
        private const int HeartbeatMs = 3000;

        /// <summary>周期"关闭-重搜-重开"探测频率：每 N 次心跳做一次（约 N×3 秒）。
        /// 为什么需要：WMI/注册表在应用还握着打开句柄时会被驱动残留骗过（鬼设备），
        /// 只有关掉句柄再搜才绝对可靠；正在收数据时自动延后探测。</summary>
        private const int CloseRescanEveryTicks = 4;   // 约 12 秒

        /// <summary>距上次"关闭-重搜-重开"探测过了几次心跳。</summary>
        private int _ticksSinceCloseRescan;

        /// <summary>读超时（毫秒）：防止串口假死时 ReadExisting 一直挂着。</summary>
        private const int ReadTimeoutMs = 2000;

        /// <summary>串口数据最大缓存行长度，防御异常/噪声数据撑爆内存。</summary>
        private const int MaxLineLen = 512;

        /// <summary>从设备名称里提取 COM 口的正则，例："Honeywell Xenon 1902 (COM10)" → "COM10"。
        /// 用 (?!\d) 防止 "COM10" 里的 "COM1" 被当成独立端口误匹配。</summary>
        private static readonly Regex ComPortInNameRegex =
            new Regex(@"(COM\d{1,4})(?!\d)", RegexOptions.IgnoreCase);

        /// <summary>换行符字符集合（\r 和 \n，兼容 CR/LF/CRLF 三种结尾）。</summary>
        private static readonly char[] LineBreakChars = { '\r', '\n' };

        /// <summary>扫到一条完整条码的事件（参数为条码文本，在工作线程触发，UI 需 Invoke）</summary>
        public event EventHandler<string> SerialNumberScanned;

        /// <summary>
        /// 连接（串口打开）状态变化事件：连上 true / 断开 false（边沿触发，状态没变不发）。
        /// 在后台线程触发（收数据线程 / 心跳线程），UI 订阅方需自行 Invoke。
        /// </summary>
        public event EventHandler<bool> ConnectionChanged;

        /// <summary>串口是否已打开（实际连接成功）。</summary>
        public bool IsOpen
        {
            get { lock (_lock) return _port != null && _port.IsOpen; }
        }

        /// <summary>设备名称（实际连接的串口名，如 "COM3"），供日志与连接指示灯标识这台枪。</summary>
        public string Name => _currentPortName ?? _cfg?.PortName ?? "串口扫码枪";

        public ScannerService(ScanConfig cfg) => _cfg = cfg;

        /// <summary>
        /// 打开扫码枪：未启用直接返回 false；立即尝试一次连接（成功则进入已连接状态），
        /// 并启动后台心跳线程持续"断连检测 + 静默重连"。
        /// 本方法不在 UI 线程做任何阻塞网络/串口 IO（串口 Open 在心跳线程内执行）。
        /// </summary>
        public bool Open()
        {
            if (!_cfg.Enabled) return false;

            // 后台心跳线程：负责断连检测 + 静默重连（见 HeartbeatLoop）
            EnsureHeartbeatRunning();

            // 立即尝试第一次连接（成功则直接进入已连接状态）
            TryConnect();
            return true;
        }

        /// <summary>确保后台心跳线程已启动（幂等）。</summary>
        private void EnsureHeartbeatRunning()
        {
            if (_heartbeatThread != null && _heartbeatThread.IsAlive) return;
            _heartbeatThread = new Thread(HeartbeatLoop)
            {
                IsBackground = true,
                Name = "ScannerHeartbeat"
            };
            _heartbeatThread.Start();
        }

        /// <summary>
        /// 后台心跳主循环：每 3 秒"检查当前连接是否还活着 → 未连接则重连"。
        /// 日志只记边沿（连上/断开各一次），连续失败的中间过程静默。
        /// </summary>
        private void HeartbeatLoop()
        {
            while (!_disposed)
            {
                try
                {
                    // 1) 已连接时心跳检查：验证串口/设备是否还在（USB 拔出时状态能及时变"未连接"）
                    CheckConnectionAlive();
                    // 2) 未连接时自动尝试重连（TryConnect 内部有边沿日志，不会刷屏）
                    TryConnect();
                }
                catch (Exception ex)
                {
                    LogHelper.Warn("扫码枪心跳异常 " + ex.Message);
                }
                Thread.Sleep(HeartbeatMs);
            }
        }

        /// <summary>
        /// 尝试连接扫码枪（由 Open / 心跳线程调用）。
        /// 端口选择：PortName 留空 → WMI 按 DeviceKeyword 自动识别；填了 → 固定用配置端口。
        /// 边沿日志：只在"未连接→已连接"提示一次，失败过程静默重试。
        /// </summary>
        private void TryConnect()
        {
            lock (_lock)
            {
                // 已经连着就不重复连接
                if (_port != null && _port.IsOpen) return;

                string failReason = null;
                bool ok = false;
                try
                {
                    // 1) 确定要用的串口：配置固定端口优先，否则 WMI 按关键词自动识别
                    string port = !string.IsNullOrWhiteSpace(_cfg.PortName)
                        ? _cfg.PortName.Trim()
                        : FindScannerPort();

                    if (string.IsNullOrEmpty(port))
                    {
                        failReason = "未找到扫码枪串口（请确认设备已连接并处于虚拟串口模式）";
                    }
                    else
                    {
                        // 若之前连过，先断开旧连接，避免重复 Open 报"端口被占用"
                        ClosePortInternal();

                        // 串口参数以 SerialScannerTest Demo 实测为准：波特率 115200 等按配置
                        _port = new SerialPort(port, _cfg.BaudRate, ParityFromName(_cfg.Parity), _cfg.DataBits, StopBitsFromString(_cfg.StopBits))
                        {
                            ReadTimeout = ReadTimeoutMs
                        };
                        _port.DataReceived += OnDataReceived;
                        _port.ErrorReceived += OnSerialError;
                        _port.Open();
                        _currentPortName = port;
                        ok = true;
                    }
                }
                catch (Exception ex)
                {
                    // 打开失败（端口被占用/拔掉了/驱动异常等）：不抛出，交给下方边沿逻辑
                    failReason = $"连接失败: {ex.Message}";
                    ClosePortInternal();
                }

                // ===== 边沿日志（只在状态变化时提示，失败过程静默） =====
                if (ok)
                {
                    // 连接成功：只有"之前未连接"才提示一次
                    if (!_wasConnected)
                    {
                        _wasConnected = true;
                        _disconnectReported = false;
                        LogHelper.Info($"扫码枪已连接: {_currentPortName}，等待扫码...");
                        ConnectionChanged?.Invoke(this, true);
                    }
                    DebugLog($"连接成功: 端口={_currentPortName}，识别关键词='{_cfg.DeviceKeyword}'");
                }
                else
                {
                    _wasConnected = false;
                    // 只在"本次掉线还没提示过"时提示一次，之后静默重试
                    if (!_disconnectReported)
                    {
                        _disconnectReported = true;
                        LogHelper.Warn(failReason ?? "扫码枪未连接，正在后台自动重试...");
                    }
                }
            }
        }

        /// <summary>通过 WMI 查询设备描述，自动定位首个包含关键词的 COM 端口；找不到返回 null。</summary>
        /// <returns>端口名称（如 "COM10"），未找到/查询失败返回 null</returns>
        private string FindScannerPort()
        {
            List<string> matches = FindMatchingPorts();
            return (matches == null || matches.Count == 0) ? null : matches[0];
        }

        /// <summary>
        /// 按设备关键词搜索匹配的串口名称列表（WMI 动态搜索）。
        /// 连接建立与心跳断连判定共用同一套"动态搜索串口名"逻辑。
        /// 【返回值约定】（心跳判定依赖这个区分）
        /// - null：WMI 查询失败（权限不足/服务临时异常）——无法判定设备状态（不误判断连）
        /// - 空列表：查询成功但无串口匹配关键词——设备已不在（被拔掉）
        /// - 非空列表：查询成功，找到匹配串口
        /// </summary>
        private List<string> FindMatchingPorts()
        {
            var matches = new List<string>();
            try
            {
                // 系统串口列表转成集合（忽略大小写），用于比对
                string[] portNames = SerialPort.GetPortNames();
                if (portNames == null || portNames.Length == 0)
                    return matches;
                var portSet = new HashSet<string>(portNames, StringComparer.OrdinalIgnoreCase);

                // WMI 查询 PnP 设备，过滤名称同时含 "COM" 和关键词的设备（如 "Honeywell Xenon 1902 (COM10)"）
                using (var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%COM%' AND Name LIKE '%{_cfg.DeviceKeyword}%'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString();
                        if (string.IsNullOrEmpty(name)) continue;

                        // 正则提取 COM 口（避免 "COM10" 被误匹配出 "COM1"），与系统列表比对去重
                        Match m = ComPortInNameRegex.Match(name);
                        while (m.Success)
                        {
                            string port = m.Value;
                            if (portSet.Contains(port) && !matches.Contains(port))
                                matches.Add(port);
                            m = m.NextMatch();
                        }
                    }
                }
                return matches;
            }
            catch (Exception)
            {
                // WMI 查询失败（可能权限不足）：返回 null，心跳时据此不误判断连
                return null;
            }
        }

        /// <summary>
        /// 心跳检查：当前连接是否还存活（核心是"和连接建立一样动态搜索设备关键词再确认一遍"）。
        /// 为什么 GetPortNames/ReadExisting 不可靠：USB 虚拟串口被拔后，注册表 SERIALCOMM 条目
        /// 在应用还握着打开句柄时常常残留（鬼设备），ReadExisting 多数驱动只是静默返回空串不抛异常。
        /// 而 WMI 反映的是物理 PnP 设备节点是否真在，不受残留影响。
        /// 动态模式（PortName 留空）：WMI 关键词搜索 + 系统串口列表双信号，任一路确定端口不在 → 断连；
        /// 固定模式：回落"端口是否还在系统串口列表"。
        /// 再叠加周期"关-重搜-重开"兜底（句柄一关残留才能真正释放）。
        /// </summary>
        private void CheckConnectionAlive()
        {
            // ① 锁内快照：当前端口名 + 确认串口仍打开（未打开交给 TryConnect 处理，本轮心跳结束）
            string currentPort;
            lock (_lock)
            {
                if (_port == null || !_port.IsOpen) return;
                currentPort = _currentPortName;
            }

            // ② 【锁外查询】WMI 关键词搜索 + 系统串口列表在锁外执行——它们耗时几十~几百 ms，
            //    若放在 _lock 内会阻塞 DataReceived 收码（它与心跳共用 _lock）。查完拿结果进锁内判定。
            bool dynamicMode = string.IsNullOrWhiteSpace(_cfg.PortName);
            List<string> wmiMatches = dynamicMode ? FindMatchingPorts() : null;
            string[] portNames = SafeGetPortNames();

            lock (_lock)
            {
                // 查询期间端口可能已被重开/关闭：作废本轮判定（交给下轮或 TryConnect）
                if (_port == null || !_port.IsOpen) return;

                if (dynamicMode)
                {
                    // 动态识别模式：WMI 关键词搜索 + 系统串口列表双信号
                    // ① WMI 设备关键词搜索：null=查询失败 / 空=不在 / 非空=在
                    bool wmiSaysPresent = wmiMatches != null &&
                        wmiMatches.Exists(p => string.Equals(p, currentPort, StringComparison.OrdinalIgnoreCase));

                    // ② 系统串口列表（GetPortNames，读注册表 SERIALCOMM）：当前端口是否还在
                    bool inPortList = portNames != null &&
                        Array.Exists(portNames, p => string.Equals(p, currentPort, StringComparison.OrdinalIgnoreCase));

                    DebugLog($"心跳: 当前={currentPort}, WMI匹配=[{JoinPorts(wmiMatches)}], " +
                             $"系统列表=[{JoinPorts(portNames)}], WMI在={wmiSaysPresent}, 列表在={inPortList}");

                    // 两路独立判定：任一路"查询成功但端口已不在" → 判定断连（查询失败那路自动跳过）
                    if ((wmiMatches != null && !wmiSaysPresent) ||
                        (portNames != null && !inPortList))
                    {
                        OnDisconnectDetected("扫码枪已被拔掉（动态搜索不到该串口）");
                        return;
                    }
                }
                else
                {
                    // 固定串口模式：回落到"当前端口名是否还在系统串口列表"判断
                    bool portExists = portNames != null &&
                        Array.Exists(portNames, p => string.Equals(p, currentPort, StringComparison.OrdinalIgnoreCase));
                    if (!portExists)
                    {
                        OnDisconnectDetected("USB 串口已被移除");
                        return;
                    }
                }

                // ===== 周期"关闭-重搜-重开"探测（断连检测兜底保证）=====
                // 应用还握着打开句柄时，WMI / 系统串口列表会被驱动残留骗过（鬼设备）。
                // 关掉句柄是唯一能把残留真正释放的操作，句柄一关再搜 WMI 就是绝对真实的物理状态。
                TryPeriodicCloseRescan();
                if (_port == null || !_port.IsOpen) return;   // 探测判定断连已处理，结束本轮

                // I/O 探测兜底：上面判不出断连时主动读一次。正在收数据（BytesToRead>0）时跳过，
                // 避免与 DataReceived 抢数据；安静时 ReadExisting 立即返回空串（不阻塞等待数据），
                // 句柄已失效（设备被拔/驱动异常）抛异常 → catch 判定断连。
                try
                {
                    if (_port.BytesToRead <= 0)
                    {
                        _port.ReadExisting();
                    }
                }
                catch (Exception ex)
                {
                    OnDisconnectDetected($"端口探测失败: {ex.Message}");
                }
            }
        }

        /// <summary>安全获取系统串口列表；失败返回 null（区别于"空列表"）。</summary>
        private static string[] SafeGetPortNames()
        {
            try { return SerialPort.GetPortNames(); }
            catch { return null; }
        }

        /// <summary>
        /// 周期"关闭-重搜-重开"探测：每 CloseRescanEveryTicks 次心跳（约 12 秒）把句柄关掉
        /// 再重搜重开一次。设备真没了 → 搜不到保持未连接（状态变红）；设备还在 → 重开新句柄用户无感。
        /// 正在收数据（BytesToRead&gt;0）时跳过并清零计数，避免把条码读丢。
        /// </summary>
        private void TryPeriodicCloseRescan()
        {
            if (_port == null || !_port.IsOpen) return;

            // 正在收数据：延后探测（避免与 DataReceived 抢数据、把条码读丢）
            if (_port.BytesToRead > 0)
            {
                _ticksSinceCloseRescan = 0;
                return;
            }

            _ticksSinceCloseRescan++;
            if (_ticksSinceCloseRescan < CloseRescanEveryTicks) return;
            _ticksSinceCloseRescan = 0;

            DebugLog("心跳：周期探测-关闭串口重新识别（确认设备真实存在）");
            // 关闭句柄 → 释放鬼设备残留；重搜：设备在 → 重开新句柄；设备没了 → 保持未连接。
            // 注意：ClosePortInternal 会把 _wasConnected 置 false，TryConnect 若连上会触发一次
            // "已连接" 边沿（这是"关-重搜-重开"的预期结果，日志里表现为周期性重连，属正常）。
            OnDisconnectDetected("周期探测：关闭串口重新识别");
            TryConnect();
        }

        /// <summary>
        /// 断连边沿处理：只在"原本已连接"变成"断开"时提示一次，并标记"已提示"，
        /// 让后续静默重试不刷日志。调用方（OnDisconnectDetected）已持有 _lock。
        /// </summary>
        /// <param name="reason">断连原因描述</param>
        private void OnDisconnectDetected(string reason)
        {
            if (_disposed) return;

            // 原本就没连接上：不算"掉线"，不提示
            if (!_wasConnected) return;

            _wasConnected = false;
            _disconnectReported = true;   // 断连原因已提示，后续失败静默
            ClosePortInternal();
            LogHelper.Warn($"扫码枪已断开，正在后台自动重试...（{reason}）");
            ConnectionChanged?.Invoke(this, false);
        }

        /// <summary>关闭并释放串口对象（须在 _lock 内调用），清除连接状态与接收缓冲区。</summary>
        private void ClosePortInternal()
        {
            if (_port != null)
            {
                try
                {
                    if (_port.IsOpen) _port.Close();
                }
                catch { /* 关闭失败不阻塞（可能已被系统移除） */ }
                _port.DataReceived -= OnDataReceived;
                _port.ErrorReceived -= OnSerialError;
                _port.Dispose();
                _port = null;
            }
            _currentPortName = null;
            _buffer.Clear();
        }

        /// <summary>串口数据接收事件（后台线程触发）：缓冲区按换行符切分成一条条完整条码。</summary>
        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                lock (_lock)
                {
                    if (_port == null || !_port.IsOpen) return;

                    string chunk = _port.ReadExisting();
                    if (string.IsNullOrEmpty(chunk)) return;

                    _buffer.Append(chunk);

                    // 不断取出"以换行符结尾"的完整行，直到没有换行符为止
                    while (true)
                    {
                        int nlIndex = _buffer.ToString().IndexOfAny(LineBreakChars);
                        if (nlIndex < 0) break;   // 还没有完整的一行，留到缓冲区等下一帧

                        string line = _buffer.ToString(0, nlIndex).Trim();

                        // 跳过这一行结尾的换行符（可能同时有 \r\n 两个字符）
                        int removeCount = nlIndex + 1;
                        while (removeCount < _buffer.Length &&
                               (_buffer[removeCount] == '\r' || _buffer[removeCount] == '\n'))
                        {
                            removeCount++;
                        }
                        _buffer.Remove(0, removeCount);

                        // 非空行 → 触发扫码完成事件
                        if (!string.IsNullOrEmpty(line))
                        {
                            SerialNumberScanned?.Invoke(this, line);
                        }
                    }

                    // 防御异常/噪声数据撑爆内存：串口没有行分隔符时 _buffer 无限增长
                    if (_buffer.Length > MaxLineLen) _buffer.Clear();
                }
            }
            catch (Exception ex)
            {
                // 读取异常（设备被拔掉等）：按"断连边沿"提示一次并断开连接，
                // 否则心跳会以为还连着而不重试，导致扫码枪永远恢复不了
                lock (_lock)
                {
                    OnDisconnectDetected($"读取数据异常: {ex.Message}");
                }
            }
        }

        /// <summary>串口错误接收事件（后台线程触发）：收到错误（如设备移除）时按断连边沿处理。</summary>
        private void OnSerialError(object sender, SerialErrorReceivedEventArgs e)
        {
            lock (_lock)
            {
                OnDisconnectDetected($"串口错误: {e.EventType}");
            }
        }

        /// <summary>心跳调试日志：DebugLog=true 时把端口搜索/判定结果打到 LOG，供现场排查断连识别不到。</summary>
        private void DebugLog(string message)
        {
            if (_disposed || !_cfg.DebugLog) return;
            LogHelper.Info($"[扫码枪心跳调试] {message}");
        }

        /// <summary>把端口列表拼成可读字符串（null/空列表显示 "-"）。</summary>
        private static string JoinPorts(IEnumerable<string> ports)
        {
            if (ports == null) return "-";
            bool any = false;
            var sb = new StringBuilder();
            foreach (string p in ports)
            {
                if (any) sb.Append(',');
                sb.Append(p);
                any = true;
            }
            return any ? sb.ToString() : "-";
        }

        /// <summary>触发指令：串口扫码枪上电即读码，无需触发指令，直接返回 true。</summary>
        public bool SendTrigger() => true;

        /// <summary>释放资源：停心跳线程、关串口、退订事件、触发 ConnectionChanged(false)。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 停心跳线程（后台线程循环体按 _disposed 退出）
            var th = _heartbeatThread;
            if (th != null)
            {
                _heartbeatThread = null;
                // 心跳循环按 _disposed 退出，最坏再等一个心跳周期（3s）。
                // 旧代码 Join(2000) 超时后用 Thread.Abort 兜底——Abort 已过时且可能破坏串口句柄
                // 状态，现改为等待自然退出；若真卡在 WMI/串口系统查询里（概率极低），进程退出时
                // 后台线程随之结束，不阻塞关窗。
                try { if (!th.Join(3000)) LogHelper.Warn("扫码枪心跳线程未在 3s 内退出（可能阻塞在系统查询），交由进程退出清理"); }
                catch { }
            }

            lock (_lock)
            {
                try
                {
                    if (_port != null && _port.IsOpen) _port.Close();
                }
                catch { }
                if (_port != null)
                {
                    _port.DataReceived -= OnDataReceived;
                    _port.ErrorReceived -= OnSerialError;
                    _port.Dispose();
                    _port = null;
                }
                _currentPortName = null;
                _buffer.Clear();

                // 关闭串口：通知订阅方状态变"已关闭"（边沿触发，已 false 则不发）
                if (_wasConnected)
                {
                    _wasConnected = false;
                    ConnectionChanged?.Invoke(this, false);
                }
            }
        }

        // ============ 串口参数解析（遵循项目配置序列化约定） ============

        /// <summary>
        /// 把配置里的校验位字符串解析为 Parity 枚举。
        /// 支持标准枚举名 "None"/"Odd"/"Even"/"Mark"/"Space"（忽略大小写），
        /// 非法值/空串兜底 None。与 StopBitsFromString 成对使用（见 ScanConfig 注释约定）。
        /// </summary>
        private static Parity ParityFromName(string parity)
        {
            if (string.IsNullOrWhiteSpace(parity)) return Parity.None;
            switch (parity.Trim().ToLowerInvariant())
            {
                case "even": return Parity.Even;
                case "odd": return Parity.Odd;
                case "mark": return Parity.Mark;
                case "space": return Parity.Space;
                default: return Parity.None;
            }
        }

        /// <summary>
        /// 把配置里的停止位字符串解析为 StopBits 枚举。
        /// 遵循项目约定：配置存 "1"/"15"/"2"（"15" 表示 1.5 停止位），
        /// 非法值/空串兜底 1 位。与 ParityFromName 成对使用。
        /// </summary>
        private static StopBits StopBitsFromString(string stopBits)
        {
            int value;
            if (!int.TryParse(stopBits, out value)) return StopBits.One;
            switch (value)
            {
                case 2: return StopBits.Two;
                case 15: return StopBits.OnePointFive; // 需要 1.5 停止位时配置写 "15"
                default: return StopBits.One;
            }
        }
    }
}