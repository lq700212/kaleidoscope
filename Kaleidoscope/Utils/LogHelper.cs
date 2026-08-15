using System;
using System.IO;
using System.Text;

namespace Kaleidoscope.Utils
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
    ///
    /// 【写入策略】常驻 StreamWriter + 跨天滚动：相机触发等高频场景逐条日志时不再每次
    /// Open/Close 文件（旧实现 File.AppendAllText 每次打开-写入-关闭，高频下是浪费的 IO）。
    /// AutoFlush=true 保证即时落盘（通讯日志需要现场实时可查）；跨天时关旧流开新文件。
    /// </summary>
    public static class LogHelper
    {
        private static readonly object _lock = new object();

        /// <summary>默认日志目录：程序运行目录下的 Logs 子目录</summary>
        private static string Dir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

        /// <summary>常驻日志写入流（跨天滚动重建；_lock 内访问）</summary>
        private static StreamWriter _writer;

        /// <summary>当前 _writer 对应的日期（yyyyMMdd；跨天时重建 _writer）</summary>
        private static string _writerDate = "";

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

            // 默认：写文件（常驻流 + 跨天滚动）
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Dir);
                    string date = DateTime.Now.ToString("yyyyMMdd");
                    if (_writer == null || _writerDate != date)
                    {
                        // 首次或跨天：关旧流（若有），按当天文件名开新流（追加模式）
                        try { if (_writer != null) { _writer.Flush(); _writer.Dispose(); } } catch { }
                        _writer = new StreamWriter(
                            Path.Combine(Dir, $"运行日志_{date}.log"),
                            append: true,
                            encoding: new UTF8Encoding(false));   // 无 BOM，与旧 File.AppendAllText 一致
                        _writer.AutoFlush = true;   // 每条即落盘：现场断电/崩溃前日志尽可能已写入
                        _writerDate = date;
                    }
                    _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}");
                }
            }
            catch
            {
                // 日志本身失败不允许影响业务，静默丢弃
            }
        }
    }
}
