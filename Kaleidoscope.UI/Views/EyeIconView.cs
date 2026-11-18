using System.Windows;
using System.Windows.Media;

namespace Kaleidoscope.UI.Views;

/// <summary>
/// 密码框内嵌"眼睛"矢量图标（V2.1.0 自宿主 HJVision-WPF 迁入；对标原版
/// mFormLicenseView.EyeIcon 自绘控件的两态语言）。
///
/// 【为什么自绘而不用 emoji 👁】原版实测 + 宿主目检：
/// ①emoji 字形随系统字体 fallback 波动，小尺寸按钮里常被裁/显示不全；
/// ②宿主铁律禁 emoji 当图标。矢量线条在任意 DPI/字体环境都有保障。
///
/// 【两态（原版 EyeIcon.OnPaint 一比一）】
///   Shown=false（密文圆点态）→ 杏仁眼轮廓 + 空心瞳孔 + 左下→右上斜线（"藏起来了"）；
///   Shown=true （明文可见态）→ 杏仁眼轮廓 + 实心瞳孔。
/// 颜色由宿主给 <see cref="GlyphBrush"/>：宿主沿用"隐藏=弱色 / 明文=主色"作
/// 第三重提示，原版是固定灰——形态差异已是主信号，颜色差异只是锦上添花。
///
/// 画布 20×16：眼睛轮廓 18×12 居中（左右各留 1px、上下各留 2px），
/// 斜线两端各出头约 1px，既压住轮廓又不被宿主裁切。
/// </summary>
public class EyeIconView : FrameworkElement
{
    /// <summary>默认描边色（原版 Color.FromArgb(96,96,96)，冻结复用）。</summary>
    private static readonly Brush DefaultGlyphBrush = CreateDefaultBrush();

    private static Brush CreateDefaultBrush()
    {
        SolidColorBrush b = new SolidColorBrush(Color.FromRgb(96, 96, 96));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// 杏仁眼轮廓（18×12 视框，两段三次贝塞尔）；构造期平移 (1,2) 落到 20×16 画布中央并冻结。
    /// 【血泪随迁】Geometry.Parse 返回的实例是只读冻结的，先 Clone 出可变副本再平移
    /// （harness 首跑实测：直接给 Parse 结果赋 Transform 抛"对象处于只读状态"）。
    /// </summary>
    private static readonly Geometry EyeOutline = CreateEyeOutline();

    private static Geometry CreateEyeOutline()
    {
        Geometry g = Geometry.Parse(
            "M 0,6 C 3,1.8 5.7,0 9,0 C 12.3,0 15,1.8 18,6 " +
            "C 15,10.2 12.3,12 9,12 C 5.7,12 3,10.2 0,6 Z").Clone();
        g.Transform = new TranslateTransform(1, 2);
        g.Freeze();
        return g;
    }

    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(bool), typeof(EyeIconView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>当前是否明文可见（true=实心瞳孔；false=空心瞳孔+斜线）。</summary>
    public bool Shown
    {
        get { return (bool)GetValue(ShownProperty); }
        set { SetValue(ShownProperty, value); }
    }

    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(
        nameof(GlyphBrush), typeof(Brush), typeof(EyeIconView),
        new FrameworkPropertyMetadata(DefaultGlyphBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>线条/瞳孔颜色（宿主按主题与明暗态给：弱色 Brush.Shell.FgDim / 主色 Brush.Shell.Primary）。</summary>
    public Brush GlyphBrush
    {
        get { return (Brush)GetValue(GlyphBrushProperty); }
        set { SetValue(GlyphBrushProperty, value); }
    }

    /// <summary>固定 20×16 画布（斜线出头需要比眼睛轮廓略大一圈）。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(20, 16);
    }

    protected override void OnRender(DrawingContext dc)
    {
        Brush brush = GlyphBrush ?? DefaultGlyphBrush;

        // 轮廓笔：1.3 与旧内联矢量眼一致；圆头/圆角防高 DPI 折角。
        Pen pen = new Pen(brush, 1.3);
        pen.StartLineCap = PenLineCap.Round;
        pen.EndLineCap = PenLineCap.Round;
        pen.LineJoin = PenLineJoin.Round;

        dc.DrawGeometry(null, pen, EyeOutline);

        // 瞳孔：眼睛中心 (10,8)，直径 4.6（与旧内联矢量眼一致）。
        Point center = new Point(10, 8);
        const double pupilRadius = 2.3;
        if (Shown)
        {
            // 明文可见 = 实心瞳孔。
            dc.DrawEllipse(brush, null, center, pupilRadius, pupilRadius);
        }
        else
        {
            // 密文隐藏 = 空心瞳孔 + 左下→右上斜线。
            dc.DrawEllipse(null, pen, center, pupilRadius, pupilRadius);

            Pen slash = new Pen(brush, 1.5);
            slash.StartLineCap = PenLineCap.Round;
            slash.EndLineCap = PenLineCap.Round;
            dc.DrawLine(slash, new Point(1.2, 14.8), new Point(18.8, 1.2));
        }
    }
}
