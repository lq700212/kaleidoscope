using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using Kaleidoscope.UI.Converters;

namespace Kaleidoscope.UI.Views;

/// <summary>
/// 标题条共用控件（V2.1.0 自宿主 HJVision-WPF 的 SunnyTitleBar 迁入并去 Sunny 化，
/// 与 ChromeDialogWindow 基类内嵌标题条融合成一份）。
///
/// 【干什么】圆角自绘窗的标题条：品牌渐变底（Brush.HeaderGrad）+ 底边线
///   （Brush.TopbarBorder）+ 顶部圆角（Fn.ShellRadiusTop，最大化归零）+
///   图标胶囊（可选）+ 标题文本（自动绑宿主 Window.Title）+ 副题（可选）+
///   —/▢/× 系统三钮（可配置显隐）。行为四件事收拢在本控件：
///   空白拖动 / — 最小化 / ▢ 最大化还原切换 / × 优先 CloseCommand 否则关窗。
///
/// 【两宿主口径】（缺省=CureLine，HJVision 经 KxShellDefaults 一行配置）
///   CureLine：ShowMinButton=false + ShowMaxButton=false → 只剩 ✕（44 宽，
///     hover 红白字）；标题字 13 SemiBold；顶角圆角 10。
///   HJVision：ShowMinButton=true（+按窗 ShowMaxButton）；标题字 13 Normal；
///     顶角圆角 13/9（随主题键 Fn.ShellRadiusTop）；钮宽 40（KxShellDefaults.
///     SysButtonWidth）；—/▢ hover 走 Brush.Shell.SysBtnHover。
///
/// 【血泪随迁】
///   · WindowChrome 命中：宿主窗凡用 WindowChrome.CaptionHeight>0 的，标题条
///     区域全是系统 caption 区——三钮必须逐个设 IsHitTestVisibleInChrome=True
///     否则点击被当标题栏吃掉（SettingsWindow × 点不了即此因）。
///   · × 必须叫 "BtnClose"：操作员只读护栏（宿主 ReadOnlyGuard）按控件名白名单
///     （含 "close"）放行关闭钮；没有名字且内容只是图标字符时会被一并置灰，
///     无边框窗没有系统关闭钮，全灰=操作员被锁死在弹窗里（V4.4.268 审查抓获）。
///   · DockPanel LastChildFill=False + 三钮显式 Dock=Right：先加入先靠右，
///     视觉从左到右 —/▢/×；不设 Dock 首拍 ✕ 跑到标题条左端（CureLine 灰度
///     首轮目检抓获的同款坑）。
///   · TitleBackground 落地用回调打本地值（null=ClearValue 交还键引用）：
///     直接绑 DP 会成"死 DP"——XAML/代码里 Setter 写死的键引用压住 DP，
///     MBox 四态语义渐变赋值后纹丝不动（2026-11-04 目检抓获后修）。
///
/// 【线程】UI 线程构造/使用（WPF 常规纪律）。
/// </summary>
public class TitleBar : UserControl
{
    // ---------- 骨架成员 ----------
    private Border? _titleRoot;      // 渐变底条（CornerRadius 随最大化切换）
    private TextBlock? _titleText;   // 主标题（绑宿主 Window.Title）

    // ---------- 参数 DP ----------

    /// <summary>是否显示 — 最小化按钮（缺省 true；单 × 固定小窗设 false）。</summary>
    public static readonly DependencyProperty ShowMinButtonProperty =
        DependencyProperty.Register(nameof(ShowMinButton), typeof(bool), typeof(TitleBar),
            new PropertyMetadata(true));

    public bool ShowMinButton
    {
        get => (bool)GetValue(ShowMinButtonProperty);
        set => SetValue(ShowMinButtonProperty, value);
    }

    /// <summary>是否显示 ▢ 最大化按钮（缺省 false；仅可调尺寸工作区窗设 true）。</summary>
    public static readonly DependencyProperty ShowMaxButtonProperty =
        DependencyProperty.Register(nameof(ShowMaxButton), typeof(bool), typeof(TitleBar),
            new PropertyMetadata(false));

    public bool ShowMaxButton
    {
        get => (bool)GetValue(ShowMaxButtonProperty);
        set => SetValue(ShowMaxButtonProperty, value);
    }

    /// <summary>▢ 是否可用（缺省 true；固定尺寸窗显示三钮但禁用 ▢）。</summary>
    public static readonly DependencyProperty IsMaxButtonEnabledProperty =
        DependencyProperty.Register(nameof(IsMaxButtonEnabled), typeof(bool), typeof(TitleBar),
            new PropertyMetadata(true));

    public bool IsMaxButtonEnabled
    {
        get => (bool)GetValue(IsMaxButtonEnabledProperty);
        set => SetValue(IsMaxButtonEnabledProperty, value);
    }

    /// <summary>× 的自定义关闭命令（缺省 null=直接关窗；VM 回滚/清理逻辑走这里）。</summary>
    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(TitleBar),
            new PropertyMetadata(null));

    public ICommand CloseCommand
    {
        get => (ICommand)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    /// <summary>
    /// 标题条背景（缺省 null → 根 Border 走 Brush.HeaderGrad 键引用，主题热切换
    /// 随 DynamicResource 生效）。语义渐变定制（如 HJVision MBox 四态弹窗传
    /// Ok/Warn/Err 渐变）传显式 Brush：回调打本地值压过键引用；null=ClearValue
    /// 交还键引用（见类头【血泪随迁】死 DP 段）。
    /// </summary>
    public static readonly DependencyProperty TitleBackgroundProperty =
        DependencyProperty.Register(nameof(TitleBackground), typeof(Brush), typeof(TitleBar),
            new PropertyMetadata(null, OnTitleBackgroundChanged));

    public Brush TitleBackground
    {
        get => (Brush)GetValue(TitleBackgroundProperty);
        set => SetValue(TitleBackgroundProperty, value);
    }

    private static void OnTitleBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ApplyTitleBackground((TitleBar)d, e.NewValue as Brush);

    /// <summary>把语义渐变刷到标题条根 Border（null=ClearValue 回到字典键）。</summary>
    private static void ApplyTitleBackground(TitleBar bar, Brush? brush)
    {
        if (bar._titleRoot == null) return;   // InitializeComponent 未完成时留护栏
        if (brush == null) bar._titleRoot.ClearValue(Border.BackgroundProperty);
        else bar._titleRoot.Background = brush;
    }

    /// <summary>副题（主标题右侧弱化小字；null/空 不占位）。</summary>
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(TitleBar),
            new PropertyMetadata(null));

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>标题图标（矢量 Path；与 TitleIconChar 互斥，字符优先）。null 不占位。</summary>
    public static readonly DependencyProperty TitleIconProperty =
        DependencyProperty.Register(nameof(TitleIcon), typeof(Geometry), typeof(TitleBar),
            new PropertyMetadata(null));

    public Geometry? TitleIcon
    {
        get => (Geometry?)GetValue(TitleIconProperty);
        set => SetValue(TitleIconProperty, value);
    }

    /// <summary>标题图标字符（Segoe UI Emoji 单色轮廓渲染，缺码位回落 Symbol）。null 不占位。</summary>
    public static readonly DependencyProperty TitleIconCharProperty =
        DependencyProperty.Register(nameof(TitleIconChar), typeof(string), typeof(TitleBar),
            new PropertyMetadata(null));

    public string? TitleIconChar
    {
        get => (string?)GetValue(TitleIconCharProperty);
        set => SetValue(TitleIconCharProperty, value);
    }

    public TitleBar()
    {
        BuildSkeleton();
        // 兜底：TitleBackground 在骨架成员就绪前就被赋值的场景（护栏，成本极低）。
        Loaded += delegate { ApplyTitleBackground(this, TitleBackground); };
    }

    /// <summary>
    /// 组装标题条骨架（纯代码，视觉结构与原 SunnyTitleBar.xaml 逐属性对位）：
    /// 渐变底 Border（圆上角键 + 最大化归零）→ DockPanel（LastChildFill=False）→
    /// [图标胶囊] + 标题 + [副题] + ×/▢/— 三钮（Dock=Right 逆序加入）。
    /// 高度不写死——由宿主（ChromeDialogWindow 骨架）绑 CaptionHeight DP 拉伸。
    /// </summary>
    private void BuildSkeleton()
    {
        // 根 Border：渐变底 + 底边线（Brush.TopbarBorder——Light=透明不可见、Dark
        // 可见，原 CureLine 基类代码构建版同口径）+ 左 Padding 12（原 CureLine
        // 标题条 Padding="12,0,0,0"：无图标时标题 x=12；HJVision 有图标时胶囊
        // Margin -2 使胶囊 x=10、标题再 +10=38，两宿主逐像素对位）。
        var root = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 0, 0, 0),
        };
        root.SetResourceReference(Border.BackgroundProperty, "Brush.HeaderGrad");
        root.SetResourceReference(Border.BorderBrushProperty, "Brush.TopbarBorder");
        root.SetResourceReference(Border.CornerRadiusProperty, "Fn.ShellRadiusTop");
        root.MouseLeftButtonDown += Header_Drag;
        _titleRoot = root;

        var dock = new DockPanel { LastChildFill = false };

        // —— 图标胶囊（可选：TitleIcon/TitleIconChar 都 null 时整颗不占位）——
        // Margin 左 -2：根 Padding 12 内落到 x=10（HJVision 原胶囊左距口径）
        var iconCapsule = BuildIconCapsule();
        iconCapsule.Margin = new Thickness(-2, 0, 0, 0);
        dock.Children.Add(iconCapsule);

        // —— 主标题：13 号，字色键化，自动绑宿主 Window.Title ——
        // 左距 0：根 Border Padding 12 已定标（无图标=CureLine x12；有图标=胶囊
        // 右侧 +10，HJVision 原布局 x38 对位）
        var title = new TextBlock
        {
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TopbarFg");
        // 标题文本绑宿主 Window.Title（TitleBar 没有 Title 属性，绑定路径写字符串
        // "Title" + FindAncestor Window——与原 XAML RelativeSource AncestorType 同效）
        title.SetBinding(TextBlock.TextProperty, new Binding("Title")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
        });
        // 标题字重：键化（CureLine=SemiBold / HJVision=Normal，随宿主键板换皮）
        title.SetResourceReference(TextBlock.FontWeightProperty, "Fn.ShellTitleFontWeight");
        _titleText = title;
        dock.Children.Add(title);

        // —— 副题（可选）：弱化白 72% 9.5pt ——
        var subtitle = new TextBlock
        {
            FontSize = 9.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 1, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
        };
        subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(Subtitle)) { Source = this });
        subtitle.SetBinding(TextBlock.VisibilityProperty, new Binding(nameof(Subtitle))
        {
            Source = this,
            Converter = UiConverters.NullToCollapsed,
        });
        dock.Children.Add(subtitle);

        // —— 三钮（Dock=Right 逆序加入：×/▢/—，视觉从左到右 —/▢/×）——
        var btnClose = BuildSysButton("×", 14, KxShellDefaults.CloseFontWeight, "Brush.Fresh.CloseHoverBg", "White");
        btnClose.Name = "BtnClose";   // 只读护栏白名单（见类头【血泪随迁】）
        btnClose.Click += BtnClose_Click;
        DockPanel.SetDock(btnClose, Dock.Right);
        dock.Children.Add(btnClose);

        var btnMax = BuildSysButton("▢", 13, FontWeights.Normal, "Brush.Shell.SysBtnHover", null);
        btnMax.Click += BtnMax_Click;
        btnMax.SetBinding(Button.VisibilityProperty, new Binding(nameof(ShowMaxButton))
        {
            Source = this,
            Converter = UiConverters.TrueToVisible,
        });
        btnMax.SetBinding(Button.IsEnabledProperty, new Binding(nameof(IsMaxButtonEnabled)) { Source = this });
        DockPanel.SetDock(btnMax, Dock.Right);
        dock.Children.Add(btnMax);

        var btnMin = BuildSysButton("—", 13, FontWeights.Normal, "Brush.Shell.SysBtnHover", null);
        btnMin.Click += BtnMin_Click;
        btnMin.SetBinding(Button.VisibilityProperty, new Binding(nameof(ShowMinButton))
        {
            Source = this,
            Converter = UiConverters.TrueToVisible,
        });
        DockPanel.SetDock(btnMin, Dock.Right);
        dock.Children.Add(btnMin);

        root.Child = dock;
        Content = root;
    }

    /// <summary>
    /// 图标胶囊（18×18 半透明白 .22 圆角 5 + 白 78% 内容）：Path 与字符互斥
    /// （字符优先）；两者都 null 时整颗 Collapsed。构造期按 DP 当前值一次性定
    /// 可见性——DP 在宿主 XAML 里是先于控件树渲染设置的，构造完成后再变值的
    /// 场景（极少）由 PropertyChangedCallback 覆盖（本控件两项均 null 缺省，
    /// 迁移窗在 XAML 里设值先于挂树，构造期判定即可，不做回调保持轻量）。
    /// </summary>
    private FrameworkElement BuildIconCapsule()
    {
        var capsule = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
        };

        var grid = new Grid();

        var path = new Path
        {
            Width = 12, Height = 12,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(3),
            Fill = new SolidColorBrush(Color.FromArgb(0xC6, 0xFF, 0xFF, 0xFF)),
        };
        path.SetBinding(Path.DataProperty, new Binding(nameof(TitleIcon)) { Source = this });
        grid.Children.Add(path);

        var glyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC6, 0xFF, 0xFF, 0xFF)),
        };
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(TitleIconChar)) { Source = this });
        grid.Children.Add(glyph);

        capsule.Child = grid;

        // 可见性：DP 值在挂树前已定（XAML 赋值先于 Loaded），这里一次性判定。
        // 两个 DP 运行中变更的窗（当前无此用法）如需要可在宿主侧重设胶囊可见性。
        capsule.Loaded += delegate
        {
            bool hasIcon = TitleIcon != null || !string.IsNullOrEmpty(TitleIconChar);
            capsule.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
            // 字符优先于 Path：有字符时 Path 隐藏（与原 XAML 口径一致——两者互斥）
            path.Visibility = !string.IsNullOrEmpty(TitleIconChar) ? Visibility.Collapsed : Visibility.Visible;
            glyph.Visibility = !string.IsNullOrEmpty(TitleIconChar) ? Visibility.Visible : Visibility.Collapsed;
        };
        return capsule;
    }

    /// <summary>
    /// 系统钮（—/▢/×）工厂：透明底 + 前景键（TopbarFg）+ 宽度取
    /// KxShellDefaults.SysButtonWidth、高随标题条拉伸；hover 换 hover 键底。
    /// closeHoverKey 非空=悬停白字（Win32 惯例红底白字），否则保持前景键。
    /// </summary>
    private Button BuildSysButton(string glyph, double fontSize, FontWeight weight,
        string hoverKey, string? hoverForeground)
    {
        var btn = new Button
        {
            Content = glyph,
            Width = KxShellDefaults.SysButtonWidth,
            FontSize = fontSize,
            FontWeight = weight,
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        btn.SetResourceReference(ForegroundProperty, "Brush.TopbarFg");
        WindowChrome.SetIsHitTestVisibleInChrome(btn, true);
        btn.MouseEnter += delegate
        {
            btn.Background = TryFindUiBrush(hoverKey) ?? Brushes.Transparent;
            if (hoverForeground != null) btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hoverForeground));
        };
        btn.MouseLeave += delegate
        {
            btn.Background = Brushes.Transparent;
            btn.SetResourceReference(ForegroundProperty, "Brush.TopbarFg");
        };
        return btn;
    }

    /// <summary>App 资源查语义键（窗未入树时 Window.TryFindResource 链不到
    /// Application 资源——先走 Application 兜底）。测试态无 App 返 null。</summary>
    private Brush? TryFindUiBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? TryFindResource(key) as Brush;

    /// <summary>找宿主窗口（标题栏永远长在 Window 里，拿不到就什么都不做，不抛异常）。</summary>
    private Window? HostWindow()
    {
        try { return Window.GetWindow(this); }
        catch { return null; }
    }

    /// <summary>标题栏按住拖动（WindowChrome caption 区外的窗靠这个拖动）。</summary>
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Window host = HostWindow();
            if (host != null && e.LeftButton == MouseButtonState.Pressed) host.DragMove();
        }
        catch { }
    }

    /// <summary>— 最小化宿主。</summary>
    private void BtnMin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Window host = HostWindow();
            if (host != null) host.WindowState = WindowState.Minimized;
        }
        catch { }
    }

    /// <summary>▢ 最大化/还原切换。</summary>
    private void BtnMax_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Window host = HostWindow();
            if (host != null)
                host.WindowState = host.WindowState == WindowState.Maximized
                    ? WindowState.Normal : WindowState.Maximized;
        }
        catch { }
    }

    /// <summary>× 关闭：有 CloseCommand 且可执行就走命令，否则直接关宿主。</summary>
    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ICommand cmd = CloseCommand;
            if (cmd != null && cmd.CanExecute(null))
            {
                cmd.Execute(null);
                return;
            }
            Window host = HostWindow();
            if (host != null) host.Close();
        }
        catch { }
    }

    /// <summary>
    /// 最大化态跟随：宿主 ▢ 最大化时标题条顶角圆角归零（铺满不留透明弧），
    /// 还原时交还键引用（主题热切换继续生效）。宿主 StateChanged 由基类转调。
    /// </summary>
    internal void ApplyMaximizedState(bool maximized)
    {
        if (_titleRoot == null) return;
        if (maximized) _titleRoot.CornerRadius = new CornerRadius(0);
        else _titleRoot.SetResourceReference(Border.CornerRadiusProperty, "Fn.ShellRadiusTop");
    }
}
