# -*- coding: utf-8 -*-
"""
Get-WindowShot.py — 对指定 WinForms 窗口做【真实屏幕截图】（可选先点击某位置）。
界面显示问题的视觉证据（Kaleidoscope 界面布局调试技能沉淀，用法见 .opencode/skill/ui-layout-debug/SKILL.md）。

【三个血泪坑，本脚本已内置规避】
1. 不要用 PrintWindow：SunnyUI 自绘控件在 WM_PRINT 下渲染不全（只画顶部一条），
   会产生"控件被裁"的假象误导排查——必须置顶窗口后用 ImageGrab 真实屏幕截图；
2. 不要用 SetForegroundWindow：会被系统前台锁定策略静默拒绝（截到别的窗口）——用 TOPMOST 置顶；
3. 主窗体句柄可能延迟就绪：必须轮询等待，不能取一次就用。

依赖：pywin32 + Pillow（pip install pywin32 Pillow）
用法：
    python Get-WindowShot.py                          # 启动编辑器直接截图
    python Get-WindowShot.py --click 170 227          # 先点树节点再截图
    python Get-WindowShot.py --attach --out s.png     # 附加已运行进程
"""
import argparse
import os
import subprocess
import sys
import time

import win32api
import win32gui
import win32process
from PIL import ImageGrab

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
HWND_TOPMOST = -1
SWP_NOSIZE = 0x0001
SWP_SHOWWINDOW = 0x0040


def click_at(x, y):
    """在屏幕绝对坐标处单击左键。"""
    win32api.SetCursorPos((x, y))
    time.sleep(0.3)
    win32api.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.08)
    win32api.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(1.2)  # 等布局稳定


def get_pids_by_image(process_name):
    """用 tasklist 查进程名对应的 PID 集合（避免引入 psutil 依赖）。
    注意：tasklist 会把长映像名截断到 25 字符，必须用前缀匹配。"""
    out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {process_name}.exe"],
                         capture_output=True, text=True).stdout
    pids = set()
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[0].lower().startswith(process_name.lower()):
            try:
                pids.add(int(parts[1]))
            except ValueError:
                pass
    return pids


def find_main_window(pids):
    """按 PID 集合找主窗口句柄：枚举可见顶层窗口。"""
    if not pids:
        return 0
    found = []

    def on_window(hwnd, _):
        if win32gui.IsWindowVisible(hwnd) and win32gui.GetWindowText(hwnd):
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            if pid in pids:
                found.append(hwnd)
        return True

    win32gui.EnumWindows(on_window, None)
    return found[0] if found else 0


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    default_exe = os.path.normpath(os.path.join(
        here, "..", "..", "..", "..", "ConfigEditor", "bin", "Debug", "KaleidoscopeConfigEditor.exe"))
    default_out = os.path.join(here, "window_shot.png")

    ap = argparse.ArgumentParser(description="置顶窗口 + 真实屏幕截图（视觉证据）")
    ap.add_argument("--process", default="KaleidoscopeConfigEditor", help="进程名（--attach 时用）")
    ap.add_argument("--exe", default=default_exe, help="要启动的 exe 路径（默认编辑器 Debug 输出）")
    ap.add_argument("--attach", action="store_true", help="附加到已运行进程而不是启动新进程")
    ap.add_argument("--click", nargs=2, type=int, metavar=("X", "Y"),
                    help="截图前在窗体内坐标处单击一下（选中树节点用）")
    ap.add_argument("--out", default=default_out, help="截图保存路径（默认本目录 window_shot.png）")
    args = ap.parse_args()

    # ── 启动或附加进程 ──
    # 【启动模式】直接记下 Popen 返回的 PID：tasklist 会把长映像名截断到 25 字符，
    # 按名字反查不可靠；Popen 的 PID 是精确事实。attach 模式才走 tasklist 前缀匹配。
    target_pids = set()
    if not args.attach:
        if not os.path.isfile(args.exe):
            sys.exit(f"exe 不存在：{args.exe}（先构建，或用 --exe 指定路径）")
        proc = subprocess.Popen([args.exe], cwd=os.path.dirname(args.exe))
        target_pids.add(proc.pid)
        time.sleep(1.0)
    else:
        target_pids = get_pids_by_image(args.process)
        if not target_pids:
            sys.exit(f"找不到已运行进程：{args.process}.exe")

    hwnd = 0
    deadline = time.time() + 10.0
    while time.time() < deadline:
        hwnd = find_main_window(target_pids)
        if hwnd:
            break
        time.sleep(0.5)
    if not hwnd:
        sys.exit("拿不到主窗体句柄（进程未启动或无窗口）")
    time.sleep(2.0)  # 等 SunnyUI 完成首帧布局

    # TOPMOST 置顶（不用 SetForegroundWindow——会被前台锁静默拒绝，截到别的窗口）
    win32gui.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW)
    time.sleep(0.6)

    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    w, h = right - left, bottom - top
    print(f"窗体: ({left},{top}) {w}x{h}")

    if args.click:
        click_at(left + args.click[0], top + args.click[1])

    # 真实屏幕截图（不用 PrintWindow——SunnyUI 自绘控件在 WM_PRINT 下渲染不全）
    img = ImageGrab.grab(bbox=(left, top, right, bottom))
    img.save(args.out)
    print(f"截图已保存: {args.out}")

    if not args.attach:
        try:
            proc.kill()
        except Exception:
            pass


if __name__ == "__main__":
    main()
