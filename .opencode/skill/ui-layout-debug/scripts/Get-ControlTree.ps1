#requires -Version 5.1
<#
.SYNOPSIS
  枚举指定 WinForms 进程主窗体的全部子窗口【实际屏幕矩形】——界面布局问题的决定性证据。
  （Kaleidoscope 界面布局调试技能沉淀，用法见 .opencode/skill/ui-layout-debug/SKILL.md）

.DESCRIPTION
  【为什么需要它】界面"显示不全/被遮挡/错位"类问题，截图目测坐标误差大、SunnyUI 自绘控件
  不进 UIAutomation 树——只有 Win32 EnumChildWindows 拿到的实际矩形是精确事实。
  【决定性判据】若某 Dock=Fill 的容器底边 == 其父容器底边 → 它没给底部条让位 → 查 Controls
  添加顺序（Fill 必须最先 Add，见 AGENTS.md Dock 顺序规则）。

.EXAMPLE
  .\Get-ControlTree.ps1                                    # 启动编辑器并枚举
  .\Get-ControlTree.ps1 -ClickX 170 -ClickY 227            # 启动后先点树节点再枚举
  .\Get-ControlTree.ps1 -Attach -ProcessName Notepad       # 附加到已运行进程
#>
param(
    [string]$ProcessName = "KaleidoscopeConfigEditor",
    [string]$ExePath = "",          # 为空=不启动，配合 -Attach 附加已运行进程
    [switch]$Attach,                # 附加已运行进程（否则按 ExePath 启动）
    [int]$ClickX = -1,              # 可选：枚举前在窗体内坐标处点一下（选中树节点用）
    [int]$ClickY = -1,
    [switch]$Topmost                # 置顶窗口（被遮挡时开启）
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
if (-not ("Native.CtrlTree" -as [type])) {
    $sig = @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc cb, IntPtr lParam);
[DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
public struct RECT { public int Left, Top, Right, Bottom; }
'@
    Add-Type -MemberDefinition $sig -Name CtrlTree -Namespace Native | Out-Null
}

# ── 拿主窗体句柄（启动或附加；句柄可能延迟就绪，必须轮询等待）──
if ($Attach) {
    $p = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
} else {
    if ($ExePath -eq "") { $ExePath = Join-Path $PSScriptRoot "..\..\..\..\ConfigEditor\bin\Debug\KaleidoscopeConfigEditor.exe" }
    $p = Start-Process -FilePath $ExePath -PassThru
}
$handle = [IntPtr]::Zero
for ($i = 0; $i -lt 20; $i++) {
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { $handle = $p.MainWindowHandle; break }
    Start-Sleep -Milliseconds 500
}
if ($handle -eq [IntPtr]::Zero) { throw "拿不到主窗体句柄（进程未启动或无窗口）" }
Start-Sleep -Seconds 2   # 等 SunnyUI 完成首帧布局

if ($Topmost) {
    [Native.CtrlTree]::SetWindowPos($handle, [IntPtr](-1), 0, 0, 0, 0, 0x0041) | Out-Null  # TOPMOST|NOSIZE|SHOWWINDOW
    Start-Sleep -Milliseconds 500
}

$rect = New-Object Native.CtrlTree+RECT
[Native.CtrlTree]::GetWindowRect($handle, [ref]$rect) | Out-Null
"主窗体: 句柄=$handle 矩形=($($rect.Left),$($rect.Top))-($($rect.Right),$($rect.Bottom))"

# ── 可选：先点击窗体内坐标（选中树节点，让条件显示的容器出现）──
if ($ClickX -ge 0 -and $ClickY -ge 0) {
    [Native.CtrlTree]::SetCursorPos($rect.Left + $ClickX, $rect.Top + $ClickY) | Out-Null
    Start-Sleep -Milliseconds 300
    [Native.CtrlTree]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)   # LEFTDOWN
    Start-Sleep -Milliseconds 80
    [Native.CtrlTree]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)   # LEFTUP
    Start-Sleep -Milliseconds 1200   # 等布局稳定
}

# ── 枚举全部子窗口矩形（按 Y 排序输出）──
$rows = New-Object System.Collections.ArrayList
$cb = [Native.CtrlTree+EnumProc] {
    param($h, $l)
    $cls = New-Object System.Text.StringBuilder 128
    [Native.CtrlTree]::GetClassName($h, $cls, 128) | Out-Null
    $r = New-Object Native.CtrlTree+RECT
    [Native.CtrlTree]::GetWindowRect($h, [ref]$r) | Out-Null
    [void]$rows.Add([PSCustomObject]@{
        类名   = $cls.ToString()
        Left   = $r.Left; Top = $r.Top; Right = $r.Right; Bottom = $r.Bottom
        W      = $r.Right - $r.Left
        H      = $r.Bottom - $r.Top
    })
    return $true
}
[Native.CtrlTree]::EnumChildWindows($handle, $cb, [IntPtr]::Zero) | Out-Null

$rows | Sort-Object Top, Left | Format-Table -AutoSize | Out-Host
""
"提示：Dock=Fill 的容器 Bottom 应小于父容器 Bottom（给底部条让位）；"
"若相等 = 没让位 = Controls 添加顺序问题（Fill 必须最先 Add，见 AGENTS.md）。"
if (-not $Attach) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
