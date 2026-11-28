using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kaleidoscope.UI.Rendering;

/// <summary>
/// KxRenderPolicy —— WPF 进程级渲染模式决策（远程串流软件检测 + 显式覆盖）。
///
/// 【干什么】决定本进程走 GPU 硬件渲染还是 CPU 软件渲染，并把决策落到
///   <see cref="RenderOptions.ProcessRenderMode"/>（进程级、必须在任何窗口创建前设置）。
///   决策三态（对齐 YoloDetector v2.10.3 现场结论）：
///   · <c>forceSoftware = true</c>  → 强制软件渲染（现场排查/无 GPU 兜底）；
///   · <c>forceSoftware = false</c> → 强制硬件渲染（确认远程软件误报时用）；
///   · <c>forceSoftware = null</c>  → 自动：枚举进程名，命中已知远程控制/串流软件
///     （UU远程/向日葵/ToDesk/RustDesk/TeamViewer/AnyDesk/贝锐）才切软件渲染。
///
/// 【为什么需要它（两条现场血泪）】
///   ① 远程串流双指针（YoloDetector 实测）：被控端出现 D3D 高频呈现进程时，远程软件会
///      切到"游戏/高性能采集"路径，把光标自行合成进捕获帧（滞后残影），控制端又画一个
///      实时指针 → 画面双指针。切软件渲染后远程软件回到普通桌面采集路径，双指针消失。
///   ② 分层透明窗（AllowsTransparency=True）本身就是整窗软件渲染：相机 5MP 画面
///      8fps 的缩放/合成全压 CPU（AIris 实测单核 50%+，画面明显卡顿）。正常环境
///      必须走硬件渲染，只有远程串流场景才值得付出软件渲染的代价。
///
/// 【怎么接（宿主 App 启动最顶部，任何 Window 创建之前）】
/// <code>
///   // 三态来自宿主配置（INI/JSON 均可）；null = 自动检测
///   bool software = KxRenderPolicy.Apply(forceSoftwareFromConfig);
///   // 主窗构造期据此决定是否用分层透明窗（分层窗 = 软件渲染，两者必须一致）：
///   protected override bool UseLayeredWindow => KxRenderPolicy.IsSoftwareRendering;
/// </code>
///   <b>顺序红线</b>：ProcessRenderMode 在进程首个窗口句柄创建后设置无效；
///   <see cref="IsSoftwareRendering"/> 供派生窗在基类构造期读取（此时 Apply 必须已执行）。
///
/// 【分层窗与渲染模式的一致性】WPF 的 AllowsTransparency=True 会强制该窗走软件渲染，
///   与进程级 RenderMode 无关。所以：
///   · 硬件渲染环境 → 宿主主窗应重写 UseLayeredWindow=false（圆角走 SetWindowRgn
///     物理裁剪，文字恢复 ClearType，视频走 GPU）；
///   · 软件渲染环境（远程串流）→ 保持分层窗，圆角/柔影视觉完整，反正都是 CPU。
///   两者用同一个 <see cref="IsSoftwareRendering"/> 开关联动，不会出现"以为在硬件渲染
///   实则被分层窗打回 CPU"的假象。
///
/// 【线程】Apply/IsSoftwareRendering 只应在 UI 线程启动期使用；进程枚举是同步慢操作
///   （几十毫秒），只在启动时做一次，不要放到帧循环里。
/// </summary>
public static class KxRenderPolicy
{
    /// <summary>
    /// 已知远程控制/串流软件的进程名前缀（统一小写、不含 .exe）。
    /// 匹配采用前缀匹配：覆盖同一软件的多个辅助进程（如 GameViewer/Server/Service）。
    /// 新增远程软件支持时在列表追加小写前缀即可。
    /// </summary>
    public static readonly string[] KnownRemoteControlProcessPrefixes =
    {
        "gameviewer",      // 网易 UU 远程（YoloDetector 双指针事故的直接当事软件）
        "sunloginclient",  // 向日葵远程
        "todesk",          // ToDesk 远程
        "rustdesk",        // RustDesk 远程（开源）
        "teamviewer",      // TeamViewer
        "anydesk",         // AnyDesk
        "oray",            // 贝锐全家桶其余组件（OrayRemoteShell 等）
    };

    /// <summary>
    /// 最近一次 <see cref="Apply"/> 的决策结果：true = 当前进程为软件渲染。
    /// 默认 false（硬件渲染）。窗口基类构造期据此决定是否启用分层透明窗。
    /// </summary>
    public static bool IsSoftwareRendering { get; private set; }

    /// <summary>最近一次 <see cref="Apply"/> 命中的远程软件进程名（诊断用；未命中为 null）。</summary>
    public static string? MatchedRemoteProcess { get; private set; }

    /// <summary>
    /// 判定是否应使用软件渲染（纯函数，便于单测/宿主自检）。
    /// </summary>
    /// <param name="forceSoftware">配置覆盖：true/false 直接生效；null = 自动检测。</param>
    /// <param name="processNames">当前运行进程名列表（调用方注入；null 视为无远程环境）。</param>
    /// <param name="matched">自动检测命中的进程名（未命中为 null）。</param>
    /// <returns>true = 应启用软件渲染。</returns>
    public static bool ShouldUseSoftwareRendering(bool? forceSoftware,
                                                 IEnumerable<string>? processNames,
                                                 out string? matched)
    {
        matched = null;
        if (forceSoftware.HasValue) return forceSoftware.Value;
        if (processNames == null) return false;

        foreach (string rawName in processNames)
        {
            if (string.IsNullOrEmpty(rawName)) continue;

            string name = NormalizeProcessName(rawName);
            foreach (string prefix in KnownRemoteControlProcessPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    matched = rawName;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 应用渲染决策：设置 <see cref="RenderOptions.ProcessRenderMode"/> 并记录结果。
    /// <b>必须在任何窗口创建之前调用一次</b>（进程级设置，重复调用以最后一次为准）。
    /// </summary>
    /// <param name="forceSoftware">配置覆盖三态；null = 自动检测远程控制软件。</param>
    /// <returns>true = 已切软件渲染；false = 走默认硬件渲染。</returns>
    public static bool Apply(bool? forceSoftware = null)
    {
        string? matched;
        bool software = ShouldUseSoftwareRendering(forceSoftware, GetRunningProcessNames(), out matched);

        IsSoftwareRendering = software;
        MatchedRemoteProcess = matched;

        // 软件 = SoftwareOnly（明确不走 D3D）；硬件 = Default（硬件优先、驱动异常自动回落，
        // 比强制 Hardware 稳——老工控机显卡驱动不完整时强制 Hardware 会直接白屏/崩）
        RenderOptions.ProcessRenderMode = software ? RenderMode.SoftwareOnly : RenderMode.Default;

        if (software)
        {
            Trace.WriteLine(matched == null
                ? "[KxRenderPolicy] 软件渲染（配置强制）"
                : "[KxRenderPolicy] 软件渲染（检测到远程控制软件：" + matched + "）");
        }
        else
        {
            Trace.WriteLine("[KxRenderPolicy] 硬件渲染（未检测到远程控制软件）");
        }
        return software;
    }

    /// <summary>
    /// 枚举当前系统进程名列表（Apply 内部调用一次；失败返回空列表，绝不阻断启动）。
    /// </summary>
    public static IReadOnlyList<string> GetRunningProcessNames()
    {
        var names = new List<string>();
        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    if (!string.IsNullOrEmpty(process.ProcessName)) names.Add(process.ProcessName);
                }
            }
        }
        catch (Exception ex)
        {
            // 个别受保护进程读元数据可能抛异常；整体失败按"无远程环境"处理
            Trace.WriteLine("[KxRenderPolicy] 进程枚举失败（按无远程环境处理）：" + ex.Message);
        }
        return names;
    }

    /// <summary>进程名规范化：去空白、去 .exe 后缀、转小写（ProcessName 本身不含 .exe，主要服务测试注入）。</summary>
    public static string NormalizeProcessName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        string trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(0, trimmed.Length - 4);
        return trimmed.ToLowerInvariant();
    }
}
