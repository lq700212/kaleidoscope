// =========================================================================
// ShellDialogForm：WinForms 圆角自绘弹窗基类（V1.0.0 骨架首建）
//
// 【干什么】对标宿主 HJVision 原版 SubForm/ThemeDialogForm（V4.4.342 从 40+ 弹窗
//   抽取的统一基类 : SunnyUI.UIForm）的口径，但零 SunnyUI 依赖（库级沉淀红线）：
//   ①Region 物理圆角（深 10 / 浅 14，对位原版 AppTheme.TryRoundWindow 双主题口径；
//     WinForms 子控件是独立 HWND，GDI Region 裁剪连 HWND 一起裁——WPF 分层窗
//     "HWND 不渲染"的坑在 WinForms 侧不存在，物理 Region 是唯一正确解）；
//   ②GDI 自绘 36px 渐变标题栏（左标题/可选图标胶囊 + 右 —/▢/× 系统钮，配色走
//     KxThemeTokens 语义键）；
//   ③模板方法时序一比一对位原版：OnShown → OnFirstShow → ApplyTo → OnAfterThemeApply
//     （原版 ThemeDialogForm 的四段模板，子类覆写挂各自的"首显一次性"与"换肤可重刷"逻辑）；
//   ④静态主题事件订阅/退订模板内置（KxThemeTokens.Changed 订阅 + OnFormClosed/
//     Disposed 双出口退订——原版 AppTheme.cs 文件头"静态事件双出口退订"铁律的红线，
//     子类继承即得正确生命周期，不碰事件就不会泄漏）。
//
// 【为什么】原版 ThemeDialogForm 绑死 SunnyUI.UIForm（第三方 3.3.0 DLL）——库级
//   沉淀必须零第三方 UI 依赖；本类把原版"继承即得换肤三件套+圆角+渐变标题栏"的
//   目标用纯 GDI 自绘实现，观感语义一比一。当前无立即消费宿主（原版短期不切库），
//   是原版解耦 SunnyUI 时的替换落点（提前储备，见工程 csproj 头注释）。
//
// 【怎么接】子类 Form 继承本类，XAML 无（WinForms 纯代码/Designer）：
//     public partial class SomeDialog : ShellDialogForm {
//         protected override void OnFirstShow() { ...首显一次性装配... }
//         protected override void OnAfterThemeApply() { ...按 KxThemeTokens.Get 取色重刷... }
//     }
//   圆角在 OnShown 后自动 SetWindowRgn（含 DPI 物理像素换算）；标题栏文字取 Text 属性。
//
// 【与原版差异说明（评审注意）】①原版标题栏 45px 品牌条带图标胶囊——本骨架 36px
//   对位 WPF 版 TitleBar 口径（两栈统一）；②原版四角 AA 弧线角件（抗锯齿修边）是
//   GDI+ Region 无抗锯齿的补偿——本骨架同款保留（Paint 角件）。
// 【线程】UI 线程构造/使用（WinForms 常规纪律）。
// =========================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kaleidoscope.UI.WinForms;

public class ShellDialogForm : Form
{
    // ---- 状态 ----
    private bool _firstShown;         // OnFirstShow 只走一次闸（原版 ThemeDialogForm 同名语义）
    private bool _disposedUnsub;      // 双出口退订闸（Changed/Disposed 只退一次）

    // ---- Win32（Region 圆角）----

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    public ShellDialogForm()
    {
        // 无系统边框 + 固定尺寸弹窗缺省（可缩放窗子类自行覆写 FormBorderStyle/ResizeMode）
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9f);   // 9pt≈12px，与宿主 WPF 版口径一致
        // 主题底/字色走 Token（换肤可重刷——OnAfterThemeApply 钩子）
        BackColor = KxThemeTokens.Get(KxToken.FormBg);
        ForeColor = KxThemeTokens.Get(KxToken.Fore);
        DoubleBuffered = true;   // 自绘标题栏防闪
        // 静态主题事件订阅（退订见 OnFormClosed/Disposed 双出口——血泪铁律见 KxThemeTokens.Changed）
        KxThemeTokens.Changed += OnThemeChanged;
        Disposed += delegate { UnsubscribeTheme(); };
    }

    /// <summary>主题切换广播入口（刷新底/字色+钩子；UI 线程外触发时 Invoke 回来）。</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(() => ApplyTheme()); } catch { }   // 句柄已释放时吞掉
            return;
        }
        ApplyTheme();
    }

    /// <summary>换肤刷新：底/字色 Token 重取 + 圆角 Region 重裁 + 子类钩子。</summary>
    private void ApplyTheme()
    {
        if (IsDisposed) return;
        BackColor = KxThemeTokens.Get(KxToken.FormBg);
        ForeColor = KxThemeTokens.Get(KxToken.Fore);
        ApplyRoundRegion();
        OnAfterThemeApply();
        Invalidate(true);
    }

    /// <summary>退订（双出口：OnFormClosed 与 Disposed 各调一次，_disposedUnsub 闸只退一次）。</summary>
    private void UnsubscribeTheme()
    {
        if (_disposedUnsub) return;
        _disposedUnsub = true;
        KxThemeTokens.Changed -= OnThemeChanged;
    }

    /// <summary>首显模板：OnShown → (一次性) OnFirstShow → ApplyTo(本类即 ApplyTheme) → OnAfterThemeApply。</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRoundRegion();          // Region 圆角要在句柄+尺寸就绪后裁
        if (!_firstShown)
        {
            _firstShown = true;
            try { OnFirstShow(); } catch { }   // 首显钩子异常不拦显示（原版同吞口）
        }
        ApplyTheme();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnsubscribeTheme();          // 出口一：正常关窗
        base.OnFormClosed(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (_firstShown) ApplyRoundRegion();   // 可缩放窗拖边后重裁 Region
    }

    /// <summary>Region 物理圆角：按 DPI 换算物理像素，深 10 / 浅 14（原版 TryRoundWindow 双主题口径）。</summary>
    private void ApplyRoundRegion()
    {
        try
        {
            if (!IsHandleCreated) return;
            float r = KxThemeTokens.IsDark ? 10f : 14f;
            using var g = CreateGraphics();
            float dpi = g.DpiX / 96f;
            int wd = (int)Math.Ceiling(Width * dpi) + 1;
            int ht = (int)Math.Ceiling(Height * dpi) + 1;
            int rr = Math.Max(2, (int)(r * dpi));
            IntPtr hRgn = CreateRoundRectRgn(0, 0, wd, ht, rr * 2, rr * 2);
            SetWindowRgn(Handle, hRgn, true);
        }
        catch { }   // Region 裁剪失败只影响四角弧，不拦使用（原版同吞口）
    }

    /// <summary>首显一次性钩子（原版 OnFirstShow 同名模板：只走一次的装配，如控件构建/事件挂接）。</summary>
    protected virtual void OnFirstShow()
    {
    }

    /// <summary>换肤可重刷钩子（原版 OnAfterThemeApply 同名模板：按 Token 取色重刷自绘件）。</summary>
    protected virtual void OnAfterThemeApply()
    {
    }

    /// <summary>
    /// 标题栏自绘：36px 渐变（Token TitleGradFrom→To 45°）+ 左标题文字 + 右 —/▢/×
    /// 三钮位（20px 步距热区，点击处理见 WndProc 命中区段）。▢ 只有 MaximizeBox=true
    /// 才画（与原版 UIForm 固定对话框灰 ▢ 语义一致）。
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int h = TitleBarHeight;
        var rect = new Rectangle(0, 0, Width, h);
        using var brush = KxThemeTokens.CreateTitleBrush(rect);
        g.FillRectangle(brush, rect);
        // 四角 AA 弧线角件（Region 无抗锯齿的毛边补偿，原版 AppTheme 角件同语义）
        using var arcPen = new Pen(Color.FromArgb(120, KxThemeTokens.Get(KxToken.TitleFg)), 1f);
        int r = (int)(KxThemeTokens.IsDark ? 10f : 14f) - 1;
        g.DrawArc(arcPen, 0, 0, r * 2, r * 2, 180, 90);
        g.DrawArc(arcPen, Width - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
        // 标题文字（Text 属性；字色 Token.TitleFg）
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(12, 0, Width - 140, h), KxThemeTokens.Get(KxToken.TitleFg),
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        // 右端系统钮字符 —/▢/×（▢ 按 MaximizeBox 显隐；字符热区见 OnMouseClick）
        var fg = KxThemeTokens.Get(KxToken.TitleFg);
        TextRenderer.DrawText(g, "—", Font, SysRect(0), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (MaximizeBox)
            TextRenderer.DrawText(g, "▢", Font, SysRect(1), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "✕", Font, SysRect(2), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>标题栏高度（36px=库 WPF 版 TitleBar 口径；子类可覆写加高）。</summary>
    protected virtual int TitleBarHeight => 36;

    /// <summary>右侧第 i 个系统钮热区（× 最右；▢ 按 MaximizeBox 显隐计入序位）。</summary>
    private Rectangle SysRect(int i)
    {
        int w = 40;
        int x = Width - w * (i + 1) - 4;
        return new Rectangle(x, 0, w, TitleBarHeight);
    }

    /// <summary>标题栏交互：空白区按下=拖动（SC_MOVE+HTCAPTION 惯例）；钮热区=最小化/最大化/关闭。</summary>
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Y > TitleBarHeight || e.Button != MouseButtons.Left) return;
        if (HitSys(2, e.X)) { Close(); return; }
        if (MaximizeBox && HitSys(1, e.X))
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            return;
        }
        if (HitSys(0, e.X)) { WindowState = FormWindowState.Minimized; return; }
        // 空白拖动（标题栏惯例：ReleaseCapture + WM_NCLBUTTONDOWN HTCAPTION）
        ReleaseCapture();
        _ = SendMessage(Handle, 0xA1, (IntPtr)0x02, IntPtr.Zero);
    }

    private bool HitSys(int i, int x)
    {
        var r = SysRect(i);
        return x >= r.X && x < r.X + r.Width;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
