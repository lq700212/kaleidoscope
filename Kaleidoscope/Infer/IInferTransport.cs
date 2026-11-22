using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Infer
{
    /// <summary>一次推理请求（图像以 JPEG 字节流过接口——不引 System.Drawing，WinForms/WPF 宿主通用）。</summary>
    public class InferRequest
    {
        /// <summary>模型名（服务端路由用；可空=服务端默认）。</summary>
        public string Model { get; set; } = "";

        /// <summary>系统提示（可空；anthropic 独立顶层字段，其余并进消息流）。</summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>用户提示（检测目标/输出格式约定）。</summary>
        public string UserPrompt { get; set; } = "";

        /// <summary>JPEG 图像字节（可 null=纯文本请求，如探活后的轻量调用）。</summary>
        public byte[] ImageJpeg { get; set; }

        /// <summary>最大输出 token（可空=服务端默认）。</summary>
        public int? MaxTokens { get; set; }
    }

    /// <summary>一次推理回包（统一取文本；失败走 Error 不抛异常——调用方降级口径）。</summary>
    public class InferReply
    {
        public bool Ok { get; set; }
        /// <summary>回包文本（choices[0].message.content / content[].text 拼接 / message.content）。</summary>
        public string Text { get; set; } = "";
        /// <summary>失败原因（网络/HTTP 状态/响应解析；Ok=false 时有值）。</summary>
        public string Error { get; set; } = "";
        /// <summary>HTTP 状态码（0=网络层失败未到 HTTP）。</summary>
        public int HttpStatus { get; set; }
        /// <summary>往返耗时 ms（引擎页"单帧耗时"展示源）。</summary>
        public long LatencyMs { get; set; }
    }

    /// <summary>
    /// 协议传输器（信封差异收口点：组包/头/端点路径/回包取文本）。
    /// 实现约定：失败返回 Ok=false + Error，不抛异常（检测线程 GetResult 同步等待，
    /// 抛异常会打断 RunMonitor 主循环）；线程安全（多实例各持 HttpClient 更稳，单实例串行用）。
    /// </summary>
    public interface IInferTransport
    {
        /// <summary>发一次推理请求（同步包装异步实现——net472 检测线程 GetResult 口径）。</summary>
        Task<InferReply> SendAsync(InferEndpoint ep, InferRequest req, CancellationToken ct);
    }
}
