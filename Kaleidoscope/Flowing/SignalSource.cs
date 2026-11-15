using System;
using System.Diagnostics;
using System.Threading;

namespace Kaleidoscope.Flowing
{
    /// <summary>
    /// 信号源统一口：后台采集线程把"一拍原始值"组装成整拍快照并发布。
    /// 宿主订阅 EdgesReady 消费边沿（判定一律"边沿 × 快照"双确认——边沿给时机，快照给真值），
    /// 读 LatestSnapshot 随时取最新整拍（原子换引用，读永不阻塞）。
    /// </summary>
    /// <typeparam name="TSnapshot">整拍快照类型（引用类型；一次 PollOnce 产出的完整点表/状态集）。</typeparam>
    /// <typeparam name="TEdge">边沿类型（如"某点从 false 变 true"的描述对象）。</typeparam>
    public interface ISignalSource<TSnapshot, TEdge> where TSnapshot : class
    {
        /// <summary>最新整拍快照（原子换引用读，永不阻塞；Start 前为 null）。判定一律以它为准。</summary>
        TSnapshot LatestSnapshot { get; }

        /// <summary>
        /// 边沿就绪事件（边沿集 + 已含边沿值的快照）。后台泵线程触发，订阅方自行保证线程安全；
        /// 【顺序契约】触发本事件前快照引用已换新——回调里读 LatestSnapshot 必含本批边沿值
        /// （见 PollingSignalSource 类头，勿破坏顺序）。
        /// </summary>
        event Action<TEdge[], TSnapshot> EdgesReady;

        /// <summary>采集/比对异常出口（泵线程触发；异常已隔离记账，泵不死，宿主只管感知与告警）。</summary>
        event Action<Exception> SourceError;
    }

    // =====================================================================================
    // 业务流编排 · 轮询信号源基类（"先整拍换快照，再统一发边沿"顺序契约的库级固化，V1.10.0 沉库）
    // -------------------------------------------------------------------------------------
    // 【干什么】把"轮询采集→组装整拍快照→比对边沿→发布"的通用骨架收进库：宿主只实现两个
    //   纯函数（PollOnce 采集一拍、DiffEdges 新旧快照比对出边沿），基类专属后台线程按周期
    //   跑泵，异常隔离记账，停止限时收尾。订阅方拿到的每批边沿都保证附带"已含该边沿值的
    //   整拍快照"。
    //   来源：宿主 CureLine 设备总线 IoBus.PollOnce 的轮询契约（2026-10 V0.0.64 血泪沉淀）
    //   + HJVision FlowGroupRunner 的线程红线范式，一比一提炼（抽象成模板方法，语义零改动）。
    // -------------------------------------------------------------------------------------
    // 【顺序契约=本类第一红线，V0.0.63 真机事故根因】判定链是"边沿 × 快照"双确认：边沿告诉
    //   订阅方"值变了"，订阅方读快照拿真值判定。若边沿先于快照发出，判定读到的是上一拍旧值，
    //   判断链必失败（CureLine 真机复位失效：清料按钮的空边沿让不出复位，红灯挂死，根因即
    //   早一版边沿先发、快照后换）。所以 PumpOnce 里严格：①采集 ②比对 ③【先】换 _latest
    //   引用 ④【再】触发 EdgesReady——顺序绝不可对调，冒烟 T32.1 用回调内反读快照锁死此序。
    // 【为什么首拍发基线不发边沿】泵启动瞬间全点"从无到有"，若当边沿发会淹掉订阅方（一堆
    //   假事件）。首拍只换快照（LatestSnapshot 有值），不触发 EdgesReady——订阅方按"没有
    //   边沿但有快照"理解为首拍基线。
    // 【为什么锁外发事件】EdgesReady 订阅方可能做重活（组报文/写库/开窗），泵线程持有任何
    //   锁再回调=把订阅方卡进自己的锁链。基类泵循环内无锁（_latest 单写多读靠引用原子性），
    //   事件触发在锁外（本来也没有锁）。
    // -------------------------------------------------------------------------------------
    // 【怎么接】三步：
    //   ①继承并实现两个纯函数：PollOnce()（采集一拍整拍，可做批量读硬件；抛异常=本拍失败，
    //   泵隔离记账后下一拍重试）；DiffEdges(oldSnap, newSnap)（返回本拍边沿集，无变化返回
    //   空数组；首拍 oldSnap 为 null 自行短路）。
    //   ②new 后订阅 EdgesReady/SourceError，调 Start()（periodMs 已在构造给出）。
    //   ③判定=边沿 × 快照双确认：EdgesReady(edges, snap) 里优先用参数 snap 判定（它与
    //   LatestSnapshot 同引用），别只读边沿不读快照（旧值陷阱，见类头事故）。
    // 【怎么改】PumpOnce/DiffEdges 的扩展（采集节流/边沿合并）在泵循环里加，顺序契约步骤
    //   ③④的先后绝不动；线程红线（volatile 停止位/异常绝不冲循环/Join 限时/周期钳位）
    //   与 FlowGroupRunner 同款，改任何一处都要对照那边的注释。
    // =====================================================================================
    public abstract class PollingSignalSource<TSnapshot, TEdge> : ISignalSource<TSnapshot, TEdge>
        where TSnapshot : class
    {
        private readonly string _name;                  // 泵名（线程名与日志前缀）
        private readonly int _periodMs;                 // 每拍间隔
        private readonly Action<string> _log;           // 日志口（构造注入；缺省 Debug 输出）
        private Thread _pumpThread;                     // 专属泵线程（Start 创建）
        private volatile bool _stopReq;                 // 停止请求（Stop 置位，循环每拍检查）
        private TSnapshot _latest;                      // 最新整拍快照（单写多读，引用赋值原子）
        private long _roundCount;                       // 累计拍数
        private long _errCount;                         // 累计异常数
        private int _errStreak;                         // 当前连续异常拍数（恢复归零）

        /// <inheritdoc />
        public TSnapshot LatestSnapshot { get { return _latest; } }

        /// <inheritdoc />
        public event Action<TEdge[], TSnapshot> EdgesReady;

        /// <inheritdoc />
        public event Action<Exception> SourceError;

        /// <summary>累计拍数（健康诊断；长时间不涨=泵卡死或线程死锁，排查第一入口）。</summary>
        public long RoundCount { get { return _roundCount; } }

        /// <summary>累计异常数（PollOnce/DiffEdges 抛异常记一笔；持续增长=硬件链路故障）。</summary>
        public long ErrCount { get { return _errCount; } }

        /// <summary>
        /// 构造：泵名（线程名"信号源-泵名"）、每拍间隔毫秒（&lt;10 钳到 10，防手滑 0/负打满 CPU）、
        /// 日志口（可空=Debug 输出）。
        /// </summary>
        protected PollingSignalSource(string name, int periodMs, Action<string> logSink)
        {
            _name = string.IsNullOrEmpty(name) ? "未命名" : name;
            _periodMs = periodMs < 10 ? 10 : periodMs;
            _log = logSink ?? (Action<string>)delegate(string m) { Debug.WriteLine("[信号源] " + m); };
        }

        /// <summary>采集一拍整拍快照（宿主实现：批量读硬件/总线，组装成不可变快照对象）。抛异常=本拍失败，泵隔离记账后下一拍重试。</summary>
        protected abstract TSnapshot PollOnce();

        /// <summary>
        /// 新旧快照比对出边沿集（宿主实现：逐点比对极性/值变化）。无变化返回空数组（不发事件）。
        /// oldSnap 为 null 表示首拍基线（基类不发事件，宿主直接返回空数组即可）。必须纯函数（不加锁不 IO）。
        /// </summary>
        protected abstract TEdge[] DiffEdges(TSnapshot oldSnap, TSnapshot newSnap);

        /// <summary>启动泵（专属后台线程"信号源-泵名"；已启动再调幂等跳过——防重入）。</summary>
        public void Start()
        {
            if (_pumpThread != null && _pumpThread.IsAlive)
            {
                _log("信号源「" + _name + "」已在运行，本次启动请求跳过（防重入）");
                return;
            }
            _stopReq = false;
            _pumpThread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "信号源-" + _name,
            };
            _pumpThread.Start();
            _log("信号源「" + _name + "」已启动（周期 " + _periodMs + "ms）");
        }

        /// <summary>停止泵（尽力 Join 500ms；宿主退出/换总线场景收尾用。停止后可再 Start 换新线程）。</summary>
        public void Stop()
        {
            _stopReq = true;
            try
            {
                Thread t = _pumpThread;
                if (t != null && t.IsAlive) t.Join(500);
            }
            catch { /* 原因：停止是尽力而为，Join 失败不影响退出（后台线程伴生进程回收） */ }
        }

        /// <summary>
        /// 泵循环：while+异常隔离+心跳。每拍三步：采集→比对→【先换快照引用再发边沿】（顺序契约，
        /// 见类头）。异常节流=首条+每 20 条记一条（防系统性故障刷爆日志）；订阅方回调异常单独
        /// 隔离（订阅方故障不炸泵）。异常绝不冲循环。
        /// </summary>
        private void PumpLoop()
        {
            while (!_stopReq)
            {
                try
                {
                    PumpOnceInternal();
                    if (_errStreak > 0) _errStreak = 0;
                }
                catch (Exception ex)
                {
                    _errCount++;
                    _errStreak++;
                    if (_errStreak == 1 || _errStreak % 20 == 0)
                        _log("信号源「" + _name + "」拍异常第 " + _errCount + " 次（连续 " + _errStreak + " 拍）：" + ex.Message);
                    TryRaiseSourceError(ex);
                }
                _roundCount++;
                Thread.Sleep(_periodMs);
            }
        }

        /// <summary>单拍动作（PumpLoop 调用）：采集→比对→换快照→发边沿。顺序契约红线见类头。</summary>
        private void PumpOnceInternal()
        {
            TSnapshot cur = PollOnce();                    // ①采集一拍
            TSnapshot prev = _latest;
            bool baseline = prev == null;                  // 首拍=基线（只换快照不发边沿，见类头）
            TEdge[] edges = baseline ? null : DiffEdges(prev, cur);   // ②比对（首拍短路）
            _latest = cur;                                 // ③【先】换快照引用（基线拍也换——只不发边沿）
            if (baseline) return;                          // 基线拍到此为止（快照已可被 LatestSnapshot 读到）
            if (edges == null || edges.Length == 0) return;
            try
            {
                Action<TEdge[], TSnapshot> ready = EdgesReady;
                if (ready != null) ready(edges, _latest);  // ④【再】发边沿（回调里读 LatestSnapshot 必含本批边沿值）
            }
            catch (Exception ex)
            {
                // 原因：订阅方回调故障不能炸泵（泵死=全源断供），记日志下一拍继续
                _log("信号源「" + _name + "」边沿回调异常已隔离：" + ex.Message);
                TryRaiseSourceError(ex);
            }
        }

        /// <summary>异常出口触发（事件订阅方异常再隔离一层，绝不冲泵循环）。</summary>
        private void TryRaiseSourceError(Exception ex)
        {
            try
            {
                Action<Exception> h = SourceError;
                if (h != null) h(ex);
            }
            catch { /* 原因：出口异常已记账在案（_errCount），不再放大 */ }
        }
    }
}
