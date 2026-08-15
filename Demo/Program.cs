using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using CommonLib.Utils;

namespace CommonLibDemo
{
    /// <summary>
    /// Demo 程序入口：把 CommonLib 的 LogHelper 接到本程序日志文件（Config/demo.log），
    /// 然后启动主窗体。日志文件仅作排障备份，界面右上角还有实时日志框。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // 注入日志出口：CommonLib 服务层的所有日志都走这里（文件 + 控制台兜底）
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "demo.log");
                LogHelper.LogAction = (level, msg) =>
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(logPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.AppendAllText(logPath,
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}][{level}] {msg}\r\n", Encoding.UTF8);
                    }
                    catch { }
                };
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
