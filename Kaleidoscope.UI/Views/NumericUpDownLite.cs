using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Kaleidoscope.UI.Views;

/// <summary>
/// 轻量数字微调框（WinForms NumericUpDown 的 WPF 替身；V2.1.0 自宿主 HJVision-WPF
/// 迁入，语义零改动）。
///
/// 【为什么自绘一个】WPF 内置没有 NumericUpDown；WindowsFormsHost 塞 WinForms
/// 控件会引入空域限制（弹出层/主题跟随都会踩坑），一个 TextBox+两钮的小组合更轻。
/// 范围钳制与 WinForms 同语义：键入越界=钳回边界，箭头/滚轮步进 1。
/// 【绑定】Value 依赖属性（双向）：VM 写进来刷新显示；用户改完（含键入）钳界后
/// 立刻回写（LostFocus 与每次钳界都同步，确认类操作拿到的永远是合法值）。
///
/// 【血泪随迁】
/// ①模板不放真实按钮：FrameworkElementFactory.SetValue 不接受 Visual/ContentElement
///   派生值（构造即抛 NotSupportedException，宿主实测：换型窗/点检窗凡用本控件的
///   窗体全开不起来）——模板只留命名占位，运行时 OnApplyTemplate 再填真实按钮。
/// ②控件自绘模板绕开了共享 KxTextBox 模板，输入区底/描边/字色全靠控件自身属性
///   传给模板；不显式给时 WPF 主题默认值接管 → 深色下白底黑字。这里自键化：
///   底 Brush.Fresh.InputBg、描边 Brush.Shell.InputBorder、字/插入符 Brush.Shell.Fg。
/// </summary>
public class NumericUpDownLite : TextBox
{
    static NumericUpDownLite()
    {
        // 失焦才提交与常见 TextBox 相同；这里改成"每次变更都钳界回写"更贴 NumericUpDown。
        TextAlignmentProperty.OverrideMetadata(typeof(NumericUpDownLite),
            new FrameworkPropertyMetadata(TextAlignment.Left));
    }

    public NumericUpDownLite()
    {
        VerticalContentAlignment = VerticalAlignment.Center;
        Text = "1";
        // 上下小按钮（贴右缘内侧）：实例在构造期造好，等 OnApplyTemplate 挂到模板占位上
        //（血泪①见类头）。
        mSpin = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 1, 0)
        };
        mSpin.Children.Add(MakeSpinButton("▲", 1));
        mSpin.Children.Add(MakeSpinButton("▼", -1));
        // 深色禁黑字红线：底/描边/字/插入符全键化（血泪②见类头）。
        SetResourceReference(BackgroundProperty, "Brush.Fresh.InputBg");
        SetResourceReference(BorderBrushProperty, "Brush.Shell.InputBorder");
        SetResourceReference(ForegroundProperty, "Brush.Shell.Fg");
        SetResourceReference(CaretBrushProperty, "Brush.Shell.Fg");
        Template = BuildTemplate();
    }

    /// <summary>上下钮容器（构造期造好，OnApplyTemplate 挂进模板占位）。</summary>
    private readonly StackPanel mSpin;

    /// <summary>模板占位挂载：按钮真实进树在模板应用后（见构造函数注释）。</summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ContentPresenter host = GetTemplateChild("PART_SpinHost") as ContentPresenter;
        if (host != null && mSpin != null) host.Content = mSpin;
    }

    /// <summary>组装模板：输入区 + 右缘占位（PART_SpinHost 运行时装上下两钮）。</summary>
    private ControlTemplate BuildTemplate()
    {
        FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetBinding(Border.BackgroundProperty,
            new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderBrushProperty,
            new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        // 圆角 7 与共享 KxTextBox 同款（设计稿 .input border-radius:7px）
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        FrameworkElementFactory host = new FrameworkElementFactory(typeof(Grid));
        host.Name = "Grid";
        FrameworkElementFactory scroller = new FrameworkElementFactory(typeof(ScrollViewer));
        scroller.Name = "PART_ContentHost";
        scroller.SetValue(ScrollViewer.MarginProperty, new Thickness(2, 0, 18, 0));
        scroller.SetValue(BackgroundProperty, Brushes.Transparent);
        FrameworkElementFactory btnHost = new FrameworkElementFactory(typeof(ContentPresenter));
        btnHost.Name = "PART_SpinHost";   // 只命名不塞实例；OnApplyTemplate 后挂真实按钮
        btnHost.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
        btnHost.SetValue(VerticalAlignmentProperty, VerticalAlignment.Stretch);
        host.AppendChild(scroller);
        host.AppendChild(btnHost);
        border.AppendChild(host);
        ControlTemplate t = new ControlTemplate(typeof(TextBox));
        t.VisualTree = border;
        return t;
    }

    private Button MakeSpinButton(string glyph, int delta)
    {
        Button b = new Button
        {
            Content = glyph,
            FontSize = 7,
            Width = 14,
            Height = 10,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Focusable = false
        };
        int d = delta;
        b.Click += delegate { Step(d); };
        return b;
    }

    // ---- 依赖属性 ----

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumericUpDownLite),
        new FrameworkPropertyMetadata(1,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            delegate(DependencyObject d, DependencyPropertyChangedEventArgs e)
            {
                NumericUpDownLite c = (NumericUpDownLite)d;
                int v = (int)e.NewValue;
                if (v < c.Min) v = c.Min;
                if (v > c.Max) v = c.Max;
                string text = v.ToString();
                if (c.Text != text) c.Text = text;
            }));

    /// <summary>当前值（双向绑定；越界自动钳回 Min/Max，与 WinForms NumericUpDown 同语义）。</summary>
    public int Value
    {
        get { return (int)GetValue(ValueProperty); }
        set { SetValue(ValueProperty, value); }
    }

    public static readonly DependencyProperty MinProperty = DependencyProperty.Register(
        nameof(Min), typeof(int), typeof(NumericUpDownLite), new PropertyMetadata(1));

    /// <summary>下界（含）。</summary>
    public int Min
    {
        get { return (int)GetValue(MinProperty); }
        set { SetValue(MinProperty, value); }
    }

    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
        nameof(Max), typeof(int), typeof(NumericUpDownLite), new PropertyMetadata(100));

    /// <summary>上界（含）。</summary>
    public int Max
    {
        get { return (int)GetValue(MaxProperty); }
        set { SetValue(MaxProperty, value); }
    }

    // ---- 交互 ----

    /// <summary>步进 ±1（上/下钮与上下方向键共用；越界钳回边界）。</summary>
    private void Step(int delta)
    {
        int v = CurrentValue() + delta;
        if (v < Min) v = Min;
        if (v > Max) v = Max;
        Value = v;
        Text = v.ToString();
        CaretIndex = Text.Length;
    }

    /// <summary>从文本解析当前值（非法回 Min 兜底，防 NaN 空值流入业务）。</summary>
    private int CurrentValue()
    {
        int v;
        if (!int.TryParse((Text ?? "").Trim(), out v)) v = Min;
        return v;
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        int v = CurrentValue();
        int clamped = v;
        if (clamped < Min) clamped = Min;
        if (clamped > Max) clamped = Max;
        if (Value != clamped) Value = clamped;   // 键入即钳界回写（确认类操作拿到的永远合法）
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        // 只放行数字（负号按 Min>=1 语义不放行；粘贴由 OnTextChanged 钳界兜底）。
        foreach (char c in e.Text)
        {
            if (c < '0' || c > '9') { e.Handled = true; return; }
        }
        base.OnPreviewTextInput(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Up) { Step(1); e.Handled = true; return; }
        if (e.Key == Key.Down) { Step(-1); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Step(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        // 失焦规范化显示（去前导零/空值回 Min），与 WinForms 行为一致。
        int v = CurrentValue();
        if (v < Min) v = Min;
        if (v > Max) v = Max;
        Value = v;
        Text = v.ToString();
        base.OnLostFocus(e);
    }
}
