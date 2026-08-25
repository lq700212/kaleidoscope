#requires -Version 5.1
<#
.SYNOPSIS
  对指定 WinForms 窗口做【真实屏幕截图】（可选先点击某位置）——界面显示问题的视觉证据。
  （Kaleidoscope 界面布局调试技能沉淀，用法见 .opencode/skill/ui-layout-debug/SKILL.md）

.DESCRIPTION
  【三个血泪坑，本脚本已内置规避】
  1. 不要用 PrintWindow：SunnyUI 自绘控件在 WM_PRINT 下渲染不全（只画顶部一条），
     会产生"控件被裁"的假象误导排查——必须用 SetWindowPos(TOPMOST) + CopyFromScreen 真实屏幕截图；
  2. 不要用 SetForegroundWindow：会被系统前台锁定策略静默拒绝（截到别的窗口）——用 TOPMOST 置顶；
  3. MainWindowHandle 可能延迟为 0：必须轮询等待，不能取一次就用。

.EXAMPLE
  .\Get-WindowShot.ps1 -OutFile shot.png                      # 启动编辑器直接截图
  .\Get-WindowShot.ps1 -ClickX 170 -ClickY 227 -OutFile s.png # 先点树节点再截图
#>
param(
    [string]$ProcessName = "KaleidoscopeConfigEditor",
    [string]$ExePath = "",
    [switch]$Attach,
    [int]$ClickX = -1,
    [int]$ClickY = -1,
    [string]$OutFile = "$(Join-Path $PSScriptRoot 'window_shot.png')"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
if (-not ("Native.WinShot" -as [type])) {
    $sig = @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
public struct RECT { public int Left, Top, Right, Bottom; }
'@
    Add-Type -MemberDefinition $sig -Name WinShot -Namespace Native | Out-Null
}

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
if ($handle -eq [IntPtr]::Zero) { throw "拿不到主窗体句柄" }
Start-Sleep -Seconds 2

# TOPMOST 置顶（不用 SetForegroundWindow——会被前台锁静默拒绝，截到别的窗口）
[Native.WinShot]::SetWindowPos($handle, [IntPtr](-1), 0, 0, 0, 0, 0x0041) | Out-Null
Start-Sleep -Milliseconds 600

$rect = New-Object Native.WinShot+RECT
[Native.WinShot]::GetWindowRect($handle, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
"窗体: ($($rect.Left),$($rect.Top)) ${w}x${h}"

if ($ClickX -ge 0 -and $ClickY -ge 0) {
    [Native.WinShot]::SetCursorPos($rect.Left + $ClickX, $rect.Top + $ClickY) | Out-Null
    Start-Sleep -Milliseconds 300
    [Native.WinShot]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [Native.WinShot]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1200
}

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"截图已保存: $OutFile"
if (-not $Attach) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
