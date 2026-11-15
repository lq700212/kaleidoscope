using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Kaleidoscope.Flowing
{
    /// <summary>
    /// 执行器统一口：把"期望状态"落到外部硬件/远端（写灯/写 PLC/回写报文），带去重与补写语义。
    /// 宿主在每个状态转移点调 Ensure（重复调无害），心跳/泵周期里再 Ensure 兜底补写。
    /// </summary>
    /// <typeparam name="TKey">执行目标键（如 IO 点位/寄存器地址）。</typeparam>
    /// <typeparam name="TValue">期望值类型（bool 灯态/int 字值/string 报文内容，需可判等）。</typeparam>
    public interface IEffectSink<TKey, TValue>
    {
        /// <summary>确保目标处于期望值：同值跳过；未达/偏离则执行；返回 true=已达成（含刚执行成功与同值跳过）。</summary>
        bool Ensure(TKey key, TValue value);

        /// <summary>清除期望值记账（下轮 Ensure 强制重写——怀疑外部被改/断电恢复场景用）。</summary>
        void Invalidate(TKey key);

        /// <summary>累计实写次数（去重生效后应远小于调用次数；持续高增长=外部总回异，排查第一入口）。</summary>
        long AppliedCount { get; }

        /// <summary>当前已记账（最近一次实写成功值）的目标键快照（诊断用）。</summary>
        TKey[] ExpectedSnapshot();
    }

    // =====================================================================================
    // 业务流编排 · 去重执行器（"期望值去重 + 失败不记账下轮补写"的通用执行口，V1.10.0 沉库）
    // -------------------------------------------------------------------------------------
    // 【干什么】状态机把"写外部硬件"从转移动作里剥离成本口：Ensure(key, value) 三态——
    //   同值跳过（稳态零网络写）/执行成功记账/失败不记账（下轮 Ensure 自动补写）。
    //   宿主在状态转移点与心跳泵里重复调 Ensure 也无害：稳态时同值短路，偏离时才真写。
    //   来源：宿主 CureLine 固化炉状态机 SlotCuringMachine.EnsureLamp 的期望值簿
    //   （_writtenLamps 字典 + 同值跳过 + 写失败不记账 + 每秒 Pump 补写自愈，V0.0.64 一线
    //   沉淀）一比一提炼（泛型化到任意键/值域，语义零改动）。
    // -------------------------------------------------------------------------------------
    // 【为什么失败不记账】记了账下次就跳过——失败状态被"记账"固化成永久错误（灯实际是灭的，
    //   簿子却记着亮）。不记账=失败留给下轮补写，断电/瞬断/总线抖动后一轮心跳内自愈
    //   （CureLine 现场：红灯补写+红灯补灭自愈，最多挂一个 Pump 周期）。
    // 【为什么执行在锁外】apply 是真网络 IO（写 PLC/灯），锁内做 IO=堵死其它 key 的 Ensure
    //   入口（与 LineFlow"锁内不做网络等待"同一铁律）。代价：并发 Ensure 同 key 有重复执行
    //   窗口——执行口本身必须幂等（同值重复写无害，写 PLC 天然幂等），簿记最终一致。
    // 【为什么接口+实现分开】宿主可只实现 IEffectSink 直接接已有执行链（如 CureLine 把
    //   EnsureLamp 挂接口）；DedupEffectSink 是缺省鲁棒版（去重/补写/节流日志/统计内建），
    //   自实现会丢这些防线，优先用缺省版。
    // -------------------------------------------------------------------------------------
    // 【怎么接】三步：
    //   ①new DedupEffectSink<TKey,TValue>((key, value) => bus.WritePoint(key, value))——
    //     apply 返回 true=写成功（记账）；false 或抛异常=失败（不记账，下轮补写）。
    //   ②状态转移点调 Ensure(key, 期望值)；心跳/泵周期里对关键 key 再 Ensure 兜底（断电/
    //     瞬断自愈；稳态同值短路零开销）。
    //   ③怀疑外部被改（现场手动动过/断电恢复）→ Invalidate(key) 强制下轮重写。
    // 【怎么改】新增语义（批量 Ensure/失败连击断路器）加字段+方法全走 _lock；apply 调用
    //   绝不移进锁内（锁外执行红线，见类头）；日志前缀"[执行器] "勿改（现场 grep 口径）。
    // =====================================================================================
    public sealed class DedupEffectSink<TKey, TValue> : IEffectSink<TKey, TValue>
        where TValue : IEquatable<TValue>
    {
        private readonly Func<TKey, TValue, bool> _apply;    // 真执行口（宿主注入：写硬件；必须幂等）
        private readonly Action<string> _log;                // 日志口（可空=Debug 输出）
        private readonly Dictionary<TKey, TValue> _expected; // 期望值簿：最近一次实写成功的值（去重+补写比对基准）
        private readonly object _lock = new object();        // 簿记闸（只保护字典与计数，不含 apply 调用）
        private long _appliedCount;                          // 累计实写成功次数
        private long _skippedCount;                          // 累计同值跳过次数（去重收益量化）
        private long _failedCount;                           // 累计失败次数（持续增长=外部链路故障）

        /// <summary>构造：注入执行口（返回 true=写成功）与日志口（可空=Debug 输出）。</summary>
        public DedupEffectSink(Func<TKey, TValue, bool> apply, Action<string> logSink)
        {
            if (apply == null) throw new ArgumentNullException("apply");
            _apply = apply;
            _log = logSink ?? (Action<string>)delegate(string m) { Debug.WriteLine("[执行器] " + m); };
            _expected = new Dictionary<TKey, TValue>();
        }

        /// <summary>累计同值跳过次数（去重收益量化；稳态运行时它应远大于 AppliedCount 增速）。</summary>
        public long SkippedCount { get { lock (_lock) { return _skippedCount; } } }

        /// <summary>累计失败次数（持续增长=外部链路故障，排查第一入口）。</summary>
        public long FailedCount { get { lock (_lock) { return _failedCount; } } }

        /// <inheritdoc />
        public long AppliedCount { get { lock (_lock) { return _appliedCount; } } }

        /// <summary>
        /// 确保目标处于期望值。三态：①同值跳过（簿内已有同值——稳态零网络写，返回 true）；
        /// ②执行成功记账（返回 true）；③失败不记账（返回 false，下轮 Ensure 自动补写）。
        /// 执行在锁外（锁内做 IO=堵死其它 key，见类头）；并发同 key 有重复执行窗口，
        /// apply 必须幂等（写硬件天然幂等）。
        /// </summary>
        public bool Ensure(TKey key, TValue value)
        {
            lock (_lock)
            {
                TValue have;
                if (_expected.TryGetValue(key, out have) && have.Equals(value))
                {
                    _skippedCount++;   // 同值跳过：稳态零网络写（去重的意义所在）
                    return true;
                }
            }
            bool ok;
            try
            {
                ok = _apply(key, value);
            }
            catch (Exception ex)
            {
                // 原因：执行口异常按失败处理（不记账下轮补写），不往上冲——宿主转移链不该被单点写失败打断
                ok = false;
                _log("执行异常（" + key + " ← " + value + "）：" + ex.Message);
            }
            lock (_lock)
            {
                if (ok) { _expected[key] = value; _appliedCount++; }
                else _failedCount++;
            }
            if (!ok) _log("执行失败待补写：" + key + " ← " + value);
            return ok;
        }

        /// <inheritdoc />
        public void Invalidate(TKey key)
        {
            lock (_lock) { _expected.Remove(key); }
        }

        /// <inheritdoc />
        public TKey[] ExpectedSnapshot()
        {
            lock (_lock)
            {
                TKey[] keys = new TKey[_expected.Count];
                _expected.Keys.CopyTo(keys, 0);
                return keys;
            }
        }

        /// <summary>清空簿记回初始态（含三计数器，仅自动化探针用）。</summary>
        public void ResetForTest()
        {
            lock (_lock) { _expected.Clear(); _appliedCount = 0; _skippedCount = 0; _failedCount = 0; }
        }
    }
}
