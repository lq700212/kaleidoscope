// =========================================================================
// UiConverters：通用值转换器（宿主 CureLine V0.0.109 反哺沉库，语义零改动）
//
// 【干什么】各宿主重复实现的通用转换器：
//   ① InverseBoolConverter——bool 取反（互斥 Radio"一只双向一只纯显示"
//      的标准配对；null/非 bool 回 false 安全侧）；
//   ② StringEqualsConverter——两段字符串相等 → bool（列表"生效中"徽标
//      的 MultiBinding 互相比对方案：DataTrigger 的 Condition 只能填静态值
//      不能绑定，两个绑定没法直接互相比）；
//   ③ CapsuleRadiusConverter——控件 ActualHeight/2 → 真胶囊圆角（V2.1.0 自
//      HJVision Styles/CapsuleRadiusConverter.cs 迁入，血泪注释随迁：固定 99
//      在 WPF Border 上按 X/Y 分别缩放成橄榄球椭圆、固定半高在 23~38 九档
//      按钮高度上不能两全，自绑定 ActualHeight/2 对任意高度都成立）；
//   ④ NullToVisibility / TrueToVisibility——显隐两件（Subtitle 等可选段的
//      绑定开关），经 UiConverters 静态便捷单例下发（代码构建骨架时直接
//      引单例，免去每个窗 Resources 声明）。
//
// 【为什么】转换器是绑定胶水，各窗复制后口径漂移（Trim/大小写差异）会让
//   同一徽标在不同窗表现不同——沉库统一口径。
//
// 【怎么接】宿主 xmlns 引库后直接用：
//   <ui:InverseBoolConverter x:Key="InverseBool"/>；
//   StringEqualsConverter 走 MultiBinding（两个 string 绑定 → bool）；
//   CapsuleRadiusConverter 模板里自绑定 ActualHeight（见类头注释）；
//   代码构建绑定用 Kaleidoscope.UI.Converters.UiConverters 静态单例。
// =========================================================================

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kaleidoscope.UI.Converters;

/// <summary>bool 取反。null/非 bool 一律回 false（绑定失败安全侧）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}

/// <summary>
/// 两段字符串相等 → bool（MultiBinding）。比较口径 Trim + 忽略大小写；
/// 异常/段数不对一律 false（宁可不显徽标不炸绑定）。
/// </summary>
public sealed class StringEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2)
            return false;
        var a = values[0] as string ?? string.Empty;
        var b = values[1] as string ?? string.Empty;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException(); // 纯展示（徽标显隐），不回转
}

/// <summary>
/// 胶囊圆角转换器（V2.1.0 自宿主 HJVision 迁入）：把控件当前实际高度换算成
/// CornerRadius = 高度/2，让 Border 画出「两端半圆 + 中间直线」的真胶囊。
///
/// 【为什么不能用固定 99】WPF Border 的 CornerRadius 超出控件尺寸时，横向/纵向
/// 半径按各自方向**分别**缩放（CSS border-radius:99px 是统一钳制到 min(w,h)/2
/// 的完美胶囊）；于是 124×30 按钮被 99 渲染成橄榄球椭圆——宿主 HJVision
/// 2026-10-30 用户真机目检抓包。
/// 【为什么不能用固定 15（30px 按钮的一半）】全工程主按钮实测高度 23~38 共
/// 九档，固定值在更矮的按钮上仍会超过半高而重新椭圆化。绑定 ActualHeight/2
/// 对任意高度都成立，且内层罩/描边层各自取自身高度一半，天然同心不溢出。
///
/// 【用法】模板里 Border 自绑定：
///   CornerRadius="{Binding ActualHeight, RelativeSource={RelativeSource Self},
///                  Converter={StaticResource CapsuleRadiusConverter}}"
/// 首次布局前 ActualHeight=0 → 圆角 0（不可见），布局后自动更新，无死循环
/// （CornerRadius 不参与测量，只触发重绘）。
/// </summary>
public sealed class CapsuleRadiusConverter : IValueConverter
{
    /// <summary>value=控件 ActualHeight（double）→ CornerRadius（四角同值 = 高度一半）。</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double h = 0;
        if (value is double d) h = d;
        else if (value is int i) h = i;
        return new CornerRadius(h > 0 ? h / 2 : 0);
    }

    /// <summary>单向转换，反向不做任何事（CornerRadius 不回写高度）。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>null（或空字符串）→ Collapsed、其余 → Visible（副题等可选段显隐）。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        return string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>true → Visible、其余（false/null/非 bool）→ Collapsed。</summary>
public sealed class TrueToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// 通用转换器静态便捷单例（代码构建绑定用）：骨架代码里直接
/// UiConverters.NullToCollapsed / UiConverters.TrueToVisible 引用，
/// 免去每个消费点 new 一份。
/// </summary>
public static class UiConverters
{
    public static readonly IValueConverter NullToCollapsed = new NullToVisibilityConverter();
    public static readonly IValueConverter TrueToVisible = new TrueToVisibilityConverter();
}
