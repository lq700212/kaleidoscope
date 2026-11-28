using System;
using System.Collections.Generic;

namespace Kaleidoscope.Infer
{
    /// <summary>
    /// 解析出的归一化检测框（0~1000，左上/右下）。
    /// ⚠️ 语义红线：无置信度字段——模型口径即无分数输出，判定只走 ROI-IoU 几何规则。
    /// </summary>
    public class InferBox
    {
        public string Label { get; set; } = "";
        public int X1 { get; set; }
        public int Y1 { get; set; }
        public int X2 { get; set; }
        public int Y2 { get; set; }

        public override string ToString() { return Label + " (" + X1 + "," + Y1 + ")-(" + X2 + "," + Y2 + ")"; }
    }

    /// <summary>
    /// 框解析器：从推理回包文本提取 0~1000 归一化框。两种策略（自动容错，按命中走）：
    ///  ①JSON bbox_2d 数组：{"bbox_2d":[x1,y1,x2,y2],"label":"…"}（Qwen2.5-VL grounding
    ///    默认输出形态——坐标归一化到 0~1000 参考网格，与 AIris 全链路天然一致；容 ```json 围栏）
    ///  ②&lt;box&gt; 标签文本：LocateAnything 文本形态（&lt;box&gt;(x1,y1),(x2,y2)&lt;/box&gt; 与
    ///    逗号直排 x1,y1,x2,y2 两种写法都收）
    /// 统一收口：钳位 0~1000、x1&lt;x2 / y1&lt;y2 校正、零面积丢弃。
    ///
    /// 【label 必须带出来（2026-11-28 接入 Ollama qwen2.5vl:3b 实测）】JudgeEngine 的
    /// CountMatched 会直接丢弃 Label 为空的框（无法确认框属于哪个零件）——只解坐标不解 label，
    /// 真实模型即使出框正确也会被判成"缺件 NG"。旧实现 BuildBox(..., "") 正是这个坑，
    /// 本次补齐：在 bbox_2d 所在 JSON 对象片段内取 label（前后顺序都兼容）。
    /// </summary>
    public static class BoxParser
    {
        /// <summary>解析回包文本为框列表（两种策略各扫一遍，合并去重；解析不出返回空表非 null）。</summary>
        public static List<InferBox> Parse(string text)
        {
            var boxes = new List<InferBox>();
            if (string.IsNullOrEmpty(text)) return boxes;
            ParseJsonBboxes(text, boxes);
            if (boxes.Count == 0) ParseBoxTags(text, boxes);
            return boxes;
        }

        // —— 策略①：JSON bbox_2d 数组（Qwen grounding 形态） ——

        private static void ParseJsonBboxes(string text, List<InferBox> boxes)
        {
            // 剥 ```json 围栏（模型常给 markdown 包裹；直接全文本扫 bbox_2d 也行——围栏只是保险）
            string t = text;
            int fence = t.IndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                int open = t.IndexOf("json", fence, StringComparison.Ordinal);
                int start = open >= 0 ? open + 4 : fence + 3;
                int close = t.IndexOf("```", start, StringComparison.Ordinal);
                if (close > start) t = t.Substring(start, close - start);
            }

            int i = 0;
            while ((i = t.IndexOf("\"bbox_2d\"", i, StringComparison.Ordinal)) >= 0)
            {
                int[] arr = Json.FindIntArray(t.Substring(i), "bbox_2d");
                if (arr != null && arr.Length >= 4)
                {
                    // label 与 bbox_2d 同属一个框对象；模型两种字段顺序都可能输出，
                    // 故取"bbox_2d 前后最近的 { }"包成的对象片段再找 label。
                    var b = BuildBox(arr[0], arr[1], arr[2], arr[3], FindLabelOfBox(t, i));
                    if (b != null) boxes.Add(b);
                    i += 9;
                }
                else break;     // 解析推进失败防死循环
            }
        }

        /// <summary>
        /// 取 bbox_2d 所在框对象的 label（找不到返回空串）。
        /// 向前找最近 '{'、向后找最近 '}' 得到对象片段——bbox_2d 与 label 谁在前都覆盖；
        /// 只扫当前对象，不会串到相邻框的 label。
        /// </summary>
        private static string FindLabelOfBox(string text, int bboxPos)
        {
            int start = text.LastIndexOf('{', bboxPos);
            if (start < 0) return "";
            int end = text.IndexOf('}', bboxPos);
            if (end < 0) return "";
            string obj = text.Substring(start, end - start + 1);
            return Json.FindString(obj, "label") ?? "";
        }

        // —— 策略②：&lt;box&gt; 标签（LocateAnything 文本形态） ——

        private static void ParseBoxTags(string text, List<InferBox> boxes)
        {
            const string open = "<box>";
            const string close = "</box>";
            int i = 0;
            while ((i = text.IndexOf(open, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int e = text.IndexOf(close, i, StringComparison.OrdinalIgnoreCase);
                if (e < 0) break;
                string seg = text.Substring(i + open.Length, e - i - open.Length);
                // 去圆括号/空白后按逗号拆 4 数：(x1,y1),(x2,y2) 与 x1,y1,x2,y2 同构
                seg = seg.Replace("(", "").Replace(")", "").Replace(" ", "");
                var parts = seg.Split(',');
                if (parts.Length >= 4)
                {
                    int x1, y1, x2, y2;
                    if (int.TryParse(parts[0], out x1) && int.TryParse(parts[1], out y1)
                        && int.TryParse(parts[2], out x2) && int.TryParse(parts[3], out y2))
                    {
                        var b = BuildBox(x1, y1, x2, y2, "");
                        if (b != null) boxes.Add(b);
                    }
                }
                i = e + close.Length;
            }
        }

        /// <summary>建框统一收口：钳位 0~1000 + 端点校正 + 零面积丢弃。</summary>
        private static InferBox BuildBox(int x1, int y1, int x2, int y2, string label)
        {
            x1 = Clamp(x1); y1 = Clamp(y1); x2 = Clamp(x2); y2 = Clamp(y2);
            if (x1 > x2) { int t = x1; x1 = x2; x2 = t; }
            if (y1 > y2) { int t = y1; y1 = y2; y2 = t; }
            if (x2 - x1 < 1 || y2 - y1 < 1) return null;    // 零/负面积丢
            return new InferBox { Label = label ?? "", X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
        }

        private static int Clamp(int v) { return v < 0 ? 0 : (v > 1000 ? 1000 : v); }
    }
}
