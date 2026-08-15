using System;
using System.IO;

namespace CommonLib.Utils
{
    /// <summary>
    /// 极简日志（从 CommandCenter/Utils/LogHelper.cs 抽取）。
    ///
    /// 【用途】通讯库各服务（PLC/相机/扫码枪/图片存储）的统一日志出口。
    /// 默认写程序目录 Logs\运行日志_yyyyMMdd.txt，按天一个文件；
    /// 多线程（PLC 轮询/图像监听线程）写文件不打架（静态方法 + lock）。
    ///
    /// 【可替换出口（库通用性增强）】宿主应用可把日志接到自己的日志系统：
    ///   LogHelper.LogAction = (level, message) => MyLogger.Write(level, message);
    /// 设置后文件落盘自动跳过（避免重复），不设置时维持默认文件日志。
    /// </summary>
    public static class LogHelper
    {
        private static readonly object _lock = new object();

        /// <summary>默认日志目录：程序运行目录下的 Logs 子目录</summary>
        private static string Dir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

        /// <summary>
        /// 可选的自定义日志出口（null = 用默认文件日志）。
        /// 宿主设置后，Info/Warn/Error 不再写文件，全数交给该委托。
        /// 注意：委托内不要抛异常，这里不拦截（与文件日志"失败静默"行为一致由调用方保证）。
        /// </summary>
        public static Action<string, string> LogAction { get; set; }

        /// <summary>写入一条信息日志</summary>
        public static void Info(string message) => Write("INFO", message);

        /// <summary>写入一条警告日志</summary>
        public static void Warn(string message) => Write("WARN", message);

        /// <summary>写入一条错误日志</summary>
        public static void Error(string message) => Write("ERROR", message);

        /// <summary>写入一条错误日志并附带异常堆栈</summary>
        public static void Error(string message, Exception ex) =>
            Write("ERROR", message + "\r\n" + (ex?.ToString() ?? ""));

        private static void Write(string level, string message)
        {
            // 宿主已接管日志：直接把 level+message 交给自定义出口，不再落盘
            var custom = LogAction;
            if (custom != null)
            {
                try { custom(level, message); }
                catch { /* 自定义出口异常不让它影响业务 */ }
                return;
            }

            // 默认：写文件
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Dir);
                    string file = Path.Combine(Dir, $"运行日志_{DateTime.Now:yyyyMMdd}.log");
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                    File.AppendAllText(file, line + "\r\n");
                }
            }
            catch
            {
                // 日志本身失败不允许影响业务，静默丢弃
            }
        }
    }
}
