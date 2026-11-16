using System;

namespace Kaleidoscope.Cameras
{
    /// <summary>帧像素语义（库中性枚举；宿主按需映射到自家图像类型）。</summary>
    public enum KxCamFrameKind
    {
        /// <summary>8 位灰度（w*h 线性 buffer，Pixels8 载体）。</summary>
        Mono8,
        /// <summary>16 位高度/深度（Depth=Pixels16 短整载体；Profile=RawBytes 字节载体，小端直拷语义）。</summary>
        Range16,
    }

    /// <summary>
    /// 相机中性帧 DTO——采集层与下游图像库（VisionPro/Halcon/OpenCV…）之间的解耦货币。
    ///
    /// 【干什么】三品牌适配器回调里产出的统一帧：宽高 + 规整线性像素缓冲 + 尺度元数据。
    /// 库内零下游图像库类型（VisionPro 的 ICogImage 由宿主桥转换），换图像库/换宿主
    /// 采集层一行不改——这正是相机层可复用的前提。
    ///
    /// 【为什么这么设计】（对标 HJVision CamSDK 的转换链切分）
    ///   - 缓冲一律"深拷自管"：SDK 回调返回后原缓冲即被复用（大恒/海康 3D 实测花屏血泪），
    ///     适配器必须把像素拷进托管数组再上抛，本 DTO 的数组语义=独立内存，下游拿到后
    ///     可异步使用（包装/再拷均安全）。
    ///   - Mono8：Pixels8 = w*h 字节（行跨距=宽，规整线性）——非 Mono8 帧已在适配器内
    ///     走 SDK 像素转换/钳制直拷统一成 Mono8；行跨距≠宽的填充对齐问题属于下游
    ///     图像容器（如 VisionPro CogImage 像素内存 Stride），由宿主桥按目标容器处理。
    ///   - Range16：Depth 帧 Pixels16=short[w*h]（ushort+32768 偏移重映射后），Scale*=
    ///     SDK fX/Y/ZScale 原值（下游构造"1/Scale"线性变换）；Profile 帧 RawBytes=
    ///     byte[w*h*2]（SDK 原始小端段直拷，高度钳 5 后的 w/h）。
    ///   - 坏帧永不 null：坏帧头/零帧长给 BlankMono8()（64x48 全 0 显式清零，
    ///     IsBlank=true 供诊断）——转换链"永不返回 null、永不抛"（SDK 回调线程抛异常
    ///     即崩进程）的纪律在 DTO 层延续。
    ///
    /// 【怎么接】适配器经 KxCameraDeviceBase.RaiseFrame 上抛（SDK 回调线程触发，
    /// 订阅方自行处理线程封送）；帧是"只读快照"语义，订阅方勿改数组内容。
    /// </summary>
    public sealed class KxCamFrame
    {
        /// <summary>像素语义。</summary>
        public KxCamFrameKind Kind;

        /// <summary>帧宽（像素）。</summary>
        public int Width;

        /// <summary>帧高（像素/行）。</summary>
        public int Height;

        /// <summary>Mono8 载体（w*h 字节，行跨距=宽；坏帧占位=64x48 全 0）。</summary>
        public byte[] Pixels8;

        /// <summary>Range16 Depth 载体（short[w*h]，已做 +32768 偏移重映射；Profile 帧为 null）。</summary>
        public short[] Pixels16;

        /// <summary>Range16 Profile 载体（byte[w*h*2] SDK 原始小端段，可能按 nDataLen 截短；Depth 帧为 null）。</summary>
        public byte[] RawBytes;

        /// <summary>3D 深度 X 尺度（SDK fXScale 原值；下游构造 1/Scale 线性变换）。</summary>
        public double ScaleX = 1.0;

        /// <summary>3D 深度 Y 尺度。</summary>
        public double ScaleY = 1.0;

        /// <summary>3D 深度 Z 尺度。</summary>
        public double ScaleZ = 1.0;

        /// <summary>坏帧占位标记（true=本帧是占位黑图，供诊断/日志，不做业务判定）。</summary>
        public bool IsBlank;

        /// <summary>构造 Mono8 帧（pixels 必须恰为 w*h 字节；规整线性，行跨距=宽）。</summary>
        public static KxCamFrame Mono8(int width, int height, byte[] pixels)
        {
            return new KxCamFrame
            {
                Kind = KxCamFrameKind.Mono8,
                Width = width,
                Height = height,
                Pixels8 = pixels,
            };
        }

        /// <summary>构造 3D Depth 帧（pixels 为重映射后的 short 线性段；尺度传 SDK 原值）。</summary>
        public static KxCamFrame RangeDepth(int width, int height, short[] pixels,
            double scaleX, double scaleY, double scaleZ)
        {
            return new KxCamFrame
            {
                Kind = KxCamFrameKind.Range16,
                Width = width,
                Height = height,
                Pixels16 = pixels,
                ScaleX = scaleX,
                ScaleY = scaleY,
                ScaleZ = scaleZ,
            };
        }

        /// <summary>构造 3D Profile 帧（rawBytes 为 SDK 原始小端段，w/h 已过高度钳 5）。</summary>
        public static KxCamFrame RangeProfile(int width, int height, byte[] rawBytes)
        {
            return new KxCamFrame
            {
                Kind = KxCamFrameKind.Range16,
                Width = width,
                Height = height,
                RawBytes = rawBytes,
            };
        }

        /// <summary>
        /// 坏帧占位小黑图（64x48 全 0，显式清零——下游图像容器自管内存不保证零初始化，
        /// 原版实测占位图残留 143 垃圾值血泪）。占位帧永不 null、永不抛。
        /// </summary>
        public static KxCamFrame BlankMono8()
        {
            return new KxCamFrame
            {
                Kind = KxCamFrameKind.Mono8,
                Width = 64,
                Height = 48,
                Pixels8 = new byte[64 * 48],   // byte 数组构造即全 0
                IsBlank = true,
            };
        }
    }
}
