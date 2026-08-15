using System;
using System.Collections.Generic;
using System.Globalization;

namespace CommonLib.Models
{
    /// <summary>
    /// IO 输出"备用通道映射"（一条：源物理通道 → 目标物理通道）。
    /// 【来源】AgingTestSystem.Models.IoOutputChannelRemap 原样移植。
    ///
    /// 【背景】现场某个 DQ 输出通道烧毁 / 电压不足（如继电器问题导致输出只有 16V 低于 24V）后，
    /// 无法再使用该通道。此时可把该通道的信号改写到同一耦合器上未使用的"备用通道"。
    /// 业务侧（输出点编号、UI 显示、报警联动）完全不变，只有"写 DO / 读 DO"的物理寄存器与 bit 被重定向。
    ///
    /// 【配置格式】字符串，多组用分号(;)分隔：
    ///   源寄存器@源通道->目标寄存器@目标通道
    ///   - 寄存器：十六进制（带 0x 前缀），如 0x2000
    ///   - 通道：0x 十六进制（0x00=第 1 路 bit0；0x1F=第 32 路 bit31），内部解析成 0~31 位号
    ///   示例（0x2000 的 0x00 通道烧毁 → 备用到 0x2009 的 0x10 通道）：
    ///     0x2000@0x00->0x2009@0x10;0x2008@0x00->0x2009@0x11
    /// </summary>
    public class IoOutputChannelRemap
    {
        /// <summary>源寄存器地址（绝对地址，如 0x2000）。</summary>
        public ushort SourceRegister { get; set; }

        /// <summary>源通道号（0~31，0 = 第 1 路）。</summary>
        public int SourceChannel { get; set; }

        /// <summary>目标寄存器地址（绝对地址，如 0x2009）。</summary>
        public ushort TargetRegister { get; set; }

        /// <summary>目标通道号（0~31，0 = 第 1 路）。</summary>
        public int TargetChannel { get; set; }

        /// <summary>
        /// 解析配置字符串为映射列表。逐项解析，格式非法的项跳过并汇总到 error（不影响其它合法项）。
        /// </summary>
        /// <param name="raw">配置字符串，如 "0x2000@0x00-&gt;0x2009@0x10;0x2008@0x00-&gt;0x2009@0x11"</param>
        /// <param name="error">非空时说明哪些项被跳过及原因</param>
        /// <returns>解析出的合法映射列表（可能为空）</returns>
        public static List<IoOutputChannelRemap> ParseAll(string raw, out string error)
        {
            var list = new List<IoOutputChannelRemap>();
            var errs = new List<string>();

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = null;
                return list;
            }

            // 同时兼容英文分号(;)与中文分号(；)
            string[] items = raw.Split(new[] { ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string item in items)
            {
                string token = item.Trim();
                if (token.Length == 0) continue;

                // 拆成左右两半：源 -> 目标
                string[] sides = token.Split(new[] { "->", "→" }, StringSplitOptions.None);
                if (sides.Length != 2)
                {
                    errs.Add($"'{token}' 缺少 '->' 分隔符");
                    continue;
                }

                if (!TryParseEndpoint(sides[0], out ushort srcReg, out int srcCh, out string errLeft))
                {
                    errs.Add($"'{token}' 源端格式错误：{errLeft}");
                    continue;
                }
                if (!TryParseEndpoint(sides[1], out ushort dstReg, out int dstCh, out string errRight))
                {
                    errs.Add($"'{token}' 目标端格式错误：{errRight}");
                    continue;
                }

                // 源与目标相同没有意义（没换位置），跳过
                if (srcReg == dstReg && srcCh == dstCh)
                {
                    errs.Add($"'{token}' 源与目标相同，已忽略");
                    continue;
                }

                list.Add(new IoOutputChannelRemap
                {
                    SourceRegister = srcReg,
                    SourceChannel = srcCh,
                    TargetRegister = dstReg,
                    TargetChannel = dstCh
                });
            }

            error = errs.Count > 0 ? string.Join("；", errs) : null;
            return list;
        }

        /// <summary>
        /// 解析 "寄存器@通道" 形式的一端（如 "0x2000@0x0A"）。
        /// 寄存器与通道均为十六进制（带 0x 前缀），内部统一换算成 0~31 的十进制位号（0=第 1 路）供位运算。
        /// </summary>
        /// <param name="s">原始字符串</param>
        /// <param name="reg">解析出的寄存器地址</param>
        /// <param name="channel">解析出的通道号（0~31）</param>
        /// <param name="error">解析失败时的原因说明</param>
        /// <returns>成功返回 true</returns>
        private static bool TryParseEndpoint(string s, out ushort reg, out int channel, out string error)
        {
            reg = 0;
            channel = 0;
            error = null;

            string[] parts = s.Trim().Split('@');
            if (parts.Length != 2)
            {
                error = "应为 寄存器@通道（如 0x2000@0x0A）";
                return false;
            }

            string regStr = parts[0].Trim();
            // 兼容带 / 不带 0x 前缀
            if (regStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                regStr.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
            {
                regStr = regStr.Substring(2);
            }

            if (!ushort.TryParse(regStr, NumberStyles.HexNumber, null, out reg))
            {
                error = $"寄存器 '{parts[0]}' 不是合法十六进制地址";
                return false;
            }

            string chStr = parts[1].Trim();
            // 通道号：统一十六进制（带 0x 前缀，与寄存器一致），如 @0x0A（= 第 11 路）
            if (!chStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                !chStr.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
            {
                error = $"通道 '{parts[1]}' 应为 0x00~0x1F 的十六进制（带 0x 前缀，0x00 = 第 1 路）";
                return false;
            }

            if (!int.TryParse(chStr.Substring(2), NumberStyles.HexNumber, null, out channel) ||
                channel < 0 || channel > 31)
            {
                error = $"通道 '{parts[1]}' 应为 0x00~0x1F（0x00 = 第 1 路）";
                return false;
            }
            return true;
        }
    }
}
