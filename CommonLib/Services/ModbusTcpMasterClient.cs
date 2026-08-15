using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using CommonLib.Models;
using CommonLib.Utils;
using NModbus;

namespace CommonLib.Services
{
    /// <summary>
    /// 轮询结果事件参数：一次后台轮询读回的一段数据。Name 对应配置的轮询项名称，
    /// Values 为该段数据的原始值（Holding/Input 为 ushort[]，Coil/Discrete 为 bool[] 装箱为 object）。
    /// </summary>
    public class PlcPollEventArgs : EventArgs
    {
        /// <summary>轮询项名称（与 PlcPollItem.Name 一致）</summary>
        public string Name { get; set; }

        /// <summary>读回的原始值：Holding/Input → ushort[]；Coil/Discrete → bool[]</summary>
        public object Values { get; set; }
    }

    /// <summary>
    /// 通用 Modbus TCP 主站客户端（上位机作主站主动读写从站设备）。
    /// 适用场景：上位机主动连 PLC / 远程 IO / 仪表等任意 Modbus TCP 从站，定时轮询 + 按需读写。
    /// 【来源】范式对齐 Aging 的 ModbusTcpIoController（BeginConnect 强制超时 + 锁串行化 + 断连标记），
    /// 抽掉 IO 耦合器特化逻辑，成为通用主站；新增自动轮询与重连节流。
    ///
    /// 【为什么需要它】库里已有"上位机作从站"的 PlcService（监听 502 等 PLC 来读写），
    /// 但有些项目需要"上位机作主站"主动读写 PLC。Modbus 同一协议两种角色，底层都是 NModbus，
    /// 本类提供主站角色 + 通用读写 + 后台轮询 + 自动重连 + 热更支持，让业务层少写代码。
    ///
    /// 【线程安全】_syncRoot 串行化所有对 _client/_master 的访问（重连线程/轮询线程/UI 按钮并发）；
    /// 事件在工作线程触发（OnError/ConnectionChanged/PollDataUpdated），UI 订阅方自行 Invoke。
    ///
    /// 【断连判定】读/写抛 Socket 异常 / IO 异常 / 超时 → 置 _isConnected=false 并触发边沿事件，
    /// 上层感知后由重连节流机制自动恢复（见 EnsureConnected）。Modbus 异常响应不算断开（设备在线）。
    ///
    /// 【热更支持】Dispose 干净（停轮询 + 关连接）；Connect 可反复调用换配置，无状态残留。
    /// </summary>
    public class ModbusTcpMasterClient : IDisposable
    {
        /// <summary>TCP/主站互斥锁：连接、读写、轮询、重连都在锁内串行化。</summary>
        private readonly object _syncRoot = new object();

        /// <summary>全局配置（Connect 时赋值）。</summary>
        private PlcMasterConfig _config;

        /// <summary>TCP 客户端（负责网络连接）。</summary>
        private TcpClient _client;

        /// <summary>Modbus 主站（负责组包/解包、发起请求）。</summary>
        private IModbusMaster _master;

        /// <summary>当前连接状态（true=已连接）。</summary>
        private bool _isConnected;

        /// <summary>上次连接状态（边沿检测用，只在状态变化时发 ConnectionChanged）。</summary>
        private bool _wasConnected;

        /// <summary>上次连接尝试时间（重连节流）。</summary>
        private DateTime _lastConnectAttempt = DateTime.MinValue;

        /// <summary>自动轮询定时器（StartPolling 启动，StopPolling/Dispose 停止）。</summary>
        private System.Threading.Timer _pollTimer;

        /// <summary>最近一次轮询结果缓存：Name → 原始值数组（ushort[] 或 bool[]）。</summary>
        private readonly Dictionary<string, object> _lastPollData = new Dictionary<string, object>();

        private volatile bool _disposed;

        /// <summary>是否已连接（true=可用）。</summary>
        public bool IsConnected => _isConnected;

        /// <summary>当前连接的目标 IP:端口（未连接返回 null）。</summary>
        public string ActiveEndpoint => _client != null && _isConnected
            ? (_config != null ? $"{_config.IpAddress}:{_config.Port}" : null)
            : null;

        /// <summary>连接状态边沿事件（true=连上，false=断开；工作线程触发）。</summary>
        public event EventHandler<bool> ConnectionChanged;

        /// <summary>通讯错误回调（工作线程触发；连接失败/读写失败都走这里）。</summary>
        public event EventHandler<string> OnError;

        /// <summary>后台轮询读回一段数据（Name=轮询项名，Values=原始值数组；工作线程触发）。</summary>
        public event EventHandler<PlcPollEventArgs> PollDataUpdated;

        /// <summary>
        /// 连接目标从站（断开旧连 → BeginConnect 手动超时 → 建 Modbus 主站）。
        /// 设计约定：不向外抛异常，失败通过 OnError 通知并返回 false。
        /// </summary>
        /// <param name="config">主站连接与轮询配置</param>
        /// <returns>true=连接成功</returns>
        public bool Connect(PlcMasterConfig config)
        {
            _config = config;
            lock (_syncRoot)
            {
                return ConnectInternal();
            }
        }

        /// <summary>连接实际执行部分（必须在 _syncRoot 锁内调用）。</summary>
        private bool ConnectInternal()
        {
            try
            {
                // 0) 先断开旧连接（幂等重连/热更换配置时清理旧句柄）
                Disconnect();

                if (_config == null) { OnError?.Invoke(this, "配置为空，无法连接"); return false; }

                // 1) 建立 TCP 连接（带手动超时）：TcpClient.Connect 同步阻塞且不受 Timeout 约束，
                //    IP 填错默认等 ~20s。用 BeginConnect+WaitOne 实现 TimeoutMs 内强制超时，不卡界面。
                var client = new TcpClient();
                client.SendTimeout = _config.TimeoutMs;
                client.ReceiveTimeout = _config.TimeoutMs;

                IAsyncResult connectResult = client.BeginConnect(_config.IpAddress, _config.Port, null, null);
                if (!connectResult.AsyncWaitHandle.WaitOne(_config.TimeoutMs))
                {
                    client.Close();
                    client.Dispose();
                    OnError?.Invoke(this,
                        $"ModbusTCP 主站连接超时（{_config.TimeoutMs}ms）：{_config.IpAddress}:{_config.Port}，请检查 IP/网线");
                    NotifyEdge();
                    return false;
                }
                client.EndConnect(connectResult);

                // 2) 创建 Modbus 主站（Master）
                _client = client;
                var factory = new ModbusFactory();
                _master = factory.CreateMaster(_client);
                _master.Transport.ReadTimeout = _config.TimeoutMs;
                _master.Transport.WriteTimeout = _config.TimeoutMs;

                // 3) 标记连接成功 + 边沿事件
                _isConnected = true;
                NotifyEdge();
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"ModbusTCP 主站连接失败: {ex.Message}");
                Disconnect();
                NotifyEdge();
                return false;
            }
        }

        /// <summary>断开连接（释放 TCP 与主站；锁串行化与 Connect/读写一致）。</summary>
        public void Disconnect()
        {
            lock (_syncRoot)
            {
                _isConnected = false;
                try { if (_client != null) _client.Close(); } catch { /* 拔网线场景吞掉 */ }
                finally
                {
                    if (_client != null) _client.Dispose();
                    _client = null;
                    _master = null;
                }
            }
        }

        /// <summary>
        /// 确保已连接；未连接则按节流尝试自动重连。读写方法内部先调它（操作前自愈）。
        /// 必须在 _syncRoot 锁内调用。
        /// </summary>
        private bool EnsureConnected()
        {
            if (_isConnected && _master != null && _client != null) return true;
            // 重连节流：刚失败过 ReconnectIntervalMs 内不再重试，避免对死设备频繁无效连接
            if ((DateTime.Now - _lastConnectAttempt).TotalMilliseconds < _config.ReconnectIntervalMs)
                return false;
            _lastConnectAttempt = DateTime.Now;
            return ConnectInternal();
        }

        /// <summary>立即重连一次（业务层"重试"按钮等按需场景，不等节流）。</summary>
        public bool ReconnectNow()
        {
            if (_isConnected) return true;
            lock (_syncRoot) { _lastConnectAttempt = DateTime.MinValue; return ConnectInternal(); }
        }

        /// <summary>
        /// 启动后台自动轮询：按配置 PollItems 定时逐项读取，结果发 PollDataUpdated 事件并缓存。
        /// 未配置轮询项或周期≤0 时不启动；重复调用幂等（已启动则忽略）。
        /// 调用后 DeviceHub 会在 Start() 里自动调用（见 DeviceHub.Start）。
        /// </summary>
        public void StartPolling()
        {
            if (_config == null || _config.PollIntervalMs <= 0 ||
                _config.PollItems == null || _config.PollItems.Count == 0)
                return;
            if (_pollTimer != null) return;   // 幂等
            _pollTimer = new System.Threading.Timer(PollTick, null,
                _config.PollIntervalMs, _config.PollIntervalMs);
        }

        /// <summary>停止后台自动轮询（Dispose 自动调用；重复调用安全）。</summary>
        public void StopPolling()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Dispose();
                _pollTimer = null;
            }
        }

        /// <summary>后台轮询一周期：逐项读取并按需自愈重连（重连在锁内由 EnsureConnected 处理）。</summary>
        private void PollTick(object state)
        {
            if (_disposed) return;
            try
            {
                lock (_syncRoot)
                {
                    if (!EnsureConnected()) return;   // 未连接且未到节流：本周期跳过，下周期再试
                    foreach (var item in _config.PollItems)
                    {
                        if (_disposed) return;
                        object values = ReadByFunction(item.Function, item.StartAddress, item.Count);
                        if (values != null)
                        {
                            _lastPollData[item.Name] = values;
                            PollDataUpdated?.Invoke(this,
                                new PlcPollEventArgs { Name = item.Name, Values = values });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 整周期异常兜底（单条读取失败已由 ReadXxx 内部处理并断开，这里兜全局）
                OnError?.Invoke(this, $"自动轮询异常: {ex.Message}");
            }
        }

        /// <summary>按功能码统一读取（Holding/Input → ushort[]，Coil/Discrete → bool[]）。</summary>
        private object ReadByFunction(PlcPollFunction fn, ushort start, ushort count)
        {
            switch (fn)
            {
                case PlcPollFunction.Holding: return ReadHoldingRegisters(start, count);
                case PlcPollFunction.Input: return ReadInputRegisters(start, count);
                case PlcPollFunction.Coil: return ReadCoils(start, count);
                case PlcPollFunction.Discrete: return ReadDiscreteInputs(start, count);
                default: return null;
            }
        }

        // ==================== 通用读写 API（业务层直接调用） ====================

        /// <summary>读保持寄存器（功能码 0x03）。未连接/失败返回 null（并自动断连标记 + 触发边沿）。</summary>
        public ushort[] ReadHoldingRegisters(ushort startAddress, ushort count)
        {
            return ExecuteRegisters(() => _master.ReadHoldingRegisters(_config.UnitId, startAddress, count));
        }

        /// <summary>读输入寄存器（功能码 0x04，只读区）。未连接/失败返回 null。</summary>
        public ushort[] ReadInputRegisters(ushort startAddress, ushort count)
        {
            return ExecuteRegisters(() => _master.ReadInputRegisters(_config.UnitId, startAddress, count));
        }

        /// <summary>读线圈（功能码 0x01）。未连接/失败返回 null。</summary>
        public bool[] ReadCoils(ushort startAddress, ushort count)
        {
            return ExecuteBits(() => _master.ReadCoils(_config.UnitId, startAddress, count));
        }

        /// <summary>读离散输入（功能码 0x02，只读位区）。未连接/失败返回 null。</summary>
        public bool[] ReadDiscreteInputs(ushort startAddress, ushort count)
        {
            return ExecuteBits(() => _master.ReadInputs(_config.UnitId, startAddress, count));
        }

        /// <summary>写单个保持寄存器（功能码 0x06）。成功返回 true。</summary>
        public bool WriteSingleRegister(ushort address, ushort value)
        {
            return Execute(() => _master.WriteSingleRegister(_config.UnitId, address, value));
        }

        /// <summary>写连续保持寄存器（功能码 0x10）。成功返回 true。</summary>
        public bool WriteMultipleRegisters(ushort startAddress, ushort[] values)
        {
            return Execute(() => _master.WriteMultipleRegisters(_config.UnitId, startAddress, values));
        }

        /// <summary>写单个线圈（功能码 0x05）。成功返回 true。</summary>
        public bool WriteSingleCoil(ushort address, bool value)
        {
            return Execute(() => _master.WriteSingleCoil(_config.UnitId, address, value));
        }

        /// <summary>写连续线圈（功能码 0x0F）。成功返回 true。</summary>
        public bool WriteMultipleCoils(ushort startAddress, bool[] values)
        {
            return Execute(() => _master.WriteMultipleCoils(_config.UnitId, startAddress, values));
        }

        /// <summary>读取最近一次轮询缓存数据（按轮询项名称）；未轮询过返回 false。业务层免订阅事件即可取数。</summary>
        public bool TryGetLastPollData(string name, out object values)
        {
            lock (_syncRoot) { return _lastPollData.TryGetValue(name, out values); }
        }

        /// <summary>执行一条"返回 ushort[] 的读请求"公共路径：锁内自愈连接 + 读 + 断连标记。</summary>
        private ushort[] ExecuteRegisters(Func<ushort[]> action)
        {
            try
            {
                lock (_syncRoot)
                {
                    if (!EnsureConnected()) return null;
                    return action();
                }
            }
            catch (Exception ex)
            {
                MarkDisconnectedOnFailure(ex);
                return null;
            }
        }

        /// <summary>执行一条"返回 bool[] 的读请求"公共路径。</summary>
        private bool[] ExecuteBits(Func<bool[]> action)
        {
            try
            {
                lock (_syncRoot)
                {
                    if (!EnsureConnected()) return null;
                    return action();
                }
            }
            catch (Exception ex)
            {
                MarkDisconnectedOnFailure(ex);
                return null;
            }
        }

        /// <summary>执行一条"返回 void 的写请求"公共路径：锁内自愈连接 + 写 + 断连标记。</summary>
        private bool Execute(Action action)
        {
            try
            {
                lock (_syncRoot)
                {
                    if (!EnsureConnected()) return false;
                    action();
                    return true;
                }
            }
            catch (Exception ex)
            {
                MarkDisconnectedOnFailure(ex);
                return false;
            }
        }

        /// <summary>
        /// 连接层异常统一处理：Socket 异常/IO 异常/超时 → 置未连接 + 触发边沿事件（上层自动重连）。
        /// Modbus 异常响应（SlaveException）不算断开——设备在线只是报错，不破坏连接状态。
        /// </summary>
        private void MarkDisconnectedOnFailure(Exception ex)
        {
            if (ex is SocketException || ex is System.IO.IOException || ex is TimeoutException)
            {
                _isConnected = false;
                NotifyEdge();
                OnError?.Invoke(this, $"ModbusTCP 主站通讯失败，已标记断开: {ex.Message}");
            }
        }

        /// <summary>连接状态边沿：只在状态变化时触发一次 ConnectionChanged。</summary>
        private void NotifyEdge()
        {
            if (_wasConnected != _isConnected)
            {
                _wasConnected = _isConnected;
                ConnectionChanged?.Invoke(this, _isConnected);
            }
        }

        /// <summary>释放资源：停轮询 + 断开连接（热更/关窗必调）。</summary>
        public void Dispose()
        {
            _disposed = true;
            StopPolling();
            Disconnect();
        }
    }
}