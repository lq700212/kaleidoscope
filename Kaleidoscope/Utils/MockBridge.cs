using System;

namespace Kaleidoscope.Utils
{
    /// <summary>
    /// 演示（Mock）模式桥接口：让库内服务能在"不接硬件"的演示模式下短路真实通讯，
    /// 而库本身不依赖任何业务工程的 Mock 实现。
    ///
    /// 【为什么需要这一层】
    /// 演示模式（MockMode）是业务工程的概念——它要读业务配置（MainSetting.ini 的
    /// DevPanel/MockMode 键）、要联动界面状态、要合成演示图。这些都不属于通讯库。
    /// 但库内 PLC 队列的每个读写入口都需要"演示时直接判通"的短路能力，否则演示机
    /// 没接 PLC 时会一直卡在重连上拖慢启动。
    ///
    /// 【怎么接】宿主（业务工程）在启动时把三个委托指向自己的 MockMode：
    /// <code>
    /// MockBridge.IsEnabledProvider   = () => MockMode.IsEnabled;
    /// MockBridge.FakePlcReadProvider = (dev, addr, kind) => MockMode.FakePlcRead(dev, addr, kind);
    /// MockBridge.FakePlcWriteAction  = (dev, addr, text) => MockMode.FakePlcWrite(dev, addr, text);
    /// </code>
    /// 不注入时三个委托都是 null，<see cref="IsEnabled"/> 恒为 false = 走真实通讯。
    ///
    /// 【线程】三个委托由宿主保证线程安全（MockMode 内部用 ConcurrentDictionary）。
    /// 库内只在读写入口调用，不缓存结果，宿主热切开关即时生效。
    /// </summary>
    public static class MockBridge
    {
        /// <summary>
        /// 演示模式是否启用（宿主注入；未注入 = 永远 false = 真实通讯）。
        /// 库内各读写入口第一行就判它，为 true 时直接走内存假数据，不碰任何网络/串口。
        /// </summary>
        public static Func<bool> IsEnabledProvider;

        /// <summary>
        /// 假读：按 (设备号, 地址, 数据类型名) 从宿主内存字典取值。
        /// 契约：永不返回 null，未写过的地址按类型回默认值（整字 "0"、浮点 "0"、位 "False"），
        /// 这样演示模式下读链路永不失败、不会误触发重连。
        /// </summary>
        public static Func<int, string, string, string> FakePlcReadProvider;

        /// <summary>
        /// 假写：把值记进宿主内存字典（读回校验能对上），不碰硬件。
        /// </summary>
        public static Action<int, string, string> FakePlcWriteAction;

        /// <summary>演示模式是否启用（未注入恒 false）。</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    var p = IsEnabledProvider;
                    return p != null && p();
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 假读（未注入时回 "0"，等价"读到一个 0"，不会让调用方判成断线）。
        /// </summary>
        public static string FakePlcRead(int devID, string address, string kind)
        {
            try
            {
                var p = FakePlcReadProvider;
                if (p != null)
                {
                    string v = p(devID, address, kind);
                    if (v != null) return v;
                }
            }
            catch { }
            // 未注入兜底：按类型给最保守的默认值（位=False、其余=0），与宿主 Mock 口径一致
            string k = (kind ?? "").ToUpperInvariant();
            if (k.IndexOf("BOOL", StringComparison.Ordinal) >= 0) return "False";
            return "0";
        }

        /// <summary>假写（未注入时静默丢弃，演示模式的"写成功"语义由调用方短路保证）。</summary>
        public static void FakePlcWrite(int devID, string address, string valueText)
        {
            try
            {
                var a = FakePlcWriteAction;
                if (a != null) a(devID, address, valueText);
            }
            catch { }
        }
    }
}
