using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Infer
{
    /// <summary>
    /// Ollama 原生 /api/chat 传输器。信封差异（与 OpenAI 兼容层不同！）：图像是消息里的
    /// images 字段 = **裸 base64 字符串数组**（不带 data URI 前缀——REST 传 base64 字节，
    /// 文件路径写法在 HTTP 调用下无效）；stream 必须显式 false（服务端默认流式，非流式
    /// 客户端会把 SSE 拼成一坨）；回包取 message.content。无鉴权（本地服务）。
    /// </summary>
    public class OllamaTransport : InferTransportBase
    {
        public override async Task<InferReply> SendAsync(InferEndpoint ep, InferRequest req, CancellationToken ct)
        {
            string url = ep.BaseUrl.TrimEnd('/') + "/api/chat";

            var sb = new StringBuilder(256 + (req.ImageJpeg != null ? req.ImageJpeg.Length * 4 / 3 : 0));
            sb.Append("{\"model\":\"").Append(J(string.IsNullOrEmpty(req.Model) ? ep.Model : req.Model)).Append('"');
            sb.Append(",\"stream\":false");     // 非流式（缺省 true 会回 SSE 流）
            sb.Append(",\"messages\":[{\"role\":\"user\",\"content\":\"").Append(J(req.UserPrompt)).Append('"');
            if (req.ImageJpeg != null && req.ImageJpeg.Length > 0)
                sb.Append(",\"images\":[\"").Append(Convert.ToBase64String(req.ImageJpeg)).Append("\"]");
            sb.Append("}]}");

            var reply = await PostJsonAsync(ep, url, sb.ToString(), null, ct).ConfigureAwait(false);
            if (!reply.Ok) return reply;

            // 解回包：{"message":{"role":"assistant","content":"…"},…}
            // message 对象名与 content 字段同名撞 OpenAI 的 content——限 scope 到 message 对象内再取
            string json = reply.Text;
            int m = json.IndexOf("\"message\"", StringComparison.Ordinal);
            reply.Text = m >= 0 ? Json.FindString(json.Substring(m), "content") : Json.FindString(json, "content");
            return reply;
        }
    }
}
