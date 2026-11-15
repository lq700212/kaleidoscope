using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace YoloDetection
{
    /// <summary>
    /// YOLO 检测器抽象接口。
    /// 实现类负责模型加载与推理；检测结果的后处理由 IDetectionResultProcessor 负责。
    /// 注意：Detect 不是线程安全的，同一实例的 Detect 调用必须串行化（管道内部已保证）。
    /// </summary>
    public interface IYoloDetector : IDisposable
    {
        /// <summary>模型是否已初始化</summary>
        bool IsInitialized { get; }

        /// <summary>置信度阈值（0-1）</summary>
        float ConfidenceThreshold { get; set; }

        /// <summary>NMS 阈值（0-1）</summary>
        float NmsThreshold { get; set; }

        /// <summary>加载 ONNX 模型。模型路径不存在或加载失败时抛异常。</summary>
        void Initialize(string modelPath);

        /// <summary>对一帧图像执行检测，返回检测结果列表（永不返回 null）</summary>
        List<DetectionResult> Detect(Mat mat);
    }
}
