using System;
using System.Windows;

namespace Kaleidoscope.UI.Views;

/// <summary>
/// Kx 弹窗壳的宿主级常量配置（V2.1.0 融合 HJVision SunnyDialog/ChromeDialog 时新增）。
///
/// 【宿主 UI 开工必读（AI Agent 通用路标）】宿主写任何界面控件/样式/窗之前，先读
///   Kaleidoscope.UI/README.md 的「控件复用索引（按场景查）」——弹窗壳/标题条/三窗钮/
///   表单件全有现成封装，禁重复造轮子（库内有直接用 / 近似 BasedOn / 确无才自绘）。
///   本类是宿主启动链必调点，在此设路标让任何工具的 Agent 都绕不开。
///
/// 【干什么】承载"随宿主走、不随主题变"的壳参数缺省值——两个宿主的口径差异里，
///   有一类是"视觉常量"（投影留白宽度、要不要内容 Clip、隐式样式注入键表、默认字体），
///   这类参数既不该写死在基类里（会绑死某一宿主口径），也不适合逐窗声明（51 窗重复），
///   更不能放进主题键板（它们不随主题换值）——所以收进这个静态配置类：
///   宿主启动时（App.OnStartup 最前）设一次，之后所有弹窗自动生效。
///
/// 【两宿主口径对照】
///   CureLine（缺省值，零配置）：ShadowMargin=14（浮层投影留白）、UseContentClip=false
///     （内容不铺满四角，无 Clip 诉求）、ImplicitStyleKeys=null（无隐式注入）、
///     Font=null（用 WPF 缺省字体）——V2.0.0 迁移的 3 窗逐像素口径。
///   HJVision-WPF：ShadowMargin=0（根卡片贴边、无投影留白）、UseContentClip=true
///     （根 Border 圆角要裁内容四角透空月牙）、ImplicitStyleKeys="KxDialogButtonImplicit,
///     KxDialogComboImplicit,…"（7 类窗级隐式样式）、Font=微软雅黑 12——SunnyDialog
///     36 窗 + ChromeDialog 9 窗的既有口径。
///
/// 【为什么不做成资源键】资源键放键板里要 Light/Dark 两套各抄一份，而这些参数
///   根本不随主题变；静态配置读一次落本地值，行为也更可测（单测直接断言配置）。
///   线程纪律：宿主 UI 线程启动期赋值一次，之后只读——不做并发防护（WPF App 常规）。
/// </summary>
public static class KxShellDefaults
{
    /// <summary>
    /// 骨架根 Grid 的投影留白（缺省 14=CureLine 口径，Fx.WinShadow 浮层投影要
    /// Margin 留白才不会被窗矩形裁掉）。HJVision=0：根卡片贴边、无投影留白
    /// （其弹窗观感是"圆角卡片直接撑满窗"，投影交给无）。
    /// </summary>
    public static Thickness ShadowMargin { get; set; } = new(14);

    /// <summary>
    /// 是否给骨架卡片 Border 的内容区挂圆角 Clip（缺省 false=CureLine 口径）。
    /// HJVision=true：WPF Border.CornerRadius 不裁子元素——根卡片内的实底内容
    /// 容器（渐变白底铺满）会把下两角的透空月牙填白，弹窗视觉上变成"上圆下方"
    /// （2026-10-31 四角全圆轮实测根治：给内容挂 RectangleGeometry 圆角 Clip，
    /// 半径=外弧-1px 边框内缘弧；最大化归零、还原恢复）。
    /// </summary>
    public static bool UseContentClip { get; set; } = false;

    /// <summary>
    /// 窗级隐式样式注入的键名表（逗号分隔的资源键名，null=不注入，缺省 null=CureLine）。
    /// 【机制与血泪】注入发生在 OnInitialized（InitializeComponent 之后）——不能放
    /// 构造函数：构造早于 InitializeComponent，注入后子类 XAML 的 Window.Resources
    /// 再声明同名隐式样式时，BAML 加载器往字典 Add 同键直接抛 XamlParseException
    /// "已添加项"（V0.9.52 六窗同案实测）；OnInitialized 时 XAML 已解析完，注入走
    /// 索引器=覆盖语义，子类手写/不手写都不炸。取不到键时静默跳过（独立 harness
    /// 未并键板不影响开窗）。
    /// HJVision 键表（与 ShellStyles.xaml 的 KxDialog*Implicit 键对应）：
    /// "KxDialogButtonImplicit,KxDialogComboImplicit,KxDialogTextBoxImplicit,
    ///  KxDialogPasswordImplicit,KxDialogGridImplicit,KxDialogListImplicit,KxGroupBoxImplicit"
    /// </summary>
    public static string? ImplicitStyleKeys { get; set; } = null;

    /// <summary>
    /// 弹窗默认字体族（缺省 null=不动字体，CureLine 用 WPF 缺省）。HJVision=
    /// "Microsoft YaHei UI, 微软雅黑"（与老 Designer 全表一致，FontSize 12）。
    /// </summary>
    public static string? FontFamily { get; set; } = null;

    /// <summary>弹窗默认字号（缺省 null=不动）。HJVision=12（9pt≈12px 与老 Designer 一致）。</summary>
    public static double? FontSize { get; set; } = null;

    /// <summary>
    /// 标题条系统钮（—/▢/×）统一宽度（缺省 44=CureLine ✕ 口径）。HJVision=40
    /// （SunnyTitleBar 三钮 40 宽口径，44 会把标题文字区右移 12px，像素目检必现）。
    /// 高度始终=标题条高（CaptionHeight），只参数化宽度。
    /// </summary>
    public static double SysButtonWidth { get; set; } = 44;

    /// <summary>
    /// 标题条 ✕ 关闭钮字重（缺省 Normal=CureLine 口径）。HJVision=Bold
    /// （原 SunnyCloseButton FontSize 14 Bold 口径）。
    /// </summary>
    public static FontWeight CloseFontWeight { get; set; } = FontWeights.Normal;

    /// <summary>
    /// 弹窗是否进任务栏（缺省 true=不动=CureLine 现状）。HJVision=false：
    /// 全部 36+9 窗弹窗不进任务栏（老版子窗口径，弹窗进任务栏会抢主窗焦点位）。
    /// </summary>
    public static bool ShowInTaskbar { get; set; } = true;

    /// <summary>
    /// 弹窗启动定位（缺省 Manual=不动=CureLine 现状）。HJVision=CenterScreen：
    /// 所有新窗体屏幕居中（与老版一致——老各子窗 Show 时落屏幕中央，不跟主窗
    /// 相对；CenterOwner 在主窗偏角时会把小窗带偏，统一 CenterScreen）。
    /// </summary>
    public static WindowStartupLocation StartupLocation { get; set; } = WindowStartupLocation.Manual;
}
