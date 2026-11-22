using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Kaleidoscope.UI.Views;

/// <summary>
/// KxImageStage —— 取像"舞台"容器控件【V2.3.5 装饰，V2.3.6 内置帧显示管线，
/// V2.3.7 叠加画布可交互修复】：相机画面
/// 显示区的主题舞台底 + 网格底纹 + 主色扫描光带动效（工业视觉的经典视觉语言），一行
/// XAML 替换普通 Grid 即得；喂帧场景连 Image/空态/叠加画布都不用宿主装配（傻瓜式）。
///
/// 【干什么】宿主 AIris 反哺（docs/KxImageStage取像舞台控件方案.md）：取像区以前是无背景
///   Grid，深色模式下透出面板色、没有"相机舞台"观感；设计稿口径为舞台底 + 18px 双向
///   网格 + 整面扫描光带（4.2s linear infinite，上→下循环）。本控件把三件装饰收拢：
///   [ZIndex=-2] 舞台底 Border（StageBackground）→ [ZIndex=-1] 网格 Rectangle
///   （DrawingBrush Tile 平铺，GridLineBrush + GridStep）→ [默认 0] 宿主子内容
///   → [ZIndex=5] 叠加画布 Overlay（检测框等宿主自绘，画在画面上、扫描带下）
///   → [ZIndex=10] 扫描光带 Rectangle（ScanBrush + TranslateTransform.Y 循环动画）。
///   层序全 SetZIndex 锁死，不依赖 Children 添加顺序。
///
/// 【V2.3.6 帧显示管线（对标 HJVision VisionPreviewHost 的傻瓜式口径，纯 WPF 版）】
///   控件内置 帧图 Image / 空态文本 / 叠加画布 三件，宿主一行绑定即得完整取像显示：
///   ①绑定式（MVVM）：&lt;ui:KxImageStage FrameSource="{Binding FrameImage}" IdleText="等待画面…"/&gt;
///   ②命令式（事件流）：任意线程调 ShowFrame(BitmapSource)/ClearFrame()（内部 Dispatcher
///     封送，帧线程直调不崩；与 HJVision"检测线程直调 VM 方法"同口径）。
///   有帧自动隐藏空态、无帧自动回空态文本（对标"出图瞬间网格让位/空闲网格底纹"）；
///   【扫描联动（V2.3.6，封装内建，宿主零接线）】有帧自动停扫描动效、清帧回空态自动
///   恢复——扫描只属于"初始空态"，画面/视频不被光带干扰（有效开关内含 FrameSource==null）。
///   【二选一红线】同一实例只能用一种喂帧方式：命令式 ShowFrame/ClearFrame 走本地值
///   会压死 FrameSource 绑定（ClearFrame 用 ClearValue 恢复绑定，但连发帧期间绑定
///   不生效）；混用 = 画面来源混乱，宿主自担。
///   【纯装饰用法零破坏】不喂帧 = 帧图/空态/叠加三件不可见，本控件仍是普通装饰
///   Grid，宿主子元素照旧落 Children（V2.3.5 用法原样可用）。
///
/// 【叠加画布】public Canvas Overlay 取用（ZIndex=5：画面上、扫描带下），宿主
///   code-behind 在上面画检测框/标定框等；坐标换算用 Stage 实际尺寸（即本控件），
///   与 V2.3.5"控件即 Grid，ActualWidth/Height 零改动"口径一致。
///   【V2.3.7 可交互口径】画布恒可见 + IsHitTestVisible=true——宿主可在上面放
///   可交互元素（可拖拽 ROI 框/角手柄等，AIris 编辑页实踩需求）；画布自身无背景
///   不拦截空白点击，"空白处按下"由宿主在画布上自挂事件判命中元素为空实现。
///
/// 【三色可配置（复用核心）】StageBackground / GridLineBrush / ScanBrush 三个 Brush DP
///   默认 null → 构造期 SetResourceReference 到主题键（Brush.Shell.StageBg / StageGridLine /
///   StageScan，DynamicResource 跟随宿主深浅换肤）；宿主显式设本地值即覆盖（WPF 本地值
///   优先级高于资源引用）——不配 = 主题自动配色，配了 = 项目自定义原色。
///   网格步距 GridStep、扫描时长 ScanDuration、实例开关 ScanEnabled 同为可调 DP。
///
/// 【用法（喂帧，傻瓜式全量）】
///   xmlns:ui="clr-namespace:Kaleidoscope.UI.Views;assembly=Kaleidoscope.UI"
///   &lt;ui:KxImageStage FrameSource="{Binding FrameImage}" IdleText="等待画面…"/&gt;
///   叠加：Stage.Overlay.Children.Add(...)（code-behind）。
/// 【用法（纯装饰，V2.3.5 口径）】
///   &lt;ui:KxImageStage&gt; ...宿主子元素... &lt;/ui:KxImageStage&gt;
///   本控件即 Grid（ClipToBounds 已设），宿主 code-behind 对 ActualWidth/ActualHeight、
///   SizeChanged 的既有用法零改动。喂帧用法不要再塞 Image/空态文本（内置件已代劳）。
///
/// 【适用边界】纯 WPF 项目适用（半透明画刷直写，无 HWND 合成丢 α 问题）；
///   WinForms/HWND airspace 场景（Cognex ActiveX 等 HWND 盖住 WPF 元素）不适用——
///   网格须画 WinForms 侧、半透明须预混实色（HJVision 口径），勿把本控件套在该场景。
///
/// 【性能护栏（分层窗软渲染场景）】①扫描带为单条 ~10% 透明度渐变带（小面积低透明度）；
///   ②IsVisible=false / Unloaded 双停（BeginAnimation(null)，等效"停机 RemoveStoryboard"
///   防空转）——分层窗软件渲染每帧重绘整窗，后台页签不可见时动画绝不能空转；
///   ③库级总闸 KxShellDefaults.EnableStageScanMotion（宿主一键关全局，工控低配机兜底）；
///   实例级 ScanEnabled DP 再加一层。有效开关 = 总闸 &amp;&amp; ScanEnabled &amp;&amp; IsVisible。
/// </summary>
public class KxImageStage : Grid
{
    private const string KeyStageBg = "Brush.Shell.StageBg";
    private const string KeyGridLine = "Brush.Shell.StageGridLine";
    private const string KeyScan = "Brush.Shell.StageScan";
    private const string KeyFgDim = "Brush.Shell.FgDim";   // 空态文本弱化前景（库默认板语义键）

    private Border _bg = null!;                 // [ZIndex=-2] 舞台底（构造函数同步装配）
    private Rectangle _mesh = null!;            // [ZIndex=-1] 网格底纹
    private Image _frame = null!;               // [ZIndex=0 前段] 帧图（V2.3.6 内置）
    private Canvas _overlay = null!;            // [ZIndex=5] 叠加画布（V2.3.6 内置）
    private TextBlock _idle = null!;            // 空态文本（无帧显示、有帧隐藏）
    private Rectangle _scanBand = null!;        // [ZIndex=10] 扫描光带
    private TranslateTransform _scanTranslate = null!;
    private Pen _gridPen = null!;               // 网格线画笔（Brush 绑 GridLineBrush DP；建一次复用防丢绑定）
    private Dispatcher _dispatcher = null!;     // 命令式喂帧封送（构造线程即 UI 线程口径）

    public KxImageStage()
    {
        ClipToBounds = true;            // 裁扫描带溢出（光带移出舞台的部分不外溢）
        _dispatcher = Dispatcher.CurrentDispatcher;   // 命令式喂帧封送基准（控件须在 UI 线程构造）
        BuildDecorations();
        HookLifecycle();
    }

    // ============ 三色可配置 DP（默认 null=主题键自动配色，设本地值=项目自定义原色） ============

    /// <summary>舞台底画刷。默认 null = 走主题键 Brush.Shell.StageBg（DynamicResource 随宿主深浅换肤）。</summary>
    public static readonly DependencyProperty StageBackgroundProperty = DependencyProperty.Register(
        nameof(StageBackground), typeof(Brush), typeof(KxImageStage), new PropertyMetadata(null));

    public Brush StageBackground
    {
        get => (Brush)GetValue(StageBackgroundProperty);
        set => SetValue(StageBackgroundProperty, value);
    }

    /// <summary>网格线画刷。默认 null = 走主题键 Brush.Shell.StageGridLine。</summary>
    public static readonly DependencyProperty GridLineBrushProperty = DependencyProperty.Register(
        nameof(GridLineBrush), typeof(Brush), typeof(KxImageStage), new PropertyMetadata(null));

    public Brush GridLineBrush
    {
        get => (Brush)GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    /// <summary>扫描光带画刷（整条渐变，形状也可调）。默认 null = 走主题键 Brush.Shell.StageScan。</summary>
    public static readonly DependencyProperty ScanBrushProperty = DependencyProperty.Register(
        nameof(ScanBrush), typeof(Brush), typeof(KxImageStage), new PropertyMetadata(null));

    public Brush ScanBrush
    {
        get => (Brush)GetValue(ScanBrushProperty);
        set => SetValue(ScanBrushProperty, value);
    }

    // ============ 行为 DP ============

    /// <summary>实例级扫描开关（默认 true）。有效开关 = KxShellDefaults.EnableStageScanMotion（库总闸）&amp;&amp; 本值 &amp;&amp; IsVisible。</summary>
    public static readonly DependencyProperty ScanEnabledProperty = DependencyProperty.Register(
        nameof(ScanEnabled), typeof(bool), typeof(KxImageStage), new PropertyMetadata(true, OnScanRelatedChanged));

    public bool ScanEnabled
    {
        get => (bool)GetValue(ScanEnabledProperty);
        set => SetValue(ScanEnabledProperty, value);
    }

    /// <summary>网格步距 px（默认 18 = 设计稿口径；HJVision 为 26，可配）。变化即重建网格画刷。</summary>
    public static readonly DependencyProperty GridStepProperty = DependencyProperty.Register(
        nameof(GridStep), typeof(double), typeof(KxImageStage), new PropertyMetadata(18.0, OnGridStepChanged));

    public double GridStep
    {
        get => (double)GetValue(GridStepProperty);
        set => SetValue(GridStepProperty, value);
    }

    /// <summary>扫描一轮时长秒（默认 4.2 = 设计稿 sScan 口径）。变化即重启动画。</summary>
    public static readonly DependencyProperty ScanDurationProperty = DependencyProperty.Register(
        nameof(ScanDuration), typeof(double), typeof(KxImageStage), new PropertyMetadata(4.2, OnScanRelatedChanged));

    public double ScanDuration
    {
        get => (double)GetValue(ScanDurationProperty);
        set => SetValue(ScanDurationProperty, value);
    }

    private static void OnGridStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KxImageStage)d).RebuildMesh();

    private static void OnScanRelatedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KxImageStage)d).StartScan();

    // ============ 帧显示管线 DP（V2.3.6） ============

    /// <summary>帧源（绑定式喂帧，MVVM 口径）。null = 空态（IdleText 显示）。
    /// 【二选一红线】与命令式 ShowFrame/ClearFrame 别混用同一实例（类头注释）。</summary>
    public static readonly DependencyProperty FrameSourceProperty = DependencyProperty.Register(
        nameof(FrameSource), typeof(ImageSource), typeof(KxImageStage), new PropertyMetadata(null, OnFrameSourceChanged));

    public ImageSource FrameSource
    {
        get => (ImageSource)GetValue(FrameSourceProperty);
        set => SetValue(FrameSourceProperty, value);
    }

    /// <summary>空态文本（无帧时居中显示，弱化前景主题键；null/空 = 不显示）。</summary>
    public static readonly DependencyProperty IdleTextProperty = DependencyProperty.Register(
        nameof(IdleText), typeof(string), typeof(KxImageStage), new PropertyMetadata(null, OnIdleTextChanged));

    public string IdleText
    {
        get => (string)GetValue(IdleTextProperty);
        set => SetValue(IdleTextProperty, value);
    }

    /// <summary>帧图拉伸模式（默认 Uniform：整帧完整可见 + letterbox 对齐网格舞台，
    /// 监控画面口径；Cover/Fill/None 同 Image.Stretch 语义可改）。</summary>
    public static readonly DependencyProperty FrameStretchProperty = DependencyProperty.Register(
        nameof(FrameStretch), typeof(Stretch), typeof(KxImageStage), new PropertyMetadata(Stretch.Uniform));

    public Stretch FrameStretch
    {
        get => (Stretch)GetValue(FrameStretchProperty);
        set => SetValue(FrameStretchProperty, value);
    }

    private static void OnFrameSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KxImageStage)d).UpdateFrameVisibility();

    private static void OnIdleTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KxImageStage)d).UpdateFrameVisibility();

    /// <summary>叠加画布（ZIndex=5：画面上、扫描带下）。宿主 code-behind 直接取用画
    /// 检测框/标定框等；坐标换算用本控件 ActualWidth/ActualHeight（控件即 Grid 口径）。</summary>
    public Canvas Overlay => _overlay;

    /// <summary>
    /// 命令式喂帧（事件流派；任意线程可调——内部 Dispatcher 封送，帧线程直调不崩，
    /// 对标 HJVision"检测线程直调"口径）。见 FrameSource 的【二选一红线】。
    /// </summary>
    public void ShowFrame(ImageSource frame)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action<ImageSource>(ShowFrame), frame);
            return;
        }
        FrameSource = frame;
    }

    /// <summary>命令式清帧回空态（任意线程可调，封送同 ShowFrame）。</summary>
    public void ClearFrame()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(ClearFrame));
            return;
        }
        FrameSource = null;
    }

    /// <summary>空态/有帧显隐切换：有帧显图隐空态文本并自动停扫描；无帧回空态文本并
    /// 恢复扫描（出图让位/空闲网格底纹口径）。绑定式与命令式喂帧都经 FrameSource DP
    /// 回调汇聚到这里，宿主零接线。</summary>
    private void UpdateFrameVisibility()
    {
        bool has = FrameSource != null;
        _frame.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _idle.Visibility = has || string.IsNullOrEmpty(IdleText) ? Visibility.Collapsed : Visibility.Visible;
        StartScan();    // 有效开关内含 FrameSource==null 判断：有帧=停，清帧=自动恢复
    }

    // ============ 装饰层装配 ============

    private void BuildDecorations()
    {
        _bg = new Border { IsHitTestVisible = false };
        _mesh = new Rectangle { IsHitTestVisible = false };
        // 帧显示三内置件（V2.3.6）：不喂帧时全 Collapsed，纯装饰用法零破坏
        _frame = new Image { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _frame.SetBinding(Image.SourceProperty, new Binding(nameof(FrameSource)) { Source = this });
        _frame.SetBinding(Image.StretchProperty, new Binding(nameof(FrameStretch)) { Source = this });
        _overlay = new Canvas();    // V2.3.7：恒可见 + 可命中（宿主画可交互叠加件的前提——
                                    // AIris 编辑页 ROI 拖拽实踩：Collapsed+不可命中时画上去的东西
                                    // 既不显示也收不到鼠标；空画布无背景不拦截任何点击，监控台
                                    // 纯画框用法行为不变，此前构造设 false 属库侧遗留缺陷）
        _idle = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,   // V2.3.7：空态文本不挡鼠标（否则居中文本区吞点击，宿主交互区开洞）
        };
        _idle.SetBinding(TextBlock.TextProperty, new Binding(nameof(IdleText)) { Source = this });
        _idle.SetResourceReference(TextBlock.ForegroundProperty, KeyFgDim);
        _scanBand = new Rectangle { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _scanTranslate = new TranslateTransform();
        _scanBand.RenderTransform = _scanTranslate;

        SetZIndex(_bg, -2);         // 负 ZIndex 画在 Panel Background 之上、普通子元素之下
        SetZIndex(_mesh, -1);
        SetZIndex(_frame, 0);       // 帧图与宿主子内容同层（宿主再放元素会盖在帧上，自担）
        SetZIndex(_overlay, 5);     // 叠加画布：画面上、扫描带下（检测框不被光带染色）
        SetZIndex(_scanBand, 10);

        Children.Add(_bg);
        Children.Add(_mesh);
        Children.Add(_frame);
        Children.Add(_overlay);
        Children.Add(_idle);
        Children.Add(_scanBand);

        // 三色 DP 兜底：宿主未显式设本地值（UnsetValue）时挂主题键资源引用。
        // 【优先级】本地值 > 资源引用——宿主 XAML 稍后设置的 StageBackground="…" 自动压过；
        // 【换肤】SetResourceReference = DynamicResource 语义，宿主换主题字典时 DP 值自动跟随。
        if (ReadLocalValue(StageBackgroundProperty) == DependencyProperty.UnsetValue)
            SetResourceReference(StageBackgroundProperty, KeyStageBg);
        if (ReadLocalValue(GridLineBrushProperty) == DependencyProperty.UnsetValue)
            SetResourceReference(GridLineBrushProperty, KeyGridLine);
        if (ReadLocalValue(ScanBrushProperty) == DependencyProperty.UnsetValue)
            SetResourceReference(ScanBrushProperty, KeyScan);

        // 内部元素接线用代码 Binding（装饰层元素不是模板场景；DP 值变 → 目标自动跟随）
        _bg.SetBinding(Border.BackgroundProperty, new Binding(nameof(StageBackground)) { Source = this });
        _scanBand.SetBinding(Shape.FillProperty, new Binding(nameof(ScanBrush)) { Source = this });

        // 网格线：Pen 建一次存字段，Brush 走 Binding（Pen 是 Freezable，未冻结可绑定；
        // RebuildMesh 只重建 Drawing/画刷、复用同一 Pen，绑定不丢）
        _gridPen = new Pen(Brushes.Transparent, 1d);
        BindingOperations.SetBinding(_gridPen, Pen.BrushProperty, new Binding(nameof(GridLineBrush)) { Source = this });

        RebuildMesh();
        UpdateFrameVisibility();    // 构造期兜底（XAML 标签属性在构造后、Loaded 前生效，回调已覆盖）
    }

    /// <summary>重建网格底纹画刷：GeometryDrawing 1px 双向线 + Tile 平铺（步距=GridStep）。
    /// 线画在 tile 顶缘/左缘内 0.5px 处（Pen 1px 恰好覆盖整像素行/列），平铺后连续无缝。</summary>
    private void RebuildMesh()
    {
        double step = Math.Max(2, GridStep);
        var group = new GeometryGroup();
        group.Children.Add(new LineGeometry(new Point(0, 0.5), new Point(step, 0.5)));
        group.Children.Add(new LineGeometry(new Point(0.5, 0), new Point(0.5, step)));
        var drawing = new GeometryDrawing { Geometry = group, Pen = _gridPen };
        var brush = new DrawingBrush
        {
            Drawing = drawing,
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,  // 步距按绝对像素（默认相对 bounding box 会随元素缩放）
            Viewport = new Rect(0, 0, step, step),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, step, step),
        };
        _mesh.Fill = brush;
    }

    // ============ 扫描动效与生命周期 ============

    private void HookLifecycle()
    {
        IsVisibleChanged += OnVisibilityChanged;
        Loaded += OnLoadedStart;
        Unloaded += OnUnloadedStop;
        SizeChanged += OnSizeRestart;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => StartScan();
    private void OnLoadedStart(object sender, RoutedEventArgs e) => StartScan();
    private void OnUnloadedStop(object sender, RoutedEventArgs e) => StopScan();
    private void OnSizeRestart(object sender, SizeChangedEventArgs e) => StartScan();   // 新尺寸重建动画参数

    /// <summary>按当前开关状态启动扫描动画（幂等：先清旧动画再起）。有效开关 =
    /// KxShellDefaults.EnableStageScanMotion（库总闸）&amp;&amp; ScanEnabled（实例）&amp;&amp; IsVisible；
    /// 未布局（ActualHeight&lt;1）时跳过，等 SizeChanged/Loaded 再来。
    /// 光带与舞台同高、整面渐变，Y 从 −h 扫到 +h（一轮完整扫过，循环点在画面外无跳变）。</summary>
    private void StartScan()
    {
        // 【V2.3.6 有帧自动停】显示图像/视频时扫描动效自动关闭（画面内容不该被光带干扰，
        //   帧流高频重绘期动画时钟是双重开销）——只有初始空态（无帧）才有扫描。
        if (!ScanEnabled || !KxShellDefaults.EnableStageScanMotion || !IsVisible || FrameSource != null)
        {
            StopScan();
            return;
        }
        double h = ActualHeight;
        if (h < 1) return;

        _scanBand.Visibility = Visibility.Visible;
        _scanTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        var anim = new DoubleAnimation(-h, h, TimeSpan.FromSeconds(Math.Max(0.1, ScanDuration)))
        {
            RepeatBehavior = RepeatBehavior.Forever,    // linear（不设 EasingFunction），设计稿 sScan 口径
        };
        // 【帧率降档（性能关键，实测口径）】分层窗=软件渲染，动画时钟每 tick 都触发整窗
        // 重绘；WPF 缺省 DesiredFrameRate=60fps，本机实测稳态 ~85% 单核（2026-11-22
        // AIris 屏摄期间 Get-Process CPU 增量采样）。扫描带是慢位移（4.2s 一轮，10fps
        // 每帧位移 = 2h/(4.2×10) ≈ 38px@800px 舞台，肉眼平滑无跳感），降 10fps 后重绘
        // 次数 -83%，占用同比例回落（HUD/呼吸灯类慢动效通用口径）。
        Timeline.SetDesiredFrameRate(anim, 10);
        _scanTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    /// <summary>停扫描：带子收起 + 清动画时钟（等效"停机 RemoveStoryboard"防空转口径）。</summary>
    private void StopScan()
    {
        _scanBand.Visibility = Visibility.Collapsed;
        _scanTranslate.BeginAnimation(TranslateTransform.YProperty, null);
    }
}
