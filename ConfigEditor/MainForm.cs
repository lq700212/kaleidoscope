using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Sunny.UI;
using Kaleidoscope.Configuration;
using Kaleidoscope.Models;
using Kaleidoscope.Utils;

namespace Kaleidoscope.ConfigEditor
{
    /// <summary>
    /// 树节点 Tag：标记节点对应的设备种类与列表下标（相机/扫码枪等多台设备用 Index 定位）。
    /// </summary>
    internal class NodeTag
    {
        /// <summary>节点种类</summary>
        public NodeKind Kind;

        /// <summary>列表下标（Camera/Scanner 用；单例设备 -1）</summary>
        public int Index;

        public NodeTag(NodeKind kind, int index)
        {
            Kind = kind;
            Index = index;
        }

        public override bool Equals(object obj)
        {
            var t = obj as NodeTag;
            return t != null && t.Kind == Kind && t.Index == Index;
        }

        public override int GetHashCode()
        {
            return (int)Kind * 31 + Index;
        }
    }

    /// <summary>
    /// Kaleidoscope 设备配置编辑器主窗体。
    ///
    /// 【干什么】可视化配置 DeviceHubConfig 并落盘 .kcfg：左侧设备树选择设备 →
    /// 右侧属性网格改参数（中文名/说明来自 Models 的 System.ComponentModel 元数据）→
    /// 底部按设备选"品牌预设"一键填充该品牌默认参数 → 保存前自动校验（错误阻止、警告确认）。
    ///
    /// 【为什么这么设计】
    /// - 界面用 SunnyUI（LayuiGreen 浅绿小清新主题，Program 入口已设全局）：
    ///   窗体继承 UIForm（无系统边框、圆角阴影）、工具条/状态条/品牌预设条用 UIPanel 容器，
    ///   按钮统一 UIButton（文本居中、圆角、悬浮提示），同一行的按钮控件按容器高度垂直居中；
    /// - 不用手写几十个配置表单：PropertyGrid + 模型元数据（DisplayName/Description/Category）
    ///   自动渲染，新增配置字段天然出现在界面上，编辑器代码不用同步改；
    /// - 列表类配置（型号映射/点位表/轮询项/备用通道）用 PropertyGrid 内建集合编辑器，
    ///   双击值即可增删改；
    /// - 多台相机/扫码枪在树里增删，单台设备（气压表/IO/送风机/图像/PLC）固定节点；
    /// - 品牌预设把"换厂商只改配置"落成一次选择，选中即替换该设备整段参数。
    ///
    /// 【怎么用】工具栏"打开"加载现有 .kcfg → 改参数/选品牌 → "保存"（自动校验）；
    /// 也可命令行传文件路径直接打开。产出的文件交给业务项目：
    ///   var cfg = ConfigSerializer.Load(path); hub.ApplyConfig(cfg);
    ///
    /// 【红线】本工具只产配置、不改库、不启停设备——运行仍是业务项目 + DeviceHub 的职责；
    /// 保存前必须过 DeviceHubConfigValidator（Errors 必须修，Warnings 确认后仍可保存）。
    /// </summary>
    public class MainForm : UIForm
    {
        private DeviceHubConfig _cfg;
        private string _filePath = "";
        private bool _dirty;

        // UI 控件
        private UITreeView _tree;
        private PropertyGrid _grid;
        private Panel _toolbar;
        private Panel _statusBar;
        private UILabel _statusLabel;
        private UIButton _btnAddDevice;
        private UIButton _btnDelDevice;
        private Panel _presetPanel;
        private UILabel _presetLabel;
        private UIComboBox _presetCombo;
        private UIButton _presetApply;
        private UILabel _authLabel;
        private UIButton _authBtn;
        private NodeTag _selectedTag;

        /// <summary>本次会话是否已因"HSL 未激活"自动弹过授权输入窗（防反复骚扰；点按钮仍可随时手动打开）。</summary>
        private bool _authPromptedThisSession;

        /// <summary>树节点悬停提示（品牌/协议说明）。【为什么手动驱动】SunnyUI UITreeView 未透传
        /// 原生 ShowNodeToolTips（实测节点 ToolTipText 不弹出），改用 ToolTip 组件 + MouseMove
        /// 手动驱动：跨控件库稳定，节点切换时才更新文本（node 比较防抖，MouseMove 高频不抖）。</summary>
        private readonly ToolTip _treeTip = new ToolTip { InitialDelay = 400, ReshowDelay = 200 };
        private TreeNode _tipNode;

        /// <summary>
        /// 授权检查结果缓存（3 秒 TTL）：EnsureRegistered 在未激活状态下每次都会读授权文件，
        /// 本地 SSD 上微秒级，但为防极端情况（文件被杀软锁定/程序被放到网络盘）拖慢 UI 线程，
        /// 树节点高频切换时用缓存兜底——3 秒内重复选中 HSL 节点不再读文件，
        /// 现场放入 hsl.dat 后最多 3 秒自动识别（通讯侧服务本就独立检测，不受此影响）。
        /// </summary>
        private DateTime _authCheckedAt = DateTime.MinValue;
        private bool _authCheckedResult;

        // ── 小清新配色（LayuiGreen 主题的浅绿扩展）──
        // 注意：SunnyUI 3.9.8 的 UIStyles.SetStyle 只设全局枚举、不刷新颜色缓存，
        // Inherited 控件会一直取默认蓝色——所以关键颜色一律显式指定，不依赖全局主题。
        private static readonly Color Accent = Color.FromArgb(95, 184, 120);       // 主题浅绿（标题栏/主按钮）
        private static readonly Color AccentHover = Color.FromArgb(110, 200, 135); // 主按钮悬浮
        private static readonly Color AccentPress = Color.FromArgb(80, 165, 105);  // 主按钮按下
        private static readonly Color AccentDark = Color.FromArgb(56, 142, 89);      // 深绿（次要按钮文字）
        private static readonly Color SoftBg = Color.FromArgb(232, 246, 236);        // 浅绿底（次要按钮/预设条）
        private static readonly Color SoftHover = Color.FromArgb(214, 236, 220);     // 浅绿悬浮
        private static readonly Color SoftPress = Color.FromArgb(196, 226, 204);     // 浅绿按下
        private static readonly Color SoftBorder = Color.FromArgb(176, 218, 186);    // 浅绿描边
        private static readonly Color BarBg = Color.FromArgb(250, 252, 250);         // 工具条/状态条底色
        private static readonly Color FormBg = Color.FromArgb(244, 247, 244);        // 窗体客户区底色

        /// <summary>构造：支持启动即打开指定配置</summary>
        /// <param name="openFile">可选 .kcfg 文件路径（命令行第一个参数），null=新建默认配置</param>
        public MainForm(string openFile)
        {
            _cfg = new DeviceHubConfig();
            Text = "Kaleidoscope 设备配置编辑器";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1100, 780);
            MinimumSize = new Size(920, 620);
            BackColor = FormBg;
            ShowShadow = true;
            Resizable = true;
            ShowTitleIcon = false; // SunnyUI 的"标题栏图标"开关（与 WinForms ShowIcon 是两回事）
            ShowIcon = false;      // 关键：UIForm.OnPaint 用 WinForms ShowIcon 判断是否 DrawIcon 标题栏图标，
                                   // 默认 true 会把 SunnyUI 内置红色图标画到标题栏左上角（红色小三角），必须关掉
            TitleColor = Accent;   // 标题栏显式浅绿（SetStyle 不生效时的兜底）
            TitleForeColor = Color.White;

            BuildLayout();
            RebuildTree();
            UpdateButtons();

            if (!string.IsNullOrEmpty(openFile) && TryLoad(openFile))
            {
                // 已打开
            }
            else
            {
                _statusLabel.Text = "新建配置（未保存）";
            }
        }

        // ══════════════════════ 布局（代码布局，不依赖 Designer）══════════════════════

        /// <summary>构建全部控件与事件接线</summary>
        private void BuildLayout()
        {
            // ────────── 顶部工具条：一排 UIButton，按条高垂直居中 ──────────
            // （原生 Panel：SunnyUI UIPanel 在窗体级 Dock 正常，但统一用原生容器避免嵌套布局差异）
            _toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = BarBg,
            };
            int y = (_toolbar.Height - 32) / 2; // 按钮统一 32 高，(52-32)/2=10 → 上下留白相等
            int x = 12;

            var btnNew = MakeButton("新建", "新建一份默认配置", NewConfig, true);
            var btnOpen = MakeButton("打开", "打开 .kcfg 配置文件", OpenConfig, true);
            var btnSave = MakeButton("保存", "保存（保存前自动校验）", SaveConfig, true);
            _toolbar.Controls.Add(btnNew);
            _toolbar.Controls.Add(btnOpen);
            _toolbar.Controls.Add(btnSave);
            x = PlaceButton(btnNew, x, y) + 8;
            x = PlaceButton(btnOpen, x, y) + 8;
            x = PlaceButton(btnSave, x, y) + 18; // 组间留更大间距代替分隔线

            var btnValidate = MakeButton("校验", "校验当前配置，列出错误与警告", ValidateConfig);
            var btnExport = MakeButton("导出说明书", "把全部设备配置的字段说明导出成 Markdown 文档（现场参数交接用）", ExportDoc);
            _toolbar.Controls.Add(btnValidate);
            _toolbar.Controls.Add(btnExport);
            x = PlaceButton(btnValidate, x, y) + 8;
            x = PlaceButton(btnExport, x, y) + 18;

            _btnAddDevice = MakeButton("添加设备", "在相机/扫码枪分组下新增一台", AddDevice);
            _btnDelDevice = MakeButton("删除设备", "删除选中的相机/扫码枪", DeleteDevice);
            _toolbar.Controls.Add(_btnAddDevice);
            _toolbar.Controls.Add(_btnDelDevice);
            x = PlaceButton(_btnAddDevice, x, y) + 8;
            x = PlaceButton(_btnDelDevice, x, y) + 8;

            // ────────── 左侧树 + 右侧属性区 ──────────
            _tree = new UITreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                ShowLines = true,
                ItemHeight = 26,
                ShowNodeToolTips = true,   // 节点 ToolTipText 悬停提示（品牌/协议说明，见 RebuildTree）
                FillColor = Color.White,
                SelectedColor = SoftBg,
                SelectedForeColor = AccentDark,
                HoverColor = Color.FromArgb(244, 249, 245),
                LineColor = Color.FromArgb(210, 225, 214),
                ScrollBarColor = Accent,
                ScrollBarRectColor = Accent,
                ScrollBarBackColor = Color.FromArgb(240, 245, 241),
            };
            _tree.AfterSelect += Tree_AfterSelect;

            // 设备树悬停提示：节点带 ToolTipText（品牌/协议说明）则显示，移出/换节点清空。
            // 【为什么手动驱动】SunnyUI UITreeView 未透传原生 ShowNodeToolTips（实测不弹出），
            // 改用 ToolTip 组件 + MouseMove 驱动；node 比较防抖，MouseMove 高频只在切换时更新。
            _tree.MouseMove += (s, e) =>
            {
                var node = _tree.GetNodeAt(e.Location);
                if (node == _tipNode) return;
                _tipNode = node;
                _treeTip.SetToolTip(_tree, node != null ? node.ToolTipText : "");
            };
            _tree.MouseLeave += (s, e) =>
            {
                _tipNode = null;
                _treeTip.SetToolTip(_tree, "");
            };

            // ────────── 底部品牌预设条（选中设备节点才显示）──────────
            // 【原生 Panel】实测 SunnyUI UIPanel 内嵌 UIPanel 时子控件定位会被内部布局机制
            // 偏移（控件整体沉底被裁）——预设条只是浅绿背景容器，用原生 Panel 保证子控件
            // 坐标即所见位置；条内 SunnyUI 控件（按钮/下拉）自绘不受父容器影响。
            _presetPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                BackColor = SoftBg,
            };
            _presetLabel = new UILabel
            {
                Text = "品牌预设：",
                AutoSize = true,
                ForeColor = AccentDark,
            };
            _presetCombo = new UIComboBox
            {
                Width = 240,   // 预留右侧 HSL 授权区空间：最长品牌名下拉里可看全，条上截断无碍
                DropDownStyle = UIDropDownStyle.DropDownList,
                FillColor = Color.White,
                RectColor = SoftBorder,
                ForeColor = AccentDark,
                Radius = 5,
            };
            _presetApply = MakeButton("应用预设", "用选中品牌的默认参数整段替换当前设备配置", ApplyPreset);
            // HSL 授权状态区：仅当选中"多品牌 PLC"或"EIP 扫码枪"节点时可见（见 UpdateForSelection）。
            // 未激活显示红色 ✗ + 按钮，点击弹授权输入窗；激活显示绿色 ✓（按钮仍可点，用于换码核对）。
            _authLabel = new UILabel
            {
                Text = "HSL 授权：…",
                AutoSize = true,
                ForeColor = AccentDark,
                Visible = false,
            };
            _authBtn = MakeButton("输入授权码", "输入/更换 HslCommunication 授权码（保存在本机 hsl.dat 文件）", PromptHslAuth);
            _authBtn.Visible = false;
            _presetPanel.Controls.Add(_presetLabel);
            _presetPanel.Controls.Add(_presetCombo);
            _presetPanel.Controls.Add(_presetApply);
            _presetPanel.Controls.Add(_authLabel);
            _presetPanel.Controls.Add(_authBtn);
            _presetPanel.Visible = false; // 默认隐藏，选中设备节点才显示
            // Dock 布局定型 / 窗体拉伸时重排预设条（初次 BuildLayout 时 Width 未定型，
            // 必须靠这次 Resize 回调把控件摆到正确位置——否则右侧下方区域显示不全）
            _presetPanel.Resize += (s, e) => LayoutPresetBar();
            LayoutPresetBar();

            // 属性网格（原生控件，置于白色 UIPanel 上）：按 Models 的 Category 分组显示
            _grid = new PropertyGrid
            {
                Dock = DockStyle.Fill,
                PropertySort = PropertySort.Categorized,
                HelpVisible = true,
                ToolbarVisible = true,
            };
            _grid.PropertyValueChanged += Grid_PropertyValueChanged;

            // 注意 WinForms Dock 处理顺序：引擎按 Controls 集合【从 index 大到小】依次扣减——
            // 所以 Fill 的属性网格必须先进（index 小、最后处理、填剩余），Bottom 的预设条后进
            // （index 大、先处理、扣出底部 46px）。顺序反了两者会重叠：PropertyGrid 内部的
            // 子窗口（GridView 等）会盖住预设条控件——右侧下方区域显示不全的根因（已实测复现）。
            var rightPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
            };
            rightPanel.Controls.Add(_grid);        // Fill：index 小，最后处理
            rightPanel.Controls.Add(_presetPanel); // Bottom：index 大，先扣出底部

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 300,
                FixedPanel = FixedPanel.Panel1,
                SplitterWidth = 6,
                BackColor = FormBg,
            };
            // 坑：SplitContainer 创建时 Width 还是默认 150（Dock 布局未发生），此时设
            // SplitterDistance=300 会被 clamp 到 150-6-25≈119；之后 Dock=Fill 拉伸到 1100
            // 因 FixedPanel=Panel1 保持该值不放大 → 必须等窗体 Shown（尺寸已定）后再设一次。
            // 左侧树固定 300px：PLC 从站/主站、扫码枪"（串口/TCP）"等带括号长文本
            // 缩进后剩余可用宽度需 ≥ 280，119 会截断括号内文字。
            Shown += (s, e) => split.SplitterDistance = 300;
            split.Panel1.Controls.Add(_tree);
            split.Panel1.BackColor = FormBg;
            split.Panel2.Controls.Add(rightPanel);
            split.Panel2.BackColor = FormBg;

            // ────────── 底部状态条 ──────────
            _statusBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                BackColor = BarBg,
            };
            _statusLabel = new UILabel
            {
                Text = "就绪",
                AutoSize = false,
                ForeColor = Color.FromArgb(90, 100, 92),
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(14, 0),
                Size = new Size(900, 30),
            };
            _statusBar.Controls.Add(_statusLabel);

            // Dock 处理顺序与 rightPanel 同理：引擎按 index 从大到小依次扣减——
            // split(Fill) 必须最先 Add（index 最小、最后处理、填工具条/状态条扣完的剩余）。
            // 【原顺序的隐性 bug】status/toolbar 先 Add、split 后 Add 时，split 会占满整个
            // 客户区，工具条/状态条只是 z-order 恰好盖在它上面（视觉正常）——但 split 内部
            // 全部按全高布局，底部内容（如预设条）被状态条遮挡 30px，右侧下方显示不全。
            Controls.Add(split);
            Controls.Add(_toolbar);
            Controls.Add(_statusBar);
        }

        /// <summary>摆放顶部工具条上的一个按钮，返回按钮右缘 X（供下一控件衔接）</summary>
        private static int PlaceButton(UIButton b, int x, int y)
        {
            b.Location = new Point(x, y);
            return b.Right;
        }

        /// <summary>
        /// 品牌预设条内控件布局：【从左到右顺序流式排列】全部控件（预设区 + HSL 授权区同一条线，
        /// 组间留大间距区分），统一垂直居中。
        /// 【为什么不用"贴右侧锚定"】初次调用时 _presetPanel 还没经过 Dock 布局（Width 是创建
        /// 默认值），算出的右缘坐标是错的；且窗体拉伸不会自动重排——两者都会导致授权区与预设区
        /// 重叠/被截断（右侧下方显示不全的根因）。流式排列最坏情况只是尾部超出被裁一点，
        /// 绝不与左侧控件重叠；配合下方 Resize 联动，任何尺寸下都正确。
        /// </summary>
        private void LayoutPresetBar()
        {
            // 中线固定 23 = 条高 46 的一半。不读 _presetPanel.Height：面板隐藏期间不参与 Dock
            // 布局，属性读数可能是创建时的默认值（曾导致控件整体偏下被状态条裁掉，右侧下方显示不全）
            int cy = 23;
            int px = 14;
            _presetLabel.Location = new Point(px, cy - _presetLabel.Height / 2);
            px = _presetLabel.Right + 6;
            _presetCombo.Location = new Point(px, cy - _presetCombo.Height / 2);
            px = _presetCombo.Right + 10;
            _presetApply.Location = new Point(px, cy - _presetApply.Height / 2);
            // ── HSL 授权区（仅选中多品牌 PLC / EIP 扫码枪节点时可见）──
            px = _presetApply.Right + 22; // 组间距：预设区与授权区视觉分组
            _authLabel.Location = new Point(px, cy - _authLabel.Height / 2);
            px = _authLabel.Right + 8;
            _authBtn.Location = new Point(px, cy - _authBtn.Height / 2);
        }

        /// <summary>
        /// 造一个工具条/预设条按钮：文本水平垂直居中、圆角、按文本实测宽度（保证内容完整显示）；
        /// primary=true 用实心主题绿（主操作），否则浅绿底+深绿字（次要操作）。
        /// </summary>
        /// <param name="text">按钮文本</param>
        /// <param name="tip">悬浮提示文本。注：SunnyUI 3.9.8 的 UIButton 开启 ShowTips 存在绘制 bug——
        /// 会在按钮上画一块红色渐变盖住文字（现场实测 RGB(255,0,0) 覆盖文字行），故暂不启用，
        /// 参数保留供将来升级 SunnyUI 版本后恢复悬浮提示用。</param>
        /// <param name="onClick">点击回调</param>
        /// <param name="primary">true=实心主题绿主按钮</param>
        /// <returns>UIButton 实例（未定位置，由调用方 PlaceButton 摆放）</returns>
        private UIButton MakeButton(string text, string tip, Action onClick, bool primary = false)
        {
            var b = new UIButton
            {
                Text = text,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Radius = 6,
                TabStop = false,
            };
            // UIButton 默认字号 12pt，与窗体默认 9pt 不同——必须用按钮自身字体实测宽度，
            // 否则"打开…/导出说明书…"这类长文本会截断；左右各留 20px（+40）。
            int w = TextRenderer.MeasureText(text, b.Font).Width + 40;
            b.Size = new Size(w, 32);
            if (!primary)
            {
                // 次要按钮：浅绿底 + 深绿字 + 浅绿描边（显式自定义样式）
                b.Style = UIStyle.Custom;
                b.StyleCustomMode = true;
                b.FillColor = SoftBg;
                b.FillHoverColor = SoftHover;
                b.FillPressColor = SoftPress;
                b.ForeColor = AccentDark;
                b.RectColor = SoftBorder;
                b.RectHoverColor = SoftBorder;
                b.RectPressColor = SoftBorder;
            }
            else
            {
                // 主按钮：实心主题浅绿 + 白字（显式自定义样式，不依赖全局 SetStyle）
                b.Style = UIStyle.Custom;
                b.StyleCustomMode = true;
                b.FillColor = Accent;
                b.FillHoverColor = AccentHover;
                b.FillPressColor = AccentPress;
                b.ForeColor = Color.White;
                b.RectColor = Accent;
                b.RectHoverColor = AccentHover;
                b.RectPressColor = AccentPress;
            }
            b.Click += (s, e) => onClick();
            return b;
        }

        // ══════════════════════ 设备树 ══════════════════════

        /// <summary>按当前配置重建整棵树（改动配置后调用）</summary>
        private void RebuildTree()
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();

            var root = new TreeNode("设备配置") { Tag = new NodeTag(NodeKind.Root, -1) };
            _tree.Nodes.Add(root);

            root.Nodes.Add(new TreeNode("全局设置") { Tag = new NodeTag(NodeKind.Global, -1) });

            // PLC 分组：ModBus-TCP（主站/从站）与胡工科技通讯库（HSL 多品牌）两个类别。
            // 各节点 ToolTipText 补充协议/品牌说明（品牌细节不挤占树区宽度，悬停可见）。
            var plcGroup = new TreeNode("PLC") { Tag = new NodeTag(NodeKind.PlcGroup, -1) };
            var modbusGroup = new TreeNode("ModBus-TCP")
            {
                Tag = new NodeTag(NodeKind.PlcModbusGroup, -1),
                ToolTipText = "ModBus-TCP 协议（汇川等标准 PLC 的以太网口）",
            };
            modbusGroup.Nodes.Add(new TreeNode("从站（监听 502，三拍握手）")
            {
                Tag = new NodeTag(NodeKind.PlcSlave, -1),
                ToolTipText = "上位机作 Modbus TCP 从站：监听本机 502 等 PLC 主站来读写（请求-结果-复位三拍握手）",
            });
            modbusGroup.Nodes.Add(new TreeNode("主站（主动读写 + 自动轮询）")
            {
                Tag = new NodeTag(NodeKind.PlcMaster, -1),
                ToolTipText = "上位机作 Modbus TCP 主站：主动连 PLC 读写寄存器，支持后台自动轮询（也可连远程 IO/仪表）",
            });
            plcGroup.Nodes.Add(modbusGroup);
            // 胡工科技通讯库（HSL）分组：当前仅实现主站（上位机主动读写 PLC）；
            // HSL 的 Server 类（上位机模拟 PLC 被读写）暂无业务场景未抽取，未来需要时在此挂"从站"节点
            var hslGroup = new TreeNode("胡工科技通讯库")
            {
                Tag = new NodeTag(NodeKind.PlcHslGroup, -1),
                ToolTipText = "HslCommunication 商业通讯库（V1.6.0 集成）\r\n" +
                              "当前提供主站能力；从站（Server 模拟 PLC）暂未接入",
            };
            hslGroup.Nodes.Add(new TreeNode("主站（多品牌协议）")
            {
                Tag = new NodeTag(NodeKind.PlcMultiBrand, -1),
                ToolTipText = "上位机经品牌协议主动读写 PLC：三菱 MC/A1E、欧姆龙 FINS/CIP、西门子 S7、汇川 H5U",
            });
            plcGroup.Nodes.Add(hslGroup);
            root.Nodes.Add(plcGroup);

            var camGroup = new TreeNode("相机") { Tag = new NodeTag(NodeKind.CameraGroup, -1) };
            for (int i = 0; i < _cfg.Cameras.Count; i++)
            {
                string name = _cfg.Cameras[i] == null ? "" : _cfg.Cameras[i].Name;
                camGroup.Nodes.Add(new TreeNode("相机 " + (i + 1) + (string.IsNullOrEmpty(name) ? "" : "：" + name))
                {
                    Tag = new NodeTag(NodeKind.Camera, i),
                });
            }
            root.Nodes.Add(camGroup);

            var scanGroup = new TreeNode("扫码枪") { Tag = new NodeTag(NodeKind.ScannerGroup, -1) };
            for (int i = 0; i < _cfg.Scanners.Count; i++)
            {
                string mode = ScannerModeLabel(_cfg.Scanners[i]);
                scanGroup.Nodes.Add(new TreeNode("扫码枪 " + (i + 1) + "（" + mode + "）")
                {
                    Tag = new NodeTag(NodeKind.Scanner, i),
                });
            }
            root.Nodes.Add(scanGroup);

            root.Nodes.Add(new TreeNode("气压表") { Tag = new NodeTag(NodeKind.Barometer, -1) });
            root.Nodes.Add(new TreeNode("IO 耦合器") { Tag = new NodeTag(NodeKind.Io, -1) });
            root.Nodes.Add(new TreeNode("送风机") { Tag = new NodeTag(NodeKind.Fan, -1) });
            root.Nodes.Add(new TreeNode("图像存储") { Tag = new NodeTag(NodeKind.Image, -1) });

            _tree.ExpandAll();
            _tree.EndUpdate();

            // 保持原选中（如果还存在）
            if (_selectedTag != null) SelectNode(_selectedTag);
        }

        /// <summary>扫码枪通讯方式显示标签（树节点用）：Tcp→"TCP"、Eip→"EIP"，其余按串口兜底。</summary>
        private static string ScannerModeLabel(ScanConfig s)
        {
            if (s == null) return "串口";
            if (string.Equals(s.Mode, "Tcp", StringComparison.OrdinalIgnoreCase)) return "TCP";
            if (string.Equals(s.Mode, "Eip", StringComparison.OrdinalIgnoreCase)) return "EIP";
            return "串口";
        }

        /// <summary>按 Tag 选中树节点（不存在则忽略）</summary>
        private void SelectNode(NodeTag tag)
        {
            TreeNode found = FindNode(_tree.Nodes, tag);
            if (found != null)
            {
                _tree.SelectedNode = found;
                found.EnsureVisible();
            }
        }

        /// <summary>递归找 Tag 匹配的节点</summary>
        private static TreeNode FindNode(TreeNodeCollection nodes, NodeTag tag)
        {
            foreach (TreeNode n in nodes)
            {
                if (tag.Equals(n.Tag)) return n;
                TreeNode hit = FindNode(n.Nodes, tag);
                if (hit != null) return hit;
            }
            return null;
        }

        // ══════════════════════ 选择联动 ══════════════════════

        /// <summary>树节点被选中：切换属性网格对象 + 品牌预设下拉</summary>
        private void Tree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            _selectedTag = e.Node.Tag as NodeTag;
            UpdateForSelection();
        }

        /// <summary>根据当前选中节点刷新右侧属性区与按钮状态</summary>
        private void UpdateForSelection()
        {
            _grid.SelectedObject = ObjectFor(_selectedTag);
            UpdateBrandCombo(_selectedTag);
            UpdateHslAuthArea(_selectedTag);
            UpdateButtons();
        }

        /// <summary>
        /// 当前节点是否涉及 HslCommunication 通讯（多品牌 PLC 节点，或 Mode=Eip 的扫码枪节点）。
        /// 涉及才显示"HSL 授权"状态区，并在未激活时引导输入。
        /// </summary>
        private bool IsHslNode(NodeTag tag)
        {
            if (tag == null) return false;
            if (tag.Kind == NodeKind.PlcMultiBrand) return true;
            if (tag.Kind == NodeKind.Scanner && tag.Index >= 0 && tag.Index < _cfg.Scanners.Count)
            {
                ScanConfig s = _cfg.Scanners[tag.Index];
                return s != null && string.Equals(s.Mode, "Eip", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>
        /// 刷新底部"HSL 授权"状态区：仅 HSL 相关节点显示。
        /// 【自动弹窗】首次切到 HSL 节点且本地从未激活（文件没码或码无效）时，自动弹一次
        /// 授权输入窗；用户取消后本次会话不再骚扰（预设条上的按钮随时可手动打开）。
        /// </summary>
        private void UpdateHslAuthArea(NodeTag tag)
        {
            bool hslNode = IsHslNode(tag);
            _authLabel.Visible = hslNode;
            _authBtn.Visible = hslNode;
            if (!hslNode) return;

            // 3 秒 TTL 缓存：高频切节点时不重复做文件 IO（UI 线程防卡顿，见字段注释）
            bool ok;
            if ((DateTime.Now - _authCheckedAt).TotalSeconds < 3)
            {
                ok = _authCheckedResult;
            }
            else
            {
                ok = HslAuthorization.EnsureRegistered(); // 幂等：已激活走内存快路径零 IO
                _authCheckedAt = DateTime.Now;
                _authCheckedResult = ok;
            }
            RefreshAuthUi(ok);

            if (!ok && !_authPromptedThisSession)
            {
                _authPromptedThisSession = true; // 无论用户输不输，本会话只自动弹这一次
                PromptHslAuth();
            }
        }

        /// <summary>按激活状态刷新授权标签文字与颜色（文本保持短小：预设条一行要放下两个功能区）。</summary>
        private void RefreshAuthUi(bool activated)
        {
            if (activated)
            {
                _authLabel.Text = "HSL 授权：✓ 有效";
                _authLabel.ForeColor = Color.FromArgb(46, 158, 107);
                _authBtn.Text = "查看/更换码";
            }
            else
            {
                _authLabel.Text = "HSL 授权：✗ 未激活";
                _authLabel.ForeColor = Color.FromArgb(200, 80, 80);
                _authBtn.Text = "输入授权码";
            }
            LayoutPresetBar(); // 按钮文字宽度变了，重排一次
        }

        /// <summary>弹出授权码输入窗：确定（验签通过并落盘）后刷新状态区与状态栏提示。</summary>
        private void PromptHslAuth()
        {
            using (var dlg = new HslAuthDialog(HslAuthorization.IsActivated))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    // 同步刷新 TTL 缓存：否则 3 秒内切换节点会按旧缓存显示"未激活"闪回
                    bool ok = HslAuthorization.IsActivated;
                    _authCheckedAt = DateTime.Now;
                    _authCheckedResult = ok;
                    RefreshAuthUi(ok);
                    AppendStatus("HSL 授权已保存：" + HslAuthorization.AuthFilePath);
                }
            }
        }

        /// <summary>取当前选中节点对应的配置对象（无对象的导航节点返回 null）</summary>
        private object ObjectFor(NodeTag tag)
        {
            if (tag == null) return null;
            switch (tag.Kind)
            {
                case NodeKind.Global: return _cfg;
                case NodeKind.PlcSlave: return _cfg.Plc;
                case NodeKind.PlcMaster: return _cfg.PlcMaster;
                case NodeKind.PlcMultiBrand: return _cfg.MultiBrandPlc;
                case NodeKind.Camera: return _cfg.Cameras[tag.Index];
                case NodeKind.Scanner: return _cfg.Scanners[tag.Index];
                case NodeKind.Barometer: return _cfg.Barometer;
                case NodeKind.Io: return _cfg.Io;
                case NodeKind.Fan: return _cfg.Fan;
                case NodeKind.Image: return _cfg.Image;
                default: return null;
            }
        }

        /// <summary>刷新品牌预设下拉：按设备种类载入可选品牌，无品牌的节点隐藏预设区</summary>
        private void UpdateBrandCombo(NodeTag tag)
        {
            _presetCombo.Items.Clear();
            if (tag == null)
            {
                _presetPanel.Visible = false;
                return;
            }
            List<BrandPreset> presets = BrandPresets.For(tag.Kind);
            if (presets.Count == 0)
            {
                _presetPanel.Visible = false;
                return;
            }
            _presetPanel.Visible = true;
            foreach (BrandPreset p in presets) _presetCombo.Items.Add(p);
            _presetCombo.SelectedIndex = 0;
            // 【必须显示后重排】面板隐藏期间不参与 Dock 布局，BuildLayout 早期的控件定位
            // 是错的（Resize 补偿在部分时序下不触发）——每次显示都强制摆一次才保证可见区域正确
            LayoutPresetBar();
        }

        /// <summary>刷新工具栏按钮可用性（添加/删除只在分组/叶子节点生效）</summary>
        private void UpdateButtons()
        {
            bool group = _selectedTag != null
                && (_selectedTag.Kind == NodeKind.CameraGroup || _selectedTag.Kind == NodeKind.ScannerGroup);
            bool leaf = _selectedTag != null
                && (_selectedTag.Kind == NodeKind.Camera || _selectedTag.Kind == NodeKind.Scanner);
            _btnAddDevice.Enabled = group;
            _btnDelDevice.Enabled = leaf;
        }

        // ══════════════════════ 品牌预设 ══════════════════════

        /// <summary>应用品牌预设：用该品牌默认实例整段替换当前设备的配置对象</summary>
        private void ApplyPreset()
        {
            if (_selectedTag == null) return;
            var preset = _presetCombo.SelectedItem as BrandPreset;
            if (preset == null) return;

            object newObj = preset.CreateDefault();
            ReplaceObject(_selectedTag, newObj);

            // 刷新树 + 重新选中同一节点，属性网格绑定新对象
            RebuildTree();
            SelectNode(_selectedTag);
            UpdateForSelection();
            _dirty = true;
            AppendStatus("已应用品牌预设：" + preset.Name + "（" + preset.Description + "）。记得保存。");
        }

        /// <summary>把新对象放回当前配置的对应位置（按节点种类）</summary>
        private void ReplaceObject(NodeTag tag, object obj)
        {
            switch (tag.Kind)
            {
                case NodeKind.PlcSlave: _cfg.Plc = (PlcConfig)obj; break;
                case NodeKind.PlcMaster: _cfg.PlcMaster = (PlcMasterConfig)obj; break;
                case NodeKind.PlcMultiBrand: _cfg.MultiBrandPlc = (MultiBrandPlcConfig)obj; break;
                case NodeKind.Camera: _cfg.Cameras[tag.Index] = (CameraConfig)obj; break;
                case NodeKind.Scanner: _cfg.Scanners[tag.Index] = (ScanConfig)obj; break;
                case NodeKind.Barometer: _cfg.Barometer = (BarometerConfig)obj; break;
                case NodeKind.Io: _cfg.Io = (IoConfig)obj; break;
                case NodeKind.Fan: _cfg.Fan = (FanConfig)obj; break;
                case NodeKind.Image: _cfg.Image = (ImageConfig)obj; break;
            }
        }

        // ══════════════════════ 工具栏动作 ══════════════════════

        /// <summary>新建默认配置</summary>
        private void NewConfig()
        {
            if (_dirty
                && MessageBox.Show(this, "当前配置尚未保存，新建将丢弃所有修改。继续？", "新建",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }
            _cfg = new DeviceHubConfig();
            _filePath = "";
            _dirty = false;
            _selectedTag = null;
            RebuildTree();
            UpdateForSelection();
            AppendStatus("新建配置（未保存）");
        }

        /// <summary>打开配置文件</summary>
        private void OpenConfig()
        {
            if (_dirty
                && MessageBox.Show(this, "当前配置尚未保存，打开将丢弃所有修改。继续？", "打开",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }
            var dlg = new OpenFileDialog
            {
                Title = "打开 Kaleidoscope 设备配置",
                Filter = "Kaleidoscope 配置 (*.kcfg;*.json)|*.kcfg;*.json|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            TryLoad(dlg.FileName);
        }

        /// <summary>尝试加载配置文件（成功返回 true 并刷新界面）</summary>
        private bool TryLoad(string path)
        {
            try
            {
                _cfg = ConfigSerializer.Load(path);
                _filePath = path;
                _dirty = false;
                _selectedTag = null;
                RebuildTree();
                UpdateForSelection();
                AppendStatus("已打开：" + path);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "加载配置失败：" + ex.Message, "打开",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        /// <summary>保存：先校验（错误阻止、警告确认）再落盘</summary>
        private void SaveConfig()
        {
            ConfigValidationResult r = DeviceHubConfigValidator.Validate(_cfg);
            if (r.Errors.Count > 0)
            {
                MessageBox.Show(this,
                    "配置校验未通过，请先修复以下错误后再保存：\r\n\r\n" + string.Join("\r\n", r.Errors),
                    "保存被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (r.Warnings.Count > 0
                && MessageBox.Show(this,
                    "配置存在以下警告，仍要保存吗？\r\n\r\n" + string.Join("\r\n", r.Warnings),
                    "确认保存", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            string path = _filePath;
            if (string.IsNullOrEmpty(path))
            {
                var dlg = new SaveFileDialog
                {
                    Title = "保存 Kaleidoscope 设备配置",
                    Filter = "Kaleidoscope 配置 (*.kcfg)|*.kcfg|JSON (*.json)|*.json|所有文件 (*.*)|*.*",
                    FileName = "devices.kcfg",
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }

            try
            {
                ConfigSerializer.Save(_cfg, path);
                _filePath = path;
                _dirty = false;
                AppendStatus("已保存：" + path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "保存",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>校验当前配置并弹窗展示结果</summary>
        private void ValidateConfig()
        {
            ConfigValidationResult r = DeviceHubConfigValidator.Validate(_cfg);
            if (r.IsValid && r.Warnings.Count == 0)
            {
                MessageBox.Show(this, "配置校验通过，没有错误也没有警告。", "校验",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var lines = new List<string>();
            lines.Add("校验结果：");
            if (r.Errors.Count > 0) lines.Add("错误（必须修）：");
            lines.AddRange(r.Errors);
            if (r.Warnings.Count > 0) lines.Add("");
            if (r.Warnings.Count > 0) lines.Add("警告（建议修）：");
            lines.AddRange(r.Warnings);
            MessageBox.Show(this, string.Join("\r\n", lines), "校验",
                MessageBoxButtons.OK, r.IsValid ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
        }

        /// <summary>导出配置字段说明书（Markdown）：字段中文名/类型/默认值/说明，现场参数交接用</summary>
        private void ExportDoc()
        {
            var dlg = new SaveFileDialog
            {
                Title = "导出设备配置说明书",
                Filter = "Markdown (*.md)|*.md|所有文件 (*.*)|*.*",
                FileName = "Kaleidoscope设备配置说明书.md",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                // 导出的是"字段语义说明书"（含默认值），不依赖当前编辑值；
                // 库新增配置字段后重新导出即可，文档自动跟上。
                DeviceDescriptionExporter.ExportToMarkdownFile(dlg.FileName);
                MessageBox.Show(this, "说明书已导出：" + dlg.FileName, "导出说明书",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败：" + ex.Message, "导出说明书",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>添加相机/扫码枪（只在对应分组节点选中时可用）</summary>
        private void AddDevice()
        {
            if (_selectedTag == null) return;
            if (_selectedTag.Kind == NodeKind.CameraGroup)
            {
                _cfg.Cameras.Add(new CameraConfig());
                _dirty = true;
                RebuildTree();
                SelectNode(new NodeTag(NodeKind.Camera, _cfg.Cameras.Count - 1));
                UpdateForSelection();
                AppendStatus("已添加一台相机，请配置 IP/通道后保存。");
            }
            else if (_selectedTag.Kind == NodeKind.ScannerGroup)
            {
                _cfg.Scanners.Add(new ScanConfig());
                _dirty = true;
                RebuildTree();
                SelectNode(new NodeTag(NodeKind.Scanner, _cfg.Scanners.Count - 1));
                UpdateForSelection();
                AppendStatus("已添加一把扫码枪，请配置通讯方式后保存。");
            }
        }

        /// <summary>删除选中的相机/扫码枪</summary>
        private void DeleteDevice()
        {
            if (_selectedTag == null) return;
            if (_selectedTag.Kind == NodeKind.Camera)
            {
                _cfg.Cameras.RemoveAt(_selectedTag.Index);
                _dirty = true;
                _selectedTag = null;
                RebuildTree();
                UpdateForSelection();
                AppendStatus("已删除相机。");
            }
            else if (_selectedTag.Kind == NodeKind.Scanner)
            {
                _cfg.Scanners.RemoveAt(_selectedTag.Index);
                _dirty = true;
                _selectedTag = null;
                RebuildTree();
                UpdateForSelection();
                AppendStatus("已删除扫码枪。");
            }
        }

        // ══════════════════════ 其它 ══════════════════════

        /// <summary>属性网格任何值被改 → 标记未保存</summary>
        private void Grid_PropertyValueChanged(object s, PropertyValueChangedEventArgs e)
        {
            _dirty = true;
            AppendStatus("已修改（未保存）。");
        }

        /// <summary>状态栏提示</summary>
        private void AppendStatus(string text)
        {
            _statusLabel.Text = text;
        }

        /// <summary>关窗：未保存的修改给一次确认</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dirty
                && MessageBox.Show(this, "配置尚未保存，退出将丢失修改。确定退出？", "退出",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }
    }
}