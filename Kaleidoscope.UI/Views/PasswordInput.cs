using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kaleidoscope.UI.Views;

/// <summary>
/// 密码输入控件（内嵌眼睛控件；V2.1.0 自宿主 HJVision-WPF 迁入，语义零改动）。
///
/// 结构：
/// ┌────────────────────────────┐
/// │ ••••••••••            ◉  │  ← 眼睛钮内嵌在输入框右缘内侧（不是框外独立钮）
/// └────────────────────────────┘
/// 明文态用一个 TextBox 与 PasswordBox 叠在同一格里互切（Visibility 切换），
/// 两边始终同步同一份密码。
///
/// 【为什么不用 🚫/👁 双字形切状态】设计稿里眼睛恒为 👁；且 🚫 是"禁止"符号、
/// 语义并不等于"隐藏密码"，各平台字形还不一样。三保险：
/// ①tooltip 说清动作（点击显示密码明文 / 点击隐藏密码明文）；
/// ②眼睛图标形态随状态（隐藏=空心瞳孔+斜线，明文=实心瞳孔——对位原版 EyeIcon）；
/// ③眼睛钮颜色随状态（隐藏=弱色 Brush.Shell.FgDim，明文=主色 Brush.Shell.Primary）；
/// ④框内内容本身（圆点 vs 明文）就是最直观的状态反馈。
///
/// 【PasswordBox 不能绑定】→ 对外是 <see cref="Password"/> 依赖属性（默认双向绑定）
/// + <see cref="PasswordChanged"/> 事件（给 View 做 glue 用）。
/// </summary>
public class PasswordInput : UserControl
{
    private readonly PasswordBox _pwd;
    private readonly TextBox _show;
    private readonly Button _eye;
    private readonly TextBlock _ph;
    // 矢量眼：公共 EyeIconView 自绘两态（杏仁轮廓 + 斜线/实心瞳孔）。
    // 颜色仍随明文/密文状态切（弱色/主色），作为形态之外的第二重提示。
    private readonly EyeIconView _eyeIcon;
    private bool _syncing;

    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password), typeof(string), typeof(PasswordInput),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            new PropertyChangedCallback(OnPasswordChanged)));

    /// <summary>密码明文（双向绑定；外部写也会同步到两个输入框）。</summary>
    public string Password
    {
        get { return (string)GetValue(PasswordProperty); }
        set { SetValue(PasswordProperty, value); }
    }

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(PasswordInput),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender,
            new PropertyChangedCallback(OnPlaceholderChanged)));

    /// <summary>占位提示（空值时显示在框内，弱色）。</summary>
    public string Placeholder
    {
        get { return (string)GetValue(PlaceholderProperty); }
        set { SetValue(PlaceholderProperty, value); }
    }

    public static readonly DependencyProperty IsShownProperty = DependencyProperty.Register(
        nameof(IsShown), typeof(bool), typeof(PasswordInput),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
            new PropertyChangedCallback(OnIsShownChanged)));

    /// <summary>当前是否明文可见（true=看得见密码）。</summary>
    public bool IsShown
    {
        get { return (bool)GetValue(IsShownProperty); }
        set { SetValue(IsShownProperty, value); }
    }

    /// <summary>密码变化事件（外部 glue 用：登录窗回传 VM、授权码窗同步明文框）。</summary>
    public event EventHandler? PasswordChanged;

    /// <summary>
    /// 内层输入框的 KeyDown 外抛（登录窗回车链用：密码框回车=登录 / 确认框回车=保存）。
    /// 明文态与密文态两个框都往外抛，调用方写法不变。
    /// </summary>
    public event System.Windows.Input.KeyEventHandler? PasswordKeyDown;

    public PasswordInput()
    {
        // 内层两框不写死 Height（宿主 2026-11-10 目检）：跟随控件自身高度拉伸——
        // 此前写死 30，外层调高（登录窗 34）时内层仍是 30，眼睛与新框高不配套。
        _pwd = new PasswordBox { VerticalContentAlignment = VerticalAlignment.Center };
        _pwd.SetResourceReference(FrameworkElement.StyleProperty, "KxPasswordBox");

        _show = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _show.SetResourceReference(FrameworkElement.StyleProperty, "KxTextBox");

        // 事件订阅放在两个内层框都构造完之后：密码框的回调要写 _show.Text，
        // 若在上面挂订阅，编译器按字段赋值顺序判定 _show 可能为 null（CS8602）。
        _pwd.PasswordChanged += delegate
        {
            if (_syncing) return;
            _syncing = true;
            try { _show.Text = _pwd.Password; SetCurrentValue(PasswordProperty, _pwd.Password); }
            finally { _syncing = false; }
            ApplyState();
            RaisePasswordChanged();
        };

        _show.TextChanged += delegate
        {
            if (_syncing) return;
            _syncing = true;
            try { _pwd.Password = _show.Text; SetCurrentValue(PasswordProperty, _show.Text); }
            finally { _syncing = false; }
            ApplyState();
            RaisePasswordChanged();
        };

        _eyeIcon = new EyeIconView();

        _eye = new Button
        {
            Content = _eyeIcon,
            Width = 26,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "点击显示密码明文",
        };
        _eye.Click += delegate { IsShown = !IsShown; };

        _ph = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Margin = new Thickness(10, 0, 34, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        try
        {
            object? fg = Application.Current?.TryFindResource("Brush.Shell.FgDim");
            if (fg is Brush b) _ph.Foreground = b;
        }
        catch { }

        Grid host = new Grid();
        host.Children.Add(_pwd);
        host.Children.Add(_show);
        host.Children.Add(_ph);
        host.Children.Add(_eye);
        Content = host;

        System.Windows.Input.KeyEventHandler forward = delegate(object s, System.Windows.Input.KeyEventArgs e)
        {
            try { if (PasswordKeyDown != null) PasswordKeyDown(s, e); } catch { }
        };
        _pwd.KeyDown += forward;
        _show.KeyDown += forward;

        Loaded += delegate { ApplyState(); };
        ApplyState();
    }

    /// <summary>把焦点放进密码框（登录窗初始焦点用）。</summary>
    public void FocusPassword()
    {
        try { (IsShown ? (System.Windows.Controls.Control)_show : _pwd).Focus(); } catch { }
    }

    /// <summary>按当前状态刷新三件套：框体互切 / 占位符可见性 / 眼睛钮前景与 tooltip。</summary>
    private void ApplyState()
    {
        try
        {
            bool shown = IsShown;
            _pwd.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
            _show.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            _ph.Text = Placeholder ?? "";
            _ph.Visibility = (!shown && string.IsNullOrEmpty(Password)) ? Visibility.Visible : Visibility.Collapsed;
            _eye.ToolTip = shown ? "点击隐藏密码明文" : "点击显示密码明文";
            // 两态图标（原版 EyeIcon 语言）：形态 + 颜色双通道同步翻转。
            _eyeIcon.Shown = shown;
            object? key = Application.Current?.TryFindResource(shown ? "Brush.Shell.Primary" : "Brush.Shell.FgDim");
            if (key is Brush b)
            {
                _eyeIcon.GlyphBrush = b;
            }
        }
        catch { }
    }

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        PasswordInput? c = d as PasswordInput;
        if (c == null) return;
        string v = e.NewValue as string ?? "";
        if (c._syncing) return;
        c._syncing = true;
        try
        {
            if (c._pwd.Password != v) c._pwd.Password = v;
            if (c._show.Text != v) c._show.Text = v;
        }
        finally { c._syncing = false; }
        c.ApplyState();
        c.RaisePasswordChanged();
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        PasswordInput? c = d as PasswordInput;
        if (c != null) c.ApplyState();
    }

    private static void OnIsShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        PasswordInput? c = d as PasswordInput;
        if (c != null) c.ApplyState();
    }

    private void RaisePasswordChanged()
    {
        try { if (PasswordChanged != null) PasswordChanged(this, EventArgs.Empty); }
        catch { }
    }

    /// <summary>外部回写（VM 侧清空密码等场景）：静默同步两个框，不触发 PasswordChanged。</summary>
    public void SetPasswordSilent(string value)
    {
        _syncing = true;
        try
        {
            string v = value ?? "";
            _pwd.Password = v;
            _show.Text = v;
            SetCurrentValue(PasswordProperty, v);
        }
        finally { _syncing = false; }
        ApplyState();
    }
}
