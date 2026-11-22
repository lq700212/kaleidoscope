using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Infer
{
    /// <summary>
    /// Anthropic /v1/messages 传输器。信封差异：图像走 content 块
    /// {type:"image", source:{type:"base64", media_type:"image/jpeg", data}}（裸 base64，
    /// 非 data URL）；system 是顶层字段不是消息；鉴权 x-api-key + anthropic-version
    /// 两个头都必须有。回包取 content[].text 拼接（多块时按序拼接）。
    /// </summary>
    public class AnthropicTransport : InferTransportBase
    {
        /// <summary>API 版本头（官方口径 2023-06-01 至今有效）。</summary>
        private const string ApiVersion = "2023-06-01";

        public override async Task<InferReply> SendAsync(InferEndpoint ep, InferRequest req, CancellationToken ct)
        {
            string url = ep.BaseUrl.TrimEnd('/') + "/v1/messages";

            var sb = new StringBuilder(256 + (req.ImageJpeg != null ? req.ImageJpeg.Length * 4 / 3 : 0));
            sb.Append("{\"model\":\"").Append(J(string.IsNullOrEmpty(req.Model) ? ep.Model : req.Model)).Append('"');
            sb.Append(",\"max_tokens\":").Append(req.MaxTokens.GetValueOrDefault(1024));    // anthropic 必填
            if (!string.IsNullOrEmpty(req.SystemPrompt))
                sb.Append(",\"system\":\"").Append(J(req.SystemPrompt)).Append('"');
            sb.Append(",\"messages\":[{\"role\":\"user\",\"content\":[");
            if (req.ImageJpeg != null && req.ImageJpeg.Length > 0)
            {
                sb.Append("{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/jpeg\",\"data\":\"")
                  .Append(Convert.ToBase64String(req.ImageJpeg))
                  .Append("\"}},");
            }
            sb.Append("{\"type\":\"text\",\"text\":\"").Append(J(req.UserPrompt)).Append("\"}");
            sb.Append("]}]}");

            var reply = await PostJsonAsync(ep, url, sb.ToString(), msg =>
            {
                if (!string.IsNullOrEmpty(ep.ApiKey)) msg.Headers.Add("x-api-key", ep.ApiKey);
                msg.Headers.Add("anthropic-version", ApiVersion);
            }, ct).ConfigureAwait(false);
            if (!reply.Ok) return reply;

            reply.Text = ExtractAllText(reply.Text);
            return reply;
        }

        /// <summary>拼接回包 content 数组里全部 text 块（{"type":"text","text":"…"} 按序拼接）。</summary>
        private static string ExtractAllText(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var sb = new StringBuilder();
            int i = 0;
            while ((i = json.IndexOf("\"type\":\"text\"", i, StringComparison.Ordinal)) >= 0)
            {
                // 该块内找 text 字段值（限制扫描范围到块尾对象界外一段，防跨块误拼）
                int scope = json.IndexOf('}', i);
                if (scope < 0) break;
                string seg = json.Substring(i, scope - i);
                string t = Json.FindString(seg, "text");
                if (t != null) sb.Append(t);
                i = scope;
            }
            return sb.ToString();
        }
    }
}
