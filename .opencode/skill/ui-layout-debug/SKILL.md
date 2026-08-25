---
name: ui-layout-debug
description: Kaleidoscope 项目专属的 WinForms/SunnyUI 界面布局调试技能。当用户提到界面显示不全、控件被遮挡、位置错位、布局不对、预设条/工具条/状态条异常，或需要调试 ConfigEditor、Demo 等项目内 WinForms 界面的显示问题时使用。提供已固化的自动化调试脚本（枚举子窗口实际矩形、置顶真实截图）与本项目沉淀的布局规则。
---

# Kaleidoscope 界面布局调试技能

本项目的界面调试工具与经验已固化为脚本，**遇到任何界面显示问题，第一步就跑脚本拿数据，
不要从表层症状开始推测**（历史教训：先怀疑布局算法/事件时序/控件库绕了三轮，最后 30 秒
靠矩形数据定位）。

## 调试脚本（本目录 scripts/ 下，Python 与 PowerShell 双版本同功能）

| 脚本 | 用途 | 依赖 |
|---|---|---|
| `scripts/Get-ControlTree.py` / `.ps1` | **枚举子窗口实际屏幕矩形（决定性证据）**，按 Y 排序输出 | Python 版需 pywin32；PS 版零依赖 |
| `scripts/Get-WindowShot.py` / `.ps1` | 置顶窗口 + 可选点击 + **真实屏幕截图**（视觉证据） | Python 版另需 Pillow |

### 用法示例

```powershell
# 枚举子窗口矩形（启动编辑器、先点击树节点"多品牌"、置顶）
python scripts/Get-ControlTree.py --click 170 227 --topmost

# 附加到已运行的进程枚举
python scripts/Get-ControlTree.py --attach

# 真实屏幕截图
python scripts/Get-WindowShot.py --click 170 182 --out shot.png

# PowerShell 版（现场无 Python 环境时用，参数为 -ClickX 170 -ClickY 227 -Topmost）
.\scripts\Get-ControlTree.ps1 -ClickX 170 -ClickY 227 -Topmost
```

参数说明：`--click/-ClickX,Y` 窗体内坐标（选中树节点用）；`--topmost/-Topmost` 置顶；
`--attach/-Attach` 附加已运行进程；`--out/-OutFile` 截图保存路径。
脚本默认操作 `ConfigEditor\bin\Debug\KaleidoscopeConfigEditor.exe`，可用 `--exe` 换目标。

## 决定性判据（拿到矩形数据后怎么看）

- **Dock=Fill 的容器底边 == 其父容器底边 = 没让位 = Controls 添加顺序错误**。
- Dock 引擎按 Controls 集合 **index 从大到小**依次扣减：**Fill 容器必须最先 Add（index 最小）**，
  Top/Bottom/Left/Right 边缘条后 Add（先处理、先扣边）。
- 顺序反了的症状极具迷惑性：**视觉可能完全正常**（边缘条靠 z-order 恰好盖在 Fill 上面），
  但 Fill 实际占满全客户区、内部全按全尺寸布局——底部内容被边缘条静默遮挡
  （本项目实测：split 后 Add 导致树/属性网格被状态条遮 30px 达数个版本，直到贴底的预设条出现才暴露）。

## 本项目沉淀的坑（排查时直接对照）

1. **截图验证两不要**：
   - 不要用 PrintWindow——SunnyUI 自绘控件在 WM_PRINT 下渲染不全（只画顶部一条），
     会产生"控件被裁"的假象；必须 TOPMOST 置顶 + CopyFromScreen/ImageGrab 真实屏幕截图。
   - 不要用 SetForegroundWindow——会被系统前台锁静默拒绝（截到别的窗口），用 SetWindowPos(HWND_TOPMOST)。
2. **SunnyUI 布局坑**：UIPanel 嵌套 UIPanel 时子控件定位可能偏移——**纯背景容器（工具条/状态条/
   预设条/中间白色层）一律用原生 Panel**；条内 SunnyUI 控件（UIButton/UILabel/UIComboBox）自绘
   不受父容器影响。SunnyUI 控件进 UIAutomation 树不全，控件定位一律用 Win32 EnumChildWindows。
3. **条件显示容器（Visible 切换）的布局时序**：Visible=false 期间不参与 Dock 布局，BuildLayout
   早期对其子控件的定位是错的且 Resize 补偿不保证触发——**每次 Visible=true 后必须显式重排一次**
   （见 ConfigEditor MainForm.UpdateBrandCombo 末尾的 LayoutPresetBar 调用）。
4. **脚本本身的坑**：
   - PowerShell 5.1 读无 BOM 的 UTF-8 脚本按 ANSI 解析（中文乱码语法错误）——**.ps1 必须 UTF-8 带 BOM**；
   - tasklist 会把长映像名截断到 25 字符（按名字精确匹配 PID 永远失败）——启动模式用 Popen 返回的
     PID，attach 模式用前缀匹配；
   - 主窗体句柄可能延迟就绪（MainWindowHandle=0）——必须轮询等待，不能取一次就用。

## 标准排查流程

1. 跑 `Get-ControlTree`（必要时 `--click` 选中目标节点让条件显示容器出现），拿全部矩形；
2. 对照判据找异常：Fill 底边==父底边？控件矩形超出父容器？与兄弟控件矩形重叠？
3. 定位到具体控件后，查它的**父容器 Controls 添加顺序**与 **Visible 切换后的重排调用**；
4. 改完用 `Get-WindowShot` 截图 + 再跑一次 `Get-ControlTree` 双重验证（Bounds 与渲染都要对）。
