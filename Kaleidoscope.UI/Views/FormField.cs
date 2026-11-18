using System.Windows;
using System.Windows.Controls;

namespace Kaleidoscope.UI.Views;

/// <summary>
/// 表单字段行（标签 + 右侧输入控件容器）——全局复用控件（V2.1.0 自宿主
/// HJVision-WPF 迁入，语义零改动；隐式模板在 Themes/ShellStyles.xaml）。
///
/// 【为什么抽这个】49 个窗体里有两百多处「86px 右对齐标签 + 输入控件」的栅格块，
/// 写法完全同构，每处都把同一套样板抄一遍：栅格两列 + FormLabel86 标签 +
/// 输入控件 Height=30 + 同一个 ToolTip 抄两份。样板重复 = 外观不统一 + 维护
/// 成本线性增长，所以收进控件：一行一个字段。
///
/// 【用法】
///   &lt;ui:FormField Label="工作模式：" ToolTip="IO经PLC=…"&gt;
///       &lt;ComboBox ItemsSource="{Binding ModeOptions}" Style="{StaticResource KxComboBox}"/&gt;
///   &lt;/ui:FormField&gt;
/// 标签宽默认 86（与 FormLabel86 同口径）；需要 118 的窗体设 LabelWidth="118"；
/// 必填项加 Required="True"，星标由控件自己画（不用再手摆 RequiredMark TextBlock）。
/// </summary>
public class FormField : ContentControl
{
    public FormField()
    {
        // 整行不是可聚焦控件，焦点应落在里面的输入控件上。
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    /// <summary>字段标签文案（一般带全角冒号「：」，与老版/设计稿同口径）。</summary>
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(FormField),
            new PropertyMetadata(""));

    public string Label
    {
        get { return (string)GetValue(LabelProperty); }
        set { SetValue(LabelProperty, value); }
    }

    /// <summary>标签列宽（默认 86，与 FormLabel86 同口径；宽标签窗体设 118）。</summary>
    public static readonly DependencyProperty LabelWidthProperty =
        DependencyProperty.Register(nameof(LabelWidth), typeof(double), typeof(FormField),
            new PropertyMetadata(86.0));

    public double LabelWidth
    {
        get { return (double)GetValue(LabelWidthProperty); }
        set { SetValue(LabelWidthProperty, value); }
    }

    /// <summary>输入控件行高（默认 30，与全工程表单输入框同口径）。</summary>
    public static readonly DependencyProperty EditorHeightProperty =
        DependencyProperty.Register(nameof(EditorHeight), typeof(double), typeof(FormField),
            new PropertyMetadata(30.0));

    public double EditorHeight
    {
        get { return (double)GetValue(EditorHeightProperty); }
        set { SetValue(EditorHeightProperty, value); }
    }

    /// <summary>是否必填（控件自己在标签右端画红色星标）。</summary>
    public static readonly DependencyProperty RequiredProperty =
        DependencyProperty.Register(nameof(Required), typeof(bool), typeof(FormField),
            new PropertyMetadata(false));

    public bool Required
    {
        get { return (bool)GetValue(RequiredProperty); }
        set { SetValue(RequiredProperty, value); }
    }

    /// <summary>
    /// 标签是否走弱色（Brush.Shell.FgDim）——默认 false（正常前景 Brush.Shell.Fg）。
    /// 【为什么用 bool 开关而不是 Foreground DP】只读字段的标签是弱色（提示"这行
    /// 不可编辑"），可编辑字段是正常色；bool 两值最省心，且调用方不必知道主题键名
    /// （换主题键只改控件一处）。
    /// </summary>
    public static readonly DependencyProperty LabelMutedProperty =
        DependencyProperty.Register(nameof(LabelMuted), typeof(bool), typeof(FormField),
            new PropertyMetadata(false));

    public bool LabelMuted
    {
        get { return (bool)GetValue(LabelMutedProperty); }
        set { SetValue(LabelMutedProperty, value); }
    }

    /// <summary>
    /// 把标签列宽写进模板 Grid 的第一列。
    ///
    /// 【为什么必须用代码写，不能用 TemplateBinding】——**这是本控件踩过的第一个真坑**：
    /// 模板里写 &lt;ColumnDefinition Width="{TemplateBinding LabelWidth}"/&gt; **不会报错，但也完全不生效**。
    /// 原因：ColumnDefinition 不是可视树元素，**不在 TemplatedParent 的继承链上**，
    /// TemplateBinding 在它上面解析不到模板父级 → 静默回落成 ColumnDefinition 的默认值 1*。
    /// 后果：标签列吃满剩余空间，输入控件被挤到最右（实测 HipotWindow 的「接入方式：」
    /// 标签从 x=14 被推到 x=367，整页排版错位）。
    /// （对照：TemplateBinding 绑 TextBlock/ContentPresenter 这类可视元素是有效的，
    ///   所以同一模板里的 EditorHeight / Label 都没事，只有列宽这一处出事。）
    /// 修法：模板 Grid 命名 PART_LayoutGrid，在 OnApplyTemplate 里用
    /// GetTemplateChild 拿到它，把第一列 Width 设成 LabelWidth；LabelWidth 变化时重刷一次。
    /// </summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        SyncLabelColumnWidth();
    }

    /// <summary>LabelWidth 变化时重刷标签列宽（运行期改宽度立即生效）。</summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == LabelWidthProperty)
        {
            SyncLabelColumnWidth();
        }
    }

    /// <summary>把 LabelWidth 同步到模板 Grid 的第一列（找不到模板就跳过，不抛异常）。</summary>
    private void SyncLabelColumnWidth()
    {
        Grid layout = GetTemplateChild("PART_LayoutGrid") as Grid;
        if (layout == null || layout.ColumnDefinitions.Count < 1)
        {
            return;
        }
        layout.ColumnDefinitions[0].Width = new GridLength(LabelWidth);
    }
}
