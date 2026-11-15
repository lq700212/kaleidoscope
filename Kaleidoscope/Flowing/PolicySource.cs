using System;
using System.Diagnostics;

namespace Kaleidoscope.Flowing
{
    // =====================================================================================
    // 业务流编排 · 政策源（"每拍现取勿缓存"的 provider 统一口，V1.10.0 沉库）
    // -------------------------------------------------------------------------------------
    // 【干什么】状态机运行期可变的"政策"（时长/极性/开关/抑制位……）统一从 PolicySource&lt;T&gt;
    //   取：每次读 Current 都现调 provider，绝不缓存——改配置即生效，无需重启或失效通知。
    //   provider 缺位/异常时回缺省值（CurrentOrDefault），泵/心跳场景永不因政策口故障炸线。
    //   来源：宿主 CureLine 固化炉状态机 SlotCuringMachine 的 7 个 Func provider 注入
    //   （_policyProvider/_durationText/_resetModeProvider/_grillPolarityProvider/
    //   _grillAlarmEnabledProvider/_loadPendingProvider/_resumeBaselineProvider，V0.0.69 起
    //   逐个增殖）的统一化提炼——模式本身零改动，收口成一个类型。
    // -------------------------------------------------------------------------------------
    // 【为什么每拍现取绝不缓存】政策是"现场改配置即生效"的载体：缓存就要配套失效通知
    //   （事件/轮询比对/重启），三样都是新的故障面。provider 读的是宿主配置对象（内存读），
    //   现取成本≈一次委托调用，微不足道；换来的语义是"改完即生效，没有任何陈旧窗口"。
    //   （CureLine 口径："每拍现取，勿缓存"——provider 必须线程安全纯读，后台边沿/心跳
    //   线程逐拍调。）
    // 【为什么 Current 异常透传、CurrentOrDefault 兜底】两个消费场景语义不同：启动校验/
    //   首次装配场景想让配置错误立即暴露（透传，fail-fast）；泵/心跳逐拍场景要"政策口故障
    //   不炸线"（兜底回缺省，故障留日志）。一个类型两个口，调用方按场景选。
    // -------------------------------------------------------------------------------------
    // 【怎么接】三步：
    //   ①宿主配置有唯一真源（ini/json/运行内存对象），包成 provider：new PolicySource&lt;CuringPolicy&gt;(
    //     () => config.GetActivePolicy())——provider 必须线程安全纯读（不加锁不 IO，多个后台
    //     线程会逐拍并发调）。
    //   ②泵/心跳逐拍口用 CurrentOrDefault()（缺省值在构造给，如 0 分钟/保守极性）；
    //   ③启动校验/装配口用 Current（异常透传，配置错误启动即暴露）。
    // 【怎么改】新增取值语义（缓一拍的折中口/变更通知）加方法不动 Current——"每拍现取"
    //   是本类的存在理由，任何"偷偷缓存"的改动都算破坏语义；日志前缀"[政策源] "勿改。
    // =====================================================================================
    public sealed class PolicySource<T>
    {
        private readonly Func<T> _provider;   // 政策真源读取口（宿主注入；必须线程安全纯读）
        private readonly T _fallback;         // 缺省值（provider 缺位/异常时回它）

        /// <summary>构造：注入 provider 与缺省值。provider 可空（缺位时 Current/Default 都回缺省值——"未注入=安全缺省"桥接范式）。</summary>
        public PolicySource(Func<T> provider, T fallback)
        {
            _provider = provider;
            _fallback = fallback;
        }

        /// <summary>构造：provider + default(T) 缺省（引用类型即 null——调用方自行判空）。</summary>
        public PolicySource(Func<T> provider) : this(provider, default(T)) { }

        /// <summary>
        /// 当前政策（每次读都现调 provider，绝不缓存——"改配置即生效"语义的载体）。
        /// provider 缺位回缺省值；provider 异常透传（调用方按场景自决：启动校验场景就该炸出来）。
        /// </summary>
        public T Current
        {
            get
            {
                Func<T> p = _provider;
                return p != null ? p() : _fallback;
            }
        }

        /// <summary>
        /// 当前政策（兜底版）：provider 缺位/异常一律回缺省值，绝不抛——泵/心跳逐拍场景用
        /// （政策口故障不炸线；异常已记 Debug，宿主注入日志口可见）。
        /// </summary>
        public T CurrentOrDefault()
        {
            try
            {
                return Current;
            }
            catch (Exception ex)
            {
                // 原因：兜底口的职责是"政策故障不炸泵/心跳"——异常回缺省值，留痕不放大
                Debug.WriteLine("[政策源] provider 异常，回缺省值：" + ex.Message);
                return _fallback;
            }
        }
    }
}
