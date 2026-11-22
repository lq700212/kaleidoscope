// =========================================================================
// ChromeBorderlessWindow：无边框圆角窗基类——纯行为骨架（V2.2.0 宿主
// YoloDetector v2.10.2/v2.10.3 实测口径反哺沉库）
//
// 【宿主 UI 开工必读（人 + AI Agent 通用路标）】宿主写任何主窗/控件前，先读
//   Kaleidoscope.UI/README.md 的「控件复用索引（按场景查）」——窗骨架/标题条/三窗钮/
//   导航胶囊/表单件全有现成封装，禁重复造轮子（库内有直接用 / 近似 BasedOn /
//   确无才自绘）。本类是宿主建主窗的必经基类，在此设路标让任何工具的 Agent 都绕不开。
//
// 【干什么】承接"主窗/自绘标题栏窗"形态的无边框骨架行为，与 ChromeDialogWindow
//   （弹窗形态，键板视觉一体）并列成对：
//     · WindowStyle=None + AllowsTransparency + Background=Transparent
//       （分层真圆角三件套，视觉圆角由子类根 Border 提供）；
//     · WindowChrome 外壳（CaptionHeight 命中拖动区 / 可缩放边 /
//       GlassFrameThickness=0 / UseAeroCaptionButtons=false）；
//     · 根 Border 圆角 Clip 同步（等效设计稿 overflow:hidden，裁负 Margin
//       溢出内容；最大化归零、还原恢复，几何实例复用零分配）；
//     · WM_GETMINMAXINFO 最大化工作区修正（ChromeDialogWindow 没有的能力，
//       主窗/可最大化窗与弹窗的本质差异，见下【最大化红线】）。
//   **零键板依赖**：本类不引用任何 Brush.*/Fn.* 语义键、不插任何视觉元素——
//   底色/圆角/描边/顶栏全部由子类 XAML 根 Border 自绘（玻璃拟态/Chrome 风/
//   任意视觉体系均可）。子类宿主无需合并库字典即可使用。
//
// 【与 ChromeDialogWindow 怎么选】
//   · 弹窗（带库标题条+Fresh 键板卡片骨架）→ ChromeDialogWindow；
//   · 主窗/自绘标题栏窗（顶栏是宿主业务内容、视觉自成体系）→ 本类；
//   · 本类刻意不做：标题条/系统三钮（各宿主样式差异大，留宿主自绘——
//     YoloDetector 玻璃拟态圆钮、CureLine 未来主窗基类各自提供）；
//     非分层（SetWindowRgn）分支（含 HWND 的窗请勿用本类，见红线）。
//
// 【分层窗 HWND 红线（与 ChromeDialogWindow.UseLayeredTransparency 同源血泪）】
//   本类强制 AllowsTransparency=true（分层窗口 WS_EX_LAYERED）——分层窗里
//   内嵌的 HWND（WindowsFormsHost/VisionPro 显示区等）不渲染，画面整块消失
//   （2026-10-30 harness 实测：PrintWindow 抓非分层窗红块 561 像素、分层窗 0）。
//   **内嵌 HWND 的窗禁止继承本类**，走 ChromeDialogWindow 的
//   UseLayeredTransparency=false 形态或手抄骨架。
//
// 【最大化红线（YoloDetector v2.10.2 实测沉淀，本类独有的主窗能力）】
//   现象：WindowChrome 对 WM_GETMINMAXINFO 的默认处理把最大化尺寸扩到整个
//   显示器（desktop 实测最大化 bounds = 1934×1094 @(-7,-7)，屏幕 1920×1080）
//   ——底部盖住任务栏，四周探出的 7px 藏缩放热区。
//   修复：窗口过程钩子把最大化目标改写为最近显示器的工作区（不含任务栏），
//   handled=true 阻止 DefWindowProc 用显示器全尺寸覆盖。实现收拢在
//   KxShellInterop.EnableMaximizedWorkAreaFix（V2.2.1 与弹窗基类共享同一份，
//   弹窗基类经 FixMaximizedToWorkArea DP 缺省关、诉求到了一行配置即开），
//   本类开关 FixMaximizedToWorkArea 缺省 true（宿主红线修复）。
//
// 【怎么接】子类 XAML 根元素类名改为本类、根内容包一层圆角 Border 即可：
//     <ui:ChromeBorderlessWindow x:Class="YourApp.MainWindow"
//         xmlns:ui="clr-namespace:Kaleidoscope.UI.Views;assembly=Kaleidoscope.UI"
//         CaptionHeight="62" CornerRadius="14" ...>
//       <Border CornerRadius="14">  ← 根 Border：视觉圆角与溢出裁剪的载体
//         …（顶栏/内容全部自绘）
//       </Border>
//     </ui:ChromeBorderlessWindow>
//   约定：Window.Content 是 Border（基类按 Content as Border 解析根框架）；
//   根 Border 的 CornerRadius 写值应与 CornerRadius DP 一致（基类在最大化/
//   还原时直接改写根 Border 的本地值，还原恢复取 DP 值）。WindowStyle/
//   AllowsTransparency/Background/WindowChrome 由基类构造设置，子类 XAML
//   不要重复声明。code-behind 基类同步改。
//
// 【口径来源】全部行为=YoloDetector 生产验证（v2.10.2 无边框+最大化工作区钩子、
//   v2.10.3 真圆角三件套+Clip 同步，宿主回归 harness 用例
//   "UI-无边框窗口WindowChrome配置" 锁死），值一字未动。
//
// 【线程】UI 线程构造/使用（WPF 常规纪律）。
// =========================================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace Kaleidoscope.UI.Views;

public class ChromeBorderlessWindow : Window
{
    // ---------- 骨架成员 ----------
    private Border? _rootFrame;              // 子类根 Border（视觉圆角与 Clip 载体；约定 Window.Content as Border）
                                             // ——OnContentChanged（XAML 解析完）时赋值；使用处均已判空，缺省 null=纯壳降级
    private readonly RectangleGeometry _rootClip = new RectangleGeometry();  // 复用的裁剪几何（拖拽缩放高频触发零分配）

    // ---------- 参数 DP（缺省=库口径：命中区 34、缩放边 6、圆角 10、钩子开） ----------

    /// <summary>WindowChrome 标题区（命中拖动/双击最大化/贴边分屏）高度。
    /// 缺省 34=库弹窗口径；自绘标题栏窗按宿主顶栏高度设（YoloDetector=62 玻璃条）。</summary>
    public static readonly DependencyProperty CaptionHeightProperty =
        DependencyProperty.Register(nameof(CaptionHeight), typeof(double), typeof(ChromeBorderlessWindow),
            new FrameworkPropertyMetadata(34.0, OnChromeParamChanged));

    /// <summary>边缘缩放命中区宽度（缺省 6=库口径；0=不可缩放）。</summary>
    public static readonly DependencyProperty ResizeBorderThicknessProperty =
        DependencyProperty.Register(nameof(ResizeBorderThickness), typeof(double), typeof(ChromeBorderlessWindow),
            new FrameworkPropertyMetadata(6.0, OnChromeParamChanged));

    /// <summary>
    /// 窗口圆角半径（还原态口径）。缺省 10=库 Fn.ShellRadius 口径；子类按设计稿设
    /// （YoloDetector=14）。仅作为"还原恢复值"与 Clip 半径来源——视觉圆角由子类根
    /// Border 自绘（其 CornerRadius 应与本 DP 一致，见类头【怎么接】）。
    /// </summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(ChromeBorderlessWindow),
            new FrameworkPropertyMetadata(10.0, OnCornerRadiusChanged));

    /// <summary>
    /// 是否启用 WM_GETMINMAXINFO 最大化工作区修正（缺省 true=YoloDetector 实测红线修复）。
    /// 仅多显示器下最大化落在次屏等极端场景需要关（实测未见），一般不要动。
    /// </summary>
    public static readonly DependencyProperty FixMaximizedToWorkAreaProperty =
        DependencyProperty.Register(nameof(FixMaximizedToWorkArea), typeof(bool), typeof(ChromeBorderlessWindow),
            new FrameworkPropertyMetadata(true));

    public double CaptionHeight
    {
        get => (double)GetValue(CaptionHeightProperty);
        set => SetValue(CaptionHeightProperty, value);
    }

    public double ResizeBorderThickness
    {
        get => (double)GetValue(ResizeBorderThicknessProperty);
        set => SetValue(ResizeBorderThicknessProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public bool FixMaximizedToWorkArea
    {
        get => (bool)GetValue(FixMaximizedToWorkAreaProperty);
        set => SetValue(FixMaximizedToWorkAreaProperty, value);
    }

    private static void OnChromeParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ChromeBorderlessWindow)d).ApplyChrome();

    /// <summary>圆角 DP 变化：还原态下同步根 Border 视觉圆角并重挂 Clip。</summary>
    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var w = (ChromeBorderlessWindow)d;
        if (w.WindowState != WindowState.Maximized && w._rootFrame != null)
        {
            w._rootFrame.CornerRadius = new CornerRadius(w.CornerRadius);
        }
        w.UpdateRootClip();
    }

    public ChromeBorderlessWindow()
    {
        // —— 分层真圆角三件套（构造期定格：AllowsTransparency 只能窗口初始化前设）——
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;   // 四角透出桌面，视觉全部交给子类根 Border

        ApplyChrome();

        // 根 Border 解析与联动：子类 XAML 设 Content 后取根 Border（本类不插骨架，
        // 与 ChromeDialogWindow 的内容槽转移机制不同——子类根内容就是窗内容原样）。
        // Clip 联动：首帧布局（Loaded）/拖拽缩放（SizeChanged）/最大化切换（StateChanged）
        // 三入口统一重挂（几何实例复用，见 UpdateRootClip）。
        StateChanged += delegate
        {
            ApplyMaximizedCorner();
            UpdateRootClip();
        };
    }

    /// <summary>
    /// 根 Border 解析（OnContentChanged）：子类 XAML 的根元素落定后解析
    /// Window.Content as Border 存 _rootFrame，并挂元素级 SizeChanged（首帧
    /// 布局/拖拽缩放触发）。Content 不是 Border 时静默跳过（本类能力降级为
    /// 纯 WindowChrome 壳，Clip/圆角联动自动失效——不抛异常便于渐进接入）。
    /// </summary>
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        _rootFrame = newContent as Border;
        if (_rootFrame != null)
        {
            _rootFrame.SizeChanged += delegate { UpdateRootClip(); };
        }
    }

    /// <summary>WindowChrome 外壳参数与 DP 对齐（构造/DP 回调双入口）：共享构造
    /// 收拢在 KxShellInterop.BuildChrome（库口径：GlassFrameThickness=0 去顶部
    /// 透明系统框残留 / CornerRadius=0 不产生视觉圆角 / UseAeroCaptionButtons=
    /// false 不叠加系统三钮），本类只传 CaptionHeight（顶栏空白即标题区）与
    /// 缩放边两个参数。</summary>
    private void ApplyChrome()
    {
        WindowChrome.SetWindowChrome(this, KxShellInterop.BuildChrome(CaptionHeight, ResizeBorderThickness));
    }

    /// <summary>
    /// 最大化/还原圆角联动：最大化清零根 Border 圆角（贴满工作区不留透明角），
    /// 还原恢复 CornerRadius DP 值。直接改写根 Border 本地值——子类 XAML 里
    /// 写的 CornerRadius 应与本 DP 一致（口径见类头【怎么接】）。
    /// 【与弹窗基类语义不同，勿合并】ChromeDialogWindow.ApplyMaximizedShell
    /// 操作的是库骨架 card Border（CornerRadius+BorderThickness 归零+标题条
    /// 顶角），本类操作的是子类根 Border——目标对象与联动面不同，各自实现。
    /// </summary>
    private void ApplyMaximizedCorner()
    {
        if (_rootFrame == null) return;
        bool max = WindowState == WindowState.Maximized;
        _rootFrame.CornerRadius = new CornerRadius(max ? 0 : CornerRadius);
    }

    /// <summary>
    /// 根 Border 圆角矩形 Clip 同步（等效设计稿 overflow:hidden：光斑/负 Margin
    /// 溢出的装饰元素被裁进圆角内，不会画到四角外）。半径与根 Border.CornerRadius
    /// 始终一致（最大化=0、还原=DP 值），不会出现"背景圆角、内容直角溢出"的错位。
    /// 几何实例复用（拖拽缩放时 SizeChanged 高频触发，只改 Rect/Radius 零分配）；
    /// 尺寸未布局（0）时跳过等下次触发。WPF Border.CornerRadius 只裁自身背景
    /// 不裁子元素，Clip 是子元素溢出内容的唯一约束。
    /// 【与弹窗基类语义不同，勿合并】ChromeDialogWindow 的 Clip 是"卡片内容
    /// 内缘裁剪"（card 带 1px 描边、Clip 挂 Child、半径=外弧−1px、低频每次
    /// 新建几何）；本类是"根框架整体裁剪"（无描边、Clip 挂根 Border 自身、
    /// 半径=DP 全值、高频复用几何实例）——挂载位置与半径口径均不同。
    /// </summary>
    private void UpdateRootClip()
    {
        if (_rootFrame == null) return;
        double wd = _rootFrame.ActualWidth, ht = _rootFrame.ActualHeight;
        if (wd <= 0 || ht <= 0) return;     // 未布局，等 SizeChanged 再来
        double radius = WindowState == WindowState.Maximized ? 0 : CornerRadius;
        _rootClip.Rect = new Rect(0, 0, wd, ht);
        _rootClip.RadiusX = radius;
        _rootClip.RadiusY = radius;
        _rootFrame.Clip = _rootClip;
    }

    // ============================================================
    // WM_GETMINMAXINFO 最大化工作区修正（YoloDetector v2.10.2 实测红线）
    //   实现已收拢在 KxShellInterop.EnableMaximizedWorkAreaFix（与弹窗基类
    //   共享同一份 Win32 互操作，V2.2.1 去重），本类只在开关开启时挂钩子。
    //   红线背景：WindowChrome 默认最大化盖任务栏（bounds 探出 7px/边），
    //   钩子把最大化目标改写为最近显示器工作区，详见 KxShellInterop 类头。
    // ============================================================

    /// <summary>句柄创建后挂窗口过程钩子（WindowChrome 的钩子先注册，本钩子后写入值生效）。
    /// 钩子随 HwndSource 销毁自动失效（窗口关闭即断，无需显式移除）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (FixMaximizedToWorkArea)
        {
            KxShellInterop.EnableMaximizedWorkAreaFix(this);
        }
    }
}
