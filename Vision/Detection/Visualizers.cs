using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace YoloDetection
{
    /// <summary>
    /// 可视化器工厂：根据类型枚举创建对应的绘制器实例。
    /// 两个可视化器均为跨平台实现（Skia / OpenCV），全平台行为一致。
    /// </summary>
    public static class VisualizerFactory
    {
        public static IDetectionVisualizer Create(VisualizerType type)
        {
            switch (type)
            {
                case VisualizerType.YoloBuiltin:
                    return new YoloBuiltinVisualizer();
                case VisualizerType.OpenCV:
                    return new OpenCVVisualizer();
                default:
                    return new OpenCVVisualizer();
            }
        }
    }

    /// <summary>
    /// OpenCV 可视化器（绿色检测框）。
    /// 直接在 Mat 上绘制，无编解码开销，性能优于 GDI+ 方案；纯 OpenCvSharp 实现，全平台可用。
    /// </summary>
    public class OpenCVVisualizer : IDetectionVisualizer
    {
        public Mat Draw(Mat frame, List<DetectionResult> results)
        {
            if (frame == null || frame.Empty())
            {
                return null;
            }

            // 克隆帧绘制，不污染原始帧；返回后归调用方所有
            Mat drawFrame = frame.Clone();

            if (results != null)
            {
                var green = new Scalar(0, 255, 0);
                foreach (var det in results)
                {
                    var rect = new Rect(
                        (int)Math.Max(0, det.Left),
                        (int)Math.Max(0, det.Top),
                        (int)Math.Max(1, det.Width),
                        (int)Math.Max(1, det.Height));

                    Cv2.Rectangle(drawFrame, rect, green, 2);

                    string label = $"{det.ClassName} {det.Confidence:F2}";
                    Cv2.PutText(drawFrame, label,
                        new OpenCvSharp.Point((int)det.Left, (int)Math.Max(10, det.Top - 5)),
                        HersheyFonts.HersheySimplex, 0.6, green, 2);
                }
            }

            return drawFrame;
        }
    }
}
