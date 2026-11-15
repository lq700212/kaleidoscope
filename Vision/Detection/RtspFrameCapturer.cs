using System;
using System.Threading;
using OpenCvSharp;

namespace YoloDetection
{
    /// <summary>
    /// 基于 OpenCV VideoCapture 的 RTSP 帧捕获器（IFrameSource 默认实现）。
    ///
    /// 线程安全设计要点：
    ///   1. 停止标志为 volatile；_generation 代际号标识"第几条捕获链路"
    ///   2. VideoCapture 所有权归捕获线程私有：线程内创建、线程退出时释放，
    ///      外部（Stop 等）绝不触碰实例——彻底避免"线程还卡在 native Read 里
    ///      外部却去 Release"的崩溃竞态
    ///   3. Start 失败路径完整释放已创建的 VideoCapture，不留泄漏
    ///   4. 每一帧的处理路径均有 finally 兜底释放临时 Mat
    ///
    /// 断流自愈（v2.8，两层机制）：
    ///   RTSP over TCP 在网络闪断/相机重启时会形成"半开连接"——Read 内部
    ///   socket 无数据也无 RST，FFmpeg 默认永久阻塞，表现为预览画面冻结不恢复。
    ///
    ///   第 1 层【连续失败重连】：断流后 Read 返回 false（相机重启回 RST、路由回
    ///   FIN 等场景），连续失败达到阈值即按原地址重建流，画面自动恢复，零泄漏；
    ///
    ///   第 2 层【看门狗强杀重建】：真·静默半开（NAT 表项丢失/网线拔出等）时 Read
    ///   永久挂起，靠捕获线程心跳检测——看门狗发现心跳停跳即判定线程卡死，废弃整条
    ///   捕获链路（代际号 +1 让老线程复活后自灭）、用全新实例+线程顶上，画面恢复。
    ///   代价是泄漏一个卡死的后台线程与其 VideoCapture（频率极低，进程退出回收），
    ///   这是在"native 无法打断"约束下唯一安全的取舍。
    ///
    ///   为什么不做 FFmpeg 层超时：实测本机 opencv_videoio_ffmpeg4100_64.dll
    ///   ① 带 CAP_PROP_OPEN/READ_TIMEOUT params 打开会报 unsupported parameters
    ///   直接 Bailout（任何流包括本地文件都打不开）；② OPENCV_FFMPEG_CAPTURE_OPTIONS
    ///   环境变量经 OS 继承/.NET 进程级/UCRT _wputenv 三种方式注入均不生效。
    ///   native 侧无超时可依，只能应用层看门狗。
    ///
    /// 帧所有权契约：
    ///   FrameReady 传出的 Mat 归订阅者所有，订阅者用完必须 Dispose。
    /// </summary>
    public class RtspFrameCapturer : IFrameSource
    {
        // 保护 _url/_generation/_frameCount 等共享字段的轻量锁
        // （注意：VideoCapture.Read/Open 故意不在此锁内——它们归线程私有，见类头注释）
        private readonly object _stateLock = new object();

        // 看门狗判定线程卡死的空闲阈值（毫秒）。捕获线程每轮循环都会刷心跳
        // （含读取失败的退避分支），只有真正挂在 native Read 上心跳才会停；
        // 阈值需大于单次正常重连尝试的耗时（Open 失败通常数秒）。
        private const int HeartbeatStaleMs = 15000;

        // 看门狗巡检周期（毫秒）
        private const int WatchdogPeriodMs = 5000;

        // 连续读取失败达到该次数判定断流并触发重建（每次失败 sleep 50ms ≈ 1.5 秒无帧）。
        // 不能太小：正常网络抖动丢几帧不该触发昂贵的 Reopen；不能太大：断流恢复太慢。
        private const int ReconnectAfterFailures = 30;

        private Thread _captureThread;

        // 看门狗：定期检查捕获线程心跳，停跳即强制重建捕获链路
        private Timer _watchdog;

        private volatile bool _stopRequested;

        // 流地址：Start 时保存，断流自愈重建时复用（Start 写、捕获/看门狗线程读）
        private string _url;

        // 捕获链路代际号：看门狗强杀重建时 +1；老线程发现自己代际落后即退出，
        // 保证"复活的僵尸线程"不会与新线程并发读帧或重复发帧
        private int _generation;

        // 捕获线程心跳（Environment.TickCount，Interlocked 读写）。
        // 语义是"线程最后一次活着的时间"，不是最后一帧时间——读取失败的退避
        // 分支同样刷心跳，这样看门狗只会误杀真卡死的线程，不打断正常重连节奏。
        private int _heartbeatTick = Environment.TickCount;

        // 仅工作线程写、外部近似读的统计字段（int/long 赋值原子性足够）
        private long _frameCount;

        public bool IsRunning => !_stopRequested && _captureThread != null && _captureThread.IsAlive;

        public int FrameWidth { get; private set; }

        public int FrameHeight { get; private set; }

        public event EventHandler<Mat> FrameReady;

        /// <summary>
        /// 连接 RTSP 流并开始后台捕获线程。
        /// </summary>
        /// <param name="rtspUrl">RTSP 地址（含认证信息时格式 rtsp://user:pass@ip:port/...）</param>
        /// <returns>true=启动成功</returns>
        public bool Start(string rtspUrl)
        {
            if (string.IsNullOrEmpty(rtspUrl))
                throw new ArgumentException("RTSP地址不能为空", nameof(rtspUrl));

            if (IsRunning)
            {
                Stop();
            }

            // 打开放在捕获线程之前的调用线程上同步执行：保持"失败立即返回 false"
            // 的既有契约（UI 依赖它提示地址错误）；代价是撞上无响应地址时本调用
            // 可能阻塞较久（FFmpeg 内部握手/重试），与历史行为一致
            VideoCapture capture = TryCreateCapture(rtspUrl);
            if (capture == null)
            {
                return false;
            }

            int generation;
            lock (_stateLock)
            {
                _stopRequested = false;
                _frameCount = 0;
                _url = rtspUrl;
                _generation++;
                generation = _generation;
                Volatile.Write(ref _heartbeatTick, Environment.TickCount);
            }

            var thread = new Thread(() => CaptureLoop(capture, generation))
            {
                IsBackground = true,
                Name = "RtspCaptureLoop#" + generation
            };
            _captureThread = thread;
            thread.Start();

            StartWatchdog();

            return true;
        }

        public void Stop()
        {
            _stopRequested = true;

            StopWatchdog();

            Thread thread = _captureThread;
            if (thread != null && thread.IsAlive)
            {
                // 有界等待：健康线程一帧周期内退出；卡死线程（半开连接）Join 超时放弃，
                // 其 VideoCapture/线程成为已知泄漏（见类头注释），绝不在其可能仍在使用时释放
                thread.Join(TimeSpan.FromSeconds(6));
            }
            _captureThread = null;
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        // ==================== 看门狗 ====================

        private void StartWatchdog()
        {
            StopWatchdog();
            _watchdog = new Timer(WatchdogTick, null, WatchdogPeriodMs, WatchdogPeriodMs);
        }

        private void StopWatchdog()
        {
            Timer existing = _watchdog;
            _watchdog = null;
            if (existing != null)
            {
                existing.Dispose();
            }
        }

        /// <summary>
        /// 看门狗巡检：心跳停跳超过阈值即判定捕获线程卡死（静默半开连接），
        /// 废弃当前代际并用全新实例+线程重建。全部动作在锁内完成以保证与
        /// Stop/其他重建互斥；老线程若日后从 Read 中苏醒，会因代际不符自行退出
        /// 并释放自己持有的实例，绝不会与新链路并发发帧。
        /// </summary>
        private void WatchdogTick(object state)
        {
            if (_stopRequested || !IsRunning)
            {
                return;
            }

            int idleMs = unchecked(Environment.TickCount - Volatile.Read(ref _heartbeatTick));
            if (idleMs < HeartbeatStaleMs)
            {
                return;
            }

            VideoCapture fresh = null;
            try
            {
                // 创建耗时操作放在锁外（Open 可能耗时数十秒），完成后锁内核验代际再生效
                fresh = TryCreateCapture(_url);
                if (fresh == null)
                {
                    LogManager.GeneralLog("[Capture] 看门狗重建失败, 下轮巡检继续: " + _url);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogManager.GeneralLog("[Capture] 看门狗重建异常: " + ex.Message);
                if (fresh != null)
                {
                    try { fresh.Dispose(); } catch { }
                }
                return;
            }

            bool replaced = false;
            lock (_stateLock)
            {
                if (_stopRequested)
                {
                    // Stop 与重建竞态：外部已要求停止，新实例不能顶上去，
                    // 必须就地释放（否则泄漏一个 VideoCapture）。
                    // 旧实现此处直接 return 而忘记释放 fresh。
                    try { fresh.Dispose(); } catch { }
                    return;
                }

                // 代际 +1：老线程（哪怕仍在 native Read 里）失去"现役"身份，
                // 苏醒后会自行退出并释放自己持有的旧实例
                _generation++;
                int generation = _generation;
                Volatile.Write(ref _heartbeatTick, Environment.TickCount);
                _frameCount = 0;

                var thread = new Thread(() => CaptureLoop(fresh, generation))
                {
                    IsBackground = true,
                    Name = "RtspCaptureLoop#" + generation
                };
                _captureThread = thread;
                thread.Start();
                replaced = true;

                LogManager.GeneralLog(
                    $"[Capture] 捕获线程心跳停滞 {idleMs}ms, 判定卡死, 已强制重建捕获链路: " + _url);
            }

            if (!replaced)
            {
                try { fresh.Dispose(); } catch { }
            }
        }

        // ==================== 工作线程 ====================

        /// <summary>
        /// 捕获循环。<paramref name="capture"/> 归本线程私有：退出路径（正常停止/
        /// 代际更替/异常兜底）负责释放；外部永不触碰，杜绝"线程在 native Read 中
        /// 实例被外部释放"的崩溃。
        /// </summary>
        private void CaptureLoop(VideoCapture capture, int myGeneration)
        {
            int consecutiveFailures = 0;

            while (!_stopRequested && myGeneration == Volatile.Read(ref _generation))
            {
                // 心跳：每轮循环开头刷新（含下方失败退避分支），
                // 只有整个循环体挂在 native Read 上心跳才会停
                Volatile.Write(ref _heartbeatTick, Environment.TickCount);

                Mat frame = new Mat();
                Mat bgrFrame = null;

                try
                {
                    bool readOk = capture.Read(frame);

                    if (!_stopRequested && myGeneration != Volatile.Read(ref _generation))
                    {
                        break; // 已被新一代顶替：立即交棒，不再使用任何资源
                    }

                    if (!readOk)
                    {
                        // 连续失败计数：小抖动只做短退避；达到阈值判定断流，按原地址重建
                        consecutiveFailures++;
                        if (consecutiveFailures >= ReconnectAfterFailures)
                        {
                            VideoCapture fresh = TryCreateCapture(_url);
                            if (fresh != null &&
                                !_stopRequested &&
                                myGeneration == Volatile.Read(ref _generation))
                            {
                                try { capture.Release(); } catch { }
                                capture.Dispose();
                                capture = fresh;
                                // _frameCount 归零后由第一帧自动修正分辨率（Open 属性可能不准）
                                _frameCount = 0;
                                consecutiveFailures = 0;
                                LogManager.GeneralLog("[Capture] 流断开后自动重连成功: " + _url);
                            }
                            else
                            {
                                // 重建失败或期间已停止/换代：就地丢弃新实例，继续退避
                                if (fresh != null)
                                {
                                    try { fresh.Dispose(); } catch { }
                                }
                                Thread.Sleep(200);
                            }
                        }
                        else
                        {
                            Thread.Sleep(50); // 流暂时无数据（网络抖动），稍后重试
                        }
                        continue;
                    }

                    consecutiveFailures = 0;

                    if (frame.Empty())
                    {
                        continue;
                    }

                    bgrFrame = ConvertToBgr(frame);
                    _frameCount++;

                    if (_frameCount == 1)
                    {
                        // 第一帧时修正实际分辨率（Open 属性可能不准）
                        RefreshFrameSize(bgrFrame);
                    }

                    // 克隆后发布；所有权移交给订阅者
                    Mat copy = bgrFrame.Clone();
                    var handler = FrameReady;
                    if (handler != null)
                    {
                        handler(this, copy);
                    }
                    else
                    {
                        copy.Dispose(); // 无订阅者时立即回收
                    }

                    if (_frameCount % 300 == 0)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Capture] 已捕获 {_frameCount} 帧, 尺寸: {FrameWidth}x{FrameHeight}");
                    }

                    // 轻微让步，避免空转过占 CPU
                    Thread.Sleep(1);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Capture] 捕获异常: {ex.Message}");
                    Thread.Sleep(100);
                }
                finally
                {
                    // ConvertToBgr 在已是 BGR8 时返回的是 frame 本身，避免重复释放
                    if (bgrFrame != null && !ReferenceEquals(bgrFrame, frame))
                    {
                        bgrFrame.Dispose();
                    }
                    frame.Dispose();
                }
            }

            // 线程私有实例的最终释放点：无论正常停止还是代际更替，都由最后使用者清理
            try { capture.Release(); } catch { }
            try { capture.Dispose(); } catch { }
        }

        /// <summary>用实际帧数据修正对外暴露的分辨率属性。</summary>
        private void RefreshFrameSize(Mat sample)
        {
            if (sample.Cols > 0 && sample.Rows > 0)
            {
                FrameWidth = sample.Cols;
                FrameHeight = sample.Rows;
            }
            else
            {
                lock (_stateLock)
                {
                    if (FrameWidth == 0)
                    {
                        FrameWidth = 1920;
                        FrameHeight = 1080;
                    }
                }
            }
        }

        // ==================== 内部辅助 ====================

        /// <summary>
        /// 创建并打开捕获（构造即打开）。
        /// 注意：不能给 VideoCapture 传 params 参数（如超时属性）——实测本机
        /// opencv_videoio_ffmpeg4100_64.dll 对任何 params 都报
        /// "unsupported parameters in .open(), Bailout" 直接拒绝打开（连 BUFFER_SIZE
        /// 都不行），FFmpeg 层超时在本构建上不可用（详见类头注释）。
        /// </summary>
        private static VideoCapture TryCreateCapture(string url)
        {
            try
            {
                var capture = new VideoCapture();
                if (capture.Open(url))
                {
                    // OpenCV 层内部缓冲设为 1 帧：只保留最新画面，降低延迟
                    // （FFmpeg 后端对该属性可能忽略，属尽力而为；防积压主保障在管道单槽位）
                    capture.Set(VideoCaptureProperties.BufferSize, 1);
                    return capture;
                }
                capture.Dispose();
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将任意格式帧统一转换为 BGR8（YOLO 推理输入格式）。
        /// 返回值可能是 src 本身（已是 BGR8）或新 Mat（调用方负责 Dispose）。
        /// </summary>
        private static Mat ConvertToBgr(Mat src)
        {
            if (src.Type() == MatType.CV_8UC3)
            {
                return src;
            }

            Mat bgr = new Mat();
            int channels = src.Channels();

            if (channels == 1)
            {
                Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
            }
            else if (channels == 4)
            {
                Cv2.CvtColor(src, bgr, ColorConversionCodes.RGBA2BGR);
            }
            else
            {
                src.ConvertTo(bgr, MatType.CV_8UC3);
            }

            return bgr;
        }
    }
}
