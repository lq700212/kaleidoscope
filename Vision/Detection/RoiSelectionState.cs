namespace YoloDetection
{
    /// <summary>
    /// "按住鼠标拖拽框选 ROI"的交互状态机（纯逻辑，不碰任何控件/句柄/UI 框架，
    /// WinForms/WPF/任何宿主都能用，可脱离 UI 线程环境单元测试）。
    ///
    /// 状态流转：
    ///   Press(按下左键) ──▶ 选框中(IsSelecting=true，CurrentRectPixels 随 Drag 实时更新)
    ///   Release(松开)   ──▶ 拖动幅度达标 → 输出矩形返回 true；
    ///                       幅度过小(误触点击) → 视为取消返回 false 且矩形清空；
    ///   任何时刻 Reset() 可强制复位（如窗体关闭清理）。
    ///
    /// 坐标语义：所有输入/输出均为**调用方控件表面的像素坐标**（WinForms 传
    /// e.X/e.Y 即可）；"像素坐标 → 图像归一化坐标"的换算由 <see cref="ZoomMapping"/>
    /// 完成——本类只关心"拖了多大一个框"，与显示缩放方式无关。
    ///
    /// 矩形载体复用 <see cref="EsdRoiRect"/>（通用浮点矩形 X/Y/W/H，此处为控件像素）。
    ///
    /// 线程契约：仅限 UI 线程调用（鼠标事件与重绘同线程串行）。
    /// </summary>
    public sealed class RoiSelectionState
    {
        /// <summary>松手时拖动幅度小于该像素数视为误触点击，不产生 ROI</summary>
        public const float MinDragPixels = 5f;

        private float _startX, _startY;
        private float _lastX, _lastY;
        private bool _selecting;

        /// <summary>当前是否处于拖拽选框中（重绘叠加层据此决定是否绘制虚线框）</summary>
        public bool IsSelecting
        {
            get { return _selecting; }
        }

        /// <summary>
        /// 当前拖拽矩形（控件像素坐标，已规范化为左上角+正宽高，支持反向拖拽）。
        /// 未在选框中时返回 W=H=0 的空矩形。
        /// </summary>
        public EsdRoiRect CurrentRectPixels
        {
            get { return _selecting ? NormalizeRect(_startX, _startY, _lastX, _lastY) : default(EsdRoiRect); }
        }

        /// <summary>按下鼠标左键开始框选。</summary>
        public void Press(float x, float y)
        {
            _selecting = true;
            _startX = x;
            _startY = y;
            _lastX = x;
            _lastY = y;
        }

        /// <summary>拖拽移动（未按下时的调用被忽略）。</summary>
        public void Drag(float x, float y)
        {
            if (_selecting)
            {
                _lastX = x;
                _lastY = y;
            }
        }

        /// <summary>
        /// 松开鼠标结束框选。
        /// 返回 true 时 rectPixels 为本次框选的控件像素矩形；
        /// 返回 false 表示误触点击（幅度不足）或没有进行中的框选，
        /// 此时状态已复位、rectPixels 为空矩形（W=0），调用方无需再判幅度。
        /// </summary>
        public bool Release(float x, float y, out EsdRoiRect rectPixels)
        {
            rectPixels = default(EsdRoiRect);

            if (!_selecting)
            {
                return false;
            }

            _lastX = x;
            _lastY = y;

            // 先取矩形再复位：CurrentRectPixels 依赖 _selecting 标志，顺序反了会拿到空矩形
            EsdRoiRect rect = NormalizeRect(_startX, _startY, _lastX, _lastY);
            _selecting = false;

            bool valid = rect.W >= MinDragPixels && rect.H >= MinDragPixels;
            if (valid)
            {
                rectPixels = rect;
            }
            return valid;
        }

        /// <summary>强制复位（窗体关闭等场景）。</summary>
        public void Reset()
        {
            _selecting = false;
        }

        /// <summary>任意方向拖拽统一转成"左上角 + 正宽高"矩形（支持从右下往左上反着框）。</summary>
        private static EsdRoiRect NormalizeRect(float x1, float y1, float x2, float y2)
        {
            return new EsdRoiRect
            {
                X = x1 < x2 ? x1 : x2,
                Y = y1 < y2 ? y1 : y2,
                W = x1 < x2 ? x2 - x1 : x1 - x2,
                H = y1 < y2 ? y2 - y1 : y1 - y2
            };
        }
    }
}
