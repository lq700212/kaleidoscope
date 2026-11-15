using System;

namespace YoloDetection
{
    /// <summary>
    /// 检测模块统一日志门面（静态）。
    ///
    /// 设计说明（模块化约束）：
    ///   - 本类是模块内唯一的日志出口，模块内部不依赖任何具体日志实现
    ///     （文件/UI/控制台均由宿主经委托注入），保证整个模块可独立迁移
    ///   - 未注入输出通道时默认写到 Debug 输出（Debug.WriteLine），不抛异常、零副作用
    ///   - 各通道开关独立，避免调试日志刷屏影响性能
    ///
    /// 宿主接入方式：
    ///   LogManager.Initialize(
    ///       enableYoloLog: false,
    ///       enableGeneralLog: true,
    ///       enableDetectionResultLog: false,
    ///       outputSink: msg => MyFileLogger.Write(msg),   // 落地通道（文件/控制台…）
    ///       uiSink: msg => MyUiPanel.Append(msg));         // 界面通道（可为 null）
    /// </summary>
    public static class LogManager
    {
        /// <summary>YOLO 推理过程详细日志开关（默认关闭）</summary>
        public static bool EnableYoloLog { get; set; } = false;

        /// <summary>检测模块通用日志开关（默认开启）</summary>
        public static bool EnableGeneralLog { get; set; } = true;

        /// <summary>每帧检测结果日志开关（会产生大量输出，默认关闭）</summary>
        public static bool EnableDetectionResultLog { get; set; } = false;

        /// <summary>日志落地通道（宿主注入；默认 Debug 输出）</summary>
        private static Action<string> _output = msg => System.Diagnostics.Debug.WriteLine(msg);

        /// <summary>UI 日志回调（可选）。由宿主程序注入，如显示到界面日志面板。</summary>
        private static Action<string> _uiSink;

        /// <summary>
        /// 初始化各日志开关与输出通道。
        /// </summary>
        /// <param name="enableYoloLog">YOLO 详细日志开关</param>
        /// <param name="enableGeneralLog">通用日志开关</param>
        /// <param name="enableDetectionResultLog">每帧结果日志开关</param>
        /// <param name="outputSink">日志落地通道（文件/控制台等，null=保持默认 Debug 输出）。
        ///   注意回调可能在后台线程触发，宿主实现需自行保证线程安全</param>
        /// <param name="uiSink">UI 同步显示回调（可为 null）</param>
        public static void Initialize(bool enableYoloLog, bool enableGeneralLog,
            bool enableDetectionResultLog, Action<string> outputSink = null, Action<string> uiSink = null)
        {
            EnableYoloLog = enableYoloLog;
            EnableGeneralLog = enableGeneralLog;
            EnableDetectionResultLog = enableDetectionResultLog;
            if (outputSink != null)
            {
                _output = outputSink;
            }
            _uiSink = uiSink;
        }

        /// <summary>清除 UI 回调（宿主界面关闭时调用，防止持有已释放控件引用）</summary>
        public static void ClearUiSink()
        {
            _uiSink = null;
        }

        /// <summary>YOLO 详细日志</summary>
        public static void YoloLog(string message)
        {
            if (EnableYoloLog)
            {
                Write(message);
            }
        }

        /// <summary>通用日志</summary>
        public static void GeneralLog(string message)
        {
            if (EnableGeneralLog)
            {
                Write(message);
            }
        }

        /// <summary>每帧检测结果日志</summary>
        public static void DetectionResultLog(string message)
        {
            if (EnableDetectionResultLog)
            {
                Write(message);
            }
        }

        private static void Write(string message)
        {
            // 落地通道与 UI 通道相互独立：落地由宿主决定写到哪里，UI 仅同步显示。
            //
            // 双通道各自 try/catch（v2.9.2 新增）：宿主注入的回调运行在调用方线程上
            //（检测线程/看门狗 Timer 线程），一旦抛异常——
            //   - 在检测循环的 catch 块里抛 → 逃出 DetectionLoop → 检测线程死亡；
            //   - 在看门狗 Timer 回调里抛 → .NET Framework 下 ThreadPool 未处理异常
            //     直接终结进程。
            // 日志是辅助通道，绝不能有杀死主链路的能力；异常只写 Debug，不再上抛。
            try
            {
                _output(message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LogManager] 落地通道异常(已吞掉): " + ex.Message);
            }

            var sink = _uiSink;
            if (sink != null)
            {
                try
                {
                    sink(message);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[LogManager] UI 通道异常(已吞掉): " + ex.Message);
                }
            }
        }
    }
}
