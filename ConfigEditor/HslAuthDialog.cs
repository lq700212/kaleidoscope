using System;
using System.Drawing;
using System.Windows.Forms;
using Kaleidoscope.Utils;

namespace Kaleidoscope.ConfigEditor
{
    /// <summary>
    /// HslCommunication 授权码输入弹窗。
    ///
    /// 【什么时候弹】
    /// ① 用户在设备树切到"多品牌 PLC"或"EIP 扫码枪"节点、且本地尚未激活授权时由主窗体自动弹出；
    /// ② 用户点预设条上的"HSL 授权"按钮手动弹出（查看状态/换码）。
    ///
    /// 【行为】输入授权码 → 点确定 → <see cref="HslAuthorization.TryActivate"/> 本地验签：
    ///   - 有效：立即在本进程生效，并写入 exe 同目录 hsl.auth 文件（下次启动免输），提示成功后关窗；
    ///   - 无效：红字提示"授权码无效"，不关窗、不落盘，聚焦输入框让用户重试；
    ///   - 取消：直接关窗不保存（未激活状态下多品牌/EIP 通讯仍可用，但受限流，日志会提醒）。
    ///
    /// 【查看已保存的授权码】打开时自动把本地保存的授权码（解密后）预填在输入框——授权码是
    /// 购买的资产，忘记时在这里查看明文；"复制授权码"按钮一键复制到剪贴板，方便用到其它项目。
    /// </summary>
    public class HslAuthDialog : Form
    {
        private readonly TextBox _txtCode;
        private readonly Label _lblTip;
        private readonly Button _btnOk;
        private readonly Button _btnCancel;
        private readonly Button _btnCopy;

        /// <summary>构造并构建界面。</summary>
        /// <param name="currentActive">当前是否已激活（影响顶部说明文字）</param>
        public HslAuthDialog(bool currentActive)
        {
            Text = "HslCommunication 授权";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 190);
            BackColor = Color.White;

            var lblTitle = new Label
            {
                Text = currentActive
                    ? "HSL 授权当前有效。可在此更换授权码（换公司新码时用）。"
                    : "多品牌 PLC / EIP 扫码枪使用商业协议库 HslCommunication，需要授权码。",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = Color.FromArgb(56, 142, 89),
            };
            Controls.Add(lblTitle);

            Controls.Add(new Label
            {
                Text = "授权码：",
                Location = new Point(16, 48),
                AutoSize = true,
            });

            _txtCode = new TextBox
            {
                Location = new Point(80, 44),
                Size = new Size(420, 26),
                Font = new Font("Consolas", 10F),
            };
            Controls.Add(_txtCode);

            _lblTip = new Label
            {
                Text = "说明：授权码保存在本机 " + HslAuthorization.AuthFilePath +
                       "\r\n（不入 git；也可把该文件直接拷到程序目录免输入）。未授权时通讯仍可运行，但受每日次数限制。",
                Location = new Point(16, 82),
                Size = new Size(488, 52),
                ForeColor = Color.Gray,
            };
            Controls.Add(_lblTip);

            _btnOk = new Button
            {
                Text = "确定并保存",
                Location = new Point(300, 146),
                Size = new Size(110, 30),
                UseVisualStyleBackColor = true,
            };
            _btnOk.Click += BtnOk_Click;
            Controls.Add(_btnOk);

            _btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(420, 146),
                Size = new Size(80, 30),
                UseVisualStyleBackColor = true,
            };
            _btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            Controls.Add(_btnCancel);

            // 复制授权码：把输入框当前内容（打开时预填=本地已存的解密明文）复制到剪贴板，
            // 方便把授权码用到其它项目/机器。复制的是输入框内容——改了未保存时复制的也是新码。
            _btnCopy = new Button
            {
                Text = "复制授权码",
                Location = new Point(16, 146),
                Size = new Size(110, 30),
                UseVisualStyleBackColor = true,
            };
            _btnCopy.Click += BtnCopy_Click;
            Controls.Add(_btnCopy);

            AcceptButton = _btnOk;   // 回车=确定
            CancelButton = _btnCancel;
        }

        /// <summary>复制输入框当前授权码到剪贴板（空则提示）。</summary>
        private void BtnCopy_Click(object sender, EventArgs e)
        {
            string code = _txtCode.Text.Trim();
            if (code.Length == 0)
            {
                ShowTip("没有可复制的授权码。", Color.Red);
                return;
            }
            try
            {
                Clipboard.SetText(code);
                ShowTip("授权码已复制到剪贴板，可直接粘贴到其它项目使用。", Color.FromArgb(56, 142, 89));
            }
            catch (Exception ex)
            {
                ShowTip("复制失败：" + ex.Message, Color.Red);
            }
        }

        /// <summary>窗口打开时若文件里已有授权码则预填（方便核对/换码）。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                string saved = HslAuthorization.LoadFromFile();
                if (!string.IsNullOrWhiteSpace(saved)) _txtCode.Text = saved;
            }
            catch { }
            _txtCode.Focus();
        }

        /// <summary>确定：验签 → 成功才落盘并关窗；失败红字提示留在窗口重试。</summary>
        private void BtnOk_Click(object sender, EventArgs e)
        {
            string code = _txtCode.Text.Trim();
            if (code.Length == 0)
            {
                ShowTip("请输入授权码。", Color.Red);
                return;
            }
            if (!HslAuthorization.TryActivate(code))
            {
                ShowTip("授权码无效（校验未通过），请核对后重新输入。", Color.Red);
                _txtCode.SelectAll();
                _txtCode.Focus();
                return;
            }
            try
            {
                // 验签通过才落盘：保证文件里的码一定是能用的（避免存了个错码下次启动静默失败）
                HslAuthorization.SaveToFile(code);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                // 落盘失败（磁盘只读等）：授权本进程内已生效，但不重启就会丢——明确告知用户手工保存
                ShowTip("授权已在本进程生效，但写入文件失败：" + ex.Message +
                        "\r\n请手工把授权码保存到 " + HslAuthorization.AuthFilePath, Color.Red);
            }
        }

        /// <summary>刷新提示文字与颜色。</summary>
        private void ShowTip(string text, Color color)
        {
            _lblTip.ForeColor = color;
            // 提示行替换说明行时保留文件路径信息在 tooltip，避免窗口高度反复变化
            _lblTip.Text = text;
            _lblTip.BackColor = Color.White;
        }
    }
}
