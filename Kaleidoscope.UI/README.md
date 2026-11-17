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

## 内容清单（V2.0.0）

| 件 | 文件 | 说明 |
| --- | --- | --- |
| 圆角弹窗基类 | `Views/ChromeDialogWindow.cs` | 子类 XAML 根内容自动进骨架内容槽；DP：`CaptionHeight`(34)、`Resizeable`(false→缩放边 0/6) |
| 圆角窗骨架样式 | `Themes/ChromeStyles.xaml` | `Fx.WinShadow` + `WinTitleBar`/`WinTitleText`/`WinTitleClose`/`WinSysBtn`/`WinSysClose`——供不继承基类的窗（主窗等）手抄用 |
| 按钮/输入族 | `Themes/FreshStyles.xaml` | `GhostButton`/`PrimaryGhostButton`/`FreshButton`/`FreshPrimaryButton`/`FreshDangerButton`/`FreshSuccessButton`/`FreshInput`/`FreshPwdInput`/`FreshCombo`/`FreshComboItem`（全 keyed，绝不做隐式） |
| 缺省键板 | `Themes/DefaultLight.xaml` / `DefaultDark.xaml` | 36 语义键缺省值（取自 CureStation Light/Dark，一字未动）；新宿主开箱即用 |
| 转换器 | `Converters/UiConverters.cs` | `InverseBoolConverter` / `StringEqualsConverter` |

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

## 键名契约（改 = 全库宿主回归）

控件引用的 36 个语义键清单见 [Themes/DefaultLight.xaml](Themes/DefaultLight.xaml) 头注释。
**键名是契约、键值可换皮**——`DynamicResource` 缺键=静默空白（不报错，现场肉眼难查），
宿主必须提供键板或合并缺省键板。宿主侧应有"库字典引用的语义键 ⊆ 宿主键板键集"
的文本级契约测试（参考 CureLine `KaleidoscopeUiContractTests`，照搬其
`ThemeDictionaryTests` 模式）。

### 程序私有像素契约（宿主覆盖通道）

`FreshPwdInput` 的 `Padding`（库缺省 `10,0,8,0`）这类逐程序校准值，宿主在本地
App.xaml 重定义同名键覆盖即可（CureStation=`3,0,8,0`，来历：PasswordBox 内部渲染
无 TextBoxView 内缩，做"账号/密码首字符左缘对齐"校准的值；动值须目检截图像素比对）。

## 路线图

| 波次 | 内容 | 状态 |
| --- | --- | --- |
| 第一波 | 圆角弹窗基类 + 骨架样式 + 按钮/输入族 + 缺省键板 + 转换器 + 契约测试 | ✅ 本版 |
| 第二波 | 主窗基类（品牌顶栏 44 + 系统三钮 + 最大化 Margin 补偿）；ThemeManager 沉库（两宿主逐行相同的 ParseTheme/ApplyTheme/ToggleTarget）；ViewModelBase（含 UiInvoke 封送） | 规划 |
| 第三波 | 状态徽章族（StateBadge：状态枚举 DP + `Brush.Tint{State}`/`Pill{State}` 同名键约定）；卡片双层骨架控件（垫层投影+内容层，CureLine V0.0.108 性能架构）；分页条控件 | 规划 |
| net472 多目标 | HJVision-WPF（net472）接入需加多目标（net472 WPF 无 UseWPF，须 classic 程序集引用）——跨框架视觉回归量大，等有真实宿主诉求再做 | 规划 |

## 下沉纪律

- 沉进来的件必须是"口径来源明确"的（哪个宿主验证过、值一字未动）；凭空新设计的
  控件不收——库是提炼器不是设计稿。
- 隐式样式一律不做（全局劫持宿主是最大坑）；宿主要隐式化在宿主内
  `BasedOn="{StaticResource 库键}"` 再造。
- 零硬编码色：颜色全走语义键 DynamicResource；新键先加进 Default 键板 + 契约测试。
- 改口径（圆角/高度/投影参数/键名）= 影响所有宿主，先在宿主侧目检像素再动，
  CHANGELOG 记一笔。
