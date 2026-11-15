using System;
using OpenCvSharp;

namespace YoloDetection
{
    /// <summary>
    /// 视频帧源抽象接口。
    /// 实现类负责从具体介质（RTSP 流 / USB 摄像头 / 本地文件等）获取视频帧。
    ///
    /// 帧所有权契约（重要）：
    ///   FrameReady 事件传出的 Mat 归订阅者所有，订阅者使用完毕后必须调用 frame.Dispose()。
    ///   事件在后台捕获线程上同步触发，订阅者应尽快处理（内部克隆）而不要长时间持有。
    /// </summary>
    public interface IFrameSource : IDisposable
    {
        /// <summary>帧源是否正在运行</summary>
        bool IsRunning { get; }

        /// <summary>帧宽度（Start 成功后有效）</summary>
        int FrameWidth { get; }

        /// <summary>帧高度（Start 成功后有效）</summary>
        int FrameHeight { get; }

        /// <summary>新帧就绪事件（Mat 归订阅者所有）</summary>
        event EventHandler<Mat> FrameReady;

        /// <summary>连接到帧源并开始捕获。返回 false 表示启动失败。</summary>
        bool Start(string sourceUrl);

        /// <summary>停止捕获并释放底层资源</summary>
        void Stop();
    }
}
