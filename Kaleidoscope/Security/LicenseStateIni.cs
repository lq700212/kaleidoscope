using System;
using System.IO;
using System.Text;

namespace Kaleidoscope.Security
{
    /// <summary>
    /// 授权状态 ini 专用读写器（独立文件 MainSetting\LicenseState.ini；一台机器一个文件一个实例）。
    ///
    /// 【来历与语义基准】对位 WPF 侧 HuaJiVision.Core.Config.IniFile（原版 SubClass/IniFile.cs
    /// V4.4.286 统一 UTF-8 版）的**授权三键**消费口径，一比一对齐以下六条语义（改这里前先读
    /// 原版 IniFile 头注释——那是全工程 ini 读写事故的定论区）：
    /// ① 读：文件不存在**抛异常**（ApplicationException）——调用方（LicenseGate.ReadLicenseKey）
    ///    catch 转 null = "记录缺位"，首装/新机按"未激活"处理而不是"判定异常"；
    /// ② 写：节不存在追加、文件与目录不存在自动新建（新文件天生 UTF-8 BOM）；
    /// ③ 编码： 读 UTF-8（带/无 BOM 都认）；写 UTF-8(BOM) + CRLF——杜绝 Win32 API 的
    ///    系统代码页乱码族事故（V4.4.58/68/285 三次同根）；
    /// ④ 节/键大小写不敏感、取首个命中键、值去首尾空白（Win32 宽松口径，移植码/手改文件都读得对）；
    /// ⑤ 只动目标键那一行、其余行一字节不动（最小 diff；手工排查时历史行不漂移）；
    /// ⑥ 写静默失败（磁盘满/只读），不抛——LicenseStateIni 调用方另有"写后回读自检"
    ///    （LicenseGate.WriteIniVerify）现形打日志，与原版 IniWriteValue 口径一致。
    ///
    /// 【为什么库内自管而不是继续委托注入】原 WPF 版经 StateIniProvider 注入 Core IniFile，
    /// 那是把"Core 全量 ini 设施"当依赖；授权 ini 只有 [License] 一个段三个键，专用轻量实现
    /// 让通讯库零外部 ini 依赖，也方便其它宿主（新产品开窗用授权）直接复用。各宿主需要改
    /// 文件位置时用 LicenseGate.StateIniPathOverride 覆盖，不必碰本类。
    ///
    /// 【线程】LicenseGate 全部调用点在 UI 线程（启动/巡检定时器/激活按钮），不加锁——
    /// 若宿主在多线程调 LicenseGate，约束自行保证（与原版 mIniOp 单例口径一致）。
    /// </summary>
    public sealed class LicenseStateIni
    {
        private readonly string _path;

        /// <summary>构造（只存路径，不碰 IO——真正的文件读写在 Read/Write 时发生，
        /// 因此"路径合法但文件暂缺"不构成构造失败）。</summary>
        public LicenseStateIni(string path)
        {
            _path = path;
        }

        /// <summary>ini 文件全路径（提示文案/日志里展示用，别硬编码路径字符串）。</summary>
        public string Path { get { return _path; } }

        /// <summary>
        /// 读单键（Win32 宽松口径：节/键大小写不敏感、取首个命中键、值去首尾空白）。
        /// 文件不存在抛 ApplicationException——**语义对齐 Core IniFile 原口径**：
        /// LicenseGate.ReadLicenseKey 靠这个异常转 null 实现"首装=记录缺位=未激活"。
        /// </summary>
        public string ReadValue(string section, string key)
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
                throw new ApplicationException("文件不存在：" + _path);
            // UTF-8 逐行读；本文件由本类自写（永远 UTF-8+ASCII 内容），无 GBK 混排历史包袱
            string[] lines = File.ReadAllLines(_path, new UTF8Encoding(false, true));
            string secWant = "[" + (section ?? "").Trim() + "]";
            string keyWant = (key ?? "").Trim();
            bool inSection = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;   // 空行/注释行跳过
                if (line.StartsWith("["))
                {
                    // 段头：命中目标段=进入；碰到下一个段=目标段结束
                    if (string.Equals(line, secWant, StringComparison.OrdinalIgnoreCase)) inSection = true;
                    else if (inSection) break;
                    continue;
                }
                if (!inSection) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                if (string.Equals(k, keyWant, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(eq + 1).Trim();   // 首个命中键即返回（Win32 口径）
            }
            return "";   // 节内无该键：返回空串（Win32 语义），与"文件缺失=抛异常"区分开
        }

        /// <summary>
        /// 写单键（只动目标键那一行；节不存在追加、文件/目录不存在自动新建 UTF-8(BOM)）。
        /// 失败静默不抛（磁盘满/只读）——LicenseGate.WriteIniVerify 会回读自检现形打日志。
        /// </summary>
        public void WriteValue(string section, string key, string value)
        {
            try
            {
                if (string.IsNullOrEmpty(_path)) return;
                string dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);   // 目录不存在自动建

                string[] lines = File.Exists(_path)
                    ? File.ReadAllLines(_path, new UTF8Encoding(false, true))
                    : new string[0];
                string secWant = "[" + (section ?? "").Trim() + "]";
                string keyWant = (key ?? "").Trim();
                string newLine = keyWant + "=" + (value ?? "");

                int sectionStart = -1, sectionEnd = lines.Length;   // 目标段的起止（含头/不含下个段头）
                int keyLine = -1;                                   // 目标键所在行（段内）
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("["))
                    {
                        if (sectionStart < 0 && string.Equals(t, secWant, StringComparison.OrdinalIgnoreCase))
                        {
                            sectionStart = i;        // 目标段头定位
                            continue;
                        }
                        if (sectionStart >= 0)
                        {
                            sectionEnd = i;          // 下一个段头=目标段结束（键搜索已在此前完成）
                            break;
                        }
                    }
                    else if (sectionStart >= 0)
                    {
                        int eq = t.IndexOf('=');
                        if (eq <= 0) continue;
                        if (string.Equals(t.Substring(0, eq).Trim(), keyWant, StringComparison.OrdinalIgnoreCase))
                        {
                            keyLine = i;             // 首个命中键所在行（后写不覆盖前写，与读口径一致）
                            break;
                        }
                    }
                }

                // 组装回写：结构三态——改行 / 段内插行 / 追加段
                System.Collections.Generic.List<string> output = new System.Collections.Generic.List<string>(lines.Length + 2);
                if (keyLine >= 0)
                {
                    // ① 段内已有键：原位整行替换（其余行一字节不动的口径）
                    for (int i = 0; i < lines.Length; i++) output.Add(i == keyLine ? newLine : lines[i]);
                }
                else if (sectionStart >= 0)
                {
                    // ② 段存在键缺失：在段尾（下个段头之前/文件尾之前）插入新键行
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i == sectionEnd) output.Add(newLine);
                        output.Add(lines[i]);
                    }
                    if (sectionEnd >= lines.Length) output.Add(newLine);   // 目标段就是最后一段：插文件尾
                }
                else
                {
                    // ③ 段不存在：文件尾追加段头 + 键行（ guarantee 与其它段隔一行可读性更佳，保守按紧凑格式——原版 IniWriteValue 同为紧凑追加）
                    output.AddRange(lines);
                    output.Add(secWant);
                    output.Add(newLine);
                }

                // UTF-8(BOM) + CRLF 落盘（文件级覆盖写；授权文件内容只来自本链路，无并发写）
                using (StreamWriter sw = new StreamWriter(_path, false, new UTF8Encoding(true)))
                {
                    for (int i = 0; i < output.Count; i++) sw.Write(output[i] + "\r\n");
                }
            }
            catch (Exception)
            {
                // 原因：写失败静默（对齐原版 IniWriteValue 口径）——WriteIniVerify 回读自检现形
            }
        }
    }
}
