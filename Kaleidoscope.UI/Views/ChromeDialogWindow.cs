// =========================================================================
// ChromeDialogWindow：圆角自绘弹窗基类（宿主 CureLine V0.0.109 反哺沉库）
//
// 【干什么】把各宿主"每个弹窗手抄一遍"的圆角窗骨架收成一个 Window 基类——
//   WindowStyle=None + WindowChrome（CaptionHeight 命中拖动 / 可缩放边）
//   + 透明分层窗 + 根 Grid Margin=14（投影渲染留白）+ 圆角 10 卡片 Border
//   （Brush.Fresh.WinBg 底 / Brush.Fresh.Border 描边 / Fx.WinShadow 投影）
//   + 品牌标题条（Brush.HeaderGrad 渐变底 + TopbarBorder 底边线 + 顶部
//   圆角 10 与外框对齐）+ 标题文本（绑 Window.Title）+ ✕ 关闭钮（悬停
//   红换白字 Win32 惯例）。子类 XAML 的根内容自动进入骨架内容槽。
//
// 【为什么】宿主 CureLine 单仓 26 个弹窗手抄同一段骨架 XAML（逐窗 ~15 行），
//   圆角/标题条高/投影参数全靠人肉对齐必然漂移；HuaJiVision-WPF 的
//   SunnyDialog 已验证过"圆角窗基类"路线（其血泪：隐式样式注入时机必须在
//   OnInitialized、圆角裁内容要 ContentClip——本类按 CureLine 现行 26 窗
//   的"无 Clip、无隐式注入"口径实现，迁移后与手抄窗逐像素一致，两坑绕行）。
//
// 【怎么接】窗 XAML 把根元素类名改为本类（x:Class 不动、命名空间换成
//   Kaleidoscope.UI.Views），其余零改动：
//     <ui:ChromeDialogWindow x:Class="CureStation.MesPostConfigWindow" ...
//         xmlns:ui="clr-namespace:Kaleidoscope.UI.Views;assembly=Kaleidoscope.UI"
//         Title="过账配置" Width="720" Height="560"
//         Resizeable="True">          ← 可缩放窗才写（缺省 false=定高弹窗，
//                                       ResizeBorderThickness 0/6 与手抄口径一致）
//       …（原根 Grid/内容原样保留——自动进骨架内容槽）
//     </ui:ChromeDialogWindow>
//   code-behind 的 Window 基类同步改：public partial class MesPostConfigWindow
//   : ChromeDialogWindow。原手抄骨架（根 Grid Margin/圆角 Border/WinTitleBar/
//   WinTitleClose）从 XAML 里删除。宿主须先合并 ChromeStyles.xaml 或提供
//   语义键板（本类只引 DynamicResource 语义键，缺键=静默空白）。
//
// 【线程】UI 线程构造/使用（WPF 常规纪律）。
// =========================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace Kaleidoscope.UI.Views;

public class ChromeDialogWindow : Window
{
    // ---------- 骨架成员 ----------
    private ContentPresenter? _contentHost;   // 子类内容槽（标题条之下的填充区）
    private Grid? _skeleton;                  // 窗骨架根（Margin 14 + 卡片 Border）
    private bool _restoringContent;           // OnContentChanged 重入闸

    // ---------- 参数 DP（缺省=弹窗口径：标题条 34、不可缩放） ----------

    /// <summary>标题条命中拖动区高度（兼标题条 Border 高度）。缺省 34=库口径
    /// （与 WinTitleBar 样式同源；主窗品牌条 44 是主窗基类的事，不归本类）。</summary>
    public static readonly DependencyProperty CaptionHeightProperty =
        DependencyProperty.Register(nameof(CaptionHeight), typeof(double), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(34.0, OnChromeParamChanged));

    /// <summary>true=可缩放窗（ResizeBorderThickness=6，如日志/报警/调试窗）；
    /// false=定高弹窗（0）。缺省 false——与手抄口径逐窗对应。</summary>
    public static readonly DependencyProperty ResizeableProperty =
        DependencyProperty.Register(nameof(Resizeable), typeof(bool), typeof(ChromeDialogWindow),
            new FrameworkPropertyMetadata(false, OnChromeParamChanged));

    public double CaptionHeight
    {
        get => (double)GetValue(CaptionHeightProperty);
        set => SetValue(CaptionHeightProperty, value);
    }

    public bool Resizeable
    {
        get => (bool)GetValue(ResizeableProperty);
        set => SetValue(ResizeableProperty, value);
    }

    private static void OnChromeParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ChromeDialogWindow)d).ApplyChrome();

    public ChromeDialogWindow()
    {
        // 透明分层窗三件套（与手抄口径一致）：无边框 + 透明底 + 分层允许
        //（AllowsTransparency 是 Fx.WinShadow 浮层投影的前提——非分层窗投影
        // 会被窗口矩形裁掉）
        WindowStyle = WindowStyle.None;
        Background = Brushes.Transparent;
        AllowsTransparency = true;
        ApplyChrome();
        BuildSkeleton();
        Content = _skeleton;
    }

    /// <summary>
    /// 组装窗骨架：根 Grid（Margin 14 投影留白）→ 圆角卡片 Border（底/描边/
    /// 投影全语义键 DynamicResource——宿主键板缺失时静默空白，与 XAML 手抄
    /// 同语义）→ DockPanel（标题条钉顶 + 内容槽填充）。标题文本绑 Window.Title
    ///（子类设 Title 即随动，与手抄窗 RelativeSource 绑定同效）。
    /// 视觉结构与 ChromeStyles.xaml 的手抄骨架逐属性一致——两边改口径必须同步。
    /// </summary>
    private void BuildSkeleton()
    {
        var root = new Grid { Margin = new Thickness(14) };

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(BackgroundProperty, "Brush.Fresh.WinBg");
        card.SetResourceReference(BorderBrushProperty, "Brush.Fresh.Border");
        card.SetResourceReference(EffectProperty, "Fx.WinShadow");

        var dock = new DockPanel();

        // 品牌标题条：HeaderGrad 渐变底 + TopbarBorder 底边线 + 顶角圆 10；
        // 高度绑 CaptionHeight（DP 变更跟随）——与 WinTitleBar 样式同口径
        var titleBar = new Border
        {
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 0, 0, 0),
        };
        titleBar.SetResourceReference(BackgroundProperty, "Brush.HeaderGrad");
        titleBar.SetResourceReference(BorderBrushProperty, "Brush.TopbarBorder");
        titleBar.SetBinding(HeightProperty, new Binding(nameof(CaptionHeight)) { Source = this });

        var titleDock = new DockPanel();
        // ✕ 钉右上（与手抄骨架 HorizontalAlignment=Right 同位）——DockPanel
        // 子元素按加入顺序停靠，CloseButton 先加入必须显式 Right，否则停靠
        // 缺省 Left（首拍目检抓到的偏差：✕ 跑到标题条左端）
        var closeButton = BuildCloseButton();
        DockPanel.SetDock(closeButton, Dock.Right);
        titleDock.Children.Add(closeButton);

        var titleText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TopbarFg");
        titleText.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        titleDock.Children.Add(titleText);

        titleBar.Child = titleDock;
        DockPanel.SetDock(titleBar, Dock.Top);
        dock.Children.Add(titleBar);

        // 内容槽：子类 XAML 根内容落这里（OnContentChanged 转移）
        _contentHost = new ContentPresenter();
        dock.Children.Add(_contentHost);

        card.Child = dock;
        root.Children.Add(card);
        _skeleton = root;
    }

    /// <summary>
    /// ✕ 关闭钮：44×34、悬停红（Fresh.CloseHoverBg）换白字（Win32 惯例）、
    /// chrome 命中豁免（落 caption 拖动区外才点得到）、Click=Close。
    /// hover 用 MouseEnter/Leave 直接改属性（与 WinTitleClose 样式触发器等价）；
    /// 悬停色 TryFindResource 取宿主键板值，缺键兜底 #E81123（Win32 惯例色，
    /// 与缺省键板同值——键缺失视觉也不变）。
    /// </summary>
    private Button BuildCloseButton()
    {
        var btn = new Button
        {
            Content = "✕",
            Width = 44,
            Height = 34,
            FontSize = 14,
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = Brushes.Transparent,
        };
        btn.SetResourceReference(ForegroundProperty, "Brush.TopbarFg");
        WindowChrome.SetIsHitTestVisibleInChrome(btn, true);
        btn.Click += (_, _) => Close();
        btn.MouseEnter += (_, _) =>
        {
            btn.Background = TryFindUiBrush("Brush.Fresh.CloseHoverBg")
                ?? new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));
            btn.Foreground = Brushes.White;
        };
        btn.MouseLeave += (_, _) =>
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

    /// <summary>把 WindowChrome 参数与 DP 对齐（构造/DP 回调双入口）。</summary>
    private void ApplyChrome()
    {
        var chrome = new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
            // 定高弹窗 0 / 可缩放窗 6——与手抄口径一致（6 是库缩放边宽缺省）
            ResizeBorderThickness = new Thickness(Resizeable ? 6 : 0),
        };
        WindowChrome.SetWindowChrome(this, chrome);
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
}
