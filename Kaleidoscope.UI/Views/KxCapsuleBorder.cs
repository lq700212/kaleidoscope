using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Kaleidoscope.UI.Converters;

namespace Kaleidoscope.UI.Views;

/// <summary>
/// 胶囊圆角的取轴方式（<see cref="KxCapsuleBorder"/> 专用）。
/// </summary>
public enum KxCapsuleAxis
{
    /// <summary>圆角 = 实际高度 / 2（横向胶囊：按钮、输入框、Chip、导航项、进度条）。</summary>
    Height,

    /// <summary>圆角 = 实际宽度 / 2（竖向胶囊：竖排 Tab、竖向滑轨等）。</summary>
    Width
}

/// <summary>
/// KxCapsuleBorder —— "一行写出真胶囊"的 Border 子类【V2.4.1】。
///
/// 【干什么】把 <c>&lt;Border CornerRadius="..."/&gt;</c> 换成
/// <c>&lt;ui:KxCapsuleBorder/&gt;</c>，它会在内部把 <c>CornerRadius</c> 绑定到自身
/// <c>ActualHeight / 2</c>（竖向胶囊改 <c>Axis="Width"</c> 取宽度一半），
/// 布局尺寸变化时自动跟随，端头永远是正圆半圆，不需要宿主写任何 Binding。
///
/// 【为什么需要它（两个宿主各栽一次的同一个坑）】
///   · WPF 的 <c>Border.CornerRadius</c> 在超出元素尺寸时，**横向/纵向半径各自独立钳制**
///     到 宽/2、高/2；而 CSS 的 <c>border-radius:99px</c> 是统一缩到 min(宽,高)/2。
///     于是照抄设计稿的 99 在宽按钮上会变成"横 99 / 纵 半高"的橄榄球，端头收弧过急，
///     肉眼就是"太尖/发扁"。HJVision 2026-10-30、AIris V0.1.12、Sinan 2026-11-24
///     三次实机返工，全部是同一个写法。
///   · 库内 <see cref="CapsuleRadiusConverter"/> 早已提供正确换算，但用法是一长串
///     <c>CornerRadius="{Binding ActualHeight, RelativeSource={RelativeSource Self}, ...}"</c>；
///     新项目/新模板作者查不到、想不到，就退回手写 99。本控件把这条 Binding
///     封进构造器，XAML 层面只剩标签名，属于"默认正确"而不是"记得写对"。
///
/// 【怎么用】
///   xmlns:ui="clr-namespace:Kaleidoscope.UI.Views;assembly=Kaleidoscope.UI"
///   &lt;ui:KxCapsuleBorder Background="..." BorderBrush="..." BorderThickness="1"&gt;
///       &lt;ContentPresenter/&gt;
///   &lt;/ui:KxCapsuleBorder&gt;
///   竖向胶囊：&lt;ui:KxCapsuleBorder Axis="Width" ... /&gt;
///
/// 【三条纪律】
///   ① 不要再写 <c>CornerRadius</c>：本地值优先级高于构造器里的 Binding，写多少就覆盖多少，
///      等于白用本控件（审计工具会当普通 Border 检查，超半高同样报错）。
///   ② 需要"只有上圆角/下圆角"这类非对称形状时用普通 Border + 显式数值，
///      本控件只负责"四角同半径的真胶囊"。
///   ③ 继承 Border，所有既有属性/触发器/样式（TargetType="Border"）原样可用，
///      模板里替换标签即可，x:Name 与 TargetName 不受影响。
///
/// 【与转换器的关系】内部就是 <see cref="CapsuleRadiusConverter"/> 的封装；
/// 无法替换标签的场合（例如第三方控件的模板内部、代码动态构建）仍可用转换器，
/// 二者换算口径完全一致（高度或宽度的一半）。
/// </summary>
public class KxCapsuleBorder : Border
{
    /// <summary>取轴方式；默认 <see cref="KxCapsuleAxis.Height"/>（按高度算横向胶囊）。</summary>
    public static readonly DependencyProperty AxisProperty = DependencyProperty.Register(
        nameof(Axis), typeof(KxCapsuleAxis), typeof(KxCapsuleBorder),
        new PropertyMetadata(KxCapsuleAxis.Height, OnAxisChanged));

    /// <summary>转换器无状态，全类型共用一个实例（避免每个胶囊元素 new 一份）。</summary>
    private static readonly CapsuleRadiusConverter Converter = new();

    public KxCapsuleBorder()
    {
        // 构造即生效：XAML 解析完、首次测量前就已挂好绑定，首帧就是胶囊，不会闪一下直角。
        ApplyCapsuleBinding();
    }

    /// <summary>取轴方式；改这个值会重建绑定（高度一半 ↔ 宽度一半）。</summary>
    public KxCapsuleAxis Axis
    {
        get => (KxCapsuleAxis)GetValue(AxisProperty);
        set => SetValue(AxisProperty, value);
    }

    /// <summary>Axis 变更回调：只重建一条绑定，代价可忽略（胶囊通常几十个以内）。</summary>
    private static void OnAxisChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KxCapsuleBorder)d).ApplyCapsuleBinding();

    /// <summary>
    /// 把 CornerRadius 绑到自身 ActualHeight（或 ActualWidth）的一半。
    /// 用 BindingOperations.SetBinding 而不是直接赋值：只有绑定才能在每次布局尺寸变化时
    /// 自动重算，赋值只能拿到构造那一刻的 0。
    /// </summary>
    private void ApplyCapsuleBinding()
    {
        string path = Axis == KxCapsuleAxis.Width ? nameof(ActualWidth) : nameof(ActualHeight);
        BindingOperations.SetBinding(this, CornerRadiusProperty, new Binding(path)
        {
            Source = this,
            Mode = BindingMode.OneWay,
            Converter = Converter
        });
    }
}
