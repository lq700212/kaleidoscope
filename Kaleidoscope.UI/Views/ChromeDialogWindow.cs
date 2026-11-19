// =========================================================================
// ChromeDialogWindow：圆角自绘弹窗基类（V2.0.0 宿主 CureLine V0.0.109 反哺沉库；
// V2.1.0 融合宿主 HJVision-WPF 的 SunnyDialog/ChromeDialog 两基类，二合一）
//
// 【干什么】把各宿主"每个弹窗手抄一遍"的圆角窗骨架收成一个 Window 基类——
//   WindowStyle=None + WindowChrome（CaptionHeight 命中拖动 / 可缩放边）
//   + 透明分层窗 + 根 Grid（投影留白 Margin，KxShellDefaults.ShadowMargin）
//   + 圆角卡片 Border（Brush.Fresh.WinBg 底 / Brush.Fresh.Border 描边 /
//   Fn.ShellRadius 圆角 / Fx.WinShadow 投影，全部语义键 DynamicResource）
//   + 标题条（TitleBar 共用控件：渐变底 + 标题 + 可选 —/▢/× 三钮/副题/图标）
//   + 内容槽（子类 XAML 根内容自动进槽）。子类 XAML 可以像普通 Window 一样
//   只写根内容，骨架零感知。
//
// 【为什么】宿主 CureLine 单仓 26 个弹窗手抄同一段骨架 XAML（逐窗 ~15 行），
//   圆角/标题条高/投影参数全靠人肉对齐必然漂移；HJVision 侧 SunnyDialog
//   （36 窗）+ ChromeDialog（9 窗）也各自沉淀了两套平行基类——三个类职责
//   完全相同（圆角自绘弹窗壳），V2.1.0 按公司级"类似基类只留一份"口径融合：
//   所有宿主差异下沉为 DP 参数 / KxShellDefaults 宿主常量 / 语义键板换皮，
//   缺省值=CureLine 现状（CureLine 已迁窗零视觉变化）。
//
// 【两宿主口径对照】
//   CureLine（缺省零配置）：CaptionHeight=34、仅 ×（44 宽 hover 红）、根 Grid
//     Margin 14 浮层投影、无内容 Clip、无隐式注入、CenterScreen 不设（Manual）。
//   HJVision：KxShellDefaults 一行配置（ShadowMargin=0 / UseContentClip=true /
//     ImplicitStyleKeys=七类隐式键表 / 字体微软雅黑 12 / ShowInTaskbar=false /
//     StartupLocation=CenterScreen / SysButtonWidth=40）+ 窗级 DP
//     （CaptionHeight=36、ShowMinButton 按窗、CancelCommand 走 VM）。
//
// 【怎么接】窗 XAML 把根元素类名改为本类（x:Class 不动、命名空间换成
//   Kaleidoscope.UI.Views），其余零改动：
//     <ui:ChromeDialogWindow x:Class="YourApp.SomeWindow" ...
//         xmlns:ui="clr-namespace:Kaleidoscope.UI.Views;assembly=Kaleidoscope.UI"
//         Title="标题" Width="720" Height="560"
//         Resizeable="True">          ← 可缩放窗才写；定高弹窗缺省 false
//       …（原根内容原样保留——自动进骨架内容槽）
//     </ui:ChromeDialogWindow>
//   code-behind 的 Window 基类同步改。宿主须先合并 ChromeStyles/ShellStyles
//   或提供语义键板（本类只引 DynamicResource 语义键，缺键=静默空白）。
//
// 【血泪随迁（HJVision 两基类三年实测）】
//   · 分层窗 HWND 红线：内嵌 WindowsFormsHost（VisionPro 显示区 HWND）的窗
//     必须 UseLayeredTransparency=false——分层窗（WS_EX_LAYERED）里 HWND 不
//     渲染，画面整块消失（2026-10-30 harness 实测：PrintWindow 抓非分层窗
//     红块 561 像素、分层窗 0）。非分层窗圆角走 SetWindowRgn 物理 Region
//     （Region 裁剪含子 HWND，四角仍圆；边缘硬边锯齿为已知代价）。
//   · Border 圆角不裁子元素：WPF Border.CornerRadius 只裁自身背景不裁子元素
//     ——根卡片内的实底内容容器会把下两角透空月牙填白，弹窗"上圆下方"
//     （Login 首报，36 家共性）。根治=内容挂 RectangleGeometry 圆角 Clip
//     （半径=外弧−1px 边框内缘弧，最大化归零还原恢复）。
//   · 隐式样式注入时机必须在 OnInitialized：构造早于 InitializeComponent，
//     注入后子类 XAML 的 Window.Resources 再声明同名隐式样式时 BAML 加载器
//     抛 XamlParseException"已添加项"（V0.9.52 六窗同案）；OnInitialized 时
//     XAML 已解析完，注入走索引器=覆盖语义。键表经 KxShellDefaults.
//     ImplicitStyleKeys（"类型=键名"CSV）由宿主提供，库不硬编码键名。
//   · WindowChrome 命中：标题条按钮必须 IsHitTestVisibleInChrome=True（见
//     TitleBar 类头）。
//
// 【线程】UI 线程构造/使用（WPF 常规纪律）。
// =========================================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Kaleidoscope.UI.Views;

public class ChromeDialogWindow : Window
{
    // ---------- 骨架成员 ----------
    private ContentPresenter? _contentHost;   // 子类内容槽（标题条之下的填充区）
    private Grid? _skeleton;                  // 窗骨架根（投影留白 Margin + 卡片 Border）
    private Border? _card;                    // 圆角卡片（Clip/最大化归零操作目标）
    private TitleBar? _titleBar;              // 标题条控件（DP 转发目标）
    private bool _restoringContent;           // OnContentChanged 重入闸
    private bool _layered;                    // 分层分支（构造期定格，运行中不切换）

    // ---------- 参数 DP（缺省=弹窗口径：标题条 34、不可缩放、仅 ×） ----------

    /// <summary>标题条高度（兼 WindowChrome 命中拖动区高度）。缺省 34=库口径
    /// （与 WinTitleBar 样式同源）。HJVision 口径 36。</summary>
    public static readonly DependencyProperty CaptionHeightProperty =
        DependencyProperty.Register(nameof(CaptionHeight), typeof(double), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(34.0, OnChromeParamChanged));

    /// <summary>
    /// 是否装配基类内建标题条（缺省 true）。false=骨架只有内容槽（自绘标题栏的窗用，
    /// 如宿主 HJVision 的脚本编辑器：VS 深灰 30px 自绘条 + WindowChrome CaptionHeight=30
    /// 命中拖动——基类渐变标题条与自绘条不能双装）。
    /// </summary>
    public static readonly DependencyProperty ShowTitleBarProperty =
        DependencyProperty.Register(nameof(ShowTitleBar), typeof(bool), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(true));

    /// <summary>true=可缩放窗（ResizeBorderThickness=6，如日志/报警/调试窗）；
    /// false=定高弹窗（0）。缺省 false——与手抄口径逐窗对应。</summary>
    public static readonly DependencyProperty ResizeableProperty =
        DependencyProperty.Register(nameof(Resizeable), typeof(bool), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(false, OnChromeParamChanged));

    /// <summary>
    /// 可缩放窗的边缘缩放命中区宽度（仅 Resizeable=true 时生效；缺省 6=库口径）。
    /// HJVision 部分直连窗用 4px（DataCenter/Help 等窄边框手感），窗 XAML 逐窗设。
    /// </summary>
    public static readonly DependencyProperty ResizeBorderThicknessProperty =
        DependencyProperty.Register(nameof(ResizeBorderThickness), typeof(double), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(6.0, OnChromeParamChanged));

    /// <summary>是否显示 — 最小化钮（缺省 false=仅 ×；HJVision 固定小弹窗 true）。</summary>
    public static readonly DependencyProperty ShowMinButtonProperty =
        DependencyProperty.Register(nameof(ShowMinButton), typeof(bool), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(false));

    /// <summary>是否显示 ▢ 最大化钮（缺省 false；可缩放工作区窗设 true）。</summary>
    public static readonly DependencyProperty ShowMaxButtonProperty =
        DependencyProperty.Register(nameof(ShowMaxButton), typeof(bool), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(false));

    /// <summary>▢ 是否可用（缺省 true；固定尺寸但显示三钮的窗可禁用）。</summary>
    public static readonly DependencyProperty IsMaxButtonEnabledProperty =
        DependencyProperty.Register(nameof(IsMaxButtonEnabled), typeof(bool), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(true));

    /// <summary>× 的自定义关闭命令（缺省 null=直接关窗）。转发给 TitleBar。</summary>
    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(null));

    /// <summary>标题条副题（主标题右侧弱化小字；null 不占位）。</summary>
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(null));

    /// <summary>标题条图标字符（Segoe UI Emoji 单色轮廓；与 TitleIcon 互斥，字符优先）。</summary>
    public static readonly DependencyProperty TitleIconCharProperty =
        DependencyProperty.Register(nameof(TitleIconChar), typeof(string), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(null));

    /// <summary>标题条图标（矢量 Path；字符优先于 Path）。</summary>
    public static readonly DependencyProperty TitleIconProperty =
        DependencyProperty.Register(nameof(TitleIcon), typeof(Geometry), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(null));

    /// <summary>
    /// 标题条背景（语义渐变定制：宿主 HJVision 的 MBox 四态弹窗按 Ok/Warn/Err/Ask
    /// 传对应渐变——经 code-behind 给本 DP 赋值，转发给内建 TitleBar；null=走键板
    /// Brush.HeaderGrad 缺省渐变，主题热切换随 DynamicResource 生效）。
    /// </summary>
    public static readonly DependencyProperty TitleBackgroundProperty =
        DependencyProperty.Register(nameof(TitleBackground), typeof(Brush), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(null));

    /// <summary>
    /// ESC 键触发的取消命令（缺省 null=无行为）。HJVision 口径：弹窗 Esc 走
    /// VM 的 CancelCommand（回滚/清理在 VM）——V0.9 前各窗自写 PreviewKeyDown
    /// 抄 N 遍，V2.1.0 收进基类（兑现原 SunnyDialog 头注释第③条承诺）。
    /// </summary>
    public static readonly DependencyProperty CancelCommandProperty =
        DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(null));

    /// <summary>
    /// 是否用分层透明窗（缺省 true）。
    /// 【红线】内嵌 WindowsFormsHost（VisionPro 显示区 HWND）的窗必须设 false
    /// ——分层窗（WS_EX_LAYERED）里 HWND 不渲染，画面会整块消失。false 分支：
    /// AllowsTransparency=false + 窗自身挂 Brush.Shell.WinBg 底 + Loaded 后
    /// SetWindowRgn 物理 Region 圆角（含子 HWND 裁剪，边缘锯齿为已知代价）；
    /// 且投影留白强制归零（非分层窗投影会被窗矩形裁掉）。
    /// </summary>
    public static readonly DependencyProperty UseLayeredTransparencyProperty =
        DependencyProperty.Register(nameof(UseLayeredTransparency), typeof(bool), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(true));

    /// <summary>
    /// 是否启用 WM_GETMINMAXINFO 最大化工作区修正（缺省 **false=两宿主现状一字
    /// 不变**）。背景：WindowChrome 默认最大化盖任务栏（最大化 bounds 探出
    /// 7px/边，宿主 YoloDetector v2.10.2 实测）——可缩放弹窗（Resizeable=true
    /// 的日志/报警/调试窗）最大化时同样受影响。实现收拢在
    /// KxShellInterop.EnableMaximizedWorkAreaFix（与无边框窗基类共享同一份
    /// Win32 互操作，V2.2.1 去重），宿主有诉求时逐窗设 true 即开。
    /// </summary>
    public static readonly DependencyProperty FixMaximizedToWorkAreaProperty =
        DependencyProperty.Register(nameof(FixMaximizedToWorkArea), typeof(bool), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(false));

    public double CaptionHeight
    {
        get => (double)GetValue(CaptionHeightProperty);
        set => SetValue(CaptionHeightProperty, value);
    }

    public bool ShowTitleBar
    {
        get => (bool)GetValue(ShowTitleBarProperty);
        set => SetValue(ShowTitleBarProperty, value);
    }

    public bool Resizeable
    {
        get => (bool)GetValue(ResizeableProperty);
        set => SetValue(ResizeableProperty, value);
    }

    public double ResizeBorderThickness
    {
        get => (double)GetValue(ResizeBorderThicknessProperty);
        set => SetValue(ResizeBorderThicknessProperty, value);
    }

    public bool ShowMinButton
    {
        get => (bool)GetValue(ShowMinButtonProperty);
        set => SetValue(ShowMinButtonProperty, value);
    }

    public bool ShowMaxButton
    {
        get => (bool)GetValue(ShowMaxButtonProperty);
        set => SetValue(ShowMaxButtonProperty, value);
    }

    public bool IsMaxButtonEnabled
    {
        get => (bool)GetValue(IsMaxButtonEnabledProperty);
        set => SetValue(IsMaxButtonEnabledProperty, value);
    }

    public ICommand CloseCommand
    {
        get => (ICommand)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string? TitleIconChar
    {
        get => (string?)GetValue(TitleIconCharProperty);
        set => SetValue(TitleIconCharProperty, value);
    }

    public Geometry? TitleIcon
    {
        get => (Geometry?)GetValue(TitleIconProperty);
        set => SetValue(TitleIconProperty, value);
    }

    public Brush? TitleBackground
    {
        get => (Brush?)GetValue(TitleBackgroundProperty);
        set => SetValue(TitleBackgroundProperty, value);
    }

    public ICommand CancelCommand
    {
        get => (ICommand)GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public bool UseLayeredTransparency
    {
        get => (bool)GetValue(UseLayeredTransparencyProperty);
        set => SetValue(UseLayeredTransparencyProperty, value);
    }

    public bool FixMaximizedToWorkArea
    {
        get => (bool)GetValue(FixMaximizedToWorkAreaProperty);
        set => SetValue(FixMaximizedToWorkAreaProperty, value);
    }

    private static void OnChromeParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ChromeDialogWindow)d).ApplyChrome();

    public ChromeDialogWindow()
    {
        _layered = UseLayeredTransparency;   // 构造期定格（AllowsTransparency 只能窗口初始化前设）
        WindowStyle = WindowStyle.None;
        ResizeMode = Resizeable ? ResizeMode.CanResize : ResizeMode.NoResize;
        // 宿主级常量（KxShellDefaults）：任务栏显隐/启动定位/字体——缺省不动
        // （CureLine 现状），HJVision 启动时配置一次。
        ShowInTaskbar = KxShellDefaults.ShowInTaskbar;
        WindowStartupLocation = KxShellDefaults.StartupLocation;
        if (KxShellDefaults.FontFamily is string ff) FontFamily = new FontFamily(ff);
        if (KxShellDefaults.FontSize is double fs) FontSize = fs;
        // 窗级默认字色随主题（深色禁黑字红线）：TextBlock/Label 等未显式设色的
        // 元素沿视觉树继承 Window.Foreground。子类 XAML 显式 Foreground（本地值）
        // 会覆盖本键化——CureLine 子窗 FgMid 口径不受影响。
        SetResourceReference(ForegroundProperty, "Brush.Fresh.FgStrong");

        // —— 圆角/底色：分层窗窗自身透明（底色交给骨架卡片的 Brush.Shell.WinBg 键）——
        if (_layered)
        {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            if (KxShellDefaults.UseContentClip)
            {
                // WPF Border 圆角不裁子元素 → 根卡片内容若带实底，下两角露直角
                // （血泪见文件头）；Loaded/SizeChanged/StateChanged 统一挂圆角 Clip。
                Loaded += delegate { ApplyContentClipToCard(); };
                SizeChanged += delegate { ApplyContentClipToCard(); };
                StateChanged += delegate { ApplyContentClipToCard(); };
            }
        }
        else
        {
            // 非分层窗（含 WindowsFormsHost 的画布窗）：窗自身挂主题底，
            // 四角圆角走 SetWindowRgn 物理裁剪（物理 Region 含子 HWND）。
            SetResourceReference(BackgroundProperty, "Brush.Fresh.WinBg");
            Loaded += delegate { ApplyRoundWindowRegion(this); };
            SizeChanged += delegate { ApplyRoundWindowRegion(this); };
        }
        StateChanged += delegate { ApplyMaximizedShell(); };
        // 隐式样式兜底注入不放构造函数（V0.9.52 六窗 XamlParseException 同案，
        // 血泪见文件头）——统一 OnInitialized（XAML 已解析完，索引器=覆盖语义）。

        ApplyChrome();
        BuildSkeleton();
        Content = _skeleton;
    }

    /// <summary>
    /// ESC→Cancel：有 CancelCommand 且可执行就走命令（HJVision 口径：VM 回滚/
    /// 清理），缺省 null=无行为（CureLine 现状不变）。
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape) return;
        ICommand cmd = CancelCommand;
        if (cmd != null && cmd.CanExecute(null))
        {
            cmd.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 句柄创建后按开关挂最大化工作区修正钩子（缺省 false=现状不变；
    /// 实现见 KxShellInterop.EnableMaximizedWorkAreaFix，钩子随 HwndSource
    /// 销毁自动失效，无需显式移除）。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (FixMaximizedToWorkArea)
        {
            KxShellInterop.EnableMaximizedWorkAreaFix(this);
        }
    }

    /// <summary>
    /// 窗级隐式样式注入的时机：**OnInitialized**（InitializeComponent 末尾）。
    /// 【为什么不能放构造函数】构造函数早于 InitializeComponent 执行，此时窗体
    /// Resources 还是空的；注入后 XAML 里的 Window.Resources 若也声明了同名隐式
    /// 样式，XAML 解析器往字典里 Add 同键会直接抛 XamlParseException"已添加项"
    /// （V0.9.52 目检 harness 实测 6 窗开不起来）。放到 OnInitialized：XAML 已
    /// 解析完，注入即覆盖，语义与窗体自己声明的一致。键表取不到时静默跳过
    /// （独立 harness 未并键板不影响本窗弹出）。
    /// </summary>
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        InjectImplicitStyles(this, KxShellDefaults.ImplicitStyleKeys);
    }

    /// <summary>
    /// 静态隐式样式注入（键表格式 "类型=键名" CSV，如
    /// "Button=KxDialogButtonImplicit,TextBox=KxDialogTextBoxImplicit"）：
    /// 类型名按 System.Windows.Controls 的控件短名解析（Button/ComboBox/TextBox/
    /// PasswordBox/DataGrid/ListBox/GroupBox…任意带无参缺省样式的控件）。
    /// 供不继承本基类的窗借用（原 SunnyDialog.InjectDialogStyles 的参数化版）。
    /// </summary>
    public static void InjectImplicitStyles(Window w, string? keyTable)
    {
        if (w == null || string.IsNullOrWhiteSpace(keyTable)) return;
        try
        {
            foreach (var pair in keyTable.Split(','))
            {
                var seg = pair.Split('=');
                if (seg.Length != 2) continue;
                var style = Application.Current?.Resources[seg[1].Trim()] as Style;
                if (style == null) continue;   // 缺键静默跳过（独立 harness 场景）
                var targetType = style.TargetType;
                if (targetType != null) w.Resources[targetType] = style;
            }
            // 窗级字色随主题（深色禁黑字红线，与基类构造同口径）
            w.SetResourceReference(ForegroundProperty, "Brush.Fresh.FgStrong");
        }
        catch { /* 注入失败不影响开窗（样式兜底是锦上添花） */ }
    }

    /// <summary>
    /// 组装窗骨架：根 Grid（KxShellDefaults.ShadowMargin 投影留白）→ 圆角卡片
    /// Border（底/描边/圆角/投影全语义键 DynamicResource——宿主键板缺失时静默
    /// 空白，与 XAML 手抄同语义）→ DockPanel（标题条钉顶 + 内容槽填充）。子类
    /// XAML 的根内容落 _contentHost（OnContentChanged 转移）。
    /// </summary>
    private void BuildSkeleton()
    {
        var root = new Grid { Margin = KxShellDefaults.ShadowMargin };
        if (!_layered) root.Margin = new Thickness(0);   // 非分层窗投影会被裁，强制贴边

        var card = new Border { BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Fresh.WinBg");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Fresh.Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "Fn.ShellRadius");
        card.SetResourceReference(Border.EffectProperty, "Fx.WinShadow");
        _card = card;

        var dock = new DockPanel();

        // 标题条：共用 TitleBar 控件（—/▢/× 显隐、副题、图标全部 DP 转发），高度绑
        // CaptionHeight（DP 变更跟随）。ShowTitleBar=false 时不装配（自绘标题栏窗，
        // WindowChrome.CaptionHeight 仍按 CaptionHeight DP 给自绘条命中拖动区）。
        if (ShowTitleBar)
        {
            var bar = new TitleBar();
            bar.SetBinding(TitleBar.ShowMinButtonProperty, new Binding(nameof(ShowMinButton)) { Source = this });
            bar.SetBinding(TitleBar.ShowMaxButtonProperty, new Binding(nameof(ShowMaxButton)) { Source = this });
            bar.SetBinding(TitleBar.IsMaxButtonEnabledProperty, new Binding(nameof(IsMaxButtonEnabled)) { Source = this });
            bar.SetBinding(TitleBar.CloseCommandProperty, new Binding(nameof(CloseCommand)) { Source = this });
            bar.SetBinding(TitleBar.SubtitleProperty, new Binding(nameof(Subtitle)) { Source = this });
            bar.SetBinding(TitleBar.TitleIconCharProperty, new Binding(nameof(TitleIconChar)) { Source = this });
            bar.SetBinding(TitleBar.TitleIconProperty, new Binding(nameof(TitleIcon)) { Source = this });
            bar.SetBinding(TitleBar.TitleBackgroundProperty, new Binding(nameof(TitleBackground)) { Source = this });
            bar.SetBinding(TitleBar.HeightProperty, new Binding(nameof(CaptionHeight)) { Source = this });
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            _titleBar = bar;
        }

        // 内容槽：子类 XAML 根内容落这里（OnContentChanged 转移）
        _contentHost = new ContentPresenter();
        dock.Children.Add(_contentHost);

        card.Child = dock;
        root.Children.Add(card);
        _skeleton = root;
    }

    /// <summary>把 WindowChrome 参数与 DP 对齐（构造/DP 回调双入口）：
    /// 共享构造收拢在 KxShellInterop.BuildChrome（V2.2.1 与无边框窗基类
    /// ChromeBorderlessWindow 去重——库口径 GlassFrameThickness=0 /
    /// CornerRadius=0 / UseAeroCaptionButtons=false 固定），本基类只补弹窗
    /// 特有逻辑：CaptionHeight=DP（>0 时标题条落在系统 caption 区内，空白可拖）；
    /// 定高弹窗缩放边 0 / 可缩放窗取 DP（6 是库口径缺省，个别窗 4）。</summary>
    private void ApplyChrome()
    {
        WindowChrome.SetWindowChrome(this, KxShellInterop.BuildChrome(
            CaptionHeight, Resizeable ? ResizeBorderThickness : 0));
    }

    /// <summary>
    /// 子类内容转移：子类 XAML 设 Content（根元素）时，把骨架还原为窗内容、
    /// 子类内容移入骨架内容槽——子类 XAML 因此可以像普通 Window 一样写根
    /// 内容，骨架零感知。_restoringContent 闸防还原赋值递归触发本方法。
    /// </summary>
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (_restoringContent || _contentHost == null || _skeleton == null)
            return;
        if (ReferenceEquals(newContent, _skeleton) || ReferenceEquals(newContent, _contentHost))
            return;
        _restoringContent = true;
        try
        {
            Content = _skeleton;                 // 还原骨架（触发本方法但被闸短路）
            _contentHost.Content = newContent;   // 子类内容进槽
        }
        finally
        {
            _restoringContent = false;
        }
    }

    /// <summary>
    /// 最大化态跟随：卡片圆角与勾边归零（铺满屏幕不留四角透明弧——Gallery/
    /// Settings 类常最大化窗必须处理，原 ChromeDialogRootBorder 样式 trigger 的
    /// 基类内建版）+ 标题条顶角归零（TitleBar.ApplyMaximizedState）+ 内容 Clip
    /// 归零（ApplyContentClipToCard 内含 WindowState 判定）。
    /// </summary>
    private void ApplyMaximizedShell()
    {
        if (_card == null) return;
        bool max = WindowState == WindowState.Maximized;
        if (max)
        {
            _card.CornerRadius = new CornerRadius(0);
            _card.BorderThickness = new Thickness(0);
        }
        else
        {
            _card.SetResourceReference(Border.CornerRadiusProperty, "Fn.ShellRadius");
            _card.BorderThickness = new Thickness(1);
        }
        _titleBar?.ApplyMaximizedState(max);
    }

    /// <summary>
    /// 分层窗内容圆角裁剪（宿主 HJVision 四角全圆轮根治方案，骨架内建版）：
    /// 根卡片 Border 的 Child（DockPanel 内容容器）若带实底背景，直角铺满会把
    /// 卡片下两角的透空月牙盖白——对 Child 挂 RectangleGeometry 圆角 Clip。
    /// 几何：Child 原点=内容区原点（外框 (1,1) 起），半径=外弧−1px 边框内缘弧，
    /// Clip 弧与边框内缘弧完全重合——角部既透空又不露底色缝。最大化归零
    /// （还原后 SizeChanged/StateChanged 重挂恢复）。防御：尺寸未布局（0）时
    /// 跳过等下次 SizeChanged。
    /// </summary>
    private void ApplyContentClipToCard()
    {
        try
        {
            if (_card?.Child is not FrameworkElement child) return;
            double cw = child.ActualWidth, ch = child.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            double r = 0;
            if (WindowState != WindowState.Maximized)
            {
                object? cr = Application.Current?.TryFindResource("Fn.ShellRadius");
                double outer = (cr is CornerRadius corner) ? corner.TopLeft : 10;
                int thick = _card.BorderThickness.Left > 0 ? 1 : 0;
                r = Math.Max(0, outer - thick);
            }
            child.Clip = new RectangleGeometry(new Rect(0, 0, cw, ch), r, r);
        }
        catch { }
    }

    /// <summary>
    /// 分层窗内容圆角裁剪（公开静态，供不继承本基类的直连窗借用——RecordViewer/JobEdit
    /// 等"窗根 Border + 圆角"形态：对 w.Content as Border 的 Child 挂圆角 Clip，逻辑与
    /// 实例版 ApplyContentClipToCard 同源）。基类派生窗走实例版（操作骨架卡片）。
    /// </summary>
    public static void ApplyContentClip(Window w)
    {
        try
        {
            if (w?.Content is not Border root) return;
            if (root.Child is not FrameworkElement child) return;
            double cw = child.ActualWidth, ch = child.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            double r = 0;
            if (w.WindowState != WindowState.Maximized)
            {
                object? cr = Application.Current?.TryFindResource("Fn.ShellRadius");
                double outer = (cr is CornerRadius corner) ? corner.TopLeft : 10;
                int thick = root.BorderThickness.Left > 0 ? 1 : 0;
                r = Math.Max(0, outer - thick);
            }
            child.Clip = new RectangleGeometry(new Rect(0, 0, cw, ch), r, r);
        }
        catch { }
    }

    /// <summary>
    /// 非分层窗的物理 Region 圆角（Loaded 后调用；公开静态——含 HWND 的直连
    /// Window 非本基类派生时也可借用）：CreateRoundRectRgn 按物理像素（DIP×DPI）
    /// 裁窗，圆角直径取 Fn.ShellRadius.TopLeft×2。固定尺寸窗 Loaded 一次即可，
    /// SizeChanged 兜底重裁。
    /// </summary>
    public static void ApplyRoundWindowRegion(Window w)
    {
        try
        {
            var dpiInfo = VisualTreeHelper.GetDpi(w);
            double r = 10;
            object? cr = Application.Current?.TryFindResource("Fn.ShellRadius");
            if (cr is CornerRadius corner) r = corner.TopLeft;
            double dpi = dpiInfo.PixelsPerDip;
            int wd = (int)Math.Ceiling(w.ActualWidth * dpi) + 1;
            int ht = (int)Math.Ceiling(w.ActualHeight * dpi) + 1;
            int rr = Math.Max(2, (int)(r * dpi));
            IntPtr hRgn = CreateRoundRectRgn(0, 0, wd, ht, rr * 2, rr * 2);
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero) SetWindowRgn(hwnd, hRgn, true);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
}
