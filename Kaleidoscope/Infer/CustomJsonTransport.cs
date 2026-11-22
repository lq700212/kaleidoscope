using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Infer
{
    /// <summary>
    /// 自研 FastAPI 检测契约传输器（调研报告 §10.2 既定）：POST /detect
    /// 请求 {model, prompts[], image_b64} → 响应 {ok, detections:[{label,x1,y1,x2,y2}]}。
    /// 坐标 0~1000 归一化、无置信度——与 AIris 全链路口径一致，框直出不走文本解析。
    /// </summary>
    public class CustomJsonTransport : InferTransportBase
    {
        public override async Task<InferReply> SendAsync(InferEndpoint ep, InferRequest req, CancellationToken ct)
        {
            string url = ep.BaseUrl.TrimEnd('/') + "/detect";
            // prompts 并入 UserPrompt 约定：上层把 prompts 用 '\n' 连进 UserPrompt
            // （契约字段 prompts 是数组——这里按行拆回数组）
            var prompts = new List<string>();
            if (!string.IsNullOrEmpty(req.UserPrompt))
                prompts.AddRange(req.UserPrompt.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));

            var sb = new StringBuilder(256 + (req.ImageJpeg != null ? req.ImageJpeg.Length * 4 / 3 : 0));
            sb.Append("{\"model\":\"").Append(J(string.IsNullOrEmpty(req.Model) ? ep.Model : req.Model)).Append('"');
            sb.Append(",\"prompts\":[");
            for (int i = 0; i < prompts.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(J(prompts[i])).Append('"');
            }
            sb.Append(']');
            if (req.ImageJpeg != null && req.ImageJpeg.Length > 0)
                sb.Append(",\"image_b64\":\"").Append(Convert.ToBase64String(req.ImageJpeg)).Append('"');
            sb.Append('}');

            var reply = await PostJsonAsync(ep, url, sb.ToString(), null, ct).ConfigureAwait(false);
            if (!reply.Ok) return reply;

            // 契约响应 ok=false → 降级口径
            if (reply.Text != null && reply.Text.Contains("\"ok\":false"))
            {
                reply.Ok = false;
                string err = Json.FindString(reply.Text, "error");
                reply.Error = string.IsNullOrEmpty(err) ? "服务返回失败" : err;
            }
            // 文本字段放原始 JSON（BoxParser/InferClient 直接解析 detections；Text 语义=未加工回包）
            return reply;
        }
    }
}
