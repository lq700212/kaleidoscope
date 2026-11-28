using System;
using System.Collections.Generic;

namespace Kaleidoscope.Infer
{
    /// <summary>
    /// 推理服务模型目录发现（"这个服务上到底有哪些模型"）——与具体厂商无关的通用能力。
    ///
    /// 【为什么需要】
    /// 接一个新模型服务时，模型名往往是一长串 id（Magpie 反代网关的
    /// "workbuddy/deepseek-v4.1-flash"、vLLM 的 "Qwen/Qwen3-VL-8B-Instruct"），
    /// 手抄极易拼错（错一个字符就是 404/400）。标准服务都提供模型列表端点：
    ///   · OpenAI 兼容（vLLM / llama.cpp server / LMDeploy / Magpie 反代网关）→ GET /v1/models
    ///   · Ollama → GET /api/tags
    /// 本类负责把回包解析成统一 DTO；网络请求由 <see cref="InferClient.ListModelsAsync"/> 发。
    ///
    /// 【为什么不用 Newtonsoft / DataContractJsonSerializer】
    /// 本库铁律：不引第三方序列化依赖。模型列表结构简单（对象数组 + 少量字段），
    /// 用 <see cref="Json"/> 的字符串扫描 + 本类的括号深度切分足够；且能容忍服务端
    /// 多返回字段（各家网关字段差异很大，强类型反序列化反而要不断补契约）。
    ///
    /// 【图像能力三态】
    /// 检测/视觉任务必须图像输入，但"模型是否支持图像"只有部分服务会声明：
    ///   · Magpie /v1/models 每项带 modalities.input（如 ["text","image"]）→ 可确定；
    ///   · OpenAI 官方 /v1/models、Ollama /api/tags 不声明 → 只能标"未知"或按名字猜。
    /// 所以 <see cref="InferModelInfo.ImageSupportKnown"/> 区分"确定"与"猜测"，
    /// UI 不要把"未知"误显示成"纯文本"（会把能用的模型挡掉）。
    /// </summary>
    public static class InferModelCatalog
    {
        /// <summary>
        /// 解析 OpenAI 兼容 /v1/models 回包（Magpie / vLLM / llama.cpp / LMDeploy 通用）。
        /// 兼容字段：id（必需）、display_name（可选）、owned_by（可选）、
        /// modalities.input（可选，含 "image" 即支持图像）。
        /// 回包没有 modalities 字段时，所有模型 ImageSupportKnown=false（能力未知，不是"不支持"）。
        /// </summary>
        public static List<InferModelInfo> ParseOpenAi(string json)
        {
            var list = new List<InferModelInfo>();
            if (string.IsNullOrEmpty(json)) return list;

            string arr = ExtractArray(json, "data");
            if (arr == null) return list;

            // 整个回包是否提供能力元数据：没有就一律"未知"，有就逐项判定
            bool anyModalities = json.IndexOf("\"modalities\"", StringComparison.Ordinal) >= 0;

            foreach (string obj in SplitObjects(arr))
            {
                string id = Json.FindString(obj, "id");
                if (string.IsNullOrEmpty(id)) continue;

                string display = Json.FindString(obj, "display_name");
                string owner = Json.FindString(obj, "owned_by");
                bool known = anyModalities && obj.IndexOf("\"modalities\"", StringComparison.Ordinal) >= 0;
                bool image = known && InputModalitiesContainImage(obj);

                list.Add(new InferModelInfo
                {
                    Id = id,
                    DisplayName = string.IsNullOrEmpty(display) ? id : display,
                    Owner = owner ?? "",
                    SupportsImage = image,
                    ImageSupportKnown = known,
                });
            }

            SortForVision(list);
            return list;
        }

        /// <summary>
        /// 解析 Ollama /api/tags 回包：{"models":[{"name":"qwen2.5vl:3b","model":"…"}…]}。
        /// Ollama 不回能力元数据，视觉能力只能按模型名启发式猜测（vl/vision/llava…），
        /// 因此 ImageSupportKnown 一律 false——UI 标"疑似图像"，不要当确定结论。
        /// </summary>
        public static List<InferModelInfo> ParseOllamaTags(string json)
        {
            var list = new List<InferModelInfo>();
            if (string.IsNullOrEmpty(json)) return list;

            string arr = ExtractArray(json, "models");
            if (arr == null) return list;

            foreach (string obj in SplitObjects(arr))
            {
                string id = Json.FindString(obj, "model");
                if (string.IsNullOrEmpty(id)) id = Json.FindString(obj, "name");
                if (string.IsNullOrEmpty(id)) continue;

                list.Add(new InferModelInfo
                {
                    Id = id,
                    DisplayName = id,
                    Owner = "",
                    SupportsImage = GuessOllamaVision(id),
                    ImageSupportKnown = false,
                });
            }

            SortForVision(list);
            return list;
        }

        /// <summary>视觉模型优先排序：确定支持 → 未知 → 确定不支持；同档按 id 字典序（列表稳定好找）。</summary>
        private static void SortForVision(List<InferModelInfo> list)
        {
            list.Sort(delegate (InferModelInfo a, InferModelInfo b)
            {
                int ra = Rank(a), rb = Rank(b);
                if (ra != rb) return ra.CompareTo(rb);
                return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static int Rank(InferModelInfo m)
        {
            if (m.ImageSupportKnown) return m.SupportsImage ? 0 : 2;
            return 1;   // 未知排中间：可能是视觉模型，不能直接沉底
        }

        /// <summary>Ollama 模型名启发式（Ollama 不回能力元数据）：常见视觉模型家族关键字。</summary>
        private static bool GuessOllamaVision(string id)
        {
            string s = (id ?? "").ToLowerInvariant();
            return s.Contains("vl") || s.Contains("vision") || s.Contains("llava")
                || s.Contains("minicpm-v") || s.Contains("moondream") || s.Contains("gemma3")
                || s.Contains("bakllava") || s.Contains("cogvlm");
        }

        /// <summary>对象内 modalities.input 数组是否含精确的 "image" 元素。</summary>
        private static bool InputModalitiesContainImage(string obj)
        {
            string mod = ExtractObject(obj, "modalities");
            if (mod == null) return false;
            string input = ExtractArray(mod, "input");
            if (input == null) return false;
            return input.IndexOf("\"image\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 取 JSON 中 "key" 对应的数组原文（含方括号）；找不到返回 null。
        /// 扫描跳过字符串字面量，避免 display_name 里的括号/引号干扰。
        /// </summary>
        private static string ExtractArray(string json, string key)
        {
            return ExtractBlock(json, key, '[', ']');
        }

        /// <summary>取 JSON 中 "key" 对应的对象原文（含花括号）；找不到返回 null。</summary>
        private static string ExtractObject(string json, string key)
        {
            return ExtractBlock(json, key, '{', '}');
        }

        /// <summary>通用块提取：定位 "key" 后第一个 open 字符，按深度匹配到对应 close。</summary>
        private static string ExtractBlock(string json, string key, char open, char close)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            string pat = "\"" + key + "\"";
            int k = json.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return null;

            int i = json.IndexOf(open, k + pat.Length);
            if (i < 0) return null;

            int depth = 0;
            bool inStr = false;
            for (int p = i; p < json.Length; p++)
            {
                char c = json[p];
                if (inStr)
                {
                    if (c == '\\') p++;              // 跳过转义字符（\" 不算收尾引号）
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return json.Substring(i, p - i + 1);
                }
            }
            return null;
        }

        /// <summary>把数组原文切成顶层对象文本（按 {} 深度；跳过字符串字面量）。</summary>
        private static List<string> SplitObjects(string arrayText)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(arrayText)) return list;

            int depth = 0;
            int start = -1;
            bool inStr = false;
            for (int p = 0; p < arrayText.Length; p++)
            {
                char c = arrayText[p];
                if (inStr)
                {
                    if (c == '\\') p++;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{')
                {
                    if (depth == 0) start = p;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        list.Add(arrayText.Substring(start, p - start + 1));
                        start = -1;
                    }
                }
            }
            return list;
        }
    }

    /// <summary>
    /// 一条服务端模型记录（协议无关的统一 DTO；UI 下拉/胶囊直接绑定）。
    /// </summary>
    public class InferModelInfo
    {
        /// <summary>模型 id（填进 InferEndpoint.Model 的原值，如 workbuddy/deepseek-v4.1-flash）。</summary>
        public string Id { get; set; } = "";

        /// <summary>展示名（服务端 display_name；没有则等于 Id）。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>提供方（OpenAI 的 owned_by；如 workbuddy / zcode；Ollama 为空）。</summary>
        public string Owner { get; set; } = "";

        /// <summary>是否支持图像输入。仅当 <see cref="ImageSupportKnown"/>=true 时才是确定结论。</summary>
        public bool SupportsImage { get; set; }

        /// <summary>
        /// 服务端是否明确声明了图像能力。
        /// false = 服务端没给能力元数据（如 OpenAI 官方 /v1/models、Ollama），
        /// <see cref="SupportsImage"/> 只是启发式猜测——UI 不要标成"纯文本"挡掉可用模型。
        /// </summary>
        public bool ImageSupportKnown { get; set; }

        /// <summary>
        /// 列表单行文案（ComboBox DisplayMemberPath 用）：
        /// "图像 | DeepSeek-V4.1-Flash · workbuddy  (workbuddy/deepseek-v4.1-flash)"。
        /// </summary>
        public string Label
        {
            get
            {
                string tag = ImageSupportKnown
                    ? (SupportsImage ? "图像" : "纯文本")
                    : (SupportsImage ? "疑似图像" : "能力未知");
                string owner = string.IsNullOrEmpty(Owner) ? "" : " · " + Owner;
                return tag + " | " + DisplayName + owner + "  (" + Id + ")";
            }
        }

        /// <summary>
        /// 短文案（胶囊按钮用）："DeepSeek-V4.1-Flash · workbuddy"。
        /// 同名模型可能来自多个提供方（实测 Magpie 的 GLM-5.3-Flash 有 4 个 id），必须带 owner。
        /// </summary>
        public string ChipLabel
        {
            get { return string.IsNullOrEmpty(Owner) ? DisplayName : DisplayName + " · " + Owner; }
        }

        public override string ToString() { return Label; }
    }

    /// <summary>ListModelsAsync 输出（Ok=false 时 Error 有值；Models 可能为空）。</summary>
    public class InferModelListReply
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public List<InferModelInfo> Models { get; set; } = new List<InferModelInfo>();
        public long LatencyMs { get; set; }
    }
}
