using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kaleidoscope.Infer
{
    /// <summary>
    /// 轻 JSON 工具（手拼请求体的解包侧）：不引第三方、不做完整 DOM——只提供
    /// "按键名找字符串值 / 找数字数组"两个高频操作，覆盖 chat 回包与 bbox JSON 解析。
    /// 定位：推理协议回包结构稳定且字段名唯一，扫描足够；复杂嵌套解析需求出现时再升级。
    /// </summary>
    public static class Json
    {
        /// <summary>在 JSON 文本中找 "key":"value" 的字符串值（第一个匹配）。
        /// 处理转义（\" \\ \n 等）；找不到返回 null。</summary>
        public static string FindString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            // 定位 "key"（带引号原文，避免子串误配如 "content" 撞 "content_type"）
            string pat = "\"" + key + "\"";
            int k = json.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return null;
            int i = k + pat.Length;
            // 跳空白到 ':'
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != ':') return null;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;    // 值不是字符串
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 5 < json.Length &&
                                int.TryParse(json.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                                sb.Append((char)cp);
                            i += 4;
                            break;
                        default: sb.Append(n); break;
                    }
                    i += 2;
                    continue;
                }
                if (c == '"') return sb.ToString();     // 收尾引号
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>找 "key" 后的第一个数字数组，如 "bbox_2d":[120,85,320,240] → [120,85,320,240]。
        /// 找不到返回 null。</summary>
        public static int[] FindIntArray(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            string pat = "\"" + key + "\"";
            int k = json.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return null;
            int i = json.IndexOf('[', k + pat.Length);
            if (i < 0) return null;
            int end = json.IndexOf(']', i);
            if (end < 0) return null;

            var list = new List<int>();
            int segStart = -1;
            for (int p = i + 1; p < end; p++)
            {
                char c = json[p];
                if (char.IsDigit(c) || c == '-')
                {
                    if (segStart < 0) segStart = p;
                }
                else if (segStart >= 0)
                {
                    int v;
                    if (int.TryParse(json.Substring(segStart, p - segStart), out v)) list.Add(v);
                    segStart = -1;
                }
            }
            if (segStart >= 0)
            {
                int v;
                if (int.TryParse(json.Substring(segStart, end - segStart), out v)) list.Add(v);
            }
            return list.Count >= 2 ? list.ToArray() : null;
        }
    }
}
