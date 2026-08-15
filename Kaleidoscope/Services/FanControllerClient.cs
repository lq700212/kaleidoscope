using System;
using System.Collections.Generic;
using System.IO;          // 用于读写"上次连接成功 IP"的磁盘缓存文件
using System.Net.Sockets;
using System.Threading.Tasks;
using Kaleidoscope.Models;
using NModbus;

namespace Kaleidoscope.Services
{
    /// <summary>
    /// 冷却送风机通讯实现（Modbus TCP）。
    /// 【来源】AgingTestSystem.Services.FanControllerClient 原样移植，配置类型换成独立强类型
    /// <see cref="FanConfig"/>。
    ///
    /// 【寄存器映射】（实测，见 Demo 文档）
    ///   0x0000 组合状态（未使用，忽略）
    ///   0x0001 控制/状态（写：0x0003=定值启动，0x0002=定值停止；读回同值）
    ///   0x0002 当前温度（值/100 = °C）
    ///   0x0003 当前湿度（值/100 = %RH）
    ///   0x0004 温度设定值（值/100 = °C）
    ///   0x0005 湿度设定值（值/100 = %RH）
    ///
    /// 【物理层】TCP/IP；端口默认 50000（非标准 502）；从站地址（UnitId）默认 1。
    ///
    /// 【线程安全】读写请求用 _syncRoot 锁串行化（同一时刻只有一个线程发请求）；
    /// 但【连接建立】放在锁外执行（Connect 用局部对象逐个候选建连，成功才一次性锁内 commit）——
    /// 设备离线时多候选尝试最坏耗时 候选数×FanTimeoutMs，锁外执行避免把锁占住、卡住读写调用。
    ///
    /// 【断线自愈（心跳机制）】送风机是"可选设备"，现场可能中途断电/断网。
    /// 采用"每次操作前检查连接，未连接则自动重连"的策略，后台静默持续重连；
    /// 用 10 秒重连节流，避免对已断电的设备频繁发起连接导致卡顿。
    /// 失败过程不刷日志，只由上层记"连上/断开"边沿。
    ///
    /// 【工控机 IP 记忆】自动识别连接成功后，会把"本工控机连上的控制器 IP"写入程序目录 FanLastIp.cache；
    /// 下次启动优先用缓存地址直接连，连不上再回落 FanIpAddress / FanIpCandidates 配置列表。
    /// </summary>
    public class FanControllerClient : IFanController
    {
        /// <summary>主站/连接对象的互斥锁（线程安全，见类注释）。</summary>
        private readonly object _syncRoot = new object();

        /// <summary>全局配置（Connect 时赋值）。</summary>
        private FanConfig _config;

        /// <summary>TCP 客户端（负责网络连接）。</summary>
        private TcpClient _client;

        /// <summary>Modbus 主站（负责组包/解包、发起请求）。</summary>
        private IModbusMaster _master;

        /// <summary>连接状态。volatile：Monitor 心跳/业务轮询线程在锁外读 IsConnected（见 EnsureConnected）。</summary>
        private volatile bool _isConnected;

        /// <summary>上次连接尝试的时间（重连节流：设备掉线时不要每秒都去连一次）。</summary>
        private DateTime _lastConnectAttempt = DateTime.MinValue;

        /// <summary>最近一次连接成功的 IP 地址（自动识别的结果）：设备地址没变的话一次就连上。
        /// volatile：候选列表在锁外构建（Connect），多线程并发重连时读它（见 BuildCandidateIps）。</summary>
        private volatile string _activeIp;

        /// <summary>是否已尝试从磁盘缓存恢复 _activeIp（防止每次构建候选列表都读一次磁盘）。volatile 同 _activeIp。</summary>
        private volatile bool _activeIpLoadedFromDisk;

        /// <summary>重连节流间隔（毫秒）：两次连接尝试之间至少间隔 10 秒，避免对死设备频繁发起连接。</summary>
        private const int ReconnectIntervalMs = 10000;

        /// <summary>磁盘缓存文件名（程序 exe 所在目录）：一行文本 = 本工控机最近一次连接成功的送风机 IP。</summary>
        private const string IpCacheFileName = "FanLastIp.cache";

        public bool IsConnected => _isConnected;

        /// <summary>当前实际连接成功的送风机 IP（自动识别结果），与配置里的 FanIpAddress 可能不同；未连接时为 null。</summary>
        public string ActiveIp => _activeIp;

        public event EventHandler<string> OnError;

        /// <summary>连接送风机控制屏（设计约定：不向外抛异常，统一用 OnError 通知上层）。</summary>
        public bool Connect(FanConfig config)
        {
            if (config == null)
            {
                OnError?.Invoke(this, "配置为空，无法连接");
                return false;
            }

            // ① 组装候选 IP（读配置 + 内存/磁盘缓存；锁外执行，相关字段 volatile 保证可见性）
            List<string> candidates = BuildCandidateIps(config);
            if (candidates.Count == 0)
            {
                OnError?.Invoke(this, "送风机连接参数错误：没有可用的 IP 地址，请检查 FanIpAddress/FanIpCandidates 配置");
                return false;
            }

            // ② 逐个候选【锁外】建连（纯局部对象，最坏阻塞 候选数×FanTimeoutMs，不占 _syncRoot）：
            //    设备全离线时 ReadStatus/WriteCommand/按钮拿锁调用不会再被卡数秒
            //    （旧实现锁内串行尝试，离线时锁被占住 候选数×FanTimeoutMs）。
            Exception lastError = null;
            foreach (string ip in candidates)
            {
                TcpClient client; IModbusMaster master; string err;
                if (TryBuildConnection(ip, config.FanPort, config.FanTimeoutMs, out client, out master, out err))
                {
                    // ③ 成功后一次性锁内 commit（先 Disconnect 清掉可能残留的旧连接，防多连叠加）
                    lock (_syncRoot)
                    {
                        Disconnect();
                        _config = config;
                        _client = client;
                        _master = master;
                        _isConnected = true;
                        _activeIp = ip;      // 记住本次成功的 IP（内存），下次重连优先尝试它
                        SaveCachedIp(ip);    // 写盘缓存：本工控机"上次连上的控制器 IP"，下次启动直接用它
                    }
                    return true;
                }
                // 本 IP 失败：记下原因，继续尝试下一个候选
                lastError = new Exception($"IP {ip}: {err}");
            }

            // ④ 全部候选失败：更新配置引用并通知上层（会显示在 UI 上）
            lock (_syncRoot) { _config = config; }
            OnError?.Invoke(this, $"送风机连接失败（已尝试 {candidates.Count} 个 IP: {string.Join(", ", candidates)}）: {lastError?.Message}");
            return false;
        }

        /// <summary>
        /// 建立到单个候选 IP 的 TCP + Modbus 主站连接（静态方法：只用局部对象，不碰任何字段，
        /// 供 Connect 在锁外逐个尝试，成功后才一次性 commit 到字段）。
        /// </summary>
        /// <param name="ip">候选 IP</param>
        /// <param name="port">送风机端口（默认 50000）</param>
        /// <param name="timeoutMs">连接/读写超时（毫秒）</param>
        /// <param name="client">成功时输出已连接的 TcpClient（未占用字段，调用方负责 commit）</param>
        /// <param name="master">成功时输出已就绪的 Modbus 主站</param>
        /// <param name="error">失败原因（成功时为 null）</param>
        /// <returns>true=本候选连接成功</returns>
        private static bool TryBuildConnection(string ip, int port, int timeoutMs,
                                               out TcpClient client, out IModbusMaster master, out string error)
        {
            client = null; master = null; error = null;
            TcpClient c = new TcpClient();
            try
            {
                c.SendTimeout = timeoutMs;
                c.ReceiveTimeout = timeoutMs;

                // 【重要】TcpClient.Connect 是同步方法且不受上面 Timeout 属性控制（走系统 TCP 连接
                // 超时，默认最长 ~20 秒）。用 BeginConnect + WaitOne 实现"手动超时"：timeoutMs 内
                // 没成功立即放弃，避免对不可达 IP 卡住调用线程。
                IAsyncResult ar = c.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    error = $"连接超时（{timeoutMs}ms）";
                    try { c.Close(); } catch { }
                    c.Dispose();
                    return false;
                }
                c.EndConnect(ar);

                var factory = new ModbusFactory();
                IModbusMaster m = factory.CreateMaster(c);
                m.Transport.ReadTimeout = timeoutMs;
                m.Transport.WriteTimeout = timeoutMs;
                client = c;
                master = m;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { c.Close(); } catch { }
                try { c.Dispose(); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 组装本次连接要尝试的候选 IP 列表（自动识别核心，在 Connect 的锁外阶段调用）。
        /// 顺序（越靠前越优先）：
        ///   1) 自动识别开启时：上次连接成功的 IP（_activeIp，程序重启后从磁盘缓存恢复）——工控机记忆
        ///   2) 配置的主 IP（FanIpAddress）——始终优先尝试
        ///   3) FanAutoDetectEnabled=true 时，追加配置的候选 IP 列表（FanIpCandidates）
        /// 自动过滤：空字符串 / 非法 IP / 重复项。
        /// 【线程安全】本方法读 config（调用方传入，连接期间不改）与 volatile 字段（_activeIp/
        /// _activeIpLoadedFromDisk），可在锁外安全调用；并发重连重复读盘缓存是幂等无害的。
        /// </summary>
        /// <param name="config">送风机配置（本次要连接的参数来源）</param>
        /// <returns>候选 IP 列表（可能为空，表示配置里没 IP）</returns>
        private List<string> BuildCandidateIps(FanConfig config)
        {
            var list = new List<string>();

            // 局部函数：把"合法且未出现过"的 IP 追加进列表
            void AddCandidate(string ip)
            {
                if (string.IsNullOrWhiteSpace(ip)) return;
                ip = ip.Trim();
                if (!System.Net.IPAddress.TryParse(ip, out _)) return;   // 跳过非法 IP
                foreach (string x in list)
                {
                    if (string.Equals(x, ip, StringComparison.OrdinalIgnoreCase)) return;   // 已存在则跳过
                }
                list.Add(ip);
            }

            // 1) 自动识别开启时：优先用"上次连接成功的 IP"（磁盘缓存恢复/本次会话内存）
            if (config.FanAutoDetectEnabled)
            {
                if (!_activeIpLoadedFromDisk)
                {
                    _activeIpLoadedFromDisk = true;
                    _activeIp = _activeIp ?? LoadCachedIp();   // 首次构建时从磁盘恢复缓存
                }
                AddCandidate(_activeIp);
            }

            // 2) 配置的主 IP 始终尝试
            AddCandidate(config.FanIpAddress);

            // 3) 自动识别开启时，追加候选 IP 列表
            if (config.FanAutoDetectEnabled && config.FanIpCandidates != null)
            {
                foreach (string ip in config.FanIpCandidates)
                {
                    AddCandidate(ip);
                }
            }

            return list;
        }

        /// <summary>
        /// 读取磁盘缓存的上次连接成功的送风机 IP（仅自动识别开启时使用）。
        /// 文件不存在/内容非法 → 返回 null（回落配置列表逐个尝试）；读失败不阻塞连接。
        /// 只认合法 IPv4：防止缓存被写坏后一直连错地址。
        /// </summary>
        private string LoadCachedIp()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, IpCacheFileName);
                if (!File.Exists(path)) return null;
                string content = File.ReadAllText(path).Trim();
                return System.Net.IPAddress.TryParse(content, out _) ? content : null;
            }
            catch
            {
                return null;   // 读缓存失败不阻塞连接
            }
        }

        /// <summary>把"本次连接成功的送风机 IP"写入磁盘缓存；写失败忽略（无写权限/磁盘只读等）。</summary>
        private void SaveCachedIp(string ip)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, IpCacheFileName);
                File.WriteAllText(path, ip);
            }
            catch
            {
                // 写缓存失败忽略，下次仍回落配置列表
            }
        }

        /// <summary>
        /// 断开连接。【线程安全】用 _syncRoot 锁保护对 _client/_master 的修改。
        /// 注意：C# 的 lock 在同一线程是可重入的，所以 ConnectInternal 在锁内再调 Disconnect 也不会死锁。
        /// </summary>
        public void Disconnect()
        {
            lock (_syncRoot)
            {
                _isConnected = false;
                try
                {
                    if (_client != null)
                    {
                        _client.Close();
                        _client.Dispose();
                    }
                }
                catch
                {
                    // Close/Dispose 在"网线被拔"等场景可能抛异常，这里吞掉
                }
                finally
                {
                    _client = null;
                    _master = null;
                }
            }
        }

        /// <summary>
        /// 确保连接已建立；未连接则尝试（节流后）自动重连。可在锁外调用。
        /// 【心跳自愈】不再设"重试上限"：后台静默持续重连（10 秒节流），失败过程不刷日志；
        /// 需要送风机时上层可调用 <see cref="ReconnectNow"/> 立即重连。
        /// 【锁策略】节流判断 + 标记在锁内串行化（防止业务轮询与 Monitor 后台重连同时通过节流
        /// 窗口重复建连），但真正的建连放锁外（Connect 内部锁外建连 + 锁内 commit），不占锁。
        /// </summary>
        /// <returns>true 表示当前可用（已连接），false 表示不可用</returns>
        private bool EnsureConnected()
        {
            // 已连接且主站存在 → 直接可用
            if (_isConnected && _master != null && _client != null)
            {
                return true;
            }

            // 未连接：节流判断 + 标记
            lock (_syncRoot)
            {
                if (_isConnected && _master != null && _client != null) return true;
                if ((DateTime.Now - _lastConnectAttempt).TotalMilliseconds < ReconnectIntervalMs)
                {
                    return false;
                }
                _lastConnectAttempt = DateTime.Now;
            }

            // 建连放锁外
            return Connect(_config);
        }

        /// <summary>
        /// 按需重连：用户点击"定值启动/定值停止"等需要送风机的操作时由上层调用，
        /// 立即触发重连（不等后台 10 秒节流，保证按钮响应及时）。
        /// 【异步化】同步执行最坏阻塞 候选数×FanTimeoutMs（设备离线时数秒），UI 按钮场景会卡死
        /// 界面，故改为后台执行、立即返回当前状态；连接完成后由后续 ReadStatus/WriteCommand 生效
        /// （EnsureConnected 也会兜底重连）。
        /// </summary>
        /// <returns>重连后是否已连接（异步重连下为发起时的当前状态，可能仍 false）</returns>
        public bool ReconnectNow()
        {
            if (_isConnected) return true;
            var cfg = _config;
            if (cfg == null) return false;
            Task.Run(() => Connect(cfg));
            return _isConnected;
        }

        /// <summary>
        /// 读取送风机当前状态（状态 + 温度 + 湿度 + 设定值），一次批量读 6 个寄存器（0x0000~0x0005）。
        /// </summary>
        /// <returns>送风机数据；读取失败返回 null（上层显示"离线"）</returns>
        public FanData ReadStatus()
        {
            // 配置为空或未启用送风机时，直接返回 null
            if (_config == null || !_config.FanEnabled)
            {
                OnError?.Invoke(this, "送风机未启用");
                return null;
            }

            try
            {
                // 锁外自愈：未连接则按节流重连（建连在锁外，不占锁，见 Connect）
                if (!EnsureConnected()) return null;

                ushort[] values;
                lock (_syncRoot)
                {
                    if (_master == null) return null;
                    // 读保持寄存器（功能码 0x03），从 0x0000 一次读 6 个
                    values = _master.ReadHoldingRegisters(_config.FanUnitId, 0x0000, 6);
                }

                // 防御性检查：寄存器数量不足说明设备返回异常
                if (values == null || values.Length < 6) return null;

                // 按实测映射解析（索引对应关系见类注释）：
                // values[0] -> 0x0000（组合状态，未使用，忽略）
                // values[1] -> 0x0001（控制/状态）
                // values[2] -> 0x0002（当前温度，/100 = °C）
                // values[3] -> 0x0003（当前湿度，/100 = %RH）
                // values[4] -> 0x0004（温度设定值，/100 = °C）
                // values[5] -> 0x0005（湿度设定值，/100 = %RH）
                return new FanData
                {
                    RunState = (FanRunState)values[1],
                    Temperature = values[2] / 100.0f,
                    Humidity = values[3] / 100.0f,
                    TempSetpoint = values[4] / 100.0f,
                    HumSetpoint = values[5] / 100.0f,
                    IsOnline = true,
                    CollectTime = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                // 读取失败：标记断开并释放坏连接（下次操作自动重连）
                MarkDisconnected();
                OnError?.Invoke(this, $"送风机读取失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>定值启动（写入 0x0001 = 0x0003）：让送风机按控制屏设定的温度运行（厂商自动控温）。</summary>
        public bool StartFixedValue()
        {
            return WriteCommand(0x0003);
        }

        /// <summary>定值停止（写入 0x0001 = 0x0002）。</summary>
        public bool Stop()
        {
            return WriteCommand(0x0002);
        }

        /// <summary>向控制寄存器 0x0001 写入控制命令（公共内部方法）。</summary>
        /// <param name="command">命令值（0x0003=定值启动，0x0002=定值停止）</param>
        /// <returns>是否发送成功</returns>
        private bool WriteCommand(ushort command)
        {
            // 配置为空或未启用送风机时，直接返回 false
            if (_config == null || !_config.FanEnabled)
            {
                OnError?.Invoke(this, "送风机未启用");
                return false;
            }

            try
            {
                // 锁外自愈：未连接则按节流重连（建连在锁外，不占锁，见 Connect）
                if (!EnsureConnected()) return false;

                lock (_syncRoot)
                {
                    if (_master == null) return false;
                    // 写单个保持寄存器（功能码 0x06）
                    _master.WriteSingleRegister(_config.FanUnitId, 0x0001, command);
                }
                return true;
            }
            catch (Exception ex)
            {
                // 发送失败：标记断开并释放坏连接（下次操作自动重连）
                MarkDisconnected();
                OnError?.Invoke(this, $"送风机命令发送失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 标记断开并释放坏连接（幂等）。锁内释放与读写串行化。
        /// 【断连即释放】不必等 Monitor 下一轮重连才在 Connect 里关——坏 socket 多挂几秒会
        /// 让后续读请求白等一个超时。
        /// </summary>
        private void MarkDisconnected()
        {
            lock (_syncRoot)
            {
                _isConnected = false;
                try { if (_client != null) _client.Close(); } catch { }
                try { if (_client != null) _client.Dispose(); } catch { }
                _client = null;
                _master = null;
            }
        }

        /// <summary>释放资源（关闭连接）。</summary>
        public void Dispose()
        {
            Disconnect();
        }
    }
}