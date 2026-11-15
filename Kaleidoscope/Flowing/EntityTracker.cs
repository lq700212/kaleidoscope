using System;
using System.Collections.Generic;

namespace Kaleidoscope.Flowing
{
    // =====================================================================================
    // 业务流编排 · 实体发号器（"发号三件套"泛型化：发号/回收/掉电续号 + 在制占用账本，V1.10.0 沉库）
    // -------------------------------------------------------------------------------------
    // 【干什么】给流转实体（产品/托盘/工单）分配跨工位全局唯一的流转号，并维护"在制占用账本"：
    //   Alloc 发号（跳过在制号、值域回绕、无效哨兵永不发放）、Release 下线回收（号回池可复用）、
    //   RestoreNext 掉电续号（启动时从库水位恢复计数器单调性）、MarkOccupied 外部给号域登记
    //   （号是外部给的（MES/扫码 SN），本地只防重登记不生成）。另配账本查询口（IsOccupied/
    //   OccupiedCount/OccupiedSnapshot）给宿主做号池水位诊断。
    //   来源：宿主 HJVision 产品数据状态机 LineFlow 的发号三件套（TrackId 1–2100000000 回绕，
    //   值域史 V4.4.9=1–9999 → UDINT 全域 → V4.4.31 收紧到 21 亿——ST20 的 E3–E17 数据 ID 由
    //   PLC 按 DINT 交接，超 21 亿 PLC 读成负数、E 区组包对不上号）一比一下沉，语义零改动。
    // -------------------------------------------------------------------------------------
    // 【为什么是实例类不是静态类】发号域是业务概念：一个宿主可能同时跑多条产线/多个库区，
    //   各域独立计数器与占用表（静态类做不到多域隔离，脚本重载还会丢状态）。单域宿主 new 一个
    //   存成静态字段即可（与静态类等价）；多域宿主按域各建一个。
    // 【为什么鸽巢 k+1 次必命中】在制 k 个时最长连续占用段≤k，线性递增试号 k+1 次内必撞到
    //   空号——所以 Alloc 的试号上限=占用数+1，绝不死循环；试完仍无号（全值域在制，实际不可能）
    //   返回 default(TKey)（uint 域即 0），调用方按"无号"处理（fail-closed，不断线）。
    // 【外部给号域】不是所有宿主都本地发号：MES 派工号/扫码 SN 是外部给号，本地只需 MarkOccupied
    //   登记（同一号重复递入幂等无害）防"同号两件在制"串件；此时不调 Alloc/RestoreNext。
    // -------------------------------------------------------------------------------------
    // 【怎么接】三步：
    //   ①new EntityTracker<uint>(seed => (uint)seed, id => id != 0, 2100000000L)——三参：
    //     种子→ID 工厂（数值域直接转型；字符串号域可 seed => "SN" + seed）、无效哨兵判定
    //     （返回 false 的 ID 永不发放，uint 域即 0 号哨兵）、值域上限（含，到顶回绕到 1）。
    //   ②业务流转点调 Alloc（诞生点）/Release（下线点）；进程启动时 RestoreNext(库水位+1)
    //     掉电续号（只恢复计数器单调性不重发历史号，占用表保守按空——在制防撞靠"重启先清线"
    //     + 发号跳过已知在制）。
    //   ③（可选）宿主把自己的业务锁内嵌套调本类方法时，锁序必须固定"宿主锁 → 本类内锁"，
    //     绝不反向（本类方法绝不回调宿主，反向嵌套没有触发路径，但宿主若在注入的委托里反过来
    //     抢宿主锁即死锁——idFactory/isValid 必须是纯函数，不做任何加锁与 IO）。
    // 【怎么改】新增管控语义（号段预留/优先段/黑白名单）在锁内加字段+对应方法，全部走 _lock；
    //   值域/回绕语义改动必须同步类头值域史注释；日志口 message 形如"[实体发号] …"勿改前缀
    //   （现场按前缀 grep 定位发号问题）。
    // =====================================================================================
    public sealed class EntityTracker<TKey> where TKey : struct, IEquatable<TKey>
    {
        /// <summary>
        /// 日志口（宿主项目注入；缺省 Debug 输出，与 FlowGroupRunner.LogSink 同范式）。
        /// 发号域问题（耗尽/回绕异常）默认静默走 Debug，宿主注入后与业务日志同流。
        /// 多线程并发调用，宿主注入的实现自行保证线程安全。
        /// </summary>
        public static Action<string> LogSink = delegate(string m) { System.Diagnostics.Debug.WriteLine("[实体发号] " + m); };

        private readonly Func<long, TKey> _idFactory;   // 种子→ID 工厂（必须纯函数：不加锁不 IO）
        private readonly Func<TKey, bool> _isValid;     // 无效哨兵判定（false=永不发放；uint 域即 0 号）
        private readonly long _maxSeed;                 // 值域上限（含；到顶回绕到 1）
        private long _maxSeedOverride;                  // 探针专用上限覆盖（0=用 _maxSeed；号池耗尽路径测试用）
        private long _nextSeed;                         // 下一个候选的前一个（Alloc 时 +1；long 存防回绕判断溢出）
        private readonly HashSet<TKey> _occupied;       // 在制占用表（发号即占/下线即释/外部给号即登记）
        private readonly object _lock = new object();   // 本类唯一闸：保护计数器与占用表

        /// <summary>构造：注入种子→ID 工厂、无效哨兵判定、值域上限（含）。三参均可为最小实现：</summary>
        /// <param name="idFactory">种子（1..maxSeed）→ 流转号；必须纯函数（不加锁不 IO，见锁序红线）。</param>
        /// <param name="isValid">号有效性（false=哨兵永不发放）；uint 域传 id => id != 0。必须纯函数。</param>
        /// <param name="maxSeed">值域上限（含）。到顶回绕到 1；非法种子（≤0）一律按 0 处理下次从 1 发。</param>
        public EntityTracker(Func<long, TKey> idFactory, Func<TKey, bool> isValid, long maxSeed)
        {
            if (idFactory == null) throw new ArgumentNullException("idFactory");
            if (isValid == null) throw new ArgumentNullException("isValid");
            if (maxSeed < 1) throw new ArgumentOutOfRangeException("maxSeed", "值域上限必须 ≥1（含）");
            _idFactory = idFactory;
            _isValid = isValid;
            _maxSeed = maxSeed;
            _occupied = new HashSet<TKey>();
        }

        /// <summary>
        /// 分配一个流转号（1–上限循环复用，跳过在制号；号池耗尽返回 default(TKey)，调用方按无号处理）。
        /// 发号即占用（同号在下线前绝不二次发出）。线程安全（锁内发号，一次只发一号）。
        /// </summary>
        public TKey Alloc()
        {
            lock (_lock)
            {
                long max = EffectiveMax();
                int attempts = _occupied.Count + 1;   // 最长连续占用段≤在制数，k+1 次必命中空号（鸽巢原理，见类头）
                if (attempts < 1) attempts = 1;
                for (int i = 0; i < attempts; i++)
                {
                    long id = _nextSeed + 1;
                    if (id > max || id <= 0) id = 1;  // 回绕（id<=0 防非法种子）；种子恒在 [1,max]
                    _nextSeed = id;
                    TKey key = _idFactory(id);
                    if (!_isValid(key)) continue;     // 无效哨兵永不发放（uint 域 0 号兜底；工厂纯函数，正常不触发）
                    if (!_occupied.Contains(key)) { _occupied.Add(key); return key; }
                }
                return default(TKey);   // 号池耗尽（全值域在制，实际不可能；调用方按"无号"回 NG，不断线）
            }
        }

        /// <summary>
        /// 下线释放（产品真正离开产线时调——宿主的"上报成功后释放"等业务触发点自定）。
        /// 从占用表摘除，号回池可复用；号不在表内返回 false（重复释放幂等无害）。
        /// 【宿主连带动作自理】本类只管账本：宿主释放时对"当前在制对象/实时数据桶"的清理
        /// （如 HJVision 清 mCurrent 与 DataHubService.DropLive）留在宿主 Release 包装方法里做。
        /// </summary>
        public bool Release(TKey id)
        {
            lock (_lock) { return _occupied.Remove(id); }
        }

        /// <summary>
        /// 掉电恢复种子（启动时调一次）：下次发号从 seed 开始（宿主传"库水位+1"）。
        /// 只动计数器，不动占用表（在制未知，保守按空表；在制防撞靠宿主"重启先清线"要求
        /// + 发号跳过已知在制）。seed 非法（≤1/超上限）一律从 1 起发，绝不抛异常挡启动。
        /// </summary>
        public void RestoreNext(long seed)
        {
            lock (_lock)
            {
                long start = seed - 1;                 // _nextSeed 是"上一个"，下次 +1 正好是 seed
                if (start < 0 || start >= EffectiveMax()) start = 0;
                _nextSeed = start;
            }
        }

        /// <summary>
        /// 外部给号登记（MES 派工号/扫码 SN 等"号是外面发的"场景）：本地不生成，只登记防重。
        /// 已在表内幂等无操作；返回 false=该号已被占用（宿主按"同号两件在制"串件风险处理）。
        /// 兜底占用也走它（如宿主"换号建档"时对新号的兜底登记——走过 Alloc 的号已在表，无操作）。
        /// </summary>
        public bool MarkOccupied(TKey id)
        {
            if (!_isValid(id)) return false;   // 哨兵号不登记（0 号占用没有意义）
            lock (_lock) { return _occupied.Add(id); }
        }

        /// <summary>该号是否在制占用中。</summary>
        public bool IsOccupied(TKey id)
        {
            lock (_lock) { return _occupied.Contains(id); }
        }

        /// <summary>在制占用数（号池水位诊断用；水位只涨不掉=宿主漏调 Release，排查第一入口）。</summary>
        public int OccupiedCount
        {
            get { lock (_lock) { return _occupied.Count; } }
        }

        /// <summary>在制占用表快照（锁内拷贝、当场脱钩；诊断/日志用，别在热路径高频调）。</summary>
        public TKey[] OccupiedSnapshot()
        {
            lock (_lock)
            {
                TKey[] copy = new TKey[_occupied.Count];
                _occupied.CopyTo(copy);
                return copy;
            }
        }

        private long EffectiveMax() { return _maxSeedOverride > 0 ? _maxSeedOverride : _maxSeed; }

        // ================= 测试辅助（生产代码禁止调用） =================

        /// <summary>清空账本回初始态（含计数器/占用表/上限覆盖，仅自动化探针用）。</summary>
        public void ResetForTest()
        {
            lock (_lock) { _nextSeed = 0; _occupied.Clear(); _maxSeedOverride = 0; }
        }

        /// <summary>探针专用：直接置发号计数器（回绕/占用跳过测试用，生产代码禁止调用）。</summary>
        public void SetNextSeedForTest(long v)
        {
            lock (_lock) { _nextSeed = v < 0 ? 0 : v; }
        }

        /// <summary>探针专用：覆盖值域上限（号池耗尽路径测试用；0=恢复构造常量，生产代码禁止调用）。</summary>
        public void SetMaxSeedOverrideForTest(long v)
        {
            lock (_lock) { _maxSeedOverride = v; }
        }
    }
}
