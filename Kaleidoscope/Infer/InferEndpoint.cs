using System;

namespace Kaleidoscope.Infer
{
    /// <summary>推理服务协议族（信封差异，语义统一为"图+文字提示→文字回包"）。</summary>
    public enum InferProtocol
    {
        /// <summary>OpenAI /v1/chat/completions 兼容（vLLM / llama.cpp server / LMDeploy / Ollama 兼容层）。</summary>
        OpenAi,
        /// <summary>Anthropic /v1/messages（x-api-key + anthropic-version 头）。</summary>
        Anthropic,
        /// <summary>Ollama 原生 /api/chat（images=裸 base64 数组，非 data URL）。</summary>
        Ollama,
        /// <summary>自研 FastAPI 检测契约 POST /detect（调研报告 §10.2：框直出，不走文本解析）。</summary>
        CustomJson,
    }

    /// <summary>
    /// 推理服务端点配置（一份配置 = 协议 + 地址 + 模型 + 鉴权；INI/上层配置载体映射进来）。
    /// </summary>
    public class InferEndpoint
    {
        /// <summary>协议（决定用哪个 transport 组包/解包）。</summary>
        public InferProtocol Protocol { get; set; }

        /// <summary>服务根地址（如 http://127.0.0.1:8000；transport 自行拼端点路径）。</summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>模型名/服务路由名（openai: model 字段；ollama: model；anthropic: model；custom: 路由用）。</summary>
        public string Model { get; set; } = "";

        /// <summary>API Key（openai=Bearer；anthropic=x-api-key；ollama/local=可空）。</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>HTTP 超时 ms（检测单帧 430ms 量级，缺省 5000 留余量）。</summary>
        public int TimeoutMs { get; set; } = 5000;
    }
}
