using System;
using System.Text;
using System.Threading;
using Kaleidoscope.Models;
using Kaleidoscope.Utils;
using HslCommunication;
using HslCommunication.Profinet.AllenBradley;

namespace Kaleidoscope.Services
{
    /// <summary>
    /// 扫码枪服务（EtherNet/IP 版）：扫码枪作 EIP 适配器(Adapter)，上位机作扫描器(Scanner)
    /// 周期读取 Assembly 数据取条码。适用基恩士 SR / 康耐视 DataMan 等刷成 EIP 模式的机型。
    /// 【来源】V1.6.0 集成；连接管理范式对齐库内 ScannerTcpService
    /// （后台线程 + 重连节流 + 边沿事件 + 日志降噪）。
    ///
    /// 【协议要点】扫码枪在 EtherNet/IP 上把读到的条码放在一个 Assembly 实例（T→O 数据区），
    /// 上位机用 CIP 读指令周期拉取。与 TCP 无协议"读到就推"不同：
    /// - Assembly 是【保持型】数据：扫到一条码后会一直保留，直到下一条覆盖；
    /// - 因此必须去重：仅当"条码变化"或"中间出现过空"才上报——读到空数据说明扫码枪已清码，
    ///   清掉去重缓存后，同一条码再次扫到能重新触发（"同码→空→同码"场景，现场实测语义）；
    /// - 条码外的填充字节一般是 0x00，转 ASCII 后统一 Trim 掉。
    ///
    /// 【对接方式】ScanConfig.Mode="Eip"；EipAssemblyAddress=Assembly 实例号（SR/DataMan 常见 "4"）；
    /// EipReadLengthBytes=单次读取字节数（条码最大长度）；EipPollingMs=轮询周期（默认 200ms）。
    /// EIP 模式无需触发指令（扫码枪常开读码），SendTrigger 为空操作返回 true。
    ///
    /// 【线程模型】本类自持一个后台线程：未连接按节流(3s)调 HSL ConnectServer 建连（自带
    /// ConnectTimeOut 强制超时）；已连接按 EipPollingMs 周期轮询 Assembly。读写失败由 HSL 内部
    /// 自动重连一次再试，仍失败则以"最近通讯结果"为准标记断线并触发边沿事件。
    ///
    /// 【热更支持】Dispose 干净（停线程 + 关长连接）；用新 ScanConfig 构造新实例即可生效。
    /// </summary>
    public class ScannerEipService : IScanner
    {
        private readonly ScanConfig _cfg;

        /// <summary>EIP 长连接客户端（HSL CIP；ConnectServer/ConnectClose 管理生命周期）。</summary>
        private AllenBradleyNet _client;

        private readonly object _lock = new object();
        private Thread _thread;
        private DateTime _lastAttempt = DateTime.MinValue;
        private DateTime _lastFailLogAt = DateTime.MinValue;   // 连接失败日志节流（30s）
        private volatile bool _disposed;
        private volatile bool _connected;      // 最近一次 CIP 通讯是否成功（边沿检测驱动状态灯）

        /// <summary>上一次读到的条码（去重缓存：Assembly 保持型，防同一码每周期重复上报）。</summary>
        private string _lastBarcode = "";

        /// <summary>重连节流间隔（毫秒）。</summary>
        private const int ReconnectMs = 3000;

        /// <summary>轮询周期下限（毫秒）：配置过小会被抬到 50，防打爆扫码枪。</summary>
        private const int MinPollingMs = 50;

        /// <summary>扫到一条完整条码的事件（参数为条码文本，工作线程触发，UI 需 Invoke）。</summary>
        public event EventHandler<string> SerialNumberScanned;

        /// <summary>
        /// 连接状态变化事件：true=CIP 通讯恢复，false=通讯失败/断开（边沿检测，状态没变不发）。
        /// 工作线程触发，UI 订阅方需自行 Invoke。
        /// </summary>
        public event EventHandler<bool> ConnectionChanged;

        /// <summary>
        /// 事件安全触发器：订阅方代码抛异常时只记日志、不外抛。
        /// 【为什么必须有】收码/断线事件都在后台线程触发，若订阅者（UI 回调）抛异常会顺着
        /// Invoke 冒泡进 Worker 循环——虽然 Worker 有整圈兜底不会死线程，但每次都走 catch
        /// 重连分支会造成"好端端的连接被反复误断"；把异常在事件边界隔离掉才是治本。
        /// </summary>
        private void RaiseSafe<T>(EventHandler<T> handler, T args, string eventName)
        {
            if (handler == null) return;
            try { handler(this, args); }
            catch (Exception ex) { LogHelper.Warn("扫码枪(EIP) " + eventName + " 订阅者异常(已隔离)：" + ex.Message); }
        }

        /// <param name="cfg">扫码枪配置（Mode 应为 "Eip"，IP/端口/EIP 参数从本配置取）</param>
        public ScannerEipService(ScanConfig cfg) => _cfg = cfg;

        /// <summary>设备名称（"EIP:IP:端口"），供日志与连接指示灯标识这台枪。</summary>
        public string Name => $"EIP:{_cfg.IpAddress}:{_cfg.Port}";

        /// <summary>
        /// 是否已连接（以最近一次 CIP 通讯结果为准）。注意 Assembly 场景没有"空闲即断"的
        /// socket 判活手段，本属性反映的是"最近 ≤ 一个轮询周期前的通讯结论"。
        /// </summary>
        public bool IsOpen => _connected;

        /// <summary>启动后台连接与轮询线程。幂等：重复调用不叠加线程；立即返回 true（连接在后台异步进行）。
        /// 未启用（Enabled=false）直接返回 false 不起线程，与 TCP/串口实现行为对齐。</summary>
        public bool Open()
        {
            if (_disposed || _thread != null) return false;
            if (!_cfg.Enabled) return false;
            _thread = new Thread(Worker) { IsBackground = true, Name = "ScannerEip" };
            _thread.Start();
            LogHelper.Info($"扫码枪(EIP)启动：{_cfg.IpAddress}:{_cfg.Port}（Assembly={_cfg.EipAssemblyAddress}）");
            return true;
        }

        /// <summary>
        /// 后台主循环：未连接 → 按节流建连；已连接 → 按 EipPollingMs 周期读 Assembly 收码。
        /// 单次轮询异常不退出线程（下个周期继续，HSL 内部会自动重建 socket）。
        /// 【整圈兜底】SerialNumberScanned 的订阅方代码若抛异常，绝不能让它顺着事件冒泡把
        /// 本线程带死（否则扫码枪静默失联、状态灯还停在"已连接"）——与 ScannerTcpService
        /// 的 ReadLoop 整体 try/catch 同一防线。
        /// </summary>
        private void Worker()
        {
            while (!_disposed)
            {
                try
                {
                    bool connectedNow;
                    lock (_lock)
                    {
                        connectedNow = EnsureConnected();
                    }

                    if (!connectedNow)
                    {
                        Thread.Sleep(200); // 节流期内歇一下再试
                        continue;
                    }

                    // 一轮收码：周期读 Assembly → 去重 → 触发事件；失败标记断线走重连分支
                    PollOnce();

                    int period = Math.Max(MinPollingMs, _cfg.EipPollingMs);
                    Thread.Sleep(period);
                }
                catch (Exception ex)
                {
                    // 单圈任何异常（含订阅方回调异常）：记日志、标记断线走重连，下圈恢复，线程不退出
                    LogHelper.Warn("扫码枪(EIP)收码循环异常（线程继续运行）：" + ex.Message);
                    SetConnected(false);
                    Thread.Sleep(200);
                }
            }
        }

        /// <summary>确保 CIP 长连接可用；未连接按节流 ConnectServer。必须在 _lock 锁内调用。</summary>
        private bool EnsureConnected()
        {
            if (_disposed) return false;
            if (_client != null && _connected) return true;
            if ((DateTime.Now - _lastAttempt).TotalMilliseconds < ReconnectMs) return false;
            _lastAttempt = DateTime.Now;
            return TryConnect();
        }

        /// <summary>建立 EIP 连接（锁内调用）：建 AllenBradleyNet → ConnectServer（自带超时）。</summary>
        private bool TryConnect()
        {
            try
            {
                if (_client == null)
                {
                    var ab = new AllenBradleyNet(_cfg.IpAddress?.Trim() ?? "", _cfg.Port);
                    ab.ConnectTimeOut = 2000;   // 对齐库铁律：连接强制超时，不可达 IP 不卡线程
                    ab.ReceiveTimeOut = 2000;
                    ab.Slot = 0;                // 扫码枪无机架槽位概念，固定 0
                    _client = ab;
                }
                OperateResult ret = _client.ConnectServer();
                if (ret.IsSuccess)
                {
                    SetConnected(true);
                    LogHelper.Info($"扫码枪(EIP)已连接 {_cfg.IpAddress}:{_cfg.Port}");
                    _lastBarcode = "";          // 重连后清去重缓存：断线期间扫码枪可能已清码/换码
                    return true;
                }
                LogConnectFailure("连接失败：" + ret.Message);
                try { _client.ConnectClose(); } catch { /* 半开连接关闭异常吞掉 */ }
                SetConnected(false);
                return false;
            }
            catch (Exception ex)
            {
                LogConnectFailure("连接异常：" + ex.Message);
                SetConnected(false);
                return false;
            }
        }

        /// <summary>
        /// 轮询一次 Assembly 并按去重语义上报条码（工作线程调用）：
        /// - 成功且非空、与上次不同 → 上报 SerialNumberScanned；
        /// - 成功但为空   → 清去重缓存（扫码枪已清码，允许"同码→空→同码"再次触发）；
        /// - 失败         → 标记断线（触发边沿事件），下一轮 Worker 循环走重连分支。
        /// </summary>
        private void PollOnce()
        {
            OperateResult<byte[]> readRet;
            lock (_lock)
            {
                if (_disposed || _client == null) return;
                ushort len = (ushort)Math.Max(2, _cfg.EipReadLengthBytes);
                readRet = _client.Read(
                    string.IsNullOrWhiteSpace(_cfg.EipAssemblyAddress) ? "4" : _cfg.EipAssemblyAddress.Trim(), len);
            }

            if (readRet == null) return;
            if (!readRet.IsSuccess)
            {
                // 读失败视为链路故障（HSL 内部已自动重试过一次）：标记断线，交给 Worker 重连分支
                MarkDown("读取失败：" + readRet.Message);
                return;
            }

            SetConnected(true);
            // Assembly 定长字节区的填充一般是 0x00，连同帧尾控制字符一起清掉
            string code = Encoding.ASCII.GetString(readRet.Content).Trim('\0', '\r', '\n', ' ', '\t');
            if (code.Length > 0)
            {
                if (code != _lastBarcode)
                {
                    _lastBarcode = code;
                    LogHelper.Info("扫码枪(EIP)收到条码：" + code);
                    RaiseSafe(SerialNumberScanned, code, "SerialNumberScanned");
                }
                // 相同码：保持型数据的正常现象，静默跳过
            }
            else
            {
                _lastBarcode = ""; // 空=扫码枪已清码，清缓存保证同码可再次触发
            }
        }

        /// <summary>
        /// 发送触发指令（实现 IScanner.SendTrigger）：EIP 模式扫码枪常开读码、无需触发指令，
        /// 本实现为空操作直接返回 true（界面"手动触发"按钮点了无副作用）。
        /// </summary>
        public bool SendTrigger() => true;

        /// <summary>更新连接状态（边沿检测：状态没变不发事件）。</summary>
        private void SetConnected(bool value)
        {
            if (_connected == value) return;
            _connected = value;
            if (!value)
                LogHelper.Warn("扫码枪(EIP)连接断开，3s 节流后自动重连");
            RaiseSafe(ConnectionChanged, value, "ConnectionChanged");
        }

        /// <summary>读取失败统一处理：释放坏连接 + 标记断线（边沿事件），等 Worker 下轮重连。</summary>
        private void MarkDown(string reason)
        {
            lock (_lock)
            {
                try { _client?.ConnectClose(); } catch { }
                _client = null;
            }
            bool wasConnected = _connected;
            SetConnected(false);
            if (wasConnected)
                LogHelper.Warn($"扫码枪(EIP){reason}，已标记断开待自动重连");
        }

        /// <summary>连接失败日志（30s 节流：重连期间静默不刷屏，30s 至少留一条根因方向）。</summary>
        private void LogConnectFailure(string kind)
        {
            if ((DateTime.Now - _lastFailLogAt).TotalSeconds < 30) return;
            _lastFailLogAt = DateTime.Now;
            LogHelper.Warn($"扫码枪(EIP){kind} {_cfg.IpAddress}:{_cfg.Port}（3s 节流持续重连）");
        }

        /// <summary>
        /// 释放资源（关窗/热更必调）：置废弃标记 + 锁外强关长连接（让阻塞中的操作尽快失败退出）
        /// + 等线程退出（短超时不阻塞关窗）+ 复位状态。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 对齐 ScannerTcpService 的"限时抢锁 + 锁外强断网"约定：拿不到锁就锁外关连接，
            // Close 会让锁内的 ConnectServer/Read 尽快抛异常退出，不会拖住 UI 关窗线程
            if (Monitor.TryEnter(_lock, TimeSpan.FromMilliseconds(300)))
            {
                try { CloseConn(); }
                finally { Monitor.Exit(_lock); }
            }
            else
            {
                LogHelper.Warn("扫码枪(EIP) Dispose 未能拿到锁（后台任务繁忙），改走锁外强断网");
                CloseConn();
            }
            var t = _thread;
            if (t != null && t != Thread.CurrentThread)
            {
                try { t.Join(500); } catch { }
            }
            SetConnected(false); // 关闭即断开：通知订阅方状态复位（幂等，已 false 则不发事件）
            LogHelper.Info("扫码枪(EIP)已释放");
        }

        /// <summary>关闭 EIP 长连接（幂等）。</summary>
        private void CloseConn()
        {
            try { _client?.ConnectClose(); } catch { }
            _client = null;
        }
    }
}
