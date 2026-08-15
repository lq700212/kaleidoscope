using System;
using System.Collections.Generic;
using System.IO;          // 用于读写"上次连接成功 IP"的磁盘缓存文件
using System.Net.Sockets;
using CommonLib.Models;
using NModbus;

namespace CommonLib.Services
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
    /// 【线程安全】与 ModbusTcpIoController 相同，用 _syncRoot 锁串行化对主站的所有访问。
    /// 采集线程和 UI 线程（按钮点击）可能并发调用本类，锁保证同一时刻只有一个线程发请求。
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

        /// <summary>连接状态。</summary>
        private bool _isConnected;

        /// <summary>上次连接尝试的时间（重连节流：设备掉线时不要每秒都去连一次）。</summary>
        private DateTime _lastConnectAttempt = DateTime.MinValue;

        /// <summary>最近一次连接成功的 IP 地址（自动识别的结果）：设备地址没变的话一次就连上。</summary>
        private string _activeIp;

        /// <summary>是否已尝试从磁盘缓存恢复 _activeIp（防止每次构建候选列表都读一次磁盘）。</summary>
        private bool _activeIpLoadedFromDisk;

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
            _config = config;
            lock (_syncRoot)
            {
                return ConnectInternal();
            }
        }

        /// <summary>
        /// 连接送风机控制屏（实际执行部分，必须在 _syncRoot 锁内调用）。
        /// 【自动识别 IP】现场控制器 IP 可能是 192.168.1.220/.221/.222 中任意一个（换工作台、换控制器都会变）。
        /// 为免去每次改配置，这里按顺序逐个尝试候选 IP，第一个连接成功的即为设备真实地址。
        /// </summary>
        private bool ConnectInternal()
        {
            try
            {
                // 0) 先断开旧连接（避免重复 Connect 导致句柄泄漏）
                Disconnect();

                // 1) 组装候选 IP 列表（自动识别核心，见 BuildCandidateIps）
                List<string> candidates = BuildCandidateIps();
                if (candidates.Count == 0)
                {
                    OnError?.Invoke(this, "送风机连接参数错误：没有可用的 IP 地址，请检查 FanIpAddress/FanIpCandidates 配置");
                    return false;
                }

                // 2) 按顺序逐个尝试候选 IP：第一个连上的就是设备真实地址
                Exception lastError = null;
                foreach (string ip in candidates)
                {
                    try
                    {
                        // 为每个候选 IP 单独创建 TcpClient（上一个失败的已关闭，不能复用）
                        TcpClient client = new TcpClient();
                        client.SendTimeout = _config.FanTimeoutMs;
                        client.ReceiveTimeout = _config.FanTimeoutMs;

                        // 【重要】TcpClient.Connect 是同步方法，且不受上面 Timeout 属性控制
                        //（走的是系统 TCP 连接超时，默认可能长达 ~20 秒）。如果送风机掉线/网线没插好，
                        // 直接 Connect 会让界面卡住很久。这里用 BeginConnect + WaitOne 实现"手动超时"：
                        //   - FanTimeoutMs 内连接成功 → EndConnect 完成连接
                        //   - FanTimeoutMs 内没成功 → 抛超时异常，继续试下一个候选 IP
                        IAsyncResult connectResult = client.BeginConnect(ip, _config.FanPort, null, null);
                        if (!connectResult.AsyncWaitHandle.WaitOne(_config.FanTimeoutMs))
                        {
                            client.Close();
                            client.Dispose();
                            lastError = new TimeoutException($"连接超时（{_config.FanTimeoutMs}ms）");
                            continue;   // 本 IP 超时：尝试下一个候选
                        }
                        client.EndConnect(connectResult);

                        // 连接成功：绑定当前客户端 + 创建 Modbus 主站（Master）
                        _client = client;
                        var factory = new ModbusFactory();
                        _master = factory.CreateMaster(_client);
                        _master.Transport.ReadTimeout = _config.FanTimeoutMs;
                        _master.Transport.WriteTimeout = _config.FanTimeoutMs;

                        _isConnected = true;
                        _activeIp = ip;      // 记住本次成功的 IP（内存），下次重连优先尝试它
                        SaveCachedIp(ip);    // 写盘缓存：本工控机"上次连上的控制器 IP"，下次启动直接用它
                        return true;
                    }
                    catch (Exception ex)
                    {
                        // 本 IP 连接失败：记下原因，继续尝试下一个候选
                        lastError = ex;
                    }
                }

                // 3) 所有候选 IP 都连不上：通知上层（会显示在 UI 上），并清理资源
                OnError?.Invoke(this, $"送风机连接失败（已尝试 {candidates.Count} 个 IP: {string.Join(", ", candidates)}）: {lastError?.Message}");
                Disconnect();
                return false;
            }
            catch (Exception ex)
            {
                // 兜底异常：连接失败，通知上层并清理资源
                OnError?.Invoke(this, $"送风机连接失败: {ex.Message}");
                Disconnect();
                return false;
            }
        }

        /// <summary>
        /// 组装本次连接要尝试的候选 IP 列表（自动识别核心）。
        /// 顺序（越靠前越优先）：
        ///   1) 自动识别开启时：上次连接成功的 IP（_activeIp，程序重启后从磁盘缓存恢复）——工控机记忆
        ///   2) 配置的主 IP（FanIpAddress）——始终优先尝试
        ///   3) FanAutoDetectEnabled=true 时，追加配置的候选 IP 列表（FanIpCandidates）
        /// 自动过滤：空字符串 / 非法 IP / 重复项。
        /// </summary>
        private List<string> BuildCandidateIps()
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
            if (_config.FanAutoDetectEnabled)
            {
                if (!_activeIpLoadedFromDisk)
                {
                    _activeIpLoadedFromDisk = true;
                    _activeIp = _activeIp ?? LoadCachedIp();   // 首次构建时从磁盘恢复缓存
                }
                AddCandidate(_activeIp);
            }

            // 2) 配置的主 IP 始终尝试
            AddCandidate(_config.FanIpAddress);

            // 3) 自动识别开启时，追加候选 IP 列表
            if (_config.FanAutoDetectEnabled && _config.FanIpCandidates != null)
            {
                foreach (string ip in _config.FanIpCandidates)
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
        /// 确保连接已建立；未连接则尝试（节流后）自动重连。必须在 _syncRoot 锁内调用。
        /// 【心跳自愈】不再设"重试上限"：后台静默持续重连（10 秒节流），失败过程不刷日志；
        /// 需要送风机时上层可调用 <see cref="ReconnectNow"/> 立即重连。
        /// </summary>
        /// <returns>true 表示当前可用（已连接），false 表示不可用</returns>
        private bool EnsureConnected()
        {
            // 已连接且主站存在 → 直接可用
            if (_isConnected && _master != null && _client != null)
            {
                return true;
            }

            // 未连接：先做"重连节流"判断（刚尝试过失败，10 秒内不再重试，避免对死设备频繁连接）
            if ((DateTime.Now - _lastConnectAttempt).TotalMilliseconds < ReconnectIntervalMs)
            {
                return false;
            }

            _lastConnectAttempt = DateTime.Now;
            return Connect(_config);
        }

        /// <summary>
        /// 按需重连：用户点击"定值启动/定值停止"等需要送风机的操作时由上层调用，
        /// 立即重连一次（不等后台 10 秒节流，保证按钮响应及时）。
        /// </summary>
        /// <returns>重连后是否已连接</returns>
        public bool ReconnectNow()
        {
            if (_isConnected) return true;
            return Connect(_config);
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
                ushort[] values;
                lock (_syncRoot)
                {
                    // 未连接则尝试（节流后）自动重连
                    if (!EnsureConnected()) return null;

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
                // 读取失败：断开连接（下次操作会按节流规则自动重连）
                _isConnected = false;
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
                lock (_syncRoot)
                {
                    // 未连接则尝试（节流后）自动重连
                    if (!EnsureConnected()) return false;

                    // 写单个保持寄存器（功能码 0x06）
                    _master.WriteSingleRegister(_config.FanUnitId, 0x0001, command);
                }
                return true;
            }
            catch (Exception ex)
            {
                // 发送失败：断开连接（下次操作自动重连）
                _isConnected = false;
                OnError?.Invoke(this, $"送风机命令发送失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>释放资源（关闭连接）。</summary>
        public void Dispose()
        {
            Disconnect();
        }
    }
}