using System.Windows;
using System.Windows.Media;

namespace Kaleidoscope.UI.Views
{
    /// <summary>
    /// KxRoundedClip —— 给任意 FrameworkElement 挂"圆角矩形裁剪"的附加属性【V2.3.1】。
    ///
    /// 为什么需要它：WPF 的 Border.CornerRadius 只裁自身背景/边框，**不裁子元素**——
    /// 卡片里放表格/图片等自带方角背景的子元素时，子元素四角会盖过卡片的圆角弧线，
    /// 看起来"外壳圆、内容直角"（ProductionStatistics 主表格 DataGrid 是实际案例：
    /// 卡片圆角 18，但表头/行底自带背景，四角仍是直角）。
    /// 标准解法是给元素挂 Clip=RectangleGeometry(圆角矩形)，本属性把套路收拢成一行 XAML：
    ///   xmlns:ui="clr-namespace:Kaleidoscope.UI.Views;assembly=Kaleidoscope.UI"
    ///   &lt;DataGrid ui:KxRoundedClip.Radius="12" ... /&gt;
    ///
    /// 做什么：Radius>0 时自动在 Loaded/SizeChanged 时按元素**实际尺寸**同步裁剪几何
    ///   （几何实例按元素复用，拖拽缩放高频触发零分配）；Radius=0 时摘除裁剪恢复方角。
    /// 语义与 ChromeBorderlessWindow.UpdateRootClip（根框架整体裁剪：Clip 挂元素自身、
    ///   坐标取元素自己的 (0,0,ActualWidth,ActualHeight)、半径=属性全值）同源，但作为
    ///   公共附加属性开放给任意元素；与 ChromeDialogWindow 的"卡片内容内缘裁剪"
    ///   （半径=外弧−1px、Clip 挂 Child）是两种口径，按需选用，勿混为一谈。
    ///
    /// 怎么改：宿主零代码；半径取值建议 = 外层卡片圆角 − 卡片 Padding（同心圆弧观感）。
    /// </summary>
    public static class KxRoundedClip
    {
        /// <summary>圆角半径（逻辑像素）。0=不裁（默认）；>0=按元素实际尺寸裁圆角矩形。</summary>
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(KxRoundedClip),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnRadiusChanged));

        /// <summary>私有：按元素挂"已就绪的裁剪几何"，尺寸/半径变化只改参数不重建</summary>
        private static readonly DependencyProperty GeometryProperty = DependencyProperty.RegisterAttached(
            "Geometry", typeof(RectangleGeometry), typeof(KxRoundedClip), new PropertyMetadata(null));

        /// <summary>私有：是否已挂钩 Loaded/SizeChanged（保证只挂一次）</summary>
        private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(KxRoundedClip), new PropertyMetadata(false));

        public static double GetRadius(DependencyObject obj) { return (double)obj.GetValue(RadiusProperty); }
        public static void SetRadius(DependencyObject obj, double value) { obj.SetValue(RadiusProperty, value); }

        private static RectangleGeometry GetGeometry(DependencyObject obj) { return (RectangleGeometry)obj.GetValue(GeometryProperty); }
        private static void SetGeometry(DependencyObject obj, RectangleGeometry value) { obj.SetValue(GeometryProperty, value); }
        private static bool GetHooked(DependencyObject obj) { return (bool)obj.GetValue(HookedProperty); }
        private static void SetHooked(DependencyObject obj, bool value) { obj.SetValue(HookedProperty, value); }

        /// <summary>
        /// 半径变化入口：首次使用时挂钩（Loaded/SizeChanged）+ 建几何；之后只同步参数。
        /// 只支持 FrameworkElement（裁剪坐标取实际尺寸）；挂到别的类型静默忽略不抛异常。
        /// </summary>
        private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var el = d as FrameworkElement;
            if (el == null) return;

            if (!GetHooked(el))
            {
                el.SizeChanged += OnSizeChanged;
                el.Loaded += OnLoaded;
                SetHooked(el, true);
            }

            var geo = GetGeometry(el);
            if (geo == null)
            {
                geo = new RectangleGeometry();
                SetGeometry(el, geo);
            }
            double r = (double)e.NewValue;
            geo.RadiusX = r;
            geo.RadiusY = r;
            // 半径=0：摘掉裁剪（恢复默认方角）；>0：立即按当前尺寸挂上（未布局时矩形会跳过，等 SizeChanged 再来）
            el.Clip = r > 0 ? geo : null;
            更新矩形(el, geo);
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) { 刷新((FrameworkElement)sender); }
        private static void OnLoaded(object sender, RoutedEventArgs e) { 刷新((FrameworkElement)sender); }

        /// <summary>按元素当前实际尺寸重算裁剪矩形（Loaded/SizeChanged 统一入口）</summary>
        private static void 刷新(FrameworkElement el)
        {
            var geo = GetGeometry(el);
            if (geo == null) return;
            更新矩形(el, geo);
        }

        /// <summary>更新几何矩形；元素未布局（0 尺寸）时跳过，等下次 SizeChanged 再来</summary>
        private static void 更新矩形(FrameworkElement el, RectangleGeometry geo)
        {
            if (el.ActualWidth <= 0 || el.ActualHeight <= 0) return;
            geo.Rect = new Rect(0, 0, el.ActualWidth, el.ActualHeight);
        }
    }
}
