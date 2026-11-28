using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Threading
{
    /// <summary>
    /// 后台执行门面（2026-11-28 AIris 启动卡顿事故沉淀）。
    ///
    /// 【为什么需要这个类】
    /// AIris 启动时窗口"鼠标转圈"假死 30 秒，根因是两处**同步网络 IO 跑在 UI 线程**：
    ///   · HttpDetectionEngine.Load → ProbeAsync(...).GetAwaiter().GetResult()
    ///   · RtspCameraSource.Start → cap.Start(url)（RTSP 握手阻塞到 TCP 超时）
    /// 这类错误的共同形态是"耗时操作没有离开 UI 线程"。本类提供统一出口，
    /// 让"丢后台"成为默认动作，而不是每个开发者各自记得 Task.Run。
    ///
    /// 【为什么不引入第三方线程池】
    /// 调研过主流方案（2026-11-28 实测数据）：
    ///   · dotnet/runtime 自带 ThreadPool：18.5k star，微软维护、每日更新——Task.Run 的底层就是它；
    ///   · Hangfire：10.2k star，但定位是"持久化后台作业调度"（要数据库/服务端），桌面单机过重；
    ///   · SmartThreadPool：509 star，最后更新 2023-11，已停更；
    ///   · PowerThreadPool：.NET Foundation seed 新项目，太年轻。
    /// 结论：线程池本身用 .NET 内置的（最稳、零依赖），本类只补裸 Task.Run 缺的三件事：
    ///   1) **异常必达**：裸 Task 的异常没人 await 就被静默吞掉（线上最难查的一类问题）；
    ///   2) **有界并发**：按 key 共享信号量，防探活/重连/缩略图生成等场景并发风暴；
    ///   3) **UI 封送**：结果回 UI 线程统一走 UiPost，库不依赖 WPF Dispatcher（可跨 UI 框架复用）。
    ///
    /// 【使用纪律（宿主项目应写进 AGENTS.md）】
    ///   · 网络 / 磁盘 / 模型推理 / 相机握手 / 串口等待 —— 一律 Run/RunBounded，禁止在 UI 线程同步等；
    ///   · 禁止 .GetAwaiter().GetResult() / .Wait() / .Result 出现在 UI 线程可达路径；
    ///   · fire-and-forget 必须走 Run（异常进 ErrorSink），不要裸 Task.Run；
    ///   · 后台结果要改 UI 绑定时，用 Post 封送，不要直接改依赖属性。
    ///
    /// 【线程模型】
    ///   Run/RunBounded 的工作体在线程池线程执行；Post 只负责把委托交给宿主注入的 UI 调度器。
    ///   本类不创建任何专用线程，也不管理线程生命周期——线程池伸缩交给 .NET 运行时。
    /// </summary>
    public static class KxBackground
    {
        /// <summary>
        /// 异常出口（宿主启动时注入，如 AppLog.Error）。签名：(任务名, 异常)。
        /// 未注入时写 Trace，绝不静默吞异常。
        /// </summary>
        public static Action<string, Exception> ErrorSink { get; set; }

        /// <summary>
        /// UI 封送委托（宿主注入，如 action => Application.Current.Dispatcher.BeginInvoke(action)）。
        /// 未注入时退回创建时捕获的 SynchronizationContext；两者都没有则直接执行（控制台/测试）。
        /// </summary>
        public static Action<Action> UiPost { get; set; }

        /// <summary>
        /// 有界并发的槽位表：同一个 key 共享一个 SemaphoreSlim。
        /// 容量取**首次调用**传入的 maxConcurrency（后续同 key 调用以先到者为准，
        /// 避免运行期改容量导致已排队任务语义漂移）。
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        /// <summary>
        /// 后台执行一个无返回值的任务（fire-and-forget）。
        /// 【异常语义】工作体抛出的任何异常都会被捕获并送 ErrorSink——调用方不需要 await，
        /// 也不会出现"任务静默失败、界面毫无反应"的黑洞。
        /// </summary>
        /// <param name="name">任务名（日志定位用，建议"模块.动作"，如 "engine.probe"）</param>
        /// <param name="work">工作体（在线程池线程执行，禁止碰 UI 元素）</param>
        public static Task Run(string name, Action work)
        {
            if (work == null) return Task.FromResult(0);
            return Task.Run(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Report(name, ex);
                }
            });
        }

        /// <summary>
        /// 后台执行一个有返回值的任务。
        /// 【异常语义】与 Run 不同：异常会**抛给 await 方**（调用方要结果就必须处理失败），
        /// 适合"取数据 → 再用数据"的链路；纯副作用任务请用 Run。
        /// </summary>
        public static Task<T> RunAsync<T>(string name, Func<T> work)
        {
            if (work == null) return Task.FromResult(default(T));
            return Task.Run(() =>
            {
                try
                {
                    return work();
                }
                catch (Exception ex)
                {
                    Report(name, ex);
                    throw;
                }
            });
        }

        /// <summary>
        /// 有界并发后台执行：同 key 最多同时跑 maxConcurrency 个，超出的**排队**而不是拒绝。
        /// 典型场景：多引擎探活（限 2）、缩略图生成（限 CPU 核数）、设备重连（限 1）。
        /// 【异常语义】同 Run：异常送 ErrorSink，不抛给调用方。
        /// </summary>
        /// <param name="key">并发组名（同组共享槽位）</param>
        /// <param name="maxConcurrency">该组并发上限（&lt;1 按 1 处理）</param>
        public static async Task RunBounded(string key, int maxConcurrency, string name, Action work)
        {
            if (work == null) return;
            if (maxConcurrency < 1) maxConcurrency = 1;

            var gate = Gates.GetOrAdd(key ?? "", k => new SemaphoreSlim(maxConcurrency, maxConcurrency));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception ex)
                    {
                        Report(name, ex);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 把委托封送回 UI 线程执行（后台任务更新界面绑定的唯一正确姿势）。
        /// 优先级：宿主注入的 UiPost → 当前 SynchronizationContext → 直接执行。
        /// </summary>
        public static void Post(Action action)
        {
            if (action == null) return;

            var post = UiPost;
            if (post != null)
            {
                try { post(action); return; }
                catch (Exception ex) { Report("KxBackground.Post", ex); return; }
            }

            var ctx = SynchronizationContext.Current;
            if (ctx != null)
            {
                ctx.Post(delegate { action(); }, null);
                return;
            }

            // 无 UI 上下文（控制台/单元测试）：直接执行，保证语义不退化为"什么都不做"
            action();
        }

        /// <summary>异常统一出口：宿主 sink 优先，未注入时写 Trace（永不静默）。</summary>
        private static void Report(string name, Exception ex)
        {
            var sink = ErrorSink;
            if (sink != null)
            {
                try { sink(name, ex); }
                catch { /* 日志出口自身失败不二次抛，避免递归 */ }
                return;
            }
            Trace.TraceError("[KxBackground] 后台任务失败：" + (name ?? "(未命名)") + " → " + ex);
        }
    }
}
