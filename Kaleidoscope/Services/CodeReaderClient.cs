// =====================================================================================
// 【下沉注记】本文件 2026-10-25 自 HJVision-WPF 生产工程（第二轮仪器设备下沉）
// 原样搬入，口径与第一轮 PlcNetCommQue/TcpChannel 下沉相同：
//   ①来源 GYZVision.Core/Devices/CodeReaderClient.cs（IDMVS 读码器 TCP 客户端）；
//   ②命名空间 → Kaleidoscope.Services（其余零改动，含 ReaderFrameParser）；
//   ③追加实现 ICodeReader 统一读码抽象（2026-10-25 与库内基恩士 SR 家族归一）：
//      ConnectionChanged(bool) 由四态 StateChanged 边沿转发、AcquireAsync 按参数分流
//      到既有 AcquireSingleAsync/AcquireContinuousAsync——既有公共面与行为一字未动。
//   其余算法/默认值/异常口径/注释全保留，公共 API 签名不变（主项目经
//   文件级 using 别名壳零改动引用，断点可跨工程打进本源码）。
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 读码器 TCP 客户端（老工程 SubClass/CodeReaderClient.cs，原样搬运，仅命名空间改变）。
    // -------------------------------------------------------------------------------------
    // IDMVS 单协议三命令：<Set,Acq,1> 开采集开灯 → start 触发读码 → <Set,Acq,0> 关采集关灯。
    // 回码原样返回不解析；接收换行切分 + 80ms 静默兜底；指令 SemaphoreSlim 串行；
    // 事件经创建时捕获的 SynchronizationContext 回调；AcquireSingleAsync 开→打→关一体，
    // finally 关灯。生产脚本经 ReaderServiceHub 调它。
    // =====================================================================================

    /// <summary>读码器客户端的连接状态。UI 可据此刷新灯的颜色。</summary>
    public enum ReaderState
    {
        /// <summary>初始未连接</summary>
        Disconnected,
        /// <summary>正在连接</summary>
        Connecting,
        /// <summary>已连接</summary>
        Connected,
        /// <summary>出错（连接失败 / 断线 / IO 异常）</summary>
        Error
    }

    public sealed class ReaderStateChangedEventArgs : EventArgs
    {
        public ReaderState State { get; set; }
        public string Message { get; set; }
    }

    public sealed class ReaderCodeReceivedEventArgs : EventArgs
    {
        /// <summary>二维码内容（原样回码；NoRead 时为 null）。</summary>
        public string Code { get; set; }
        /// <summary>产生该二维码的原始报文（整帧），便于排查格式。</summary>
        public string RawFrame { get; set; }
        /// <summary>是否为 NoRead（枪明确说没读到码）。</summary>
        public bool IsNoRead { get; set; }
        /// <summary>收到时间。</summary>
        public DateTime ReceivedAt { get; set; } = DateTime.Now;
    }

    public sealed class ReaderRawDataEventArgs : EventArgs
    {
        public string Frame { get; set; }
        public DateTime ReceivedAt { get; set; } = DateTime.Now;
    }

    public sealed class ReaderLogEventArgs : EventArgs
    {
        public string Message { get; set; }
        public DateTime At { get; set; } = DateTime.Now;
    }

    /// <summary>开关采集指令的执行结果。</summary>
    public sealed class ReaderCommandResult
    {
        /// <summary>是否成功（Success == true 表示拿到了含 OK 的回包）。</summary>
        public bool Success { get; set; }
        /// <summary>读码器返回的原始文本（例如 "&lt;Set,Acq,OK&gt;"）。</summary>
        public string Response { get; set; }
        /// <summary>失败原因。</summary>
        public string Error { get; set; }

        public static ReaderCommandResult Ok(string response) { return new ReaderCommandResult { Success = true, Response = response }; }
        public static ReaderCommandResult Fail(string error, string response = null) { return new ReaderCommandResult { Success = false, Error = error, Response = response }; }
    }

    /// <summary>start 触发一次读码的结果。</summary>
    public sealed class ReaderAcquireResult
    {
        public bool Success { get; set; }
        /// <summary>二维码内容（原样；NoRead/失败时为 null）。</summary>
        public string Code { get; set; }
        /// <summary>产生该二维码的原始报文。</summary>
        public string RawFrame { get; set; }
        /// <summary>是否为 NoRead（没读到码，业务上判 NG，不算通讯故障）。</summary>
        public bool IsNoRead { get; set; }
        /// <summary>失败/无码原因。</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// 读码器 TCP 客户端。一个实例对应一台 IDMVS 读码器。
    /// 线程安全：内部对共享状态加了锁；事件通过创建时捕获的 SynchronizationContext 回调。
    /// </summary>
    public sealed class CodeReaderClient : IDisposable, ICodeReader
    {
        /// <summary>开采集 + 开灯（扫码前发）。</summary>
        public const string CmdOpenAcq = "<Set,Acq,1>";
        /// <summary>关采集 + 关灯（平时保持，扫码结束必须发）。</summary>
        public const string CmdCloseAcq = "<Set,Acq,0>";
        /// <summary>触发读一次码（开采集后发）。</summary>
        public const string CmdTrigger = "start";
        /// <summary>查采集态（回 &lt;Get,Acq,0/1/2&gt;，0=停止/关灯，1=开始/开灯；errno=枪报错）。</summary>
        public const string CmdGetAcq = "<Get,Acq>";
        /// <summary>枪回的"没读到码"标志（大小写不敏感）。</summary>
        public const string NoReadMark = "NoRead";
        /// <summary>开关采集成功标志：回包里含它即成功（如 &lt;Set,Acq,OK&gt;）。</summary>
        public const string AcqOkMark = "OK";
        /// <summary>静默成帧间隔（毫秒）：超过它没新字节即判定裸回包一帧结束。</summary>
        public const int SilenceGapMs = 80;
        /// <summary>
        /// 持续触发模式相邻两次 start 的垫隔（毫秒）：NoRead 一回来立刻再打容易打爆枪，
        /// 垫 50ms 给曝光/解码喘口气；真实触发节奏仍由枪回 NoRead 的快慢决定（这 50ms 只是垫底）。
        /// </summary>
        public const int ContinuousGapMs = 50;
        /// <summary>持续触发时长下限（毫秒）：再短连"开采集+一次start+关采集"都跑不完。</summary>
        public const int ContinuousMsMin = 500;
        /// <summary>持续触发时长上限（毫秒）：误配大数也不能把节拍拖死（与 ReaderConfig 钳制同源）。</summary>
        public const int ContinuousMsMax = 60000;
        /// <summary>
        /// 持续模式的"最低一轮"预算（毫秒）：剩余预算不够一轮 start+垫隔（NoRead 快回约
        /// 50~200ms/轮）就按当前结论收尾。不能沿用单次模式的 800——那会把 1~2 秒的短时长
        /// 配置直接掐成一枪不打（开采集+关采集预留吃掉全部预算）。
        /// </summary>
        public const int ContinuousMinRoundMs = 150;

        private readonly string _name;
        private readonly string _ip;
        private readonly int _port;
        private readonly int _connectTimeoutMs;
        private readonly int _commandTimeoutMs;

        private readonly SynchronizationContext _syncContext;

        private TcpClient _tcp;
        private NetworkStream _stream;
        private CancellationTokenSource _receiveCts;
        private Task _receiveLoopTask;

        private volatile ReaderState _state = ReaderState.Disconnected;
        public ReaderState State { get { return _state; } }
        public bool IsConnected { get { return _state == ReaderState.Connected; } }
        public string Name { get { return _name; } }
        public string Ip { get { return _ip; } }
        public int Port { get { return _port; } }

        private readonly SemaphoreSlim _cmdLock = new SemaphoreSlim(1, 1);
        private TaskCompletionSource<string> _pendingResponse;
        private readonly object _pendingLock = new object();

        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private volatile bool _manualDisconnect;

        /// <summary>断线后是否自动重连（默认关闭，主程序按需打开）。开启后按指数退避尝试重连。</summary>
        public bool AutoReconnect { get; set; }

        // ---------------- 持续触发模式（V4.4.93，主窗按每枪 [Reader_ParameterN] 配置灌入） ----------------
        // 【是什么】单次模式=发一条 start 等一条回包（重读有限次）；持续模式=在配置时长内
        // 循环发 start（NoRead 回来垫个短间隔立刻再打），读到码立即停止并按成功返回，
        // 时长耗尽自动结束——关采集都在 finally 里，两种模式的灯都绝不常亮。
        // 【三个属性谁灌】建连（启动装配 + ReloadReaders 热刷）统一从该枪的配置行灌，
        // 来源是料号 ini [Reader_ParameterN] 的每枪三键（配置页表格 3 列）；缺省全关=单次
        // 扫码原行为（Hub 分流见 ReaderServiceHub.AcquireSingle）。
        // 【钳制只做两层】ini 解析（ReaderConfig.ParseContinuousMs，缺省/坏值/越界一次钳齐）
        // + AcquireContinuousAsync 入口（本类可直接被测试页等调用）；setter 不再兜底——
        // 三层钳制是冗余包袱，谁灌进非法值谁自己负责（Hub/测试页取值时也有仲裁兜底）。

        /// <summary>持续触发开关（true 时 Hub 的 AcquireSingle 走持续模式；缺省 false 走原单次逻辑）。</summary>
        public bool ContinuousTrigger { get; set; }

        private int _continuousTriggerMs = 3000;
        /// <summary>持续触发时长 ms（缺省 3000；钳制在 ini 解析层与 AcquireContinuousAsync 入口做，setter 直赋不兜底）。</summary>
        public int ContinuousTriggerMs
        {
            get { return _continuousTriggerMs; }
            set { _continuousTriggerMs = value; }
        }

        /// <summary>
        /// 读到码即停（true=读到第一笔码立即结束返回；false=读到后继续触发到超时，取最后读到的码。
        /// 关掉的用途：观察枪的连续读码稳定性/防错统计，生产缺省开）。
        /// </summary>
        public bool StopOnFirstCode { get; set; }

        private volatile bool _reconnectRunning;

        private long _lastByteTicksUtc;

        public event EventHandler<ReaderStateChangedEventArgs> StateChanged;
        public event EventHandler<ReaderCodeReceivedEventArgs> CodeReceived;
        public event EventHandler<ReaderRawDataEventArgs> RawFrameReceived;
        public event EventHandler<ReaderLogEventArgs> Log;

        // ---- ICodeReader 统一抽象（2026-10-25 第二轮下沉新增；既有公共面一行未动）----
        // 连接边沿（bool 版，边沿去重）：由 SetState 从四态 StateChanged 归一转发，
        // 供只关心通/断的上层（ICodeReader 消费方）订阅；既有四态 StateChanged 照旧。
        public event EventHandler<bool> ConnectionChanged;
        private bool _lastConnForEvent;

        /// <summary>创建一个读码器客户端实例（事件经 syncContext 回调，null 则直接回调）。</summary>
        public CodeReaderClient(string name, string ip, int port,
            int connectTimeoutMs, int commandTimeoutMs,
            SynchronizationContext syncContext)
        {
            _name = name ?? (ip + ":" + port);
            _ip = ip;
            _port = port;
            _connectTimeoutMs = connectTimeoutMs > 0 ? connectTimeoutMs : 3000;
            _commandTimeoutMs = commandTimeoutMs > 0 ? commandTimeoutMs : 5000;
            _syncContext = syncContext ?? SynchronizationContext.Current;
            // 持续触发缺省读到即停（V4.4.93；bool 默认 false 与设计相反，构造显式赋。
            // 未开持续触发的枪 ContinuousTrigger=false，此值无任何副作用）。
            StopOnFirstCode = true;
        }

        /// <summary>建立 TCP 连接（带超时）。已连接则直接返回。</summary>
        public async Task ConnectAsync(CancellationToken ct)
        {
            await _connectLock.WaitAsync(ct);
            try
            {
                if (IsConnected) return;

                _manualDisconnect = false;
                SetState(ReaderState.Connecting, "正在连接 " + _ip + ":" + _port + " ...");

                DisposeConnection();

                TcpClient tcp = new TcpClient();
                try { tcp.Client.IOControl(IOControlCode.KeepAliveValues, BuildKeepAlive(3000, 1000), null); }
                catch { }

                try
                {
                    Task connectTask = tcp.ConnectAsync(_ip, _port);
                    Task winner = await Task.WhenAny(connectTask, Task.Delay(_connectTimeoutMs, ct));
                    if (winner != connectTask)
                    {
                        SafeClose(tcp);
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        throw new TimeoutException("连接 " + _ip + ":" + _port + " 超时（" + _connectTimeoutMs + "ms）");
                    }
                    await connectTask;

                    _tcp = tcp;
                    _stream = tcp.GetStream();
                    _receiveCts = new CancellationTokenSource();
                    CancellationToken token = _receiveCts.Token;
                    _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(token));

                    SetState(ReaderState.Connected, "已连接 " + _ip + ":" + _port);
                }
                catch (Exception ex)
                {
                    SafeClose(tcp);
                    SetState(ReaderState.Error, "连接失败：" + ex.Message);
                    EnsureReconnectLoop();
                    throw;
                }
            }
            finally
            {
                _connectLock.Release();
            }
        }

        /// <summary>建立 TCP 连接（带超时）。已连接则直接返回。</summary>
        public Task ConnectAsync()
        {
            return ConnectAsync(default(CancellationToken));
        }

        /// <summary>主动断开（不会触发自动重连）。</summary>
        public Task DisconnectAsync()
        {
            _manualDisconnect = true;
            DisposeConnection();
            SetState(ReaderState.Disconnected, "已主动断开");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 判断是否为"没读到码"类回包（纯函数）。只认空与 NoRead（大小写不敏感），
        /// 绝不按 ng/fail/error 等子串误判（真码里出现这些字母就全判 NG 了）。
        /// </summary>
        public static bool IsNoRead(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return raw.Trim().Equals(NoReadMark, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 开关采集（扫码前发 1 开灯，扫码完发 0 关灯）。
        /// 成功条件：收到回包且含 OK（大小写不敏感）。
        /// </summary>
        public async Task<ReaderCommandResult> SetAcqAsync(int onOff, CancellationToken ct)
        {
            if (onOff != 0 && onOff != 1)
                throw new ArgumentOutOfRangeException("onOff", "只能是 0 或 1");

            string cmd = onOff == 1 ? CmdOpenAcq : CmdCloseAcq;
            try
            {
                string resp = await SendRawAndWaitAsync(cmd, _commandTimeoutMs, ct);
                if (!string.IsNullOrEmpty(resp)
                    && resp.IndexOf(AcqOkMark, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ReaderCommandResult.Ok(resp);
                }
                return ReaderCommandResult.Fail(
                    string.IsNullOrEmpty(resp) ? "无回包" : ("回包无OK标志：" + resp),
                    resp);
            }
            catch (Exception ex)
            {
                return ReaderCommandResult.Fail(ex.Message);
            }
        }

        /// <summary>开关采集（扫码前发 1 开灯，扫码完发 0 关灯）。</summary>
        public Task<ReaderCommandResult> SetAcqAsync(int onOff)
        {
            return SetAcqAsync(onOff, default(CancellationToken));
        }

        /// <summary>
        /// 查采集态（发 &lt;Get,Acq&gt;）：0=停止/关灯，1=开始/开灯，2=其它态；
        /// 查不到/枪报错回 null。不抛异常。
        /// </summary>
        public async Task<int?> GetAcqAsync(CancellationToken ct)
        {
            try
            {
                string resp = await SendRawAndWaitAsync(CmdGetAcq, 3000, ct);
                return ParseAcqState(resp);
            }
            catch { return null; }
        }

        /// <summary>查采集态：0=停止/关灯，1=开始/开灯；未知回 null。</summary>
        public Task<int?> GetAcqAsync()
        {
            return GetAcqAsync(default(CancellationToken));
        }

        /// <summary>
        /// 从 &lt;Get,Acq,N&gt; 回包里提 state（纯函数）。只认 Acq, 后首个数字；
        /// errno/杂串/空回 null，绝不猜。
        /// </summary>
        public static int? ParseAcqState(string raw)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return null;
                int k = raw.IndexOf("Acq,", StringComparison.OrdinalIgnoreCase);
                if (k < 0) return null;
                for (int i = k + 4; i < raw.Length; i++)
                {
                    char c = raw[i];
                    if (c >= '0' && c <= '9') return (int)(c - '0');
                    if (c == ' ' || c == '\t') continue;
                    return null;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 发 start 触发一次读码并等回码（调用前枪必须已开采集）。
        /// 回 NoRead → Success=false + IsNoRead=true（业务判 NG）；回码串原样返回。
        /// </summary>
        public async Task<ReaderAcquireResult> StartAcquireAsync(CancellationToken ct)
        {
            return await StartAcquireAsync(_commandTimeoutMs, ct);
        }

        /// <summary>发 start 触发一次读码，并等回码（调用前枪必须已开采集）。</summary>
        public Task<ReaderAcquireResult> StartAcquireAsync()
        {
            return StartAcquireAsync(default(CancellationToken));
        }

        private async Task<ReaderAcquireResult> StartAcquireAsync(int waitMs, CancellationToken ct)
        {
            string raw;
            try
            {
                raw = await SendRawAndWaitAsync(CmdTrigger, waitMs, ct);
            }
            catch (Exception ex)
            {
                return new ReaderAcquireResult { Success = false, Error = ex.Message };
            }
            if (string.IsNullOrEmpty(raw))
                return new ReaderAcquireResult { Success = false, Error = "未收到读码结果" };

            string code = raw.Trim();
            bool noRead = IsNoRead(code);

            RaiseCodeReceived(noRead ? null : code, raw, noRead);

            return new ReaderAcquireResult
            {
                Success = !noRead,
                Code = noRead ? null : code,
                RawFrame = raw,
                IsNoRead = noRead,
                Error = noRead ? "NoRead（未读到码）" : null
            };
        }

        /// <summary>单次扫码默认最多触发次数（含首次重读）。</summary>
        public const int DefaultMaxAttempts = 3;
        /// <summary>重读间隔默认毫秒。</summary>
        public const int DefaultRetryDelayMs = 300;
        /// <summary>触发次数上限钳制（防误配拖死节拍）。</summary>
        public const int MaxAttemptsCap = 5;

        /// <summary>
        /// 一次完整扫码：开采集 → start 等码（失败按惯例重读）→ 关采集（finally 关灯）。
        /// timeoutMs 是三步总预算（建议≥8000）；开采集失败不重试（快速 NG 转人工）；
        /// start 无码最多重读到 3 次、间隔 300ms；中途掉线立即停。
        /// </summary>
        public Task<ReaderAcquireResult> AcquireSingleAsync(int timeoutMs, CancellationToken ct)
        {
            return AcquireSingleAsync(timeoutMs, DefaultMaxAttempts, DefaultRetryDelayMs, ct);
        }

        /// <summary>一次完整扫码（触发次数与间隔可配，超上限钳制）。</summary>
        public async Task<ReaderAcquireResult> AcquireSingleAsync(int timeoutMs,
            int maxAttempts, int retryDelayMs, CancellationToken ct)
        {
            if (timeoutMs <= 0) timeoutMs = 8000;
            if (maxAttempts < 1) maxAttempts = 1;
            if (maxAttempts > MaxAttemptsCap) maxAttempts = MaxAttemptsCap;
            if (retryDelayMs < 0) retryDelayMs = 0;
            if (retryDelayMs > 2000) retryDelayMs = 2000;
            int openBudget = Math.Min(2500, Math.Max(1000, timeoutMs / 4));
            int closeBudget = 800;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                ReaderCommandResult open;
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(openBudget);
                    try { open = await SetAcqAsync(1, cts.Token); }
                    catch (OperationCanceledException) { open = ReaderCommandResult.Fail("开采集超时"); }
                }
                if (open == null || !open.Success)
                    return new ReaderAcquireResult
                    {
                        Success = false,
                        Error = "开采集失败(" + (open == null ? "无结果" : open.Error) + ")"
                    };
                ReaderAcquireResult last = null;
                int used = 0;
                for (int att = 1; att <= maxAttempts; att++)
                {
                    if (!IsConnected)
                        return new ReaderAcquireResult { Success = false, Error = "读码中掉线" };
                    int remain = timeoutMs - (int)sw.ElapsedMilliseconds - closeBudget;
                    if (remain < 800)
                    {
                        if (last != null) return last;
                        return new ReaderAcquireResult { Success = false, Error = "读码超时（预算耗尽）" };
                    }
                    used = att;
                    last = await StartAcquireAsync(Math.Min(remain, _commandTimeoutMs), ct);
                    if (last != null && last.Success) return last;
                    if (att < maxAttempts && IsConnected)
                    {
                        string why = last == null ? "无结果" : (last.IsNoRead ? "NoRead" : last.Error);
                        EmitLog("第" + att + "次未读到(" + why + ")，" + retryDelayMs + "ms后第" + (att + 1) + "次重读");
                        try { await Task.Delay(retryDelayMs, ct); }
                        catch { break; }
                    }
                }
                if (last != null && last.IsNoRead)
                    last.Error = "NoRead（未读到码，共触发" + used + "次）";
                return last ?? new ReaderAcquireResult { Success = false, Error = "未读到码" };
            }
            finally
            {
                try
                {
                    using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cts.CancelAfter(closeBudget);
                        try { await SetAcqAsync(0, cts.Token); }
                        catch (Exception ex) { EmitLog("关采集异常：" + ex.Message); }
                    }
                }
                catch (Exception ex) { EmitLog("关采集异常：" + ex.Message); }
            }
        }

        /// <summary>一次完整扫码：开采集 → start 等码（惯例重读）→ 关采集（finally 关灯）。</summary>
        public Task<ReaderAcquireResult> AcquireSingleAsync(int timeoutMs)
        {
            return AcquireSingleAsync(timeoutMs, default(CancellationToken));
        }

        /// <summary>
        /// 持续触发扫码（V4.4.93，时长内循环发 start）：开采集 → 循环{start→读到即停或垫隔再打}
        /// → 结束方式三选一：①读到码（即停或预算耗尽取最后码）→ 成功返回；②预算耗尽没读到
        /// → NoRead 失败；③中途掉线 → 立即失败。关采集在 finally 里发：三种结束方式灯都必灭
        /// （超时自动关闭）。与 AcquireSingleAsync 完全独立（原方法一行未动，探针锁死的重读
        /// 惯例不受影响）。返回语义与单次模式一致：NoRead/超时 Success=false（调用方判 NG），
        /// 不抛异常。
        /// </summary>
        public async Task<ReaderAcquireResult> AcquireContinuousAsync(int durationMs, bool stopOnFirstCode, CancellationToken ct)
        {
            if (durationMs <= 0) durationMs = 3000;
            if (durationMs < ContinuousMsMin) durationMs = ContinuousMsMin;
            if (durationMs > ContinuousMsMax) durationMs = ContinuousMsMax;
            int openBudget = Math.Min(2500, Math.Max(1000, durationMs / 4));
            int closeBudget = 800;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 开采集：失败不重试（枪/链路异常，重试烧节拍无意义，快速失败转 NG；
                // 掉线自愈由 AutoReconnect 负责）——与单次模式同惯例。
                ReaderCommandResult open;
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(openBudget);
                    try { open = await SetAcqAsync(1, cts.Token); }
                    catch (OperationCanceledException) { open = ReaderCommandResult.Fail("开采集超时"); }
                }
                if (open == null || !open.Success)
                    return new ReaderAcquireResult
                    {
                        Success = false,
                        Error = "开采集失败(" + (open == null ? "无结果" : open.Error) + ")"
                    };

                ReaderAcquireResult last = null;      // 最后一笔结果（NoRead/超时提示文案用）
                ReaderAcquireResult lastCode = null;  // 最后成功码（不即停模式收尾返回它——
                                                      // 后续 NoRead 不许冲掉已读到的码）
                int used = 0;                      // 实际触发次数（进 Error 提示，现场对枪调参用）
                while (true)
                {
                    if (!IsConnected)
                    {
                        // 读码中掉线：读到过有效码仍按成功回（码是真的），否则按失败
                        if (lastCode != null) return lastCode;
                        return new ReaderAcquireResult { Success = false, Error = "读码中掉线（已触发" + used + "次）" };
                    }
                    // 总预算封顶：不够再打一轮（start+垫隔的最低耗时）就按当前结论结束。
                    // 这里不预留关采集的 800ms——关采集在 finally 里独立发（顶多让本轮
                    // 超出总预算 800ms），预留会把 500~1500ms 的短时长配置直接掐成一枪不打。
                    int remain = durationMs - (int)sw.ElapsedMilliseconds;
                    if (remain < ContinuousMinRoundMs) break;

                    used++;
                    last = await StartAcquireAsync(Math.Min(remain, _commandTimeoutMs), ct);
                    if (last != null && last.Success)
                    {
                        lastCode = last;
                        if (stopOnFirstCode)
                            return last;   // 读到即停：立即返回，finally 里关采集关灯
                    }

                    // 还要继续（NoRead/单次超时/读到码但配置了不即停）：垫个短间隔再打。
                    // ct 被取消就按当前结论收尾，不死等。
                    try { await Task.Delay(ContinuousGapMs, ct); }
                    catch (OperationCanceledException) { break; }
                }

                if (lastCode != null)
                    return lastCode;   // 不即停模式：预算耗尽，按最后读到的码成功返回
                if (last != null && last.IsNoRead)
                    last.Error = "NoRead（持续" + durationMs + "ms内未读到码，共触发" + used + "次）";
                return last ?? new ReaderAcquireResult { Success = false, Error = "未读到码（预算耗尽）" };
            }
            finally
            {
                // 灯必须灭（超时自动关闭的落点）：关采集悄悄发，不等太久、不记失败
                // （读码结论已定，关失败只记一条日志，下次扫码开采集会重新同步状态）。
                try
                {
                    using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cts.CancelAfter(closeBudget);
                        try { await SetAcqAsync(0, cts.Token); }
                        catch (Exception ex) { EmitLog("关采集异常：" + ex.Message); }
                    }
                }
                catch (Exception ex) { EmitLog("关采集异常：" + ex.Message); }
            }
        }

        /// <summary>持续触发扫码（不带取消令牌的便捷重载，见三参重载注释）。</summary>
        public Task<ReaderAcquireResult> AcquireContinuousAsync(int durationMs, bool stopOnFirstCode)
        {
            return AcquireContinuousAsync(durationMs, stopOnFirstCode, default(CancellationToken));
        }

        /// <summary>
        /// ICodeReader 统一入口：按请求参数分流到既有单次/持续两模式
        /// （实现只是薄包装，重读/钳制/关灯语义全部沿用既有方法，一行业务不改）。
        /// </summary>
        public Task<ReaderAcquireResult> AcquireAsync(CodeReadRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (request.Continuous)
                return AcquireContinuousAsync(request.ContinuousMs, request.StopOnFirstCode, ct);
            int timeout = request.TimeoutMs > 0 ? request.TimeoutMs : 8000;
            int attempts = request.MaxAttempts > 0 ? request.MaxAttempts : DefaultMaxAttempts;
            int delay = request.RetryDelayMs >= 0 ? request.RetryDelayMs : DefaultRetryDelayMs;
            return AcquireSingleAsync(timeout, attempts, delay, ct);
        }

        /// <summary>
        /// 单步发一条命令并等回包原文（测试页单步调试用）。超时/未连接抛异常。
        /// 注意：单发 start 不代关灯，调完记得手发关采集。
        /// </summary>
        public async Task<string> SendSingleAsync(string command, int waitMs, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(command))
                throw new ArgumentNullException("command");
            return await SendRawAndWaitAsync(command.Trim(), waitMs, ct);
        }

        /// <summary>单步发一条命令并等回包原文（测试页单步调试用）。</summary>
        public Task<string> SendSingleAsync(string command, int waitMs)
        {
            return SendSingleAsync(command, waitMs, default(CancellationToken));
        }

        /// <summary>裸发一条指令并等下一帧回包（三个 IDMVS 命令共用；指令串行，超时抛异常）。</summary>
        private async Task<string> SendRawAndWaitAsync(string command, int waitMs, CancellationToken ct)
        {
            if (!IsConnected)
                throw new InvalidOperationException("尚未连接，请先 ConnectAsync。");
            if (string.IsNullOrEmpty(command))
                throw new ArgumentNullException("command");
            if (waitMs <= 0) waitMs = _commandTimeoutMs;

            await _cmdLock.WaitAsync(ct);
            TaskCompletionSource<string> tcs = null;
            try
            {
                tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_pendingLock) { _pendingResponse = tcs; }

                using (CancellationTokenSource timeoutCts = new CancellationTokenSource(waitMs))
                using (timeoutCts.Token.Register(() =>
                {
                    lock (_pendingLock)
                    {
                        if (_pendingResponse == tcs)
                        {
                            _pendingResponse = null;
                            tcs.TrySetException(new TimeoutException("等待回包超时（指令：" + command + "，" + waitMs + "ms）"));
                        }
                    }
                }))
                using (ct.Register(() =>
                {
                    lock (_pendingLock)
                    {
                        if (_pendingResponse == tcs)
                        {
                            _pendingResponse = null;
                            tcs.TrySetCanceled(ct);
                        }
                    }
                }))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(command);
                    await _stream.WriteAsync(bytes, 0, bytes.Length, ct);
                    await _stream.FlushAsync(ct);
                    EmitLog("→ 发送：" + command);

                    string response = await tcs.Task;
                    EmitLog("← 收到：" + response);
                    return response;
                }
            }
            finally
            {
                lock (_pendingLock) { _pendingResponse = null; }
                _cmdLock.Release();
            }
        }

        private readonly object _bufferLock = new object();
        private readonly StringBuilder _rxBuffer = new StringBuilder();

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            StringBuilder sb = _rxBuffer;
            Decoder decoder = Encoding.UTF8.GetDecoder();
            byte[] buf = new byte[4096];
            char[] chars = new char[4096];

            Timer silenceTimer = new Timer(delegate { SilenceFlush(); }, null, SilenceGapMs, SilenceGapMs);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = await _stream.ReadAsync(buf, 0, buf.Length, ct);
                    if (n == 0)
                    {
                        throw new SocketException((int)SocketError.ConnectionReset);
                    }

                    int charCount = decoder.GetChars(buf, 0, n, chars, 0, false);

                    lock (_bufferLock)
                    {
                        sb.Append(chars, 0, charCount);
                        _lastByteTicksUtc = DateTime.UtcNow.Ticks;

                        string leftover;
                        List<string> parsed = ReaderFrameParser.Extract(sb.ToString(), out leftover);
                        if (parsed.Count > 0)
                        {
                            sb.Length = 0;
                            sb.Append(leftover);
                            foreach (string f in parsed) HandleFrame(f);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                EmitLog("接收循环异常：" + ex.Message);
            }
            finally
            {
                if (silenceTimer != null) silenceTimer.Dispose();
                lock (_bufferLock)
                {
                    if (sb.Length > 0) HandleFrame(sb.ToString());
                    sb.Length = 0;
                }
                OnConnectionLost("连接已断开");
            }
        }

        private void SilenceFlush()
        {
            try
            {
                lock (_bufferLock)
                {
                    StringBuilder sb = _rxBuffer;
                    if (sb.Length == 0) return;
                    long last = _lastByteTicksUtc;
                    if (last == 0) return;
                    double elapsedMs = (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.TicksPerMillisecond;
                    if (elapsedMs < SilenceGapMs) return;
                    string frame = sb.ToString();
                    sb.Length = 0;
                    if (!string.IsNullOrEmpty(frame))
                        HandleFrame(frame.TrimEnd('\r', '\n'));
                }
            }
            catch { }
        }

        /// <summary>处理一帧完整报文：先回填当前挂起的指令回包，否则当作自发码上报。</summary>
        private void HandleFrame(string frame)
        {
            RaiseRaw(frame);

            TaskCompletionSource<string> waiter;
            lock (_pendingLock) { waiter = _pendingResponse; _pendingResponse = null; }

            if (waiter != null)
            {
                waiter.TrySetResult(frame);
                return;
            }

            // 无等待者时的自发帧：开关采集/查态的迟到应答直接丢弃（否则进生产缓存误判读到码）。
            try
            {
                if (!string.IsNullOrEmpty(frame)
                    && (frame.IndexOf("Set,Acq,", StringComparison.OrdinalIgnoreCase) >= 0
                        || frame.IndexOf("Get,Acq,", StringComparison.OrdinalIgnoreCase) >= 0))
                    return;
            }
            catch { }

            string code = frame == null ? null : frame.Trim();
            bool noRead = IsNoRead(code);
            RaiseCodeReceived(noRead ? null : code, frame, noRead);
        }

        private void OnConnectionLost(string reason)
        {
            lock (_pendingLock)
            {
                TaskCompletionSource<string> w = _pendingResponse;
                _pendingResponse = null;
                if (w != null) w.TrySetException(new InvalidOperationException("连接已断开：" + reason));
            }

            DisposeConnection();
            SetState(ReaderState.Disconnected, reason);

            EnsureReconnectLoop();
        }

        private void EnsureReconnectLoop()
        {
            try
            {
                if (!AutoReconnect || _manualDisconnect || _disposed) return;
                if (_reconnectRunning) return;
                _reconnectRunning = true;
                Task.Run(async delegate
                {
                    try { await ReconnectLoopAsync(); }
                    catch { }
                    finally { _reconnectRunning = false; }
                });
            }
            catch { try { _reconnectRunning = false; } catch { } }
        }

        private async Task ReconnectLoopAsync()
        {
            int backoff = 2000;
            while (AutoReconnect && !_disposed && !IsConnected)
            {
                try
                {
                    EmitLog("自动重连中...（下次间隔 " + backoff + "ms）");
                    await ConnectAsync();
                    return;
                }
                catch
                {
                    await Task.Delay(backoff);
                    backoff = Math.Min(backoff * 2, 30000);
                }
            }
        }

        private bool IsSocketAlive()
        {
            try
            {
                Socket sock = _tcp != null ? _tcp.Client : null;
                if (sock == null || !_tcp.Connected) return false;
                bool poll = sock.Poll(1, SelectMode.SelectRead);
                bool avail = sock.Available == 0;
                if (poll && avail) return false;
                return true;
            }
            catch { return false; }
        }

        private static byte[] BuildKeepAlive(int onOffMs, int intervalMs)
        {
            byte[] b = new byte[12];
            BitConverter.GetBytes(1u).CopyTo(b, 0);
            BitConverter.GetBytes((uint)onOffMs).CopyTo(b, 4);
            BitConverter.GetBytes((uint)intervalMs).CopyTo(b, 8);
            return b;
        }

        private void DisposeConnection()
        {
            try { if (_receiveCts != null) _receiveCts.Cancel(); } catch { }
            _receiveCts = null;
            try { if (_stream != null) _stream.Close(); } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
            _stream = null;
            _tcp = null;
        }

        private static void SafeClose(TcpClient c)
        {
            try { if (c != null) c.Close(); } catch { }
        }

        private void SetState(ReaderState newState, string message)
        {
            _state = newState;
            RaiseStateChanged(newState, message);
            RaiseConnectionEdge(newState == ReaderState.Connected);
        }

        /// <summary>ICodeReader 连接边沿转发（bool 翻转才发，工作线程 Post 回同步上下文）。</summary>
        private void RaiseConnectionEdge(bool connected)
        {
            if (connected == _lastConnForEvent) return;
            _lastConnForEvent = connected;
            EventHandler<bool> h = ConnectionChanged;
            if (h == null) return;
            Post(delegate { try { h(this, connected); } catch { } });
        }

        private void Post(Action<object> cb)
        {
            if (_syncContext != null) _syncContext.Post(delegate (object s) { cb(s); }, null);
            else cb(null);
        }

        private void RaiseStateChanged(ReaderState state, string msg)
        {
            Post(delegate
            {
                if (StateChanged != null)
                    StateChanged(this, new ReaderStateChangedEventArgs { State = state, Message = msg });
            });
        }

        private void RaiseCodeReceived(string code, string raw, bool noRead)
        {
            Post(delegate
            {
                if (CodeReceived != null)
                    CodeReceived(this, new ReaderCodeReceivedEventArgs
                    {
                        Code = code,
                        RawFrame = raw,
                        IsNoRead = noRead
                    });
            });
        }

        private void RaiseRaw(string frame)
        {
            Post(delegate
            {
                if (RawFrameReceived != null)
                    RawFrameReceived(this, new ReaderRawDataEventArgs { Frame = frame });
            });
        }

        private void EmitLog(string message)
        {
            EventHandler<ReaderLogEventArgs> h = Log;
            if (h == null) return;
            Post(delegate { h(this, new ReaderLogEventArgs { Message = message }); });
        }

        private volatile bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _manualDisconnect = true;
            AutoReconnect = false;
            DisposeConnection();
            SetState(ReaderState.Disconnected, "已释放");
            if (_cmdLock != null) _cmdLock.Dispose();
            if (_connectLock != null) _connectLock.Dispose();
        }
    }

    // =====================================================================================
    // ReaderFrameParser：TCP 字节流按换行切帧（纯函数，零连接，供测试直测）。
    // 不带换行的裸回包不走这里，由接收循环的静默计时器成帧。
    // =====================================================================================
    public static class ReaderFrameParser
    {
        /// <summary>
        /// 按回车换行切帧：完整行进 frames（去首尾空白、跳空行），
        /// 不完整尾巴原样放 leftover（调用方拼回缓冲等下批字节）。
        /// </summary>
        public static List<string> Extract(string buffer, out string leftover)
        {
            List<string> frames = new List<string>();
            leftover = buffer == null ? "" : buffer;
            if (string.IsNullOrEmpty(buffer)) return frames;

            StringBuilder sb = new StringBuilder(buffer);
            while (sb.Length > 0)
            {
                int nl = IndexOfAny(sb, '\r', '\n');
                if (nl < 0) break;
                string line = sb.ToString(0, nl);
                if (!string.IsNullOrWhiteSpace(line)) frames.Add(line.Trim());
                int eat = nl;
                while (eat < sb.Length && (sb[eat] == '\r' || sb[eat] == '\n')) eat++;
                sb.Remove(0, eat);
            }

            leftover = sb.ToString();
            return frames;
        }

        private static int IndexOfAny(StringBuilder sb, char a, char b)
        {
            for (int i = 0; i < sb.Length; i++) if (sb[i] == a || sb[i] == b) return i;
            return -1;
        }
    }
}
