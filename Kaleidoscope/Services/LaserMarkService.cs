// =====================================================================================
// 【下沉注记】本文件 2026-10-25 自 HJVision-WPF 生产工程（第二轮仪器设备下沉）
// 原样搬入，口径与第一轮 PlcNetCommQue/TcpChannel 下沉相同：
//   ①来源 GYZVision.Core/Devices/LaserMarkService.cs（激光打码机通讯服务）；
//   ②命名空间 → Kaleidoscope.Services；
//   ③演示模式出口 MockMode.IsEnabled → MockBridge.IsEnabled（2 处，宿主静态构造接线）；
//   ④连带件同轮下沉：MarkContentHub（含 MarkContentSource/MarkEmptyPolicy 枚举）在
//      Kaleidoscope.Services（同命名空间直用），SerialPortNameResolver 在
//      Kaleidoscope.Utils（using 引入）；MarkDoneHub 静态中继随本文件同迁。
//   其余算法/默认值/异常口径/注释全保留，公共 API 签名不变（主项目经
//   文件级 using 别名壳零改动引用，断点可跨工程打进本源码）。
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Kaleidoscope.Utils;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 激光打码机通讯服务（老工程 SubClass/LaserMarkService.cs，原样搬运，仅命名空间改变）。
    // -------------------------------------------------------------------------------------
    // 默认应答方：下位机 F2 发"命令"字索要内容，我们回"内容+回车"，对不上只记日志。
    // 工作模式三选一（Answer 老行为缺省 / DoneOnly 只收完成 / Both 双向）；
    // 双传输 TCP 监听（默认 1212）/ SERIAL 多口同时监听；编码 ASCII/UNICODE/UTF8 可配；
    // 完成信号关键词+后缀判定（Ok/Ng/Unknown）；脚本经 MarkDoneHub 静态中继订阅完成信号。
    // 常驻后台线程，绝不碰 UI 控件；Dispose 置旗 + 限时 Join。
    // =====================================================================================
    public enum MarkWorkMode
    {
        Answer,
        DoneOnly,
        Both
    }

    /// <summary>完成信号判定结果（三态：成功/失败/未知）。报文"关键词+后缀"，如 HJ:DONE,OK。</summary>
    public enum MarkDoneResult
    {
        Ok,
        Ng,
        Unknown
    }

    /// <summary>
    /// 打码完成信号静态中继（给现场脚本订阅用：脚本拿不到服务实例，订这个静态事件）。
    /// 后台收码线程触发，处理函数只做写 PLC/记日志短操作，不要弹框、不要碰 UI。
    /// </summary>
    public static class MarkDoneHub
    {
        /// <summary>完成信号（deviceId, 结果, 原文, 距上次应答耗时ms，-1=无配对）。</summary>
        public static event Action<int, MarkDoneResult, string, long> Done;

        /// <summary>服务内部调用（internal：只允许 LaserMarkService 触发，外部只能订阅）。</summary>
        internal static void Raise(int deviceId, MarkDoneResult result, string raw, long elapsedMs)
        {
            try { if (Done != null) Done(deviceId, result, raw, elapsedMs); }
            catch { }
        }
    }

    public class LaserMarkConfig
    {
        public int DeviceId;
        public string Transport = "TCP";
        public int ListenPort = 1212;
        public string Command = "HJ:REQ";
        public string EncodingName = "ASCII";
        public int TimeoutMs = 3000;
        public MarkWorkMode WorkMode = MarkWorkMode.Answer;
        public string DoneKeyword = "HJ:DONE";
        public string DoneOkText = "OK";
        public string DoneNgText = "NG";
        public int DoneTimeoutMs = 10000;
        public MarkContentSource ContentSource = MarkContentSource.Plc;
        public MarkEmptyPolicy EmptyPolicy = MarkEmptyPolicy.Alarm;
        public string ErrorText = "ERROR:NO_CONTENT";
        public string FixedText = "TEST";
        public string ComPort = "";
        public string DeviceKeyword = "";
        public int BaudRate = 9600;
        public int DataBits = 8;
        public string StopBits = "1";
        public string Parity = "None";

        public bool IsSerial
        {
            get { return string.Equals(Transport, "SERIAL", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>是否回内容（Answer/Both 回，DoneOnly 不回）。</summary>
        public bool NeedAnswer
        {
            get { return WorkMode != MarkWorkMode.DoneOnly; }
        }

        /// <summary>是否收完成信号（DoneOnly/Both 收且关键词非空；Answer 忽略=老行为）。</summary>
        public bool WatchDone
        {
            get
            {
                return WorkMode != MarkWorkMode.Answer
                    && !string.IsNullOrWhiteSpace(DoneKeyword);
            }
        }
    }

    public class LaserMarkService : DeviceServiceBase<LaserMarkConfig>
    {
        /// <summary>连接状态变化事件（deviceId, connected），后台线程触发，订阅方封送回业务线程。</summary>
        public event Action<int, bool> ConnectionChanged;

        /// <summary>
        /// 完成信号事件（deviceId, 结果, 原文, 距上次应答耗时ms）。
        /// 后台收码线程触发，订阅方必须封送回业务线程且内部 try-catch。
        /// elapsedMs=-1 表示本次完成之前没给过内容（DoneOnly 常态）。
        /// </summary>
        public event Action<int, MarkDoneResult, string, long> DoneReceived;

        /// <summary>监听状态摘要（配置页"测试通讯"串口模式用）。绝不抛异常。</summary>
        public override string GetDebugInfo()
        {
            try
            {
                if (MockBridge.IsEnabled)
                {
                    if (!mCfg.IsSerial)
                        return "TCP监听端口" + mCfg.ListenPort + "（Mock演示：视为有下位机连入，不接硬件）";
                    return "已打开：Mock串口（演示，不接硬件）；活动口：Mock";
                }
                if (!mCfg.IsSerial)
                {
                    return "TCP监听端口" + mCfg.ListenPort
                        + "（" + (mConnected ? "有下位机连入" : "等待下位机连接") + "）";
                }
                lock (mPortsLock)
                {
                    if (mPorts.Count == 0) return "串口未打开（检查ComPort/DeviceKeyword/线缆）";
                    List<string> names = new List<string>(mPorts.Keys);
                    return "已打开：" + string.Join(",", names.ToArray())
                        + "；活动口：" + (string.IsNullOrEmpty(mActivePort) ? "无（尚无命令）" : mActivePort);
                }
            }
            catch { return "未知"; }
        }

        /// <summary>
        /// 一次性连通性测试：TCP+回内容模式做本机回环真发一条；
        /// 其余模式以"服务在监听"为通。失败不抛异常，info 给排查一句话。
        /// </summary>
        public override bool TestConnection(out string info)
        {
            info = "";
            try
            {
                string mockInfo;
                if (DeviceServiceBase.MockOk(out mockInfo, "激光打码")) { info = mockInfo; return true; }
                if (mDisposed) { info = "服务已停止（未配置或已被热更关闭）"; return false; }
                if (!mCfg.IsSerial && mCfg.NeedAnswer)
                {
                    string reply; long ms;
                    bool ok = TestTcpLoopback(mCfg.ListenPort, mCfg.Command, mEnc, 3000, out reply, out ms);
                    if (ok)
                    {
                        string preview = (reply ?? "").Trim();
                        info = "本机回环 " + ms.ToString() + "ms，应答[" + preview + "]"
                            + (string.Equals(preview, (mCfg.ErrorText ?? "").Trim(), StringComparison.Ordinal)
                                ? "（空仓，回的是错误串）" : "（本次消耗了一条排队内容）");
                        return true;
                    }
                    info = "连不上 127.0.0.1:" + mCfg.ListenPort.ToString()
                        + "，检查服务是否已启动、端口与防火墙";
                    return false;
                }
                info = GetDebugInfo();
                return mConnected;
            }
            catch (Exception ex) { info = "测试异常：" + ex.Message; return false; }
        }

        /// <summary>
        /// TCP 自环测试：本机连监听端口，发"命令+回车"等一行应答。
        /// 【副作用】通的时候会消耗一条排队内容（空仓回错误串），调用方须如实告知用户。
        /// </summary>
        public static bool TestTcpLoopback(int port, string command, Encoding enc,
            int timeoutMs, out string reply, out long elapsedMs)
        {
            reply = null;
            elapsedMs = 0;
            if (MockBridge.IsEnabled) { reply = "MOCK-OK"; elapsedMs = 3; return true; }
            Stopwatch sw = Stopwatch.StartNew();
            TcpClient c = new TcpClient();
            try
            {
                IAsyncResult ar = c.BeginConnect("127.0.0.1", port, null, null);
                WaitHandle wh = ar.AsyncWaitHandle;
                bool linked = wh.WaitOne(Math.Max(500, timeoutMs));
                try { wh.Close(); } catch { }
                if (!linked)
                {
                    sw.Stop();
                    return false;
                }
                c.EndConnect(ar);
                c.ReceiveTimeout = timeoutMs;
                c.SendTimeout = timeoutMs;
                NetworkStream ns = c.GetStream();
                byte[] req = enc.GetBytes((command ?? "") + "\r\n");
                ns.Write(req, 0, req.Length);
                Decoder dec = enc.GetDecoder();
                byte[] buf = new byte[4096];
                char[] chars = new char[1024];
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    int cc = dec.GetChars(buf, 0, n, chars, 0);
                    sb.Append(chars, 0, cc);
                    if (sb.ToString().IndexOf('\n') >= 0) break;
                }
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                string s = sb.ToString();
                if (s.IndexOf('\n') < 0) return false;
                reply = s;
                return true;
            }
            catch
            {
                sw.Stop();
                return false;
            }
            finally { try { c.Close(); } catch { } }
        }

        private readonly Encoding mEnc;

        private TcpListener mListener;
        private Thread mAcceptThread;
        private readonly object mClientsLock = new object();
        private readonly List<TcpClient> mClients = new List<TcpClient>();

        private readonly object mPortsLock = new object();
        private readonly Dictionary<string, SerialPortState> mPorts =
            new Dictionary<string, SerialPortState>(StringComparer.OrdinalIgnoreCase);
        private Thread mRescanThread;
        private DateTime mLastRxTime = DateTime.MinValue;
        private readonly Dictionary<string, bool> mOpenFailLogged =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private string mActivePort;
        private bool mFallbackWarned;

        /// <summary>串口单个口的状态：句柄 + 解码器（跨包不断字）+ 行缓冲，各口独立。</summary>
        private class SerialPortState
        {
            public SerialPort Port;
            public Decoder Decoder;
            public StringBuilder Pending = new StringBuilder();
        }

        private readonly object mMismatchLock = new object();
        private readonly Dictionary<string, DateTime> mMismatchLastLog =
            new Dictionary<string, DateTime>();

        private const int RescanMs = 5000;
        private const int MismatchLogGapMs = 5000;
        private const int MaxLineBuffer = 4096;

        private readonly object mDoneLock = new object();
        private DateTime mLastAnswerTime = DateTime.MinValue;

        public LaserMarkService(LaserMarkConfig cfg)
            : base(cfg)
        {
            if (string.IsNullOrWhiteSpace(mCfg.Command)) mCfg.Command = "HJ:REQ";
            mEnc = ResolveEncoding(mCfg.EncodingName);
            MarkContentHub.Configure(mCfg.DeviceId, mCfg.ContentSource, mCfg.EmptyPolicy,
                mCfg.ErrorText, mCfg.FixedText);
        }

        /// <summary>
        /// 工作模式解析（ANSWER/ DONE_ONLY/DONEONLY/DONE / BOTH，大小写不敏感；
        /// 非法回 fallback，正常传 Answer 回老行为不断线）。存盘用 WorkModeToText。
        /// </summary>
        public static MarkWorkMode ParseWorkMode(string name, MarkWorkMode fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            string n = name.Trim().ToUpperInvariant();
            if (n == "ANSWER") return MarkWorkMode.Answer;
            if (n == "DONE_ONLY" || n == "DONEONLY" || n == "DONE") return MarkWorkMode.DoneOnly;
            if (n == "BOTH") return MarkWorkMode.Both;
            return fallback;
        }

        /// <summary>工作模式转存盘文本（ParseWorkMode 的逆操作）。</summary>
        public static string WorkModeToText(MarkWorkMode mode)
        {
            if (mode == MarkWorkMode.DoneOnly) return "DONE_ONLY";
            if (mode == MarkWorkMode.Both) return "BOTH";
            return "ANSWER";
        }

        /// <summary>编码解析（ASCII/UNICODE/UTF8 兼容；非法回落 ASCII，不断线）。</summary>
        public static Encoding ResolveEncoding(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Encoding.ASCII;
            string n = name.Trim().ToUpperInvariant();
            if (n == "UNICODE" || n == "UTF-16" || n == "UTF16") return Encoding.Unicode;
            if (n == "UTF8" || n == "UTF-8") return Encoding.UTF8;
            return Encoding.ASCII;
        }

        private static StopBits ParseStopBits(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return StopBits.One;
            string n = name.Trim();
            if (n == "2") return StopBits.Two;
            if (n == "15" || n == "1.5" || n == "OnePointFive") return StopBits.OnePointFive;
            return StopBits.One;
        }

        private static Parity ParseParity(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Parity.None;
            try { return (Parity)Enum.Parse(typeof(Parity), name.Trim(), true); }
            catch { return Parity.None; }
        }

        /// <summary>启动服务（TCP 开监听 / 串口开候选监听 + 重扫线程）。异常只记日志不抛。</summary>
        public override void Start()
        {
            try
            {
                if (mCfg.IsSerial) StartSerial();
                else StartTcp();
            }
            catch (Exception ex)
            {
                Log("启动失败：" + ex.Message);
            }
        }

        /// <summary>停止服务（置旗→关句柄→限时 Join，可重复调）。</summary>
        public override void Dispose()
        {
            mDisposed = true;
            try
            {
                if (mListener != null) { try { mListener.Stop(); } catch { } }
                lock (mClientsLock)
                {
                    for (int i = 0; i < mClients.Count; i++)
                    {
                        try { mClients[i].Close(); } catch { }
                    }
                    mClients.Clear();
                }
                lock (mPortsLock)
                {
                    foreach (KeyValuePair<string, SerialPortState> kv in mPorts)
                    {
                        try
                        {
                            if (kv.Value.Port != null)
                            {
                                try { if (kv.Value.Port.IsOpen) kv.Value.Port.Close(); } catch { }
                                kv.Value.Port.Dispose();
                            }
                        }
                        catch { }
                    }
                    mPorts.Clear();
                }
            }
            catch { }
            JoinQuietly(mAcceptThread, 1500);
            JoinQuietly(mRescanThread, 1500);
            SetConnected(false);
        }

        protected override string FormatLog(string msg)
        {
            return DateTime.Now + " 打码机[" + mCfg.DeviceId + "]" + msg;
        }

        private void SetConnected(bool value)
        {
            if (mConnected == value) return;
            mConnected = value;
            try
            {
                Log(value ? "已连接" : "连接中断，后台自动重连…");
                if (ConnectionChanged != null) ConnectionChanged(mCfg.DeviceId, value);
            }
            catch { }
        }

        /// <summary>
        /// 处理一行收到的文本（分发层）：①完成信号（WatchDone 命中关键词→完成分支）；
        /// ②索要应答（DoneOnly 只记日志；对上命令字才回内容）；③对不上→节流记日志。
        /// reply 是发送回调，返回实际发送字节数。
        /// </summary>
        private void HandleCommandLine(string line, Func<byte[], int> reply, string from)
        {
            string cmd = (line ?? "").Trim().Trim('\0', ' ', '\t');
            if (cmd.Length == 0) return;
            if (mCfg.WatchDone && IsDoneLine(cmd))
            {
                HandleDoneLine(cmd, from);
                return;
            }
            if (!mCfg.NeedAnswer)
            {
                Log("收到索要命令已忽略（DONE_ONLY 模式不回内容，来自" + from + "）：“" + cmd + "”");
                return;
            }
            if (!string.Equals(cmd, mCfg.Command, StringComparison.Ordinal))
            {
                LogMismatch(from, cmd);
                return;
            }
            Stopwatch sw = Stopwatch.StartNew();
            string content;
            bool isError;
            MarkContentHub.TryTake(mCfg.DeviceId, out content, out isError);
            try
            {
                byte[] data = mEnc.GetBytes((content ?? "") + "\r\n");
                int sent = reply(data);
                sw.Stop();
                string warn = sw.ElapsedMilliseconds > mCfg.TimeoutMs
                    ? "（超耗时告警线" + mCfg.TimeoutMs + "ms）" : "";
                Log("收到命令→回[" + content + "] " + sent + "字节 来自" + from
                    + " 队列剩余" + MarkContentHub.QueuedCount(mCfg.DeviceId)
                    + (isError ? " [空仓策略]" : "") + warn);
                if (mCfg.WatchDone)
                {
                    lock (mDoneLock) { mLastAnswerTime = DateTime.Now; }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log("应答发送失败（来自" + from + "）：" + ex.Message);
                SetConnected(false);
            }
        }

        /// <summary>
        /// 是否完成信号行：等于关键词，或以关键词开头且紧随分隔符（, : ; 空格 Tab = |）。
        /// 防"HJ:DONE2"被前缀误判。
        /// </summary>
        private bool IsDoneLine(string cmd)
        {
            string key = (mCfg.DoneKeyword ?? "").Trim();
            if (key.Length == 0 || cmd.Length < key.Length) return false;
            if (!cmd.StartsWith(key, StringComparison.Ordinal)) return false;
            if (cmd.Length == key.Length) return true;
            char c = cmd[key.Length];
            return c == ',' || c == ':' || c == ';' || c == ' '
                || c == '\t' || c == '=' || c == '|';
        }

        /// <summary>
        /// 解析完成结果：结果段含成功后缀→Ok；否则含失败后缀→Ng；否则 Unknown。
        /// 空后缀不参与判定；成功优先于失败。
        /// </summary>
        private MarkDoneResult ParseDoneResult(string cmd)
        {
            string key = (mCfg.DoneKeyword ?? "").Trim();
            string tail = cmd.Length > key.Length
                ? cmd.Substring(key.Length).Trim(',', ':', ';', ' ', '\t', '=', '|')
                : "";
            string ok = (mCfg.DoneOkText ?? "").Trim();
            string ng = (mCfg.DoneNgText ?? "").Trim();
            if (ok.Length > 0 && tail.IndexOf(ok, StringComparison.OrdinalIgnoreCase) >= 0)
                return MarkDoneResult.Ok;
            if (ng.Length > 0 && tail.IndexOf(ng, StringComparison.OrdinalIgnoreCase) >= 0)
                return MarkDoneResult.Ng;
            return MarkDoneResult.Unknown;
        }

        /// <summary>
        /// 完成信号处理：解析结果→算配对耗时→记日志→抛 DoneReceived 事件→转 MarkDoneHub 给脚本。
        /// 超 DoneTimeoutMs 只记告警不做动作（停线/重打由业务订阅方决定）。
        /// </summary>
        private void HandleDoneLine(string cmd, string from)
        {
            MarkDoneResult result = ParseDoneResult(cmd);
            long elapsedMs = -1;
            lock (mDoneLock)
            {
                if (mLastAnswerTime != DateTime.MinValue)
                    elapsedMs = (long)(DateTime.Now - mLastAnswerTime).TotalMilliseconds;
            }
            string cost = elapsedMs >= 0 ? " 距上次应答" + elapsedMs + "ms" : "";
            string over = (elapsedMs >= 0 && elapsedMs > mCfg.DoneTimeoutMs)
                ? "（超完成告警线" + mCfg.DoneTimeoutMs + "ms）" : "";
            string tag = result == MarkDoneResult.Ok ? "成功"
                : (result == MarkDoneResult.Ng ? "失败" : "未知结果段");
            Log("收到完成信号→" + tag + "[" + cmd + "] 来自" + from + cost + over
                + (result == MarkDoneResult.Unknown ? " [请核对成功/失败后缀配置]" : ""));
            try
            {
                if (DoneReceived != null) DoneReceived(mCfg.DeviceId, result, cmd, elapsedMs);
            }
            catch (Exception ex)
            {
                Log("完成事件订阅方异常已隔离：" + ex.Message);
            }
            MarkDoneHub.Raise(mCfg.DeviceId, result, cmd, elapsedMs);
        }

        /// <summary>命令不匹配日志（5 秒同来源只记一次，防串口噪声刷屏）。</summary>
        private void LogMismatch(string from, string cmd)
        {
            bool logIt = false;
            lock (mMismatchLock)
            {
                DateTime last;
                if (!mMismatchLastLog.TryGetValue(from, out last) ||
                    (DateTime.Now - last).TotalMilliseconds >= MismatchLogGapMs)
                {
                    mMismatchLastLog[from] = DateTime.Now;
                    logIt = true;
                }
            }
            if (logIt) Log("收到未知命令（来自" + from + "）：“" + cmd + "”，已忽略（期望“" + mCfg.Command + "”）");
        }

        /// <summary>
        /// 从行缓冲切出完整行（\r、\n、CRLF 全兼容）。尾部无终止符半包留缓冲等下一段；
        /// 尾部单个 \r 暂扣（可能是被拆开的 CRLF 前半）；超 4KB 清空防爆内存。
        /// </summary>
        private void ExtractLines(StringBuilder pending, List<string> outLines)
        {
            outLines.Clear();
            string s = pending.ToString();
            pending.Length = 0;
            if (s.Length > MaxLineBuffer)
            {
                Log("行缓冲超4KB无终止符，清空（请检查下位机行尾/编码配置）");
                return;
            }
            string hold = "";
            if (s.Length > 0 && s[s.Length - 1] == '\r' && !s.EndsWith("\r\n"))
            {
                hold = "\r";
                s = s.Substring(0, s.Length - 1);
            }
            string[] parts = s.Split(new char[] { '\r', '\n' }, StringSplitOptions.None);
            bool complete = s.Length > 0 && (s[s.Length - 1] == '\r' || s[s.Length - 1] == '\n');
            int emitCount = complete ? parts.Length : parts.Length - 1;
            for (int i = 0; i < emitCount; i++) outLines.Add(parts[i]);
            if (!complete && parts.Length > 0) pending.Append(parts[parts.Length - 1]);
            pending.Append(hold);
        }

        private void StartTcp()
        {
            mListener = new TcpListener(IPAddress.Any, mCfg.ListenPort);
            mListener.Start();
            Log("TCP 监听已启动 端口" + mCfg.ListenPort + " 命令“" + mCfg.Command
                + "” 编码" + mEnc.EncodingName);
            mAcceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LaserMarkAccept" + mCfg.DeviceId };
            mAcceptThread.Start();
        }

        /// <summary>Accept 循环：阻塞等下位机连入，来一个起一个收码线程；Stop 后抛异常即退出。</summary>
        private void AcceptLoop()
        {
            while (!mDisposed)
            {
                try
                {
                    TcpClient client = mListener.AcceptTcpClient();
                    if (mDisposed) { try { client.Close(); } catch { } break; }
                    string peer = "TCP客户端";
                    try { peer = client.Client.RemoteEndPoint.ToString(); } catch { }
                    lock (mClientsLock) { mClients.Add(client); }
                    SetConnected(true);
                    Log("下位机连入：" + peer);
                    Thread t = new Thread(ClientLoop) { IsBackground = true };
                    t.Start(client);
                }
                catch
                {
                    if (!mDisposed) { Thread.Sleep(500); }
                    else break;
                }
            }
        }

        /// <summary>
        /// 单客户端收码循环：阻塞读→解码器（跨包不断字）→切行→应答。
        /// 读超时只继续等；对端关闭/异常则收尾退出线程。
        /// </summary>
        private void ClientLoop(object obj)
        {
            TcpClient client = (TcpClient)obj;
            string peer = "TCP客户端";
            try { peer = client.Client.RemoteEndPoint.ToString(); } catch { }
            Decoder decoder = mEnc.GetDecoder();
            byte[] buf = new byte[4096];
            char[] chars = new char[4096];
            StringBuilder pending = new StringBuilder();
            List<string> lines = new List<string>();
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 5000;
                NetworkStream ns = client.GetStream();
                while (!mDisposed && client.Connected)
                {
                    int n;
                    try { n = ns.Read(buf, 0, buf.Length); }
                    catch (System.IO.IOException)
                    {
                        continue;
                    }
                    if (n <= 0) break;
                    int cc = decoder.GetChars(buf, 0, n, chars, 0);
                    pending.Append(chars, 0, cc);
                    ExtractLines(pending, lines);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string line = lines[i];
                        HandleCommandLine(line, delegate (byte[] data)
                        {
                            ns.Write(data, 0, data.Length);
                            return data.Length;
                        }, peer);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("连接异常（" + peer + "）：" + ex.Message);
            }
            finally
            {
                try { client.Close(); } catch { }
                lock (mClientsLock) { mClients.Remove(client); }
                if (mClients.Count == 0) SetConnected(false);
                Log("下位机断开：" + peer);
            }
        }

        private void StartSerial()
        {
            RescanPorts(true);
            mRescanThread = new Thread(RescanLoop) { IsBackground = true, Name = "LaserMarkRescan" + mCfg.DeviceId };
            mRescanThread.Start();
            Log("串口监听已启动 命令“" + mCfg.Command + "” 编码" + mEnc.EncodingName
                + " 参数" + mCfg.BaudRate + "/" + mCfg.DataBits + "/" + mCfg.StopBits + "/" + mCfg.Parity
                + (string.IsNullOrWhiteSpace(mCfg.ComPort) ? "（自动识别" : "（固定口" + mCfg.ComPort.Trim().ToUpperInvariant())
                + (string.IsNullOrWhiteSpace(mCfg.DeviceKeyword) ? "" : " 关键词“" + mCfg.DeviceKeyword.Trim() + "”") + "）");
        }

        /// <summary>重扫循环：每 5 秒按候选开新口、关消失口；动态模式空闲超 60 秒全关重开。</summary>
        private void RescanLoop()
        {
            int tick = 0;
            while (!mDisposed)
            {
                try
                {
                    tick++;
                    RescanPorts(false);
                    bool dynamicMode = string.IsNullOrWhiteSpace(mCfg.ComPort);
                    if (dynamicMode && tick % 12 == 0 &&
                        (DateTime.Now - mLastRxTime).TotalSeconds > 60)
                    {
                        ReopenAllPorts();
                    }
                }
                catch (Exception ex)
                {
                    Log("串口重扫异常：" + ex.Message);
                }
                for (int i = 0; i < RescanMs / 200 && !mDisposed; i++) Thread.Sleep(200);
            }
        }

        /// <summary>按候选列表开新口、关消失口。first 为启动首扫。</summary>
        private void RescanPorts(bool first)
        {
            string cached = SerialPortNameResolver.LoadCachedPort(mCfg.DeviceId);
            bool fallback;
            List<string> candidates = SerialPortNameResolver.BuildCandidatePorts(
                cached, mCfg.ComPort, mCfg.DeviceKeyword, out fallback);
            if (fallback && !mFallbackWarned)
            {
                mFallbackWarned = true;
                Log("警告：未配置固定口且关键词无匹配，已回落监听系统全部串口（建议配 DeviceKeyword 精确定位）");
            }

            HashSet<string> want = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
            string[] system = SerialPortNameResolver.SafeGetPortNames();
            HashSet<string> sysSet = new HashSet<string>(system, StringComparer.OrdinalIgnoreCase);

            lock (mPortsLock)
            {
                List<string> toClose = new List<string>();
                foreach (string name in mPorts.Keys)
                {
                    if (!want.Contains(name) || !sysSet.Contains(name)) toClose.Add(name);
                }
                for (int i = 0; i < toClose.Count; i++) ClosePortLocked(toClose[i], "重扫关闭");

                for (int i = 0; i < candidates.Count; i++)
                {
                    if (mPorts.ContainsKey(candidates[i])) continue;
                    OpenPortLocked(candidates[i], first);
                }
                SetConnected(mPorts.Count > 0);
            }
        }

        /// <summary>开单个串口（必须在 mPortsLock 内调用）。失败记边沿日志，不刷屏。</summary>
        private void OpenPortLocked(string portName, bool first)
        {
            try
            {
                SerialPort sp = new SerialPort(portName, mCfg.BaudRate,
                    ParseParity(mCfg.Parity), mCfg.DataBits, ParseStopBits(mCfg.StopBits));
                sp.ReadTimeout = 2000;
                sp.WriteTimeout = 2000;
                SerialPortState st = new SerialPortState();
                st.Port = sp;
                st.Decoder = mEnc.GetDecoder();
                sp.DataReceived += OnSerialData;
                sp.ErrorReceived += delegate { };
                sp.Open();
                mPorts[portName] = st;
                mOpenFailLogged[portName] = false;
                Log("串口已打开：" + portName);
            }
            catch (Exception ex)
            {
                bool logged;
                if (!mOpenFailLogged.TryGetValue(portName, out logged) || !logged || first)
                {
                    mOpenFailLogged[portName] = true;
                    Log("串口打开失败 " + portName + "：" + ex.Message + "（后台继续重试）");
                }
            }
        }

        /// <summary>关单个串口（必须在 mPortsLock 内调用）。禁 Thread.Abort，只关句柄。</summary>
        private void ClosePortLocked(string portName, string reason)
        {
            SerialPortState st;
            if (!mPorts.TryGetValue(portName, out st)) return;
            mPorts.Remove(portName);
            try
            {
                if (st.Port != null)
                {
                    try { st.Port.DataReceived -= OnSerialData; } catch { }
                    try { if (st.Port.IsOpen) st.Port.Close(); } catch { }
                    try { st.Port.Dispose(); } catch { }
                }
            }
            catch { }
            Log("串口已关闭：" + portName + "（" + reason + "）");
        }

        /// <summary>全关重开（清鬼设备残留）。</summary>
        private void ReopenAllPorts()
        {
            Log("空闲超60秒，执行全关重开（清串口残留）");
            lock (mPortsLock)
            {
                List<string> names = new List<string>(mPorts.Keys);
                for (int i = 0; i < names.Count; i++) ClosePortLocked(names[i], "空闲重开");
            }
            RescanPorts(false);
        }

        /// <summary>
        /// 串口收数事件（线程池线程）：读尽可用字节→解码→切行→命令对上就回该口。
        /// 全程按口加锁（SerialPort 非线程安全），单口异常不影响其他口。
        /// </summary>
        private void OnSerialData(object sender, SerialDataReceivedEventArgs e)
        {
            if (mDisposed) return;
            SerialPort sp = (SerialPort)sender;
            SerialPortState st = null;
            lock (mPortsLock) { mPorts.TryGetValue(sp.PortName, out st); }
            if (st == null) return;
            lock (st)
            {
                try
                {
                    if (!sp.IsOpen) return;
                    int avail = sp.BytesToRead;
                    if (avail <= 0) return;
                    byte[] buf = new byte[Math.Min(avail, 8192)];
                    int n = sp.Read(buf, 0, buf.Length);
                    if (n <= 0) return;
                    mLastRxTime = DateTime.Now;
                    char[] chars = new char[mEnc.GetMaxCharCount(n) + 8];
                    int cc = st.Decoder.GetChars(buf, 0, n, chars, 0);
                    st.Pending.Append(chars, 0, cc);
                    List<string> lines = new List<string>();
                    ExtractLines(st.Pending, lines);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string line = lines[i];
                        string portName = sp.PortName;
                        HandleCommandLine(line, delegate (byte[] data)
                        {
                            sp.Write(data, 0, data.Length);
                            return data.Length;
                        }, "串口" + portName);
                        if (string.Equals(line.Trim().Trim('\0', ' ', '\t'), mCfg.Command, StringComparison.Ordinal))
                        {
                            if (!string.Equals(mActivePort, portName, StringComparison.OrdinalIgnoreCase))
                            {
                                mActivePort = portName;
                                Log("活动串口切换为：" + portName);
                            }
                            SerialPortNameResolver.SaveCachedPort(mCfg.DeviceId, portName);
                        }
                    }
                }
                catch
                {
                }
            }
        }
    }
}
