using System;

namespace YoloDetection
{
    /// <summary>
    /// PictureBox Zoom 模式（图像等比缩放+居中，letterbox 布局）的坐标换算数学。
    /// 纯函数、零 UI 框架依赖（全 float 参数），WinForms/WPF 宿主均可直接使用。
    ///
    /// 背景：Zoom 模式下控件客户区与图像显示区之间存在"黑边"（letterbox），
    /// 鼠标坐标不能直接除以控件尺寸当归一化坐标，必须先换算到图像显示区。
    ///
    /// 布局示意（宽图放进竖高控件）：
    /// ┌───────────────────────────┐ ← 控件
    /// │ ░░░░░░░ 上下黑边 ░░░░░░░ │
    /// │   ┌───────────────────┐   │
    /// │   │   图像实际显示区     │   │ ← displayRect（TryGetDisplayRect 的输出）
    /// │   └───────────────────┘   │
    /// │ ░░░░░░░ 上下黑边 ░░░░░░░ │
    /// └───────────────────────────┘
    /// </summary>
    public static class ZoomMapping
    {
        /// <summary>
        /// 计算 Zoom 模式下图像的实际显示矩形（含居中偏移，控件像素坐标）。
        /// 任一尺寸非正时返回 false（如图像尚未加载）。
        /// 矩形载体复用 <see cref="EsdRoiRect"/>（通用浮点矩形）。
        /// </summary>
        public static bool TryGetDisplayRect(
            int controlWidth, int controlHeight, int imageWidth, int imageHeight,
            out EsdRoiRect displayRect)
        {
            displayRect = default(EsdRoiRect);

            if (controlWidth <= 0 || controlHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
            {
                return false;
            }

            // 与 WinForms Zoom 模式同源算法：取较小缩放比保证整图可见，再双向居中
            float scale = Math.Min((float)controlWidth / imageWidth, (float)controlHeight / imageHeight);
            float displayW = imageWidth * scale;
            float displayH = imageHeight * scale;

            displayRect = new EsdRoiRect
            {
                X = (controlWidth - displayW) / 2f,
                Y = (controlHeight - displayH) / 2f,
                W = displayW,
                H = displayH
            };
            return true;
        }

        /// <summary>
        /// 控件坐标 → 图像归一化坐标（0~1，相对图像宽高）。
        ///
        /// 点落在 letterbox 黑边或控件外时不判失败而是夹紧到 [0,1]：
        /// 框选拖拽经常甩出画面边缘，夹紧让"拖到头"得到贴边矩形，体验更顺滑。
        /// 图像尺寸无效时返回 false。
        /// </summary>
        public static bool TryMapControlToNormalized(
            float controlX, float controlY,
            int controlWidth, int controlHeight, int imageWidth, int imageHeight,
            out float normalizedX, out float normalizedY)
        {
            normalizedX = 0f;
            normalizedY = 0f;

            EsdRoiRect rect;
            if (!TryGetDisplayRect(controlWidth, controlHeight, imageWidth, imageHeight, out rect))
            {
                return false;
            }

            // 显示区内线性映射；越界部分由 Clamp01 收口
            normalizedX = Clamp01((controlX - rect.X) / rect.W);
            normalizedY = Clamp01((controlY - rect.Y) / rect.H);
            return true;
        }

        /// <summary>
        /// 一次拖拽（起点→终点）直接换算成归一化 ROI 矩形——框选标定的端到端换算。
        ///
        /// 处理链：两端点分别映射归一化坐标 → 取包围盒（支持反向拖拽）→
        /// 宽高下限 0.01（防零面积区域）→ 贴右/下边缘时向回收，保证 X+W ≤ 1、Y+H ≤ 1。
        /// 图像尺寸无效时返回 false。
        /// </summary>
        public static bool TryMapDragToNormalizedRoi(
            float dragStartX, float dragStartY, float dragEndX, float dragEndY,
            int controlWidth, int controlHeight, int imageWidth, int imageHeight,
            out EsdRoiRect normalizedRoi)
        {
            normalizedRoi = default(EsdRoiRect);

            float n1x, n1y, n2x, n2y;
            bool ok1 = TryMapControlToNormalized(
                dragStartX, dragStartY, controlWidth, controlHeight, imageWidth, imageHeight,
                out n1x, out n1y);
            bool ok2 = TryMapControlToNormalized(
                dragEndX, dragEndY, controlWidth, controlHeight, imageWidth, imageHeight,
                out n2x, out n2y);

            if (!ok1 || !ok2)
            {
                return false;
            }

            float roiX = Math.Min(n1x, n2x);
            float roiY = Math.Min(n1y, n2y);
            float roiW = Math.Max(0.01f, Math.Abs(n1x - n2x));
            float roiH = Math.Max(0.01f, Math.Abs(n1y - n2y));

            normalizedRoi = new EsdRoiRect
            {
                X = Math.Min(roiX, 1f - roiW),
                Y = Math.Min(roiY, 1f - roiH),
                W = roiW,
                H = roiH
            };
            return true;
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
