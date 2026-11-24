using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Kaleidoscope.UI.Views;

/// <summary>
/// KxRadiusAudit —— 圆角"会不会画成椭圆"的运行时体检器【V2.4.1】。
///
/// 【干什么】从指定根元素出发遍历**可视树（含 ControlTemplate 内部展开的元素）**，
/// 对每个 <see cref="Border"/> 检查它的 <c>CornerRadius</c> 是否超过
/// <c>min(ActualWidth, ActualHeight) / 2</c>。超了就意味着 WPF 会把横/纵半径分别钳制，
/// 端头从"正圆半圆"退化成"椭圆急收弧"——正是用户嘴里"圆角太尖/发扁"的样子。
/// 命中时输出一条可直接定位的报告（尺寸、四角值、上限、模板父类型、Name），
/// 返回命中数量；可选用 <c>failFast</c> 直接抛异常，接进宿主自检/单元测试。
///
/// 【为什么需要它】靠注释和口头纪律拦不住：HJVision（2026-10-30）、AIris（V0.1.12）、
/// Sinan（2026-11-24）三家宿主先后在同一处栽倒，全部是"设计稿 CSS 写 99，XAML 照抄 99"。
/// 人眼在 1440×900 的 1x 截图上分辨不出 1–2px 的弧线差异，问题往往上线后才被用户看出。
/// 把判据做成可执行代码，才能在开发期（甚至 CI）稳定拦住。
///
/// 【判据为什么是"四角最大值 &gt; 短边/2"】
///   · 正圆头像 22×22、半径 11：max=11，上限=11 → 不报。
///   · 卡片 20 / 内卡 16、同心圆角：远小于短边/2 → 不报。
///   · 宽 124 高 30 的按钮写成 99：max=99，上限=15 → 报（正确目标）。
///   · 只圆上两角的标签页（18,18,0,0）在 40 高元素上：max=18 &lt; 20 → 不报。
///   唯一误报可能是"故意用超大半径画拱形"的元素，用 <c>Tag="KxRadiusAudit.Skip"</c> 白名单。
///
/// 【怎么接（二选一）】
///   ① 开发期自检（推荐，Debug 构建）：
///      <code>
///      // 主窗 ContentRendered / Loaded 之后调用（此时布局已完成，ActualWidth/Height 有效）
///      #if DEBUG
///      KxRadiusAudit.Scan(Application.Current.MainWindow);
///      #endif
///      </code>
///      默认输出到 <see cref="Debug.WriteLine(string)"/>，用 DebugView / VS 输出窗口看。
///   ② CI / 单元测试断言零命中：
///      <code>
///      Assert.Equal(0, KxRadiusAudit.Scan(window, _ => { }, failFast: false));
///      </code>
///
/// 【限制】
///   · 只查 <see cref="Border"/> 的 CornerRadius；用 <c>Clip</c> 自绘的圆角不在检查范围。
///   · <c>Popup</c> 内容在独立视觉树（PopupRoot），从主窗扫不到，需要单独 Scan(popup.Child)。
///   · 必须在布局完成后调用；未布局的元素 ActualWidth/Height=0，会被跳过（不误报）。
/// </summary>
public static class KxRadiusAudit
{
    /// <summary>白名单标记：元素 <c>Tag</c> 含此字符串时跳过检查（故意的大半径拱形等）。</summary>
    public const string SkipTag = "KxRadiusAudit.Skip";

    /// <summary>
    /// 遍历可视树检查圆角是否会椭圆化。
    /// </summary>
    /// <param name="root">检查根（通常是 Window / UserControl）；null 直接返回 0。</param>
    /// <param name="report">报告输出；null 时写 <see cref="Debug.WriteLine(string)"/>。</param>
    /// <param name="failFast">true=发现第一处即抛 <see cref="InvalidOperationException"/>（测试/CI 用）。</param>
    /// <returns>命中数量（0 = 全部合规）。</returns>
    public static int Scan(DependencyObject? root, Action<string>? report = null, bool failFast = false)
    {
        if (root is null) return 0;
        report ??= message => Debug.WriteLine(message);

        int hits = 0;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            DependencyObject node = queue.Dequeue();

            if (node is Border border && Check(border, report))
            {
                hits++;
                if (failFast)
                    throw new InvalidOperationException(
                        $"[KxRadiusAudit] 发现 {hits} 处会把圆角画成椭圆的 Border（详见上一条报告），" +
                        "改用 <ui:KxCapsuleBorder> 或 CapsuleRadiusConverter。");
            }

            // VisualTreeHelper 只接受 Visual / Visual3D；其它节点（如 ContentElement）直接跳过。
            if (node is not Visual && node is not Visual3D) continue;

            int childCount = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < childCount; i++)
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }

        return hits;
    }

    /// <summary>
    /// 单个 Border 的判定：四角最大值 &gt; 短边/2 + 0.5 → 报。
    /// 0.5 是像素容差（布局亚像素舍入），避免 20×40 元素半径正好 10 时因浮点误差误报。
    /// </summary>
    private static bool Check(Border border, Action<string> report)
    {
        if (border.Tag is string tag &&
            tag.Contains(SkipTag, StringComparison.OrdinalIgnoreCase))
            return false;

        // 【为什么不查 KxCapsuleBorder（2026-11-24 Sinan 实测）】
        // 它构造时就把 CornerRadius 绑到 ActualHeight/2。当元素"比高还窄"（例如只有一位
        // 数字的状态徽标 18×20，半径=10）时，横向半径 10 > 宽/2=9，判据会命中——但这不是
        // 缺陷：WPF 把横向钳到 9，画出来正是"两端半圆"的胶囊，本来就该是这个样子。
        // 不排除它的话，每块单字徽标都会报一条 WARN，把真正的问题淹掉。
        if (border is KxCapsuleBorder) return false;

        double w = border.ActualWidth;
        double h = border.ActualHeight;
        if (w <= 0 || h <= 0) return false; // 未布局 / Collapsed：无从判断，跳过

        CornerRadius c = border.CornerRadius;
        double maxCorner = Math.Max(Math.Max(c.TopLeft, c.TopRight), Math.Max(c.BottomLeft, c.BottomRight));
        double limit = Math.Min(w, h) / 2.0;
        if (maxCorner <= limit + 0.5) return false;

        string name = string.IsNullOrEmpty(border.Name) ? "(未命名)" : border.Name;
        string owner = border.TemplatedParent?.GetType().Name ?? border.GetType().Name;
        report(
            $"[KxRadiusAudit] {w:0.#}×{h:0.#} 的元素 CornerRadius={maxCorner:0.#}，" +
            $"短边一半只有 {limit:0.#} → WPF 会按横/纵分别钳制，端头变成椭圆（看起来像“尖/扁”）。" +
            $"元素={border.GetType().Name} \"{name}\"，模板父={owner}。" +
            "改用 <ui:KxCapsuleBorder>（一行）或 CapsuleRadiusConverter。");

        return true;
    }
}
