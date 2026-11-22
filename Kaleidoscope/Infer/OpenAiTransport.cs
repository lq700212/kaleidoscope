using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Infer
{
    /// <summary>
    /// OpenAI /v1/chat/completions 兼容传输器（生态最广的信封：vLLM、llama.cpp server、
    /// LMDeploy、Ollama 兼容层、各类网关通吃）。组包：content 数组 = text + image_url
    /// （base64 必须完整 data URI 前缀，规范原文口径）；回包取 choices[0].message.content。
    /// 鉴权：ApiKey 非空时 Authorization: Bearer（本地服务可空省略）。
    /// </summary>
    public class OpenAiTransport : InferTransportBase
    {
        public override async Task<InferReply> SendAsync(InferEndpoint ep, InferRequest req, CancellationToken ct)
        {
            string url = ep.BaseUrl.TrimEnd('/') + "/v1/chat/completions";

            var sb = new StringBuilder(256 + (req.ImageJpeg != null ? req.ImageJpeg.Length * 4 / 3 : 0));
            sb.Append("{\"model\":\"").Append(J(string.IsNullOrEmpty(req.Model) ? ep.Model : req.Model)).Append('"');
            if (req.MaxTokens.HasValue) sb.Append(",\"max_tokens\":").Append(req.MaxTokens.Value);
            sb.Append(",\"messages\":[");
            if (!string.IsNullOrEmpty(req.SystemPrompt))
                sb.Append("{\"role\":\"system\",\"content\":\"").Append(J(req.SystemPrompt)).Append("\"},");
            sb.Append("{\"role\":\"user\",\"content\":[");
            if (req.ImageJpeg != null && req.ImageJpeg.Length > 0)
            {
                sb.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,")
                  .Append(Convert.ToBase64String(req.ImageJpeg))
                  .Append("\"}},");
            }
            sb.Append("{\"type\":\"text\",\"text\":\"").Append(J(req.UserPrompt)).Append("\"}");
            sb.Append("]}]}");

            var reply = await PostJsonAsync(ep, url, sb.ToString(), msg =>
            {
                if (!string.IsNullOrEmpty(ep.ApiKey))
                    msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ep.ApiKey);
            }, ct).ConfigureAwait(false);
            if (!reply.Ok) return reply;

            // 解回包：choices[0].message.content（手扫比反序列化整个响应省事且容错）
            reply.Text = Json.FindString(reply.Text, "content");
            return reply;
        }
    }
}
