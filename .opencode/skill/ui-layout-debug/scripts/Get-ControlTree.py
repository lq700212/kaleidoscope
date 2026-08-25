# -*- coding: utf-8 -*-
"""
Get-ControlTree.py — 枚举指定 WinForms 进程主窗体的全部子窗口【实际屏幕矩形】。
界面布局问题的决定性证据（ConfigEditor 布局排查沉淀，用法见 AGENTS.md「已知界面布局关键点」）。

【为什么需要它】界面"显示不全/被遮挡/错位"类问题，截图目测坐标误差大、SunnyUI 自绘控件
不进 UIAutomation 树——只有 Win32 EnumChildWindows 拿到的实际矩形是精确事实。

【决定性判据】若某 Dock=Fill 的容器底边 == 其父容器底边 → 它没给底部条让位
→ 查 Controls 添加顺序（Fill 必须最先 Add，见 AGENTS.md Dock 顺序规则）。

依赖：pywin32（pip install pywin32）
用法：
    python Get-ControlTree.py                          # 启动编辑器并枚举
    python Get-ControlTree.py --click 170 182          # 启动后先点树节点再枚举
    python Get-ControlTree.py --attach                 # 附加到已运行的编辑器
    python Get-ControlTree.py --topmost                # 置顶窗口（被遮挡时）
"""
import argparse
import os
import subprocess
import sys
import time

import win32api
import win32gui
import win32process

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
HWND_TOPMOST = -1
SWP_NOSIZE = 0x0001
SWP_SHOWWINDOW = 0x0040


def click_at(x, y):
    """在屏幕绝对坐标处单击左键（选中树节点等交互用）。"""
    win32api.SetCursorPos((x, y))
    time.sleep(0.3)
    win32api.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.08)
    win32api.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(1.2)  # 等布局稳定


def get_pids_by_image(process_name):
    """用 tasklist 查进程名对应的 PID 集合（避免引入 psutil 依赖）。"""
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
    """按 PID 集合找主窗口句柄：枚举可见顶层窗口，匹配 PID 集合。"""
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

    ap = argparse.ArgumentParser(description="枚举子窗口实际矩形（布局问题决定性证据）")
    ap.add_argument("--process", default="KaleidoscopeConfigEditor", help="进程名（--attach 时用）")
    ap.add_argument("--exe", default=default_exe, help="要启动的 exe 路径（默认编辑器 Debug 输出）")
    ap.add_argument("--attach", action="store_true", help="附加到已运行进程而不是启动新进程")
    ap.add_argument("--click", nargs=2, type=int, metavar=("X", "Y"),
                    help="枚举前在窗体内坐标处单击一下（选中树节点用）")
    ap.add_argument("--topmost", action="store_true", help="置顶窗口（被遮挡时开启）")
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
            sys.exit(f"找不到已运行进程：{args.process}.exe")  # 给进程起来留时间

    # ── 轮询等待主窗体句柄就绪（MainWindow 类句柄可能延迟，不能取一次就用）──
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

    if args.topmost:
        win32gui.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW)
        time.sleep(0.5)

    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    print(f"主窗体: 句柄={hwnd} 矩形=({left},{top})-({right},{bottom})")

    if args.click:
        click_at(left + args.click[0], top + args.click[1])

    # ── 枚举全部子窗口矩形（按 Y、X 排序输出）──
    rows = []

    def enum_handler(child, _):
        rect = win32gui.GetWindowRect(child)
        rows.append({
            "cls": win32gui.GetClassName(child),
            "l": rect[0], "t": rect[1], "r": rect[2], "b": rect[3],
            "w": rect[2] - rect[0], "h": rect[3] - rect[1],
        })
        return True

    win32gui.EnumChildWindows(hwnd, enum_handler, None)
    rows.sort(key=lambda r: (r["t"], r["l"]))
    print(f"{'类名':<46}{'Left':>6}{'Top':>6}{'Right':>7}{'Bottom':>7}{'W':>6}{'H':>5}")
    for r in rows:
        print(f"{r['cls']:<46}{r['l']:>6}{r['t']:>6}{r['r']:>7}{r['b']:>7}{r['w']:>6}{r['h']:>5}")

    print()
    print("提示：Dock=Fill 的容器 Bottom 应小于父容器 Bottom（给底部条让位）；")
    print("若相等 = 没让位 = Controls 添加顺序问题（Fill 必须最先 Add，见 AGENTS.md）。")

    # 非附加模式下结束启动的进程（附加模式不动别人的进程）
    if not args.attach:
        for pid in get_pids_by_image(args.process):
            subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)


if __name__ == "__main__":
    main()
