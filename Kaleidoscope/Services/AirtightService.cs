// =====================================================================================
// 【下沉注记】本文件 2026-10-25 自 HJVision-WPF 生产工程（第二轮仪器设备下沉）
// 原样搬入，口径与第一轮 PlcNetCommQue/TcpChannel 下沉相同：
//   ①来源 GYZVision.Core/Devices/AirtightService.cs（气密双分支服务）；
//   ②命名空间 → Kaleidoscope.Services；
//   ③演示模式出口 MockMode.IsEnabled → MockBridge.IsEnabled（7 处，宿主静态构造接线）；
//   ④PLC 分支静态委托（PlcWriteBool/PlcReadInt）签名不变，宿主适配器接线照旧；
//   ⑤基类 DeviceServiceBase 已同轮下沉库内，继承关系不变；HslCommunication 库已引。
//   其余算法/默认值/异常口径/注释全保留，公共 API 签名不变（主项目经
//   文件级 using 别名壳零改动引用，断点可跨工程打进本源码）。
// =====================================================================================
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading;
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.ModBus;
using Kaleidoscope.Utils;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 气密测试服务（双分支二选一，INI 的 Mode 切换，不改代码）。
    // 对标原版 SubClass/AirtightService.cs（V4.4.113 口径）一比一搬入；
    // 2026-10-25 同步增量：结果邮箱（Seq+克隆）/ 独立·统一启动 / 凑批等待 /
    // 上位机启动检查握手（173-176/251/161-164）/ 完成信号→通知 PLC 延时队列 /
    // 只发OK/NG / 结果强制OK / ModbusTCP 0 基址修复 / 双四通道"先1后2"完成边沿判据 /
    // 泄漏状态 178-181 / 读实时快照（配置页"读取实时数据"）。
    // -------------------------------------------------------------------------------------
    // 背景：客户给了两种气密仪资料，现场到底用哪台开工前未定，所以两个分支都写：
    //   IoViaPlc     = 旧款 IO 型（16 针航空插：干接点启动 + 24V 结果电平）。
    //                  工控机不直连仪器：启动经 PLC 队列写线圈给 PLC（PLC 经继电器
    //                  转干接点给仪器），结果由 PLC 锁存到 D 寄存器，工控机经队列读回来。
    //                  只能拿 OK/NG，拿不到泄漏值/压力数值（仪器就没有串口）。
    //   ModbusDirect = 希立款（Modbus TCP/RTU 直连工控机）。能拿结果 + 稳压压力 +
    //                  泄漏值 + 泄漏状态（178-181，双四通道）+ 条码；条码：当前项目
    //                  气密仪没有扫码动作，产品条码复用产线 SN（随 TrackId 流转绑定
    //                  结果），仪器 125 条码寄存器不读（单通道版 BarcodeEnabled 开关
    //                  保留，仪器扫码工作流备用）；双四通道启动分独立/统一两种
    //                  （线圈3/5/7/9 独立，线圈1 统一，见 AirtightConfig.IndependentStart）。
    // 【事件契约】两分支统一抛 ResultReady（整批结果，通道数=配置 ChannelCount），
    // 但事件只做监视（日志/状态页），不做业务关联——事件里没有 TrackId，按它记账必串号。
    // 业务闭环走"启动-邮箱"：脚本下发启动（非阻塞；独立模式调 TryBeginTest(腔号) 写
    // 分通道线圈，统一模式调 TryBeginUnifiedTest 写一次线圈1、一批两腔认领），结果进邮箱
    // （序号+克隆），脚本凭序号+时间按请求 ID 认领（见 mBatchLock 注释）。
    // 换仪器改配置即切，业务侧不用动。
    // 【线程】常驻后台轮询线程 + 双层 try-catch（内层单次隔离，外层保线程不死），
    // 关窗 Dispose 先置旗再关句柄再限时 Join（与 LaserMarkService 同套路）。
    // 【与原版的唯一结构差异】IO 分支直调 PlcNetComm_Que 改为静态委托注入
    // （PlcWriteBool 启动脉冲 / PlcReadInt 同步读字，Core 禁直引业务队列——见文件头
    // 旧版迁移注释），Mock 由适配器侧处理，服务层语义不变。其余一比一。
    // =====================================================================================
    public enum AirtightMode
    {
        IoViaPlc,
        ModbusDirect
    }

    /// <summary>单通道判定（三态：未知=还没出结果/通讯中断时保持）。</summary>
    public enum AirtightChannelState
    {
        Unknown,
        Pass,
        Fail
    }

    /// <summary>Modbus 分支仪器型号（寄存器布局见手册 xls）。</summary>
    public enum AirtightModbusModel
    {
        Single,     // 单通道：线圈1启/2停/4完毕，结果39，压力40，泄漏42，条码44-51
        DualQuad    // 双四通道：线圈1统一启/2统一停、3/5/7/9分通道启、4/6/8/10分通道停；
                    // 165-168完毕（0空闲/1测试中/2完毕，1→0=异常停），结果105-108，
                    // 压力109-115，泄漏117-123；心跳线圈11（5秒内必写一次）
    }

    /// <summary>一批测试结果（一次完成边沿产生一批，通道按下标 0..N-1）。</summary>
    public class AirtightBatchResult
    {
        public long Seq;                            // 批次序号（服务内单调递增；脚本凭它+时间判定"新批次"，防捡上一轮剩数）
        public DateTime Time;
        public AirtightChannelState[] States = new AirtightChannelState[0];
        public float[] Pressures = new float[0];   // 稳压压力（IO 分支恒 NaN：仪器给不出）
        public float[] Leaks = new float[0];       // 泄漏值（同上）
        // 泄漏值状态（手册 178-181，仅双四通道有：0未超限/1微漏/2超上下限大漏/3保压压力波动超范围）。
        // -1=未读到/不适用（单通道与 IO 分支没有该寄存器；双四通道未完毕通道按业务门控不读）。
        // 业务门控同结果/压力/泄漏：只填完毕通道，未完毕保持 -1——残留状态会误导 NG 归因。
        public int[] LeakStates = new int[0];
        public string[] Barcodes = new string[0];  // 条码（读不到/未配恒空串）
        public bool AllPass;                        // 全部通道 Pass 才 true（含 0 通道=false）
    }

    /// <summary>
    /// 气密"完成信号→通知 PLC"延时队列（纯逻辑件，无设备依赖，可被回归探针直测）。
    ///
    /// 【为什么要有它】气密仪测完会立刻把"完成"信号交给上位机，但现场工装要求：
    /// 收到完成信号后先等 N 毫秒（缺省 2 秒）再回执 PLC——给工件取出/机构复位留时间。
    /// 延时的计时起点必须是"收到完成信号那一刻"：此刻仪器的结果寄存器刚定值，
    /// 晚读可能已被下一轮测试覆盖（读到别人的结果）。所以完成信号一到就把结果整批
    /// 快照下来，延后的是"通知"，不是"取数"。
    ///
    /// 【职责边界】本类只管"什么时候可以通知"，不管数据从哪来：
    ///   ①收到完成信号 → Enqueue(快照批次, 信号到达时刻)，到期时刻=到达时刻+延时；
    ///   ②轮询线程每 50ms → TakeDue(当前时刻)，到期批次交给服务投进邮箱，
    ///     脚本按序号认领后回执 PLC（对应原版脚本 PollAirtightSlot）。
    /// 延时=0 时行为与加此特性前完全一致（入队即到期，同一轮刷新就发出去）。
    ///
    /// 【为什么要单独成类】"延后多少才通知"极易改错（起点取错/数据晚读被覆盖/时序没对齐），
    /// 做成纯逻辑件后可脱离硬件单测（回归探针 AirtightMesProbe M8 组）。
    /// </summary>
    public class AirtightNotifyQueue
    {
        /// <summary>缺省延时毫秒（现场要求 2 秒）。</summary>
        public const int DefaultDelayMs = 2000;
        /// <summary>延时上限毫秒（防手改 INI 写个几小时=永远不回执 PLC）。</summary>
        public const int MaxDelayMs = 60000;

        /// <summary>排队中的一条（批次快照 + 到期时刻）。</summary>
        private class Item
        {
            public AirtightBatchResult Batch;
            public DateTime DueTime;
        }

        private readonly List<Item> mItems = new List<Item>();
        private readonly object mLock = new object();
        private int mDelayMs = DefaultDelayMs;

        /// <summary>排队延时毫秒（0=不延时，收到完成信号即通知）。</summary>
        public int DelayMs
        {
            get { lock (mLock) return mDelayMs; }
            set { lock (mLock) mDelayMs = ClampDelay(value); }
        }

        /// <summary>队列里待通知的批次数（状态栏/探针用）。</summary>
        public int PendingCount
        {
            get { lock (mLock) return mItems.Count; }
        }

        /// <summary>延时钳制：负数→0（不延时），超上限→MaxDelayMs。</summary>
        public static int ClampDelay(int ms)
        {
            if (ms < 0) return 0;
            if (ms > MaxDelayMs) return MaxDelayMs;
            return ms;
        }

        /// <summary>
        /// 排一条待通知批次（收到完成信号时调，now=信号到达时刻）。
        /// 返回排队后的条数。延时 0 时到期时刻=now，调用方随即 TakeDue 即可立刻发出。
        /// </summary>
        public int Enqueue(AirtightBatchResult batch, DateTime now)
        {
            if (batch == null) return PendingCount;
            lock (mLock)
            {
                mItems.Add(new Item { Batch = batch, DueTime = now.AddMilliseconds(mDelayMs) });
                return mItems.Count;
            }
        }

        /// <summary>
        /// 取到期批次（轮询线程每轮调，now=当前时刻）。按入队顺序返回（先后不乱）。
        /// 没到期的一条不取（继续等），到期的一次全取（不丢批）。
        /// </summary>
        public List<AirtightBatchResult> TakeDue(DateTime now)
        {
            List<AirtightBatchResult> due = new List<AirtightBatchResult>();
            lock (mLock)
            {
                for (int i = 0; i < mItems.Count; )
                {
                    if (mItems[i].DueTime <= now)
                    {
                        due.Add(mItems[i].Batch);
                        mItems.RemoveAt(i);
                    }
                    else i++;
                }
            }
            return due;
        }

        /// <summary>清空队列（服务停机/热更时调：残留批次已无主，丢弃）。</summary>
        public void Clear()
        {
            lock (mLock) mItems.Clear();
        }
    }

    public class AirtightConfig
    {
        public AirtightMode Mode = AirtightMode.IoViaPlc;
        public int ChannelCount = 2;        // 2=双通道 / 4=四通道（IO 四通道=单线高低模式，见下）
        public int PollMs = 200;            // 轮询周期（希立 DOC 建议 200ms，太快仪器反应不过来）

        // ---- IO 分支（PLC 中转）----
        // 约定（PLC 程序按此实现，配错=批量误判，配置页悬停里同样写明）：
        //   启动：工控机写 StartCoil=true 300ms 后写 false（脉冲，PLC 转 1 秒干接点给仪器）；
        //   完成：CompleteWord 非 0=本批完成（边沿触发，只认 0→非0，防重复读）；
        //   结果：ResultWord 按位，bit(i)=1 表示通道 i+1 OK（双通道只看低 2 位；
        //         四通道 IO 模式仪器是"单线高低"，PLC 把 4 根线拼成 4 位后放这里）。
        public int PlcDevId = 0;            // PLC 设备号
        public string StartCoil = "M500";   // 启动线圈地址（HSL 地址串，按 PLC 品牌写，如 M500/D0.0）
        public string CompleteWord = "D500";// 完成字地址（Int16）
        public string ResultWord = "D501";  // 结果字地址（Int16，按位）

        // ---- Modbus 分支（希立直连）----
        public string ModbusTransport = "TCP";  // TCP / RTU
        public string ModbusIp = "192.168.88.88";
        public int ModbusPort = 502;
        public byte ModbusStation = 1;      // 从站号（DOC 默认 1）
        public string ModbusCom = "COM1";   // RTU 串口
        public int ModbusBaud = 115200;     // RTU 波特率（仪器默认 115200）
        public AirtightModbusModel ModbusModel = AirtightModbusModel.DualQuad;
        // 独立启动（手册线圈表：3=通道1启动、5=通道2启动、7/9=通道3/4启动，1=统一启动、2=统一停止）。
        // true=分通道独立启动（ST14-1 只发通道1，ST14-2 只发通道2，天然并行不串扰；
        // 要求仪器端启动模式为"独立启动"，出厂默认一般即此）；false=统一启动（线圈1，
        // 两腔同测，脚本侧按整机忙排队——仅仪器端设成"统一启动"模式时用）。
        public bool IndependentStart = true;
        // 统一启动凑批等待（毫秒；仅 IndependentStart=false 时脚本用）：整机空闲后等另一腔
        // 就位的最长时间——两腔都有在途立即启动；单腔在途超该时间后先行（空腔空测一轮）。
        // 0=不等凑批，有在途就启动。连续流产线建议 2000~5000；单腔生产设 0。默认 3000。
        public int UnifiedBatchWaitMs = 3000;
        // 上位机启动检查握手（手册 173-176/251；默认 false=不握手）。
        // false=仪器"上位机启动检查"开关关闭（出厂默认，启动线圈写完即测，本软件默认走此路）；
        // true=仪器端开关已打开，此时光写启动线圈仪器只会把 173-176 置 1 干等，
        // 服务在轮询线程里等开启通道变 1 后逐个写 0，仪器才真启动（"检验"内容手册未定义，
        // 本软件做无脑通过）。只在 ModbusDirect + DualQuad 下有效。
        // 现场：先读 251 确认仪器开关状态再拨本键，两边必须一致；配错（本键 ON 但 251=0）
        // 服务记告警并按无握手走，不卡死。料号 Comm [Airtight] PcStartCheck=ON/OFF。
        public bool PcStartCheck = false;
        public bool BarcodeEnabled = true;  // 读条码（单通道版；双四通道暂只读结果/压力/泄漏）
        public string BarcodeEncoding = "UTF8"; // UTF8 / UNICODE / GB2312（单通道 DOC 标 utf-8）
        // 结果强制OK（测试用）（联调开关，默认 false=按仪器实测判；true=仪器 FAIL 也按 OK 回 PLC，
        // 泄漏/压力仍记实测值。超时（仪器无结果）不受影响，照常回异常可重发——
        // 断线也判 OK 会掩盖真故障，所以超时不强制。配置页复选框，保存即热更。
        // 2026-09-30 改语义：启动装载与配置页打开都不再读 INI、一律 false——开关只活在
        // 本次会话（勾选→保存→热更生效，重启自动回不勾），防现场配 ON 忘关带进生产。
        // 料号 Comm [Airtight] 的 ForcePass 键仅作留痕，装载侧已忽略。）
        public bool ForcePass = false;
        // 只发OK/NG（默认 true=勾选：超时也按 NG 回 OKNG=2，不再回 3；PLC 只认 1/2，
        // 不用改 PLC 程序。false=不勾：保持旧逻辑，超时回 3（异常可重发）。
        // 超时永不受 ForcePass 强制（断线判 OK 会掩盖真故障）。配置页复选框"只发OK/NG"，
        // 保存即热更。料号 Comm [Airtight] OkNgOnly=ON/OFF，缺省 ON。只影响 ST14 两腔，
        // 其它站的 3 保持不动。）
        public bool OkNgOnly = true;
        // 完成信号→通知 PLC 的延时（毫秒；缺省 2000=2 秒，0=收到即回执）。
        // 【口径】计时起点=收到仪器完成信号那一刻（此刻结果寄存器刚定值，快照后晚点再发，
        // 见 AirtightNotifyQueue 注释）；终点=批次投进邮箱、脚本认领后回执 PLC 的握手字。
        // 为什么可配：现场工装要求工件取出/机构复位后再让 PLC 推进，节拍随工装改。
        // 料号 Comm [Airtight] NotifyDelayMs=毫秒，配置页"通知PLC延时ms"可改，保存即热更。
        // 只影响"收到结果后的回执时刻"，不影响超时回 NG（超时按 120s 兜底，不走本延时）。
        public int NotifyDelayMs = AirtightNotifyQueue.DefaultDelayMs;
    }

    public class AirtightService : DeviceServiceBase<AirtightConfig>
    {
        /// <summary>
        /// PLC 布尔写注入（slot, address, values, devId）。宿主接 PLC 队列的脉冲写；
        /// 未注入时 IO 分支启动脉冲静默跳过（只记日志，不抛异常）。
        /// </summary>
        public static Action<int, string, bool[], int> PlcWriteBool;

        /// <summary>
        /// PLC 整字同步读注入（slot, address, devId）→ 原始文本；失败/超时回 null。
        /// 宿主接 PLC 队列（入队→3 秒上限等完成→取槽位）；测试注入内存假值。
        /// </summary>
        public static Func<int, string, int, string> PlcReadInt;

        /// <summary>
        /// PLC 队列读结果槽位：借相机槽之外的下标 10（与相机采集读数抢槽会串数）。
        /// 槽位占用现状（原版 PlcNetComm_Que._mReadResultData，14 槽）：0~9=相机、10=本服务、
        /// 11=耐压握手套1、12=脚本垃圾槽、13=耐压握手套2——各设备各占一槽，共槽读会串台。
        /// 为什么不用枚举加成员：显示相机列表键会被显示设置页拿去建下拉，加成员会污染；
        /// 这里用裸下标 + 强注释。
        /// </summary>
        internal const int PlcSlotIndex = 10;

        /// <summary>一批结果就绪（后台轮询线程触发，订阅方封送回业务线程，内部已吞异常）。</summary>
        public event Action<AirtightBatchResult> ResultReady;

        /// <summary>连接状态变化（true=通讯正常，false=中断）。边沿触发。</summary>
        public event Action<bool> ConnectionChanged;

        private Thread mPollThread;

        /// <summary>
        /// Modbus 收发互斥锁（V4.4.35：希立气密仪是单连接服务器，测试必须复用本连接；
        /// 轮询线程（200ms 一轮）与配置页测试线程共用 mModbusTcp/mModbusRtu 时必须串行，
        /// 否则两个请求的应答字节流交错、双双错位。凡是直接碰 mModbusTcp/mModbusRtu
        /// 收发原语的方法（读寄存器/读浮点/写线圈/单通道读线圈/条码读字节）一律在锁内执行；
        /// Open/Close/重连走服务启停与轮询线程，不与测试并发。
        /// </summary>
        private readonly object mModbusLock = new object();

        // IO 分支边沿记忆：上次完成字（只认 0→非0，维持期间不重复抛批）
        private int mLastComplete;
        // Modbus 分支边沿记忆：上次完毕字（单通道=线圈4，双四通道=165-168）
        private readonly int[] mLastModbusDone = new int[4];
        private DateTime mLastHeartbeat = DateTime.MinValue;

        // ---- 结果邮箱（ST14 双腔闭环用，脚本 PollAirtight 消费）----
        // 背景：脚本轮询线程 50ms 一轮要管 15 个站，绝不能阻塞等气密测完（充气/保压/检测
        // 几十秒）。所以启动与消费分离：脚本下发启动即返（独立模式 TryBeginTest(腔号) 写
        // 分通道线圈，统一模式 TryBeginUnifiedTest 写一次线圈1，见两方法注释）；
        // 服务轮询线程照常读完毕字，出结果进邮箱（序号+整批克隆）；脚本每轮凭"序号>触发时刻序号
        // 且批次/启动时刻>=触发时刻"认领自己那一轮的新批次（防捡上一轮剩数）。
        private readonly object mBatchLock = new object();
        private long mBatchSeq;                    // 已发布批次数（单调递增，脚本启动时快照它）
        private AirtightBatchResult mLastBatch;    // 最新一批克隆（无结果时 null）
        private DateTime mStartCooldownUntil = DateTime.MinValue;   // 启动后短暂视为忙（防仪器还没转"测试中"就连发）
        private DateTime mLastStart = DateTime.MinValue;   // 上次下发启动时刻（脚本判定"新批次"用：只消费启动于本触发之后的轮次）。
            // 时钟约定（V4.4.103 血泪）：必须用本地 Now，与邮箱批次 Time（Now）、脚本触发时刻
            // slotTrig（Now）同一时钟。V4.4.94~V4.4.102 原版这里曾用 UtcNow，北京时间下比 Now
            // 慢 8 小时，导致脚本 isNewRun 门控恒为假，ST14 永远认领不到、PLC 先报完成超时。
        private bool mMockPending;                 // Mock：一轮待完成（TryBeginTest 置位，消费后清零）
        private DateTime mMockStartUtc;            // Mock：本轮启动时刻（2 秒后"出结果"）
        private bool mUnifiedStarted;              // 统一启动本轮已下发（TryBeginUnifiedTest 置位，发布新批清零，防两腔各写一次线圈1）
        // ---- 上位机启动检查握手状态（仅 PcStartCheck=true 时用，见 BeginPcCheck/PumpPcCheck）----
        // 背景：仪器开关打开后，写启动线圈≠真启动——仪器先把开启通道的 173-176 置 1，
        // 等上位机写 0 才跑流程。本软件"检验"无脑通过（手册未定义检验内容），只做握手。
        // 非阻塞：TryBeginTest 下发启动后只置位，推进在 200ms 轮询线程里做；
        // 超时 5 秒放弃（记告警，脚本 120s 超时兜底，不断线）。
        private bool mPcCheckPending;              // 握手进行中（等 173 变 1 写 0）
        private DateTime mPcCheckUntil;            // 握手超时时刻（置位时 Now+5 秒）
        private bool[] mPcCheckMask;               // 要握手的通道（161-164 读到的开启掩码，下标=通道-1）

        private ModbusTcpNet mModbusTcp;
        private ModbusRtu mModbusRtu;

        /// <summary>
        /// 完成信号→通知 PLC 的延时队列（收到完成信号时快照排队，到期才投进邮箱）。
        /// 见 AirtightNotifyQueue 类注释（口径/为什么快照/为什么单独成类）。
        /// </summary>
        private readonly AirtightNotifyQueue mNotifyQueue = new AirtightNotifyQueue();

        public AirtightService(AirtightConfig cfg)
            : base(cfg)
        {
            if (mCfg.ChannelCount < 1) mCfg.ChannelCount = 1;
            if (mCfg.ChannelCount > 4) mCfg.ChannelCount = 4;
            if (mCfg.PollMs < 50) mCfg.PollMs = 50;
            if (mCfg.PollMs > 5000) mCfg.PollMs = 5000;
            // 延时钳制（负数=不延时，超上限=最多 60 秒；手改 INI 写离谱值不会把回执永远卡住）
            mCfg.NotifyDelayMs = AirtightNotifyQueue.ClampDelay(mCfg.NotifyDelayMs);
            mNotifyQueue.DelayMs = mCfg.NotifyDelayMs;
        }

        /// <summary>解析模式（非法回落 IoViaPlc：不断线，宁可用老路）。</summary>
        public static AirtightMode ParseMode(string name, AirtightMode fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            string n = name.Trim().ToUpperInvariant();
            if (n == "IO" || n == "IOVIAPLC" || n == "PLC") return AirtightMode.IoViaPlc;
            if (n == "MODBUS" || n == "MODBUSDIRECT" || n == "XILI" || n == "希立") return AirtightMode.ModbusDirect;
            return fallback;
        }

        public static string ModeToText(AirtightMode mode)
        {
            return mode == AirtightMode.ModbusDirect ? "MODBUS" : "IO";
        }

        public static AirtightModbusModel ParseModel(string name, AirtightModbusModel fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            string n = name.Trim().ToUpperInvariant();
            if (n == "SINGLE" || n == "单通道" || n == "1") return AirtightModbusModel.Single;
            if (n == "DUALQUAD" || n == "DUAL" || n == "QUAD" || n == "双通道" || n == "四通道" || n == "2" || n == "4")
                return AirtightModbusModel.DualQuad;
            return fallback;
        }

        // ================= 启动 / 停止 =================

        /// <summary>启动轮询线程（异常只记日志不抛，不挡主程序启动）。</summary>
        public override void Start()
        {
            try
            {
                if (mCfg.Mode == AirtightMode.ModbusDirect) OpenModbus();
                mPollThread = new Thread(PollLoop) { IsBackground = true, Name = "AirtightPoll" };
                mPollThread.Start();
                Log("服务已启动 模式=" + ModeToText(mCfg.Mode) + " 通道=" + mCfg.ChannelCount
                    + " 通知延时=" + mCfg.NotifyDelayMs + "ms（收到完成信号后延这么久才回执 PLC）");
            }
            catch (Exception ex)
            {
                Log("启动失败：" + ex.Message);
            }
        }

        public override void Dispose()
        {
            mDisposed = true;
            JoinQuietly(mPollThread, 1500);
            try
            {
                // 待通知批次随服务一起丢（服务没了批次也没主了；对应在件由脚本按 120s 超时回 NG）
                int n = mNotifyQueue.PendingCount;
                if (n > 0) Log("服务停止，待通知批次丢弃 " + n + " 条（对应在件将按超时回 NG）");
                mNotifyQueue.Clear();
            }
            catch { }
            CloseModbus();
            SetConnected(false);
        }

        private void LogErrThrottled(string msg)
        {
            // 同一种通讯异常 10 秒只记一次（200ms 轮询下不记节流=日志爆炸）；节流计时在基类
            LogThrottled(msg, 10);
        }

        /// <summary>日志前缀（与原版逐字一致：时间戳 + " 气密[模式]"）。</summary>
        protected override string FormatLog(string msg)
        {
            return DateTime.Now + " 气密[" + ModeToText(mCfg.Mode) + "]" + msg;
        }

        private void SetConnected(bool value)
        {
            if (mConnected == value) return;
            mConnected = value;
            try
            {
                Log(value ? "通讯恢复" : "通讯中断，后台自动重连…");
                if (ConnectionChanged != null) ConnectionChanged(value);
            }
            catch { }
        }

        /// <summary>监听状态摘要（配置页"测试通讯"与状态栏用，绝不抛异常）。</summary>
        public override string GetDebugInfo()
        {
            try
            {
                // Mock演示：状态摘要直接报演示连通（配置页状态行/测试文案跟着变绿，不读硬件）。
                if (MockBridge.IsEnabled)
                    return "Mock演示：" + (mCfg.Mode == AirtightMode.ModbusDirect ? "Modbus直连已连接" : "IO经PLC读写正常") + "（不接硬件）";
                string force = (mCfg != null && mCfg.ForcePass) ? "（结果强制OK）" : "";
                // 通知延时也报出来（现场确认"收到完成信号后多久回 PLC"直接看状态行）
                string delay = " 通知延时" + (mCfg.NotifyDelayMs / 1000.0).ToString("0.#",
                    System.Globalization.CultureInfo.InvariantCulture) + "s";
                if (mCfg.Mode == AirtightMode.ModbusDirect)
                    return "Modbus" + mCfg.ModbusTransport + " "
                        + (mCfg.ModbusTransport == "RTU" ? mCfg.ModbusCom : mCfg.ModbusIp + ":" + mCfg.ModbusPort)
                        + "（" + (mConnected ? "已连接" : "未连接") + "）" + force + delay;
                return "IO经PLC 启动" + mCfg.StartCoil + "/完成" + mCfg.CompleteWord
                    + "/结果" + mCfg.ResultWord + "（" + (mConnected ? "读写正常" : "未连接") + "）" + force + delay;
            }
            catch { return "未知"; }
        }

        // ================= 对外动作 =================

        /// <summary>
        /// 下发启动并认领（ST14 双腔闭环用，脚本 PollAirtight 在触发边沿调，非阻塞即返）。
        /// channel：要启动的通道（1 起；ST14-1 传 1，ST14-2 传 2；越界钳到有效范围）。
        /// Modbus 分支：独立启动模式写分通道线圈（DualQuad：通道1→3、通道2→5、通道3→7、
        /// 通道4→9，手册线圈表）；统一启动模式（IndependentStart=false）写线圈 1；
        /// 单通道版恒写线圈 1。IO 分支=给 PLC 发 300ms 启动脉冲（PLC 转干接点给仪器）。
        /// 返回 true=启动已下发（调用方进入"等结果"态，每轮查邮箱）；
        /// 返回 false=对应通道忙/未连接（调用方不报错、不重发启动，继续等，超时再判异常可重发）。
        /// 忙的定义（Modbus）：本通道在测（完毕字=1）或整机刚启动 3 秒内（仪器还没翻成"测试中"，
        /// 防连发把在测的一轮重启；3 秒冷却全机共享，独立模式下异腔连启最多等一轮 50ms）。
        /// IO 分支恒可下发（仪器侧自仲裁），返回恒 true。
        /// 绝不抛异常。
        /// </summary>
        public bool TryBeginTest(int channel)
        {
            try
            {
                if (mDisposed) return false;
                if (MockBridge.IsEnabled && mCfg.Mode == AirtightMode.ModbusDirect)
                {
                    lock (mBatchLock)
                    {
                        mMockPending = true;   // Mock：一轮待完成（2 秒后 GetLastBatch 吐假 PASS 批）
                        mMockStartUtc = DateTime.UtcNow;
                        mLastStart = DateTime.Now;   // 本地时钟（见字段注释：须与 slotTrig/batch.Time 同钟，UtcNow 会卡死认领）
                        mStartCooldownUntil = DateTime.Now.AddSeconds(3);
                    }
                    Log("Mock演示：已下发启动（不接硬件，2 秒后出模拟结果）");
                    return true;
                }
                if (mCfg.Mode == AirtightMode.ModbusDirect)
                {
                    if (!ModbusAlive()) return false;
                    if (IsChannelTesting(channel)) return false;   // 本通道忙：调用方等下轮再试，不连发
                    string coil = StartCoilFor(channel);
                    if (!ModbusWriteCoil(coil, true)) { Log("启动测试下发失败（线圈" + coil + "）"); return false; }
                    ResetChannelEdgeMemory(channel);   // 只清本通道（跨腔互清会吃掉另一腔的完成边沿，见方法注释）
                    lock (mBatchLock) { mLastStart = DateTime.Now; mStartCooldownUntil = DateTime.Now.AddSeconds(3); }   // 本地 Now
                    Log("已下发启动（通道" + channel + "，线圈" + coil + "）");
                    // 仪器若开了"上位机启动检查"，光写线圈仪器只会置 173-176=1 干等，
                    // 这里触发握手（等变 1 写 0），推进在轮询线程；开关关着时 BeginPcCheck 直接返回。
                    BeginPcCheck();
                    return true;
                }
                // IO 分支：脉冲 fire-and-forget（PLC 侧转 1 秒干接点给仪器，见类注释约定）
                lock (mBatchLock) { mLastStart = DateTime.Now; }   // 本地 Now
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        Action<int, string, bool[], int> w = PlcWriteBool;
                        if (w == null) { Log("启动脉冲未下发（PLC 写未注入）"); return; }
                        w(PlcSlotIndex, mCfg.StartCoil, new bool[] { true }, mCfg.PlcDevId);
                        Thread.Sleep(300);
                        if (!mDisposed)
                            w(PlcSlotIndex, mCfg.StartCoil, new bool[] { false }, mCfg.PlcDevId);
                        mLastComplete = 0;   // 新一轮：完成边沿重新武装
                        Log("已下发启动脉冲 " + mCfg.StartCoil);
                    }
                    catch (Exception ex) { Log("启动脉冲下发异常：" + ex.Message); }
                });
                return true;
            }
            catch (Exception ex) { Log("启动测试异常：" + ex.Message); return false; }
        }

        /// <summary>
        /// 下发统一启动（一批测完两腔，脚本统一模式用，非阻塞即返）。
        /// 为什么不能让两腔各调 TryBeginTest：统一启动（线圈1）测的是所有开启通道，
        /// 腔1那一轮其实已经把腔2的件测了——若腔2槽再写一次线圈1，同件测两轮，
        /// 浪费一倍节拍。所以统一模式一次只允许写一次线圈1，两腔认领同一批。
        /// 本方法=防重发外壳：本轮已下发返回 false（调用方静等结果）；整机空闲才调
        /// TryBeginTest(1)（统一模式下它本来就写线圈1+按整机忙），成功后置位。
        /// 发布新批（PublishBatch/Mock 造批）清零。Mock/IO 语义随 TryBeginTest(1)。绝不抛异常。
        /// </summary>
        public bool TryBeginUnifiedTest()
        {
            try
            {
                if (mDisposed) return false;
                lock (mBatchLock) { if (mUnifiedStarted) return false; }
                if (!TryBeginTest(1)) return false;
                lock (mBatchLock) { mUnifiedStarted = true; }
                Log("已下发统一启动（一批，两腔认领同一批）");
                return true;
            }
            catch (Exception ex) { Log("统一启动异常：" + ex.Message); return false; }
        }

        /// <summary>
        /// 上位机启动检查握手·发起（TryBeginTest 写启动线圈成功后调；只在 ModbusDirect +
        /// DualQuad + PcStartCheck=true 时干活，其它情况直接返回不置位）。
        /// 流程（手册 173-176/251/161-164）：读 251 确认仪器开关真开了（=1）→ 读 161-164
        /// 取开启通道掩码 → 置 mPcCheckPending，推进交给轮询线程 PumpPcCheck（等变 1 写 0）。
        /// fail-open：251 读失败/≠1、掩码全关，都只记日志不置位（按无握手走，不卡启动；
        /// 配错键的告警看日志）。"检验"无脑通过——手册未定义检验内容，若厂家要求真检验
        /// （如先确认 169-172 扫码完成），在此处加判定再置位。在脚本 50ms 线程里执行，
        /// 3 次寄存器读（251/161 起 n 个），低频操作可接受。绝不抛异常。
        /// </summary>
        private void BeginPcCheck()
        {
            try
            {
                if (mDisposed) return;
                if (!mCfg.PcStartCheck || mCfg.Mode != AirtightMode.ModbusDirect
                    || mCfg.ModbusModel != AirtightModbusModel.DualQuad) return;
                ushort[] sw;
                if (!ModbusReadRegs("251", 1, out sw))
                { Log("上位机检查握手未发起（251 读失败，按无握手走；若仪器开关开着，本轮会超时）"); return; }
                if (sw == null || sw.Length == 0 || sw[0] != 1)
                { Log("PcStartCheck=ON 但仪器 251=" + (sw == null || sw.Length == 0 ? "?" : sw[0].ToString()) + "（开关没开），按无握手走"); return; }
                int n = mCfg.ChannelCount;
                if (n < 1) n = 1;
                if (n > 4) n = 4;
                ushort[] ch;
                if (!ModbusReadRegs("161", n, out ch) || ch == null || ch.Length < n)
                { Log("上位机检查握手未发起（161-164 通道开关读失败，按无握手走）"); return; }
                bool[] mask = new bool[n];
                bool any = false;
                for (int i = 0; i < n; i++) { mask[i] = ch[i] == 1; if (mask[i]) any = true; }
                if (!any) { Log("上位机检查握手未发起（161-164 全关，无通道可测）"); return; }
                lock (mBatchLock)
                {
                    mPcCheckMask = mask;
                    mPcCheckUntil = DateTime.Now.AddSeconds(5);
                    mPcCheckPending = true;
                }
                Log("上位机检查握手已发起（等 173-176 变 1 写 0，5 秒超时）");
            }
            catch (Exception ex) { Log("上位机检查握手发起异常（按无握手走）：" + ex.Message); }
        }

        /// <summary>
        /// 上位机启动检查握手·推进（PollModbus 每轮调；非阻塞，一轮只做一小步）。
        /// 读 173-176：开启通道全变 1 → 逐个写 0 → 清 pending（仪器真启动，后续完毕字照常出）；
        /// 写 0 失败留待下轮（pending 保持，超时兜底）；超 5 秒还没全 1 → 清 pending 记告警
        /// （仪器可能没收到启动，上层脚本 120s 超时兜底）。关闭通道不碰（手册：不用给它写 0）。
        /// 绝不抛异常。
        /// </summary>
        private void PumpPcCheck()
        {
            try
            {
                if (mDisposed) return;
                bool[] mask;
                lock (mBatchLock)
                {
                    if (!mPcCheckPending) return;
                    if (DateTime.Now > mPcCheckUntil)
                    {
                        mPcCheckPending = false;
                        Log("上位机检查握手超时（5 秒内 173-176 未全变 1，放弃；查仪器是否收到启动/251 开关）");
                        return;
                    }
                    mask = mPcCheckMask;
                }
                if (mask == null || mask.Length == 0) { lock (mBatchLock) { mPcCheckPending = false; } return; }
                ushort[] st;
                if (!ModbusReadRegs("173", mask.Length, out st) || st == null || st.Length < mask.Length)
                    return;   // 读失败下轮再试（不刷屏，超时兜底会说话）
                for (int i = 0; i < mask.Length; i++)
                {
                    if (!mask[i]) continue;   // 关闭通道不碰
                    if (st[i] != 1) return;   // 还没全变 1，等下轮
                }
                for (int i = 0; i < mask.Length; i++)
                {
                    if (!mask[i]) continue;
                    if (!ModbusWriteRegs((173 + i).ToString(), new ushort[] { 0 }))
                        return;   // 写失败下轮重试（pending 保持）
                }
                lock (mBatchLock) { mPcCheckPending = false; }
                Log("上位机检查握手完成（173-176 已写 0，仪器开始测试）");
            }
            catch (Exception ex) { Log("上位机检查握手推进异常已隔离：" + ex.Message); }
        }

        /// <summary>
        /// 启动一次测试（旧入口，现为 TryBeginTest(1) 的 void 包装：手动点启动默认测通道1。
        /// 脚本闭环请调 TryBeginTest(channel)，看返回值做排队）。
        /// </summary>
        public void StartTest()
        {
            try { if (!TryBeginTest(1)) Log("启动测试本次未下发（设备忙或未连接）"); }
            catch (Exception ex) { Log("启动测试异常：" + ex.Message); }
        }

        /// <summary>
        /// 清"本通道"的完毕字边沿记忆（新一轮测试重新武装）。
        ///
        /// 【为什么不能 Array.Clear 清全部 —— 这是现场"气密被触发两次"的半个根因】
        /// 双腔并行是现场常态（ST14-1/腔1 与 ST14-2/腔2 各测各的）。腔1 启动时若把
        /// mLastModbusDone 全清，腔2 正在测的那一轮记忆（1=测试中）也被抹成 0，
        /// 腔2 测完读到 2 时"不是从 1 变 2"→ 完成边沿丢失 → 腔2 这一轮结果永远收不到，
        /// 脚本只能等 120s 超时回 NG。只清本通道，异腔互不影响。
        ///
        /// 【为什么清本通道不会误报】
        /// 仪器把上一轮的"完毕=2"一直保持到新测试（手册：165-168 完毕保持到新测试），
        /// 所以本通道上一轮记忆=2、仪器上也是 2，两者一致；清成 0 只是回到"未武装"，
        /// 由 PollModbus 的"必须先见测试中(1)才认完成"判据兜住残留（见 PollModbus 注释）。
        /// 绝不抛异常。
        /// </summary>
        private void ResetChannelEdgeMemory(int channel)
        {
            try
            {
                int idx = channel - 1;
                if (idx < 0) idx = 0;
                if (idx >= mLastModbusDone.Length) return;
                mLastModbusDone[idx] = 0;
            }
            catch { }
        }

        /// <summary>
        /// 按通道选启动线圈（手册线圈表，见 AirtightConfig.IndependentStart 注释）。
        /// DualQuad 独立启动：1→3、2→5、3→7、4→9；统一启动或单通道版：1。
        /// </summary>
        private string StartCoilFor(int channel)
        {
            try
            {
                if (mCfg.ModbusModel == AirtightModbusModel.Single) return "1";
                if (!mCfg.IndependentStart) return "1";
                if (channel == 2) return "5";
                if (channel == 3) return "7";
                if (channel == 4) return "9";
                return "3";   // 通道1（含越界钳制：非法通道按通道1启动，调用方日志可查）
            }
            catch { return "1"; }
        }

        /// <summary>
        /// 本通道是否在测（脚本排队用：忙就不下发新启动，等在测的一轮先出结果）。
        /// Modbus DualQuad：本通道完毕字=1（测试中）或整机启动 3 秒冷却内；
        /// 单通道版只看线圈完毕（恒按整机口径，与 IsTesting 同）；IO：恒 false（仪器侧自仲裁）。
        /// Mock：待完成且 2 秒模拟测试期内报忙。绝不抛异常。
        /// </summary>
        public bool IsChannelTesting(int channel)
        {
            try
            {
                if (mDisposed) return false;
                lock (mBatchLock)
                {
                    if (DateTime.Now < mStartCooldownUntil) return true;
                    if (MockBridge.IsEnabled && mCfg.Mode == AirtightMode.ModbusDirect)
                        return mMockPending && (DateTime.UtcNow - mMockStartUtc).TotalSeconds < 2;
                }
                if (mCfg.Mode != AirtightMode.ModbusDirect) return false;
                if (mCfg.ModbusModel == AirtightModbusModel.Single) return IsTesting();
                // 统一启动模式下分通道语义不存在，按整机忙排队（防异腔连发把同测的一轮重启）
                if (!mCfg.IndependentStart) return IsTesting();
                int idx = channel - 1;
                if (idx < 0) idx = 0;
                if (idx >= mLastModbusDone.Length) return true;   // 越界按忙处理（fail-closed，不下发）
                return mLastModbusDone[idx] == 1;   // 双四通道 1=测试中
            }
            catch { return false; }
        }

        /// <summary>
        /// 整机是否在测（任一通道完毕字=1 或启动 3 秒冷却内；统一启动模式的排队口径）。
        /// Mock/IO 语义同 IsChannelTesting。绝不抛异常。
        /// </summary>
        public bool IsTesting()
        {
            try
            {
                if (mDisposed) return false;
                lock (mBatchLock)
                {
                    if (DateTime.Now < mStartCooldownUntil) return true;
                    if (MockBridge.IsEnabled && mCfg.Mode == AirtightMode.ModbusDirect)
                        return mMockPending && (DateTime.UtcNow - mMockStartUtc).TotalSeconds < 2;
                }
                if (mCfg.Mode != AirtightMode.ModbusDirect) return false;
                int n = mCfg.ChannelCount;
                if (n < 1) n = 1;
                if (n > mLastModbusDone.Length) n = mLastModbusDone.Length;
                for (int i = 0; i < n; i++)
                    if (mLastModbusDone[i] == 1) return true;   // 双四通道 1=测试中（单通道线圈语义不同，不走这里）
                return false;
            }
            catch { return false; }
        }

        /// <summary>当前已发布批次数（脚本在触发/启动时刻快照，消费时要求邮箱序号更大，防捡旧批）。</summary>
        public long GetBatchSeq()
        {
            lock (mBatchLock) { return mBatchSeq; }
        }

        /// <summary>
        /// 是否独立启动模式（脚本排队口径用：独立=按本腔忙排队，统一=按整机忙排队）。
        /// 只读配置快照，缺省 true，绝不抛异常。
        /// </summary>
        public bool IndependentStart
        {
            get { try { return mCfg == null ? true : mCfg.IndependentStart; } catch { return true; } }
        }

        /// <summary>
        /// 统一启动凑批等待毫秒（脚本统一模式用：单腔在途超该时间后先行；0=不等。
        /// 负数钳 0，缺省 3000）。只读配置快照，绝不抛异常。
        /// </summary>
        public int UnifiedBatchWaitMs
        {
            get { try { int v = mCfg == null ? 3000 : mCfg.UnifiedBatchWaitMs; return v < 0 ? 0 : v; } catch { return 3000; } }
        }

        /// <summary>
        /// 是否上位机启动检查握手（配置页/料号 Comm 开关透出，缺省 false）。
        /// 只读配置快照，绝不抛异常。
        /// </summary>
        public bool PcStartCheck
        {
            get { try { return mCfg != null && mCfg.PcStartCheck; } catch { return false; } }
        }

        /// <summary>
        /// 是否结果强制OK（配置页"结果强制OK（测试用）"复选框透出，缺省 false；
        /// 启动装载强制 false 不读 INI，只在本会话内经配置页勾选+保存热更变成 true）。
        /// 脚本认领批次时看它：true 则 FAIL 也按 OK 回 PLC（泄漏/压力仍记实测值，
        /// 超时不强制）。只读配置快照，绝不抛异常。
        /// </summary>
        public bool ForcePass
        {
            get { try { return mCfg != null && mCfg.ForcePass; } catch { return false; } }
        }

        /// <summary>
        /// 是否只发OK/NG（配置页"只发OK/NG"复选框透出，缺省 true）。
        /// 脚本 ST14 超时收尾时看它：true 则超时按 NG 回 OKNG=2（不再回 3）；
        /// false 则保持旧逻辑回 3（异常可重发）。只影响 ST14 两腔，其它站不动。
        /// 只读配置快照，绝不抛异常。
        /// </summary>
        public bool OkNgOnly
        {
            get { try { return mCfg == null ? true : mCfg.OkNgOnly; } catch { return true; } }
        }

        /// <summary>
        /// 完成信号→通知 PLC 的延时毫秒（配置页"通知PLC延时ms"透出，缺省 2000=2 秒；负数钳 0）。
        /// 计时起点=收到仪器完成信号那一刻，只延后"回执 PLC"，不延后"取数"
        /// （结果在完成信号那一刻已快照，见 AirtightNotifyQueue 注释）。
        /// 只读配置快照，绝不抛异常。
        /// </summary>
        public int NotifyDelayMs
        {
            get { try { return mNotifyQueue.DelayMs; } catch { return AirtightNotifyQueue.DefaultDelayMs; } }
        }

        /// <summary>
        /// 上次下发启动时刻（本地时间；脚本消费判定用：批次时间与启动时刻都必须晚于本触发时刻，
        /// 才认定是为我这一件测的那一轮；MinValue=还没启动过）。V4.4.103 定名 LastStart（本地时钟）。
        /// </summary>
        public DateTime LastStart
        {
            get { lock (mBatchLock) { return mLastStart; } }
        }

        /// <summary>
        /// 取邮箱最新一批（克隆，调用方随便读；无结果返回 false）。
        /// Mock：待完成满 2 秒即现造一批全 PASS（通道数按配置，Time=此刻，序号递增）。
        /// </summary>
        public bool GetLastBatch(out long seq, out AirtightBatchResult batch)
        {
            seq = 0;
            batch = null;
            try
            {
                lock (mBatchLock)
                {
                    if (MockBridge.IsEnabled && mCfg.Mode == AirtightMode.ModbusDirect && mMockPending
                        && (DateTime.UtcNow - mMockStartUtc).TotalSeconds >= 2)
                    {
                        int n = mCfg.ChannelCount;
                        if (n < 1) n = 1;
                        if (n > 4) n = 4;
                        AirtightBatchResult mb = new AirtightBatchResult();
                        mBatchSeq++;
                        mb.Seq = mBatchSeq;
                        mb.Time = DateTime.Now;
                        mb.States = new AirtightChannelState[n];
                        mb.Pressures = new float[n];
                        mb.Leaks = new float[n];
                        mb.LeakStates = NewLeakStates(n);   // Mock 无状态概念，恒 -1（0 会冒充"未超限"）
                        mb.Barcodes = new string[n];
                        for (int i = 0; i < n; i++)
                        {
                            mb.States[i] = AirtightChannelState.Pass;
                            mb.Pressures[i] = 50.0f + i;
                            mb.Leaks[i] = 0.1f * (i + 1);
                            mb.Barcodes[i] = "";
                        }
                        mb.AllPass = true;
                        mLastBatch = mb;
                        mMockPending = false;
                        mUnifiedStarted = false;   // Mock 批已出，统一启动允许下一轮（同 PublishBatch）
                    }
                    if (mLastBatch == null) return false;
                    seq = mBatchSeq;
                    batch = CloneBatch(mLastBatch);
                    return true;
                }
            }
            catch { seq = 0; batch = null; return false; }
        }

        /// <summary>批次深拷贝（邮箱内外各持一份，mutually 不串改）。</summary>
        private static AirtightBatchResult CloneBatch(AirtightBatchResult src)
        {
            if (src == null) return null;
            AirtightBatchResult b = new AirtightBatchResult();
            b.Seq = src.Seq;
            b.Time = src.Time;
            b.States = src.States == null ? new AirtightChannelState[0] : (AirtightChannelState[])src.States.Clone();
            b.Pressures = src.Pressures == null ? new float[0] : (float[])src.Pressures.Clone();
            b.Leaks = src.Leaks == null ? new float[0] : (float[])src.Leaks.Clone();
            b.LeakStates = src.LeakStates == null ? new int[0] : (int[])src.LeakStates.Clone();
            b.Barcodes = src.Barcodes == null ? new string[0] : (string[])src.Barcodes.Clone();
            b.AllPass = src.AllPass;
            return b;
        }

        /// <summary>
        /// 泄漏状态数组初始化（全 -1=未读/不适用哨兵；int 数组默认 0 会冒充"未超限"，必须显式填 -1）。
        /// </summary>
        private static int[] NewLeakStates(int n)
        {
            int[] a = new int[n < 0 ? 0 : n];
            for (int i = 0; i < a.Length; i++) a[i] = -1;
            return a;
        }

        /// <summary>
        /// 发布一批进邮箱（序号递增+克隆存档；ResultReady 事件照常抛，订阅方只做监视）。
        /// 【注意】统一启动旗（mUnifiedStarted）不在这里清——"这一轮测完了"的事实发生在
        /// 收到完成信号那一刻（EnqueueNotify 里清），若拖到发布时才清，延时窗口内脚本
        /// 已发过新一轮统一启动的话会被误清，下一轮再发一次=同件测两轮（见 EnqueueNotify 注释）。
        /// </summary>
        private void PublishBatch(AirtightBatchResult b)
        {
            if (b == null) return;
            try
            {
                lock (mBatchLock)
                {
                    mBatchSeq++;
                    b.Seq = mBatchSeq;
                    mLastBatch = CloneBatch(b);
                }
            }
            catch { }
            try { if (ResultReady != null) ResultReady(b); } catch (Exception ex) { Log("结果事件订阅方异常已隔离：" + ex.Message); }
        }

        /// <summary>
        /// 收到完成信号：把结果整批快照排队（延后 NotifyDelayMs 才进邮箱=才通知 PLC）。
        /// 【为什么快照】结果寄存器在完成信号那一刻才刚定值，等 2 秒再读可能已被下一轮
        /// 测试覆盖（读到的是别人的结果）——所以这里读一次存下，延后的是"通知"不是"取数"。
        /// 【统一启动旗在此清】"这一轮测完了"这个事实发生在收到完成信号这一刻，
        /// 不能等到延时到期后才清——否则统一模式下会多挡下一轮启动（见 TryBeginUnifiedTest 注释）。
        /// 绝不抛异常。
        /// </summary>
        private void EnqueueNotify(AirtightBatchResult b)
        {
            if (b == null) return;
            try
            {
                lock (mBatchLock) { mUnifiedStarted = false; }   // 测完了（此刻即事实，见方法注释）
                int n = mNotifyQueue.Enqueue(b, DateTime.Now);
                Log("完成信号已收，延后 " + mNotifyQueue.DelayMs + "ms 通知 PLC（待发 " + n + " 条）");
            }
            catch (Exception ex) { Log("结果排队异常已隔离：" + ex.Message); }
        }

        /// <summary>
        /// 到期批次投进邮箱（=通知 PLC 的前置一步：脚本认领批次后才真正写握手字）。
        /// 轮询线程每 50ms 调一次：配 2000ms 的实际误差 ≤50ms（轮询粒度，够用）。
        /// 绝不抛异常（外层已兜底，这里再兜一层防一条坏批卡死整条轮询）。
        /// </summary>
        private void FlushNotifyQueue()
        {
            try
            {
                if (mNotifyQueue.PendingCount == 0) return;
                List<AirtightBatchResult> due = mNotifyQueue.TakeDue(DateTime.Now);
                if (due == null || due.Count == 0) return;
                for (int i = 0; i < due.Count; i++) PublishBatch(due[i]);
                Log("已通知 PLC（延后 " + mNotifyQueue.DelayMs + "ms，本轮 " + due.Count + " 条）");
            }
            catch (Exception ex) { LogErrThrottled("到期通知异常已隔离：" + ex.Message); }
        }

        /// <summary>停止测试（Modbus 写线圈2；IO 分支无停止语义，空实现记一条）。</summary>
        public void StopTest()
        {
            try
            {
                if (mDisposed) return;
                if (mCfg.Mode == AirtightMode.ModbusDirect)
                {
                    if (!ModbusWriteCoil("2", true)) Log("停止测试下发失败（线圈2）");
                }
                else Log("停止测试（IO 分支无停止语义，靠仪器流程跑完/急停硬线）");
            }
            catch (Exception ex) { Log("停止测试异常：" + ex.Message); }
        }

        /// <summary>
        /// 一次性连通性测试（配置页"测试通讯"按钮用：在调用方后台线程里调，
        /// 内部同步阻塞几秒。IO=读一次完成字；Modbus=重连一次+读完毕字）。
        /// </summary>
        public override bool TestConnection(out string info)
        {
            info = "";
            try
            {
                string mockInfo;
                if (DeviceServiceBase.MockOk(out mockInfo, "气密")) { info = mockInfo; return true; }
                if (mCfg.Mode == AirtightMode.ModbusDirect)
                {
                    OpenModbus();
                    int[] done;
                    if (!ModbusReadDone(out done)) { info = "Modbus 连接失败（检查IP/串口/站号），详见运行日志"; return false; }
                    info = GetDebugInfo() + " 完毕字=" + string.Join("/", Array.ConvertAll(done, delegate (int x) { return x.ToString(); }));
                    return true;
                }
                int v;
                if (!PlcQueueReadInt(mCfg.CompleteWord, out v)) { info = "PLC 队列读失败（检查PLC连接/地址）"; return false; }
                info = GetDebugInfo() + " 完成字当前值=" + v;
                return true;
            }
            catch (Exception ex) { info = "测试异常：" + ex.Message; return false; }
        }

        /// <summary>
        /// 经已有连接测一次（配置页"测试通讯"优先走这里，V4.4.35 新增）。
        ///
        /// 【为什么不能 new 一个服务来测】希立气密仪是单连接 ModbusTCP 服务器
        /// （2026-09-09 实测取证：后台轮询持有一个连接时，第二个连接建连成功但读请求
        /// 5 秒饿死超时；单独连则 15ms 读回）。主程序启动后后台轮询常驻持有连接，
        /// 此时测试若另建连接，读必超时、页面永远"测试失败"——ping 通、端口通、
        /// 站号地址全对也一样。本方法只读不连：复用本实例的已有连接。
        ///
        /// 【线程安全】读原语内部已持 mModbusLock（读寄存器/线圈/浮点/条码全覆盖），
        /// 与 200ms 轮询线程天然串行，一次测试读约十几毫秒，不影响轮询节拍。
        /// IO 分支走 PLC 队列（队列本身串行），同样安全。
        ///
        /// 未连接（服务刚建还没连上/已 Dispose/通讯中断）返回 false，info 明示，
        /// 调用方回落到直连实测或提示。
        /// </summary>
        public bool TestViaExistingConnection(out string info)
        {
            info = "";
            try
            {
                string mockInfo;
                if (DeviceServiceBase.MockOk(out mockInfo, "气密")) { info = mockInfo + "（经现有连接）"; return true; }
                if (mDisposed) { info = "服务已释放"; return false; }
                if (mCfg.Mode == AirtightMode.ModbusDirect)
                {
                    if (!ModbusAlive()) { info = "服务未连接（气密服务未启动或通讯中断），请先确认服务状态"; return false; }
                    int[] done;
                    if (!ModbusReadDone(out done)) { info = "经现有连接读完毕字失败（连接可能已断，轮询线程会自动重连）"; return false; }
                    info = GetDebugInfo() + " 完毕字=" + string.Join("/", Array.ConvertAll(done, delegate (int x) { return x.ToString(); }));
                    return true;
                }
                int v;
                if (!PlcQueueReadInt(mCfg.CompleteWord, out v)) { info = "PLC 队列读失败（检查PLC连接/地址）"; return false; }
                info = GetDebugInfo() + " 完成字当前值=" + v;
                return true;
            }
            catch (Exception ex) { info = "测试异常：" + ex.Message; return false; }
        }

        /// <summary>
        /// 页面配置与本实例是否为同一连接目标（V4.4.35：测试前比对用）。
        /// 用户在页上改了 IP/端口/站号/串口/模式但还没点保存时，现有连接测的是旧地址，
        /// 必须回落到直连实测页面上的新值——否则"明明改了 IP，测的还是老的"。
        /// 只比"连到哪"（模式/传输/地址四件套/型号/通道数），轮询周期/条码开关不影响连接，
        /// 改了它们也算同一目标（走现有连接测，省一次建连）。
        /// </summary>
        public bool SameConnectionTarget(AirtightConfig other)
        {
            try
            {
                if (other == null) return false;
                if (mCfg.Mode != other.Mode) return false;
                if (mCfg.ChannelCount != other.ChannelCount) return false;
                if (mCfg.Mode == AirtightMode.ModbusDirect)
                {
                    if (!string.Equals((mCfg.ModbusTransport ?? "").Trim(), (other.ModbusTransport ?? "").Trim(),
                        StringComparison.OrdinalIgnoreCase)) return false;
                    if (mCfg.ModbusModel != other.ModbusModel) return false;
                    if (string.Equals((mCfg.ModbusTransport ?? "").Trim(), "RTU", StringComparison.OrdinalIgnoreCase))
                        return string.Equals((mCfg.ModbusCom ?? "").Trim(), (other.ModbusCom ?? "").Trim(),
                                StringComparison.OrdinalIgnoreCase)
                            && mCfg.ModbusBaud == other.ModbusBaud
                            && mCfg.ModbusStation == other.ModbusStation;
                    return string.Equals((mCfg.ModbusIp ?? "").Trim(), (other.ModbusIp ?? "").Trim(),
                            StringComparison.OrdinalIgnoreCase)
                        && mCfg.ModbusPort == other.ModbusPort
                        && mCfg.ModbusStation == other.ModbusStation;
                }
                return mCfg.PlcDevId == other.PlcDevId
                    && string.Equals((mCfg.CompleteWord ?? "").Trim(), (other.CompleteWord ?? "").Trim(),
                            StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ================= 实时数据读取（配置页"读取实时数据"按钮用） =================

        /// <summary>
        /// 读一次全通道实时快照并格式化成多行文本（配置页"读取实时数据"按钮用）。
        /// 定位：现场联调时人工核对"仪器寄存器→上位机"链路是否真的通、值是否真的对——
        /// 点一下看通道1/2此刻的原始值（完毕字/测试结果/稳压压力/泄漏值/泄漏状态），与仪器屏幕对照。
        ///
        /// 【与业务轮询的关系】只读不写、不碰邮箱/批次/边沿记忆：与 200ms 轮询线程并发时靠
        /// mModbusLock 串行（所有 Modbus 原语内部已持锁，一次全通道读约十几毫秒），不影响轮询
        /// 节拍；也不复用/不改动 mLastModbusDone（轮询的完毕边沿判定不受这里影响）。
        /// 【与业务门控的区别】业务轮询 RaiseModbusBatch 只对本轮"真正测完"的通道（先见测试中(1)
        /// 再见完毕(2)）才读结果/压力/泄漏（防残留值误判）；本方法恰恰要看"此刻"的原始状态，
        /// 所以全部照读，但在未完毕的通道行尾注明"残留/中间值仅供参考"，防拿中间值当真结果。
        /// 【单连接铁律】复用服务已有连接，绝不另建（希立单连接服务器，另建必饿死，见
        /// TestViaExistingConnection 注释）；未连接直接返回失败、不代连（代连会与轮询线程的
        /// OpenModbus 重连互撞），提示语已说明等轮询自动重连。
        /// 返回 true=info 是格式化快照文本（多行）；false=info 是失败原因。绝不抛异常。
        /// </summary>
        public bool ReadLiveSnapshot(out string info)
        {
            info = "";
            try
            {
                if (mDisposed) { info = "服务已释放"; return false; }
                if (mCfg.Mode == AirtightMode.ModbusDirect)
                {
                    if (MockBridge.IsEnabled)
                    { info = "Mock演示模式：不接硬件，无实时数据可读"; return false; }
                    if (!ModbusAlive())
                    { info = "服务未连接（服务未启动或通讯中断；轮询线程会自动重连，稍后再试）"; return false; }
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("读取时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + GetDebugInfo());
                    int n = mCfg.ChannelCount;
                    if (n < 1) n = 1;
                    if (n > 4) n = 4;
                    if (mCfg.ModbusModel == AirtightModbusModel.Single)
                    {
                        // 单通道版（手册：线圈4完毕 0/1；39结果 1PASS/2FAIL/0未检出；
                        // 40-41稳压压力、42-43泄漏（各2字）；44-51条码8字，BarcodeEnabled 才读）。
                        // 照读不门控（见方法注释"与业务门控的区别"），未完毕行尾标注。
                        int done0;
                        lock (mModbusLock)
                        {
                            try
                            {
                                OperateResult<bool[]> rd = mModbusRtu != null
                                    ? mModbusRtu.ReadCoil("4", 1) : mModbusTcp.ReadCoil("4", 1);
                                done0 = (rd != null && rd.IsSuccess && rd.Content != null && rd.Content.Length >= 1)
                                    ? (rd.Content[0] ? 1 : 0) : -1;
                            }
                            catch { done0 = -1; }
                        }
                        if (done0 < 0)
                        { info = "读完毕字（线圈4）失败（连接可能已断，轮询线程会自动重连，稍后再试）"; return false; }
                        ushort[] res0;
                        bool hasRes0 = ModbusReadRegs("39", 1, out res0);
                        float[] pl0;
                        bool hasPl0 = ModbusReadFloats("40", 2, out pl0);
                        string bc0 = "";
                        if (mCfg.BarcodeEnabled)
                        {
                            lock (mModbusLock)
                            {
                                try
                                {
                                    OperateResult<byte[]> rb = mModbusRtu != null
                                        ? mModbusRtu.Read("44", 8) : mModbusTcp.Read("44", 8);
                                    if (rb != null && rb.IsSuccess && rb.Content != null)
                                        bc0 = BytesToString(rb.Content, ResolveBarcodeEncoding(mCfg.BarcodeEncoding));
                                }
                                catch { }
                            }
                        }
                        sb.Append("通道1：");
                        sb.Append("完毕=" + (done0 == 1 ? "1(完毕)" : "0(未完毕)"));
                        if (hasRes0 && res0.Length > 0) sb.Append(" 结果=" + FormatResultReg(res0[0]));
                        if (hasPl0 && pl0.Length >= 2)
                        {
                            sb.Append(" 压力=" + FormatLiveFloat(pl0[0]));
                            sb.Append(" 泄漏=" + FormatLiveFloat(pl0[1]));
                        }
                        if (bc0.Length > 0) sb.Append(" 条码=" + bc0);
                        if (done0 != 1)
                            sb.Append("（未完毕：结果/压力/泄漏为残留或中间值，仅供参考）");
                        sb.AppendLine();
                    }
                    else
                    {
                        // 双四通道版（手册：165-168完毕 0空闲/1测试中/2完毕；105-108结果 1PASS/2FAIL/0未检出；
                        // 109起稳压压力、117起泄漏值（各通道2字）；178-181泄漏值状态 0未超限/1微漏/2大漏/3波动）。
                        // 全部照读+未完毕标注（见方法注释）；完毕字读失败才算整页失败（其余组失败在行内显示缺省）。
                        ushort[] doneR, resR, stR;
                        float[] presR, leakR;
                        bool hasDone = ModbusReadRegs("165", n, out doneR);
                        bool hasRes = ModbusReadRegs("105", n, out resR);
                        bool hasPres = ModbusReadFloats("109", n, out presR);
                        bool hasLeak = ModbusReadFloats("117", n, out leakR);
                        bool hasSt = ModbusReadRegs("178", n, out stR);
                        if (!hasDone)
                        { info = "读完毕字（165-168）失败（连接可能已断，轮询线程会自动重连，稍后再试）"; return false; }
                        int[] doneInt = Array.ConvertAll(doneR, delegate (ushort v) { return (int)v; });   // IsModbusFinished 收 int[]
                        for (int i = 0; i < n; i++)
                        {
                            sb.Append("通道" + (i + 1) + "：");
                            sb.Append("完毕=" + FormatDoneWord(i < doneR.Length ? doneR[i] : -1));
                            if (hasRes && i < resR.Length) sb.Append(" 结果=" + FormatResultReg(resR[i]));
                            if (hasPres && i < presR.Length) sb.Append(" 压力=" + FormatLiveFloat(presR[i]));
                            if (hasLeak && i < leakR.Length) sb.Append(" 泄漏=" + FormatLiveFloat(leakR[i]));
                            if (hasSt && i < stR.Length) sb.Append(" 泄漏状态=" + FormatLeakState(stR[i]));
                            if (!IsModbusFinished(doneInt, i))
                                sb.Append("（未完毕：结果/压力/泄漏为残留或中间值，仅供参考）");
                            sb.AppendLine();
                        }
                        sb.AppendLine("说明：压力/泄漏数值不带单位（仪器程序设置决定）；当前项目气密仪不扫码，产品条码复用产线 SN，125 条码寄存器不读。");
                    }
                    info = sb.ToString();
                    return true;
                }
                // IO 分支：完成字（非0=本批完成）+ 结果字（按位 bit(i)=通道i+1 OK）。
                // 走 PLC 队列注入（PlcQueueReadInt 3 秒上限），probe 实例（服务未跑时配置页自建）同样能读。
                int complete, bits;
                if (!PlcQueueReadInt(mCfg.CompleteWord, out complete))
                { info = "PLC 队列读完成字失败（检查PLC连接/" + mCfg.CompleteWord + "）"; return false; }
                bool hasBits = PlcQueueReadInt(mCfg.ResultWord, out bits);
                StringBuilder sbIo = new StringBuilder();
                sbIo.AppendLine("读取时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + GetDebugInfo());
                sbIo.AppendLine("完成字 " + mCfg.CompleteWord + " = " + complete
                    + (complete != 0 ? "（非0=本批完成）" : "（0=无完成）"));
                if (hasBits)
                {
                    sbIo.Append("结果字 " + mCfg.ResultWord + " = 0x" + bits.ToString("X4") + " →");
                    for (int i = 0; i < mCfg.ChannelCount; i++)
                        sbIo.Append(" 通道" + (i + 1) + "=" + (((bits >> i) & 1) == 1 ? "OK" : "NG"));
                    sbIo.AppendLine();
                }
                else sbIo.AppendLine("结果字 " + mCfg.ResultWord + " 读失败");
                info = sbIo.ToString();
                return true;
            }
            catch (Exception ex) { info = "读取异常：" + ex.Message; return false; }
        }

        /// <summary>完毕字转文本（双四通道手册：0空闲/1测试中/2完毕；-1=读失败）。</summary>
        private static string FormatDoneWord(int v)
        {
            if (v == 0) return "0(空闲)";
            if (v == 1) return "1(测试中)";
            if (v == 2) return "2(完毕)";
            if (v < 0) return "?";
            return v + "(?)";
        }

        /// <summary>测试结果寄存器转文本（手册：1 PASS / 2 FAIL / 0 未检出）。</summary>
        private static string FormatResultReg(int v)
        {
            if (v == 1) return "1(PASS)";
            if (v == 2) return "2(FAIL)";
            if (v == 0) return "0(未检出)";
            return v + "(?)";
        }

        /// <summary>泄漏值状态转文本（手册 178-181：0未超限/1微漏/2超上下限大漏/3保压波动超范围）。</summary>
        private static string FormatLeakState(int v)
        {
            if (v == 0) return "0(未超限)";
            if (v == 1) return "1(微漏)";
            if (v == 2) return "2(大漏)";
            if (v == 3) return "3(压力波动)";
            return v + "(?)";
        }

        /// <summary>实时浮点转文本（NaN/Inf→"-"，其余 InvariantCulture 通用格式）。</summary>
        private static string FormatLiveFloat(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "-";
            return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // ================= 轮询主循环 =================

        private void PollLoop()
        {
            while (!mDisposed)
            {
                try
                {
                    // Mock演示：Modbus 分支不开串口/网口，保持已连接假态静默轮询（IO 分支读队列假值天然无噪音）。
                    if (MockBridge.IsEnabled && mCfg.Mode == AirtightMode.ModbusDirect) SetConnected(true);
                    else if (mCfg.Mode == AirtightMode.ModbusDirect) PollModbus();
                    else PollIo();
                }
                catch (Exception ex)
                {
                    // 外层兜底：保线程不死（内层各分支自己还有单次隔离）
                    LogErrThrottled("轮询异常已隔离：" + ex.Message);
                }
                for (int i = 0; i < mCfg.PollMs / 50 && !mDisposed; i++)
                {
                    Thread.Sleep(50);
                    // 延时通知到期即发（50ms 粒度，别等整轮 PollMs：配 2000ms 误差 ≤50ms）
                    FlushNotifyQueue();
                }
            }
        }

        // ================= IO 分支（PLC 中转） =================

        /// <summary>
        /// 经注入读一个 Int16：失败/"Error"串/超时回 false，发起方永不永久挂死。
        /// </summary>
        private bool PlcQueueReadInt(string address, out int value)
        {
            value = 0;
            try
            {
                Func<int, string, int, string> r = PlcReadInt;
                if (r == null) return false;
                string raw = r(PlcSlotIndex, address, mCfg.PlcDevId);
                if (string.IsNullOrEmpty(raw) || raw == "Error") return false;
                return int.TryParse(raw.Trim(), out value);
            }
            catch { return false; }
        }

        private void PollIo()
        {
            int complete;
            if (!PlcQueueReadInt(mCfg.CompleteWord, out complete))
            {
                SetConnected(false);
                return;   // 读失败：不断线程，下轮继续（不断刷屏靠边沿日志）
            }
            SetConnected(true);
            // 边沿：只认 0→非0。维持期间不重复抛批；仪器掉电复位回 0 后自动重新武装。
            if (mLastComplete == 0 && complete != 0)
            {
                int bits;
                if (!PlcQueueReadInt(mCfg.ResultWord, out bits)) return;   // 结果字读失败就等下轮（完成字还维持着，不丢批）
                RaiseBatchFromBits(bits);
            }
            mLastComplete = complete;
        }

        private void RaiseBatchFromBits(int bits)
        {
            try
            {
                AirtightBatchResult b = new AirtightBatchResult();
                b.Time = DateTime.Now;
                b.States = new AirtightChannelState[mCfg.ChannelCount];
                b.Pressures = new float[mCfg.ChannelCount];
                b.Leaks = new float[mCfg.ChannelCount];
                b.LeakStates = NewLeakStates(mCfg.ChannelCount);   // IO 分支无状态寄存器，恒 -1
                b.Barcodes = new string[mCfg.ChannelCount];
                b.AllPass = mCfg.ChannelCount > 0;
                for (int i = 0; i < mCfg.ChannelCount; i++)
                {
                    b.States[i] = ((bits >> i) & 1) == 1 ? AirtightChannelState.Pass : AirtightChannelState.Fail;
                    if (b.States[i] != AirtightChannelState.Pass) b.AllPass = false;
                    b.Pressures[i] = float.NaN;   // IO 分支无数值（仪器就没有串口，见类注释）
                    b.Leaks[i] = float.NaN;
                    b.Barcodes[i] = "";
                }
                Log("完成一批 结果位=0x" + bits.ToString("X") + " " + (b.AllPass ? "全PASS" : "有FAIL"));
                EnqueueNotify(b);   // 快照排队（延后 NotifyDelayMs 才通知 PLC，见 EnqueueNotify 注释）
            }
            catch (Exception ex) { Log("结果事件订阅方异常已隔离：" + ex.Message); }
        }

        // ================= Modbus 分支（希立直连） =================

        private void OpenModbus()
        {
            CloseModbus();
            string tran = (mCfg.ModbusTransport ?? "TCP").Trim().ToUpperInvariant();
            if (tran == "RTU")
            {
                mModbusRtu = new ModbusRtu(mCfg.ModbusStation);
                mModbusRtu.SerialPortInni(mCfg.ModbusCom, mCfg.ModbusBaud, 8, StopBits.One, Parity.None);
                OperateResult open = mModbusRtu.Open();
                // 失败走 10 秒节流（同 TCP 分支：轮询离线时每轮都进 OpenModbus，直记即刷屏）。
                if (!open.IsSuccess) { LogErrThrottled("ModbusRTU 打开失败 " + mCfg.ModbusCom + "：" + open.Message); mModbusRtu = null; }
                else Log("ModbusRTU 已打开 " + mCfg.ModbusCom + "/" + mCfg.ModbusBaud);
            }
            else
            {
                mModbusTcp = new ModbusTcpNet(mCfg.ModbusIp, mCfg.ModbusPort, mCfg.ModbusStation);
                // 希立仪器是 0 基址（2026-09-28 现场实测：报文地址=文档编号，不减 1。
                // 取证：读 wire160-163 得 [0,1,1,0] 与工装调试"通道1/2开"错一位；
                // wire30=0（无光栅）而 wire29=1，wire1=1（程序编号1合理）；
                // 按 0 基写 wire5（文档线圈5）0.5 秒后通道2进测试中，写 wire4 则无反应。
                // 之前设 false 全体错一位：读"165"实际读到文档164（通道4开关恒0，
                // 永远等不到完毕边沿），写"3/5"实际落到文档线圈2（统一停止）/4
                // （通道1停止）——ping 通、测试通讯（只读不校验）通过，但永远触发不了。
                // 所以必须 true（填几发几）。RTU 分支从未改过（HSL 默认即 true），本来就是对的。
                // 显式赋值不依赖库默认（与下行 DataFormat 同理，防 HSL 换版改默认）。
                mModbusTcp.AddressStartWithZero = true;
                // DOC 浮点按 Float AB CD 解析：高字在前，与 ReadFloat32 默认一致，显式锁死防版本默认变。
                mModbusTcp.DataFormat = DataFormat.ABCD;
                mModbusTcp.ConnectTimeOut = 3000;   // 建连 3 秒上限（后台线程也不许死等 10 秒默认）
                OperateResult link = mModbusTcp.ConnectServer();
                // 失败走 10 秒节流（轮询 200ms 一轮，离线时每轮都进 OpenModbus，直记=每秒 5 行刷屏；
                // 成功直记（建连成功才到这里，一次一条）；通断边沿另有 SetConnected 的"通讯恢复/中断"。
                if (!link.IsSuccess) { LogErrThrottled("ModbusTCP 连接失败 " + mCfg.ModbusIp + ":" + mCfg.ModbusPort + "：" + link.Message); CloseModbus(); }
                else Log("ModbusTCP 已连接 " + mCfg.ModbusIp + ":" + mCfg.ModbusPort);
            }
        }

        private void CloseModbus()
        {
            try
            {
                if (mModbusTcp != null) { try { mModbusTcp.ConnectClose(); } catch { } mModbusTcp = null; }
                if (mModbusRtu != null) { try { mModbusRtu.Close(); } catch { } mModbusRtu = null; }
            }
            catch { }
        }

        private bool ModbusAlive()
        {
            if ((mCfg.ModbusTransport ?? "TCP").Trim().ToUpperInvariant() == "RTU")
                return mModbusRtu != null;
            return mModbusTcp != null;
        }

        private void ModbusReconnect()
        {
            try { OpenModbus(); } catch { }
        }

        private bool ModbusWriteCoil(string addr, bool value)
        {
            // 锁内执行：与轮询/测试线程互斥（见 mModbusLock 注释），防应答交错。
            lock (mModbusLock)
            {
                try
                {
                    // HSL9 的线圈写是泛型 Write(string, bool)（无 WriteCoil 名，反射确认过）。
                    OperateResult r;
                    if (mModbusRtu != null) r = mModbusRtu.Write(addr, value);
                    else if (mModbusTcp != null) r = mModbusTcp.Write(addr, value);
                    else return false;
                    if (!r.IsSuccess) LogErrThrottled("写线圈" + addr + "失败：" + r.Message);
                    return r.IsSuccess;
                }
                catch (Exception ex) { LogErrThrottled("写线圈" + addr + "异常：" + ex.Message); return false; }
            }
        }

        /// <summary>
        /// 写保持寄存器（HSL Write(addr, ushort[]) 批量写；单写传 1 个元素。
        /// 锁内执行，与轮询/测试线程互斥，防应答交错。返回 false=通讯失败。
        /// 用途：上位机启动检查握手给 173-176 写 0。绝不抛异常。
        /// </summary>
        private bool ModbusWriteRegs(string addr, ushort[] values)
        {
            lock (mModbusLock)
            {
                try
                {
                    if (values == null || values.Length == 0) return false;
                    if (mModbusRtu == null && mModbusTcp == null) return false;   // 未连接：静默失败（调用方按超时/重试走，不刷屏）
                    OperateResult r = mModbusRtu != null
                        ? mModbusRtu.Write(addr, values) : mModbusTcp.Write(addr, values);
                    if (r == null || !r.IsSuccess)
                    {
                        LogErrThrottled("写寄存器" + addr + "失败：" + (r == null ? "null" : r.Message));
                        return false;
                    }
                    return true;
                }
                catch (Exception ex) { LogErrThrottled("写寄存器" + addr + "异常：" + ex.Message); return false; }
            }
        }

        /// <summary>
        /// 读保持寄存器（HSL9 无 ReadRegister 名，用原语 Read(addr, 个数)→裸字节，
        /// 每寄存器 2 字节大端）。返回 false=通讯失败。
        /// </summary>
        private bool ModbusReadRegs(string addr, int count, out ushort[] regs)
        {
            regs = new ushort[0];
            // 锁内执行：与轮询/测试线程互斥（见 mModbusLock 注释），防应答交错。
            lock (mModbusLock)
            {
                try
                {
                    OperateResult<byte[]> r = mModbusRtu != null
                        ? mModbusRtu.Read(addr, (ushort)count) : mModbusTcp.Read(addr, (ushort)count);
                    if (r == null || !r.IsSuccess || r.Content == null || r.Content.Length < count * 2) return false;
                    regs = new ushort[count];
                    for (int i = 0; i < count; i++)
                        regs[i] = (ushort)((r.Content[i * 2] << 8) | r.Content[i * 2 + 1]);
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>读连续 float（HSL ReadFloat 走 DataFormat=ABCD，与 DOC 的 Float AB CD 对齐）。</summary>
        private bool ModbusReadFloats(string addr, int count, out float[] vals)
        {
            vals = new float[0];
            // 锁内执行：与轮询/测试线程互斥（见 mModbusLock 注释），防应答交错。
            lock (mModbusLock)
            {
                try
                {
                    OperateResult<float[]> r = mModbusRtu != null
                        ? mModbusRtu.ReadFloat(addr, (ushort)count) : mModbusTcp.ReadFloat(addr, (ushort)count);
                    if (r == null || !r.IsSuccess || r.Content == null || r.Content.Length < count) return false;
                    vals = r.Content;
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 单通道 finished 判定（与 PollModbus 边沿判定同语义，抽出来给发布门控复用）。
        /// idx 越界/快照为空按未完成处理（fail-closed：宁可多等一批，不吃残留值）。
        /// </summary>
        private bool IsModbusFinished(int[] doneSnapshot, int idx)
        {
            try
            {
                if (doneSnapshot == null || idx < 0 || idx >= doneSnapshot.Length) return false;
                if (mCfg.ModbusModel == AirtightModbusModel.Single) return doneSnapshot[idx] == 1;
                return doneSnapshot[idx] == 2;
            }
            catch { return false; }
        }

        /// <summary>读完毕字：单通道=线圈4（0/1），双四通道=寄存器165-168（0空闲/1测试中/2完毕）。</summary>
        private bool ModbusReadDone(out int[] done)
        {
            done = new int[mCfg.ChannelCount];
            try
            {
                if (mCfg.ModbusModel == AirtightModbusModel.Single)
                {
                    // 读线圈同样锁内执行（与轮询/测试线程互斥，见 mModbusLock 注释）。
                    lock (mModbusLock)
                    {
                        OperateResult<bool[]> r = mModbusRtu != null
                            ? mModbusRtu.ReadCoil("4", 1) : mModbusTcp.ReadCoil("4", 1);
                        if (r == null || !r.IsSuccess || r.Content == null || r.Content.Length < 1) return false;
                        done[0] = r.Content[0] ? 1 : 0;
                        return true;
                    }
                }
                ushort[] regs;
                if (!ModbusReadRegs("165", mCfg.ChannelCount, out regs)) return false;
                for (int i = 0; i < mCfg.ChannelCount; i++) done[i] = regs[i];
                return true;
            }
            catch { return false; }
        }

        /// <summary>裸字节转字符串（寄存器大端字节序，尾部 \0/空白全去）。</summary>
        private static string BytesToString(byte[] b, Encoding enc)
        {
            if (b == null || b.Length == 0) return "";
            string s = enc.GetString(b);
            return s.Trim('\0', ' ', '\t', '\r', '\n');
        }

        private static Encoding ResolveBarcodeEncoding(string name)
        {
            if (string.Equals(name, "UNICODE", StringComparison.OrdinalIgnoreCase)) return Encoding.Unicode;
            try
            {
                if (string.Equals(name, "GB2312", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "GBK", StringComparison.OrdinalIgnoreCase))
                    return Encoding.GetEncoding("GB2312");
            }
            catch { }
            return Encoding.UTF8;
        }

        private void PollModbus()
        {
            if (!ModbusAlive()) { SetConnected(false); ModbusReconnect(); return; }
            // 心跳：5 秒内必须写一次线圈 11（DOC 原文），每轮都写最省心（写操作本身很轻）。
            if ((DateTime.Now - mLastHeartbeat).TotalSeconds >= 2)
            {
                mLastHeartbeat = DateTime.Now;
                ModbusWriteCoil("11", true);
            }
            // 上位机启动检查握手推进（PcStartCheck=true 且已发起时：等 173 变 1 写 0；
            // 开关关着/未发起时 PumpPcCheck 直接返回，零开销）。
            PumpPcCheck();
            int[] done;
            if (!ModbusReadDone(out done)) { SetConnected(false); ModbusReconnect(); return; }
            SetConnected(true);
            bool anyEdge = false;
            // chEdge[i]=本轮"真正测完"的通道（完成边沿）。批次只对这些通道读结果/压力/泄漏，
            // 别的通道一律 Unknown+NaN（门控见 RaiseModbusBatch 注释）。
            bool[] chEdge = new bool[mCfg.ChannelCount < 1 ? 1 : mCfg.ChannelCount];
            for (int i = 0; i < mCfg.ChannelCount; i++)
            {
                if (mCfg.ModbusModel == AirtightModbusModel.Single)
                {
                    // 单通道版：线圈4 只有 0/1 两态，没有"测试中"中间态可用，
                    // 只能靠"启动时清本通道记忆"重新武装（0→1 边沿），保持原判据。
                    // 局限：仪器若把线圈4 保持为 1，启动后的首个轮询会误报一次完成，
                    // 现场在用的是双四通道（DualQuad），下面那条判据才是主路径。
                    if (done[i] == 1 && mLastModbusDone[i] != 1) { anyEdge = true; chEdge[i] = true; }
                }
                else
                {
                    // 双四通道（现场在用）：165-168 = 0空闲/1测试中/2完毕，且"完毕保持到新测试"（手册原文）。
                    // 【为什么必须"先见测试中(1)、再见完毕(2)"才算完成】
                    // 仪器把上一轮的 2 一直留着，下一轮启动前它就是 2。若只看"当前=2 且上次≠2"，
                    // 则服务刚启动/热更重建（记忆全 0）或本通道刚清记忆时，都会把仪器上**残留的 2**
                    // 当成"新一轮完成"→ 发布一批内容其实是上一轮残留结果的批次。
                    // 现场日志实证（2026-10-20）：双腔并行时另一腔先完成发布的批次里本腔是 Unknown，
                    // 脚本据此误清启动旗 → 同一件被测两轮 → 仪器被触发两次。
                    // 改为"先读到 1（测试中）、再读到 2（完毕）"才判边沿后：
                    //   正常一轮 165: 2(上轮残留) → 1(测试中) → 2(本轮完毕)，读到 1 后再读到 2 才是真边沿 ✓
                    //   残留场景 165: 2(残留) → 记忆 0 → 不判边沿 ✓（等真的转 1 才武装）
                    //   异常停   165: 1 → 0（手册：1→0=异常停）→ 不判边沿 ✓
                    // 代价：某轮测试快到 200ms 轮询都采不到"测试中"中间态时会漏这一批——
                    // 气密实测一轮 20+ 秒（现场日志），不存在该风险。
                    if (done[i] == 2 && mLastModbusDone[i] == 1) { anyEdge = true; chEdge[i] = true; }
                }
                mLastModbusDone[i] = done[i];
            }
            if (anyEdge) RaiseModbusBatch(chEdge);
        }

        /// <summary>
        /// 发布 Modbus 批次（chEdge=本轮真正测完的通道掩码）。
        /// 【完毕门控】只有**本轮真正测完**的通道（chEdge[i]=true，即"读到过测试中再读到完毕"）
        /// 才读结果/压力/泄漏；其余通道强制 Unknown+NaN——
        /// 整批是"任一通道完毕"即发布，未完成通道的结果寄存器是上一轮的残留值，信它=误判
        /// （上轮 PASS 残留会把没测完的腔判成 PASS；更糟的是脚本会拿这份残留当本件结果回 PLC）。
        /// 【为什么不用 done[i]==2 当门控】仪器把上一轮的"完毕=2"保持到新测试，光看 done
        /// 分不清"本轮测完"和"上轮残留"，必须靠 chEdge（血缘）判定。
        /// </summary>
        private void RaiseModbusBatch(bool[] chEdge)
        {
            try
            {
                AirtightBatchResult b = new AirtightBatchResult();
                b.Time = DateTime.Now;
                int n = mCfg.ChannelCount;
                b.States = new AirtightChannelState[n];
                b.Pressures = new float[n];
                b.Leaks = new float[n];
                b.LeakStates = NewLeakStates(n);
                b.Barcodes = new string[n];
                b.AllPass = n > 0;
                for (int i = 0; i < n; i++)
                {
                    b.States[i] = AirtightChannelState.Unknown;
                    b.Pressures[i] = float.NaN;
                    b.Leaks[i] = float.NaN;
                    b.Barcodes[i] = "";
                }
                bool ch0Finished = chEdge != null && chEdge.Length > 0 && chEdge[0];
                if (mCfg.ModbusModel == AirtightModbusModel.Single)
                {
                    // 39=结果(1 PASS/2 FAIL)，40-41=稳压压力float，42-43=泄漏float（DOC 报文例）。
                    // 注意：DOC 只明确给了 40 稳压/42 泄漏的起始寄存器，这里按连续 float 取；
                    // 若现场压力错位，先查此假设。没测完不读（门控见方法注释）。
                    ushort[] regs;
                    if (ch0Finished && ModbusReadRegs("39", 1, out regs))
                    {
                        b.States[0] = regs[0] == 1 ? AirtightChannelState.Pass
                            : (regs[0] == 2 ? AirtightChannelState.Fail : AirtightChannelState.Unknown);
                    }
                    float[] fl;
                    if (ch0Finished && ModbusReadFloats("40", 2, out fl))
                    {
                        b.Pressures[0] = fl[0];
                        b.Leaks[0] = fl[1];
                    }
                    if (mCfg.BarcodeEnabled)
                    {
                        // 条码裸读同样锁内执行（与轮询/测试线程互斥，见 mModbusLock 注释）。
                        lock (mModbusLock)
                        {
                            try
                            {
                                OperateResult<byte[]> rb = mModbusRtu != null
                                    ? mModbusRtu.Read("44", 8) : mModbusTcp.Read("44", 8);
                                if (rb != null && rb.IsSuccess && rb.Content != null)
                                    b.Barcodes[0] = BytesToString(rb.Content, ResolveBarcodeEncoding(mCfg.BarcodeEncoding));
                            }
                            catch { }
                        }
                    }
                }
                else
                {
                    // 双四通道：结果 105-108（1 PASS/2 FAIL，与单通道 39 同义，现场核对），
                    // 压力 109 起、泄漏 117 起各 n 个 float；泄漏状态 178-181（0未超限/1微漏/2大漏/3波动）。
                    ushort[] rr, stR;
                    float[] rp, rl;
                    bool hasR = ModbusReadRegs("105", n, out rr);
                    bool hasP = ModbusReadFloats("109", n, out rp);
                    bool hasL = ModbusReadFloats("117", n, out rl);
                    bool hasSt = ModbusReadRegs("178", n, out stR);
                    for (int i = 0; i < n; i++)
                    {
                        if (chEdge == null || i >= chEdge.Length || !chEdge[i]) continue;   // 本轮没测完的通道不读（门控见方法注释）
                        if (hasR)
                            b.States[i] = rr[i] == 1 ? AirtightChannelState.Pass
                                : (rr[i] == 2 ? AirtightChannelState.Fail : AirtightChannelState.Unknown);
                        if (hasP) b.Pressures[i] = rp[i];
                        if (hasL) b.Leaks[i] = rl[i];
                        if (hasSt && i < stR.Length) b.LeakStates[i] = stR[i];   // 未完毕通道保持 -1（残留状态会误导 NG 归因）
                    }
                    // 双四通道条码（原 TODO-AIRTIGHT-BARCODE，2026-09-29 现场确认结案）：DOC 支持
                    // "先写 177 选通道再读 125"的仪器扫码工作流，但当前项目气密仪没有扫码动作，
                    // 产品条码复用产线 SN（ST2 扫码建档、随 TrackId 流转），125 寄存器无码可读，
                    // Barcodes 恒空。日后若现场启用仪器扫码，在此处补"写 177=通道号→读 125"即可。
                }
                for (int i = 0; i < n; i++)
                    if (b.States[i] != AirtightChannelState.Pass) b.AllPass = false;
                Log("完成一批 " + (b.AllPass ? "全PASS" : "有FAIL/未知"));
                EnqueueNotify(b);   // 快照排队（延后 NotifyDelayMs 才通知 PLC，见 EnqueueNotify 注释）
            }
            catch (Exception ex) { Log("结果事件订阅方异常已隔离：" + ex.Message); }
        }
    }
}
