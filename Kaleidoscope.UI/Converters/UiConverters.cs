// =========================================================================
// UiConverters：通用值转换器（宿主 CureLine V0.0.109 反哺沉库，语义零改动）
//
// 【干什么】两个各宿主重复实现的通用转换器：
//   ① InverseBoolConverter——bool 取反（互斥 Radio"一只双向一只纯显示"
//      的标准配对；null/非 bool 回 false 安全侧）；
//   ② StringEqualsConverter——两段字符串相等 → bool（列表"生效中"徽标
//      的 MultiBinding 互相比对方案：DataTrigger 的 Condition 只能填静态值
//      不能绑定，两个绑定没法直接互相比）。
//
// 【为什么】转换器是绑定胶水，各窗复制后口径漂移（Trim/大小写差异）会让
//   同一徽标在不同窗表现不同——沉库统一口径。
//
// 【怎么接】宿主 xmlns 引库后直接用：
//   <ui:InverseBoolConverter x:Key="InverseBool"/>；
//   StringEqualsConverter 走 MultiBinding（两个 string 绑定 → bool）。
// =========================================================================

using System.Globalization;
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
