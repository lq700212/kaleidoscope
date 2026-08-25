using System;
using System.Collections.Concurrent;
using System.Threading;
using Kaleidoscope.Models;
using Kaleidoscope.Utils;
using HslCommunication;
using HslCommunication.Core.Device;
using HslCommunication.Core;
using HslCommunication.Profinet.Melsec;
using HslCommunication.Profinet.Omron;
using HslCommunication.Profinet.Inovance;
using HslCommunication.Profinet.Siemens;

namespace Kaleidoscope.Services
{
    /// <summary>
    /// 多品牌 PLC 主站客户端（HSL 版）：上位机经三菱 MC/A1E、欧姆龙 FINS/CIP、西门子 S7、
    /// 汇川 H5U 等【非 Modbus 协议】主动读写 PLC。协议能力来自商业通讯库 HslCommunication
    /// （12.6.0.0，商业授权、授权码需自行购买，统一在 Utils/HslAuthorization 注册）。
    ///
    /// 【来源与取舍（现场验证过的精华与坑）】
    /// - 精华①「一个接口吃掉所有品牌」：所有读写都走 IReadWriteNet 统一接口，工厂
    ///   CreateDevice 是唯一知道具体协议类的地方——新增品牌只加一个 case，读写/轮询/事件链路零改动；
    /// - 精华②「单通道串行化」：以 ConcurrentQueue+专职线程把所有寄存器访问串行化，
    ///   规避多线程共用一条连接的竞态；本类改用 _syncRoot 锁串行化（对齐库内
    ///   ModbusTcpMasterClient 范式，调用即执行、心智更简单），效果等价且少一条常驻线程；
    /// - 替换掉的坑：Thread.Abort 关线程 → volatile 标记 + Timer；读结果静态槽位（多读者互相覆盖）
    ///   → 返回值直接给调用方 + 按名缓存；同步 Connect 无超时 → HSL ConnectTimeOut 强制超时。
    ///
    /// 【连接管理（无 ConnectionMonitor 参与，自带自愈）】
    /// 多品牌模式（PlcRole.MultiBrand）下 DeviceHub 的 ConnectionMonitor 不管本类（它只管
    /// PlcService/PlcMaster），因此本类自带两条后台 Timer：
    /// - 心跳 Timer（HeartbeatIntervalMs，默认 3s）：未连接按重连节流自动恢复；已连接且配置了
    ///   HeartbeatAddress 时周期读一次做应用层探活（能发现拔网线这类静默断连）；
    /// - 轮询 Timer（PollIntervalMs + PollItems 非空时启动）：周期读取配置的数据段，
    ///   结果发 PollDataUpdated 事件并缓存（TryGetLastPollData 免订阅取数）。
    ///
    /// 【断连判定】读写返回 IsSuccess=false 或抛异常 → 视为链路故障：置未连接、释放坏 socket、
    /// 边沿触发 ConnectionChanged(false)，由心跳/轮询 Timer 自动重连恢复。（朴素策略：PLC 报
    /// 协议错误也会被当成断线，代价只是下个周期重连一次，现场影响可忽略；换来的是逻辑简单可靠。）
    ///
    /// 【线程安全】_syncRoot 串行化所有连接/读写操作；事件在工作线程触发，UI 订阅方自行 Invoke。
    ///
    /// 【热更支持】Dispose 干净（停双 Timer + 关连接）；Connect 可反复调用换配置，状态集中在实例内。
    /// </summary>
    public class HslPlcClient : IDisposable
    {
        /// <summary>连接/读写互斥锁：连接、读写、轮询、心跳、重连都在锁内串行化。</summary>
        private readonly object _syncRoot = new object();

        /// <summary>当前生效配置（Connect 时赋值）。</summary>
        private MultiBrandPlcConfig _config;

        /// <summary>HSL 设备实例（连接层视图：ConnectServer/ConnectClose/超时参数都在这层）。</summary>
        private DeviceTcpNet _conn;

        /// <summary>同一实例的读写接口视图（IReadWriteNet 一个接口吃掉所有品牌的读写差异）。</summary>
        private IReadWriteNet _rw;

        /// <summary>当前连接状态（volatile：UI/业务线程可能在锁外读）。</summary>
        private volatile bool _isConnected;

        /// <summary>上次连接状态（边沿检测用，只在锁内读写）。</summary>
        private bool _wasConnected;

        /// <summary>上次连接尝试时间（EnsureConnected 重连节流）。</summary>
        private DateTime _lastConnectAttempt = DateTime.MinValue;

        /// <summary>上次"连接失败"日志时间（30s 节流，防重连期间刷屏，对齐扫码枪降噪策略）。</summary>
        private DateTime _lastFailLogAt = DateTime.MinValue;

        /// <summary>后台轮询 Timer（StartPolling 启动，StopPolling/Dispose 停止）。</summary>
        private Timer _pollTimer;

        /// <summary>后台心跳自愈 Timer（Connect 成功即启动，Dispose 停止）。</summary>
        private Timer _keepAliveTimer;

        /// <summary>
        /// 最近一次轮询结果缓存：Name → 原始数组（bool[]/short[]/ushort[]/int[]/float[]）。
        /// ConcurrentDictionary：轮询线程写、业务/UI 读，不共用网络锁——一次 2s 超时的读请求
        /// 不会把"取缓存"也卡住（锁分离，同 ModbusTcpMasterClient 的设计教训）。
        /// </summary>
        private readonly ConcurrentDictionary<string, object> _lastPollData =
            new ConcurrentDictionary<string, object>();

        private volatile bool _disposed;

        /// <summary>是否已连接（true=最近一次通讯成功）。</summary>
        public bool IsConnected => _isConnected;

        /// <summary>当前连接目标描述（如 "MelsecMc 192.168.0.1:6000"；未配置返回 null）。</summary>
        public string ActiveEndpoint => _config == null ? null :
            $"{BrandLabel(_config.Brand)} {_config.IpAddress}:{EffectivePort(_config)}";

        /// <summary>连接状态边沿事件（true=连上，false=断开；工作线程触发）。</summary>
        public event EventHandler<bool> ConnectionChanged;

        /// <summary>通讯错误回调（工作线程触发；连接失败/读写失败都走这里）。</summary>
        public event EventHandler<string> OnError;

        /// <summary>后台轮询读回一段数据（Name=轮询项名，Values=原始类型数组；工作线程触发）。
        /// 与 Modbus 主站的 PlcPollEventArgs 同构，上层可用同一套取数逻辑。</summary>
        public event EventHandler<PlcPollEventArgs> PollDataUpdated;

        /// <summary>
        /// 事件安全触发器：订阅方代码抛异常时只记日志、不外抛。
        /// 【为什么必须有】本类事件多在 Timer 回调/后台线程的 catch 块里触发（如心跳失败→
        /// ConnectionChanged(false) + OnError），若订阅者再抛异常，会从 catch 块冒出成为
        /// 【未捕获异常】直接拖垮整个进程；一个写坏的 UI 回调绝不能拖垮通讯线程——工控库
        /// 必须自带这层隔离（库约定"UI 订阅方自行 Invoke"，但无法强制它不抛异常）。
        /// </summary>
        private void RaiseSafe<T>(EventHandler<T> handler, T args, string eventName)
        {
            if (handler == null) return;
            try { handler(this, args); }
            catch (Exception ex) { LogHelper.Warn("多品牌 PLC " + eventName + " 订阅者异常(已隔离)：" + ex.Message); }
        }

        // ═══════════════════ 连接生命周期 ═══════════════════

        /// <summary>
        /// 带配置构造（**DeviceHub 标准用法**）：只保存配置快照并启动心跳 Timer，不在调用线程碰网络——
        /// 首次连接与断线自愈都由心跳后台线程按节流异步完成（多品牌模式没有 ConnectionMonitor 参与，
        /// 本类必须自己保证"至少有一条后台通道在工作"，否则永远没人发起首连）。
        /// </summary>
        /// <param name="config">多品牌 PLC 连接与轮询配置</param>
        public HslPlcClient(MultiBrandPlcConfig config)
        {
            lock (_syncRoot)
            {
                // _config 必须在锁内赋值：防止心跳首拍与赋值交错读到半初始化状态
                _config = config;
            }
            StartKeepAlive(); // 心跳后台线程负责首连与自愈（dueTime≥1s，届时构造已完成，安全）
        }

        /// <summary>无参构造（直连测试等手动驱动场景）：先建实例，之后由调用方在后台线程调
        /// <see cref="Connect"/> 传入配置并立即建连。</summary>
        public HslPlcClient()
        {
        }

        /// <summary>
        /// 连接目标 PLC（换配置并立即建连：断开旧连 → 按品牌建 HSL 客户端 → ConnectServer）。
        /// 设计约定：不向外抛异常，失败通过 OnError 通知并返回 false。
        /// 注意：本方法同步执行（最多阻塞 TimeoutMs×2），请勿在 UI 线程调用——DeviceHub 场景
        /// 用带参构造即可（连接在心跳线程异步完成），本方法主要供测试台等手动驱动场景使用。
        /// </summary>
        /// <param name="config">多品牌 PLC 连接与轮询配置</param>
        /// <returns>true=连接成功</returns>
        public bool Connect(MultiBrandPlcConfig config)
        {
            lock (_syncRoot)
            {
                // _config 必须在锁内赋值：否则心跳/轮询线程可能读到"新配置+旧连接"，拿新地址读旧设备
                _config = config;
                return ConnectInternal();
            }
        }

        /// <summary>连接实际执行部分（必须在 _syncRoot 锁内调用）。</summary>
        private bool ConnectInternal()
        {
            // 已废弃拒绝重连：【关键防线】Dispose 时心跳/轮询回调可能已越过开头的 _disposed
            // 检查正在等锁（in-flight），若此处不拦，回调拿到锁后会"复活"刚被释放的连接，
            // 甚至经 StartKeepAlive 把刚停掉的 Timer 重新拉起——死对象带活跃线程，热更必泄漏。
            if (_disposed) return false;
            try
            {
                CloseConnCore(); // 先清旧设备（幂等：热更换配置/重连时清理旧句柄）

                if (_config == null)
                {
                    RaiseSafe(OnError, "多品牌 PLC 配置为空，无法连接", "OnError");
                    return false;
                }

                // HSL 商业授权：进程内首次使用时注册（幂等，见 HslAuthorization）
                HslAuthorization.EnsureRegistered();

                // 按品牌建客户端（工厂是唯一知道具体协议类的地方，新增品牌只加 case）
                _conn = CreateDevice(_config);
                OperateResult ret = _conn.ConnectServer();
                if (!ret.IsSuccess)
                {
                    LogConnectFailure("连接失败：" + ret.Message);
                    // 连接失败也通知订阅方（对齐 Modbus 主站："连接失败→状态灯变红"；边沿检测防重复事件）
                    RaiseSafe(OnError,
                        $"多品牌 PLC 连接失败[{BrandLabel(_config.Brand)}] " +
                        $"{_config.IpAddress}:{EffectivePort(_config)}：{ret.Message}", "OnError");
                    try { _conn.ConnectClose(); } catch { /* 半开连接关闭异常吞掉 */ }
                    _conn = null;
                    _rw = null;
                    SetConnected(false);
                    return false;
                }

                _rw = _conn; // 同一实例的读写接口视图
                SetConnected(true);
                LogHelper.Info($"多品牌 PLC 已连接[{BrandLabel(_config.Brand)}] " +
                               $"{_config.IpAddress}:{EffectivePort(_config)}");
                StartKeepAlive(); // 连上后启动心跳自愈（幂等）
                return true;
            }
            catch (Exception ex)
            {
                LogConnectFailure("连接异常：" + ex.Message);
                RaiseSafe(OnError, $"多品牌 PLC 连接失败: {ex.Message}", "OnError");
                CloseConnCore();
                SetConnected(false);
                return false;
            }
        }

        /// <summary>
        /// 断开连接（释放 HSL 客户端长连接；幂等）。主动断开不发告警事件（对齐 Modbus 主站）。
        /// 【阻塞语义】抢同一把 _syncRoot：若心跳线程正在重连中（最长 TimeoutMs 级），本方法
        /// 最坏等一个重连周期才返回——与库内 ModbusTcpMasterClient.Disconnect 同一范式。
        /// DeviceHub 的热更/关窗在最坏情况会因此短暂停顿（秒级），属已知且可接受的权衡：
        /// 不做"锁外强杀 socket"是因为 HSL 连接对象无法安全跨线程关闭（内部缓冲区状态不可控）。
        /// </summary>
        public void Disconnect()
        {
            lock (_syncRoot)
            {
                _isConnected = false;
                CloseConnCore();
            }
        }

        /// <summary>
        /// 确保已连接；未连接则按节流尝试自动重连（ReconnectIntervalMs 内不重复尝试）。
        /// 心跳/轮询/读写前都会调它（操作前自愈）。必须在 _syncRoot 锁内调用。
        /// </summary>
        private bool EnsureConnected()
        {
            if (_disposed) return false;   // 废弃后不再自愈（防 in-flight 回调复活连接，见 ConnectInternal）
            if (_isConnected && _conn != null) return true;
            if (_config == null) return false;
            // 重连节流：刚失败过 ReconnectIntervalMs 内不再重试，避免对断电设备高频无效连接
            if ((DateTime.Now - _lastConnectAttempt).TotalMilliseconds < Math.Max(1000, _config.ReconnectIntervalMs))
                return false;
            _lastConnectAttempt = DateTime.Now;
            return ConnectInternal();
        }

        /// <summary>立即重连一次（业务层"重试"按钮等按需场景，不等节流）。须在后台线程调用。</summary>
        public bool ReconnectNow()
        {
            lock (_syncRoot)
            {
                if (_disposed) return false;
                if (_isConnected && _conn != null) return true; // 锁内二次确认：防"检查后连接被心跳并发断开"窗口期重复重连
                _lastConnectAttempt = DateTime.MinValue;
                return ConnectInternal();
            }
        }

        // ═══════════════════ 后台心跳 / 自动轮询 ═══════════════════

        /// <summary>
        /// 启动后台自动轮询：PollIntervalMs &gt; 0 且 PollItems 非空时定时逐项读取，
        /// 结果发 PollDataUpdated 并写入缓存。重复调用幂等。由 DeviceHub.Start() 自动调用。
        /// </summary>
        public void StartPolling()
        {
            if (_disposed) return;
            if (_config == null || _config.PollIntervalMs <= 0 ||
                _config.PollItems == null || _config.PollItems.Count == 0) return;
            if (_pollTimer != null) return; // 幂等
            _pollTimer = new Timer(PollTick, null, _config.PollIntervalMs, _config.PollIntervalMs);
        }

        /// <summary>停止后台自动轮询（Dispose 自动调用；重复调用安全）。</summary>
        public void StopPolling()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Dispose();
                _pollTimer = null;
            }
        }

        /// <summary>
        /// 启动心跳自愈 Timer（幂等）：周期检查连接态——未连接按节流重连；已连接且配了
        /// HeartbeatAddress 则读该地址探活。多品牌模式无 ConnectionMonitor 参与，这是本类
        /// 断线自愈的兜底（轮询 Timer 只在有轮询项时才启动，不能依赖它探活）。
        /// 【加锁】构造线程与连接成功路径（心跳/读写线程）都可能首次调用，双检必须在锁内
        /// 才能保证只创建一个 Timer（否则并发窗口会泄漏一个永远跑着的 Timer）。
        /// </summary>
        private void StartKeepAlive()
        {
            if (_disposed) return;
            lock (_syncRoot)
            {
                if (_disposed || _keepAliveTimer != null) return;
                int period = _config != null ? Math.Max(1000, _config.HeartbeatIntervalMs) : 3000;
                _keepAliveTimer = new Timer(KeepAliveTick, null, period, period);
            }
        }

        /// <summary>停止心跳 Timer。</summary>
        private void StopKeepAlive()
        {
            if (_keepAliveTimer != null)
            {
                _keepAliveTimer.Dispose();
                _keepAliveTimer = null;
            }
        }

        /// <summary>心跳一拍：自愈重连 + 可选应用层探活（锁内执行，与读写在 _syncRoot 上串行）。</summary>
        private void KeepAliveTick(object state)
        {
            if (_disposed) return;
            try
            {
                lock (_syncRoot)
                {
                    if (!EnsureConnected()) return; // 未连接且未到节流期：本拍跳过
                    // 未配心跳地址：只做"连接态检查"（socket 由 HSL 维持），不发报文打扰设备
                    if (_config == null || string.IsNullOrWhiteSpace(_config.HeartbeatAddress)) return;
                    object content;
                    OperateResult ret = ReadCore(_config.HeartbeatDataType,
                        _config.HeartbeatAddress.Trim(), 1, out content);
                    if (ret.IsSuccess) SetConnected(true);
                    else MarkDisconnected($"心跳探活失败：{ret.Message}");
                }
            }
            catch (Exception ex)
            {
                // 心跳异常兜底（单次失败已在 ReadCore 内标记断开，这里防 Timer 回调把进程带崩）
                RaiseSafe(OnError, $"心跳异常: {ex.Message}", "OnError");
            }
        }

        /// <summary>轮询一周期：逐项读取并按需自愈重连（对齐 ModbusTcpMasterClient.PollTick）。</summary>
        private void PollTick(object state)
        {
            if (_disposed) return;
            try
            {
                lock (_syncRoot)
                {
                    if (!EnsureConnected()) return; // 未连接且未到节流：本周期跳过
                    foreach (var item in _config.PollItems)
                    {
                        if (_disposed) return;  // 已废弃：直接退出（不是 continue，别再碰任何项）
                        if (item == null) continue;
                        object content;
                        OperateResult ret = ReadCore(item.DataType, item.Address, item.Count, out content);
                        if (!ret.IsSuccess)
                        {
                            MarkDisconnected($"轮询 [{item.Name}] 失败：{ret.Message}");
                            break; // 本周期放弃后续项（已标记断开，下周期自愈后重来）
                        }
                        _lastPollData[item.Name] = content;
                        RaiseSafe(PollDataUpdated, new PlcPollEventArgs { Name = item.Name, Values = content }, "PollDataUpdated");
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseSafe(OnError, $"自动轮询异常: {ex.Message}", "OnError");
            }
        }

        // ═══════════════════ 通用读写 API（业务层直接调用） ═══════════════════
        // 地址一律填 PLC 品牌自己的语法字符串（三菱 "D100"、欧姆龙 "W10"、西门子 "DB1.DBW0"…）。
        // 全部方法：未连接/失败返回 false 或 null（内部已自动标记断开并等待心跳自愈重连）。
        //
        // 【线程红线】本组 API 是【同步阻塞】的：锁内等待网络往返，单次最坏耗时 ≈ TimeoutMs×2
        // （含失败自愈重连）。必须在业务后台线程调用——在 UI 线程直接调会卡界面（违反库铁律）。
        // 高频状态查询用 IsConnected / TryGetLastPollData（无锁读，微秒级，UI 线程可安全调）。

        /// <summary>读一个位。地址如 M10/X0/DB1.DBX0.0（按品牌语法）。</summary>
        public bool ReadBool(string address, out bool value)
        {
            var ret = GuardRead(() => _rw.ReadBool(address));
            value = ret.IsSuccess ? ret.Content : default(bool);
            return ret.IsSuccess;
        }

        /// <summary>读连续多个位。返回 null 表示未连接/通讯失败。</summary>
        public bool[] ReadBools(string address, ushort count)
        {
            var ret = GuardRead(() => _rw.ReadBool(address, count));
            return ret.IsSuccess ? ret.Content : null;
        }

        /// <summary>读一个有符号字（short）。</summary>
        public bool ReadInt16(string address, out short value)
        {
            var ret = GuardRead(() => _rw.ReadInt16(address));
            value = ret.IsSuccess ? ret.Content : default(short);
            return ret.IsSuccess;
        }

        /// <summary>读连续多个有符号字。返回 null 表示未连接/通讯失败。</summary>
        public short[] ReadInt16s(string address, ushort count)
        {
            var ret = GuardRead(() => _rw.ReadInt16(address, count));
            return ret.IsSuccess ? ret.Content : null;
        }

        /// <summary>读一个无符号字（ushort）。</summary>
        public bool ReadUInt16(string address, out ushort value)
        {
            var ret = GuardRead(() => _rw.ReadUInt16(address));
            value = ret.IsSuccess ? ret.Content : default(ushort);
            return ret.IsSuccess;
        }

        /// <summary>读连续多个无符号字。返回 null 表示未连接/通讯失败。</summary>
        public ushort[] ReadUInt16s(string address, ushort count)
        {
            var ret = GuardRead(() => _rw.ReadUInt16(address, count));
            return ret.IsSuccess ? ret.Content : null;
        }

        /// <summary>读一个双字整数（int，占 2 个寄存器）。</summary>
        public bool ReadInt32(string address, out int value)
        {
            var ret = GuardRead(() => _rw.ReadInt32(address));
            value = ret.IsSuccess ? ret.Content : default(int);
            return ret.IsSuccess;
        }

        /// <summary>读连续多个双字整数。返回 null 表示未连接/通讯失败。</summary>
        public int[] ReadInt32s(string address, ushort count)
        {
            var ret = GuardRead(() => _rw.ReadInt32(address, count));
            return ret.IsSuccess ? ret.Content : null;
        }

        /// <summary>读一个单精度浮点（占 2 个寄存器，字节序由各品牌协议自动处理）。</summary>
        public bool ReadFloat(string address, out float value)
        {
            var ret = GuardRead(() => _rw.ReadFloat(address));
            value = ret.IsSuccess ? ret.Content : default(float);
            return ret.IsSuccess;
        }

        /// <summary>读连续多个浮点。返回 null 表示未连接/通讯失败。</summary>
        public float[] ReadFloats(string address, ushort count)
        {
            var ret = GuardRead(() => _rw.ReadFloat(address, count));
            return ret.IsSuccess ? ret.Content : null;
        }

        /// <summary>写一个位。成功返回 true。</summary>
        public bool Write(string address, bool value) => GuardWrite(() => _rw.Write(address, value));

        /// <summary>写连续多位。成功返回 true。</summary>
        public bool Write(string address, bool[] values) => GuardWrite(() => _rw.Write(address, values));

        /// <summary>写一个有符号字。成功返回 true。</summary>
        public bool Write(string address, short value) => GuardWrite(() => _rw.Write(address, value));

        /// <summary>写连续多个有符号字。成功返回 true。</summary>
        public bool Write(string address, short[] values) => GuardWrite(() => _rw.Write(address, values));

        /// <summary>写一个无符号字。成功返回 true。</summary>
        public bool Write(string address, ushort value) => GuardWrite(() => _rw.Write(address, value));

        /// <summary>写连续多个无符号字。成功返回 true。</summary>
        public bool Write(string address, ushort[] values) => GuardWrite(() => _rw.Write(address, values));

        /// <summary>写一个双字整数。成功返回 true。</summary>
        public bool Write(string address, int value) => GuardWrite(() => _rw.Write(address, value));

        /// <summary>写连续多个双字整数。成功返回 true。</summary>
        public bool Write(string address, int[] values) => GuardWrite(() => _rw.Write(address, values));

        /// <summary>写一个单精度浮点。成功返回 true。</summary>
        public bool Write(string address, float value) => GuardWrite(() => _rw.Write(address, value));

        /// <summary>写连续多个浮点。成功返回 true。</summary>
        public bool Write(string address, float[] values) => GuardWrite(() => _rw.Write(address, values));

        /// <summary>读取最近一次轮询缓存（按轮询项名称）；未轮询过返回 false。免订阅事件即可取数。</summary>
        public bool TryGetLastPollData(string name, out object values)
        {
            return _lastPollData.TryGetValue(name, out values);
        }

        // ═══════════════════ 内部执行器 ═══════════════════

        /// <summary>读请求公共路径：锁内自愈连接 + 执行 + 成败状态维护。返回的 OperateResult 不为 null。</summary>
        private OperateResult<T> GuardRead<T>(Func<OperateResult<T>> action)
        {
            try
            {
                lock (_syncRoot)
                {
                    if (!EnsureConnected()) return new OperateResult<T> { Message = "未连接" };
                    var ret = action();
                    if (ret.IsSuccess) SetConnected(true);
                    else MarkDisconnected("通讯失败：" + ret.Message);
                    return ret;
                }
            }
            catch (Exception ex)
            {
                MarkDisconnected("通讯异常：" + ex.Message);
                return new OperateResult<T> { Message = ex.Message };
            }
        }

        /// <summary>写请求公共路径：语义同 GuardRead，成功返回 true。</summary>
        private bool GuardWrite(Func<OperateResult> action)
        {
            try
            {
                lock (_syncRoot)
                {
                    if (!EnsureConnected()) return false;
                    var ret = action();
                    if (ret.IsSuccess) SetConnected(true);
                    else MarkDisconnected("写入失败：" + ret.Message);
                    return ret.IsSuccess;
                }
            }
            catch (Exception ex)
            {
                MarkDisconnected("写入异常：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 按数据类型统一读取（心跳与轮询共用）：Bool/Int16/UInt16/Int32/Float 各走对应批量读法。
        /// 返回基础 OperateResult（IsSuccess 判定），content 带回原始类型数组（bool[]/short[]/…）。
        /// 必须在 _syncRoot 锁内调用。
        /// </summary>
        private OperateResult ReadCore(PlcDataType dataType, string address, ushort count, out object content)
        {
            switch (dataType)
            {
                case PlcDataType.Bool:
                {
                    var r = _rw.ReadBool(address, count); content = r.Content; return r;
                }
                case PlcDataType.UInt16:
                {
                    var r = _rw.ReadUInt16(address, count); content = r.Content; return r;
                }
                case PlcDataType.Int32:
                {
                    var r = _rw.ReadInt32(address, count); content = r.Content; return r;
                }
                case PlcDataType.Float:
                {
                    var r = _rw.ReadFloat(address, count); content = r.Content; return r;
                }
                default:
                    // 快速失败而非静默回退 Int16：未来 PlcDataType 追加新值时若漏改此处，
                    // 静默按 Int16 读会造成"数据错乱且无任何报错"的最难查事故——宁可当场暴露
                    content = null;
                    throw new NotSupportedException(
                        "未支持的数据类型：" + dataType + "（请在 ReadCore 补充对应读取分支）");
            }
        }

        // ═══════════════════ 工厂与工具 ═══════════════════

        /// <summary>
        /// 按品牌创建 HSL 客户端（唯一知道具体协议类的地方——新增品牌只需加一个 case，
        /// 其余读写/轮询/事件链路零改动）。
        /// 超时统一用配置 TimeoutMs（HSL 的 ConnectTimeOut 对不可达 IP 强制超时，不长时间卡线程）。
        /// </summary>
        private static DeviceTcpNet CreateDevice(MultiBrandPlcConfig cfg)
        {
            string ip = cfg.IpAddress?.Trim() ?? "";
            int port = EffectivePort(cfg);
            DeviceTcpNet dev;
            switch (cfg.Brand)
            {
                case MultiBrandPlcBrand.MelsecA1E:
                    dev = new MelsecA1ENet(ip, port);
                    break;

                case MultiBrandPlcBrand.MelsecMc:
                    dev = new MelsecMcNet(ip, port);
                    break;

                case MultiBrandPlcBrand.OmronFins:
                    // FINS/TCP 默认适配 CS/CJ/CP 系列；地址格式 D100（DM区）、C100（CIO区）、W10（WR区）
                    var fins = new OmronFinsNet(ip, port);
                    fins.PlcType = OmronPlcType.CSCJ;
                    dev = fins;
                    break;

                case MultiBrandPlcBrand.OmronCip:
                    // EtherNet/IP(CIP)：NJ/NX 系列，地址为 CIP 标签名；Slot 一般 0
                    var cip = new OmronCipNet(ip, port);
                    cip.Slot = cfg.RackSlot;
                    dev = cip;
                    break;

                case MultiBrandPlcBrand.InovanceH5U:
                    // 汇川 H5U：走 Modbus TCP 兼容口（502），地址 D 区/M 区
                    var ino = new InovanceTcpNet(ip, port);
                    ino.Series = InovanceSeries.H5U;
                    dev = ino;
                    break;

                case MultiBrandPlcBrand.SiemensS7:
                    // 西门子 S7（ISO-on-TCP）：型号决定 Rack/Slot 语义；S300/S400 现场常见 Slot=2
                    var s7 = new SiemensS7Net(ParseSiemensModel(cfg.SiemensModel), ip);
                    s7.Rack = cfg.RackSlot;
                    s7.Slot = cfg.SiemensSlot;
                    dev = s7;
                    break;

                default:
                    // 配置层已校验枚举合法，走到这里说明新增品牌后工厂漏配 case——快速失败暴露问题
                    throw new ArgumentOutOfRangeException(nameof(cfg.Brand),
                        $"未知的 PLC 品牌：{cfg.Brand}（请在 CreateDevice 工厂补充该品牌的客户端创建分支）");
            }

            dev.ConnectTimeOut = Math.Max(500, cfg.TimeoutMs);  // 连接强制超时（IP 错误不卡线程）
            dev.ReceiveTimeOut = Math.Max(500, cfg.TimeoutMs);  // 单次收包超时
            return dev;
        }

        /// <summary>解析西门子型号字符串（大小写不敏感）；非法值回退 S1200 并告警一次。</summary>
        private static SiemensPLCS ParseSiemensModel(string model)
        {
            SiemensPLCS plc;
            if (Enum.TryParse(model?.Trim(), true, out plc)) return plc;
            LogHelper.Warn($"多品牌 PLC：西门子型号 [{model}] 无法识别，回退 S1200");
            return SiemensPLCS.S1200;
        }

        /// <summary>端口生效值：配置填 0（默认）= 按品牌默认端口；填了具体值则尊重配置。</summary>
        private static int EffectivePort(MultiBrandPlcConfig cfg)
        {
            if (cfg.Port > 0) return cfg.Port;
            switch (cfg.Brand)
            {
                case MultiBrandPlcBrand.OmronFins: return 9600;
                case MultiBrandPlcBrand.OmronCip: return 44818;
                case MultiBrandPlcBrand.InovanceH5U: return 502;
                case MultiBrandPlcBrand.SiemensS7: return 102;
                default: return 6000; // 三菱 A1E / MC 默认端口
            }
        }

        /// <summary>品牌中文标签（日志/ActiveEndpoint 用）。</summary>
        internal static string BrandLabel(MultiBrandPlcBrand brand)
        {
            switch (brand)
            {
                case MultiBrandPlcBrand.MelsecA1E: return "三菱A1E";
                case MultiBrandPlcBrand.MelsecMc: return "三菱MC";
                case MultiBrandPlcBrand.OmronFins: return "欧姆龙FINS";
                case MultiBrandPlcBrand.OmronCip: return "欧姆龙CIP";
                case MultiBrandPlcBrand.InovanceH5U: return "汇川H5U";
                case MultiBrandPlcBrand.SiemensS7: return "西门子S7";
                default: return brand.ToString();
            }
        }

        // ═══════════════════ 状态边沿与清理 ═══════════════════

        /// <summary>更新连接状态（边沿检测：状态没变不发事件不打日志）。</summary>
        private void SetConnected(bool value)
        {
            if (_wasConnected == value) return;
            _wasConnected = value;
            _isConnected = value;
            if (!value)
                LogHelper.Warn($"多品牌 PLC{EndpointText()} 连接断开，将由心跳按节流自动重连");
            RaiseSafe(ConnectionChanged, value, "ConnectionChanged");
        }

        /// <summary>
        /// 链路故障统一处理（读写失败/心跳失败）：置未连接 + 释放坏 socket + 边沿事件 + OnError。
        /// 释放坏连接很重要：坏 socket 多挂几秒，下次读写会白等一个 ReceiveTimeOut。
        /// 必须在 _syncRoot 锁内调用。
        /// </summary>
        private void MarkDisconnected(string reason)
        {
            bool was = _wasConnected;
            CloseConnCore();
            SetConnected(false);
            // 边沿才发 OnError：持续失败期间只在"首次变红"时报一次原因，避免每秒刷错误回调
            if (was) RaiseSafe(OnError, $"多品牌 PLC 通讯失败，已标记断开：{reason}", "OnError");
        }

        /// <summary>锁内清理连接引用（幂等，不碰状态/事件）。</summary>
        private void CloseConnCore()
        {
            try { if (_conn != null) _conn.ConnectClose(); } catch { /* 拔网线场景吞掉 */ }
            _conn = null;
            _rw = null;
        }

        /// <summary>"未配置"时的空文本（日志拼接用）。</summary>
        private string EndpointText()
        {
            return _config == null ? "" : $"[{BrandLabel(_config.Brand)} {_config.IpAddress}]";
        }

        /// <summary>连接失败日志（30s 节流：重连期间静默，30s 至少留一条根因方向，防刷屏）。</summary>
        private void LogConnectFailure(string kind)
        {
            if ((DateTime.Now - _lastFailLogAt).TotalSeconds < 30) return;
            _lastFailLogAt = DateTime.Now;
            LogHelper.Warn($"多品牌 PLC{EndpointText()} {kind}（后台持续重连）");
        }

        /// <summary>释放资源：停双 Timer + 断开连接（热更/关窗必调）。重复调用安全。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopKeepAlive();
            StopPolling();
            Disconnect();
            LogHelper.Info("多品牌 PLC 客户端已释放");
        }
    }
}
