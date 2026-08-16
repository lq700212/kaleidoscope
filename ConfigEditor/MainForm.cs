using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Kaleidoscope.Configuration;
using Kaleidoscope.Models;

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
    public class MainForm : Form
    {
        private DeviceHubConfig _cfg;
        private string _filePath = "";
        private bool _dirty;

        // UI 控件
        private TreeView _tree;
        private PropertyGrid _grid;
        private ToolStrip _toolbar;
        private StatusStrip _status;
        private ToolStripStatusLabel _statusLabel;
        private ToolStripButton _btnAddDevice;
        private ToolStripButton _btnDelDevice;
        private Panel _presetPanel;
        private Label _presetLabel;
        private ComboBox _presetCombo;
        private Button _presetApply;
        private NodeTag _selectedTag;

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
            // 顶部工具栏
            _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 0, 0, 0) };
            _toolbar.Items.Add(MakeButton("新建", "新建一份默认配置", NewConfig));
            _toolbar.Items.Add(MakeButton("打开…", "打开 .kcfg 配置文件", OpenConfig));
            _toolbar.Items.Add(MakeButton("保存", "保存（保存前自动校验）", SaveConfig));
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(MakeButton("校验", "校验当前配置，列出错误与警告", ValidateConfig));
            _toolbar.Items.Add(MakeButton("导出说明书…", "把全部设备配置的字段说明导出成 Markdown 文档（现场参数交接用）", ExportDoc));
            _toolbar.Items.Add(new ToolStripSeparator());
            _btnAddDevice = MakeButton("添加设备", "在相机/扫码枪分组下新增一台", AddDevice);
            _btnDelDevice = MakeButton("删除设备", "删除选中的相机/扫码枪", DeleteDevice);
            _toolbar.Items.Add(_btnAddDevice);
            _toolbar.Items.Add(_btnDelDevice);

            // 左侧树 + 右侧属性区
            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                ShowLines = true,
            };
            _tree.AfterSelect += Tree_AfterSelect;

            // 底部品牌预设条
            _presetLabel = new Label
            {
                Text = "品牌预设：",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
            };
            _presetCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 300,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
            };
            _presetApply = new Button
            {
                Text = "应用预设",
                Width = 90,
                Anchor = AnchorStyles.Right,
            };
            _presetApply.Click += PresetApply_Click;
            _presetPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                Padding = new Padding(6, 8, 6, 6),
            };
            _presetLabel.Location = new Point(6, 11);
            _presetCombo.Location = new Point(90, 8);
            _presetApply.Location = new Point(_presetCombo.Right + 8, 7);
            _presetPanel.Controls.Add(_presetLabel);
            _presetPanel.Controls.Add(_presetCombo);
            _presetPanel.Controls.Add(_presetApply);
            _presetPanel.Visible = false; // 默认隐藏，选中设备节点才显示

            _grid = new PropertyGrid
            {
                Dock = DockStyle.Fill,
                PropertySort = PropertySort.Categorized, // 按 Models 的 Category 分组显示
                HelpVisible = true,
                ToolbarVisible = true,
            };
            _grid.PropertyValueChanged += Grid_PropertyValueChanged;

            // 注意 WinForms Dock 布局按添加顺序：Bottom 的先占位、Fill 的后填剩余，
            // 顺序反了 Fill 会盖住 Bottom（先 add 的控件后布局，后 add 的覆盖在先 add 之上）。
            var rightPanel = new Panel { Dock = DockStyle.Fill };
            rightPanel.Controls.Add(_presetPanel);
            rightPanel.Controls.Add(_grid);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 240,
                FixedPanel = FixedPanel.Panel1,
            };
            split.Panel1.Controls.Add(_tree);
            split.Panel2.Controls.Add(rightPanel);

            // 底部状态栏
            _status = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel("就绪");
            _status.Items.Add(_statusLabel);

            // 同样的 Dock 顺序：Top/Bottom 先占位，Fill 最后填剩余
            Controls.Add(_status);
            Controls.Add(_toolbar);
            Controls.Add(split);
            _toolbar.Dock = DockStyle.Top;
            _status.Dock = DockStyle.Bottom;
        }

        /// <summary>造一个工具栏按钮（统一事件接线；Action 便于直接绑无参方法）</summary>
        private static ToolStripButton MakeButton(string text, string tip, Action onClick)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip, AutoSize = false, Width = 76 };
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

            var plcGroup = new TreeNode("PLC") { Tag = new NodeTag(NodeKind.PlcGroup, -1) };
            plcGroup.Nodes.Add(new TreeNode("从站（监听 502，三拍握手）") { Tag = new NodeTag(NodeKind.PlcSlave, -1) });
            plcGroup.Nodes.Add(new TreeNode("主站（主动读写 + 轮询）") { Tag = new NodeTag(NodeKind.PlcMaster, -1) });
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
                bool tcp = _cfg.Scanners[i] != null
                    && string.Equals(_cfg.Scanners[i].Mode, "Tcp", StringComparison.OrdinalIgnoreCase);
                string mode = tcp ? "TCP" : "串口";
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
            UpdateButtons();
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
        private void PresetApply_Click(object sender, EventArgs e)
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