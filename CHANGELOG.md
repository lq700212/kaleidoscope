# 版本改动记录

> **本文件自 V1.0.0（Kaleidoscope 库抽取之日）起记，只记录 Kaleidoscope 仓库自身的改动。**
> 更早的 V2.14.26 及以前版本均为 CommandCenter 原项目的历史记录（窗口徽标、点位配置、
> 产品型号弹窗等界面功能，与 Kaleidoscope 库代码无关），**不随本仓库迁移**；其中通讯相关的
> 血泪背景（PLC 从站释放 V2.14.23、相机判定即写 V2.13.7、PW 同程序号跳过 V2.14.19、
> 存图清理防误删 V2.14.12 等）已沉淀在 `AGENTS.md`「已知通讯关键点」（位于仓库根），需要
> 原始完整记录时查原 CommandCenter 项目的 CHANGELOG.md。

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
