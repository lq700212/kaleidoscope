using System;

namespace Kaleidoscope.Security
{
    /// <summary>
    /// 授权门（LicenseGate）的宿主桥接层：库内授权体系无法自给的两件"业务侧能力"
    /// 与可选覆盖点，全部走本桥注入——**库不引用任何业务工程，业务工程零改造用授权**。
    ///
    /// 【为什么需要这一层】
    /// LicenseGate 判定链里有两个"宿主才有答案"的问题：
    /// ① 演示模式是否放行——Mock 演示机免硬件免授权（Mock 不参与激活门禁，与历史口径一致）。
    ///    MockMode 是业务工程的概念（它读业务配置、联动界面状态），不属于通讯库；
    /// ② 防回拨水位的第三存储（SQLite DbMeta）——设备记录队列（SqliteRecordQue 一类）
    ///    是宿主业务的数据设施，库内没有也不该有。
    /// 未接管时各回退到安全缺省：非 Mock（照跑真实门禁）/ 无 SQLite 水位（另两存储兜底），
    /// 授权判定永不因桥缺位而放行——桥只能"收紧"信息面，缺位=按无信息从严。
    ///
    /// 【怎么接】宿主在启动装配时注入（推荐放在自身 Mock 类 / 日志类的静态构造，
    /// 进程内首次触碰即生效；App.xaml.cs 启动链亦可）：
    /// <code>
    /// LicenseBridges.IsMockLicensedProvider = () => MockMode.IsEnabled;        // 演示机免授权
    /// LicenseBridges.SqliteWaterMarkGet = key => SqliteRecordQue.GetMeta(key); // 水位存储 C 读
    /// LicenseBridges.SqliteWaterMarkSet = (key, v) => SqliteRecordQue.SetMeta(key, v); // 写
    /// </code>
    /// 不注入 = 全部缺位语义（见上），无需任何桥也能安全运行。
    ///
    /// 【线程】三个委托由宿主保证线程安全（宿主 MQ/配置设施自带锁）；库内只在判定链
    /// 调用点读值，不缓存结果——宿主热切开关即时生效。
    /// </summary>
    public static class LicenseBridges
    {
        /// <summary>
        /// 演示模式是否启用（宿主注入；未注入 = 永远 false = 真实门禁）。
        /// 对位 WPF 侧 MockMode.IsEnabled——Refresh()/IsProductionAllowed() 的步骤 0 由此短路。
        /// </summary>
        public static Func<bool> IsMockLicensedProvider;

        /// <summary>
        /// 演示模式安全取值（未注入/抛异常一律 false——从严不识别为 Mock，绝不异常放行）。
        /// </summary>
        public static bool IsMockLicensed
        {
            get
            {
                try
                {
                    Func<bool> p = IsMockLicensedProvider;
                    return p != null && p();
                }
                catch (Exception) { return false; }   // 原因：桥异常按"非 Mock"处理，判定链自己会锁死
            }
        }

        /// <summary>
        /// SQLite 水位读委托（宿主注入；未注入/抛异常 = null = 该存储缺位）。
        /// 签名 (DbMeta 键) → 值（可 null/空白 = 缺位）。对位 WPF 侧 SqliteRecordQue.GetMeta。
        /// </summary>
        public static Func<string, string> SqliteWaterMarkGet;

        /// <summary>
        /// SQLite 水位写委托（宿主注入；未注入 = 静默跳过——该存储退出水位双保险链，
        /// ini + 注册表两处仍兜底）。签名 (DbMeta 键, 值)。对位 SqliteRecordQue.SetMeta。
        /// </summary>
        public static Action<string, string> SqliteWaterMarkSet;

        /// <summary>SQLite 水位安全读（未注入/异常=null=存储缺位，绝不抛——判定链另有 ini/注册表兜底）。</summary>
        public static string GetWaterMark(string key)
        {
            try
            {
                Func<string, string> p = SqliteWaterMarkGet;
                return p != null ? p(key) : null;
            }
            catch (Exception) { return null; }   // 原因：读失败按缺位处理，不误报回拨也不异常放行
        }

        /// <summary>SQLite 水位安全写（未注入/异常静默——单存储失效不阻塞主判定；WriteIniVerify/注册表各自自检）。</summary>
        public static void SetWaterMark(string key, string value)
        {
            try
            {
                Action<string, string> p = SqliteWaterMarkSet;
                if (p != null) p(key, value);
            }
            catch (Exception) { /* 原因：写失败不抛——水位三存储任一失效另有兜底，失败留痕走受托日志 */ }
        }
    }
}
