using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace YoloDetection
{
    /// <summary>
    /// 检测管道抽象接口：整合帧缓冲、推理、后处理与可视化。
    ///
    /// 线程模型：
    ///   - ProcessFrame 可在任意线程调用（内部克隆帧，调用方随后可立即释放原帧）
    ///   - 事件在内部检测线程上触发；DetectionsUpdated 传出的列表是快照，
    ///     FrameProcessed 传出的 Mat 归订阅者所有（用完必须 Dispose）
    /// </summary>
    public interface IDetectionPipeline : IDisposable
    {
        /// <summary>管道是否正在运行</summary>
        bool IsRunning { get; }

        float ConfidenceThreshold { get; set; }

        float NmsThreshold { get; set; }

        /// <summary>检测结果更新事件（参数为结果快照，外部可自由持有）</summary>
        event EventHandler<List<DetectionResult>> DetectionsUpdated;

        /// <summary>帧处理完成事件（参数 Mat 已绘制检测框，归订阅者所有）</summary>
        event EventHandler<Mat> FrameProcessed;

        /// <summary>启动检测线程。检测器必须已 Initialize，否则抛 InvalidOperationException。</summary>
        void Start();

        /// <summary>停止检测线程并等待其退出（有界等待，绝不永久阻塞）</summary>
        void Stop();

        /// <summary>提交一帧进行检测。帧会被克隆，原始 frame 可立即释放。</summary>
        void ProcessFrame(Mat frame);

        /// <summary>获取最近一次检测结果（返回副本）</summary>
        List<DetectionResult> GetLatestDetections();
    }
}
