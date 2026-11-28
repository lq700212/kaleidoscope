using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Kaleidoscope.UI.Views;

/// <summary>
/// KxFramePixelFormat —— 帧像素格式（<see cref="KxFrameSurface"/> 的输入口径）。
///
/// 【为什么单独定义枚举而不直接暴露 WPF PixelFormat】
///   宿主推帧的源格式来自各自体系（GDI+ Bitmap / SkiaSharp / OpenCV），枚举只保留
///   相机预览真正用到的三种，把"源格式 → WPF 格式"的映射收口在本库一处，宿主
///   不用再记 WPF 的 PixelFormats 名字。
///
/// 【字节序红线（血泪，勿改）】GDI+ 与 Skia 的内存布局都是小端 B,G,R(,A)：
///   · GDI Format24bppRgb  → 内存 B,G,R      → WPF <see cref="Bgr24"/>
///   · GDI Format32bppArgb → 内存 B,G,R,A    → WPF <see cref="Bgra32"/>（直通 alpha）
///   · GDI Format32bppPArgb→ 内存 预乘 B,G,R,A → WPF <see cref="Pbgra32"/>
///   把"直通 alpha"错配成 Pbgra32（预乘）会让半透明像素叠底爆白（AIris V0.1.5 实测：
///   Mock 透明帧零件区全糊成白）；反过来预乘错配直通则透明区域发灰。
/// </summary>
public enum KxFramePixelFormat
{
    /// <summary>24bpp BGR（GDI Format24bppRgb / OpenCV BGR / Skia Bgr888）。相机裸流最常用。</summary>
    Bgr24 = 0,

    /// <summary>32bpp BGRA 直通 alpha（GDI Format32bppArgb / Skia Bgra8888）。</summary>
    Bgra32 = 1,

    /// <summary>32bpp BGRA 预乘 alpha（GDI Format32bppPArgb / WPF 内部合成格式）。</summary>
    Pbgra32 = 2,
}

/// <summary>
/// KxFrameSurface —— 高频相机帧呈现面（复用 WriteableBitmap + 丢帧背压 + 合并调度）。
///
/// 【干什么】把"采集线程的高频帧"安全、低开销地送到 WPF 画面：
///   ① 复用同一个 <see cref="WriteableBitmap"/>（分辨率/格式不变时零重建、零新对象）；
///   ② 后台线程推帧时只做一次整块拷贝到内部缓冲，像素写入封送到 UI 线程；
///   ③ 天然丢帧背压：UI 来不及渲染时新帧直接覆盖旧帧，Dispatcher 队列永不堆积
///      （这是"越跑越卡、延迟越拖越大"的根因——旧写法每帧 BeginInvoke 一个闭包，
///      渲染慢于采集时任务在队列里无限积压）；
///   ④ 可选 <see cref="MaxFps"/> 限流（相机 30fps、界面只想 15fps 的场景）。
///
/// 【为什么】各宿主此前各自手写"每帧 new BitmapSource"或"自管 WriteableBitmap"，
///   踩的坑一模一样（AIris V0.1.x / YoloDetector v2.x 双份实现）：
///   · <c>BitmapSource.Create</c> 每帧新建 → 1080P 每帧 ~8MB、5MP 相机每帧 ~20MB
///     大对象堆分配，GC 频繁 + LOH 碎片 → 画面周期性卡顿；
///   · <c>Imaging.ToBitmapSource</c> 先用 LockBits 请求 32bppArgb 读 24bpp 源，
///     GDI+ 内部做整帧格式转换（5MP 实测 20~40ms/帧），再拷一次进 BitmapSource；
///   · 每帧新 BitmapSource 还会让 Image 重新上传纹理，软件渲染下叠加整窗重绘。
///   本类把这条链路收敛为"源格式直拷 + 复用位图 + 丢帧"：5MP 24bpp 帧实测约 1~3ms
///   （一次 15MB 内存拷贝），分配量趋近于零。
///
/// 【怎么接（MVVM 绑定式）】
/// <code>
///   // VM（UI 线程构造）：
///   private readonly KxFrameSurface _surface = new KxFrameSurface();
///   public KxFrameSurface Surface => _surface;              // 可选：暴露给 View/诊断
///   public BitmapSource FrameImage { get; private set; }     // KxImageStage.FrameSource 绑定源
///
///   // 构造里订阅"位图重建"（首帧/分辨率/格式变化时触发，UI 线程）：
///   _surface.SurfaceChanged += bmp => FrameImage = bmp;      // bmp 为 null = Clear() 回空态
///
///   // 帧线程（任意线程）：
///   _surface.PushFrame(bmpData.Scan0, bmp.Width, bmp.Height, bmpData.Stride,
///                      KxFramePixelFormat.Bgr24);
/// </code>
///   XAML 侧仍是 <c>&lt;ui:KxImageStage FrameSource="{Binding FrameImage}"/&gt;</c>——
///   首帧后 FrameSource 指向复用的 WriteableBitmap，后续原地 WritePixels，WPF 自动刷新，
///   宿主不要每帧重设 FrameSource（重设只会多触发一轮绑定/空态切换）。
///
/// 【怎么接（命令式）】宿主已有 <c>Image</c> 时：<c>img.Source = surface.Bitmap;</c>
///   在 SurfaceChanged 里赋值一次即可。
///
/// 【线程契约】
///   · 构造必须在 UI 线程（内部捕获 <see cref="Dispatcher.CurrentDispatcher"/>）；
///   · <see cref="PushFrame(IntPtr,int,int,int,KxFramePixelFormat)"/> 任意线程可调，
///     调用返回时源像素已被拷走，调用方可以立即 UnlockBits/Dispose 源位图；
///   · <see cref="SurfaceChanged"/> 在 UI 线程触发；<see cref="Bitmap"/> 仅 UI 线程读。
///
/// 【性能护栏】
///   · 内部最多保留 2~3 块复用缓冲（推帧写一块、UI 渲染用一块），不会随帧数增长；
///   · 渲染调度用 <see cref="DispatcherPriority.Render"/>：输入/绑定优先，画面帧可让路；
///   · 分层透明窗（AllowsTransparency=True）= WPF 软件渲染，缩放/合成仍走 CPU——
///     本类只消除"每帧分配 + 重复格式转换"，窗口级开销另见宿主渲染策略。
/// </summary>
public sealed class KxFrameSurface : IDisposable
{
    /// <summary>UI 线程 Dispatcher（构造时捕获；所有 WritePixels 封送基准）。</summary>
    private readonly Dispatcher _dispatcher;

    /// <summary>推帧/渲染共享锁（保护缓冲交换与待渲染状态，临界区只做引用交换）。</summary>
    private readonly object _sync = new object();

    /// <summary>下一帧的写入目标（后台线程 PushFrame 写这里；也是"待渲染帧"载体）。</summary>
    private byte[]? _writeBuf;

    /// <summary>UI 渲染完归还的空闲缓冲（下次 PushFrame 复用，避免反复分配）。</summary>
    private byte[]? _spareBuf;

    private int _pendingW;
    private int _pendingH;
    private int _pendingStride;
    private KxFramePixelFormat _pendingFormat;

    /// <summary>是否有已提交但尚未渲染的帧。</summary>
    private bool _hasPending;

    /// <summary>是否已向 Dispatcher 投递渲染任务（合并多次推帧为一次渲染的关键）。</summary>
    private bool _renderPosted;

    private WriteableBitmap? _bitmap;
    private int _bitmapW;
    private int _bitmapH;
    private KxFramePixelFormat _bitmapFormat;

    /// <summary>
    /// 下次允许接受帧的 Stopwatch 时间戳（MaxFps 时间片累加用；0 = 尚未初始化）。
    ///
    /// 【为什么是"下次"而不是"上次"（2026-11-28 修正）】
    /// 旧算法「距上次接受 ≥ 1000/MaxFps 才收」在固定帧间隔输入下会系统性少收：
    /// 20fps 输入（帧间隔 50ms）限流 12fps 时判据是 83.3ms → 50ms 被拒、100ms 才收
    /// → 实际只有 10fps，且节拍 100ms 一停一顿（AIris 实测信息行 9fps，
    /// 排查时被误当成解码积压）。现在每接受一帧就 next += 1000/MaxFps，
    /// 20fps 输入限流 12fps = 每 5 帧收 3 帧，长期平均精确 12fps。
    /// </summary>
    private long _nextAcceptTimestamp;

    private long _pushedFrames;
    private long _droppedFrames;
    private bool _disposed;

    /// <summary>创建呈现面。<b>必须在 UI 线程调用</b>（WriteableBitmap 有线程亲和性）。</summary>
    public KxFrameSurface()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// 当前呈现位图（首帧到达前为 null）。宿主把它赋给 <c>Image.Source</c> /
    /// <c>KxImageStage.FrameSource</c> 后，后续帧原地更新、无需重新赋值。
    /// 仅 UI 线程读取（位图重建在 UI 线程完成）。
    /// </summary>
    public WriteableBitmap? Bitmap
    {
        get { lock (_sync) { return _bitmap; } }
    }

    /// <summary>
    /// 呈现位图重建事件（首帧、分辨率变化、像素格式变化时触发一次；<see cref="Clear"/>
    /// 时以 null 触发）。UI 线程触发，宿主在此把新位图挂到 Image/KxImageStage。
    /// </summary>
    public event Action<WriteableBitmap?>? SurfaceChanged;

    /// <summary>
    /// 最大接受帧率（0 = 不限，默认 0）。超过该速率的帧在入口直接丢弃（计入
    /// <see cref="DroppedFrames"/>），用于"相机 30fps、界面 15fps 足够"的降载场景。
    /// 可运行期修改（int 读写原子，无需锁）。
    /// </summary>
    public int MaxFps { get; set; }

    /// <summary>
    /// 推帧下采样上限（像素宽度，0 = 不限，默认 0）。
    ///
    /// 用途：5MP 相机（2592×1944）直接推全尺寸位图时，WPF 每帧要把 5MP 上传并缩放到
    /// 显示区——软件渲染环境下实测占单核约 80%（AIris 2026-11-28：监控台 128.8% vs
    /// 切页 49.1%）。设 1600 后库内按整数倍 box filter 下采样（2592→1296×972，1.3MP），
    /// UI 只渲染小图，1450px 宽显示区内肉眼无差。
    ///
    /// 重要：只影响本呈现面（预览显示），检测链路必须用宿主自己的全尺寸帧，
    /// 不要拿本面的位图做推理。
    ///
    /// 取值建议 = 显示区最大宽度 × 1.1（1920 窗口设 1600~2000）；0 保旧行为。
    /// 下采样在推帧线程完成（5MP 约 2~4ms/帧，见 csproj AllowUnsafeBlocks 注释），
    /// 不改渲染线程契约；可运行期修改（int 读写原子）。
    /// </summary>
    public int MaxPixelWidth { get; set; }

    /// <summary>已接受（进入渲染管线）的帧数（诊断用，可跨线程读）。</summary>
    public long PushedFrames { get { return System.Threading.Interlocked.Read(ref _pushedFrames); } }

    /// <summary>
    /// 被丢弃的帧数（诊断用）：含 MaxFps 限流丢弃 + UI 未渲染完被新帧覆盖的帧。
    /// 该值持续快速增长说明界面渲染跟不上采集，属正常背压（画面保持最新，延迟不累积）。
    /// </summary>
    public long DroppedFrames { get { return System.Threading.Interlocked.Read(ref _droppedFrames); } }

    /// <summary>
    /// 推入一帧（指针版，任意线程）。<b>调用返回时像素已拷入内部缓冲</b>，
    /// 调用方可立即 UnlockBits/Dispose 源位图，无需等待 UI 渲染。
    /// </summary>
    /// <param name="scan0">首行像素地址（GDI BitmapData.Scan0 / SKBitmap.GetPixels()）。</param>
    /// <param name="width">帧宽（像素）。</param>
    /// <param name="height">帧高（像素）。</param>
    /// <param name="stride">源行跨距（字节）。负值 = bottom-up（GDI 罕见但合法），内部逐行翻转。</param>
    /// <param name="format">源像素格式（必须与内存实际布局一致，见枚举注释）。</param>
    /// <returns>true = 已接受；false = 参数非法/已释放/被 MaxFps 限流丢弃。</returns>
    public bool PushFrame(IntPtr scan0, int width, int height, int stride, KxFramePixelFormat format)
    {
        if (scan0 == IntPtr.Zero) return false;
        return PushCore(scan0, null, width, height, stride, format);
    }

    /// <summary>
    /// 推入一帧（托管缓冲版，任意线程）。用于宿主已持有 byte[] 像素块的场景
    /// （如 SkiaSharp 解码结果、网络流缓冲）。<paramref name="source"/> 长度必须
    /// ≥ |stride| × height，否则返回 false（不做部分拷贝）。
    /// </summary>
    public bool PushFrame(byte[] source, int width, int height, int stride, KxFramePixelFormat format)
    {
        if (source == null) return false;
        return PushCore(IntPtr.Zero, source, width, height, stride, format);
    }

    /// <summary>
    /// 清空画面回空态：丢弃待渲染帧并释放当前位图，以 <c>SurfaceChanged(null)</c>
    /// 通知宿主（KxImageStage 绑定式会自动回"等待画面…"空态）。任意线程可调。
    /// </summary>
    public void Clear()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(Clear), DispatcherPriority.Normal);
            return;
        }

        lock (_sync)
        {
            _hasPending = false;
            _writeBuf = _spareBuf;
            _spareBuf = null;
            _bitmap = null;
            _bitmapW = 0;
            _bitmapH = 0;
            _bitmapFormat = KxFramePixelFormat.Bgr24;
        }

        var handler = SurfaceChanged;
        if (handler != null) handler(null);
    }

    /// <summary>释放（幂等）：清事件、丢缓冲；之后 PushFrame 一律返回 false。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _hasPending = false;
            _writeBuf = null;
            _spareBuf = null;
            _bitmap = null;
        }
        SurfaceChanged = null;
    }

    // ============ 内部实现 ============

    /// <summary>
    /// 推帧核心：限流 → 拷贝进复用缓冲 → 交换待渲染标记 → 按需投递一次渲染任务。
    /// 锁内只做内存拷贝与引用交换（不碰 WPF 对象），UI 线程渲染在锁外进行。
    /// </summary>
    private bool PushCore(IntPtr scan0, byte[]? source, int width, int height, int stride, KxFramePixelFormat format)
    {
        if (width <= 0 || height <= 0) return false;

        int bpp = BytesPerPixel(format);
        int absStride = stride < 0 ? -stride : stride;
        if (absStride < width * bpp) return false;      // 行跨距不足一行的字节数 = 参数错

        // 源缓冲长度按<b>源尺寸</b>校验（下采样时输出更小，不能用输出尺寸校验）
        long srcBytesLong = (long)absStride * height;
        if (srcBytesLong > int.MaxValue) return false;  // 防御：单帧 > 2GB 不是预览帧
        int srcBytes = (int)srcBytesLong;
        if (source != null && source.Length < srcBytes) return false;

        // 下采样倍率：只做整数倍（2/3/4…）box filter——任意比例插值在托管侧没有
        // 质量/性能都划算的实现，整数倍平均对预览足够且 SIMD 友好。
        int scale = 1;
        int maxW = MaxPixelWidth;
        if (maxW > 0 && width > maxW) scale = (width + maxW - 1) / maxW;
        if (scale > 1 && (width / scale <= 0 || height / scale <= 0)) scale = 1;

        int outW = width / scale;
        int outH = height / scale;
        int outStride = outW * bpp;
        int outBytes = outStride * outH;
        bool bottomUp = stride < 0;

        bool post = false;
        lock (_sync)
        {
            if (_disposed) return false;

            // MaxFps 限流（Stopwatch 单调时钟，不受系统时间调整影响）。
            // 时间片累加算法：接受一帧就把"下次允许时刻"+= 1000/MaxFps，
            // 而不是"距上次 ≥ 周期才收"——后者在固定帧间隔输入下会系统性少收
            // （详见 _nextAcceptTimestamp 字段注释）。本方法已持 _sync 锁，无需原子操作。
            int maxFps = MaxFps;
            if (maxFps > 0)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                long intervalTicks = Stopwatch.Frequency / maxFps;
                if (intervalTicks < 1) intervalTicks = 1;

                if (_nextAcceptTimestamp == 0)
                {
                    _nextAcceptTimestamp = nowTicks;
                }

                if (nowTicks < _nextAcceptTimestamp)
                {
                    System.Threading.Interlocked.Increment(ref _droppedFrames);
                    return false;
                }

                _nextAcceptTimestamp += intervalTicks;
                // 断流恢复/长时间无帧时 next 会落后很多，重置防止瞬间连收一串
                if (_nextAcceptTimestamp < nowTicks - intervalTicks * 2)
                {
                    _nextAcceptTimestamp = nowTicks + intervalTicks;
                }
            }

            // 写入目标：优先复用已归还的空闲缓冲；被 UI 占着时新建（最多 2~3 块，不随帧数增长）
            byte[] buf = _writeBuf ?? _spareBuf ?? new byte[outBytes];
            if (buf.Length < outBytes) buf = new byte[outBytes];
            _writeBuf = buf;
            if (ReferenceEquals(_spareBuf, buf)) _spareBuf = null;

            // 上一帧还没被 UI 取走 → 本帧覆盖它（丢帧背压，画面始终是最新帧）
            if (_hasPending) System.Threading.Interlocked.Increment(ref _droppedFrames);

            if (scale > 1)
            {
                // 下采样直接读源指针/数组 → 写小缓冲：省掉全尺寸中间拷贝（5MP 15MB）
                if (source != null)
                {
                    Downsample(source, width, height, absStride, bpp, scale, outW, outH, outStride, buf, bottomUp);
                }
                else
                {
                    Downsample(scan0, width, height, absStride, bpp, scale, outW, outH, outStride, buf, bottomUp);
                }
            }
            else if (source != null)
            {
                Buffer.BlockCopy(source, 0, buf, 0, outBytes);
            }
            else if (stride > 0)
            {
                // 常规 top-down：整块一次拷贝（5MP/15MB 实测 1~2ms）
                Marshal.Copy(scan0, buf, 0, outBytes);
            }
            else
            {
                // 负 stride = bottom-up：源首行是图像最后一行，逐行倒序拷成 top-down。
                // GDI+ LockBits 实测基本都返回正 stride，此分支只做契约兜底。
                for (int y = 0; y < height; y++)
                {
                    IntPtr srcRow = IntPtr.Add(scan0, (height - 1 - y) * absStride);
                    Marshal.Copy(srcRow, buf, y * absStride, absStride);
                }
            }

            _pendingW = outW;
            _pendingH = outH;
            _pendingStride = outStride;
            _pendingFormat = format;
            _hasPending = true;

            // 合并调度：已有渲染任务在途就不再投递（多次推帧 → 一次渲染，队列零堆积）
            if (!_renderPosted)
            {
                _renderPosted = true;
                post = true;
            }
        }

        System.Threading.Interlocked.Increment(ref _pushedFrames);

        if (post)
        {
            // Render 优先级低于输入/绑定：界面交互优先，视频帧可让路（丢帧由背压兜底）
            _dispatcher.BeginInvoke(new Action(RenderPending), DispatcherPriority.Render);
        }
        return true;
    }

    // ============ 下采样实现（unsafe，仅本文件内部） ============

    /// <summary>
    /// 整数倍 box filter 下采样（源指针版）：每 scale×scale 像素块取通道平均，
    /// 写入 <paramref name="dst"/>（outW×outH，行跨距 outStride）。
    /// bottomUp=true 时源首行是图像末行（GDI 负 stride），按倒序取行。
    /// </summary>
    private static unsafe void Downsample(IntPtr scan0, int width, int height, int srcStride,
        int bpp, int scale, int outW, int outH, int outStride, byte[] dst, bool bottomUp)
    {
        byte* src = (byte*)scan0;
        fixed (byte* d0 = dst)
        {
            DownsampleCore(src, width, height, srcStride, bpp, scale, outW, outH, outStride, d0, bottomUp);
        }
    }

    /// <summary>整数倍 box filter 下采样（托管数组源版，语义同指针版）。</summary>
    private static unsafe void Downsample(byte[] source, int width, int height, int srcStride,
        int bpp, int scale, int outW, int outH, int outStride, byte[] dst, bool bottomUp)
    {
        fixed (byte* s0 = source)
        fixed (byte* d0 = dst)
        {
            DownsampleCore(s0, width, height, srcStride, bpp, scale, outW, outH, outStride, d0, bottomUp);
        }
    }

    /// <summary>
    /// 下采样核心：按通道数特化循环（3 通道 BGR / 4 通道 BGRA），
    /// 内层不做 bpp 分支——5MP→1.3MP 实测 2~4ms，若混写 4 通道判断会退化到 8~12ms。
    /// </summary>
    private static unsafe void DownsampleCore(byte* src, int width, int height, int srcStride,
        int bpp, int scale, int outW, int outH, int outStride, byte* dst, bool bottomUp)
    {
        int n = scale * scale;
        if (bpp == 3)
        {
            for (int y = 0; y < outH; y++)
            {
                byte* d = dst + y * outStride;
                for (int x = 0; x < outW; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0;
                    for (int sy = 0; sy < scale; sy++)
                    {
                        int srcY = y * scale + sy;
                        if (bottomUp) srcY = height - 1 - srcY;
                        byte* row = src + srcY * srcStride + x * scale * 3;
                        for (int sx = 0; sx < scale; sx++)
                        {
                            sumB += row[0];
                            sumG += row[1];
                            sumR += row[2];
                            row += 3;
                        }
                    }
                    d[0] = (byte)(sumB / n);
                    d[1] = (byte)(sumG / n);
                    d[2] = (byte)(sumR / n);
                    d += 3;
                }
            }
        }
        else
        {
            for (int y = 0; y < outH; y++)
            {
                byte* d = dst + y * outStride;
                for (int x = 0; x < outW; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    for (int sy = 0; sy < scale; sy++)
                    {
                        int srcY = y * scale + sy;
                        if (bottomUp) srcY = height - 1 - srcY;
                        byte* row = src + srcY * srcStride + x * scale * 4;
                        for (int sx = 0; sx < scale; sx++)
                        {
                            sumB += row[0];
                            sumG += row[1];
                            sumR += row[2];
                            sumA += row[3];
                            row += 4;
                        }
                    }
                    d[0] = (byte)(sumB / n);
                    d[1] = (byte)(sumG / n);
                    d[2] = (byte)(sumR / n);
                    d[3] = (byte)(sumA / n);
                    d += 4;
                }
            }
        }
    }

    /// <summary>
    /// UI 线程渲染：取走待渲染缓冲 → 确保位图规格 → 整块 WritePixels → 归还缓冲。
    /// 位图重建（首帧/换分辨率）时触发 SurfaceChanged，宿主据此挂载新位图。
    /// </summary>
    private void RenderPending()
    {
        byte[]? buf;
        int w, h, stride;
        KxFramePixelFormat format;

        lock (_sync)
        {
            _renderPosted = false;
            if (_disposed || !_hasPending || _writeBuf == null) return;

            buf = _writeBuf;
            w = _pendingW;
            h = _pendingH;
            stride = _pendingStride;
            format = _pendingFormat;
            _hasPending = false;

            // 待渲染帧已被取走：把空闲缓冲（若已归还）作为下一个写入目标；
            // 未归还时置 null，下一次 PushFrame 会新建一块（最坏 3 块缓冲封顶）
            _writeBuf = _spareBuf;
            _spareBuf = null;
        }

        try
        {
            WriteableBitmap bmp = EnsureBitmap(w, h, format);
            bmp.WritePixels(new Int32Rect(0, 0, w, h), buf, stride, 0);
        }
        catch
        {
            // 渲染失败静默：显示链路绝不能反过来把采集/检测主链路带崩
        }
        finally
        {
            lock (_sync)
            {
                if (!_disposed && _spareBuf == null) _spareBuf = buf;
            }
        }
    }

    /// <summary>
    /// 确保呈现位图与当前帧规格匹配：规格不变直接复用（零分配）；变化则新建并
    /// 触发 <see cref="SurfaceChanged"/>（UI 线程，宿主在此重挂 Image.Source）。
    /// </summary>
    private WriteableBitmap EnsureBitmap(int width, int height, KxFramePixelFormat format)
    {
        WriteableBitmap? bmp = _bitmap;
        if (bmp != null && _bitmapW == width && _bitmapH == height && _bitmapFormat == format)
        {
            return bmp;
        }

        // DPI 固定 96：像素尺寸即显示逻辑尺寸，缩放交给 Image.Stretch=Uniform
        // （letterbox 等比缩放，与 WinForms PictureBox.SizeMode=Zoom 语义一致）
        bmp = new WriteableBitmap(width, height, 96, 96, ToPixelFormat(format), null);
        _bitmap = bmp;
        _bitmapW = width;
        _bitmapH = height;
        _bitmapFormat = format;

        var handler = SurfaceChanged;
        if (handler != null) handler(bmp);
        return bmp;
    }

    /// <summary>格式枚举 → WPF 像素格式（对应关系见 <see cref="KxFramePixelFormat"/> 注释）。</summary>
    private static PixelFormat ToPixelFormat(KxFramePixelFormat format)
    {
        switch (format)
        {
            case KxFramePixelFormat.Bgr24: return PixelFormats.Bgr24;
            case KxFramePixelFormat.Pbgra32: return PixelFormats.Pbgra32;
            default: return PixelFormats.Bgra32;
        }
    }

    /// <summary>单像素字节数（行跨距校验用）。</summary>
    private static int BytesPerPixel(KxFramePixelFormat format)
    {
        return format == KxFramePixelFormat.Bgr24 ? 3 : 4;
    }
}
