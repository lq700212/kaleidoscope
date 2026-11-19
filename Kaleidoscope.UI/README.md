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
| 圆角弹窗基类 | `Views/ChromeDialogWindow.cs` | 子类 XAML 根内容自动进骨架内容槽；DP：`CaptionHeight`(34)/`Resizeable`/`ShowMinButton`/`ShowMaxButton`/`IsMaxButtonEnabled`/`CloseCommand`/`Subtitle`/`TitleIconChar`/`TitleIcon`/`TitleBackground`/`CancelCommand`(ESC→命令，缺省无行为)/`UseLayeredTransparency`(false=HWND 窗物理 Region 圆角)/`ResizeBorderThickness`(6)/`ShowTitleBar`(false=自绘标题栏窗)；宿主级常量走 `Views/KxShellDefaults` 静态配置（ShadowMargin=14/UseContentClip=false/ImplicitStyleKeys=null/Font=null/ShowInTaskbar=true/StartupLocation=Manual/SysButtonWidth=44/CloseFontWeight=Normal——缺省全部=CureLine 现状，HJVision 启动一行配置切到 HJVision 口径）。**V2.1.0 融合宿主 HJVision 的 SunnyDialog/ChromeDialog 两基类（三合一）** |
| 无边框主窗基类 | `Views/ChromeBorderlessWindow.cs` | **V2.2.0 新增**（宿主 YoloDetector 反哺）：主窗/自绘标题栏窗形态的**纯行为骨架**——WindowStyle=None+分层透明三件套+WindowChrome 外壳+根 Border 圆角 Clip 同步+最大化圆角归零+**WM_GETMINMAXINFO 最大化工作区修正**（库内首件，防最大化盖任务栏）。**零键板依赖**（不引语义键不插视觉，底色/圆角/顶栏全由子类根 Border 自绘）；刻意不做标题条/系统三钮（留宿主自绘）；含 HWND 的窗禁用（分层窗 HWND 不渲染红线，类头注明） |
| 标题条控件 | `Views/TitleBar.cs` | 渐变底+底边线+顶角圆（最大化归零）+图标胶囊+标题绑 Window.Title+副题+—/▢/× 三钮；V2.1.0 自 HJVision SunnyTitleBar 迁入去 Sunny 化，CureLine 缺省参数复现 ✕ 单钮口径 |
| 圆角窗骨架样式 | `Themes/ChromeStyles.xaml` | `Fx.WinShadow` + `WinTitleBar`/`WinTitleText`/`WinTitleClose`/`WinSysBtn`/`WinSysClose`——供不继承基类的窗（主窗等）手抄用 |
| 按钮/输入族 | `Themes/FreshStyles.xaml` | `GhostButton`/`PrimaryGhostButton`/`FreshButton`/`FreshPrimaryButton`/`FreshDangerButton`/`FreshSuccessButton`/`FreshInput`/`FreshPwdInput`/`FreshCombo`/`FreshComboItem`（全 keyed，绝不做隐式）——CureLine 视觉族 |
| Kx 族控件样式 | `Themes/ShellStyles.xaml` | **V2.1.0 新增**：`KxPrimaryButton` 等按钮六档/`StatusChip` 对/`KxTextBox`·`KxPasswordBox`·`KxDatePicker`·`KxComboBox`/`KxSwitch`/`KxTitleClose·Min·MaxButton`/`BrandTab*`/`BrandGrid*`/`WinMenu*`/`KxCheckBox`·`KxRadioButton`·`KxGridCheckBox`/`KxGroupBoxImplicit`/`FormLabel*`·`RequiredMark`·`Hint*`·`SectionTitle`/`Kpi*`/`ChromeDialogRootBorder`·`HeadBorder`/`KxDialog*Implicit` 七类兜底/`FormField` 隐式模板——自宿主 HJVision SunnyTheme 通用段迁入**去 Sunny 化**（Kx=Kaleidoscope 前缀；与 Fresh 族的关系：模板结构同构才融、视觉语言不同分族，键名空间统一 Brush.*） |
| 表单控件 | `Views/FormField.cs` 等 | **V2.1.0 迁入**：FormField（表单行）/NumericUpDownLite（数字微调）/PasswordInput（密码+眼睛）/EyeIconView（矢量眼） |
| 转换器 | `Converters/UiConverters.cs` | `InverseBoolConverter` / `StringEqualsConverter` / `CapsuleRadiusConverter`（真胶囊）/ NullToVisibility / TrueToVisibility + 静态便捷单例 |
| 缺省键板 | `Themes/DefaultLight.xaml` / `DefaultDark.xaml` | 全键集缺省值（V2.1.0 扩容：Fresh 36 键+Shell 族 45 键）；新宿主开箱即用 |

**兄弟工程**：`../Kaleidoscope.UI.WinForms`（V1.0.0 骨架）——WinForms 侧双栈 UI 基类储备
（`KxThemeTokens` 色板 Token 层 + `ShellDialogForm` 圆角自绘弹窗基类，零 SunnyUI 依赖，
对标宿主原版 ThemeDialogForm 口径；net472+net8.0-windows 多目标）。

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
`Fx.Shell*` 为 Kx 族控件样式键）。
**键名是契约、键值可换皮**——`DynamicResource` 缺键=静默空白（不报错，现场肉眼难查），
宿主必须提供键板或合并缺省键板。宿主侧应有"库字典引用的语义键 ⊆ 宿主键板键集"
的文本级契约测试（参考 CureLine `KaleidoscopeUiContractTests`，照搬其
`ThemeDictionaryTests` 模式）。

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
| 新宿主 | 任意 | 合并 DefaultLight+ChromeStyles+FreshStyles+ShellStyles 四字典零配置开箱 | — |

## 路线图

| 波次 | 内容 | 状态 |
| --- | --- | --- |
| 第一波 | 圆角弹窗基类 + 骨架样式 + 按钮/输入族 + 缺省键板 + 转换器 + 契约测试 | ✅ V2.0.0 |
| 1.5 波 | **net472 多目标 + 弹窗基类三合一融合（HJVision SunnyDialog/ChromeDialog）+ TitleBar/ShellStyles/表单控件迁入去 Sunny 化 + KxShellDefaults 宿主常量 + HJVision 51 窗全切** | ✅ V2.1.0 |
| 第二波 | 主窗基类（品牌顶栏 44 + 系统三钮 + 最大化 Margin 补偿）；ThemeManager 沉库（CureLine 两宿主逐行相同的 ParseTheme/ApplyTheme/ToggleTarget 孪生副本 + HJVision ThemeService）；ViewModelBase（含 UiInvoke 封送）——**主窗行为地基已提前落地：`ChromeBorderlessWindow`（V2.2.0，YoloDetector 反哺，纯行为零键板；带键板视觉的品牌主窗基类仍待 CureLine 口径）** | 部分落地 |
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
