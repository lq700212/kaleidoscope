// =========================================================================
// KxThemeTokens：双栈语义色板 Token 层（V1.0.0 骨架首建）
//
// 【干什么】WinForms 侧的语义色单一事实源：IsDark 开关 + 语义键→Color 取值 +
//   Changed 静态事件——ShellDialogForm 等控件按语义键取色，不写死色值；
//   WPF 侧键板（Kaleidoscope.UI Themes/DefaultLight|Dark.xaml）按同语义键对齐，
//   两栈配色同源（改一处语义值，两栈文档同步）。
//
// 【为什么】宿主 HJVision 原版 WinForms 的 SubClass/AppTheme.cs（V4.4.13 起）
//   就是这个形态（IsDark+Changed+语义键值表），WPF 版 ThemeLight|Dark 双字典是
//   同一套色板的另一栈表达——两栈色板键名语义一一对应（调研实证），Token 层把
//   "对应关系"固化成代码，防两栈漂移。深色=方案A「深夜中控」、浅色=方案C
//   「渐变活力」（与 WPF Docs/ui_redesign_mockups.html 同源定稿）。
//
// 【怎么接】控件 OnThemeApply 钩子里按 Get(FormBg) 等语义键取色刷自身；
//   订阅 Changed 做换肤刷新——【铁律（原版 AppTheme 血泪）】静态事件订阅方必须
//   在窗体 Disposed 双出口退订，否则窗体被静态事件根住=内存泄漏（原版文件头
//   有专门红线段，ShellDialogForm 已内置正确订阅/退订模板）。
//
// 【色值来源】原版 AppTheme 色板键值表（WinForms 侧真机定稿值）——与 WPF
//   ThemeLight|Dark.xaml 同键同语义。库不复制全部键（当前骨架只放 ShellDialogForm
//   用到的），扩键时两侧同步。
// =========================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Kaleidoscope.UI.WinForms;

/// <summary>语义色键（WinForms 侧枚举；与 WPF 键板语义一一对应，见类头）。</summary>
public enum KxToken
{
    /// <summary>窗体底色（WPF：ThemeWindowBg）。</summary>
    FormBg,
    /// <summary>面板/卡片底（WPF：ThemePanelBg）。</summary>
    PanelBg,
    /// <summary>标题栏渐变起点（WPF：ThemeDlgTitleGradientBrush 起点色）。</summary>
    TitleGradFrom,
    /// <summary>标题栏渐变终点（WPF：ThemeDlgTitleGradientBrush 终点色）。</summary>
    TitleGradTo,
    /// <summary>标题栏文字（WPF：Brush.TopbarFg）。</summary>
    TitleFg,
    /// <summary>正文前景（WPF：ThemeTextFg）。</summary>
    Fore,
    /// <summary>弱化前景（WPF：ThemeMutedFg）。</summary>
    Muted,
    /// <summary>边框/描边（WPF：ThemeBorder）。</summary>
    Border,
    /// <summary>输入框底（WPF：ThemeInputBg）。</summary>
    InputBg,
    /// <summary>主色单色（WPF：ThemePrimaryBrush）。</summary>
    Accent,
    /// <summary>OK 语义字（WPF：ThemeOkFgBrush）。</summary>
    StatusOk,
    /// <summary>警告语义字（WPF：ThemeWarnFgBrush）。</summary>
    StatusWarn,
    /// <summary>NG 语义字（WPF：ThemeNgTextBrush）。</summary>
    StatusNg,
    /// <summary>关闭钮悬停红（Win32 惯例，两主题同值）。</summary>
    CloseHover,
}

/// <summary>
/// 双栈语义色板 Token 层（静态类；线程纪律：UI 线程读写，Changed 事件在
/// SetIsDark 调用线程触发——WinForms 控件收到后自行 Invoke 到 UI 线程）。
/// </summary>
public static class KxThemeTokens
{
    private static bool _isDark;

    /// <summary>当前是否深色主题（缺省 false=浅色方案C；与原版 AppTheme.IsDark 同语义）。</summary>
    public static bool IsDark => _isDark;

    /// <summary>
    /// 主题切换事件（静态）。【血泪铁律】订阅方（窗体/控件）必须在 OnFormClosed/
    /// Disposed 双出口退订——静态事件根住实例=整窗泄漏（原版 AppTheme.cs 文件头
    /// 红线段同款；ShellDialogForm 内置正确模板，直接继承的窗零操心）。
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>切主题（true=深夜中控 A 方案 / false=渐变活力 C 方案）并广播。</summary>
    public static void SetIsDark(bool dark)
    {
        if (_isDark == dark) return;
        _isDark = dark;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // ---- 色板（方案A 深色 / 方案C 浅色；取自原版 AppTheme 定稿值）----

    private static readonly Dictionary<KxToken, Color> _light = new()
    {
        [KxToken.FormBg] = Color.FromArgb(0xFF, 0xF7, 0xF6, 0xFF),      // －－bg 渐变活力底
        [KxToken.PanelBg] = Color.White,                                // －－panel 纯白
        [KxToken.TitleGradFrom] = Color.FromArgb(0xFF, 0x63, 0x66, 0xF1), // 靛青
        [KxToken.TitleGradTo] = Color.FromArgb(0xFF, 0x22, 0xB1, 0xCF), // 青薄荷
        [KxToken.TitleFg] = Color.White,
        [KxToken.Fore] = Color.FromArgb(0xFF, 0x24, 0x1F, 0x3D),        // －－fg 深靛字
        [KxToken.Muted] = Color.FromArgb(0xFF, 0x81, 0x78, 0xA3),       // －－muted
        [KxToken.Border] = Color.FromArgb(0xFF, 0xE6, 0xE4, 0xF4),      // －－border
        [KxToken.InputBg] = Color.Transparent,                          // －－input 透明只留描边
        [KxToken.Accent] = Color.FromArgb(0xFF, 0x63, 0x66, 0xF1),
        [KxToken.StatusOk] = Color.FromArgb(0xFF, 0x0B, 0x7A, 0x55),
        [KxToken.StatusWarn] = Color.FromArgb(0xFF, 0x7A, 0x4D, 0x05),
        [KxToken.StatusNg] = Color.FromArgb(0xFF, 0xCF, 0x43, 0x43),
        [KxToken.CloseHover] = Color.FromArgb(0xFF, 0xE8, 0x11, 0x23),  // Win32 惯例红
    };

    private static readonly Dictionary<KxToken, Color> _dark = new()
    {
        [KxToken.FormBg] = Color.FromArgb(0xFF, 0x0B, 0x15, 0x26),      // 深夜中控三层底
        [KxToken.PanelBg] = Color.FromArgb(0xFF, 0x10, 0x1A, 0x2E),
        [KxToken.TitleGradFrom] = Color.FromArgb(0xFF, 0x1E, 0x3A, 0x8A), // 弹窗标题专用深蓝
        [KxToken.TitleGradTo] = Color.FromArgb(0xFF, 0x22, 0xD3, 0xEE), // 青
        [KxToken.TitleFg] = Color.White,
        [KxToken.Fore] = Color.FromArgb(0xFF, 0xE8, 0xEF, 0xF9),
        [KxToken.Muted] = Color.FromArgb(0xFF, 0x8E, 0xA6, 0xC5),
        [KxToken.Border] = Color.FromArgb(0xFF, 0x22, 0x31, 0x4F),
        [KxToken.InputBg] = Color.Transparent,
        [KxToken.Accent] = Color.FromArgb(0xFF, 0x3E, 0x8B, 0xFF),
        [KxToken.StatusOk] = Color.FromArgb(0xFF, 0x10, 0xB9, 0x81),
        [KxToken.StatusWarn] = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B),
        [KxToken.StatusNg] = Color.FromArgb(0xFF, 0xFF, 0x8F, 0x8F),
        [KxToken.CloseHover] = Color.FromArgb(0xFF, 0xE8, 0x11, 0x23),
    };

    /// <summary>按语义键取当前主题色（改主题后重取即新值）。</summary>
    public static Color Get(KxToken token)
    {
        var table = _isDark ? _dark : _light;
        return table.TryGetValue(token, out var c) ? c : Color.Magenta;   // 缺键.Magenta 可见性兜底（防静默黑）
    }

    /// <summary>标题栏 45° 渐变画刷（每次新建；GDI 刷不缓存防主题切换残留）。</summary>
    public static LinearGradientBrush CreateTitleBrush(Rectangle rect)
    {
        return new LinearGradientBrush(rect, Get(KxToken.TitleGradFrom), Get(KxToken.TitleGradTo), 45f);
    }
}
