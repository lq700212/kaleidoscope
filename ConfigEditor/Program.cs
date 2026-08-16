using System;
using System.Windows.Forms;

namespace Kaleidoscope.ConfigEditor
{
    /// <summary>
    /// 配置编辑器入口。支持命令行第一个参数直接传入 .kcfg 文件路径（启动即打开）。
    /// 无参数时以默认配置启动（新建状态）。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string openFile = (args != null && args.Length > 0) ? args[0] : null;
            Application.Run(new MainForm(openFile));
        }
    }
}