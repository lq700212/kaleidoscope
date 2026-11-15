using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace YoloDetection
{
    /// <summary>
    /// 人体姿态检测器抽象接口：输入整帧 + 已检出的人体框列表，
    /// 输出每个人体框对应的关键点（COCO 17 点）。
    ///
    /// 设计动机（为什么吃"人体框"而不是自己找人）：
    ///   上游 YOLO 检测器已经把 person 找好了，姿态检测只需对每个框裁剪小图推理，
    ///   既省算力（不用全图跑大分辨率），又天然把关键点归属到具体的人。
    ///
    /// 线程契约：Detect 不是线程安全的，同一实例必须串行调用
    /// （与 IYoloDetector 相同，管道内部已在单一检测线程上保证）。
    /// </summary>
    public interface IPoseDetector : IDisposable
    {
        /// <summary>模型是否已初始化</summary>
        bool IsInitialized { get; }

        /// <summary>人体框置信度阈值（0-1）：低于此值的候选直接丢弃</summary>
        float PersonConfidenceThreshold { get; set; }

        /// <summary>
        /// 加载 ONNX 姿态模型（如 yolo11n-pose.onnx）。失败时抛异常。
        /// </summary>
        void Initialize(string modelPath);

        /// <summary>
        /// 对整帧执行姿态推理。返回列表与 persons 一一对应（顺序一致、数量一致）；
        /// 某个人未检出有效关键点时，对应项的 Keypoints 为空集合。永不返回 null。
        /// </summary>
        /// <param name="frame">整帧图像（BGR）。方法内只读，不释放、不修改。</param>
        /// <param name="persons">上游检测结果中的人体框列表（须为原图像素坐标系）</param>
        List<PoseResult> Detect(Mat frame, List<DetectionResult> persons);
    }
}
