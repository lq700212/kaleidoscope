using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Infer
{
    /// <summary>
    /// 推理传输器共用基座：HttpClient 生命周期 + POST 收口 + TLS1.2（net472 缺省不走 1.2，
    /// 现代 HTTPS 服务全要）+ 错误统一转 InferReply.Ok=false。子类只管组包体与解回包文本。
    /// JSON 组包用 StringBuilder 手拼（依赖字段极少、结构稳定——DataContractJsonSerializer
    /// 对匿名结构/字典不友好，手拼反而最直白可审计；解析同理走轻量字符串扫描或手动反序列化）。
    /// </summary>
    public abstract class InferTransportBase : IInferTransport
    {
        protected readonly HttpClient _http;

        static InferTransportBase()
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; }
            catch { }   // 老平台已含 TLS1.2 时抛不覆盖，忽略
        }

        protected InferTransportBase()
        {
            _http = new HttpClient();
            // 超时一律走每次请求的 CancellationTokenSource（见 PostJsonAsync/GetAsync）。
            // 【为什么不能改 _http.Timeout（2026-11-28 实测踩坑）】.NET Framework 的 HttpClient
            // 在发出过第一个请求后再设置 Timeout 会抛 InvalidOperationException（"此实例已经启动了
            // 一个或多个请求。只能在发送第一个请求之前修改属性"）——而引擎页的调用顺序恰好是
            // 先 ProbeAsync（GET /api/tags）再 DetectAsync（POST /api/chat），第二个请求必炸，
            // 表现为"探活在线、检测永远降级"。故这里固定为无限，每请求用 CTS.CancelAfter 控超时。
            _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        }

        public abstract Task<InferReply> SendAsync(InferEndpoint ep, InferRequest req, CancellationToken ct);

        /// <summary>POST JSON → 回包文本（统一收口：状态码/异常/耗时）。ok=false 时 Error 有值。
        /// 超时用「链接调用方 ct + CancelAfter(ep.TimeoutMs)」实现每请求独立超时
        /// （HttpClient.Timeout 在 net472 发过请求后不可再改，见构造函数注释）。</summary>
        protected async Task<InferReply> PostJsonAsync(InferEndpoint ep, string url, string jsonBody,
                                                        Action<HttpRequestMessage> setHeaders, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var reply = new InferReply();
            try
            {
                int timeoutMs = Math.Max(1000, ep.TimeoutMs);
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    using (var content = new StringContent(jsonBody, Encoding.UTF8, "application/json"))
                    using (var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
                    {
                        if (setHeaders != null) setHeaders(msg);
                        using (var resp = await _http.SendAsync(msg, cts.Token).ConfigureAwait(false))
                        {
                            reply.HttpStatus = (int)resp.StatusCode;
                            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            reply.LatencyMs = sw.ElapsedMilliseconds;
                            if (!resp.IsSuccessStatusCode)
                            {
                                reply.Error = "HTTP " + (int)resp.StatusCode + "：" + Truncate(text, 200);
                                return reply;
                            }
                            reply.Text = text;
                            reply.Ok = true;
                            return reply;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                reply.Error = "请求超时/取消（" + ep.TimeoutMs + "ms）";
                reply.LatencyMs = sw.ElapsedMilliseconds;
                return reply;
            }
            catch (Exception ex)
            {
                reply.Error = "服务不可达：" + ex.Message;
                reply.LatencyMs = sw.ElapsedMilliseconds;
                return reply;
            }
        }

        /// <summary>GET 探活（各协议 health 端点；状态码 2xx 即视为在线）。
        /// internal 由 InferClient 探活转发用（ProbeGetCoreAsync 扩展入口）。</summary>
        internal Task<InferReply> ProbeGetCoreAsync(InferEndpoint ep, string url)
        {
            return GetAsync(ep, url, null, CancellationToken.None);
        }

        /// <summary>GET 任意端点并带回包文本（模型目录发现用）。
        /// 与探活共用同一超时口径（≤3s）；鉴权头由调用方按协议组装
        /// （OpenAI=Bearer / Anthropic=x-api-key+anthropic-version）。
        /// internal：只给同程序集的 InferClient 用，避免上层绕过门面。</summary>
        internal Task<InferReply> GetCoreAsync(InferEndpoint ep, string url, Action<HttpRequestMessage> setHeaders, CancellationToken ct)
        {
            return GetAsync(ep, url, setHeaders, ct);
        }

        /// <summary>GET 探活（各协议 health 端点；状态码 2xx 即视为在线）。
        /// 超时从紧（≤3s），同样走每请求 CTS（不改 HttpClient.Timeout，见构造函数注释）。</summary>
        protected async Task<InferReply> GetAsync(InferEndpoint ep, string url, Action<HttpRequestMessage> setHeaders, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var reply = new InferReply();
            try
            {
                int timeoutMs = Math.Max(1000, Math.Min(ep.TimeoutMs, 3000));  // 探活从紧（≤3s）
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    using (var msg = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (setHeaders != null) setHeaders(msg);
                        using (var resp = await _http.SendAsync(msg, cts.Token).ConfigureAwait(false))
                        {
                            // 读文本：探活不看内容，但模型目录（/v1/models、/api/tags）要解析回包
                            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            reply.HttpStatus = (int)resp.StatusCode;
                            reply.LatencyMs = sw.ElapsedMilliseconds;
                            reply.Ok = (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
                            if (!reply.Ok) reply.Error = "HTTP " + (int)resp.StatusCode + "：" + Truncate(text, 200);
                            else reply.Text = text;
                            return reply;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                reply.Error = "服务不可达：" + ex.Message;
                reply.LatencyMs = sw.ElapsedMilliseconds;
                return reply;
            }
        }

        /// <summary>JSON 字符串字面量转义（手拼请求体必用：引号/反斜杠/控制字符）。</summary>
        protected static string J(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        public virtual void Dispose() { _http.Dispose(); }
    }
}
