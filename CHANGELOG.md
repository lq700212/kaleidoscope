# 版本改动记录

> **本文件自 V1.0.0（Kaleidoscope 库抽取之日）起记，只记录 Kaleidoscope 仓库自身的改动。**
> 更早的 V2.14.26 及以前版本均为 CommandCenter 原项目的历史记录（窗口徽标、点位配置、
> 产品型号弹窗等界面功能，与 Kaleidoscope 库代码无关），**不随本仓库迁移**；其中通讯相关的
> 血泪背景（PLC 从站释放 V2.14.23、相机判定即写 V2.13.7、PW 同程序号跳过 V2.14.19、
> 存图清理防误删 V2.14.12 等）已沉淀在 `AGENTS.md`「已知通讯关键点」（位于仓库根），需要
> 原始完整记录时查原 CommandCenter 项目的 CHANGELOG.md。

## V2.4.2（2026-11-24）标题条副题改走主题键 + 圆角自检不再误报胶囊（Sinan 宿主二次反哺）

> 宿主 Sinan 补齐详情页弹窗时暴露两个库内缺陷：一个是"硬编码白色"在浅色标题条上不可读，
> 一个是自检器把自己的正解当成了错。两个都属于"库替宿主做假设"的同类问题。

### Kaleidoscope.UI（V2.4.2）

- **`TitleBar` 副题颜色改走键 `Brush.Shell.TitleSubFg`**（`Views/TitleBar.cs`）：原先硬编码
  `0xB8FFFFFF`，只有"深色渐变标题条"（CureLine / HJVision 口径）看得见；Sinan 的标题条是
  浅米色渐变，白字几乎不可读。改为优先取宿主键，**取不到回落原白色**——老宿主零改动、
  零键板变更。宿主想调副题颜色只需在自己的 Light/Dark 键板各加一行。
- **`KxRadiusAudit` 排除 `KxCapsuleBorder`**（`Views/KxRadiusAudit.cs`）：胶囊控件构造即把
  半径绑到 `ActualHeight/2`，当元素"比高还窄"（单字状态徽标 18×20，半径 10）时必然满足
  "横向半径 > 宽/2"，但 WPF 把横向钳到 9 画出来**正是胶囊该有的样子**。不排除的话每块单字
  徽标都报一条 WARN——Sinan 实测一次扫描 6 条全属此类，真问题会被噪音淹没。

### 验证（宿主 Sinan）

- 库单独 `dotnet build` 0 警告 0 错误；Sinan 全解决方案 0 警告 0 错误。
- 修前日志：`[圆角自检·页面切换] 共 6 处会被画成椭圆`（全部是 `KxCapsuleBorder "Bd"`）；
  修后：`[圆角自检·页面切换] 通过：未发现会被画成椭圆的圆角。`
- 弹窗副题实机目检：浅色主题下 `P7391-027 · 接近开关动作节拍偏慢` 清晰可读。

## V2.4.1（2026-11-24）胶囊圆角从"记得写对"变成"默认正确"：KxCapsuleBorder + KxRadiusAudit（Sinan 宿主反哺）

> 宿主 Sinan 2026-11-24 再次复现"按钮圆角太尖锐"：设计稿 CSS `border-radius:99`，XAML 照抄
> `CornerRadius="99"`。这是 HJVision（2026-10-30）、AIris（V0.1.12）、Sinan 第三次同款返工；
> 库内早有 `CapsuleRadiusConverter` 和「禁手写 99」注释，但用法是一长串 Binding，新宿主查不到、
> 想不到——纪律只写在文档里等于没落地。本轮把"正确写法"做成一行控件，把"错误写法"做成可执行审计。

### Kaleidoscope.UI（V2.4.1）

- **KxCapsuleBorder**（`Views/KxCapsuleBorder.cs`）：Border 子类，构造即把 `CornerRadius` 绑定到自身
  `ActualHeight/2`（竖向窄条用 `Axis="Width"` 取宽度一半）；布局尺寸变化自动跟随，任意高度都是正圆端头。
  继承 Border，宿主模板只需替换标签，既有样式/触发器/`x:Name` 不受影响。
- **KxRadiusAudit**（`Views/KxRadiusAudit.cs`）：运行时圆角体检器，遍历可视树（含 ControlTemplate
  内部展开的元素），把 `CornerRadius > min(宽,高)/2` 的 Border 逐条报出（尺寸/四角值/模板父/Name），
  返回命中数；`failFast` 可抛异常接 CI，`Tag="KxRadiusAudit.Skip"` 白名单放行故意的大半径拱形。
- **UiConverters** 补 `CapsuleRadius` 静态单例（README 宣称五个单例，此前漏了这一个）。
- README「控件复用索引」新增胶囊圆角场景行 + 「圆角自检」接入说明。

### 验证（宿主 Sinan）

- 库单独 `dotnet build` 0 error；Sinan 全解决方案 0 warning / 0 error。
- 反向验证：临时放一个 124×30 / `CornerRadius="99"` 的 Border，Debug 自检在日志报出
  "124×30 的元素 CornerRadius=99，短边一半只有 15 → 会画成椭圆"；删除后复测 0 命中。

## V2.4.0（2026-11-22）新增 Infer 推理协议适配层：四视觉协议 + JEV Decisions 决策协议（AIris 宿主反哺）

> 宿主 AIris 引擎页调研定版：本地部署形态多元（vLLM / llama.cpp server / Ollama / 自研
> FastAPI / 云端），差异只在"信封"（端点/头/传图字段/回包结构）——信封收口成协议层，
> 上层门面统一，换部署形态只改下拉框。

### Kaleidoscope（主库 V2.4.0，新目录 Infer\，命名空间 Kaleidoscope.Infer）

- **零第三方依赖**：JSON 手拼/轻扫（Json.cs），HTTP 走 BCL System.Net.Http（TLS1.2 静态收口），
  图像以 JPEG byte[] 过接口（不引 System.Drawing，WinForms/WPF 宿主通用）。
- **协议传输器**（IInferTransport + InferTransportBase 共用基座，失败转 Ok=false 不抛异常）：
  OpenAiTransport（/v1/chat/completions，image_url=data URI，Bearer）、
  AnthropicTransport（/v1/messages，base64 source 块，x-api-key+anthropic-version，
  system 顶层字段，content[].text 拼接）、OllamaTransport（/api/chat stream:false，
  images=**裸 base64 数组**非 data URL）、CustomJsonTransport（POST /detect 自研契约框直出）、
  DecisionsTransport（JEV 决策协议：choice/score/noul 三题型，兼容 TypeSafe 原生
  /v1/systemone 与 OpenRouter /api/alpha/decisions 端点——云端文本决策模型，无图像输入，
  供宿主可选语义决策通道，不进生产主链路）。
- **BoxParser**：回包文本 → 0~1000 归一化框。策略①JSON bbox_2d 数组（Qwen2.5-VL grounding
  默认形态，容 ```json 围栏）；②\<box\> 标签（LocateAnything 文本形态，两种写法都收）。
  钳位/端点校正/零面积丢弃；**无置信度字段（宿主语义红线，全链路判定走 ROI-IoU）**。
- **InferClient 门面**：DetectAsync（组三句式 grounding prompt → transport → BoxParser）、
  ProbeAsync 探活（openai:/v1/models、ollama:/api/tags、custom:/health、anthropic:配置即就绪）。

### 验证（宿主 AIris V0.1.7）

- 编译 0 error；四协议组包格式对照官方文档逐字段核对；探活对无服务地址友好降级；
  JEV Decisions 组包/解包单测口径（choice/noul 回包扫描）通过。

## V2.3.7（2026-11-22）KxImageStage 叠加画布可交互修复（AIris 宿主 ROI 拖拽实踩）

> 宿主 AIris 配方编辑页要在取像舞台上放**可鼠标拖拽的 ROI 框**（移动/四角缩放/拖画新框），
> 发现内置 Overlay 画布构造为 `Collapsed + IsHitTestVisible=false` 且从未翻开——画上去的
> 元素既不显示也收不到鼠标，属 V2.3.6 引入的库侧遗留缺陷（监控台此前画检测框实际也没显示出来）。

### Kaleidoscope.UI（V2.3.7）

- **Overlay 恒可见 + 可命中**：构造改 `new Canvas()`（默认 Visible + IsHitTestVisible=true）。
  空画布无背景不拦截任何空白点击——监控台"纯画框"用法行为零变化；宿主可在画布上放
  可交互元素（拖拽框/角手柄），"空白处按下"由宿主在画布上自挂事件判命中元素为空实现。
- 类头注释补【V2.3.7 可交互口径】说明。

### 验证（宿主 AIris V0.1.7）

- 编译 0 error；监控台检测框叠加恢复显示；编辑页 ROI 拖拽三件（移动/缩放/画新框）实点验证通过。

## V2.3.6（2026-11-22）KxImageStage 内置帧显示管线：傻瓜式喂帧 + 扫描联动（AIris 宿主反哺）

> 宿主 AIris 第八~九轮反馈：①取像控件要对标 HJVision VisionPreviewHost 的"傻瓜式
> 复用"——黑盒逻辑全在控件内，宿主一行绑定即得完整取像显示（单图/实时视频通吃）；
> ②**显示图像/实时视频时自动关闭扫描动效，只有初始空态（无帧）才有扫描**——封装
> 进控件，外部调用自动保证；③全舞台"一层网格+扫描"（画面内容不带装饰网格）。

### Kaleidoscope.UI（V2.3.6）

- **KxImageStage 内置帧显示三件**：帧图 `Image`（ZIndex=0）/ 叠加画布
  `public Canvas Overlay`（ZIndex=5：画面上、扫描带下，宿主 code-behind 直接取用
  画检测框/标定框）/ 空态文本（弱化前景键 `Brush.Shell.FgDim`）。层序全 SetZIndex
  锁死不变；**纯装饰用法零破坏**——不喂帧=内置三件全 Collapsed，V2.3.5 用法原样可用。
- **双喂帧入口（二选一红线：同实例禁混用）**：
  ①绑定式 `FrameSource="{Binding FrameImage}"`（MVVM 口径，ImageSource DP）；
  ②命令式 `ShowFrame(ImageSource)` / `ClearFrame()`——**任意线程安全**（内部
  Dispatcher 封送，帧线程直调不崩，对标 HJVision"检测线程直调 VM"口径）。
- **新 DP**：`IdleText`（空态文本，null/空=不显示；有帧自动隐藏、无帧自动回来）、
  `FrameStretch`（默认 Uniform，letterbox 对齐网格舞台，监控画面口径）。
- **扫描联动（封装内建，宿主零接线）**：有效开关内含 `FrameSource == null`——
  有帧自动停扫描（带子收起 + 清动画时钟，防空转口径），清帧回空态自动恢复。
  扫描只属于"初始空态"：画面/视频不被光带干扰，帧流高频重绘期动画时钟不再叠加
  开销（分层窗软渲染性能口径的自然延伸）。

### 验证（宿主 AIris V0.1.5）

- 编译 0 error；浅色屏摄逐位采样：舞台底 #E9EEF8 命中、18px 网格透底、透明帧
  零件浮层清晰、无第二层台面网格；**有帧连拍 diff=0**（扫描停实锤——同参数
  V0.1.4 纯装饰版连拍 diff 显著）；Mock"空场透明帧"按契约即"有帧"，故宿主演示
  链路空态扫描只在"未启动检测"前出现——联动契约正确执行；存档链路 alpha 保留
  实测（零件半透明像素 A38 / 底 A0）。
- 宿主侧配套（AIris 主仓 V0.1.5）：Mock 帧透明化 + `Imaging.ToBitmapSource`
  保 alpha 改造（**alpha 语义红线：GDI 直通 alpha 必须配 `PixelFormats.Bgra32`，
  喂 Pbgra32 预乘会把半透明像素当预乘值解读、显示叠底溢出爆白**——实测教训）。

## V2.3.5（2026-11-22）取像舞台控件首件：KxImageStage（舞台底/网格/扫描光带动效，AIris 宿主反哺）

> 宿主 AIris（视觉检测防错平台）诉求：①取像区做出 UI 设计稿的扫描动效（深色舞台底
> + 网格 + 主色扫描光带 4.2s 循环——"工业视觉的经典视觉语言"）；②深色模式下取像区
> 不再透出面板色；③做在库基类控件上，其他项目引用直接复用。用户追加：**控件主体
> 颜色与扫描颜色做成可配置 DP**，复用性更高。

### Kaleidoscope.UI（V2.3.5）

- **新增圆角矩形档状态胶囊 `KxChip` + `KxChipText`**（ShellStyles.xaml，AIris 宿主
  反哺）：与既有 `StatusChip`（纯药丸，CapsuleRadiusConverter 半圆端）同 Tag 语义档
  （Ok/Warn/Ng/Err/Pri）/同默认底色，唯一差异=形状 `CornerRadius=7`（KxLangButton
  同口径圆角矩形）。**两档并存缘由**：纯药丸在矮胶囊上两端尖弧"太尖锐"（AIris 用户
  V0.1.1 否过顶栏、V0.1.4 又抓到页面 7 处手写 `CornerRadius=99` 残留漏改）——圆角
  矩形档成为多数宿主首选后沉库，宿主查复用索引直接拿正确形状，禁再手写；
  HJVision 等药丸口径宿主不受影响（StatusChip 原样保留）。

- **新增 `Views/KxImageStage.cs`**：取像"舞台"容器控件，继承 `Grid`——宿主 XAML
  子元素直接落 Children，宿主 code-behind 对 ActualWidth/Height、SizeChanged 的既有
  用法零改动。层序用 SetZIndex 锁死（不依赖 Children 顺序）：[ZIndex=-2] 舞台底
  Border → [ZIndex=-1] 网格 Rectangle（DrawingBrush Tile 平铺、1px 双向线、
  Viewport/Viewbox Absolute 按 GridStep 步距）→ [默认 0] 宿主子内容 → [ZIndex=10]
  扫描光带 Rectangle（TranslateTransform.Y 从 −h 扫到 +h，linear Forever）。
  WPF 负 ZIndex 子元素画在 Panel Background 之上、普通子元素之下，正好承载装饰层。
- **三色可配置 DP（复用核心）**：`StageBackground` / `GridLineBrush` / `ScanBrush`
  三个 Brush DP 默认 null → 构造期 SetResourceReference 主题键（`Brush.Shell.StageBg`
  / `StageGridLine` / `StageScan`，DynamicResource 跟随宿主深浅换肤）；宿主显式设
  本地值即覆盖（本地值优先级高于资源引用）——不配=主题自动配色，配了=项目自定义
  原色。内部元素接线用代码 Binding（Pen.Brush 也是 DP，未冻结 Freezable 可绑定）。
- **行为 DP**：`ScanEnabled`（默认 true）/ `GridStep`（默认 18=设计稿口径）/ 
  `ScanDuration`（默认 4.2s）；**库级总闸** `KxShellDefaults.EnableStageScanMotion`
  （缺省 true，宿主一键关全局动效）——有效开关 = 总闸 && ScanEnabled && IsVisible。
- **生命周期防空转**（分层窗软件渲染红线口径）：IsVisible=false / Unloaded 双停
  （BeginAnimation(null)），恢复可见/Loaded 且有效开关为真再 Begin；未布局
  （ActualHeight<1）跳过等 SizeChanged。
- **动画帧率降档（实测口径，性能关键）**：`Timeline.SetDesiredFrameRate(anim, 10)`
  ——WPF 动画时钟缺省 60fps，每 tick 触发分层窗整窗重绘，宿主 AIris 屏摄验证期间
  CPU 增量实测：60fps 动效稳态 ~85-90% 单核，最小化（IsVisible=false 双停）骤降
  ~15% 实锤主因；降 10fps 后稳态 ~50%（动画纯增量 ~10%）。扫描带慢位移
  （4.2s 一轮，10fps 每帧 ~38px）肉眼平滑无跳感。**通用红线：分层窗软渲染宿主的
  装饰性动画必须设 DesiredFrameRate（≤10fps 量级），60fps 时钟不可接受**。
- **缺省键板两板各补 3 键**（`DefaultLight|Dark.xaml`，两板键集一致）：`Brush.Shell.StageBg`
  （浅 #E9EEF8 / 深 #060B16，设计稿相机舞台口径）+ `StageGridLine`（浅 #1A5064A0 /
  深 #1A7E96BE，主蓝灰 10%）+ `StageScan`（各板主色 10% 透明度整面渐变，0.46 停靠
  最亮、92% 渐隐——浅板主色 #1E90FF、深板 #2E8FE0）；DefaultLight 头注释键名清单
  同步登记。
- **适用边界（登记进 README）**：纯 WPF 项目适用（半透明画刷直写，无 HWND 合成丢
  α 问题）；WinForms/HWND airspace 场景（Cognex ActiveX 等 HWND 盖住 WPF 元素）
  不适用——网格须 WinForms 侧自绘、半透明须预混实色（HJVision 口径）。

### 验证（宿主 AIris V0.1.4）

- AIris 主仓 MonitorPage 取像区 StageGrid（Grid）替换为 `ui:KxImageStage`，三子元素
  原样保留、code-behind 零改动；编译 0 error；深浅两模式屏摄目检舞台底+网格+扫描
  光带可见（深色 letterbox 像素采样 #060B16 逐位命中），间隔半周期连拍确认光带位移
  （动效在跑）；检测框叠加（Mock 放件贴合）/空态文本/页面切换（切走卸载→切回
  重启动画）/最小化-还原全回归无恙；
- **性能实测（CPU 增量采样）**：60fps 动效稳态 ~85-90% 单核 → DesiredFrameRate=10
  修复后 ~50%（动画纯增量 ~10%；最小化护栏实测 86%→15%）；

## V2.3.4（2026-11-22）UI 复用纪律多锚点沉淀：README 接入必读节 + 五字典/三基类类头路标（AIris 宿主实训反哺）

> 宿主 AIris 主窗导航胶囊漏查库内 `KxLangButton`（自绘 ControlTemplate 还漏清 WPF
> 默认焦点虚线框，"胶囊效果很奇怪"）。根因不是库缺控件，而是"开工先查索引"这个动作
> 没有可靠触发点——宿主 AGENTS.md 绑定单一仓库、AI 工具记忆绑定单一工具，换工具或
> 换宿主开发即失效。本版把路标埋进宿主接入库的**每一条必经之路**，任何 AI Agent
> 工具、任何宿主项目都绕不开：clone 库先读 README → App.xaml 合并字典开五文件 →
> 启动链设 KxShellDefaults → 建弹窗/主窗继承两个窗基类 → 查样式键 grep 五字典。

### Kaleidoscope（V2.3.4，纯文档无代码改动）

- **README.md** 新增「宿主项目 UI 接入必读（人 + AI Agent 通用）」节（快速开始之前，
  仓库入口第一屏）；
- **Themes/ 五字典**（DefaultLight / DefaultDark / ChromeStyles / FreshStyles /
  ShellStyles）文件头注释加「宿主 UI 开工必读」路标段——宿主合并字典与查键 grep
  的必经文件；
- **Views/ 三类头**（KxShellDefaults / ChromeDialogWindow / ChromeBorderlessWindow）
  注释加同款路标——宿主启动链与建窗的必经接触点；
- 路标只负责把人和 Agent 引过去，细则本体仍是 `Kaleidoscope.UI/README.md`
  「控件复用索引（按场景查）」节 + 仓库根 AGENTS.md 铁律 7。

## V2.3.3（2026-11-22）TitleBar 标题条渐变底被 Loaded 兜底 ClearValue 误杀——白标题栏根治（HJVision 宿主 51 窗目检抓获）

> 宿主张 HJVision 用户目检：弹窗标题栏渐变蓝（Brush.HeaderGrad）变白、白字白底几乎不可读。
> 对照实验（git bisect 式两轮构建）锁定：V2.1.0 迁库当轮即已白（非 V2.2.x~V2.3.x 引入），
> 沉库轮目检漏看了标题条颜色，潜伏三天。

### Kaleidoscope.UI（V2.3.3）

- **TitleBar.ApplyTitleBackground 的 null 分支由 ClearValue 改 SetResourceReference**：
  ClearValue 清的是本地值层——而 BuildSkeleton 里 `SetResourceReference` 放置的资源引用
  表达式**就在本地值层**，ClearValue 把 Brush.HeaderGrad 键引用一并清掉，Background 回落
  null（透明）→ 标题条露出白卡片底。构造函数的 Loaded 兜底对 null（绝大多数窗 XAML 未设
  TitleBackground）无条件触发，等于每窗必中。修复后 null=显式重装键引用（幂等，兜底反复
  触发无害）；显式 Brush 照旧打本地值压过键引用（MBox 四态语义渐变场景不受影响）。
- **血泪归档（TitleBar 类头【血泪随迁】）**：同一行代码跨架构换代要重审语义——宿主 XAML
  Setter 时代"null=ClearValue"正确（键引用在样式层，ClearValue 只清本地值层、值回落样式
  层键引用仍在）；2026-11-19 迁库去 XAML 化改代码 SetResourceReference 后语义悄然变化。
  ClearValue 语义=清本地值层**一切条目**，资源引用表达式也在其中。

## V2.3.2（2026-11-21）分层窗决策改虚属性钩子：`protected virtual bool UseLayeredWindow` 让派生类 override 真正关掉分层（HJVision 宿主反哺）


> 宿主张 HJVision（WPF 视觉检测）实测血泪：含 WindowsFormsHost/HwndHost 的弹窗内嵌
> 显示区**整块不渲染**——WinForms 侧连 WM_PAINT 都收不到、`GetWindowRgn` 报错、
> EXSTYLE=0x00080000 实锤带 WS_EX_LAYERED，预览区只剩纯底纹。根因是本基类构造函数里
> `_layered = UseLayeredTransparency` 读到的永远是 DP 缺省值 true：XAML 的
> `UseLayeredTransparency="False"` 要到 `InitializeComponent` 才赋值，晚于基类构造。

### Kaleidoscope.UI（V2.3.2）

- **新增派生类钩子 `protected virtual bool UseLayeredWindow`**（缺省 `=> UseLayeredTransparency`）：
  构造函数改 `_layered = UseLayeredWindow`。虚属性在基类构造期求值会派发到派生类 override，
  因此派生类写 `protected override bool UseLayeredWindow => false` 才真正关掉分层窗
  （子 HWND 恢复渲染）；非分层分支的 SetWindowRgn 物理圆角/投影留白归零逻辑不变。
  **红线**：窗内嵌 `WindowsFormsHost` / `HwndHost`（VisionPro 显示区、Cog 工具块编辑器等）
  必须 override 返回 false，否则子 HWND 整块不渲染。
- **`UseLayeredTransparency` DP 注释改口径**：该 DP 现在**只影响自身读取方，不决定窗模式**；
  要关分层请 override 钩子，XAML 里写 `UseLayeredTransparency="False"` 无效（构造期已定格，
  事后赋值只是改一个没人再读的 DP）。
- 宿主接线实况：HJVision 侧 DisplayMark / SearchRegion / ZeroCalib / JobEdit 四窗 override=false，
  另有六窗（无 HwndHost）删除 XAML 里那行无效属性；五处取像/预览显示区迁移到统一底座后，
  浅色 + 深色两轮真机目检全部正常，24 批回归 ALL-PASS。

## V2.3.1（2026-11-20）主窗内容块圆角化：`Fn.ShellRadiusBottom` 令牌 + `KxRoundedClip` 圆角裁剪附加属性（ProductionStatistics 宿主反哺）

> 宿主 ProductionStatistics 用户反馈"外壳圆角了，但内容块四角还是直角"：顶栏白块上缘、
> 页签带下缘、状态条四角、卡片内 DataGrid 表头/行底四角都是直角，与外壳圆弧错位。
> 本版把宿主需要的两件通用能力沉淀进库，宿主自身只写一行 XAML 挂接。

### Kaleidoscope.UI（V2.3.1）

- **新增语义键 `Fn.ShellRadiusBottom`**（`Themes/DefaultLight|Dark.xaml`）：底缘圆角
  `0,0,10,10`（深色板同口径取 `0,0,9,9`），与 `Fn.ShellRadiusTop` 成对补全"上缘/下缘"
  令牌组——页签带/底栏等内容块贴容器下缘时用，宿主挂 `CornerRadius="{DynamicResource Fn.ShellRadiusBottom}"`。
- **新增圆角裁剪附加属性 `Views/KxRoundedClip.cs`**：`Border.CornerRadius` 只裁自身背景
  **不裁子元素**（库内 `ChromeDialogWindow.cs` 类头注释早已记录此坑），卡片内放 DataGrid/
  图片等自带方角背景的子元素时四角盖过圆弧。本属性一行 XAML 收拢标准解法：
  `ui:KxRoundedClip.Radius="12"`——Loaded/SizeChanged 按元素实际尺寸同步圆角矩形 Clip
  （几何实例按元素复用，拖拽缩放零分配），Radius=0 摘除恢复方角；语义同
  `ChromeBorderlessWindow.UpdateRootClip`（元素自身坐标整体裁剪），与弹窗基类的
  "卡片内容内缘裁剪"是两种口径，类头已注明勿混用。宿主主表格/查询表格挂 12
  （=卡片 18 − 网格内边距 6，同心圆弧）。
- 宿主接线实况：顶栏 `CornerRadius={Fn.ShellRadiusTop}`、页签带 `{Fn.ShellRadiusBottom}`、
  状态条四角 `{Fn.ShellRadius}`（最大化归零/还原挂回由宿主 窗口_StateChanged 与根 Border
  同步处理）、主表格/查询表格 `ui:KxRoundedClip.Radius="12"`；还原态/最大化态截图目检
  四角全部跟随圆弧。

## V2.3.0（2026-11-19）业务壳族十一件：卡片式看板主窗全切反哺（ProductionStatistics 宿主）

> 宿主张 ProductionStatistics（WPF 生产统计看板）按"可迁的直接用库键、库没有的沉淀进库"
> 完成主窗 UI 真接入（上一轮只是引用程序集）。本版收下宿主验证过的**业务壳族**：
> 全部 keyed 样式、零硬编码色（走 `Brush.Shell.*` / `Fn.*` / `Fx.*` 语义键）、
> 视觉口径逐像素取自宿主现状（卡片圆角 18 / 主题开关 64×32 / 双行页签圆角 9 /
> 语言胶囊圆角 7 / 稼动率圆环 46px 描边 5）。

### Kaleidoscope.UI（V2.3.0）

- **新增业务壳族样式（`Themes/ShellStyles.xaml`）**：
  - `KxThemeSwitch`：主题开关滑动轨道（3D 轨道 + 月/日图标，两板同值刻意不随主题变，
    见 DefaultLight 注释）；`KxShiftTab`：双行班次页签（ToggleButton，选中主色弱底 +
    主色描边 + 加粗）；`KxLangButton`：语言单选胶囊（RadioButton，选中实主色）；
  - `KxCard`：投影卡片（`Fn.ShellCardRadius`=18 + `Fx.ShellCardShadow`）；
    `KxIconBox`：26×26 主色弱底图标方块；`KxTopBar`：顶栏容器（面板底 + 下边线 + 20,14）；
  - `KxInfoBox`：提示框（`Tag="Ok/Warn/Ng/Pri"` 语义弱底 + 同色描边）；
    `KxStatusDot`：状态灯圆点（Tag 语义换 Fill，宽高可覆盖）；
  - `KxRingBg` / `KxRingValue`：稼动率圆环底环/值环（描边 5，弧长由宿主代码设
    `StrokeDashArray`）；`KxSlimScrollBar`：10px 细滚动条（垂直/水平双模板，
    宿主隐式化用 `BasedOn="{StaticResource KxSlimScrollBar}"` 再造，不双份并存）。
- **`StatusChipText` 补 `Tag="Pri"` 触发**（原 Ok/Warn/Ng/Err 四态，主色提示文字复用同一件）。
- **缺省键板补 9 键**（`DefaultLight.xaml` / `DefaultDark.xaml` 各 90 键、两板键集一致）：
  `Brush.Shell.ThemeTrack/ThemeTrackOn/ThemeTrackBorder/ThemeKnob/ThemeKnobOn/ThemeMoonFg/ThemeSunFg`
  （主题开关七键，两板同值）+ `Fn.ShellCardRadius` + `Fx.ShellCardShadow`/`Fx.ShellTheme*Shadow`。
- **库内 `{StaticResource Fn.*}` 统一改 `{DynamicResource Fn.*}`（8 处）**：
  宿主题切换=替换键板字典，`StaticResource` 会把首次解析值固化在元素上，
  换主题后圆角/字体等不变——本版起库内样式对键一律 DynamicResource
  （**库新增样式红线：禁止 StaticResource 引用键板**）。
- **宿主反馈的键契约红线（写进 README）**：宿主两本主题字典的键集必须完全一致
  （只补一本 → 切主题时该处颜色不跟着变，编译期静默）；宿主补段必须写字面值、
  禁止 `<StaticResource ResourceKey=.../>` 元素转发（net472 BAML 键值错位）。

### 验证

- ProductionStatistics 主窗全切后：深浅双主题运行时切换、1280×900 / 1500×920 /
  1920×1080（真最大化）三档目检通过；宿主侧 `Run-ThemeContractTests.ps1`
  静态契约 `PASS=9 FAIL=0`（键集 133/133、库键 ⊆ 宿主键板、零元素转发）。
- 库工程 `dotnet build Kaleidoscope.UI/Kaleidoscope.UI.csproj` 0 error 0 warning（net472 + net8.0-windows 双目标）。

## V2.2.0（2026-11-19）主窗形态骨架基类首件：ChromeBorderlessWindow（宿主 YoloDetector 反哺）

> 宿主 YoloDetector（net472，v2.11.2）把 v2.10.2/v2.10.3 两轮实测沉淀的"真圆角
> 无边框主窗骨架"（自绘顶栏 + WindowChrome + 分层透明 + 最大化工作区钩子）整体
> 改继承库新基类，宿主 code-behind 删手抄骨架 ~130 行；全量回归 182 用例全绿 +
> GUI 冒烟通过 + desktop 三态像素级目检（正常/最大化/还原）零视觉变化。

### Kaleidoscope.UI（V2.2.0）

- **新基类 `Views/ChromeBorderlessWindow`**：无边框圆角窗**纯行为骨架**，与
  ChromeDialogWindow（弹窗形态，键板视觉一体）并列成对，补齐"主窗/自绘标题栏窗"
  形态（原第二波规划"主窗基类"的行为地基）：
  - **零键板依赖**：不引用任何 Brush.*/Fn.* 语义键、不插任何视觉元素——底色/圆角/
    顶栏全部由子类 XAML 根 Border 自绘，宿主不合并库字典即可使用（与弹窗基类的
    本质差异：弹窗吃库视觉体系，主窗吃宿主视觉体系）；
  - 行为集（全部 YoloDetector 生产验证、值一字未动）：WindowStyle=None +
    AllowsTransparency + Background=Transparent（分层真圆角三件套）；WindowChrome
    外壳（CaptionHeight=DP 缺省 34 / ResizeBorderThickness=DP 缺省 6 /
    GlassFrameThickness=0 / UseAeroCaptionButtons=false）；根 Border 圆角 Clip 同步
    （约定 Window.Content as Border；Loaded/SizeChanged/StateChanged 三入口，几何
    实例复用零分配）；最大化圆角归零/还原恢复（CornerRadius=DP 缺省 10，YoloDetector
    传 14）；
  - **WM_GETMINMAXINFO 最大化工作区修正**（`FixMaximizedToWorkArea` DP 缺省 true）：
    库内首件——WindowChrome 默认最大化盖任务栏（bounds 探出 7px/边，v2.10.2 实测），
    窗口过程钩子把最大化目标改写为最近显示器工作区（handled=true 阻止 DefWindowProc
    覆盖）。弹窗基类的可最大化窗（Resizeable=true 的日志/报警窗）暂未带此修正，
    等 HJVision 诉求再融（不主动改已有宿主行为）；
  - 刻意不做：标题条/系统三钮（各宿主样式差异大，留宿主自绘）、非分层
    SetWindowRgn 分支（无验证过的口径不收，含 HWND 的窗勿用本类——分层窗 HWND
    不渲染红线写进类头，走 ChromeDialogWindow.UseLayeredTransparency=false 形态）。
  - 接法：子类 XAML 根元素类名改为本类 + 根内容包圆角 Border + 根元素传
    `CaptionHeight`/`CornerRadius` DP；WindowStyle/AllowsTransparency/Background/
    WindowChrome 由基类构造设置，子类 XAML 不重复声明。

### 宿主接入实录（YoloDetector v2.11.2）

- 主窗 MainWindow 改继承基类：XAML 删 WindowStyle/AllowsTransparency/Background
  三属性 + WindowChrome 节点（根元素传 CaptionHeight=62/CornerRadius=14 DP）；
  code-behind 删手抄骨架 ~130 行（圆角常量/裁剪几何/UpdateFrameClip/
  WM_GETMINMAXINFO 整段含 Win32 结构体与 P/Invoke）；StateChanged 只剩宿主图标
  切换行为。
- 【坑】宿主主 csproj 的 submodule 排除规则**必须补 `<Page Remove="submodules\**" />`**：
  UI 库新增 .xaml 样式字典后，UseWPF 的 SDK 默认通配会把它们当 Page 扫进主项目
  XAML 编译（实测 MC3074 主程序集解析不到库内 clr-namespace 类型；此前 submodule
  无 .xaml 所以三件排除够用，四件缺一不可）。
- 验证：YoloDetector 全量回归 182/182 全绿（含"UI-无边框窗口WindowChrome配置"
  运行时断言用例——WindowChrome 参数/三件套/rootFrame Clip 全部由基类承担后
  断言原样通过）+ GUI 冒烟 + desktop 三态目检（最大化贴合工作区任务栏可见/
  圆角归零，还原 14px 圆角恢复，像素采样证实）。

### Kaleidoscope.UI（V2.2.1）同日去重优化：外壳共享互操作收拢

- **新静态类 `Views/KxShellInterop`**：两窗基类重复面去重收口——① `BuildChrome`
  （WindowChrome 库口径构造：GlassFrameThickness=0/CornerRadius=0/
  UseAeroCaptionButtons=false 固定，CaptionHeight/缩放边参数化），ChromeDialogWindow
  与 ChromeBorderlessWindow 的 ApplyChrome 各自手抄的同一段构造改为共用一份；
  ② `EnableMaximizedWorkAreaFix`（WM_GETMINMAXINFO 最大化工作区修正的 Win32
  互操作全套：钩子/WndProc/结构体/P-Invoke 自无边框窗基类收拢，带幂等闸）。
- **ChromeDialogWindow 补 `FixMaximizedToWorkArea` DP（缺省 false=两宿主现状
  一字不变，additive 向后兼容）**：可缩放弹窗（Resizeable=true 的日志/报警窗）
  最大化同样盖任务栏，修正能力经共享实现备好，HJVision 诉求到了逐窗设 true 即开。
- **语义差异注释互指（防误合并）**：两基类的 Clip（弹窗=卡片内缘裁剪/主窗=根
  框架整体裁剪）与最大化圆角归零（弹窗=骨架 card/主窗=子类根 Border）看似同构、
  实则挂载对象与半径口径不同，双方注释明确"勿合并"。
- 验证：UI 库双目标 0 错误零新增警告；宿主 YoloDetector 全量回归 182/182 全绿
  + GUI 冒烟 + desktop 像素复核（最大化贴合工作区/圆角归零与改造前一致）。

## V2.1.1（2026-11-20）宿主可裁剪目标框架（离线构建根治）

> 宿主 HJVision-WPF（net472）反馈：`git pull` 后 VS 编译报 NU1105 / NETSDK1004，根因是该
> 工程不在宿主 sln 内、`obj/project.assets.json` 跟机器走；且双目标 restore 会连带拉 net8
> targeting pack（3 个 ref 包），工厂离线机无法还原。需求：宿主侧只构建 net472、彻底不碰
> net8，同时 CureLine（net8）默认行为一字不变。

### 变更

- **`Kaleidoscope.UI.csproj`**：`TargetFrameworks` 改为宿主可配置——`KaleidoscopeUITargetFrameworks`
  为空时仍是 `net472;net8.0-windows`（CureLine 等 net8 宿主零改动）；宿主设 `net472` 时只还原/
  构建该目标。外部 `ProjectReference` 写法不变（MSBuild NearestTargetFramework 自动选目标）。
- **实测（2026-11-20）**：
  - HJVision 侧（设 `net472`）：`project.assets.json` 仅 `frameworks: net472`、`libraries` 为空
    （零 NuGet 包）；`-Clean -Offline` 全量清空重建 10s 通过。
  - 不设属性（CureLine 口径）：`TargetFrameworks` 仍为 `net472;net8.0-windows`，且双目标真实
    构建成功——net472 与 net8.0-windows 两个 `Kaleidoscope.UI.dll` 均生成，exit 0。
- **宿主落地**：HJVision 在**主仓根** `Directory.Build.props` 设
  `<KaleidoscopeUITargetFrameworks>net472</KaleidoscopeUITargetFrameworks>`（该文件属主仓，不入本库）。
- **注意**：在设置了该属性的宿主仓库内单独构建本工程也会被裁到 net472；需要双目标时显式
  `-p:KaleidoscopeUITargetFrameworks="net472;net8.0-windows"`。

## V2.1.0（2026-11-19）弹窗基类融合 + net472 多目标 + WinForms 骨架（宿主 HJVision-WPF 一条龙接入）

> 宿主 HJVision-WPF（net472，51 窗）提出"基类沉库+两宿主类似基类只留一份"诉求：
> ①本地 SunnyDialog（36 窗）/ChromeDialog（9 窗）两基类与库 ChromeDialogWindow 融合；
> ②UI 库加 net472 多目标打通 net472 宿主接入；③开 WinForms 骨架工程（双栈 UI 基类
> 储备）。宿主侧 51 窗全切库版+全量回归 24 批 PASS+冒烟+真机目检（主窗/登录窗/
> 真实登录交互）；CureLine 侧 652 测试全绿+DocShot 全窗 PASS+灰度 3 窗目检零变化
> （缺省值=CureLine 现状原则）。E:\Project\kaleidoscope 独立副本请 `git pull` 同步。

### Kaleidoscope.UI（V2.1.0）

- **多目标** `net472;net8.0-windows`：宿主 net472 用 ProjectReference 自动选 net472
  产物（MSBuild NearestTargetFramework）；CureLine HintPath net8 产物路径不变。
  【坑】LangVersion 必须显式 12（net472 TFM 缺省 7.3 会炸文件范围命名空间）。
- **弹窗基类融合** `ChromeDialogWindow` 参数化吸收 HJVision 两基类全部能力，缺省值
  =CureLine 现状：`ShowMinButton/ShowMaxButton/IsMaxButtonEnabled/CloseCommand/Subtitle/
  TitleIconChar/TitleIcon/TitleBackground/CancelCommand(ESC→命令，缺省无行为)/
  UseLayeredTransparency(缺省 true，false=HWND 窗 SetWindowRgn 物理圆角)/
  ResizeBorderThickness(缺省 6)/ShowTitleBar(缺省 true，false=自绘标题栏窗不装内建条)。
  宿主级常量走新 `KxShellDefaults` 静态配置类（ShadowMargin/UseContentClip/
  ImplicitStyleKeys/Font/FontSize/ShowInTaskbar/StartupLocation/SysButtonWidth/
  CloseFontWeight——HJVision 启动一行配置，CureLine 零改动）。
- **标题条控件** `TitleBar`（自 HJVision SunnyTitleBar 迁入去 Sunny 化）：三钮/副题/
  图标胶囊 DP 面齐备；CureLine 口径=ShowMin/Max 缺省 false 复现 ✕ 单钮；对位修补三处
  （✕ 字重 CloseFontWeight 参数化、TopbarBorder 底边线回归、标题左距根 Padding 12）。
- **ShellStyles.xaml**（新）：Kx 族控件样式 ~50 键（按钮六档 KxPrimaryButton 等/
  StatusChip 对/输入四件 KxTextBox·KxPasswordBox·KxDatePicker·KxComboBox/KxSwitch/
  标题钮三件/BrandTab/BrandGrid/WinMenu 族/勾选三件/KxGroupBoxImplicit/FormLabel 族/
  Kpi 四件/ChromeDialogRootBorder 对/KxDialog*Implicit 七类兜底/FormField 隐式模板），
  自 HJVision SunnyTheme 通用段迁入**去 Sunny 化**（"Sunny"是原版第三方库 SunnyUI
  品牌致敬，公司级控件库不带第三方前缀，样式键统一 Kx 前缀）。键名契约=复用键
  （Brush.Fresh.*等）+新 Shell 族键（Brush.Shell.*38 键+Fn.Shell*4+Fx.Shell*3），
  键板三方对齐（库 Default 板扩到全键集 / CureStation·CimGateway 键板补 Shell 段 /
  HJVision ThemeLight|Dark 追加段）。
- **控件沉库**：FormField/NumericUpDownLite/PasswordInput/EyeIconView（自 HJVision
  迁入，血泪注释随迁）+ CapsuleRadiusConverter 并入 UiConverters。
- **顺手修复**：ThemePanelBgBrush 悬空键（原 SunnyTheme Calendar 隐式引用——迁宿主
  AppStyles 时改正 ThemePanelBg）。
- **宿主切换记录**（HJVision-WPF）：51 窗根元素改 ui:ChromeDialogWindow+删手写骨架
  （根 Border/TitleBar/底栏）；6 直连窗静态方法借用点改库版；SunnyTheme.xaml 拆壳
  （通用段沉库、Script* 三段+App 级隐式四段留宿主 AppStyles.xaml）；本地基类/控件/
  CapsuleRadiusConverter 九件删除（不双轨）；键名全局替换 Sunny*→Kx*。
- 【net472 BAML 血泪】宿主主题字典追加段**禁止 StaticResource 元素转发**（`<StaticResource
  x:Key="A" ResourceKey="B"/>` 在 net472 BAML 实测键值错位——Effect 拿到相邻键值，
  主窗首帧崩，冒烟抓包）；改写字面值后正常。CureLine 键板补段同此口径。

### Kaleidoscope.UI.WinForms（V1.0.0 新工程骨架）

- 双栈 UI 基类储备（宿主 HJVision 提"同时支持 WinForms 和 WPF 基类控件"，原版短期
  挂 SunnyUI 不切库——按下沉纪律只放骨架不扩面，等真实宿主）：`KxThemeTokens`
  双栈语义色板 Token 层（IsDark+语义键→Color+Changed 事件，原版 AppTheme 色板
  同源）+ `ShellDialogForm` 圆角自绘弹窗基类（Region 圆角深 10/浅 14 对位原版
  TryRoundWindow + GDI 渐变标题栏 + OnShown→OnFirstShow→ApplyTo→OnAfterThemeApply
  模板时序一比一对位原版 ThemeDialogForm，**零 SunnyUI 依赖**；静态事件双出口退订
  模板内置）。多目标 net472;net8.0-windows + UseWindowsForms，独立版本线 V1.0.0。

## V2.0.0（2026-11-18）Kaleidoscope.UI 首建：WPF 复用控件库（宿主 CureLine V0.0.109 反哺）

> 对应宿主 CureLine V0.0.109：UI 资产下沉调研（两程序 26 个弹窗手抄骨架 /
> 按钮/输入族 / 主题键板三处重复盘点）后首建 WPF 控件库。SDK 风格
> net8.0-windows + UseWPF 单目标（与主库 Kaleidoscope net472 并行的独立工程，
> 各自版本线；HJVision-WPF 等 net472 宿主接入需后续加多目标，见 UI README 路线图）。

### 新增

- `Kaleidoscope.UI/Views/ChromeDialogWindow.cs`：**圆角自绘弹窗基类**——WindowStyle=None
  + WindowChrome（CaptionHeight/Resizeable DP，缩放边 0/6）+ 透明分层 + 根 Grid Margin=14
  （投影留白）+ 圆角 10 卡片 Border（WinBg/Border/Fx.WinShadow）+ 品牌标题条（HeaderGrad
  + TopbarBorder 底边 + 顶角圆 10）+ 标题绑 Window.Title + ✕ 关闭钮（悬停红换白字，
  chrome 命中豁免）。子类 XAML 根内容自动进骨架内容槽（OnContentChanged 转移，重入闸防递归）。
  零 StaticResource 依赖（全 SetResourceReference 语义键）；按宿主 CureLine 现行 26 窗口径
  逐像素一致（无 ContentClip、无隐式样式注入——HJVision SunnyDialog 两坑绕行）。
- `Kaleidoscope.UI/Themes/ChromeStyles.xaml`：圆角窗骨架 keyed 样式 6 键（Fx.WinShadow +
  WinTitleBar/WinTitleText/WinTitleClose/WinSysBtn/WinSysClose）——与基类同口径，供不继承
  基类的窗（主窗/定制标题条窗）手抄用；像素口径来源=CureLine 两程序逐字相同值。
- `Kaleidoscope.UI/Themes/FreshStyles.xaml`：按钮/输入族 keyed 10 键（GhostButton/
  PrimaryGhostButton/FreshButton/FreshPrimaryButton/FreshDangerButton/FreshSuccessButton/
  FreshInput/FreshPwdInput/FreshCombo/FreshComboItem）——零硬编码色全 DynamicResource；
  【隐式样式绝不下沉】纪律入注释；FreshPwdInput Padding 库缺省取通用值 10,0,8,0，
  宿主像素契约走本地覆盖通道。
- `Kaleidoscope.UI/Themes/DefaultLight|Dark.xaml`：缺省键板 36 语义键（值取宿主
  CureStation Light/Dark 一字未动）——新宿主开箱即用；键名=契约（缺键=DynamicResource
  静默空白），键值可换皮。
- `Kaleidoscope.UI/Converters/UiConverters.cs`：InverseBoolConverter（互斥 Radio 单向显示）
  + StringEqualsConverter（MultiBinding 徽标比对）——语义零改动提炼。
- `Kaleidoscope.UI/README.md`：调研结论沉淀（候选清单/技术障碍/三波路线图/net472 多目标
  评估）+ 两步接入指南 + 键名契约清单 + 下沉纪律（口径来源明确的才收、隐式不做、
  零硬编码色）。

### 接入（宿主侧事实）

- 宿主 CureLine：两程序 App.xaml 合并库字典（宿主键板供语义键值，缺省键板不并入），
  本地已下沉键删除（CureStation 866→462 行 / CimGateway 766→344 行；FreshPwdInput 与
  隐式族按契约保留）；灰度迁移 3 窗（MesPostConfigWindow / ScannerConfigWindow /
  MesDebugWindow 可缩放变体）——其余 23 窗第二波。键契约测试在宿主
  （KaleidoscopeUiContractTests：库引用语义键 ⊆ 两程序键板、缺省键板 Light/Dark 全等、
  库样式键清单锁）。
## V1.12.0（2026-11-17）双域扩容：Stores 落库引擎 + Cameras 相机层（HJVision V0.9.68 反哺）

- **新增 `Stores` 命名空间（SQLite 单写线程落库引擎）**：`KxSqliteStore`（Register/Close
  幂等状态机 + 单写线程批量事务 + WAL + 坏库自愈 `.bad_时间戳` 隔离 + 积压保险丝 +
  独立短连接读 + DbMeta 固有 kv + EnsureColumn 旧库补列迁移 + 增量空间回收）+
  `KxDbAction`（委托型写动作：引擎对业务表零知识，宿主闭包捕获自己的行 DTO）。
  逻辑一比一对齐 HJVision `SqliteRecordQue` 骨架与全部血泪注释（PRAGMA 顺序铁律
  auto_vacuum 先于 WAL/提交防线/自愈只隔离真损坏等）；**Solo 独占通道的完成回执
  收敛为确定性语义**——Done 在 OnCommitted 之后才 Set，同步等待方读数无竞态
  （老版为尽力语义）。宿主 SqliteRecordQue 改壳后公开 API 零改动。
- **新增 `Cameras` 命名空间（三品牌工业相机层）**：`KxCamFrame`（中性帧 DTO：Mono8/
  Range16 + 尺度元数据，**解 ICogImage 焊点**——库零 VisionPro 依赖，换图像库宿主
  直接消费）+ `KxCameraDeviceBase`（抽象基类：生命周期/触发/回调/掉线/NoteNoDevice
  静默开关）+ `KxCameraDiscovery`（三品牌只发现不打开）+ `KxGigEIpAdmin`（改 IP
  执行体：校验与 SDK 执行分离/海康 GevPersistent 三键+DeviceReset 复位生效链/
  同网段前置检查）+ 三适配器 `KxDaHengCamera`（Init/Uninit 进程级配对计数/深拷
  防花屏/最优包大小/帧防抖）、`KxHik2DCamera`（Create/Destroy 配对/丢包重发使能/
  EnsureMono8 验真/特征名新旧回落/软触发武装兜底/探活断点）、`KxHik3DCamera`
  （Depth +32768 偏移重映射/Profile 高度钳 5/半成品空壳一比一保留并注明欠账）。
  宿主 `CameraAbstract` 组合壳 + `CogFrameBridge` 桥（KxCamFrame→ICogImage：Stride
  感知拷贝/占位黑图/3D 尺度变换/Profile 承载对象保活环形槽），存量调用点零改动。
- **依赖增量**：`libs/` 新增 System.Data.SQLite.dll（1.0.119）+ x64/x86 SQLite.Interop.dll
  （native 三件套，消费项目按进程位数部署）+ GxIAPINET.dll / MvCameraControl.Net.dll /
  Mv3dLpNet.dll（三相机 wrapper，均 AMD64；**原生运行库硬约束**：海康 MVS 客户端/
  大恒 Galaxy 驱动/Mv3dLP 运行库须现场安装，缺驱动机器上"校验与 SDK 执行分离"
  保证枚举/校验路径可用，SDK 执行路径走正常错误串不崩进程）。
- **验证**：库冒烟 173 全绿（FAIL=0 SKIP=3 基线不变）；宿主侧全量回归 22 批全绿
  （含新增 W22 探针 46 项：引擎直测 18 + 壳兼容 6 + 相机桥 12 + IP/发现/壳转发 10，
  含坏库自愈/坏 SQL 单条隔离/Solo 提交回执回滚修正/Stride 感知逐行拷像素逐位断言
  等负路径）；真机冒烟启动/登录/相机装配（4 台 SN 品牌识别）/掉线状态/Mock 桥/
  优雅退出全链正常。

## V1.11.0（2026-11-16）视觉检测核心沉库：YOLO 检测域入库（源自 YoloDetector 项目）

- **新增 `Vision/Detection/` 核心库**（Kaleidoscope.Detection.dll，多目标 net472/netstandard2.0，
  dotnet build 构建）：自 YoloDetector 项目 Detection 类库沉淀的通用视觉检测核心 19 个源文件——
  YOLO ONNX 检测器（YoloV26Detector：letterbox 预处理/双格式输出解析/NMS）+ 姿态检测器
  （YoloPoseDetector：逐人裁剪扩边→17 关键点）、RTSP 帧源（RtspFrameCapturer：断流两层自愈），
  检测域接口六件套（IYoloDetector/IPoseDetector/IDetectionPipeline/IFrameSource/
  IDetectionVisualizer/IDetectionResultProcessor 及后处理三实现），Mat↔SKBitmap 高性能互转
  （MatExtensions），可视化双方案（Skia 红框/OpenCV 绿框），ROI 框选状态机 + Zoom 坐标换算
  （RoiSelectionState/ZoomMapping/EsdRoiRect），分级日志门面（LogManager）。
- **命名空间保持 `YoloDetection` 不改**：源宿主（YoloDetector）以 git submodule 引用本库，
  同名命名空间类型无缝解析，两侧代码零 using 改动；接入指南见 `Vision/Detection/README.md`。
- **依赖 vendor 入库（约 201MB）**：托管 DLL（OpenCvSharp/OnnxRuntime/SkiaSharp/System.*）
  + native 运行库（Windows .dll + Linux .so），克隆即编译、运行全离线；与主库 MSBuild 老式
  工程并存，Vision 子库用 `dotnet build Vision/Detection/Kaleidoscope.Detection.csproj`。
- **管道实现留宿主**：YoloDetectionService（Monitor 生产者-消费者管道，带静电触摸/门监测
  业务旁路）留在源宿主业务层，本库只出 `IDetectionPipeline` 契约——依赖方向是管道→业务
  分析器，与"业务留宿主"的拆分一致，复用方按接口注释自行实现或参考源宿主。
- 验收：独立编译双目标 0 error 0 warning；源宿主 submodule 引用改造后全量回归全绿
  （记录见 YoloDetector 仓库 CHANGELOG）。

## V1.10.1（2026-11-15）使用说明 6.7 补双宿主回接先例（CureLine V0.0.106 回接落地）

- **纯文档变更，零代码/零 API 变化**（用例基线 173 不变）：`使用说明.md` 6.7 节补
  "回接先例（2026-11-15，双宿主对位）"——HJVision 侧 LineFlow 直消费发号器
  （`EntityTracker<uint>` 薄壳化，宿主 V0.9.66）+ CureLine 侧政策源四字段收口
  `PolicySource<T>`、灯写账本换 `DedupEffectSink` 并补 Attach/断连对齐清账口
  （宿主 V0.0.106，三期原语回接）。
- 沉淀三条对接纪律进使用说明：①宿主锁 → 库内部锁单向嵌套绝不反向；②带参查询
  （Func<r,l,…>）与函数桩（缺省=活时钟等）形态不对口 PolicySource 勿硬套；
  ③已有健壮轮询骨架（停机静默阀/重连节流）的宿主总线可保留骨架只对位契约，
  新信号源一律继承库基类 PollingSignalSource。
