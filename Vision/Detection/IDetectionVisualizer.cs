using System.Collections.Generic;
using OpenCvSharp;

namespace YoloDetection
{
    /// <summary>
    /// 检测结果可视化抽象接口（策略模式）。
    /// 实现类负责把检测框绘制到帧上并返回新帧；不得修改传入的原始 frame。
    ///
    /// 所有权契约：返回的 Mat 归调用方所有（调用方负责 Dispose）；
    /// 返回 null 表示绘制失败，调用方应回退为直接显示原始帧。
    /// </summary>
    public interface IDetectionVisualizer
    {
        /// <summary>在帧上绘制检测框，返回绘制后的新 Mat</summary>
        Mat Draw(Mat frame, List<DetectionResult> results);
    }

    /// <summary>可视化器类型枚举</summary>
    public enum VisualizerType
    {
        /// <summary>GDI+ 绘制（红色框）</summary>
        YoloBuiltin,

        /// <summary>OpenCV 绘制（绿色框）</summary>
        OpenCV
    }
}
