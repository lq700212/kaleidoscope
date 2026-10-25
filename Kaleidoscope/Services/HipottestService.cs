// =====================================================================================
// 【下沉注记】本文件 2026-10-25 自 HJVision-WPF 生产工程（第二轮仪器设备下沉）
// 原样搬入，口径与第一轮 PlcNetCommQue/TcpChannel 下沉相同：
//   ①来源 GYZVision.Core/Devices/HipottestService.cs（耐压/微电阻 SCPI 服务）；
//   ②命名空间 → Kaleidoscope.Services；
//   ③演示模式出口 MockMode.IsEnabled → MockBridge.IsEnabled（5 处，宿主静态构造接线）；
//   ④基类 DeviceServiceBase 已同轮下沉库内，继承关系不变。
//   其余算法/默认值/异常口径/注释全保留，公共 API 签名不变（主项目经
//   文件级 using 别名壳零改动引用，断点可跨工程打进本源码）。
// =====================================================================================
using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using Kaleidoscope.Utils;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 耐压绝缘 / 微电阻测试服务（原版 SubClass/HipottestService.cs 一比一搬入，
    // 2026-10-25 耐压轮同步：补 GPT 武装旗状态机/COMP? 二道确认/MMET 完整路径/115 竞态
    // 静默/NG 判定码原文日志/GOM 判定指纹/首轮建档/scan 双段解析/高压哨兵/读批按需口
    // ReadBatchOnce/经现有连接测试——WPF 旧版带着"MMET 缺 :RES 节点现场必报 ERR、
    // 服务启动拿旧结果误抛 NG、GOM 上电残留值当真批"三个旧 bug，全部随之修复）。
    // 固纬 GPT-9500（安规耐压/绝缘）+ GOM-804/805（微电阻），
    // 都是 RS-232 SCPI 直连工控机（GPT 出厂 115200/CR+LF，GOM 波特率档含 115200）。
    // -------------------------------------------------------------------------------------
    // 【协议要点（2026-09-29 按两份说明书逐页核对，页码=PDF 物理页码）】
    //   GPT（GPT-9500 系列使用手册）：
    //     SAFE:STAR 启动(p221) / SAFE:STOP 停止(p222) / SAFE:STAT? 查状态(p223)——
    //       返回字符串 RUNNING|STOPPED（不是数字！旧版按"含1"判定恒假已修）。
    //     SAFE:RES:COMP? 完成确认(p226)——返回 0|1，1=所有设定值的执行动作已完成。
    //     SAFE:RES:STEP<n>:JUDG? 逐腔判定(p230，长形式 [:SOURce]:SAFEty:RESult:STEP<n>
    //       [:JUDGment]?)——判定码表(p223/229/230)：116=PASS；115=TESTING（测试中，
    //       不能当 FAIL）；112/113=STOP、114=CAN NOT TEST、120~124=异常态；
    //       NO GOOD：AC 17/18/19、DC 33/34/35、IR 49/50、OSC 97/98。解析见
    //       ParseGptPass（115 特判）+ GptJudgeCodeToText（码→现场可读文本）。
    //     SAFE:RES:STEP<n>:MMET? 逐腔测量值(p231)——完整路径含 :RES 节点，SCPI 不允许
    //       跳中间节点（旧版 SAFE:STEP<n>:MMET? 现场必报 ERR，已修，见 BuildMeasureCmd）。
    //     多腔=STEP n（≤8，GPT-9513 内置 8 通道扫描矩阵 p54，不需要外部继电器）；
    //       AREP 自动上报仅 RS232 支持（p226~228，未测 STEP 返回 +9.910000E+37 哨兵）
    //       ——一期仍用轮询（免依赖报文格式），二期现场核对后可切。
    //   GOM（GOM-804 & GOM-805 使用手册）：
    //     READ? 读值(p143)——单值 NR3；+9.9999E+9=检测到高压（HVP）哨兵，见
    //       IsGomHvpSentinel。CALC:COMP:LIM:RES? 比较器判定(p115)——0=LO 1=IN 2=HI，
    //       IN=合格。MEAS<x>? scan 模式逐腔(p143)——返回"判定,值"两段（如 1,+0.9978E+1
    //       =IN 区间 9.978Ω），见 ParseGomMeasPair；scan 只有 805 有（804 单腔机走
    //       READ?+CALC 组合）。
    //   两台均为 8N1 无流控；GOM RS232 用交叉线、GPT 用 null-modem（同为交叉接法）。
    // 【完成判定（2026-09-29 核对后落定）】GPT=完成边沿状态机：轮询见 STAT?=RUNNING 置
    //   武装旗，RUNNING→STOPPED 边沿 + RES:COMP? 二道确认后才读结果抛批、随后解除武装
    //   等下一个测试周期——杜绝上电空转拿 112/114 旧码误报 NG、同批结果重复抛。
    //   GOM 无状态字：值+判定组合指纹变化判新批，服务启动首轮只建档不抛（上电残留
    //   显示值不是本会话测的）；Handler 口 EOT 硬件完成信号（p70-72）是二期收紧方向。
    // 【安全】GPT Interlock 出厂默认 ON 必须短接端子否则 START 无效（p152，现场 P0 项，
    //   非软件问题）；Control By 只有 Front/Signal/Both 三档但串口 SAFE:STAR 不受限(p151)。
    // 【角色】默认外部硬线触发测试，软件只轮询读结果；SoftwareStart=true 时业务可调
    //   StartTest() 下发 SAFE:STAR（仅 GPT，p221）。
    // =====================================================================================

    /// <summary>仪器型号（两台命令集不同）。</summary>
    public enum HipotModel
    {
        Gpt9500,    // 固纬安规耐压/绝缘（SAFE: 命令集）
        Gom80x      // GOM 微电阻（READ?/MEAS? 命令集）
    }

    public class HipottestConfig
    {
        public HipotModel Model = HipotModel.Gpt9500;
        public string ComPort = "";          // 串口号（TODO-SITE：现场定，如 COM3；空=服务起不来，只记日志）
        public int BaudRate = 115200;        // 两台出厂/档位都含 115200
        public int PollMs = 500;             // 结果轮询周期
        public int ChannelCount = 4;         // 腔数：GPT=STEP 数（≤8），GOM=Scan 数（架子限 8）
        public bool SoftwareStart = false;   // false=外部硬线触发测试（软件只读结果）；true=StartTest() 下发（仅 GPT）
    }

    /// <summary>一批耐压/微电阻结果（一次完成边沿产生一批，下标 0..ChannelCount-1=腔位）。</summary>
    public class HipotBatchResult
    {
        public DateTime Time;
        public bool[] Pass = new bool[0];     // true=该腔合格（GPT 116 / GOM IN）
        public float[] Values = new float[0]; // 该腔测得值：GPT=漏电流/绝缘值(MMET)，GOM=电阻值(READ)
        public bool AllPass;                  // 全腔合格（含 0 腔=false）
    }

    public class HipottestService : DeviceServiceBase<HipottestConfig>
    {
        /// <summary>一批结果就绪（后台轮询线程触发，订阅方需 BeginInvoke 回业务线程，内部已吞异常）。</summary>
        public event Action<HipotBatchResult> ResultReady;

        /// <summary>连接状态变化（true=串口已开，false=断开）。边沿触发。</summary>
        public event Action<bool> ConnectionChanged;

        private SerialPort mPort;
        private readonly object mIoLock = new object();   // 串口一问一答串行化（轮询与测试通讯互斥）
        private Thread mPollThread;
        private DateTime mLastErrLog = DateTime.MinValue;

        public HipottestService(HipottestConfig cfg)
            : base(cfg)
        {
            if (mCfg.ChannelCount < 1) mCfg.ChannelCount = 1;
            if (mCfg.ChannelCount > 8) mCfg.ChannelCount = 8;   // 架子上限，GOM Scan 可到 100，现场确认腔数后再放
            if (mCfg.PollMs < 100) mCfg.PollMs = 100;
            if (mCfg.PollMs > 5000) mCfg.PollMs = 5000;
        }

        /// <summary>日志前缀（与框架子类惯例一致：时间戳 + " 耐压[型号]"）。</summary>
        protected override string FormatLog(string msg)
        {
            return DateTime.Now + " 耐压[" + ModelToText(mCfg.Model) + "]" + msg;
        }

        public static string ModelToText(HipotModel m)
        {
            return m == HipotModel.Gom80x ? "GOM" : "GPT";
        }

        public static HipotModel ParseModel(string name, HipotModel fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            string n = name.Trim().ToUpperInvariant();
            if (n == "GOM" || n == "GOM80X" || n == "GOM804" || n == "GOM805") return HipotModel.Gom80x;
            if (n == "GPT" || n == "GPT9500" || n == "GPT-9500") return HipotModel.Gpt9500;
            return fallback;
        }

        // =====================================================================================
        // SCPI 命令拼装与响应解析（静态纯函数：DeviceFrameworkProbe 直接测，现场核对协议
        // 只改这些函数+IsTestRunning，不动线程骨架）。
        // =====================================================================================

        /// <summary>仪器身份查询（SCPI 标准，两台都应支持；测试通讯用）。</summary>
        public static string BuildIdnCmd() { return "*IDN?"; }

        /// <summary>启动测试命令（GPT SAFE:STAR；GOM 无远程启动语义返回 null）。</summary>
        public static string BuildStartCmd(HipotModel m)
        {
            return m == HipotModel.Gpt9500 ? "SAFE:STAR" : null;
        }

        /// <summary>停止测试命令（GPT SAFE:STOP；GOM 返回 null）。</summary>
        public static string BuildStopCmd(HipotModel m)
        {
            return m == HipotModel.Gpt9500 ? "SAFE:STOP" : null;
        }

        /// <summary>状态查询命令（GPT SAFE:STAT?；GOM 无状态字返回 null——用读值代替）。</summary>
        public static string BuildStatCmd(HipotModel m)
        {
            return m == HipotModel.Gpt9500 ? "SAFE:STAT?" : null;
        }

        /// <summary>
        /// 第 ch 腔(1 基)判定查询命令。GPT=SAFE:RES:STEP<n>:JUDG?（p230 示例缩写，
        /// 判定码解析见 ParseGptPass/GptJudgeCodeToText）；GOM 单腔=READ? 读值（p143，
        /// 804 是单腔机没有 scan，判定另发 BuildJudgeCmd）；GOM 多腔=MEAS<x>?（scan
        /// 模式仅 805 有，响应"判定,值"两段一问双答，见 ParseGomMeasPair）。
        /// </summary>
        public static string BuildResultCmd(HipotModel m, int ch)
        {
            return m == HipotModel.Gpt9500
                ? "SAFE:RES:STEP" + ch.ToString() + ":JUDG?"
                : (ch == 1 ? "READ?" : "MEAS" + ch.ToString() + "?");
        }

        /// <summary>
        /// 第 ch 腔(1 基)测量值查询命令。GPT=SAFE:RES:STEP<n>:MMET?（p231 示例缩写，
        /// 完整路径必含 :RES 节点——SCPI 不允许跳中间节点，旧版 SAFE:STEP<n>:MMET?
        /// 缺节点现场必报 ERR）；GOM 的 READ?/MEAS<x>? 响应已带值，无需二次查询（返回 null）。
        /// </summary>
        public static string BuildMeasureCmd(HipotModel m, int ch)
        {
            return m == HipotModel.Gpt9500
                ? "SAFE:RES:STEP" + ch.ToString() + ":MMET?"
                : null;
        }

        /// <summary>第 ch 腔(1 基)合格判定查询命令（GOM 专用：0=LO 1=IN 2=HI，p115；
        /// GPT 判定在 JUDG 里返回 null）。</summary>
        public static string BuildJudgeCmd(HipotModel m, int ch)
        {
            return m == HipotModel.Gom80x ? "CALC:COMP:LIM:RES?" : null;
        }

        /// <summary>
        /// GPT 完成确认命令（p226：SAFE:RES:COMP? 返回 0|1，1=所有设定值的执行动作
        /// 已完成）。配合 STAT? 做结果新鲜度二道确认（见 PollOnceGpt）；GOM 无对应命令
        /// 返回 null。
        /// </summary>
        public static string BuildCompCmd(HipotModel m)
        {
            return m == HipotModel.Gpt9500 ? "SAFE:RES:COMP?" : null;
        }

        /// <summary>
        /// GPT 判定响应解析（说明书 p223/229/230 判定码表：116=PASS）。
        /// 116=true；其余数字码=false（能读到码说明该步已出结果，不是 116 就是不合格，
        /// 含 NO GOOD 17/18/19/33/34/35/49/50/97/98 与异常态 112/113/114/120~124）；
        /// 115=TESTING 特判返回 null——测试中读判定会拿到它，不是 FAIL 也不是 PASS，
        /// 调用方（ReadBatch）按"本轮作废等下轮"处理，绝不能错抛 NG；
        /// 非数字（异常文本/ERR）=null 未知，宁可疑不可漏。
        /// </summary>
        public static bool? ParseGptPass(string resp)
        {
            if (string.IsNullOrEmpty(resp)) return null;
            string t = resp.Trim();
            if (t == "116") return true;
            if (t == "115") return null;   // TESTING：测试中，不能判 FAIL（见方法注释）
            int n;
            return int.TryParse(t, out n) ? (bool?)false : null;
        }

        /// <summary>
        /// GPT 判定码→现场可读文本（说明书 p223/229/230 码表）。NG 批抛出时随日志记
        /// 判定码原文，现场不用翻手册就知道是超限/电弧/开短路还是仪器异常态。
        /// </summary>
        public static string GptJudgeCodeToText(int code)
        {
            switch (code)
            {
                case 116: return "PASS";
                case 115: return "TESTING(测试中)";
                case 114: return "CAN NOT TEST(无法测试)";
                case 113: return "STOP(停止)";
                case 112: return "STOP(停止)";
                case 123: return "VOLT OVER(过压)";
                case 124: return "VOLT LOW(欠压)";
                case 120: return "GR CONT.(接地连续)";
                case 121: return "GFCI(漏电保护)";
                case 122: return "POWER GND(电源接地)";
                case 17: return "AC HI(耐压上限)";
                case 18: return "AC LO(耐压下限)";
                case 19: return "AC ARC(耐压电弧)";
                case 33: return "DC HI(耐压上限)";
                case 34: return "DC LO(耐压下限)";
                case 35: return "DC ARC(耐压电弧)";
                case 49: return "IR HI(绝缘上限)";
                case 50: return "IR LO(绝缘下限)";
                case 97: return "OSC SHORT(短路)";
                case 98: return "OSC OPEN(开路)";
                default: return "CODE" + code.ToString() + "(未定义)";
            }
        }

        /// <summary>GOM 判定响应解析（0=LO 1=IN 2=HI，IN=合格，p115）。无法识别=null。</summary>
        public static bool? ParseGomJudge(string resp)
        {
            if (string.IsNullOrEmpty(resp)) return null;
            string t = resp.Trim();
            if (t == "1") return true;    // IN 区间=合格
            if (t == "0" || t == "2") return false;   // LO/HI 超差
            return null;
        }

        /// <summary>
        /// GOM scan 逐腔 MEAS&lt;x&gt;? 双段响应解析（说明书 p143：返回"判定,值"两段，
        /// 如 1,+0.9978E+1 表示 IN 区间、9.978Ω——旧版按单值解析必失败变 NaN，已修）。
        /// 返回判定（0=LO 1=IN 2=HI）；测得值经 out 带出（无法解析=NaN）。
        /// 格式不含逗号/判定段废=null（整段作废，宁可不抛不错抛）。
        /// </summary>
        public static bool? ParseGomMeasPair(string resp, out float value)
        {
            value = float.NaN;
            if (string.IsNullOrEmpty(resp)) return null;
            string t = resp.Trim();
            int comma = t.IndexOf(',');
            if (comma <= 0) return null;   // 没有逗号=不是"判定,值"双段格式
            bool? pass = ParseGomJudge(t.Substring(0, comma));
            value = ParseValue(t.Substring(comma + 1)) ?? float.NaN;
            return pass;
        }

        /// <summary>
        /// 测得值解析（GPT MMET/GOM READ 的 NR3 响应，如 +5.000000E-05）。固定用
        /// InvariantCulture——SCPI 数字格式与系统区域设置无关，防区域差异把小数点
        /// 解析错。无法解析=null。
        /// </summary>
        public static float? ParseValue(string resp)
        {
            if (string.IsNullOrEmpty(resp)) return null;
            float f;
            return float.TryParse(resp.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out f) ? (float?)f : null;
        }

        /// <summary>
        /// GOM 高压哨兵判定（说明书 p143：READ? 返回 +9.9999E+9=检测到高压 HVP，
        /// 治具/工件带电时仪器保护性报此值）。正常测量上限 5.1MΩ(5.1E6)，哨兵 9.9999E+9，
        /// 阈值取 9.9E9——中间隔 3 个数量级，float 精度（约 7 位有效数字）无碍。
        /// </summary>
        public static bool IsGomHvpSentinel(float value)
        {
            return !float.IsNaN(value) && value >= 9.9e9f;
        }

        /// <summary>
        /// GPT 测试运行中判定（说明书 p223：SAFE:STAT? 返回字符串 RUNNING|STOPPED——
        /// 不是数字码！旧版按"响应含字符1"判定，两串都不含'1'恒判空闲，测试中会抢读
        /// 结果，已修为字符串比较）。GOM 无状态字恒返回 null（走指纹变化路径）。
        /// 两种字串都不是=响应异常（半行/固件差异）返回 null，调用方记日志跳过本轮。
        /// </summary>
        public static bool? IsTestRunning(HipotModel m, string statResp)
        {
            if (m != HipotModel.Gpt9500) return null;
            if (string.IsNullOrEmpty(statResp)) return null;
            string t = statResp.Trim().ToUpperInvariant();
            if (t.IndexOf("RUNNING") >= 0) return (bool?)true;
            if (t.IndexOf("STOPPED") >= 0) return (bool?)false;
            return null;
        }

        // ================= 启动 / 停止 =================

        public override void Start()
        {
            try
            {
                mPollThread = new Thread(PollLoop) { IsBackground = true, Name = "HipotPoll" };
                mPollThread.Start();
                Log("服务已启动 串口=" + (mCfg.ComPort == "" ? "(未配置)" : mCfg.ComPort)
                    + "@" + mCfg.BaudRate.ToString() + " 腔数=" + mCfg.ChannelCount.ToString());
            }
            catch (Exception ex)
            {
                Log("启动失败：" + ex.Message);
            }
        }

        public override void Dispose()
        {
            mDisposed = true;
            try
            {
                lock (mIoLock)
                {
                    if (mPort != null)
                    {
                        try { if (mPort.IsOpen) mPort.Close(); } catch { }
                        try { mPort.Dispose(); } catch { }
                        mPort = null;
                    }
                }
            }
            catch { }
            JoinQuietly(mPollThread, 1500);
            SetConnected(false);
        }

        private void LogErrThrottled(string msg)
        {
            // 串口打开失败/读写失败是常态高频事件（未插线时每轮都发生），10 秒节流防刷屏
            LogThrottled(msg, 10);
        }

        /// <summary>连接状态边沿（变了才发事件+记日志；签名 Action<bool>，与气密同型）。</summary>
        private void SetConnected(bool value)
        {
            if (mConnected == value) return;
            mConnected = value;
            try
            {
                Log(value ? "串口已打开" : "串口断开，后台自动重连…");
                if (ConnectionChanged != null) ConnectionChanged(value);
            }
            catch { }
        }

        // ================= 轮询主循环（骨架：重连→判完成→读结果→抛批） =================

        private void PollLoop()
        {
            while (!mDisposed)
            {
                try
                {
                    // Mock演示：不开串口，保持已连接假态静默轮询（演示不断线程不刷屏）。
                    if (MockBridge.IsEnabled) { SetConnected(true); SleepInSlots(mCfg.PollMs); continue; }
                    if (!EnsurePortOpen())
                    {
                        SetConnected(false);
                        SleepInSlots(mCfg.PollMs);   // 串口未配置/打不开：慢周期重试，不断线程
                        continue;
                    }
                    SetConnected(true);
                    PollOnce();
                }
                catch (Exception ex)
                {
                    // 外层兜底保线程不死；单次通讯异常已在内层隔离
                    LogErrThrottled("轮询异常已隔离：" + ex.Message);
                    SetConnected(false);
                }
                SleepInSlots(mCfg.PollMs);
            }
        }

        /// <summary>分片睡眠：长周期拆 50ms 片，Dispose 置旗后 50ms 内可见（不拖关窗）。</summary>
        private void SleepInSlots(int totalMs)
        {
            for (int i = 0; i < totalMs / 50 + 1 && !mDisposed; i++) Thread.Sleep(50);
        }

        private bool EnsurePortOpen()
        {
            if (string.IsNullOrWhiteSpace(mCfg.ComPort)) return false;   // 未配置=起不来，只记日志
            lock (mIoLock)
            {
                try
                {
                    if (mPort != null && mPort.IsOpen) return true;
                    if (mPort == null)
                    {
                        mPort = new SerialPort(mCfg.ComPort, mCfg.BaudRate, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
                        mPort.ReadTimeout = 500;
                        mPort.WriteTimeout = 500;
                    }
                    mPort.Open();
                    Log("串口已打开 " + mCfg.ComPort);
                    return true;
                }
                catch (Exception ex)
                {
                    LogErrThrottled("串口打开失败 " + mCfg.ComPort + "：" + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// 一轮轮询。GPT 走"RUNNING→STOPPED 完成边沿"状态机（有状态字，结果新鲜度可控）；
        /// GOM 无状态字走"值+判定指纹变化"路径（两型号完成判定机制不同，必须分流）。
        /// </summary>
        private void PollOnce()
        {
            if (mCfg.Model == HipotModel.Gpt9500) PollOnceGpt();
            else PollOnceGom();
        }

        // ---- GPT 完成边沿状态机 ----

        /// <summary>
        /// GPT 武装旗：轮询见 STAT?=RUNNING 置位；RUNNING→STOPPED 边沿读批成功抛出后
        /// 清除。保证每个测试周期只抛一批——服务启动时仪器多半是 STOPPED（上电/待机），
        /// 若不设旗会拿内存里的旧结果（112/113/114 等码）当新批错抛 NG。
        /// </summary>
        private bool mGptSawRunning;

        /// <summary>
        /// GPT 一轮：问 STAT? → RUNNING 置武装等结束；STOPPED 边沿（武装中）才读结果。
        /// 协议依据：STAT? 返回 RUNNING|STOPPED（p223）、RES:COMP? 完成确认（p226）。
        /// </summary>
        private void PollOnceGpt()
        {
            string resp;
            if (!QueryLine(BuildStatCmd(mCfg.Model), out resp)) { SetConnected(false); return; }
            bool? running = IsTestRunning(mCfg.Model, resp);
            if (running == true)
            {
                mGptSawRunning = true;   // 武装：测试进行中，等 STOPPED 边沿
                return;
            }
            if (running == null)
            {
                // 两种合法字串都不是：半行串扰/固件差异。宁可跳过本轮也不按空闲抢读
                LogErrThrottled("STAT? 响应异常[" + (resp ?? "").Trim() + "]，本轮跳过");
                return;
            }
            // running==false（STOPPED）：武装中=本周期刚结束，结果新鲜可读
            if (!mGptSawRunning) return;   // 未武装（上电/上批已消费）：不读，防旧结果误报
            // RES:COMP? 二道确认（p226：1=所有设定值执行完成）。响应异常按继续处理
            // （不因可选确认命令卡死主流程），明确 0=结果未写完才放弃本轮
            string comp;
            if (QueryLine(BuildCompCmd(mCfg.Model), out comp))
            {
                if ((comp ?? "").Trim() == "0") return;   // 异常时序：STAT 已停但结果未写完，下轮再看
            }
            HipotBatchResult batch = ReadBatch();
            if (batch != null)
            {
                mGptSawRunning = false;   // 本周期已消费，等下一个 RUNNING 边沿（防同批重复抛）
                RaiseBatch(batch);
            }
            // 读失败（通讯抖动/响应不可识别）不清武装：下轮重读同批，直到成功或下个测试周期开始
        }

        // ---- GOM 指纹变化路径 ----

        private float[] mLastGomValues;   // GOM 新批指纹缓存（上批各腔值）
        private bool[] mLastGomPass;      // GOM 新批指纹缓存（上批各腔判定）

        /// <summary>
        /// GOM 一轮：读一批 → 首轮只建档不抛 → 之后值+判定指纹变化才算新批。
        /// GOM 没有状态字也没有远程完成查询（Handler 口 EOT 硬件信号是二期方向），
        /// 只能靠"结果变了"推断新测试完成——首轮必静默兜底上电残留显示值。
        /// </summary>
        private void PollOnceGom()
        {
            HipotBatchResult batch = ReadBatch();
            if (batch == null) return;
            if (mLastGomValues == null)
            {
                // 服务启动后第一批：只建档不抛。READ? 返回的是上电残留/断电前显示值，
                // 不是本会话测的，抛出去必是假批
                CacheGomBatch(batch);
                return;
            }
            if (!GomBatchChanged(batch)) return;   // 指纹同=还是上次那批，不重复抛
            CacheGomBatch(batch);
            RaiseBatch(batch);
        }

        /// <summary>值或判定任一变化=新批（IN/HI 边界抖动可能值同判定变，两路都盯）。</summary>
        private bool GomBatchChanged(HipotBatchResult batch)
        {
            if (mLastGomValues == null || mLastGomValues.Length != batch.Values.Length) return true;
            for (int i = 0; i < batch.Values.Length; i++)
            {
                // NaN 视为相等（读不到的腔不触发边沿）
                if (!float.IsNaN(batch.Values[i]) && !float.IsNaN(mLastGomValues[i])
                    && batch.Values[i] != mLastGomValues[i]) return true;
                if (mLastGomPass != null && i < mLastGomPass.Length
                    && batch.Pass[i] != mLastGomPass[i]) return true;
            }
            return false;
        }

        private void CacheGomBatch(HipotBatchResult batch)
        {
            mLastGomValues = (float[])batch.Values.Clone();
            mLastGomPass = (bool[])batch.Pass.Clone();
        }

        /// <summary>
        /// 逐腔读判定+数值。任一腔通讯失败返回 null（本轮不抛批，等下轮重试）；
        /// 判定无法识别也返回 null（宁可不抛不错抛），响应原文随节流日志落盘供现场对协议。
        /// 三条路径（命令依据见 BuildResultCmd/BuildMeasureCmd/BuildJudgeCmd）：
        ///   GPT       = JUDG? 判定 + MMET? 测量值（两问）；
        ///   GOM 单腔  = READ? 读值（含高压哨兵检测）+ CALC:COMP:LIM:RES? 判定（两问）；
        ///   GOM 多腔  = MEAS<x>? 一问双答"判定,值"（scan 模式，仅 805）。
        /// </summary>
        private HipotBatchResult ReadBatch()
        {
            HipotBatchResult batch = new HipotBatchResult();
            batch.Time = DateTime.Now;
            batch.Pass = new bool[mCfg.ChannelCount];
            batch.Values = new float[mCfg.ChannelCount];
            batch.AllPass = mCfg.ChannelCount > 0;
            for (int ch = 1; ch <= mCfg.ChannelCount; ch++)
            {
                bool? pass = null;
                float? value = null;
                string judgResp;   // 判定响应原文（pass==null/NG 时进日志，现场对协议全靠它）
                if (mCfg.Model == HipotModel.Gpt9500)
                {
                    if (!QueryLine(BuildResultCmd(mCfg.Model, ch), out judgResp)) return null;
                    pass = ParseGptPass(judgResp);
                    if (pass == null && (judgResp ?? "").Trim() == "115")
                        return null;   // TESTING=STAT? 与结果落内存间的正常竞态，静默等下轮，不刷日志
                    string measResp;
                    if (!QueryLine(BuildMeasureCmd(mCfg.Model, ch), out measResp)) return null;
                    value = ParseValue(measResp);
                    if (pass == false)
                    {
                        // NG 批附带判定码原文（如 19=AC ARC 电弧），现场不翻手册定位原因
                        int code;
                        if (!int.TryParse((judgResp ?? "").Trim(), out code)) code = -1;
                        Log("CH" + ch.ToString() + " 判定码 " + (code >= 0
                            ? code.ToString() + "=" + GptJudgeCodeToText(code)
                            : "不可识别[" + (judgResp ?? "").Trim() + "]"));
                    }
                }
                else if (mCfg.ChannelCount == 1)
                {
                    // GOM-804 单腔标准路径（p143/p115）：804 没有 scan，发 MEAS<x>? 必无效
                    string readResp;
                    if (!QueryLine(BuildResultCmd(mCfg.Model, ch), out readResp)) return null;   // READ?
                    value = ParseValue(readResp);
                    if (IsGomHvpSentinel(value ?? float.NaN))
                        LogErrThrottled("CH1 读到高压哨兵 +9.9999E+9（检测到高压，确认治具/工件已断电再测）");
                    if (!QueryLine(BuildJudgeCmd(mCfg.Model, ch), out judgResp)) return null;    // CALC:COMP:LIM:RES?
                    pass = ParseGomJudge(judgResp);
                }
                else
                {
                    // GOM-805 scan 多腔（p143）：MEAS<x>? 一问双答，判定段优先于 CALC
                    if (!QueryLine(BuildResultCmd(mCfg.Model, ch), out judgResp)) return null;   // MEAS<x>?
                    float pairVal;   // 双段解析 out 用裸 float，再并入可空 value（NaN=值段废）
                    pass = ParseGomMeasPair(judgResp, out pairVal);
                    value = pairVal;
                }
                if (pass == null)
                {
                    // 判不出来：本轮作废。响应原文进节流日志，现场联调靠它对协议格式
                    LogErrThrottled("CH" + ch.ToString() + " 判定响应无法识别["
                        + (judgResp ?? "").Trim() + "]，本轮作废");
                    return null;
                }
                int idx = ch - 1;
                batch.Pass[idx] = pass.Value;
                batch.Values[idx] = value ?? float.NaN;
                if (!pass.Value) batch.AllPass = false;
            }
            return batch;
        }

        private void RaiseBatch(HipotBatchResult batch)
        {
            try
            {
                Log("完成一批 " + (batch.AllPass ? "全PASS" : "有FAIL")
                    + "（" + batch.Pass.Length.ToString() + " 腔）");
                if (ResultReady != null) ResultReady(batch);
            }
            catch (Exception ex) { Log("结果事件订阅方异常已隔离：" + ex.Message); }
        }

        // ================= 串口一问一答 =================

        /// <summary>
        /// 发一行命令（自动补 CR+LF）等一行响应。调用方持锁路径之外禁止直调（一问一答串行化）。
        /// 返回 false=串口没开/读写超时（响应可能半截，调用方按通讯失败处理）。
        /// </summary>
        private bool QueryLine(string cmd, out string resp)
        {
            resp = "";
            if (string.IsNullOrEmpty(cmd)) return false;
            try
            {
                lock (mIoLock)
                {
                    if (mPort == null || !mPort.IsOpen) return false;
                    mPort.DiscardInBuffer();
                    mPort.Write(cmd + "\r\n");
                    StringBuilder sb = new StringBuilder();
                    DateTime deadline = DateTime.Now.AddMilliseconds(800);
                    while (DateTime.Now < deadline && !mDisposed)
                    {
                        string chunk;
                        try { chunk = mPort.ReadExisting(); } catch { return false; }
                        if (chunk.Length > 0)
                        {
                            sb.Append(chunk);
                            if (sb.ToString().IndexOf('\n') >= 0)
                            {
                                resp = sb.ToString();
                                return true;
                            }
                        }
                        else Thread.Sleep(10);
                    }
                    return false;   // 超时无完整行
                }
            }
            catch (Exception ex)
            {
                LogErrThrottled("SCPI 读写失败：" + ex.Message);
                return false;
            }
        }

        // ================= 对外动作 / 测试 =================

        /// <summary>
        /// Mock 演示批（直连轮询不经过它；ReadBatchOnce 在 Mock 模式下回这个，
        /// 让 PLC 握手服务不接仪器也能演示完整"触发→读值→回执"闭环）。
        /// 值取说明书响应示例：GPT=116 全过/漏电流 0.05mA（p230-231 示例），GOM=IN/9.978Ω（p143 示例）。
        /// </summary>
        private HipotBatchResult BuildMockBatch()
        {
            HipotBatchResult b = new HipotBatchResult();
            b.Time = DateTime.Now;
            b.Pass = new bool[mCfg.ChannelCount];
            b.Values = new float[mCfg.ChannelCount];
            b.AllPass = mCfg.ChannelCount > 0;
            for (int i = 0; i < mCfg.ChannelCount; i++)
            {
                b.Pass[i] = true;
                b.Values[i] = mCfg.Model == HipotModel.Gpt9500 ? 5.0e-5f : 9.978f;
            }
            return b;
        }

        /// <summary>
        /// 按需读一次当前结果批（方案三 PLC 握手服务 HipotPlcService 的专用入口，直连
        /// 轮询不调它）。与直连轮询的差别：不走 GPT"RUNNING→STOPPED 完成边沿"状态机
        /// ——方案三下 PLC 已用干接点判定收尾、写握手寄存器（1/2）通知"可以读值"了
        /// 才调进来，直接读批即协议正确；GPT 115=TESTING 竞态（结果落内存延迟）仍由
        /// ReadBatch 内部兜（返回 null），调用方按"等一会重试"处理。
        /// Mock 演示：不碰串口回 BuildMockBatch 模拟批（握手闭环演示用，见该方法注释）。
        /// 返回 null=串口没开/通讯失败/判定不可识别（调用方重试，重试超时按"读值失败"放行）。
        /// </summary>
        public HipotBatchResult ReadBatchOnce()
        {
            if (mDisposed) return null;
            if (MockBridge.IsEnabled) return BuildMockBatch();
            if (!EnsurePortOpen()) { SetConnected(false); return null; }
            SetConnected(true);
            return ReadBatch();
        }

        /// <summary>启动一次测试（仅 GPT 有远程启动语义；SoftwareStart=false 时业务一般不调）。</summary>
        public void StartTest()
        {
            try
            {
                if (mDisposed) return;
                if (MockBridge.IsEnabled) { Log("Mock演示：已下发启动（不接硬件）"); return; }
                string cmd = BuildStartCmd(mCfg.Model);
                if (cmd == null) { Log("该型号无远程启动（GOM），请走仪器面板/硬线触发"); return; }
                string resp;
                if (QueryLine(cmd, out resp)) Log("已下发启动 " + cmd);
                else Log("启动下发失败（串口不通）");
            }
            catch (Exception ex) { Log("启动测试异常：" + ex.Message); }
        }

        public void StopTest()
        {
            try
            {
                if (mDisposed) return;
                if (MockBridge.IsEnabled) { Log("Mock演示：已下发停止（不接硬件）"); return; }
                string cmd = BuildStopCmd(mCfg.Model);
                if (cmd == null) { Log("该型号无远程停止（GOM）"); return; }
                string resp;
                if (QueryLine(cmd, out resp)) Log("已下发停止 " + cmd);
                else Log("停止下发失败（串口不通）");
            }
            catch (Exception ex) { Log("停止测试异常：" + ex.Message); }
        }

        /// <summary>
        /// 页面配置与本实例是否为同一连接目标（测试前比对用，与气密 SameConnectionTarget 同套路）。
        /// 用户在页上改了型号/串口/波特率但还没点保存时，现有连接测的是旧地址，
        /// 必须回落到直连实测页面上的新值——否则"明明改了串口，测的还是老的"。
        /// 只比"连到哪"（型号/串口/波特率）；腔数/轮询周期/软启动不影响连接，
        /// 改了它们也算同一目标（走现有连接测，省一次开关串口）。
        /// </summary>
        public bool SameConnectionTarget(HipottestConfig other)
        {
            try
            {
                if (other == null) return false;
                if (mCfg.Model != other.Model) return false;
                if (!string.Equals((mCfg.ComPort ?? "").Trim(), (other.ComPort ?? "").Trim(),
                    StringComparison.OrdinalIgnoreCase)) return false;
                return mCfg.BaudRate == other.BaudRate;
            }
            catch { return false; }
        }

        /// <summary>
        /// 经已有连接测一次（配置页"测试通讯"优先走这里，与气密 TestViaExistingConnection 同套路）。
        ///
        /// 【为什么不能 new 一个服务来测】串口独占：生产轮询占着 COM 时，测试另建连接 Open
        /// 必报"被占用"，即使仪器是好的——页面永远"测试失败"，换线/换串口全白查。
        /// 本方法只问不连：复用本实例的已有串口发 *IDN?（QueryLine 内部持 mIoLock，
        /// 与轮询线程天然串行，一次测试约几十毫秒，不影响轮询节拍）。
        ///
        /// 未连接（服务刚建还没打开/已 Dispose/串口被拔）返回 false，info 明示，
        /// 调用方（MainForm.TryTestHipotViaExisting）回落到直连实测或提示。
        /// </summary>
        public bool TestViaExistingConnection(out string info)
        {
            info = "";
            try
            {
                string mockInfo;
                if (DeviceServiceBase.MockOk(out mockInfo, "耐压")) { info = mockInfo + "（经现有连接）"; return true; }
                if (mDisposed) { info = "服务已释放"; return false; }
                if (string.IsNullOrWhiteSpace(mCfg.ComPort))
                {
                    info = "串口号未配置，填好并保存后再测";
                    return false;
                }
                string resp;
                if (!QueryLine(BuildIdnCmd(), out resp))
                {
                    info = "经现有连接问询无应答（串口未打开或仪器离线，轮询线程会自动重连）";
                    return false;
                }
                info = GetDebugInfo() + " 仪器应答[" + (resp ?? "").Trim() + "]（经现有连接）";
                return true;
            }
            catch (Exception ex) { info = "测试异常：" + ex.Message; return false; }
        }

        /// <summary>
        /// 一次性连通性测试（配置页"测试通讯"用）：发 *IDN?（SCPI 标准），
        /// 收到任何完整响应行=通。在调用方后台线程里调，内部有界（≤2 秒）。
        /// </summary>
        public override bool TestConnection(out string info)
        {
            info = "";
            try
            {
                string mockInfo;
                if (DeviceServiceBase.MockOk(out mockInfo, "耐压")) { info = mockInfo; return true; }
                if (mDisposed) { info = "服务已停止（未配置或已被热更关闭）"; return false; }
                if (string.IsNullOrWhiteSpace(mCfg.ComPort))
                {
                    info = "串口号未配置，填好并保存后再测";
                    return false;
                }
                if (!EnsurePortOpen())
                {
                    info = "串口 " + mCfg.ComPort + " 打不开（检查线缆/被占用/口号），详见运行日志";
                    SetConnected(false);
                    return false;
                }
                string resp;
                bool ok = QueryLine(BuildIdnCmd(), out resp);
                if (ok)
                {
                    info = GetDebugInfo() + " 仪器应答[" + (resp ?? "").Trim() + "]";
                    SetConnected(true);
                    return true;
                }
                info = "串口已开但仪器无响应（查波特率/交叉线/仪器是否上电），当前 "
                    + mCfg.BaudRate.ToString();
                SetConnected(false);
                return false;
            }
            catch (Exception ex) { info = "测试异常：" + ex.Message; return false; }
        }

        /// <summary>监听状态摘要（配置页状态栏用）。绝不抛异常。</summary>
        public override string GetDebugInfo()
        {
            try
            {
                // Mock演示：状态摘要直接报演示连通（配置页状态行跟着变，不碰串口）。
                if (MockBridge.IsEnabled)
                    return "Mock演示：" + ModelToText(mCfg.Model) + " " + mCfg.ComPort + "（已打开，" + mCfg.ChannelCount.ToString() + " 腔，不接硬件）";
                bool open;
                lock (mIoLock) { open = mPort != null && mPort.IsOpen; }
                return ModelToText(mCfg.Model) + " " + (mCfg.ComPort == "" ? "(串口未配置)" : mCfg.ComPort)
                    + "@" + mCfg.BaudRate.ToString() + "（" + (open ? "已打开" : "未打开")
                    + "，" + mCfg.ChannelCount.ToString() + " 腔）";
            }
            catch { return "未知"; }
        }
    }
}
