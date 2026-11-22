# Kaleidoscope.UI

公司级 WPF 复用控件库——把各宿主 WPF 项目重复手抄的"骨架级 UI 资产"收口成库。
首建：宿主 CureLine V0.0.109 反哺（2026-11-18），下沉前完成了两程序 UI 资产盘点调研。

## 为什么有这个库

"换新客户做新界面时底层服务一行不改"是 kaleidoscope 的定位（AGENTS.md），界面层此前没有对应物：

- **圆角窗骨架**：CureLine 单仓 26 个弹窗手抄同一段骨架 XAML（WindowStyle=None +
  WindowChrome + Margin 14 + 圆角 Border + 投影 + 标题条 + ✕，逐窗 ~15 行），
  圆角 10 / 标题条 34 / 投影 Blur 24 全靠人肉对齐必然漂移。
- **按钮/输入族**：GhostButton / FreshButton / FreshInput / FreshCombo 等两程序
  App.xaml 各抄一份（仅 FreshPwdInput Padding 一处像素契约差异）。
- **主题键板**：语义键（`Brush.*` / `Brush.Fresh.*`）+ 双主题同名异值 + ThemeManager
  换字典的机制在两程序同构重复，键集合靠文本级测试锁。

上游先例：HuaJiVision-WPF 的 `SunnyDialog`（圆角窗基类）+ `SunnyTheme.xaml`（1649 行
语义键板）已验证过"基类+键板"路线——本库是公司内第二次落地，吸收其血泪（隐式样式
注入时机、圆角裁内容 Clip），按 CureLine 现行口径实现（无 Clip、无隐式注入，迁移后
与手抄窗逐像素一致）。

## 内容清单（V2.1.0）

| 件 | 文件 | 说明 |
| --- | --- | --- |
| 圆角弹窗基类 | `Views/ChromeDialogWindow.cs` | 子类 XAML 根内容自动进骨架内容槽；DP：`CaptionHeight`(34)/`Resizeable`/`ShowMinButton`/`ShowMaxButton`/`IsMaxButtonEnabled`/`CloseCommand`/`Subtitle`/`TitleIconChar`/`TitleIcon`/`TitleBackground`/`CancelCommand`(ESC→命令，缺省无行为)/`UseLayeredTransparency`(false=HWND 窗物理 Region 圆角)/`ResizeBorderThickness`(6)/`ShowTitleBar`(false=自绘标题栏窗)/`FixMaximizedToWorkArea`(**V2.2.1 新增**，缺省 false=两宿主现状不变——可缩放弹窗最大化盖任务栏的修正开关，实现经 KxShellInterop 与主窗基类共享)；宿主级常量走 `Views/KxShellDefaults` 静态配置（ShadowMargin=14/UseContentClip=false/ImplicitStyleKeys=null/Font=null/ShowInTaskbar=true/StartupLocation=Manual/SysButtonWidth=44/CloseFontWeight=Normal——缺省全部=CureLine 现状，HJVision 启动一行配置切到 HJVision 口径）。**V2.1.0 融合宿主 HJVision 的 SunnyDialog/ChromeDialog 两基类（三合一）** |
| 无边框主窗基类 | `Views/ChromeBorderlessWindow.cs` | **V2.2.0 新增**（宿主 YoloDetector 反哺）：主窗/自绘标题栏窗形态的**纯行为骨架**——WindowStyle=None+分层透明三件套+WindowChrome 外壳+根 Border 圆角 Clip 同步+最大化圆角归零+**WM_GETMINMAXINFO 最大化工作区修正**（库内首件，防最大化盖任务栏）。**零键板依赖**（不引语义键不插视觉，底色/圆角/顶栏全由子类根 Border 自绘）；刻意不做标题条/系统三钮（留宿主自绘）；含 HWND 的窗禁用（分层窗 HWND 不渲染红线，类头注明） |
| 外壳共享互操作 | `Views/KxShellInterop.cs` | **V2.2.1 新增**（两窗基类去重收拢）：`BuildChrome`（WindowChrome 库口径构造，两基类共用）+ `EnableMaximizedWorkAreaFix`（WM_GETMINMAXINFO 最大化工作区修正的 Win32 互操作全套，供两个窗基类与直连窗借用） |
| 标题条控件 | `Views/TitleBar.cs` | 渐变底+底边线+顶角圆（最大化归零）+图标胶囊+标题绑 Window.Title+副题+—/▢/× 三钮；V2.1.0 自 HJVision SunnyTitleBar 迁入去 Sunny 化，CureLine 缺省参数复现 ✕ 单钮口径 |
| 圆角窗骨架样式 | `Themes/ChromeStyles.xaml` | `Fx.WinShadow` + `WinTitleBar`/`WinTitleText`/`WinTitleClose`/`WinSysBtn`/`WinSysClose`——供不继承基类的窗（主窗等）手抄用 |
| 按钮/输入族 | `Themes/FreshStyles.xaml` | `GhostButton`/`PrimaryGhostButton`/`FreshButton`/`FreshPrimaryButton`/`FreshDangerButton`/`FreshSuccessButton`/`FreshInput`/`FreshPwdInput`/`FreshCombo`/`FreshComboItem`（全 keyed，绝不做隐式）——CureLine 视觉族 |
| Kx 族控件样式 | `Themes/ShellStyles.xaml` | **V2.1.0 新增**：`KxPrimaryButton` 等按钮六档/`StatusChip` 对/`KxTextBox`·`KxPasswordBox`·`KxDatePicker`·`KxComboBox`/`KxSwitch`/`KxTitleClose·Min·MaxButton`/`BrandTab*`/`BrandGrid*`/`WinMenu*`/`KxCheckBox`·`KxRadioButton`·`KxGridCheckBox`/`KxGroupBoxImplicit`/`FormLabel*`·`RequiredMark`·`Hint*`·`SectionTitle`/`Kpi*`/`ChromeDialogRootBorder`·`HeadBorder`/`KxDialog*Implicit` 七类兜底/`FormField` 隐式模板——自宿主 HJVision SunnyTheme 通用段迁入**去 Sunny 化**（Kx=Kaleidoscope 前缀；与 Fresh 族的关系：模板结构同构才融、视觉语言不同分族，键名空间统一 Brush.*）。<br>**V2.3.0 新增业务壳族**（宿主张卡片式看板反哺，值逐像素取自 ProductionStatistics）：`KxThemeSwitch`（主题开关滑动轨道）/`KxShiftTab`（双行班次页签）/`KxLangButton`（语言单选胶囊）/`KxCard`（圆角 18 + 投影卡片）/`KxIconBox`（26×26 主色弱底图标方块）/`KxTopBar`（顶栏容器）/`KxInfoBox`（Tag=Ok/Warn/Ng/Pri 语义弱底+同色描边提示框）/`KxStatusDot`（Tag 语义状态灯圆点）/`KxRingBg`+`KxRingValue`（稼动率圆环底环/值环）/`KxSlimScrollBar`（10px 细滚动条，隐式化由宿主 BasedOn 再造）；另补 `StatusChipText` 的 `Tag="Pri"` 触发（原仅 Ok/Warn/Ng/Err） |
| 表单控件 | `Views/FormField.cs` 等 | **V2.1.0 迁入**：FormField（表单行）/NumericUpDownLite（数字微调）/PasswordInput（密码+眼睛）/EyeIconView（矢量眼） |
| 转换器 | `Converters/UiConverters.cs` | `InverseBoolConverter` / `StringEqualsConverter` / `CapsuleRadiusConverter`（真胶囊）/ NullToVisibility / TrueToVisibility + 静态便捷单例 |
| 圆角裁剪附加属性 | `Views/KxRoundedClip.cs` | **V2.3.1 新增**（宿主 ProductionStatistics 反哺）：任意 FrameworkElement 一行 XAML 挂圆角矩形裁剪 `ui:KxRoundedClip.Radius="12"`——`Border.CornerRadius` 只裁自身背景不裁子元素的补位（卡片里放 DataGrid/图片等自带方角背景的子元素、四角盖过圆弧时的标准解法）；Loaded/SizeChanged 按实际尺寸同步（几何实例按元素复用，缩放零分配），Radius=0 摘除恢复方角；语义同 `ChromeBorderlessWindow.UpdateRootClip`（元素自身坐标整体裁剪），与 `ChromeDialogWindow` 的"卡片内容内缘裁剪"是两种口径，按需选用 |
| 缺省键板 | `Themes/DefaultLight.xaml` / `DefaultDark.xaml` | 全键集缺省值（V2.1.0 扩容：Fresh 36 键+Shell 族 45 键；**V2.3.0 补业务壳族 9 键**；**V2.3.1 补 `Fn.ShellRadiusBottom`（底缘圆角，与 ShellRadiusTop 成对）**，各板共 91 键、两板键集一致）；新宿主开箱即用 |

**兄弟工程**：`../Kaleidoscope.UI.WinForms`（V1.0.0 骨架）——WinForms 侧双栈 UI 基类储备
（`KxThemeTokens` 色板 Token 层 + `ShellDialogForm` 圆角自绘弹窗基类，零 SunnyUI 依赖，
对标宿主原版 ThemeDialogForm 口径；net472+net8.0-windows 多目标）。

## 控件复用索引（按场景查——宿主 UI 开工先查这里，再决定自绘）

> **立节缘由（2026-11-22 AIris 实训）**：AIris 主窗导航胶囊自绘了 ControlTemplate（还漏清
> WPF 默认焦点虚线框），而库内 `KxLangButton` 本就是"语言单选胶囊"导航形态——**库里有对应物
> 却没查**。教训固化为纪律：宿主新建任何控件/样式前，先跑本节末尾的检索命令对着场景表查一遍。
> 本表按"我要做个 X"组织，`→` 后是推荐控件/样式键。

| 场景（我要做个…） | 推荐控件 / 样式键 | 备注 |
| --- | --- | --- |
| 模态/非模态弹窗 | 继承 `ChromeDialogWindow`（XAML 根直接 `<ui:ChromeDialogWindow>`） | 圆角/标题条/三系统钮/ESC 全内置，宿主零自绘 |
| 无边框主窗 / 自绘标题栏窗 | 继承 `ChromeBorderlessWindow` | 纯行为骨架（零键板），视觉宿主自绘；WindowChrome+最大化修正内置 |
| 窗体标题条 | `TitleBar` 控件 或 `WinTitleBar`/`WinTitleText`/`WinSysBtn`/`WinSysClose` 样式 | 直连窗手抄用样式版 |
| **窗体右上三系统钮（—/▢/✕）** | `KxTitleMinButton` / `KxTitleMaxButton` / `KxTitleCloseButton` | hover 内置（关闭=红底），glyph 由宿主 Content 给（—/▢·❐/✕）；勿自绘 |
| **顶部/侧栏导航胶囊项** | `KxLangButton`（RadioButton 胶囊导航形态） | 选中态胶囊语言现成；宿主换主题键即换皮。AIris 曾漏查自绘（本节缘由） |
| 页签/Tab | `BrandTabItem` + `BrandTabControl`；班次双行页签用 `KxShiftTab`（ToggleButton） | |
| 右键/下拉菜单 | `WinContextMenu`/`WinMenuItem`/`WinMenuItemSub`/`WinMenuSeparator` | |
| 普通按钮（主/次/成功/警告/危险/成功） | Kx 族：`KxPrimaryButton`/`KxSecondaryButton`/`KxOkButton`/`KxWarnButton`/`KxDangerButton`/`KxSuccessButton`；Fresh 族：`FreshPrimaryButton`/`FreshButton`/`FreshDangerButton`/`FreshSuccessButton`；幽灵钮：`GhostButton`/`PrimaryGhostButton` | 两族并存选择：同宿主视觉语言统一选一族，勿混用 |
| 输入框 | `KxTextBox`（Kx 族）或 `FreshInput`（Fresh 族）；**两者 Padding 模板都已接管**——宿主元素级 Padding 不再生效或垂直内缩会裁文本（AIris 实训：`Padding="h,v"` 的 v>0 裁掉 PART_ContentHost 文本行），**禁设非零垂直 Padding**，居中交给 VerticalContentAlignment | |
| 密码框 / 数字框 / 日期 | `FreshPwdInput` 或 `KxPasswordBox` / `NumericUpDownLite` 控件 / `KxDatePicker` / `KxComboBox`（+`FreshComboItem`） | 表单行组合用 `FormField` 控件（标签列+必填星标+ToolTip 整行） |
| 下拉选择 | `KxComboBox` / `FreshCombo` | **SelectedItem 绑字符串时 Items 请给字符串集合**（绑 ComboBoxItem 集合会让 SelectedItem 永远对不上显示空——AIris 实训同款错误） |
| 状态胶囊（OK/WARN/NG 徽标） | `StatusChip` + `StatusChipText`（Tag=Ok/Warn/Ng/Err/**Pri**） | 语义色走 Dim 键板，深浅主题自动跟随 |
| 状态灯圆点 / 提示框 | `KxStatusDot`（Tag 同上） / `KxInfoBox`（Tag=Ok/Warn/Ng/Pri） | |
| 卡片 / KPI / 圆环 | `KxCard`（圆角+投影）/ `KpiCard`+`KpiLabel`+`KpiValue`+`KpiSub` / `KxRingBg`+`KxRingValue` / `KxIconBox` | 卡内放方角子元素（DataGrid/图片）配 `KxRoundedClip.Radius` 裁剪 |
| 数据表格 | `BrandGridHeader`/`BrandGridRow`/`BrandGridCell` | ListView/GridView 列头+行样式 |
| 表单标签体系 | `FormLabel86`/`FormLabel118`/`RequiredMark`/`HintText`/`HintWarnText`/`SectionTitle` | 86/118=标签列宽两种口径 |
| 开关 / 勾选 / 单选 | `KxSwitch` / `KxCheckBox` / `KxRadioButton` / `KxGridCheckBox` / `KxThemeSwitch`（深浅主题切换滑动轨道） | |
| 细滚动条 | `KxSlimScrollBar`（隐式化由宿主 BasedOn 再造） | |
| 顶栏容器 | `KxTopBar` | |
| 布尔/等值/胶囊半径转换器 | `UiConverters` 静态单例（InverseBool/StringEquals/NullToVisibility/TrueToVisibility/CapsuleRadius） | 勿再手写同款 IValueConverter |

**自绘三条件（不满足即回去查表/沉库）**：①先跑检索命令确认库内确无同职责控件；
②自绘必须走宿主主题 Token（禁写死色值）且 `FocusVisualStyle="{x:Null}"` 清焦点框；
③自绘件登记到宿主 AGENTS.md 索引表——下一轮评审"该不该沉库"。

**检索命令（宿主仓库根执行；开工先跑，改库后再跑防漂移）**：

```powershell
# 列出全部对外样式键（对着上面场景表查；支持 grep 语义词，如 Button/Chip/Tab/Title）
grep -o 'x:Key="[^"]*"' ThirdParty/Kaleidoscope/Kaleidoscope.UI/Themes/ShellStyles.xaml | sort
grep -o 'x:Key="[^"]*"' ThirdParty/Kaleidoscope/Kaleidoscope.UI/Themes/FreshStyles.xaml | sort
grep -o 'x:Key="[^"]*"' ThirdParty/Kaleidoscope/Kaleidoscope.UI/Themes/ChromeStyles.xaml | sort
# 看某键完整定义（模板结构/触发器/Tag 契约）
grep -n -A 30 'x:Key="KxLangButton"' ThirdParty/Kaleidoscope/Kaleidoscope.UI/Themes/ShellStyles.xaml
```

## 接入指南（两步）

### ① 合并字典（App.xaml）

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <!-- 缺省键板（可省：宿主自带同名语义键时不必合并） -->
      <ResourceDictionary Source="pack://application:,,,/Kaleidoscope.UI;component/Themes/DefaultLight.xaml"/>
      <!-- 骨架样式 + 控件族 -->
      <ResourceDictionary Source="pack://application:,,,/Kaleidoscope.UI;component/Themes/ChromeStyles.xaml"/>
      <ResourceDictionary Source="pack://application:,,,/Kaleidoscope.UI;component/Themes/FreshStyles.xaml"/>
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

主题换皮：宿主 ThemeManager 替换宿主键板字典（`MergedDictionaries[0]`）即可——
控件引的是语义键，宿主键板值变、控件自动跟随（CureLine 同款机制）。

### ② 弹窗改继承基类

```xml
<ui:ChromeDialogWindow x:Class="YourApp.SomeWindow"
    xmlns:ui="clr-namespace:Kaleidoscope.UI.Views;assembly=Kaleidoscope.UI"
    xmlns:shell="http://schemas.microsoft.com/winfx/2006/xaml/presentation/shell"
    Title="标题" Width="720" Height="560"
    Resizeable="True">          <!-- 可缩放窗才写；定高弹窗缺省 false -->
  <!-- 原根内容原样保留（手抄骨架删除） -->
</ui:ChromeDialogWindow>
```

```csharp
public partial class SomeWindow : ChromeDialogWindow   // code-behind 基类同步改
```

### ②' 主窗/自绘标题栏窗改继承 ChromeBorderlessWindow（V2.2.0）

顶栏是宿主业务内容、视觉自成体系的窗（主窗等）走纯行为基类——零键板依赖，
宿主不合并库字典也可用：

```xml
<ui:ChromeBorderlessWindow x:Class="YourApp.MainWindow"
    xmlns:ui="clr-namespace:Kaleidoscope.UI.Views;assembly=Kaleidoscope.UI"
    CaptionHeight="62" CornerRadius="14" ...>   ← 标题区高度/圆角半径按宿主顶栏传
  <Border CornerRadius="14">                    ← 根 Border：视觉圆角与溢出裁剪的载体
    ...（顶栏/内容全部自绘）
  </Border>
</ui:ChromeBorderlessWindow>
```

WindowStyle/AllowsTransparency/Background/WindowChrome 由基类构造设置（子类
XAML 不重复声明）；根 Border 的 CornerRadius 应与 CornerRadius DP 一致（基类
在最大化/还原时直接改写根 Border 本地值，还原恢复取 DP 值）；最大化工作区
修正（防盖任务栏）缺省内建（`FixMaximizedToWorkArea`，勿关）。注意宿主主
csproj 若用 submodule/子目录方式引用，submodule 排除规则必须含
`<Page Remove="...">`（UseWPF 默认通配会把库 .xaml 当 Page 扫进宿主编译，
实测 MC3074）。

## 键名契约（改 = 全库宿主回归）

控件引用的语义键清单见 [Themes/DefaultLight.xaml](Themes/DefaultLight.xaml) 头注释
（V2.1.0 全键集：Fresh 36 键 + Shell 族 45 键——`Brush.Shell.*` / `Fn.Shell*` /
`Fx.Shell*` 为 Kx 族控件样式键；**V2.3.0 再补业务壳族 9 键**：`Brush.Shell.Theme*` 七键
+ `Fn.ShellCardRadius` + `Fx.ShellCardShadow`/`Fx.ShellTheme*Shadow` 投影，缺省板共 90 键）。
**键名是契约、键值可换皮**——`DynamicResource` 缺键=静默空白（不报错，现场肉眼难查），
宿主必须提供键板或合并缺省键板。宿主侧应有"库字典引用的语义键 ⊆ 宿主键板键集"
的文本级契约测试（参考 CureLine `KaleidoscopeUiContractTests`，照搬其
`ThemeDictionaryTests` 模式；ProductionStatistics 侧为
`tools/E2ETests/Run-ThemeContractTests.ps1`，同时校验两本主题键集一致）。

【V2.1.0 宿主键板补段红线】宿主主题字典追加库契约键段时**必须写字面值、禁止
`<StaticResource x:Key="A" ResourceKey="B"/>` 元素转发**——net472 BAML 下转发元素
实测键值错位（Effect 拿到相邻键值，宿主主窗首帧崩，HJVision 冒烟抓包）；
CureStation/CimGateway 键板补 Shell 段已按字面值落。

### 程序私有像素契约（宿主覆盖通道）

`FreshPwdInput` 的 `Padding`（库缺省 `10,0,8,0`）这类逐程序校准值，宿主在本地
App.xaml 重定义同名键覆盖即可（CureStation=`3,0,8,0`，来历：PasswordBox 内部渲染
无 TextBoxView 内缩，做"账号/密码首字符左缘对齐"校准的值；动值须目检截图像素比对）。

### 接入指南（V2.2.0 三宿主实况）

| 宿主 | 框架 | 接法 | 状态 |
| --- | --- | --- | --- |
| CureLine（CureStation/CimGateway） | net8.0-windows | HintPath net8 DLL，合并 ChromeStyles+FreshStyles（缺省板不并），宿主键板 143/74 键 | 灰度 3 窗，23 窗待第二波 |
| HJVision-WPF | net472 | ProjectReference（MSBuild 自动选 net472 产物），合并 ChromeStyles+FreshStyles+ShellStyles，宿主 ThemeLight/Dark 追加库契约键段（字面值），KxShellDefaults 一行配置切 HJVision 口径 | **51 窗全切**（36 SunnyDialog+9 ChromeDialog+6 直连窗借用静态方法） |
| YoloDetector | net472 | ProjectReference（自动选 net472 产物），**只继承 ChromeBorderlessWindow 不合并任何字典**（零键板基类），宿主玻璃拟态视觉全自绘 | 主窗 1 窗全切（v2.11.2），全量回归 182 用例+三态目检零变化 |
| ProductionStatistics | net472 | ProjectReference（自动选 net472 产物），合并 DefaultLight+FreshStyles+ShellStyles（主题只换宿主 Light/Dark），宿主主题追加库契约键段（字面值，133/133 键）；主窗继承 ChromeBorderlessWindow + 全量 `Kx*` 壳族 | **V2.3.0 主窗全切**（除 KPI 卡片外壳/DataGrid 刻意保留），深浅双主题+1280/1440/1920 三档目检 |
| 新宿主 | 任意 | 合并 DefaultLight+ChromeStyles+FreshStyles+ShellStyles 四字典零配置开箱 | — |

### 宿主裁剪目标框架（V2.1.1，离线构建用）

默认 `TargetFrameworks=net472;net8.0-windows`，net8 宿主（CureLine/CureStation/CimGateway）行为不变。
net472 宿主若想彻底不碰 net8 targeting pack（工厂离线机），在**宿主仓库根**的
`Directory.Build.props` 设一行即可：

```xml
<PropertyGroup>
  <KaleidoscopeUITargetFrameworks>net472</KaleidoscopeUITargetFrameworks>
</PropertyGroup>
```

效果：restore/build 只走 net472，`project.assets.json` 的 `libraries` 为空（零 NuGet 包）；
宿主 csproj 的 `ProjectReference` 写法完全不变。需要临时恢复双目标时，显式传
`-p:KaleidoscopeUITargetFrameworks="net472;net8.0-windows"`。

## 路线图

| 波次 | 内容 | 状态 |
| --- | --- | --- |
| 第一波 | 圆角弹窗基类 + 骨架样式 + 按钮/输入族 + 缺省键板 + 转换器 + 契约测试 | ✅ V2.0.0 |
| 1.5 波 | **net472 多目标 + 弹窗基类三合一融合（HJVision SunnyDialog/ChromeDialog）+ TitleBar/ShellStyles/表单控件迁入去 Sunny 化 + KxShellDefaults 宿主常量 + HJVision 51 窗全切** | ✅ V2.1.0 |
| 第二波 | 主窗基类（品牌顶栏 44 + 系统三钮 + 最大化 Margin 补偿）；ThemeManager 沉库（CureLine 两宿主逐行相同的 ParseTheme/ApplyTheme/ToggleTarget 孪生副本 + HJVision ThemeService）；ViewModelBase（含 UiInvoke 封送）——**主窗行为地基已提前落地：`ChromeBorderlessWindow`（V2.2.0，YoloDetector 反哺，纯行为零键板；V2.2.1 外壳构造收拢 `KxShellInterop`；带键板视觉的品牌主窗基类仍待 CureLine 口径）** | 部分落地 |
| 2.5 波 | **业务壳族反哺（ProductionStatistics V2.3.0）**：`KxThemeSwitch` / `KxShiftTab` / `KxLangButton` / `KxCard` / `KxIconBox` / `KxTopBar` / `KxInfoBox` / `KxStatusDot` / `KxRingBg`·`KxRingValue` / `KxSlimScrollBar` 十一件 keyed 样式 + `StatusChipText` 补 `Tag="Pri"`；缺省键板两板各补 9 键（90 键/板、键集一致）；库内 `{StaticResource Fn.*}` 统一改 `{DynamicResource}`（宿主换主题才能生效） | ✅ V2.3.0 |
| 第三波 | 状态徽章族（StateBadge：状态枚举 DP + `Brush.Tint{State}`/`Pill{State}` 同名键约定）；卡片双层骨架控件（垫层投影+内容层，CureLine V0.0.108 性能架构）；分页条控件 | 规划 |
| ~~net472 多目标~~ | ~~等真实宿主诉求再做~~ | ✅ V2.1.0 已做（ProjectReference 自动选产物；CureLine HintPath net8 路径不变） |
| WinForms 双栈 | `../Kaleidoscope.UI.WinForms`（V1.0.0 骨架：KxThemeTokens 色板 Token 层 + ShellDialogForm 圆角自绘基类，零 SunnyUI 依赖）已建；扩面等原版解耦 SunnyUI 的真实宿主诉求 | 骨架 ✅ V2.1.0 同轮 |

## 下沉纪律

- 沉进来的件必须是"口径来源明确"的（哪个宿主验证过、值一字未动）；凭空新设计的
  控件不收——库是提炼器不是设计稿。
- 隐式样式一律不做（全局劫持宿主是最大坑）；宿主要隐式化在宿主内
  `BasedOn="{StaticResource 库键}"` 再造。
- 零硬编码色：颜色全走语义键 DynamicResource；新键先加进 Default 键板 + 契约测试。
- 改口径（圆角/高度/投影参数/键名）= 影响所有宿主，先在宿主侧目检像素再动，
  CHANGELOG 记一笔。
