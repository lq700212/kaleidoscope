using System;
using System.Collections.Generic;

namespace YoloDetection
{
    /// <summary>
    /// 检测结果后处理器接口（策略模式）。
    /// 将结果过滤/裁剪逻辑从检测器中分离：检测器只负责推理，后处理器负责结果加工。
    /// </summary>
    public interface IDetectionResultProcessor
    {
        /// <summary>处理器名称（用于日志与调试）</summary>
        string ProcessorName { get; }

        /// <summary>处理检测结果。rawResults 可能为 null；返回值不得为 null。</summary>
        List<DetectionResult> Process(List<DetectionResult> rawResults, int imageWidth, int imageHeight);
    }

    /// <summary>
    /// 默认后处理器：把检测框裁剪到画面边界内，并过滤过小（噪声）的框。
    /// </summary>
    public class DefaultResultProcessor : IDetectionResultProcessor
    {
        public string ProcessorName => "Default";

        /// <summary>最小保留宽度（像素）</summary>
        public float MinWidth { get; set; } = 10;

        /// <summary>最小保留高度（像素）</summary>
        public float MinHeight { get; set; } = 20;

        public List<DetectionResult> Process(List<DetectionResult> rawResults, int imageWidth, int imageHeight)
        {
            if (rawResults == null || rawResults.Count == 0)
                return new List<DetectionResult>();

            var processed = new List<DetectionResult>();

            foreach (var result in rawResults)
            {
                float left = Math.Max(0, result.Left);
                float top = Math.Max(0, result.Top);
                float right = Math.Min(imageWidth, result.Right);
                float bottom = Math.Min(imageHeight, result.Bottom);

                float clippedWidth = right - left;
                float clippedHeight = bottom - top;

                // 完全超出画面的丢弃
                if (clippedWidth <= 0 || clippedHeight <= 0)
                    continue;

                // 过小的视为噪声
                if (clippedWidth < MinWidth || clippedHeight < MinHeight)
                    continue;

                processed.Add(new DetectionResult
                {
                    ClassId = result.ClassId,
                    ClassName = result.ClassName,
                    Confidence = result.Confidence,
                    X = left + clippedWidth / 2,
                    Y = top + clippedHeight / 2,
                    Width = clippedWidth,
                    Height = clippedHeight
                });
            }

            return processed;
        }
    }

    /// <summary>
    /// 尺寸过滤处理器：按绝对/相对尺寸上下限过滤检测框。
    /// </summary>
    public class SizeFilterProcessor : IDetectionResultProcessor
    {
        public string ProcessorName => "SizeFilter";

        public float MinWidth { get; set; } = 10;
        public float MinHeight { get; set; } = 20;

        /// <summary>最大宽度比例（相对于图像宽度）；默认不限制</summary>
        public float MaxWidthRatio { get; set; } = float.MaxValue;

        /// <summary>最大高度比例（相对于图像高度）；默认不限制</summary>
        public float MaxHeightRatio { get; set; } = float.MaxValue;

        public List<DetectionResult> Process(List<DetectionResult> rawResults, int imageWidth, int imageHeight)
        {
            if (rawResults == null || rawResults.Count == 0)
                return new List<DetectionResult>();

            var processed = new List<DetectionResult>();

            foreach (var result in rawResults)
            {
                if (result.Width < MinWidth) continue;
                if (result.Height < MinHeight) continue;
                if (result.Width > imageWidth * MaxWidthRatio) continue;
                if (result.Height > imageHeight * MaxHeightRatio) continue;

                processed.Add(result);
            }

            return processed;
        }
    }

    /// <summary>
    /// 组合后处理器（组合模式）：按添加顺序串行执行多个处理器。
    /// </summary>
    public class CompositeResultProcessor : IDetectionResultProcessor
    {
        public string ProcessorName => "Composite";

        private readonly List<IDetectionResultProcessor> _processors = new List<IDetectionResultProcessor>();

        /// <summary>添加处理器到处理链末尾</summary>
        public void AddProcessor(IDetectionResultProcessor processor)
        {
            if (processor == null) throw new ArgumentNullException(nameof(processor));
            _processors.Add(processor);
        }

        /// <summary>从处理链移除处理器</summary>
        public void RemoveProcessor(IDetectionResultProcessor processor)
        {
            _processors.Remove(processor);
        }

        public List<DetectionResult> Process(List<DetectionResult> rawResults, int imageWidth, int imageHeight)
        {
            var results = rawResults ?? new List<DetectionResult>();

            foreach (var processor in _processors)
            {
                results = processor.Process(results, imageWidth, imageHeight);
            }

            return results;
        }
    }
}
