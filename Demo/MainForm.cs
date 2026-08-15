using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CommonLib.Models;
using CommonLib.Services;
using CommonLib.Utils;

namespace CommonLibDemo
{
    /// <summary>
    /// CommonLib Demo 主窗体：现场前期"界面还没做"时，用它手动验证 通讯 + 存图 全链路。
    ///
    /// 【本窗体演示的就是 CommonLib 的标准接入方式（与 README/使用说明完全一致）】
    ///   var hub = new DeviceHub(config);  // ① 建
    ///   hub.Start();                      // ② 启
    ///   hub.ApplyConfig(newCfg);          // ③ 热更
    ///   hub.Dispose();                    // ④ 关
    /// 业务测试全部走 hub 暴露的服务实例（hub.Plc / hub.Cameras / hub.Scanners / hub.ImageStore），
    /// 不在本窗体新建任何 TcpClient/串口连接——这正是库要求业务层遵守的"连接复用"红线。
    ///
    /// 【界面布局】
    /// ┌──────────────────────────────────────────────────────────────────────────┐
    /// │ ▓ CommonLib 通讯/存图 测试台                                      [日志区] │
    /// ├──────────────────────────────────────────────┬───────────────────────────┤
    /// │【配置】型号:┌cmbModel─────┐  [btnLoad 加载配置] │ 连接状态:               │
    /// │  PLC:┌txtPlcIp────────┐:┌txtPlcPort┐          │  ● PLC   ● 相机   ● 扫码枪│
    /// │  [btnApply 应用配置(热更)] [btnSave 保存配置]  │                          │
    /// ├──────────────────────────────────────────────┤  日志:┌txtLog────────────┐│
    /// │【相机】┌cmbCamera─────────┐ [lblCamState]      │       │(多行只读滚动)   ││
    /// │  [btnT1 仅触发T1] [btnT2 触发+判定+取图存图]   │       │                  ││
    /// │  判定:[lblCamResult]（OK绿/NG红/失败灰）       │       │                  ││
    /// │  程序:[btnReadProg 读当前程序][btnSwProg 切P001]│      └──────────────────┘│
    /// ├──────────────────────────────────────────────┤ [picPreview 图片预览]     │
    /// │【扫码枪】┌cmbScanner─────────┐ [lblScannerState]│  最近存图:[lblSavedPath]│
    /// │  [btnScannerTrigger 发触发指令]               │                          │
    /// │  最近条码:[lblScannerCode 大字]               │                          │
    /// ├──────────────────────────────────────────────┤                          │
    /// │【PLC 读写】                                   │                          │
    /// │  请求:[btnReadScanReq 读扫码请求][btnReadCamReq 读相机请求]               │
    /// │        值:[lblMoveVal]                       │                          │
    /// │  扫码结果:[btnScan0 复位][btnScan1 OK][btnScan2 NG]                      │
    /// │  相机结果:[btnCam1 相机OK][btnCam2 相机NG][btnCam0 相机复位]             │
    /// │  型号:[btnWriteModel 写产品型号]┌txtModel─────┐                          │
    /// │  任意寄存器:读[┌txtReadAddr┐][btnReadReg] →[txtReadVal]                  │
    /// │             写[┌txtWriteAddr┐][┌txtWriteVal┐][btnWriteReg]              │
    /// ├──────────────────────────────────────────────┤                          │
    /// │【存图测试】[btnTestSave 生成测试图并存图]                                 │
    /// └──────────────────────────────────────────────┴──────────────────────────┘
    ///
    /// 【线程（红线）】所有网络/文件 IO（触发/读写寄存器/取图/存图）一律 Task.Run 后台线程，
    /// 完成后 SafeInvoke 回 UI 线程更新控件；扫码枪/连接状态事件本身在工作线程触发，
    /// 响应也统一 SafeInvoke。绝不在 UI 线程同步读写设备。
    /// </summary>
    public partial class MainForm : Form
    {
        // ────── CommonLib 核心：DeviceHub 门面（Demo 全程只持有它）──────
        private DeviceHub _hub;

        // Demo 自身配置（含 DeviceHubConfig 内嵌）
        private DemoConfig _cfg;

        // 防连点/并发触发（跨线程读，UI 线程写）
        private volatile bool _busy;

        // 最近扫到的条码（存图 {SN} 目录用）
        private string _currentSerial = "";

        // ────── 界面控件（代码布局，全部字段化，便于事件引用）──────
        private ComboBox cmbModel;
        private TextBox txtPlcIp, txtPlcPort;
        private ComboBox cmbCamera;
        private Label lblCamState;
        private Label lblCamResult;
        private Label lblCurProgram;
        private ComboBox cmbScanner;
        private Label lblScannerState;
        private Label lblScannerCode;
        private Label lblMoveVal;
        private TextBox txtModel, txtReadAddr, txtReadVal, txtWriteAddr, txtWriteVal;
        private PictureBox picPreview;
        private Label lblSavedPath;
        private TextBox txtLog;
        private Label lblPlcState, lblCamAllState, lblScannerAllState;

        // 忙碌时要禁用的按钮（会发起网络操作）
        private readonly List<Control> _busyControls = new List<Control>();

        public MainForm()
        {
            // Demo 配置加载（文件不存在用默认；含 PLC/相机/扫码枪/存图默认参数）
            _cfg = DemoConfig.Load();
            if (_cfg.Devices == null) _cfg.Devices = new DeviceHubConfig();
            // 型号候选预置（与库默认一致）
            if (_cfg.Devices.ProductModels == null || _cfg.Devices.ProductModels.Count == 0)
                _cfg.Devices.ProductModels = DeviceHubConfig.DefaultProductModels();

            BuildLayout();          // 代码布局（控件树 + ASCII 注释图见类头）
            WireEvents();           // 界面事件接线

            // ① 建 + ② 启：DeviceHub 标准接入（惰性连接，首次读写才真正连网）
            RebuildHub();

            RefreshStates();        // 初始按当前连接状态上色
            AppendLog("CommonLib 测试台已启动，复用 DeviceHub 统一管理全部连接。");
            AppendLog($"PLC={_hub.Plc?.IpLabel ?? "未配置"}，相机数={_hub.Cameras.Count}，扫码枪数={_hub.Scanners.Count}");
        }

        // ══════════════════════ 界面布局（代码布局，不依赖 Designer）══════════════════════

        private void BuildLayout()
        {
            Text = "CommonLib 通讯/存图 测试台";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1160, 820);
            MinimumSize = new Size(1000, 700);

            // 用 TableLayoutPanel 分两栏：左侧 7 成操作区，右侧 3 成状态/预览/日志
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(8),
                ColumnStyles = {
                    new ColumnStyle(SizeType.Percent, 100F),
                    new ColumnStyle(SizeType.Absolute, 400F)
                }
            };
            Controls.Add(root);

            // 左栏：纵向堆叠 GroupBox（配置/相机/扫码枪/PLC/存图）
            var left = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            root.Controls.Add(left, 0, 0);
            int y = 0;

            // ── 配置区 ──
            var gCfg = MakeGroup("配置（现场联调参数）", 8, y, 730, 128);
            left.Controls.Add(gCfg);
            int cfgY = 26;
            gCfg.Controls.Add(MakeLabel("型号:", 12, cfgY));
            cmbModel = MakeCombo(70, cfgY - 3, 160);
            cmbModel.Items.AddRange(_cfg.Devices.ProductModels.ToArray());
            // 选中当前型号；不在候选则显示型号文本
            if (_cfg.Devices.ProductModels.Contains(_cfg.ProductModel)) cmbModel.SelectedItem = _cfg.ProductModel;
            else { cmbModel.Items.Add(_cfg.ProductModel); cmbModel.SelectedItem = _cfg.ProductModel; }
            gCfg.Controls.Add(cmbModel);
            gCfg.Controls.Add(MakeButton("应用配置（热更）", 240, cfgY - 5, 130, BtnApplyConfig_Click));
            gCfg.Controls.Add(MakeButton("保存配置", 380, cfgY - 5, 100, BtnSaveConfig_Click));
            gCfg.Controls.Add(MakeButton("加载配置", 490, cfgY - 5, 100, BtnLoadConfig_Click));
            cfgY += 34;
            gCfg.Controls.Add(MakeLabel("PLC:", 12, cfgY));
            txtPlcIp = MakeText(70, cfgY - 3, 200);
            txtPlcIp.Text = _cfg.Devices.Plc?.IpAddress ?? "0.0.0.0";
            gCfg.Controls.Add(txtPlcIp);
            gCfg.Controls.Add(MakeLabel(":端口", 276, cfgY));
            txtPlcPort = MakeText(320, cfgY - 3, 60);
            txtPlcPort.Text = (_cfg.Devices.Plc?.Port ?? 502).ToString();
            gCfg.Controls.Add(txtPlcPort);
            gCfg.Controls.Add(MakeLabel("从站监听本机，PLC 主站连到这里", 400, cfgY + 2));
            cfgY += 34;
            gCfg.Controls.Add(MakeLabel("地址说明:PLC 用 DataStore 索引（协议号=索引+40000）;相机结果/请求地址在相机表配", 12, cfgY));
            y += 128;

            // ── 相机区 ──
            var gCam = MakeGroup("相机（基恩士 IV4）", 8, y, 730, 150);
            left.Controls.Add(gCam);
            gCam.Controls.Add(MakeLabel("相机:", 12, 26));
            cmbCamera = MakeCombo(70, 23, 260);
            gCam.Controls.Add(cmbCamera);
            lblCamState = MakeLabel("○ 断连", 340, 26);
            lblCamState.ForeColor = Color.Red;
            gCam.Controls.Add(lblCamState);
            gCam.Controls.Add(MakeButton("T1 仅触发", 12, 56, 130, BtnT1_Click));
            gCam.Controls.Add(MakeButton("T2 触发+判定+取图存图", 152, 56, 190, BtnT2_Click));
            gCam.Controls.Add(MakeButton("读当前程序", 352, 56, 130, BtnReadProgram_Click));
            gCam.Controls.Add(MakeButton("切 P001", 492, 56, 100, BtnSwProgram_Click));
            lblCamResult = MakeLabel("判定: -", 12, 92);
            lblCamResult.AutoEllipsis = true;
            lblCamResult.Width = 420;
            lblCamResult.ForeColor = Color.Gray;
            gCam.Controls.Add(lblCamResult);
            lblCurProgram = MakeLabel("当前程序: -", 12, 118);
            gCam.Controls.Add(lblCurProgram);
            y += 150;

            // ── 扫码枪区 ──
            var gScanner = MakeGroup("扫码枪（基恩士 SR）", 8, y, 730, 130);
            left.Controls.Add(gScanner);
            gScanner.Controls.Add(MakeLabel("扫码枪:", 12, 26));
            cmbScanner = MakeCombo(70, 23, 260);
            gScanner.Controls.Add(cmbScanner);
            lblScannerState = MakeLabel("○ 断连", 340, 26);
            lblScannerState.ForeColor = Color.Red;
            gScanner.Controls.Add(lblScannerState);
            gScanner.Controls.Add(MakeButton("发送触发指令", 430, 22, 130, BtnScannerTrigger_Click));
            gScanner.Controls.Add(MakeLabel("最近条码:", 12, 62));
            lblScannerCode = MakeLabel("（等待扫码…）", 90, 62);
            lblScannerCode.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
            lblScannerCode.Width = 480;
            lblScannerCode.AutoEllipsis = true;
            lblScannerCode.ForeColor = Color.FromArgb(46, 158, 107);
            gScanner.Controls.Add(lblScannerCode);
            gScanner.Controls.Add(MakeLabel("提示:TCP 模式连上后需发触发指令(LON)才开始读码;串口上电即读码", 12, 98));
            y += 130;

            // ── PLC 读写区 ──
            var gPlc = MakeGroup("PLC 读写（三拍握手：请求→结果→复位）", 8, y, 730, 220);
            left.Controls.Add(gPlc);
            gPlc.Controls.Add(MakeLabel("请求:", 12, 26));
            gPlc.Controls.Add(MakeButton("读扫码请求(40001)", 70, 22, 160, BtnReadScanReq_Click));
            gPlc.Controls.Add(MakeButton("读相机请求", 240, 22, 130, BtnReadCamReq_Click));
            lblMoveVal = MakeLabel("值: -", 380, 26);
            lblMoveVal.AutoEllipsis = true;
            lblMoveVal.Width = 280;
            gPlc.Controls.Add(lblMoveVal);
            gPlc.Controls.Add(MakeLabel("扫码结果(40004):", 12, 62));
            gPlc.Controls.Add(MakeButton("复位 0", 140, 58, 90, (s, e) => WriteScanRes(0)));
            gPlc.Controls.Add(MakeButton("OK 1", 240, 58, 90, (s, e) => WriteScanRes(1)));
            gPlc.Controls.Add(MakeButton("NG 2", 340, 58, 90, (s, e) => WriteScanRes(2)));
            gPlc.Controls.Add(MakeLabel("相机结果:", 12, 98));
            gPlc.Controls.Add(MakeButton("相机 OK 1", 90, 94, 110, (s, e) => WriteCamRes(1)));
            gPlc.Controls.Add(MakeButton("相机 NG 2", 210, 94, 110, (s, e) => WriteCamRes(2)));
            gPlc.Controls.Add(MakeButton("相机复位 0", 330, 94, 110, (s, e) => WriteCamRes(0)));
            gPlc.Controls.Add(MakeLabel("型号(40007~12):", 12, 134));
            txtModel = MakeText(130, 131, 120);
            txtModel.Text = _cfg.ProductModel;
            gPlc.Controls.Add(txtModel);
            gPlc.Controls.Add(MakeButton("写产品型号", 262, 130, 110, BtnWriteModel_Click));
            gPlc.Controls.Add(MakeLabel("任意寄存器:读", 12, 172));
            txtReadAddr = MakeText(100, 169, 70);
            txtReadAddr.Text = "2";
            gPlc.Controls.Add(txtReadAddr);
            gPlc.Controls.Add(MakeButton("读", 180, 168, 60, BtnReadReg_Click));
            txtReadVal = MakeText(250, 169, 80);
            txtReadVal.ReadOnly = true;
            gPlc.Controls.Add(txtReadVal);
            gPlc.Controls.Add(MakeLabel("写", 350, 172));
            txtWriteAddr = MakeText(380, 169, 70);
            txtWriteAddr.Text = "5";
            gPlc.Controls.Add(txtWriteAddr);
            txtWriteVal = MakeText(460, 169, 70);
            txtWriteVal.Text = "1";
            gPlc.Controls.Add(txtWriteVal);
            gPlc.Controls.Add(MakeButton("写", 540, 168, 60, BtnWriteReg_Click));
            y += 220;

            // ── 存图测试区 ──
            var gSave = MakeGroup("存图测试（不依赖相机，验证 ImageStore 归档链路）", 8, y, 730, 80);
            left.Controls.Add(gSave);
            gSave.Controls.Add(MakeButton("生成测试图并存图", 12, 26, 180, BtnTestSave_Click));
            lblSavedPath = MakeLabel("最近存图: -", 210, 30);
            lblSavedPath.AutoEllipsis = true;
            lblSavedPath.Width = 500;
            lblSavedPath.ForeColor = Color.FromArgb(46, 158, 107);
            gSave.Controls.Add(lblSavedPath);
            y += 80;

            // ── 右栏：状态 / 预览 / 日志 ──
            var right = new Panel { Dock = DockStyle.Fill };
            root.Controls.Add(right, 1, 0);
            right.Controls.Add(MakeLabel("连接状态:", 12, 12));

            lblPlcState = MakeLabel("○ PLC 断连", 12, 38);
            lblPlcState.ForeColor = Color.Red;
            right.Controls.Add(lblPlcState);
            lblCamAllState = MakeLabel("○ 相机 断连", 12, 62);
            lblCamAllState.ForeColor = Color.Red;
            right.Controls.Add(lblCamAllState);
            lblScannerAllState = MakeLabel("○ 扫码枪 断连", 12, 86);
            lblScannerAllState.ForeColor = Color.Red;
            right.Controls.Add(lblScannerAllState);

            picPreview = new PictureBox
            {
                Bounds = new Rectangle(12, 116, 372, 240),
                BackColor = Color.FromArgb(34, 36, 38),
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle
            };
            right.Controls.Add(picPreview);

            right.Controls.Add(MakeLabel("日志:", 12, 366));
            txtLog = new TextBox
            {
                Bounds = new Rectangle(12, 390, 372, 340),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White
            };
            right.Controls.Add(txtLog);
        }

        /// <summary>便捷：创建 GroupBox（统一字体/标题位置）。</summary>
        private GroupBox MakeGroup(string title, int x, int y, int w, int h)
        {
            return new GroupBox
            {
                Text = title,
                Bounds = new Rectangle(x, y, w, h),
                Font = Font
            };
        }

        /// <summary>便捷：创建 Label。</summary>
        private Label MakeLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                Font = Font
            };
        }

        /// <summary>便捷：创建 Button（订阅点击）。</summary>
        private Button MakeButton(string text, int x, int y, int w, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, 30),
                Font = Font,
                UseVisualStyleBackColor = true
            };
            b.Click += onClick;
            _busyControls.Add(b);   // 忙碌时统一禁用（所有按钮都会发起网络 IO）
            return b;
        }

        /// <summary>便捷：创建 ComboBox（下拉）。</summary>
        private ComboBox MakeCombo(int x, int y, int w)
        {
            return new ComboBox
            {
                Location = new Point(x, y),
                Size = new Size(w, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = Font
            };
        }

        /// <summary>便捷：创建 TextBox。</summary>
        private TextBox MakeText(int x, int y, int w)
        {
            return new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(w, 26),
                Font = Font
            };
        }

        // ══════════════════════ 事件接线 ══════════════════════

        private void WireEvents()
        {
            // 相机/扫码枪下拉切换时刷新对应状态标签
            cmbCamera.SelectedIndexChanged += (s, e) => RefreshStates();
            cmbScanner.SelectedIndexChanged += (s, e) => RefreshStates();
        }

        // ══════════════════════ DeviceHub 生命周期 ══════════════════════

        /// <summary>
        /// 按当前 _cfg 重建 DeviceHub（构造时与"应用配置"共用）。
        /// 设备连接是惰性的：这里只建对象，首次读写才连网。订阅聚合事件后服务即进入
        /// 自愈循环（断连自动重连），连接状态变化会实时刷新右侧状态灯。
        /// </summary>
        private void RebuildHub()
        {
            // 旧 hub 释放（首次为 null，忽略）
            if (_hub != null)
            {
                _hub.DeviceConnectionChanged -= OnDeviceConnectionChanged;
                _hub.SerialNumberScanned -= OnSerialScanned;
                _hub.Dispose();
            }

            _hub = new DeviceHub(_cfg.Devices);
            // 聚合事件：设备连接状态（多设备一个出口）与条码
            _hub.DeviceConnectionChanged += OnDeviceConnectionChanged;
            _hub.SerialNumberScanned += OnSerialScanned;
            _hub.Start();   // 扫码枪连接 + 心跳监控 + 存图定期清理

            // 重新填充相机/扫码枪下拉（配置变了行数可能变）
            FillCameraCombo();
            FillScannerCombo();
        }

        /// <summary>按当前相机配置填充相机下拉（名称 + IP）。</summary>
        private void FillCameraCombo()
        {
            cmbCamera.Items.Clear();
            for (int i = 0; i < _hub.Cameras.Count; i++)
            {
                string name = _hub.Cameras[i].DisplayName;
                if (string.IsNullOrWhiteSpace(name)) name = $"相机{i + 1}";
                cmbCamera.Items.Add($"{name}  {_hub.Cameras[i].IpLabel}");
            }
            if (cmbCamera.Items.Count > 0) cmbCamera.SelectedIndex = 0;
        }

        /// <summary>按当前扫码枪配置填充扫码枪下拉（TCP 显示 IP:端口，串口显示 COMx）。</summary>
        private void FillScannerCombo()
        {
            cmbScanner.Items.Clear();
            for (int i = 0; i < _hub.Scanners.Count; i++)
                cmbScanner.Items.Add($"扫码枪{i + 1}  {_hub.Scanners[i].Name}");
            if (cmbScanner.Items.Count > 0) cmbScanner.SelectedIndex = 0;
        }

        /// <summary>设备连接状态聚合事件（工作线程触发）：刷新右侧状态灯。</summary>
        private void OnDeviceConnectionChanged(object sender, HubConnectionChangedEventArgs e)
        {
            SafeInvoke(RefreshStates);
        }

        /// <summary>扫码枪条码聚合事件（工作线程触发）：显示并记录最近条码（存图 {SN} 用）。</summary>
        private void OnSerialScanned(object sender, string code)
        {
            SafeInvoke(() =>
            {
                _currentSerial = code ?? "";
                lblScannerCode.Text = string.IsNullOrEmpty(_currentSerial) ? "（空条码）" : _currentSerial;
                AppendLog($"扫码枪读到条码：{_currentSerial}");
            });
        }

        /// <summary>刷新右侧连接状态灯（PLC 三态 / 相机聚合 / 扫码枪聚合）。</summary>
        private void RefreshStates()
        {
            // PLC：主站连入 > 监听就绪 > 监听失败
            if (_hub?.Plc != null)
            {
                var plc = _hub.Plc;
                lblPlcState.Text = plc.HasMasterConnected ? "● PLC 已连入（主站在通讯）"
                    : plc.IsConnected ? "● PLC 监听就绪（等待主站）"
                    : "○ PLC 监听失败";
                lblPlcState.ForeColor = plc.HasMasterConnected ? Color.Green
                    : plc.IsConnected ? Color.Orange : Color.Red;
            }
            else { lblPlcState.Text = "○ PLC 未配置"; lblPlcState.ForeColor = Color.Gray; }

            // 相机：任一已连接即绿，否则红
            bool anyCam = false;
            if (_hub != null)
                foreach (var c in _hub.Cameras)
                    if (c.IsConnected) { anyCam = true; break; }
            lblCamAllState.Text = anyCam ? "● 相机 已连接" : "○ 相机 断连";
            lblCamAllState.ForeColor = anyCam ? Color.Green : Color.Red;

            // 扫码枪：任一已打开即绿
            bool anyScan = false;
            if (_hub != null)
                foreach (var s in _hub.Scanners)
                    if (s.IsOpen) { anyScan = true; break; }
            lblScannerAllState.Text = anyScan ? "● 扫码枪 已连接" : "○ 扫码枪 断连";
            lblScannerAllState.ForeColor = anyScan ? Color.Green : Color.Red;

            // 选中相机/扫码枪的状态标签
            var cam = SelectedCamera();
            lblCamState.Text = cam != null ? (cam.IsConnected ? "● 已连接" : "○ 断连") : "无相机";
            lblCamState.ForeColor = cam != null && cam.IsConnected ? Color.Green : Color.Red;
            var scanner = SelectedScanner();
            lblScannerState.Text = scanner != null ? (scanner.IsOpen ? "● 已连接" : "○ 断连") : "无扫码枪";
            lblScannerState.ForeColor = scanner != null && scanner.IsOpen ? Color.Green : Color.Red;
        }

        // ══════════════════════ 配置按钮 ══════════════════════

        /// <summary>应用配置（热更）：把界面上 PLC IP/端口/型号 写回配置，调 ApplyConfig 重建设备层。</summary>
        private void BtnApplyConfig_Click(object sender, EventArgs e)
        {
            // 收集界面参数到配置
            _cfg.ProductModel = cmbModel.SelectedItem?.ToString() ?? _cfg.ProductModel;
            if (_cfg.Devices.Plc == null) _cfg.Devices.Plc = new PlcConfig();
            _cfg.Devices.Plc.IpAddress = txtPlcIp.Text.Trim();
            if (ushort.TryParse(txtPlcPort.Text.Trim(), out ushort port)) _cfg.Devices.Plc.Port = port;

            _busy = true;
            AppendLog("→ 应用配置（热更）…");
            try
            {
                _hub.ApplyConfig(_cfg.Devices);   // 热更：停旧→重建→触发 ServicesRebuilt
                _hub.Start();
                AppendLog("← 设备层已按新配置重建（热更完成）");
            }
            catch (Exception ex)
            {
                AppendLog("← 应用配置异常：" + ex.Message);
            }
            finally
            {
                _busy = false;
                RefreshStates();
            }
        }

        /// <summary>保存配置到磁盘（Config/demo.json）。</summary>
        private void BtnSaveConfig_Click(object sender, EventArgs e)
        {
            _cfg.ProductModel = cmbModel.SelectedItem?.ToString() ?? _cfg.ProductModel;
            _cfg.Save();
            AppendLog("配置已保存：" + DemoConfig.ConfigFilePath);
        }

        /// <summary>重新加载磁盘配置并重建设备层。</summary>
        private void BtnLoadConfig_Click(object sender, EventArgs e)
        {
            var loaded = DemoConfig.Load();
            _cfg = loaded;
            _cfg.ProductModel = cmbModel.SelectedItem?.ToString() ?? loaded.ProductModel;
            // 界面字段回填
            txtPlcIp.Text = _cfg.Devices.Plc?.IpAddress ?? "0.0.0.0";
            txtPlcPort.Text = (_cfg.Devices.Plc?.Port ?? 502).ToString();
            RebuildHub();
            AppendLog("已从磁盘加载配置：" + DemoConfig.ConfigFilePath);
        }

        // ══════════════════════ 相机操作（全部后台线程）══════════════════════

        /// <summary>仅触发拍摄（T1）：收到相机回显即成功，不做判定读取。</summary>
        private void BtnT1_Click(object sender, EventArgs e)
        {
            var cam = SelectedCamera();
            if (cam == null) { Msg("请先在相机列表选择一台相机。"); return; }
            SetBusy(true);
            AppendLog($"→ 相机 {cam.IpLabel} 触发拍照（T1）…");
            Task.Run(() =>
            {
                bool ok = cam.SendTrigger();
                SafeInvoke(() =>
                {
                    lblCamResult.Text = ok ? "T1 触发成功：已收到相机回显" : "T1 触发失败：无回显";
                    lblCamResult.ForeColor = ok ? Color.Green : Color.Gray;
                    AppendLog(ok ? "← T1 触发成功" : "← T1 触发失败（相机未回显）");
                    FinishOp();
                });
            });
        }

        /// <summary>
        /// 触发＋读判定（T2）+ 取图存图：完整链路验证。
        /// 相机拍照回判定后，去该相机 FTP 目录扫"修改时间最新"的一对文件（jpeg+iv4p），
        /// 归档到 ImageConfig.SaveRootDir 下（点位固定 1，测试用），预览闪图，删 FTP 源图。
        /// 【时序】T2 成功只代表"相机已拍并回判定"，图推到 FTP 有延迟——轮询等新图最多
        /// 5 秒（认修改时间晚于触发时刻），超时兜底取最新对。
        /// </summary>
        private void BtnT2_Click(object sender, EventArgs e)
        {
            var cam = SelectedCamera();
            if (cam == null) { Msg("请先在相机列表选择一台相机。"); return; }
            int camIndex = cmbCamera.SelectedIndex;

            SetBusy(true);
            AppendLog($"→ 相机 {cam.IpLabel} 触发+读判定（T2）…");
            Task.Run(() =>
            {
                var r = cam.TriggerAndRead();
                string jpeg = null, iv4p = null, archived = null, fetchError = null;
                if (r.Succeeded)
                {
                    // 轮询等新图（与 DevTestForm 同思路）
                    DateTime triggerUtc = DateTime.UtcNow;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var pair = new ImageStore.LatestPairResult();
                    while (sw.ElapsedMilliseconds < 5000)
                    {
                        var cand = ResolveLatestFtpPair(camIndex);
                        if (!string.IsNullOrEmpty(cand.JpegPath) && IsNewerThanTrigger(cand.JpegPath, triggerUtc))
                        { pair = cand; break; }
                        Thread.Sleep(200);
                    }
                    if (string.IsNullOrEmpty(pair.JpegPath))
                        pair = ResolveLatestFtpPair(camIndex);
                    jpeg = pair.JpegPath; iv4p = pair.IvpPath;
                    if (string.IsNullOrEmpty(jpeg))
                        fetchError = "FTP 取图目录没有 jpeg（相机已触发但未推图，请检查相机 FTP 配置/网络）";
                    else if (_hub.ImageStore != null)
                    {
                        archived = _hub.ImageStore.SaveImageFilePair(jpeg, iv4p, 1, r.IsOk, _currentSerial, cam.DisplayName);
                        if (archived != null)
                        {
                            ImageStore.DeleteSourceFile(jpeg, "Demo T2");
                            ImageStore.DeleteSourceFile(iv4p, "Demo T2");
                        }
                    }
                    else fetchError = "未配置 ImageStore（无法存图）";
                }
                SafeInvoke(() =>
                {
                    if (r.Succeeded)
                    {
                        lblCamResult.Text = r.IsOk ? $"T2 判定：OK（{r.ResultText}）" : $"T2 判定：NG（{r.ResultText}）";
                        lblCamResult.ForeColor = r.IsOk ? Color.Green : Color.Red;
                        AppendLog($"← T2 判定 {(r.IsOk ? "OK" : "NG")}：{r.ResultText}");
                        if (archived != null)
                        {
                            bool shown = ShowPreview(archived);
                            lblSavedPath.Text = "最近存图：" + archived;
                            lblSavedPath.ForeColor = shown ? Color.FromArgb(46, 158, 107) : Color.Red;
                            AppendLog($"→ 已取图并存档（点位1）：{archived}，已删 FTP 源图");
                        }
                        else
                        {
                            lblSavedPath.Text = "取图失败：" + (fetchError ?? "未知");
                            lblSavedPath.ForeColor = Color.Red;
                            AppendLog("← 取图失败：" + (fetchError ?? "（无图可存）"));
                        }
                    }
                    else
                    {
                        lblCamResult.Text = "T2 失败：" + r.Detail;
                        lblCamResult.ForeColor = Color.Gray;
                        AppendLog("← T2 失败：" + r.Detail);
                    }
                    FinishOp();
                });
            });
        }

        /// <summary>读取相机当前程序号（PR），显示 Pxxx。</summary>
        private void BtnReadProgram_Click(object sender, EventArgs e)
        {
            var cam = SelectedCamera();
            if (cam == null) { Msg("请先在相机列表选择一台相机。"); return; }
            SetBusy(true);
            AppendLog($"→ 相机 {cam.IpLabel} 读取当前程序号（PR）…");
            Task.Run(() =>
            {
                int no = cam.ReadProgramNo();
                SafeInvoke(() =>
                {
                    if (no >= 0)
                    {
                        lblCurProgram.Text = $"当前程序: P{no:D3}";
                        lblCurProgram.ForeColor = Color.Green;
                        lblCamResult.Text = $"当前程序号 P{no:D3}（读回成功）";
                        lblCamResult.ForeColor = Color.Green;
                        AppendLog($"← 当前程序号 P{no:D3}");
                    }
                    else
                    {
                        lblCurProgram.Text = "当前程序: 读取失败";
                        lblCurProgram.ForeColor = Color.Red;
                        lblCamResult.Text = "PR 读取失败（未连接/无响应）";
                        lblCamResult.ForeColor = Color.Gray;
                        AppendLog("← 读取当前程序号失败");
                    }
                    FinishOp();
                });
            });
        }

        /// <summary>切到相机程序 P001（发 PW,001），顺带读回确认。</summary>
        private void BtnSwProgram_Click(object sender, EventArgs e)
        {
            var cam = SelectedCamera();
            if (cam == null) { Msg("请先在相机列表选择一台相机。"); return; }
            SetBusy(true);
            AppendLog($"→ 相机 {cam.IpLabel} 切换程序 → P001（PW,001）…");
            Task.Run(() =>
            {
                bool ok = cam.SwitchProgram(1);
                SafeInvoke(() =>
                {
                    lblCurProgram.Text = ok ? "当前程序: P001（已切）" : "当前程序: 切换失败";
                    lblCurProgram.ForeColor = ok ? Color.Green : Color.Red;
                    lblCamResult.Text = ok ? "已切到 P001（PW,001 成功）" : "切换 P001 失败（PW 无响应/相机报错）";
                    lblCamResult.ForeColor = ok ? Color.Green : Color.Red;
                    AppendLog(ok ? "← 切换 P001 成功" : "← 切换 P001 失败");
                    FinishOp();
                });
            });
        }

        // ══════════════════════ 扫码枪操作 ══════════════════════

        /// <summary>手动发送扫码枪触发指令（TCP 连上后需发 LON 才开始读码）。</summary>
        private void BtnScannerTrigger_Click(object sender, EventArgs e)
        {
            var scanner = SelectedScanner();
            if (scanner == null) { Msg("请先在列表选择一台扫码枪。"); return; }
            SetBusy(true);
            AppendLog("→ 发送扫码枪触发指令 …");
            Task.Run(() =>
            {
                bool ok = scanner.SendTrigger();
                SafeInvoke(() =>
                {
                    AppendLog(ok ? "← 触发指令已发送" : "← 触发指令发送失败（未连接）");
                    FinishOp();
                });
            });
        }

        // ══════════════════════ PLC 操作（全部后台线程）══════════════════════

        /// <summary>读扫码请求（协议 40001）：显示 PLC 主站是否请求扫码（1=请求）。</summary>
        private void BtnReadScanReq_Click(object sender, EventArgs e)
        {
            if (!EnsurePlc()) return;
            SetBusy(true);
            AppendLog("→ 读扫码请求（40001）…");
            Task.Run(() =>
            {
                bool ok = _hub.Plc.ReadScanRequest(out bool requested);
                SafeInvoke(() =>
                {
                    lblMoveVal.Text = ok ? (requested ? "扫码请求=1" : "扫码请求=0") : "读取失败";
                    lblMoveVal.ForeColor = ok ? (requested ? Color.Green : Color.Gray) : Color.Red;
                    AppendLog(ok ? $"← 扫码请求 = {(requested ? 1 : 0)}" : "← 读扫码请求失败");
                    FinishOp();
                });
            });
        }

        /// <summary>读各相机请求（每台一路通道）：显示各相机当前请求点位。</summary>
        private void BtnReadCamReq_Click(object sender, EventArgs e)
        {
            if (!EnsurePlc()) return;
            if (_cfg.Devices.Cameras == null || _cfg.Devices.Cameras.Count == 0)
            { Msg("当前没有相机配置，无法读相机请求。"); return; }
            SetBusy(true);
            var cams = new List<CameraConfig>(_cfg.Devices.Cameras);
            AppendLog($"→ 读相机请求（{cams.Count} 台相机）…");
            Task.Run(() =>
            {
                var labels = new List<string>();
                bool allOk = true;
                foreach (var c in cams)
                {
                    bool ok = _hub.Plc.ReadCameraRequest(c, out int station);
                    if (!ok) allOk = false;
                    labels.Add($"{c.Name}={station}");
                }
                string joined = string.Join("  ", labels);
                bool anyActive = labels.Exists(s => !s.EndsWith("=0"));
                SafeInvoke(() =>
                {
                    lblMoveVal.Text = allOk ? joined : "读取失败";
                    lblMoveVal.ForeColor = allOk ? (anyActive ? Color.Green : Color.Gray) : Color.Red;
                    AppendLog(allOk ? $"← 相机请求：{joined}" : "← 读相机请求失败");
                    FinishOp();
                });
            });
        }

        /// <summary>写扫码结果（协议 40004）：0=复位 / 1=OK / 2=NG。</summary>
        private void WriteScanRes(int code)
        {
            if (!EnsurePlc()) return;
            SetBusy(true);
            AppendLog($"→ 写扫码结果 = {code}（40004）…");
            Task.Run(() =>
            {
                _hub.Plc.WriteScanResult(code);
                SafeInvoke(() =>
                {
                    AppendLog($"← 已写扫码结果 {code}（{ResName(code)}）");
                    FinishOp();
                });
            });
        }

        /// <summary>写所有相机结果（各相机自己的结果通道）：0=复位 / 1=OK / 2=NG。</summary>
        private void WriteCamRes(int code)
        {
            if (!EnsurePlc()) return;
            if (_cfg.Devices.Cameras == null || _cfg.Devices.Cameras.Count == 0)
            { Msg("当前没有相机配置，无法写相机结果。"); return; }
            SetBusy(true);
            var cams = new List<CameraConfig>(_cfg.Devices.Cameras);
            AppendLog($"→ 写相机结果 = {code}（{cams.Count} 台相机）…");
            Task.Run(() =>
            {
                foreach (var c in cams)
                    _hub.Plc.WriteCameraResult(c, code);
                SafeInvoke(() =>
                {
                    AppendLog($"← 已写相机结果 {code}（{ResName(code)}，全部 {cams.Count} 台）");
                    FinishOp();
                });
            });
        }

        /// <summary>写产品型号（40007=序号 + 40008~40012=型号 ASCII），供 PLC 主站读取。</summary>
        private void BtnWriteModel_Click(object sender, EventArgs e)
        {
            if (!EnsurePlc()) return;
            SetBusy(true);
            string model = txtModel.Text.Trim();
            AppendLog($"→ 写产品型号 [{model}]（40007~40012）…");
            Task.Run(() =>
            {
                bool ok = _hub.Plc.WriteProductModel(model);
                SafeInvoke(() =>
                {
                    AppendLog(ok ? "← 型号与序号已写入" : "← 型号写入失败（从站未就绪）");
                    FinishOp();
                });
            });
        }

        /// <summary>通用读任意寄存器（DataStore 索引地址）。</summary>
        private void BtnReadReg_Click(object sender, EventArgs e)
        {
            if (!EnsurePlc()) return;
            if (!ushort.TryParse(txtReadAddr.Text.Trim(), out ushort addr))
            { Msg("读地址需为 0~65535 整数（DataStore 索引）。"); return; }
            SetBusy(true);
            AppendLog($"→ 读 D{addr} …");
            Task.Run(() =>
            {
                bool ok = _hub.Plc.ReadRegister(addr, out ushort value);
                SafeInvoke(() =>
                {
                    txtReadVal.Text = ok ? value.ToString() : "通讯失败";
                    AppendLog(ok ? $"← D{addr} = {value}" : $"← 读 D{addr} 失败");
                    FinishOp();
                });
            });
        }

        /// <summary>通用写任意寄存器（DataStore 索引地址）。</summary>
        private void BtnWriteReg_Click(object sender, EventArgs e)
        {
            if (!EnsurePlc()) return;
            if (!ushort.TryParse(txtWriteAddr.Text.Trim(), out ushort addr))
            { Msg("写地址需为 0~65535 整数（DataStore 索引）。"); return; }
            if (!ushort.TryParse(txtWriteVal.Text.Trim(), out ushort value))
            { Msg("写值需为 0~65535 整数。"); return; }
            SetBusy(true);
            AppendLog($"→ 写 D{addr} = {value} …");
            Task.Run(() =>
            {
                bool ok = _hub.Plc.WriteRegister(addr, value);
                SafeInvoke(() =>
                {
                    AppendLog(ok ? $"← 已写 D{addr} = {value}" : $"← 写 D{addr} 失败");
                    FinishOp();
                });
            });
        }

        // ══════════════════════ 存图测试（不依赖相机）══════════════════════

        /// <summary>
        /// 生成一张测试图（绘制"OK/NG + 点位 + 时间"），走 ImageStore.SaveImage 归档，
        /// 验证"存图目录结构 + 文件名模板 + 时间戳后缀"整条链路，不依赖相机。
        /// 序列号用最近扫码条码（无则用 "TEST-0001"），可验证 {SN} 目录。
        /// </summary>
        private void BtnTestSave_Click(object sender, EventArgs e)
        {
            if (_hub?.ImageStore == null) { Msg("未配置 ImageStore。"); return; }
            SetBusy(true);
            AppendLog("→ 生成测试图并存图 …");
            Task.Run(() =>
            {
                string saved = null;
                try
                {
                    // 后台线程里画图（GDI+ 放后台，UI 不禁 IO）
                    using (var bmp = new Bitmap(640, 480))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        g.DrawRectangle(Pens.Black, 0, 0, 639, 479);
                        g.DrawString("CommonLib 存图测试", new Font("Arial", 28, FontStyle.Bold), Brushes.DarkBlue, 60, 60);
                        g.DrawString("点位 1", new Font("Arial", 20), Brushes.Black, 60, 150);
                        g.DrawString("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), new Font("Arial", 16), Brushes.Gray, 60, 220);
                        g.DrawString("SN " + _currentSerial, new Font("Arial", 16), Brushes.Gray, 60, 280);
                        // 大 OK 徽标（绿）——与现场"OK=绿"习惯一致
                        g.FillEllipse(Brushes.Green, 480, 300, 110, 110);
                        g.DrawString("OK", new Font("Arial", 26, FontStyle.Bold), Brushes.White, 498, 330);
                        saved = _hub.ImageStore.SaveImage(bmp, 1, true, _currentSerial, "Demo测试");
                    }
                }
                catch (Exception ex)
                {
                    SafeInvoke(() => AppendLog("← 存图异常：" + ex.Message));
                }
                SafeInvoke(() =>
                {
                    if (!string.IsNullOrEmpty(saved))
                    {
                        lblSavedPath.Text = "最近存图：" + saved;
                        lblSavedPath.ForeColor = Color.FromArgb(46, 158, 107);
                        AppendLog("← 存图成功：" + saved);
                        ShowPreview(saved);   // 后台加载缩略图预览
                    }
                    else if (string.IsNullOrEmpty(lblSavedPath.Text) || lblSavedPath.Text.Contains("最近存图："))
                    {
                        lblSavedPath.Text = "存图失败：见日志";
                        lblSavedPath.ForeColor = Color.Red;
                    }
                    FinishOp();
                });
            });
        }

        // ══════════════════════ 工具方法 ══════════════════════

        /// <summary>当前下拉框选中的相机实例；无选中返回 null。</summary>
        private KeyenceIV4Camera SelectedCamera()
        {
            int idx = cmbCamera.SelectedIndex;
            if (idx < 0 || idx >= _hub.Cameras.Count) return null;
            return _hub.Cameras[idx];
        }

        /// <summary>当前下拉框选中的扫码枪实例；无选中返回 null。</summary>
        private IScanner SelectedScanner()
        {
            int idx = cmbScanner.SelectedIndex;
            if (idx < 0 || idx >= _hub.Scanners.Count) return null;
            return _hub.Scanners[idx];
        }

        /// <summary>取该相机 FTP 取图目录"最新一对"（目录用配置 FtpUploadDir，空回退全局 FtpRootDir）。</summary>
        private ImageStore.LatestPairResult ResolveLatestFtpPair(int cameraIndex)
        {
            if (_hub.ImageStore == null) return new ImageStore.LatestPairResult();
            var camCfgs = _cfg.Devices.Cameras;
            if (camCfgs == null || cameraIndex < 0 || cameraIndex >= camCfgs.Count)
                return new ImageStore.LatestPairResult();
            string dir = camCfgs[cameraIndex].FtpUploadDir;
            if (string.IsNullOrWhiteSpace(dir)) dir = _hub.ImageStore.DefaultFtpDir;
            return _hub.ImageStore.FindLatestPair(dir);
        }

        /// <summary>文件修改时间（UTC）不早于触发时刻（容差 1 秒）即视为本次新图。</summary>
        private static bool IsNewerThanTrigger(string path, DateTime triggerUtc)
        {
            try { return File.GetLastWriteTimeUtc(path) >= triggerUtc.AddSeconds(-1); }
            catch { return false; }
        }

        /// <summary>后台加载缩略图并显示到预览框（UI 线程禁读盘/解码——后台做，完成后回 UI 赋值）。</summary>
        private bool ShowPreview(string path)
        {
            try
            {
                Task.Factory.StartNew(() =>
                {
                    Bitmap thumb = null;
                    try
                    {
                        // FileShare.ReadWrite：容忍文件正被写入/占用
                        using (var img = Image.FromStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                            thumb = new Bitmap(img, new Size(372, 240));
                    }
                    catch { }
                    if (thumb != null)
                    {
                        SafeInvoke(() =>
                        {
                            var old = picPreview.Image;
                            picPreview.Image = thumb;
                            old?.Dispose();
                        });
                    }
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>忙碌开关：busy=true 时禁用所有网络按钮防连点；操作结束必须调 false。</summary>
        private void SetBusy(bool busy)
        {
            if (_busy == busy) return;
            _busy = busy;
            foreach (var c in _busyControls)
                if (c != null) c.Enabled = !busy;
        }

        /// <summary>操作收尾：刷新状态 + 解锁按钮。</summary>
        private void FinishOp()
        {
            RefreshStates();
            SetBusy(false);
        }

        /// <summary>PLC 服务存在性检查。</summary>
        private bool EnsurePlc()
        {
            if (_hub?.Plc == null) { Msg("PLC 服务不可用。"); return false; }
            return true;
        }

        /// <summary>结果码 → 显示名。</summary>
        private static string ResName(int code) =>
            code == 0 ? "复位" : code == 1 ? "OK" : "NG";

        /// <summary>弹窗提示。</summary>
        private void Msg(string text) =>
            MessageBox.Show(text, "CommonLib 测试台", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        /// <summary>跨线程安全更新 UI：当前在 UI 线程直接执行，否则丢给 UI 线程队列。</summary>
        private void SafeInvoke(Action action)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); }
                catch (InvalidOperationException) { }
            }
            else action();
        }

        /// <summary>追加日志（任何线程可调，内部 SafeInvoke 回 UI 线程）。</summary>
        private void AppendLog(string text)
        {
            SafeInvoke(() =>
            {
                string line = $"[{DateTime.Now:HH:mm:ss}] {text}";
                txtLog.AppendText(line + Environment.NewLine);
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            });
        }

        /// <summary>关窗：按固定顺序释放（协调器无 → 直接 hub.Dispose，内部已按序释放各服务）。</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _hub.DeviceConnectionChanged -= OnDeviceConnectionChanged;
            _hub.SerialNumberScanned -= OnSerialScanned;
            try { _hub?.Dispose(); }
            catch (Exception ex) { LogHelper.Warn("Demo 关闭释放异常：" + ex.Message); }
            base.OnFormClosing(e);
        }
    }
}
