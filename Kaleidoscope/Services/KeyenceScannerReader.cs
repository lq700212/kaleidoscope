using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 基恩士 SR 扫码枪 → ICodeReader 适配器（2026-10-25 与 ICodeReader 同轮新增）。
    // -------------------------------------------------------------------------------------
    // 【是什么】把 IScanner（被动事件流：Open 后枪自动连续读码，SerialNumberScanned
    // 逐条上报）包成 ICodeReader（触发式：一次 AcquireAsync 换一次读码结果）。
    // 【怎么适配】SR 家族的枪开起来就自动连续读码，软件层没有"再打一轮"的重试语义
    // （枪端自己连打），所以单次/持续两种请求统一映射为"SendTrigger（手动补一发激光
    // 指令，测试页同款）→ 等下一条 SerialNumberScanned 上报或超时"：
    //   - 等到上报 → Success true + Code（NoRead 判定：SR 家族枪不上报空串，超时兜底）；
    //   - 超时/取消 → Success false + Error 文案（业务判 NG）。
    // 【单飞行者】同一时刻只挂一个等待者（_pendingCode），后发 Acquire 会覆盖先发
    // 的等待者（先发的按超时收尾）——与 IDMVS 实现的"业务侧串行调 Acquire"口径一致，
    // 接口契约本就要求调用方一次只飞一枪。
    // 【生命周期】枪的 Open/Dispose 归宿主（DeviceHub/业务装配）管，本适配器只订阅
    // 事件不拥有连接；Dispose 仅摘事件（枪本体不动，可继续走事件流模式）。
    // =====================================================================================
    public sealed class KeyenceScannerReader : ICodeReader
    {
        private readonly IScanner _scanner;
        private readonly object _waitLock = new object();
        private TaskCompletionSource<string> _pendingCode;

        /// <summary>构造（scanner 为被包裹的基恩士 SR 家族实例，串口/TCP/EIP 任一实现均可）。</summary>
        public KeyenceScannerReader(IScanner scanner)
        {
            if (scanner == null) throw new ArgumentNullException("scanner");
            _scanner = scanner;
            _scanner.SerialNumberScanned += OnScanned;
            _scanner.ConnectionChanged += OnConnChanged;
        }

        public string Name
        {
            get { try { return _scanner.Name; } catch { return "(scanner)"; } }
        }

        public bool IsConnected
        {
            get { try { return _scanner.IsOpen; } catch { return false; } }
        }

        public event EventHandler<bool> ConnectionChanged;

        private void OnScanned(object sender, string code)
        {
            TaskCompletionSource<string> t;
            lock (_waitLock)
            {
                t = _pendingCode;
                _pendingCode = null;
            }
            if (t != null) t.TrySetResult(code ?? "");
        }

        private void OnConnChanged(object sender, bool connected)
        {
            EventHandler<bool> h = ConnectionChanged;
            if (h != null)
            {
                try { h(this, connected); }
                catch { }
            }
        }

        public async Task<ReaderAcquireResult> AcquireAsync(CodeReadRequest request, CancellationToken ct)
        {
            // 未开枪先补一次 Open（幂等：已开直过；开不了快速失败，业务判 NG）。
            try
            {
                if (!_scanner.IsOpen && !_scanner.Open())
                    return Fail("扫码枪打开失败（" + Name + "，详见枪端日志）");
            }
            catch (Exception ex)
            {
                return Fail("扫码枪打开异常：" + ex.Message);
            }

            // 超时预算：持续模式取 ContinuousMs，单次模式取 TimeoutMs，都非法回落 8000
            // （与 IDMVS 实现的缺省预算同值，两族超时口径对齐）。
            int timeoutMs = request == null ? 0
                : (request.Continuous ? request.ContinuousMs : request.TimeoutMs);
            if (timeoutMs <= 0) timeoutMs = 8000;

            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waitLock) { _pendingCode = tcs; }
            try
            {
                _scanner.SendTrigger();   // 手动补一发激光指令（测试页同款；枪自动读码时它只是保险）
                Task winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct));
                if (winner == tcs.Task)
                {
                    string code = (tcs.Task.Result ?? "").Trim();
                    if (code.Length == 0) return Fail("读码超时（" + timeoutMs + "ms 无上报）");
                    return new ReaderAcquireResult { Success = true, Code = code, RawFrame = code };
                }
                if (ct.IsCancellationRequested) return Fail("读码被取消");
                return Fail("读码超时（" + timeoutMs + "ms 无上报）");
            }
            finally
            {
                lock (_waitLock)
                {
                    if (_pendingCode == tcs) _pendingCode = null;
                }
            }
        }

        private static ReaderAcquireResult Fail(string error)
        {
            return new ReaderAcquireResult { Success = false, Error = error };
        }

        public void Dispose()
        {
            // 只摘事件不碰枪（枪本体归宿主管，见类注释【生命周期】）。
            try { _scanner.SerialNumberScanned -= OnScanned; } catch { }
            try { _scanner.ConnectionChanged -= OnConnChanged; } catch { }
            lock (_waitLock)
            {
                if (_pendingCode != null)
                {
                    _pendingCode.TrySetCanceled();
                    _pendingCode = null;
                }
            }
        }
    }
}
