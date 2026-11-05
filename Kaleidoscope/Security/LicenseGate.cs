using System;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Kaleidoscope.Utils;
using Microsoft.Win32;

namespace Kaleidoscope.Security
{
    /// <summary>
    /// 软件激活门禁（原版 V4.4.349 激活体系重做的使用方侧核心；编解码在 LicenseCodec）。
    ///
    /// 【来历（三段一比一，算法/默认值/异常口径/注释全保留）】
    ///   原版 GYZVision\SubClass\LicenseGate.cs（V4.4.349 建套 / V4.4.351 落盘位置+产品隔离 /
    ///   V4.4.362 era 时段挑战 + Debug 构建自动授权）
    ///   → HuaJiVision.Core.Security 抽取（V0.9.53，两处结构差异见文末）
    ///   → V0.9.56 下沉本库 Security/。主项目 Core 侧改 using 别名壳，调用点零迁移。
    ///   下沉时把"宿主才有答案"的四件事改成库内覆盖点/桥（见【宿主桥接与覆盖点】），
    ///   算法与判定链逐行未动，已签发的激活码继续有效。
    ///
    /// 【旧版的五个致命洞（本类逐一根治）】
    ///   ① 旧激活码 = 无密钥 MD5，拿到设备码人人可算 → 新码 ECDsa P-256 签名，无私钥不可伪造；
    ///   ② 旧设备绑定只有 CPU ProcessorId（同型号相同，拷 ini 即复制激活）→ 多因子指纹：
    ///      MachineGuid（装系统唯一）+ 主板 SN + 磁盘 SN + CPU id 拼 SHA256；
    ///   ③ 旧天数 = ini 递增计数器，ini 设只读即永不过期 → 新试用期 = 激活码内嵌到期日期，
    ///      没有"必须递增写的数"，只读 ini 天然无效；
    ///   ④ 改系统时钟可复活过期试用 → 防回拨水位：ini + 注册表 + SQLite DbMeta 三处取最大，
    ///      下限=构建日期/exe 文件时间，本地时间倒退超过 1 小时容差即判回拨（按过期处理并告警）；
    ///   ⑤ 旧过期只锁登录、生产照跑 → IsProductionAllowed() 全工程唯一判定口 + 主窗在
    ///      启动/上线/单拍/巡检四点拦截，过期即程序化停车（接法见各宿主主窗）。
    ///
    /// 【状态机】LicenseState 五态：NotActivated（无记录/码无效/挑战不符）→ Expired
    /// （试用到期或时钟回拨）→ Trial / Permanent（可用）→ FingerprintError（ini 记录与本机
    /// 指纹不符：整目录克隆/换硬件）。Refresh() 是唯一重算口（启动/每小时巡检/激活成功后调）。
    ///
    /// 【设备码与挑战值（V4.4.362 重做：era 时段挑战）】挑战值不再"每开一次窗随机换新"，
    /// 而是按"本机指纹 + 签发时效档（30 天一档）"确定性派生（BuildEraChallenge）：
    /// 同一台机器在同一个 30 天档内，窗口重开 / 软件重启设备码都恒定——厂商按设备码签出的
    /// 激活码在档内任何时点输入都有效，"永久 ↔ 限时"授权切换直接输新码即可（即时热生效，
    /// 不重启软件、不清授权记录）；跨入新档后旧签发码自动失配，提示重新抄设备码再签。
    /// 防重放语义保留：窃录的旧设备码+旧激活码只在同档内有意义，跨档重放在此拦下。
    ///
    /// 【落盘位置（V4.4.351 起）】授权状态（指纹/激活码/水位）写在独立文件
    /// MainSetting\LicenseState.ini（默认 exe 目录下，可经 StateIniPathOverride 覆盖；库内
    /// 自管 LicenseStateIni 极简读写器），不再写宿主主配置——该文件属运行时商业数据不入
    /// 仓库（.gitignore 排除），部署升级后需重新激活一次。
    ///
    /// 【产品隔离】多产品共用同一把 ECDsa 密钥；验签通过后必须再比对 payload 产品 ID ==
    /// HostProductId（默认 0xFFFF = HuaJiVision）。CureLine（0x434C）的码在这里落
    /// "激活码不属于本软件"拒绝分支，反之亦然。换宿主接入授权只需覆盖此一个字段。
    ///
    /// 【线程模型】指纹采集（WMI 三查 + 注册表）耗时几百毫秒：主窗 OnShown 前后台线程
    /// Warmup() 预热缓存；TryGetFingerprint() 缓存未命中时同步查（保底有值）。
    /// Refresh()/TryActivate()/UpdateWaterMark() 只在 UI 线程调（巡检走主窗每小时定时器）。
    ///
    /// 【怎么查授权状态】任何生产路径一律调 IsProductionAllowed()（含 Mock 恒放行），
    /// 不要自己读 ini 判断——判定口径只许这一处。
    ///
    /// 【宿主桥接与覆盖点（V0.9.56 通用化替换；未注入/未覆盖=默认值=WPF 主程序同款行为）】
    ///   ① 演示短路：宿主在自己的 Mock 类静态构造里接 LicenseBridges.IsMockLicensedProvider
    ///      （WPF 口径 = MockMode.IsEnabled；未注入 = 非 Mock，照跑真实门禁）；
    ///   ② SQLite 水位存储 C：宿主启动链接 LicenseBridges.SqliteWaterMarkGet/Set
    ///      （WPF 口径 = SqliteRecordQue.GetMeta/SetMeta；未注入 = 该存储缺位，另两处兜底）；
    ///   ③ 状态文件路径：LicenseGate.StateIniPathOverride（默认 exe\MainSetting\LicenseState.ini；
    ///      库内自管 LicenseStateIni，原 WPF StateIniProvider 注入点整体退役）；
    ///   ④ 其余覆盖点：RegRootOverride（注册表水位根键，默认 Software\HuaJiVision\License）、
    ///      BuiltOnOverride（构建日期水位下限，默认 "2026-11-04 00:00"）、
    ///      HostProductId（产品隔离目标产品，默认 0xFFFF）；
    ///   ⑤ 仅 Debug 编译存在（发版 Release 剥除）：DevTrialDays 天数 / EnvKeyDebugLicenseOff
    ///      开发兜底环境变量键名（WPF 口径由 HUAJI_DEV_LICENSE 通用化为 KX_DEV_LICENSE）。
    /// </summary>
    public enum LicenseState
    {
        /// <summary>无激活记录 / 激活码无效（被篡改或非本机码）。</summary>
        NotActivated = 0,
        /// <summary>试用授权中（TrialExpiry 前有效）。</summary>
        Trial = 1,
        /// <summary>永久授权。</summary>
        Permanent = 2,
        /// <summary>试用到期 / 时钟回拨被判定。</summary>
        Expired = 3,
        /// <summary>本机指纹与 ini 记录不符（目录克隆 / 硬件变化 / ini 被改）。</summary>
        FingerprintError = 4,
    }

    public static class LicenseGate
    {
        // ════════════════ 常量 ════════════════

        /// <summary>
        /// 授权状态所在 ini 段（独立文件 MainSetting\LicenseState.ini，原版 V4.4.351 起不再写在
        /// 主配置里）。段名/键名沿用 "License"/Fingerprint/LicenseCode/WaterMark 不变，
        /// 日志与排障口径保持一致，只是换了个文件。
        /// 为什么独立成文件：激活码/指纹属运行时商业数据，主配置是被 git 跟踪的
        /// 现场配置——放主配置里每次激活都会把"待提交脏改动"带到公开仓库；独立文件进
        /// .gitignore（同账号表永不入库的套路），现场部署/git 干净两不误。
        /// 文件缺失时的读判定见 ReadLicenseKey；首写由 LicenseStateIni 自动建目录。
        /// </summary>
        private const string IniSection = "License";
        private const string IniKeyFingerprint = "Fingerprint";
        private const string IniKeyLicenseCode = "LicenseCode";
        private const string IniKeyWaterMark = "WaterMark";

        /// <summary>授权状态 ini 文件路径覆盖点（null/空 = 默认 exe\MainSetting\LicenseState.ini）。
        /// 用途：回归探针把状态文件指到沙箱临时目录（隔离真机授权记录，测完不碰真激活态）；
        /// 宿主需要把记录放到别处时也可覆盖。赋值在首次使用前完成（Refresh 链内不会再读）。</summary>
        public static string StateIniPathOverride;

        /// <summary>注册表键根（水位存储 B；HKCU 用户权限即可写，无需管理员）。
        /// 覆盖点：不同宿主产品共机部署时避免互相踩值，默认沿用 WPF 原口径。</summary>
        public static string RegRootOverride = @"Software\HuaJiVision\License";
        private const string RegValueName = "WaterMark";

        /// <summary>SQLite DbMeta 水位键（水位存储 C；经 LicenseBridges.SqliteWaterMarkGet/Set 进宿主设施）。</summary>
        public const string SqliteWaterMarkKey = "LicenseWaterMark";

        /// <summary>时钟回拨判定的容差（小时）：本地时间早于水位超过该值即判回拨。
        /// 给 NTP 校时/时区修正留余量，1 小时足够且不至于放过"回拨几天续命"。</summary>
        private const double RollbackToleranceHours = 1.0;

        /// <summary>水位下限（构建日期，格式 yyyy-MM-dd HH:mm）。exe 文件修改时间通常比它新，
        /// 两者取大做下限——防"整机时钟+水位全部清零"把试用期拉回起点。
        /// 覆盖点：库被新宿主工程引用时按该工程的构建日期覆盖（库默认=WPF 主程序原口径旧值），
        /// 探针若要复现"基线日期前"场景也改这里。</summary>
        public static string BuiltOnOverride = "2026-11-04 00:00";

        /// <summary>挑战值时效档长度（V4.4.362，天）：era = 自 ChallengeEraEpoch 起每 30 天一档。
        /// 为什么 30 天：厂商签发→快递/消息→客户输码的现实周期是"天级"，30 天档内设备码恒定，
        /// "永久 ↔ 限时"切换拿之前抄的设备码随时可签可输（省去"窗口重开就换码重签"的摩擦）；
        /// 同时保留防重放时效——窃录码只在同档内有意义，跨档自动作废。</summary>
        private const int ChallengeEraDays = 30;

        /// <summary>挑战值档号纪元（2026-01-01，UTC 计档）：档号 = 自该日起每 30 天 +1。
        /// 用 UTC 日期计档，跨时区/夏令时不会同档抖动；所有机器对同一指纹算出的挑战值一致。</summary>
        private static readonly DateTime ChallengeEraEpoch = new DateTime(2026, 1, 1);

        /// <summary>产品隔离目标产品 ID（验签通过后 payload 产品 ID 必须等于它）。
        /// 覆盖点：换宿主接入授权体系时改成该产品自己的 ID（缺省=WPF 主程序 HuaJiVision 口径）。</summary>
        public static ushort HostProductId = LicenseCodec.ProductHuaJiVision;

#if DEBUG
        /// <summary>开发构建自动授权天数（V4.4.362，仅 #if DEBUG 段消费）：Debug 版启动时若
        /// 无有效真实授权，按"exe 编译时间+N 天"兜一个内存态 Trial，日常开发编译免输激活码
        /// （重编译即自动续期）。发布必走 Release 配置——本段连同调用在 Release 编译期整体
        /// 剥除，现场产物不存在该逻辑，不构成放行面。覆盖点：宿主自定天数（WPF 定值 90）。</summary>
        public static int DevTrialDays = 90;

        /// <summary>开发兜底旁路环境变量（仅 #if DEBUG 段读；值="0" 时关闭开发自动授权兜底）。
        /// 回归批次用它保真"未激活必锁"等真实状态机断言（探针 Main 开头设置）；
        /// 方向只能是"收紧"——现场 Release 产物没有读它的代码，伪造无放行价值。
        /// 【键名演化】WPF 口径原为 HUAJI_DEV_LICENSE，沉库通用化为 KX_DEV_LICENSE——
        /// 两侧常量同代（探针用本常量取键名编译期绑定，若名单漂移编译即红，不会静默失配）。</summary>
        public const string EnvKeyDebugLicenseOff = "KX_DEV_LICENSE";
#endif

        // ════════════════ 状态注入（宿主路径覆盖，默认自管） ════════════════

        /// <summary>
        /// 授权状态 ini 安全实例（每次判定现取，路径优先级：StateIniPathOverride → 默认）。
        /// 返回 null（路径解析异常等极罕见）= 记录缺位，判定链从严锁死不异常放行。
        /// 授权记录读写只发生在 LicenseGate 内部，改动键名先看这里。
        /// </summary>
        private static LicenseStateIni StateIni()
        {
            try
            {
                string path = StateIniPathOverride;
                if (string.IsNullOrEmpty(path)) path = DefaultStateIniPath();
                if (string.IsNullOrEmpty(path)) return null;
                return new LicenseStateIni(path);
            }
            catch (Exception) { return null; }   // 原因：路径异常按缺位处理，判定链自己会锁死
        }

        /// <summary>默认授权状态文件路径：入口程序集目录 \ MainSetting\LicenseState.ini。
        /// 结构差异②：库禁 WinForms，取 Assembly.GetEntryAssembly()（=主 exe 目录，与原版
        /// Application.StartupPath 同指一处；探针进程=探针目录，沙箱经 Override 指定）。</summary>
        private static string DefaultStateIniPath()
        {
            try
            {
                System.Reflection.Assembly entry = System.Reflection.Assembly.GetEntryAssembly();
                string dir = entry == null ? null : Path.GetDirectoryName(entry.Location);
                if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(dir, @"MainSetting\LicenseState.ini");
            }
            catch (Exception) { return null; }
        }

        // ════════════════ 状态 ════════════════

        /// <summary>当前授权状态（Refresh() 重算；初始 NotActivated）。</summary>
        public static LicenseState CurrentState { get; private set; }

        /// <summary>试用到期日（State=Trial 时有意义；到期日当天 24 点前可用）。</summary>
        public static DateTime TrialExpiry { get; private set; }

        /// <summary>最近一次 Refresh 判定的补充原因（日志/提示用；可为空）。</summary>
        public static string LastReason { get; private set; }

        /// <summary>指纹采集失败原因（FingerprintError 时有值；日志/提示用）。</summary>
        private static string mFpError;

        /// <summary>本机指纹 8hex 缓存（null=未采集成功）。</summary>
        private static string mFp8;

        // ════════════════ 指纹采集 ════════════════

        /// <summary>
        /// 后台预热指纹（主窗加载前 Task.Run 调一次即可）。重复调用幂等；
        /// 已有缓存时不重复采集（WMI 查询不便宜）。
        /// </summary>
        public static void Warmup()
        {
            try { CollectFingerprint(); }
            catch { /* 原因：预热失败不致命，TryGetFingerprint 未命中时会同步重查并报错 */ }
        }

        /// <summary>
        /// 取本机指纹（8 位大写 hex）。返回 false = 采集失败（error 带原因；
        /// 主程序拒绝激活并告警——绝不能像旧版那样失败回退空串/常量，那是 S7 洞）。
        /// </summary>
        public static bool TryGetFingerprint(out string fp8Hex, out string error)
        {
            if (mFp8 == null) CollectFingerprint();
            fp8Hex = mFp8;
            error = mFpError;
            return mFp8 != null;
        }

        /// <summary>采集并缓存指纹。MachineGuid 是主锚点（注册表直读、几乎不失败），
        /// 读不到整体失败；WMI 三项失败用固定占位（指纹稳定性优先于完美）。</summary>
        private static void CollectFingerprint()
        {
            try
            {
                // —— 因子 1（必须）：MachineGuid，Windows 装机唯一标识。
                // RegistryView.Registry64 显式 64 位视图：防止 AnyCPU 进程在特殊场景按 32 位
                // 被重定向到 Wow6432Node 读不到（System.dll 自带，无需加引用）。
                string machineGuid = null;
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (RegistryKey k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    {
                        if (k != null) machineGuid = Convert.ToString(k.GetValue("MachineGuid"));
                    }
                }
                catch (Exception ex) { mFpError = "读 MachineGuid 失败: " + ex.Message; }
                if (string.IsNullOrWhiteSpace(machineGuid))
                {
                    if (string.IsNullOrEmpty(mFpError)) mFpError = "MachineGuid 为空";
                    mFp8 = null;
                    return;
                }

                // —— 因子 2/3/4（尽力采集，失败用固定占位）：主板 SN / 磁盘 SN / CPU id。
                string board = WmiFirstValue("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber");
                string disk = WmiFirstValue("SELECT SerialNumber FROM Win32_DiskDrive", "SerialNumber");
                string cpu = GetCpuSerialNumber();
                string boardP = string.IsNullOrWhiteSpace(board) ? "N/A" : board.Trim();
                string diskP = string.IsNullOrWhiteSpace(disk) ? "N/A" : disk.Trim();
                string cpuP = string.IsNullOrWhiteSpace(cpu) ? "N/A" : cpu.Trim();

                // 固定顺序拼接 → SHA256 → 前 4 字节 hex。任一因子值变化 = 指纹变化 = 授权失效，
                // 这是有意的：整目录克隆到另一台机器（同型号 CPU）指纹必然不同。
                string raw = "MG=" + machineGuid + "|MB=" + boardP + "|DS=" + diskP + "|CP=" + cpuP;
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                    mFp8 = LicenseCodec.BytesToHex(new byte[] { h[0], h[1], h[2], h[3] });
                }
                mFpError = null;
            }
            catch (Exception ex)
            {
                mFp8 = null;
                mFpError = "指纹采集异常: " + ex.Message;
            }
        }

        /// <summary>WMI 单值查询助手（查不到/异常返回 null，调用方占位）。每次几十毫秒。</summary>
        private static string WmiFirstValue(string wql, string propertyName)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(wql))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object v = obj[propertyName];
                        if (v != null) return v.ToString();
                    }
                }
            }
            catch (Exception)
            {
                // 原因：WMI 服务偶发不可用——该因子按占位参与指纹（MachineGuid 主锚点不受影响）
            }
            return null;
        }

        /// <summary>CPU 序列号（激活窗"设备 ID"展示 + 指纹因子之一；原版主窗私有实现收编至此）。</summary>
        public static string GetCpuSerialNumber()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object v = obj["ProcessorId"];
                        if (v != null) return v.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                // 原因：CPU 号只是指纹四因子之一（MachineGuid 才是主锚点），读不到不致命
                Console.WriteLine("GetCpuSerialNumber failed: " + ex.Message);
            }
            return null;
        }

        // ════════════════ 挑战值（防重放；era 时段派生，V4.4.362 重做） ════════════════

        /// <summary>
        /// 取当前挑战值（V4.4.362 era 重做）。【语义变化】不再"每次调用随机生成"，而是按
        /// "本机指纹 + 当前 30 天档号"确定性派生（BuildEraChallenge）——同一个档内调用返回
        /// 同一值：激活窗每次打开调它取设备码，窗口重开/软件重启设备码都恒定；厂商按设备码
        /// 签出的激活码在档内任何时点输入都有效（"永久 ↔ 限时"切换直接输新码，即时热生效）。
        /// 【调用点】①激活窗每次打开；②回归探针构造设备码。
        /// 名称保留 NewChallenge 不改，签名/调用方零迁移。
        /// 【兜底】指纹未采集成功时按"空指纹基线"派生（设备码区另有拦截，正常到不了这里）。
        /// </summary>
        public static string NewChallenge()
        {
            try { if (mFp8 == null) CollectFingerprint(); } catch { /* 原因：采集异常按空指纹兜底，不阻塞出码 */ }
            return BuildEraChallenge(mFp8 ?? "");
        }

        /// <summary>当前挑战值（era 口径与 NewChallenge 完全一致——档内恒定，跨档自动滚动）。</summary>
        public static string CurrentChallenge()
        {
            return NewChallenge();
        }

        /// <summary>
        /// era 挑战值 = SHA256("FPR=" + 指纹 + "|ERA|" + 档号) 前 4 字节 hex。
        /// 档号 = 自 ChallengeEraEpoch（2026-01-01，UTC）起每 ChallengeEraDays（30）天一档。
        /// 【为什么确定性派生】①签发工具从设备码原文取挑战值签码，两端一致零协商；
        /// ②档内设备码恒定，授权切换（永久↔限时）无需"重开窗口重新抄码"的摩擦；
        /// ③防重放时效仍在：档滚动后 raw 串变化 → 挑战值全新 → 旧签发码 payload 对不上。
        /// </summary>
        private static string BuildEraChallenge(string fp8Hex)
        {
            long era = (long)(DateTime.UtcNow.Date - ChallengeEraEpoch).TotalDays / ChallengeEraDays;
            if (era < 0) era = 0;   // 原因：系统时钟早于纪元（罕见/回拨）地板 0，防负档号
            string raw = "FPR=" + (fp8Hex ?? "") + "|ERA|"
                + era.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return LicenseCodec.BytesToHex(new byte[] { h[0], h[1], h[2], h[3] });
            }
        }

        // ════════════════ 状态判定（唯一重算口） ════════════════

        /// <summary>
        /// 重算授权状态（启动 / 巡检定时器每小时 / 激活成功后调）。
        /// 【V4.4.362 结构拆分】本方法改为壳：先跑真实判定链 RefreshCore()（其各失败分支
        /// 都是 return 短路），再落开发构建兜底——兜底放判定链尾部是接不到短路路径的
        /// （开发机 FingerprintError 分支实锤：短路后兜底段压根不执行，Debug 版仍"未放行"）。
        /// 放外壳上，真状态优先、所有失败态都可以被"不可用才兜底"统一覆盖。
        /// 判定链顺序见 RefreshCore：指纹 → ini 记录 → 验签 → 指纹/挑战比对 → 到期日 →
        /// 回拨水位。任何一步失败都落在"更严"的状态上（宁可误锁不可漏放）。
        /// </summary>
        public static void Refresh()
        {
            RefreshCore();
#if DEBUG
            // —— 开发构建自动授权兜底（V4.4.362，仅 Debug 编译存在）：真实状态不可用
            //    （没码/码过期/指纹不符）时按"exe 编译时间 + 90 天"兜内存态 Trial，日常
            //    开发编译验证功能免输激活码。真实激活码优先；Mock 在链内已提前 return。
            //    详见 ApplyDebugTrialLicense 注释（安全面/旁路/90 天口径）。
            ApplyDebugTrialLicense();
#endif
        }

        /// <summary>真实判定链（唯一重算内核；各失败分支 return 短路；必须经 Refresh() 调用）。</summary>
        private static void RefreshCore()
        {
            LastReason = "";
            try
            {
                // 0) Mock 演示机免硬件免授权（与旧版口径一致：Mock 不参与激活门禁）
                if (LicenseBridges.IsMockLicensed) { CurrentState = LicenseState.Permanent; return; }

                // 1) 本机指纹（拿不到=无法判定，从严锁死并给原因）
                string fp8, fpErr;
                if (!TryGetFingerprint(out fp8, out fpErr))
                {
                    CurrentState = LicenseState.FingerprintError;
                    LastReason = fpErr;
                    return;
                }

                // 2) 授权记录（独立文件 LicenseState.ini；文件缺失/新建机=尚未激活，非异常）
                string storedFp = ReadLicenseKey(IniKeyFingerprint);
                string storedCode = ReadLicenseKey(IniKeyLicenseCode);
                if (string.IsNullOrEmpty(storedCode))
                {
                    CurrentState = LicenseState.NotActivated;
                    LastReason = (storedFp == null) ? "授权记录文件缺失（" + StateIniPath() + "）" : "无激活记录";
                    return;
                }
                if (storedFp != fp8)
                {
                    CurrentState = LicenseState.FingerprintError;
                    LastReason = "激活记录与本机指纹不符（目录克隆或硬件变化）";
                    return;
                }

                // 3) 解码 + 验签（每次 Refresh 都重验存储里的激活码原文——篡改文件无法延长授权，
                //    只能让记录失效回到未激活）
                byte[] payload, sig;
                if (!LicenseCodec.TryDecodeActivationCode(storedCode, out payload, out sig) ||
                    !LicenseCodec.VerifyActivation(payload, sig))
                {
                    CurrentState = LicenseState.NotActivated;
                    LastReason = "激活码无效（验签不通过）";
                    return;
                }
                byte[] fp4x, ch4x; byte tByte, dByte; uint startDays; ushort productId;
                if (!LicenseCodec.TryParsePayload(payload, out productId, out fp4x, out ch4x, out tByte, out dByte, out startDays))
                {
                    CurrentState = LicenseState.NotActivated;
                    LastReason = "激活码 payload 非法";
                    return;
                }

                // 3.5) 产品隔离（两个产品共用私钥，靠 payload 产品 ID 区分）
                if (productId != HostProductId)
                {
                    CurrentState = LicenseState.NotActivated;
                    LastReason = "该激活码属于 " + LicenseCodec.GetProductName(productId)
                        + "，不能用于 " + LicenseCodec.GetProductName(HostProductId);
                    return;
                }

                // 4) 指纹比对（验签只证明"码是厂商签的"，还要证明"签的是这台机器"）
                byte[] myFp4 = LicenseCodec.HexToBytes(fp8.Substring(0, 8));
                if (!BytesEqual(fp4x, myFp4))
                {
                    CurrentState = LicenseState.FingerprintError;
                    LastReason = "激活码签发指纹与本机不符";
                    return;
                }

                // 5) 类型分支
                if (tByte == LicenseCodec.TypePermanent)
                {
                    CurrentState = LicenseState.Permanent;
                }
                else
                {
                    if (dByte < 1 || dByte > 255)
                    {
                        CurrentState = LicenseState.NotActivated;
                        LastReason = "试用天数非法";
                        return;
                    }
                    DateTime expiry = LicenseCodec.DaysToDate(startDays).AddDays(dByte);   // 到期日=起点+天数（当天 0 点起算，即到期日全天可用）
                    if (DateTime.Now.Date > expiry.Date)
                    {
                        CurrentState = LicenseState.Expired;
                        LastReason = "试用已于 " + expiry.ToString("yyyy-MM-dd") + " 到期";
                    }
                    else
                    {
                        CurrentState = LicenseState.Trial;
                        TrialExpiry = expiry;
                    }
                }

                // 6) 防回拨水位（试用/永久都推进水位，但回拨只对试用有绕过价值——
                //    永久无到期日，回拨检测照做，留告警痕迹）。
                bool rolledBack = UpdateWaterMark();
                if (rolledBack && CurrentState == LicenseState.Trial)
                {
                    CurrentState = LicenseState.Expired;
                    LastReason = "检测到系统时钟回拨，试用授权已按过期处理";
                }
                // 链路走完：可用态（Permanent/Trial）落定；各失败分支已在上方 return 短路。
            }
            catch (Exception ex)
            {
                // 原因：判定链任何异常都从严按未激活（绝不异常放行）
                CurrentState = LicenseState.NotActivated;
                LastReason = "授权判定异常: " + ex.Message;
            }
        }

        /// <summary>生产放行判据（全工程唯一口；Mock 恒 true）。过期/未激活/指纹异常一律 false。</summary>
        public static bool IsProductionAllowed()
        {
            if (LicenseBridges.IsMockLicensed) return true;
            return CurrentState == LicenseState.Permanent || CurrentState == LicenseState.Trial;
        }

        /// <summary>状态中文文案（激活窗状态行/日志/弹窗统一用）。</summary>
        public static string StateText()
        {
            switch (CurrentState)
            {
                case LicenseState.Permanent: return "激活状态: 永久使用";
                case LicenseState.Trial: return "激活状态: 剩余使用天数 / " + Math.Max(0, (TrialExpiry.Date - DateTime.Now.Date).Days);
                case LicenseState.Expired: return "激活状态: 软件已过期";
                case LicenseState.FingerprintError: return "激活状态: 设备指纹不符，请联系厂商";
                default: return "激活状态: 未激活";
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ════════════════ 激活 ════════════════

        /// <summary>
        /// 尝试激活：验签 → 指纹比对 → 挑战值比对（防重放）→ 写 ini → Refresh。
        /// 返回 null=成功；非 null=失败原因（中文，直接展示给用户）。
        /// currentChallenge 必须传激活窗当前显示设备码里的挑战值——收旧码重放在这里拦下。
        /// </summary>
        public static string TryActivate(string codeText, string currentChallenge)
        {
            string fp8, fpErr;
            if (!TryGetFingerprint(out fp8, out fpErr))
                return "无法获取本机设备指纹：" + fpErr;

            byte[] payload, sig;
            if (!LicenseCodec.TryDecodeActivationCode(codeText, out payload, out sig))
                return "激活码格式不正确，请完整复制厂商返回的激活码";
            if (!LicenseCodec.VerifyActivation(payload, sig))
                return "激活码无效（签名验证不通过），请核对后重新输入";
            byte[] fp4x, ch4x; byte tByte, dByte; uint startDays; ushort productId;
            if (!LicenseCodec.TryParsePayload(payload, out productId, out fp4x, out ch4x, out tByte, out dByte, out startDays))
                return "激活码内容非法";

            // 产品隔离：别产品的码不能激活本产品（两个产品共用一把私钥）
            if (productId != HostProductId)
                return "该激活码属于 " + LicenseCodec.GetProductName(productId)
                    + "，不能用于 " + LicenseCodec.GetProductName(HostProductId)
                    + "，请向厂商索取本软件的激活码";

            byte[] myFp4 = LicenseCodec.HexToBytes(fp8.Substring(0, 8));
            if (!BytesEqual(fp4x, myFp4))
                return "该激活码不是签给本机的（设备指纹不符），请重新提供设备码";

            // 防重放：payload 里的挑战值必须等于当前 era 的挑战值（era=指纹+30 天档派生，
            // 见 BuildEraChallenge）。档内重开窗口/软件重启不需要重新签码——授权切换
            // （永久↔限时）直接输新码即时热生效；跨入新档后旧签发码在这里拦下，
            // 按"激活码已失效"提示重新抄设备码送厂商再签。
            if (string.IsNullOrEmpty(currentChallenge) ||
                !string.Equals(ch4x != null ? LicenseCodec.BytesToHex(ch4x) : "", currentChallenge, StringComparison.OrdinalIgnoreCase))
                return "激活码已失效（设备码已刷新），请重新生成设备码发送厂商";

            if (tByte == LicenseCodec.TypeTrial && (dByte < 1 || dByte > 255))
                return "激活码试用天数非法";

            // —— 全部通过：落盘。LicenseCode 存原文（每次 Refresh 重新验签，
            //    改文件只能让授权消失、不能延长）；Fingerprint 锁本机。
            //    写入目标是独立文件 LicenseState.ini（状态路径覆盖点决定位置）。
            LicenseStateIni ini = StateIni();
            if (ini == null) return "授权状态存储不可用（初始化失败），请联系厂商";
            ini.WriteValue(IniSection, IniKeyFingerprint, fp8);
            ini.WriteValue(IniSection, IniKeyLicenseCode, codeText.Trim());
            WriteIniVerify(ini, IniKeyFingerprint, fp8);
            WriteIniVerify(ini, IniKeyLicenseCode, codeText.Trim());

            Refresh();
            if (CurrentState != LicenseState.Permanent && CurrentState != LicenseState.Trial)
                return "激活码写入失败（存储异常），请联系厂商";   // Refresh 没认账 = 存储出问题
            return null;
        }

        /// <summary>清空授权记录（设备指纹不符时的自愈入口——重新激活即可覆盖，无需手工清 ini）。</summary>
        public static void ClearActivation()
        {
            try
            {
                LicenseStateIni ini = StateIni();
                if (ini == null) return;
                ini.WriteValue(IniSection, IniKeyFingerprint, "");
                ini.WriteValue(IniSection, IniKeyLicenseCode, "");
            }
            catch (Exception) { /* 原因：清记录失败下次 Refresh 会按指纹不符锁死，不致命 */ }
        }

        /// <summary>
        /// 授权状态安全读：文件缺失/读失败一律返回 null（=该记录缺位），而不是抛异常。
        /// 为什么要兜：LicenseStateIni.ReadValue 对"文件不存在"是抛异常的口径——首装/新机
        /// 首次启动 LicenseState.ini 还没建立，必须按"无记录"处理而不是"判定异常"。
        /// </summary>
        private static string ReadLicenseKey(string key)
        {
            try
            {
                LicenseStateIni ini = StateIni();
                if (ini == null) return null;
                return (ini.ReadValue(IniSection, key) ?? "").Trim();
            }
            catch (Exception) { return null; }   // 原因：文件缺失=记录缺位（正常态），非异常
        }

        /// <summary>授权状态文件路径（提示文案/日志用，别硬编码路径字符串）。</summary>
        private static string StateIniPath()
        {
            try
            {
                LicenseStateIni ini = StateIni();
                if (ini != null) return ini.Path;
            }
            catch (Exception) { }
            return @"MainSetting\LicenseState.ini";
        }

        // ════════════════ 防回拨水位（三存储取最大） ════════════════

        /// <summary>
        /// 推进防回拨水位。返回 true=检测到时钟回拨（调用方按过期处理并告警）。
        /// 三存储：ini [License]/WaterMark + HKCU 注册表 + SQLite DbMeta（经桥进宿主设施），
        /// 全部解析取最大；下限 = max(BuiltOn 常量, exe 文件修改时间)。写失败自检（回读
        /// 不一致记日志）——单点只读/删改都挡不住另外两处，三处全删还有 exe 时间戳下限。
        /// </summary>
        private static bool UpdateWaterMark()
        {
            try
            {
                DateTime now = DateTime.Now;
                DateTime hwm = DateTime.MinValue;

                // —— 下限：构建常量 + exe 文件时间（改系统时钟也退不到 exe 诞生之前）
                DateTime buildFloor;
                if (DateTime.TryParse(BuiltOnOverride, out buildFloor)) hwm = buildFloor;
                try
                {
                    // 结构差异②：库禁 WinForms，取入口程序集文件路径（=主 exe 全路径，与原版
                    // Application.ExecutablePath 同指一处；探针进程=探针 exe，语义不变）
                    System.Reflection.Assembly entry = System.Reflection.Assembly.GetEntryAssembly();
                    string exePath = entry == null ? null : entry.Location;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath)) hwm = Max(hwm, File.GetLastWriteTime(exePath));
                }
                catch (Exception) { /* 原因：exe 路径异常极罕见，缺下限还有三存储兜底 */ }

                // —— 三存储现值
                hwm = Max(hwm, ParseWaterMark(ReadLicenseKey(IniKeyWaterMark)));
                hwm = Max(hwm, ParseWaterMark(ReadRegWaterMark()));
                hwm = Max(hwm, ParseWaterMark(LicenseBridges.GetWaterMark(SqliteWaterMarkKey)));

                // —— 回拨判定：当前时间早于水位超过容差
                if (now < hwm.AddHours(-RollbackToleranceHours))
                {
                    try { LogHelper.Info("[License] 检测到时钟回拨：now=" + now.ToString("yyyy-MM-dd HH:mm")
                        + " 水位=" + hwm.ToString("yyyy-MM-dd HH:mm")); }
                    catch (Exception) { /* 原因：日志失败不影响判定结论 */ }
                    return true;
                }

                // —— 正常推进：水位=max(now, hwm) 写三处
                string mark = (now > hwm ? now : hwm).ToString("yyyy-MM-dd HH:mm");
                LicenseStateIni ini = StateIni();
                if (ini != null)
                {
                    ini.WriteValue(IniSection, IniKeyWaterMark, mark);
                    WriteIniVerify(ini, IniKeyWaterMark, mark);
                }
                WriteRegWaterMark(mark);
                LicenseBridges.SetWaterMark(SqliteWaterMarkKey, mark);
                return false;
            }
            catch (Exception)
            {
                // 原因：水位链路异常不阻塞主判定（上面状态已算完），返回 false 不误报回拨
                return false;
            }
        }

        private static DateTime Max(DateTime a, DateTime b) { return a > b ? a : b; }

        /// <summary>水位串 → 时间（解析失败返回 MinValue，即"没有这个存储"）。</summary>
        private static DateTime ParseWaterMark(string s)
        {
            DateTime d;
            if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
            if (DateTime.TryParse(s.Trim(), out d)) return d;
            return DateTime.MinValue;
        }

        private static string ReadRegWaterMark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegRootOverride))
                {
                    if (k == null) return null;
                    return Convert.ToString(k.GetValue(RegValueName));
                }
            }
            catch (Exception) { return null; }   // 原因：注册表读不到=该存储缺位，另两处兜底
        }

        private static void WriteRegWaterMark(string value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegRootOverride))
                {
                    if (k != null) k.SetValue(RegValueName, value);
                }
            }
            catch (Exception ex)
            {
                // 原因：注册表写失败（权限/只读）不抛——单存储失效，另两处仍兜底；留痕便于排查
                try { LogHelper.Info("[License] 注册表水位写入失败: " + ex.Message); }
                catch (Exception) { }
            }
        }

        /// <summary>ini 写后回读自检（S4 教训：写静默失败——只读 ini 在这里现形）。</summary>
        private static void WriteIniVerify(LicenseStateIni ini, string key, string expect)
        {
            try
            {
                string back = (ini.ReadValue(IniSection, key) ?? "").Trim();
                if (back != expect.Trim())
                {
                    try { LogHelper.Info("[License] ini 写入自检失败（文件可能只读）：键=" + key); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { /* 原因：自检本身失败不再放大异常，另有注册表/SQLite 兜底 */ }
        }

#if DEBUG
        /// <summary>
        /// 【开发构建自动授权——仅 Debug 编译存在（V4.4.362，默认 90 天）】
        ///
        /// 【做什么】真实激活状态不是可用态（Permanent/Trial）时，给一个内存态 Trial：
        /// 起锚 = 本机 exe 最后编译时间，到期 = 起锚 + DevTrialDays 天，填 CurrentState/
        /// TrialExpiry/LastReason。效果：开发机上编译出的 Debug 版 90 天内启动不弹激活、
        /// 可登录、可启动检测；每次重新编译 exe 时间戳变新 = 自动续期 90 天——日常开发
        /// 编译验证功能【零人工操作】。
        ///
        /// 【为什么没有安全漏洞】
        /// ① 整段包在 #if DEBUG 里：发版走 Release 配置（+混淆），编译期本方法连同
        ///    Refresh 里的调用一起被剥除，现场产物里不存在这段逻辑，逆向无路径可走——
        ///    不新增任何"运行时放行开关"；
        /// ② env 旁路（EnvKeyDebugLicenseOff，值"0"）只能"收紧"（关掉开发兜底）不能"放宽"——
        ///    伪造它最多让 Debug 版回到如实判定，无放行价值；
        /// ③ 不落盘：本逻辑不写 ini/注册表/SQLite 的任何专有数据，现场机器无法通过携带文件
        ///    或环境变量伪装成"开发机"获得授权；
        /// ④ 真实激活码永远优先：开发机上有厂商签发的真码时照常展示真实状态（验签/指纹/
        ///    挑战/水位全链照跑），本兜底只救"没码/码过期/指纹不符"的开发场景。
        ///
        /// 【90 天口径的附加价值】Debug 产物若被外带：90 天没重编就自动过期（原因里写明
        /// "重新编译即自动续 90 天"）——Debug 版本来就不允许外发，这个日期线构成又一道提醒。
        ///
        /// 【旁路使用方】宿主回归批次（探针 Main 开头设 env=0）：探针进程里入口程序集是
        /// 刚编译的探针 exe，若不关兜底，"无记录=NotActivated / 未激活不放行"等真实状态机
        /// 断言会被兜底盖成 Trial 而假红。键名编译期取本类常量，两侧漂移立刻暴露。
        /// </summary>
        private static void ApplyDebugTrialLicense()
        {
            try
            {
                // 0) 真实授权优先：真码刚判出可用态时不盖（调试真码体验保持原样）
                if (CurrentState == LicenseState.Permanent || CurrentState == LicenseState.Trial) return;

                // 1) 探针旁路（方向=收紧）：env="0" 时如实保留真实状态，供回归批考验状态机
                if (string.Equals(Environment.GetEnvironmentVariable(EnvKeyDebugLicenseOff), "0",
                    StringComparison.Ordinal)) return;

                // 2) 起锚 = exe 最后编译时间（取不到退 BuiltOn 构建常量，从严再兜一层）
                DateTime anchor = DateTime.MinValue;
                try
                {
                    // 结构差异②：库禁 WinForms，取入口程序集（=主 exe 文件，语义与原版同）
                    System.Reflection.Assembly entry = System.Reflection.Assembly.GetEntryAssembly();
                    string exePath = entry == null ? null : entry.Location;
                    if (!string.IsNullOrEmpty(exePath)) anchor = File.GetLastWriteTime(exePath);
                }
                catch (Exception) { /* 原因：exe 路径异常极罕见，缺锚用构建常量 */ }
                DateTime buildFloor;
                if (DateTime.TryParse(BuiltOnOverride, out buildFloor) && anchor < buildFloor) anchor = buildFloor;

                // 3) 到期 = 锚 + N 天；到期日当天 24 点前可用（与真 Trial 口径一致）
                DateTime expiry = anchor.Date.AddDays(DevTrialDays);
                if (DateTime.Now.Date > expiry.Date)
                {
                    // 有意行为：Debug 产物 90 天没重编也判过期——"该重编了"的提示 +
                    // 外流产物日期防线；原因给到"重编即续"，开发一眼自救。
                    CurrentState = LicenseState.Expired;
                    LastReason = "开发构建自动授权已于 " + expiry.ToString("yyyy-MM-dd")
                        + " 到期（重新编译即自动续 90 天）";
                    return;
                }

                // 4) 兜底生效：内存态 Trial（不落盘——每次 Refresh 重算，进程重启亦然）
                CurrentState = LicenseState.Trial;
                TrialExpiry = expiry;
                LastReason = "开发构建自动授权（Debug 版 " + DevTrialDays + " 天，重编即续）";

                // 一次性留痕：日志里能一眼辨认"这个 Trial 是开发兜底而非真码"（排障关键）
                if (!mDevTrialLogged)
                {
                    mDevTrialLogged = true;
                    try
                    {
                        LogHelper.Info("[License] 开发构建自动授权生效（Debug）：到期 "
                            + expiry.ToString("yyyy-MM-dd") + "（仅 Debug 版存在；真实激活码优先）");
                    }
                    catch (Exception) { /* 原因：日志失败不影响放行结论 */ }
                }
            }
            catch (Exception ex)
            {
                // 原因：兜底链自身异常绝不放行——保留真实状态（必为非可用态）与原 LastReason
                try { LogHelper.Info("[License] 开发兜底异常: " + ex.Message); }
                catch (Exception) { }
            }
        }

        /// <summary>开发兜底"首次生效已留痕"标记（进程内一次性；Refresh 每小时跑，不刷屏）。</summary>
        private static bool mDevTrialLogged;
#endif
    }
}
