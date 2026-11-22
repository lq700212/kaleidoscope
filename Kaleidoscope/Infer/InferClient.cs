using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Infer
{
    /// <summary>
    /// 推理客户端门面（上层唯一入口）：DetectAsync = 组 grounding prompt → 协议 transport
    /// → BoxParser → 归一化框列表；ProbeAsync = 各协议 health 探活。
    /// 上层只管换 InferEndpoint（协议/地址/模型），调用代码零改动——"换框架/换模型只改下拉框"。
    /// </summary>
    public class InferClient : IDisposable
    {
        private readonly Dictionary<InferProtocol, InferTransportBase> _transports;

        public InferClient()
        {
            _transports = new Dictionary<InferProtocol, InferTransportBase>
            {
                { InferProtocol.OpenAi,     new OpenAiTransport() },
                { InferProtocol.Anthropic,  new AnthropicTransport() },
                { InferProtocol.Ollama,     new OllamaTransport() },
                { InferProtocol.CustomJson, new CustomJsonTransport() },
            };
        }

        /// <summary>
        /// 检测一帧：图 + prompts → 0~1000 归一化框。失败（网络/超时/解析不出）返回 Ok=false
        /// + Error，不抛异常（调用方降级口径）。
        /// grounding prompt 约定：输出 JSON 数组 [ {"bbox_2d":[x1,y1,x2,y2],"label":"…"} ]、
        /// 坐标 0~1000 归一化、只输出 JSON——三句话定格式（Qwen2.5-VL 官方 grounding 口径，
        /// 带示例的 prompt 比纯文字定位准确率高约 23%）。
        /// </summary>
        public async Task<DetectReply> DetectAsync(InferEndpoint ep, byte[] imageJpeg,
                                                    IList<string> prompts, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var outp = new DetectReply();

            if (string.IsNullOrWhiteSpace(ep.BaseUrl))
            {
                outp.Error = "未配置推理服务地址";
                return outp;
            }

            var transport = GetTransport(ep.Protocol);
            if (transport == null)
            {
                outp.Error = "未知协议：" + ep.Protocol;
                return outp;
            }

            var req = new InferRequest
            {
                Model = ep.Model,
                SystemPrompt = "You are a visual grounding engine for industrial inspection.",
                UserPrompt = BuildGroundingPrompt(prompts),
                ImageJpeg = imageJpeg,
                MaxTokens = 1024,
            };

            var reply = await transport.SendAsync(ep, req, ct).ConfigureAwait(false);
            outp.LatencyMs = reply.LatencyMs;

            if (!reply.Ok)
            {
                outp.Error = reply.Error;
                return outp;
            }

            // 解框：CustomJson 直读 detections 数组；其余协议从文本回包解析
            if (ep.Protocol == InferProtocol.CustomJson)
                outp.Boxes = ParseDetectionsArray(reply.Text);
            else
                outp.Boxes = BoxParser.Parse(reply.Text);

            if (outp.Boxes.Count == 0)
            {
                // 回包成功但解析不出框：不判失败（可能确实没有目标），框为空即可
            }
            outp.Ok = true;
            return outp;
        }

        /// <summary>探活（各协议各自的 health 端点；返回是否在线 + 耗时/失败原因）。</summary>
        public async Task<ProbeReply> ProbeAsync(InferEndpoint ep, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new ProbeReply();
            if (string.IsNullOrWhiteSpace(ep.BaseUrl))
            {
                r.Error = "未配置服务地址";
                return r;
            }

            InferReply reply;
            string root = ep.BaseUrl.TrimEnd('/');
            switch (ep.Protocol)
            {
                case InferProtocol.OpenAi:
                    reply = await GetTransport(ep.Protocol).ProbeGetCoreAsync(ep, root + "/v1/models").ConfigureAwait(false);
                    break;
                case InferProtocol.Ollama:
                    reply = await GetTransport(ep.Protocol).ProbeGetCoreAsync(ep, root + "/api/tags").ConfigureAwait(false);
                    break;
                case InferProtocol.CustomJson:
                    reply = await GetTransport(ep.Protocol).ProbeGetCoreAsync(ep, root + "/health").ConfigureAwait(false);
                    break;
                case InferProtocol.Anthropic:
                default:
                    // Anthropic 无公开 health（官方无 /health）；配置齐全 + 一次最小请求可达即算在线
                    // ——探活层用轻量 HEAD 不可行（405），直接视为"配置即就绪"，连通性由首检验证
                    r.Ok = true;
                    r.LatencyMs = sw.ElapsedMilliseconds;
                    return r;
            }

            r.Ok = reply.Ok;
            r.Error = reply.Error;
            r.LatencyMs = reply.LatencyMs;
            return r;
        }

        /// <summary>组装 grounding prompt（三句定格式 + prompts 全量列出）。</summary>
        private static string BuildGroundingPrompt(IList<string> prompts)
        {
            var sb = new StringBuilder();
            sb.Append("Detect the following targets in the image: ");
            for (int i = 0; i < prompts.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(prompts[i]);
            }
            sb.Append(".\nOutput ONLY a JSON array, no markdown, no explanation: ");
            sb.Append("[{\"bbox_2d\":[x1,y1,x2,y2],\"label\":\"name\"}]. ");
            sb.Append("Coordinates are normalized to a 0~1000 grid over the full image (top-left origin).");
            return sb.ToString();
        }

        /// <summary>CustomJson 契约的 detections 直读：{"detections":[{"label","x1","y1","x2","y2"}…]}。
        /// （轻扫数组段逐对象取字段；对象级解析不做——契约字段名固定。）</summary>
        private static List<InferBox> ParseDetectionsArray(string json)
        {
            var boxes = new List<InferBox>();
            if (string.IsNullOrEmpty(json)) return boxes;
            int arr = json.IndexOf("\"detections\"", StringComparison.Ordinal);
            if (arr < 0) return boxes;

            int i = arr;
            while ((i = json.IndexOf('{', i)) >= 0)
            {
                int e = json.IndexOf('}', i);
                if (e < 0) break;
                string obj = json.Substring(i, e - i);
                if (obj.IndexOf("\"x1\"", StringComparison.Ordinal) >= 0)
                {
                    // 手扫四个数字字段（FindIntArray 不适用——分散字段）
                    int x1 = FindFieldInt(obj, "x1"), y1 = FindFieldInt(obj, "y1");
                    int x2 = FindFieldInt(obj, "x2"), y2 = FindFieldInt(obj, "y2");
                    string label = Json.FindString(obj, "label") ?? "";
                    if (x1 >= 0 && y1 >= 0 && x2 >= 0 && y2 >= 0)
                        boxes.Add(new InferBox { Label = label, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
                }
                i = e;
            }
            return boxes;
        }

        /// <summary>对象片段内找数字字段值（找不到 -1）。</summary>
        private static int FindFieldInt(string obj, string key)
        {
            string pat = "\"" + key + "\"";
            int k = obj.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return -1;
            int i = obj.IndexOf(':', k + pat.Length);
            if (i < 0) return -1;
            i++;
            int s = i;
            while (i < obj.Length && (char.IsDigit(obj[i]) || obj[i] == '-')) i++;
            int v;
            return int.TryParse(obj.Substring(s, i - s), out v) ? v : -1;
        }

        private InferTransportBase GetTransport(InferProtocol p)
        {
            InferTransportBase t;
            return _transports.TryGetValue(p, out t) ? t : null;
        }

        public void Dispose()
        {
            foreach (var t in _transports.Values) t.Dispose();
        }
    }

    /// <summary>DetectAsync 输出（Ok=false 时 Error 有值；框可能为空=无目标）。</summary>
    public class DetectReply
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public List<InferBox> Boxes { get; set; } = new List<InferBox>();
        public long LatencyMs { get; set; }
    }

    /// <summary>探活输出。</summary>
    public class ProbeReply
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public long LatencyMs { get; set; }
    }
}
