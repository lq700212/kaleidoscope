using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Infer
{
    /// <summary>决策问题题型（TypeSafe JEV "System One" 三原语）。</summary>
    public enum DecisionQuestionType
    {
        /// <summary>单选（≤255 选项）：criteria = 选项字典 {键: 描述}。</summary>
        Choice,
        /// <summary>有序评分（≤10 档）：criteria = 档位描述数组（按序）。</summary>
        Score,
        /// <summary>是非概率：criteria 不用，instructions 即命题。</summary>
        Noul,
    }

    /// <summary>一道决策问题（JEV Decisions API 的 questions[] 单元）。</summary>
    public class DecisionQuestion
    {
        public string Key { get; set; }
        public DecisionQuestionType Type { get; set; }
        /// <summary>问题指令（问什么）。</summary>
        public string Instructions { get; set; } = "";
        /// <summary>Choice=选项描述（键→描述）；Score=档位描述（有序）。Noul 忽略。</summary>
        public System.Collections.Generic.Dictionary<string, string> Criteria { get; set; }

        public static DecisionQuestion Choice(string key, string instructions, params string[] options)
        {
            var map = new System.Collections.Generic.Dictionary<string, string>();
            for (int i = 0; i < options.Length; i++) map["opt" + i] = options[i];
            return new DecisionQuestion { Key = key, Type = DecisionQuestionType.Choice, Instructions = instructions, Criteria = map };
        }

        public static DecisionQuestion YesNo(string key, string statement)
        {
            return new DecisionQuestion { Key = key, Type = DecisionQuestionType.Noul, Instructions = statement };
        }
    }

    /// <summary>决策回包（单问题；带校准置信度——调用方按阈值决定采纳或人工复核）。</summary>
    public class DecisionReply
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        /// <summary>Choice：选中键（opt0/opt1…）；Noul：null。</summary>
        public string Choice { get; set; }
        /// <summary>Noul：命题成立概率 0~1；Score：档位值；Choice：置信度。</summary>
        public double Value { get; set; }
        public long LatencyMs { get; set; }
    }

    /// <summary>
    /// JEV Decisions 决策传输器（V0.1.7 调研结论：云端文本决策模型——无图像输入、闭源无本地
    /// 权重，与 AIris 离线红线错位，**不进生产主链路**；本 transport 供可选语义决策通道用，
    /// 如审计 NG 原因归纳增强，默认关闭）。兼容两类端点：
    ///  TypeSafe 原生 POST {base}/v1/systemone；OpenRouter POST {base}/api/alpha/decisions
    ///  （BaseUrl 直接填含路径的完整端点则原样使用——判据：Url 已以 /v1/ 或 /api/ 开头的尾段）。
    /// 请求 {model, state, questions:{key:{type,instructions,criteria}}} → 回包 {answers:{…}}。
    /// </summary>
    public class DecisionsTransport : InferTransportBase
    {
        public override Task<InferReply> SendAsync(InferEndpoint ep, InferRequest req, CancellationToken ct)
        {
            throw new NotSupportedException("Decisions 协议无图像推理——请走 DecideAsync（语义决策通道）");
        }

        /// <summary>发一道决策问题（单问题封装；多问题批量需求出现时再扩 questions 字典）。</summary>
        public async Task<DecisionReply> DecideAsync(InferEndpoint ep, string state, DecisionQuestion q, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new DecisionReply();

            string url = BuildUrl(ep);
            var sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(J(string.IsNullOrEmpty(ep.Model) ? "jev-latest" : ep.Model)).Append('"');
            sb.Append(",\"state\":\"").Append(J(state)).Append('"');
            sb.Append(",\"questions\":{\"").Append(J(q.Key)).Append("\":{");
            sb.Append("\"type\":\"").Append(q.Type == DecisionQuestionType.Choice ? "choice" : q.Type == DecisionQuestionType.Score ? "score" : "noul").Append('"');
            if (!string.IsNullOrEmpty(q.Instructions)) sb.Append(",\"instructions\":\"").Append(J(q.Instructions)).Append('"');
            if (q.Criteria != null && q.Criteria.Count > 0)
            {
                sb.Append(",\"criteria\":{");
                bool first = true;
                foreach (var kv in q.Criteria)
                {
                    if (!first) sb.Append(',');
                    sb.Append('"').Append(J(kv.Key)).Append("\":\"").Append(J(kv.Value)).Append('"');
                    first = false;
                }
                sb.Append('}');
            }
            sb.Append("}}}");

            var reply = await PostJsonAsync(ep, url, sb.ToString(), msg =>
            {
                if (!string.IsNullOrEmpty(ep.ApiKey)) msg.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ep.ApiKey);
            }, ct).ConfigureAwait(false);
            r.LatencyMs = reply.LatencyMs;
            if (!reply.Ok)
            {
                r.Error = reply.Error;
                return r;
            }

            // 解 answers[q.key]：choice → {"choice":"opt1","confidence":0.9}；noul → {"noul":0.87}
            string json = reply.Text ?? "";
            int a = json.IndexOf("\"answers\"", StringComparison.Ordinal);
            string scope = a >= 0 ? json.Substring(a) : json;
            if (q.Type == DecisionQuestionType.Noul)
            {
                r.Value = FindNum(scope, "noul");
                r.Ok = true;
            }
            else
            {
                r.Choice = Json.FindString(scope, "choice");
                double conf = FindNum(scope, "confidence");
                r.Value = double.IsNaN(conf) ? 0 : conf;
                r.Ok = !string.IsNullOrEmpty(r.Choice);
                if (!r.Ok) r.Error = "回包无 choice 字段";
            }
            return r;
        }

        /// <summary>端点拼装：BaseUrl 已含决策路径尾段则原样用，否则拼 OpenRouter 形态 /api/alpha/decisions。</summary>
        private static string BuildUrl(InferEndpoint ep)
        {
            string root = ep.BaseUrl.TrimEnd('/');
            if (root.EndsWith("/v1/systemone", StringComparison.Ordinal)
                || root.EndsWith("/api/alpha/decisions", StringComparison.Ordinal)) return root;
            return root + "/api/alpha/decisions";
        }

        /// <summary>对象片段找数字字段（找不到 NaN）。</summary>
        private static double FindNum(string json, string key)
        {
            string pat = "\"" + key + "\"";
            int k = json.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return double.NaN;
            int i = json.IndexOf(':', k + pat.Length);
            if (i < 0) return double.NaN;
            int s = ++i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-' || json[i] == '.')) i++;
            double v;
            return double.TryParse(json.Substring(s, i - s), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }
    }
}
