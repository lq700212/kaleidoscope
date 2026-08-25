using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Kaleidoscope.Utils
{
    /// <summary>
    /// HslCommunication 商业授权统一管理口（本库唯一注册点）。
    ///
    /// 【干什么】HslCommunication 是商业 PLC 协议库（三菱/欧姆龙/西门子/汇川/
    /// EtherNet/IP 等，商业授权、授权码需自行购买），未授权运行版有调用次数限制。
    /// 本类负责：① 从本地授权文件（exe 同目录 <see cref="FileName"/>）读取授权码并激活；
    /// ② 供界面"输入授权码"场景实时校验与落盘。禁止在别处硬编码授权码或直接调
    /// HSL 的 SetAuthorizationCode。
    ///
    /// 【存储格式（混淆加密，防顺手拷走直接使用）】
    /// 授权码落盘时用 AES 加密（密钥由本类内置盐派生、IV 每次随机），文件内容为
    /// Base64 密文 + 中性注释——打开文件看不到明文授权码，拷贝到别处也无法直接识别/使用。
    /// 【安全边界（诚实声明）】本库开源，加密逻辑与盐公开可见：本防护针对"未研究源码的
    /// 现场人员顺手拷走"，无法防御蓄意逆向——这是"开源库 + 多机部署"约束下的技术上限；
    /// 更强的保护需厂商侧硬件锁或代码混淆加壳，超出本库范围。
    ///
    /// 【文件格式】UTF-8 文本：# 开头为注释行、空行跳过；数据行仅一种：
    ///   "ENC1:Base64"——AES 加密格式（IV 拼在密文前），非此格式一律忽略。
    ///
    /// 【激活语义】SetAuthorizationCode 为本地验签（离线可用），进程内可重复调用换码。
    /// 本类以"最近一次成功激活的码"为准；失败不影响已有状态。
    /// 未激活时不阻断通讯（HSL 受限流仍可运行），由界面/日志引导补授权。
    /// </summary>
    public static class HslAuthorization
    {
        /// <summary>授权码文件名（位于 exe 同目录）。</summary>
        public const string FileName = "hsl.dat";

        /// <summary>加密格式版本前缀（数据行以此开头标识密文）。</summary>
        private const string EncPrefix = "ENC1:";

        /// <summary>密钥派生盐（开源可见；本防护目标是混淆防顺手拷走，非对抗逆向）。</summary>
        private const string KeySalt = "Kaleidoscope.HslAuth.Storage.v1";

        private static readonly object _lock = new object();

        /// <summary>当前是否已成功激活商业授权。</summary>
        private static volatile bool _activated;

        /// <summary>上次"未激活"告警日志时间（30s 节流，防心跳反复调用刷屏）。</summary>
        private static DateTime _lastWarnAt = DateTime.MinValue;

        /// <summary>当前已激活的授权码；未激活为 null。</summary>
        public static string ActiveCode { get; private set; }

        /// <summary>是否已成功激活商业授权（true=有效，false=未激活/无效，受限流运行）。</summary>
        public static bool IsActivated => _activated;

        /// <summary>授权文件的完整路径（exe 同目录）。界面提示与落盘都用它。</summary>
        public static string AuthFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

        /// <summary>
        /// 确保已注册（服务连接前调用，幂等快路径：已激活直接返回）。
        /// 未激活时**每次调用都重新读授权文件并尝试激活**——本地小文件 IO 微秒级、连接频率
        /// 又受重连节流约束，开销可忽略；换来的是"现场运行中把 hsl.dat 放进/修正到程序目录，
        /// 无需重启，下一个心跳周期自动恢复授权"。未激活不抛异常、不阻断通讯（受限流），
        /// 仅按 30s 节流记 Warn 日志，由界面/日志引导补授权。
        /// </summary>
        public static bool EnsureRegistered()
        {
            if (_activated) return true;
            lock (_lock)
            {
                if (_activated) return true;

                string code = LoadFromFile();
                if (string.IsNullOrWhiteSpace(code))
                {
                    WarnThrottled($"HSL 未授权：未找到有效授权文件 {AuthFilePath}，将按未授权版受限流运行" +
                                  "（请在配置编辑器中输入授权码，或手工放置 hsl.dat 文件——放入后无需重启自动生效）");
                    return false;
                }
                if (TryActivate(code)) return true;   // 文件已是加密格式且内容未变，激活后无需重写
                WarnThrottled("HSL 授权失败：授权文件中的授权码无效（或文件格式非法），将按未授权版受限流运行" +
                              "（请核对 hsl.dat 内容，或在配置编辑器中重新输入）");
                return false;
            }
        }

        /// <summary>告警日志 30s 节流（未激活状态下心跳每次都会走到这里，防刷屏）。</summary>
        private static void WarnThrottled(string message)
        {
            if ((DateTime.Now - _lastWarnAt).TotalSeconds < 30) return;
            _lastWarnAt = DateTime.Now;
            LogHelper.Warn(message);
        }

        /// <summary>
        /// 尝试用指定授权码激活（**不落盘**）。供界面输入框实时校验用：返回 true 表示
        /// 授权码有效且已在本进程生效（调用方随后应调 <see cref="SaveToFile"/> 落盘保存）；
        /// 返回 false 表示授权码无效（不影响之前已激活的状态）。
        /// </summary>
        public static bool TryActivate(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            code = code.Trim();
            try
            {
                // 【加锁】SetAuthorizationCode 改的是 HSL 内部静态授权状态，弹窗线程与服务
                // 心跳线程可能并发调用，必须串行化（本锁与 EnsureRegistered 同一把，同线程可重入）
                lock (_lock)
                {
                    // HSL 本地验签：离线可用；返回 false 即授权码非法/过期
                    bool ok = HslCommunication.Authorization.SetAuthorizationCode(code);
                    if (!ok)
                    {
                        LogHelper.Warn("HSL 授权码校验未通过（SetAuthorizationCode 返回 false）");
                        return false;
                    }
                    bool firstTime = !_activated;
                    _activated = true;
                    ActiveCode = code;
                    // 只在"从未激活→激活"边沿记 Info，避免界面反复试码刷屏
                    if (firstTime) LogHelper.Info("HslCommunication 商业授权激活成功");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogHelper.Warn("HSL 授权码校验异常：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把授权码加密后写入本地授权文件（exe 同目录 hsl.dat，AES 混淆格式；
        /// 目录不存在自动创建）。写入失败抛 IO 异常由调用方提示。
        /// 【为什么加密】多机部署场景授权文件会随程序落到客户现场——明文文件被拷走即可直接
        /// 使用；加密后文件内容不可读，拷走无法直接识别/使用（安全边界见类头声明）。
        /// </summary>
        public static void SaveToFile(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("授权码为空", nameof(code));
            string path = AuthFilePath;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 应用配置数据（请勿修改；本文件不入 git）");
            sb.AppendLine("# 格式：# 开头为注释行；数据行为程序自动生成的加密内容，请通过程序界面输入");
            sb.AppendLine(EncryptLine(code.Trim()));
            lock (_lock)
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            LogHelper.Info("HSL 授权码已保存到 " + path);
        }

        /// <summary>
        /// 从授权文件读取授权码（仅认 ENC1 加密格式数据行，其它数据行忽略）。
        /// 文件不存在 / 全是注释空行 / 解密失败 / 读异常时返回 null。
        /// 【他机文件】加密内容与本机无关（密钥不绑机器），他机拷来的文件同样能解——这是
        /// 多机部署的设计选择；混淆只防"看一眼"，不防对照源码解密（见类头安全边界）。
        /// </summary>
        public static string LoadFromFile()
        {
            string path = AuthFilePath;
            try
            {
                if (!File.Exists(path)) return null;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw?.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                    if (!line.StartsWith(EncPrefix, StringComparison.Ordinal))
                        continue;   // 仅认加密格式数据行（明文/未知格式一律忽略）
                    return DecryptLine(line.Substring(EncPrefix.Length));
                }
                return null;
            }
            catch (Exception ex)
            {
                LogHelper.Warn("读取 HSL 授权文件失败：" + ex.Message);
                return null;
            }
        }

        // ══════════ 混淆加密内部实现 ══════════

        /// <summary>AES 密钥（由内置盐 SHA256 派生，32 字节）。</summary>
        private static byte[] DeriveKey()
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes(KeySalt));
        }

        /// <summary>加密一行：AES-CBC + 随机 IV（拼在密文前），输出 "ENC1:Base64(IV+cipher)"。</summary>
        private static string EncryptLine(string plain)
        {
            byte[] key = DeriveKey();
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV();
                using (var enc = aes.CreateEncryptor())
                {
                    byte[] plainBytes = Encoding.UTF8.GetBytes(plain);
                    byte[] cipher = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    byte[] ivAndCipher = new byte[aes.IV.Length + cipher.Length];
                    Buffer.BlockCopy(aes.IV, 0, ivAndCipher, 0, aes.IV.Length);
                    Buffer.BlockCopy(cipher, 0, ivAndCipher, aes.IV.Length, cipher.Length);
                    return EncPrefix + Convert.ToBase64String(ivAndCipher);
                }
            }
        }

        /// <summary>解密一行："ENC1:Base64(IV+cipher)" → 明文；格式/密文非法返回 null。</summary>
        private static string DecryptLine(string data)
        {
            try
            {
                byte[] ivAndCipher = Convert.FromBase64String(data);
                byte[] key = DeriveKey();
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    byte[] iv = new byte[16];
                    Buffer.BlockCopy(ivAndCipher, 0, iv, 0, 16);
                    aes.IV = iv;
                    using (var dec = aes.CreateDecryptor())
                    {
                        byte[] plain = dec.TransformFinalBlock(ivAndCipher, 16, ivAndCipher.Length - 16);
                        return Encoding.UTF8.GetString(plain);
                    }
                }
            }
            catch
            {
                return null;   // 密文非法（篡改/截断/非本库文件）
            }
        }
    }
}
