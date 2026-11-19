// =========================================================================
// KxShellInterop：窗口外壳共享构造与 Win32 互操作（V2.2.1 自两窗基类去重收拢）
//
// 【干什么】ChromeDialogWindow / ChromeBorderlessWindow 两个窗基类的公共外壳
//   能力收口到这一个静态类，杜绝"同一份逻辑两个基类各抄一份"漂移：
//   ① BuildChrome——WindowChrome 构造（CaptionHeight/缩放边参数化；
//     GlassFrameThickness=0 / CornerRadius=0 / UseAeroCaptionButtons=false
//     固定为库口径：去 DWM 玻璃帧残留、不产生视觉圆角、不叠加系统三钮）；
//   ② EnableMaximizedWorkAreaFix——WM_GETMINMAXINFO 最大化工作区修正
//     （V2.2.0 自宿主 YoloDetector v2.10.2 实测口径沉库）。
//
// 【最大化红线（YoloDetector v2.10.2 实测沉淀）】
//   现象：WindowChrome 对 WM_GETMINMAXINFO 的默认处理把最大化尺寸扩到整个
//   显示器（desktop 实测最大化 bounds = 1934×1094 @(-7,-7)，屏幕 1920×1080）
//   ——底部盖住任务栏，四周探出的 7px 藏缩放热区。
//   修复：窗口过程钩子把最大化目标改写为最近显示器的**工作区**（不含任务栏），
//   handled=true 阻止 DefWindowProc 用显示器全尺寸覆盖。
//
// 【怎么用】
//   · 窗基类在构造/DP 回调里调 BuildChrome 并 SetWindowChrome；
//   · 需要修正的窗在 OnSourceInitialized 里调 EnableMaximizedWorkAreaFix
//     （钩子随 HwndSource 销毁自动失效，窗口关闭即断，无需显式移除）；
//   · 两个窗基类的开关 DP（ChromeBorderlessWindow.FixMaximizedToWorkArea
//     缺省 true=YoloDetector 红线修复；ChromeDialogWindow.FixMaximizedToWorkArea
//     缺省 false=两宿主现状不变，HJVision 诉求到了一行配置即开）。
//
// 【线程】UI 线程调用（WPF 常规纪律）。
// =========================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Kaleidoscope.UI.Views;

public static class KxShellInterop
{
    /// <summary>
    /// 构造库口径的 WindowChrome 外壳：CaptionHeight=标题区命中高度（顶栏空白
    /// 即"标题区"——拖动/双击最大化/贴边分屏），ResizeBorderThickness=边缘缩放
    /// 热区宽（0=不可缩放），GlassFrameThickness=0（否则顶部残留透明系统框），
    /// CornerRadius=0（WindowChrome 的该属性只影响非客户区计算不产生视觉圆角，
    /// 视觉圆角由窗基类的分层透明/Region 方案各自负责），UseAeroCaptionButtons=
    /// false（不叠加系统三钮，宿主自绘）。调用方拿到后 WindowChrome.
    /// SetWindowChrome(window, chrome) 挂上。
    /// </summary>
    public static WindowChrome BuildChrome(double captionHeight, double resizeBorderThickness)
    {
        return new WindowChrome
        {
            CaptionHeight = captionHeight,
            ResizeBorderThickness = new Thickness(resizeBorderThickness),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };
    }

    /// <summary>
    /// 给窗挂 WM_GETMINMAXINFO 最大化工作区修正钩子（红线背景见类头）。
    /// 须在 OnSourceInitialized（句柄已建、窗口未显示）时机调用；重复调用安全
    /// （幂等闸：同一窗只挂一次）；钩子随 HwndSource 销毁自动失效，窗口关闭即断。
    /// </summary>
    public static void EnableMaximizedWorkAreaFix(Window window)
    {
        if (window == null || _workAreaFixApplied.Contains(window)) return;
        var source = PresentationSource.FromVisual(window) as HwndSource;
        if (source == null) return;   // 句柄未就绪（应在 OnSourceInitialized 调用）
        _workAreaFixApplied.Add(window);
        source.AddHook(WndProc);

        // 窗口关闭时移除闸记录（一个进程的窗数量级很小，闸表可忽略不计）
        window.Closed += delegate { _workAreaFixApplied.Remove(window); };
    }

    // ---- 幂等闸（防同一窗重复 AddHook：HwndSource.AddHook 无去重，重复挂会让
    //      WndProc 被调多次——本 WndProc 幂等无害，但闸更干净）----
    private static readonly HashSet<Window> _workAreaFixApplied = new HashSet<Window>();

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            AdjustMaximizedBounds(hwnd, lParam);
            handled = true; // 阻止 DefWindowProc 用显示器全尺寸覆盖我们写入的工作区值
        }
        return IntPtr.Zero;
    }

    /// <summary>把最大化尺寸/位置改写为最近显示器的"工作区"（任务栏以外的区域）</summary>
    private static void AdjustMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        // 泛型重载（4.5.1+ 双目标可用）：结构体值类型无 null 语义，免去非泛型版
        // 的 object 拆箱可空警告
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MONITORINFO();
        info.cbSize = Marshal.SizeOf(info);
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        // 最大化目标 = 工作区；ptMaxPosition 相对显示器原点
        // （任务栏在底部时 work.top == monitor.top → 0；在顶部/侧边也正确）
        mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
        mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
        mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
        mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
