using System;
using System.Collections.Generic;
using System.Threading;
using CommonLib.Models;
using CommonLib.Utils;

namespace CommonLib.Services
{
    /// <summary>
    /// 连接健康监控器（从 CommandCenter/Services/ConnectionMonitor.cs 抽取，
    /// 扩展支持气压表 / IO 耦合器 / 送风机三类主站设备 + PLC 主站（ModbusTcpMasterClient）
    /// 的"断连检测 + 节流重连 + 边沿事件"）。
    ///
    /// 【为什么单独建这个类，而不是塞进业务协调器】
    /// 协调器只负责"业务流程"；连接健康是跨流程的系统级事务——程序暂停、关窗、
    /// 设备中途插拔/重启时都要保持自愈能力。独立监控器让"后台静默重连"职责单一。
    ///
    /// 【三个原则（对照成熟心跳机制）】
    /// 1) 心跳 = 后台轮询：相机每 2s socket 级探测；PLC 的业务轮询本身就是心跳，
    ///    这里只负责未连接时的自动重连兜底；气压表/IO/送风机每 2s 检查 IsConnected，
    ///    未连接则按节流（5s）在后台静默重连。
    /// 2) 日志只记边沿：连上/断开各提示一次（带设备名），连续失败的中间过程静默。
    /// 3) 后台静默持续重连：不是"重试几次就放弃"，而是按节流一直重试，
    ///    设备插上/恢复后几秒内自动连回，全程不影响 UI 刷新。
    ///
    /// 【PLC 主站/从站两模式（V1.2.0）】
    /// 构造参数 plc 与 plcMaster 二选一（另一传 null）：plc 是 PlcService（从站，监听 502），
    /// plcMaster 是 ModbusTcpMasterClient（主站，主动连 PLC）。两模式都在本监控器内做
    /// 未连接时的节流重连：从站调 EnsureConnected 重启监听，主站调 Connect(cfg) 重连。
    /// 主站模式还向外发 PlcMasterConnectionChanged 边沿事件（DeviceHub 聚合到 Plc 灯位）。
    ///
    /// 【线程安全】
    /// - 心跳周期在 System.Threading.Timer 后台线程执行（绝不碰 UI 线程）；
    /// - 重连动作放 Task.Run 后台：TCP/串口连接最坏耗一个 TimeoutMs，绝不阻塞主链路与本周期；
    /// - 对底层服务的调用，服务层已自带锁 + 日志降噪 + 状态边沿事件，本项目安全。
    /// - 气压表/IO/送风机三种设备没有 ConnectionChanged 事件（接口只暴露 IsConnected 属性），
    ///   因此本监控器通过"缓存上次状态 + 每次心跳比较"自行产生边沿，并对外发
    ///   BarometerConnectionChanged / IoConnectionChanged / FanConnectionChanged 事件
    ///   （DeviceHub 聚合转发给 UI 指示灯）。
    ///
    /// 【热更支持】本监控器持有关闭（注入）的服务实例；热更重建服务后，
    /// 用新服务实例重新构造/重新 Start 即可（Dispose 会退订事件并停掉心跳）。
    /// </summary>
    public class ConnectionMonitor : IDisposable
    {
        private readonly PlcService _plc;
        private readonly ModbusTcpMasterClient _plcMaster;
        private readonly PlcMasterConfig _plcMasterConfig;
        private readonly List<KeyenceIV4Camera> _cameras; // 多台相机各自心跳/重连

        // ============ 三类主站设备（Aging 型，可选注入；不注入则不监控） ============
        private readonly IBarometerReader _barometer;
        private readonly BarometerConfig _baroConfig;
        private readonly IIoController _io;
        private readonly IoConfig _ioConfig;
        private readonly IFanController _fan;
        private readonly FanConfig _fanConfig;

        /// <summary>心跳周期（毫秒）：相机 socket 探测 + 未连接节流重连的决策节奏</summary>
        private const int HeartbeatMs = 2000;

        /// <summary>对已断开设备的重连节流间隔（毫秒）：避免高频无效连接占资源</summary>
        private const int ReconnectThrottleMs = 5000;

        private readonly System.Threading.Timer _timer;

        /// <summary>相机/PLC 上次发起重连的时间（节流用，后台线程读写，无需锁——最坏损失一次重连）</summary>
        private readonly DateTime[] _lastCameraAttempt;
        private DateTime _lastPlcAttempt = DateTime.MinValue;
        private DateTime _lastPlcMasterAttempt = DateTime.MinValue;
        private DateTime _lastBaroAttempt = DateTime.MinValue;
        private DateTime _lastIoAttempt = DateTime.MinValue;
        private DateTime _lastFanAttempt = DateTime.MinValue;

        /// <summary>PLC 主站（ModbusTcpMasterClient）与三类设备上次的连接状态（边沿检测用，见类注释）</summary>
        private bool _wasPlcMasterConnected;
        private bool _wasBaroConnected;
        private bool _wasIoConnected;
        private bool _wasFanConnected;

        private volatile bool _disposed;

        // ============ PLC 主站与三类设备边沿事件（DeviceHub 聚合转发给 UI） ============

        /// <summary>PLC 主站（ModbusTcpMasterClient）连接状态边沿事件（true=连上，false=断开；工作线程触发）。</summary>
        public event EventHandler<bool> PlcMasterConnectionChanged;

        /// <summary>气压表连接状态边沿事件（true=连上，false=断开；工作线程触发）。</summary>
        public event EventHandler<bool> BarometerConnectionChanged;

        /// <summary>IO 耦合器连接状态边沿事件（true=连上，false=断开；工作线程触发）。</summary>
        public event EventHandler<bool> IoConnectionChanged;

        /// <summary>送风机连接状态边沿事件（true=连上，false=断开；工作线程触发）。</summary>
        public event EventHandler<bool> FanConnectionChanged;

        /// <summary>
        /// 构造监控器：注入需要监控的设备。PLC 从站或 PLC 主站二选一（另一传 null）；相机必填；
        /// 气压表/IO/送风机为可选项（传 null 则不做该类设备的监控，多设备场景可建多个监控器）。
        /// </summary>
        /// <param name="plc">PLC 从站服务（PlcRole.Slave 时传入，主站模式传 null）</param>
        /// <param name="cameras">相机服务列表</param>
        /// <param name="plcMaster">PLC 主站服务 ModbusTcpMasterClient（PlcRole.Master 时传入，从站模式传 null）</param>
        /// <param name="plcMasterConfig">PLC 主站配置（plcMaster 非空时必填，用于重连 Connect(cfg)）</param>
        /// <param name="barometer">气压表服务（可 null）</param>
        /// <param name="baroConfig">气压表配置（barometer 非空时必填，用于重连 Connect(cfg)）</param>
        /// <param name="io">IO 耦合器服务（可 null）</param>
        /// <param name="ioConfig">IO 配置（io 非空时必填）</param>
        /// <param name="fan">送风机服务（可 null）</param>
        /// <param name="fanConfig">送风机配置（fan 非空时必填）</param>
        public ConnectionMonitor(PlcService plc, List<KeyenceIV4Camera> cameras,
            ModbusTcpMasterClient plcMaster = null, PlcMasterConfig plcMasterConfig = null,
            IBarometerReader barometer = null, BarometerConfig baroConfig = null,
            IIoController io = null, IoConfig ioConfig = null,
            IFanController fan = null, FanConfig fanConfig = null)
        {
            _plc = plc;
            _plcMaster = plcMaster;
            _plcMasterConfig = plcMasterConfig;
            _cameras = cameras;
            _barometer = barometer;
            _baroConfig = baroConfig;
            _io = io;
            _ioConfig = ioConfig;
            _fan = fan;
            _fanConfig = fanConfig;
            _lastCameraAttempt = new DateTime[Math.Max(1, cameras.Count)];

            // 记录 PLC 主站与三类设备初始连接状态，作为边沿检测基准
            _wasPlcMasterConnected = _plcMaster?.IsConnected ?? false;
            _wasBaroConnected = _barometer?.IsConnected ?? false;
            _wasIoConnected = _io?.IsConnected ?? false;
            _wasFanConnected = _fan?.IsConnected ?? false;

            // 订阅 PLC 从站与各相机的连接状态边沿事件（SetConnected 只在状态变化时触发一次）
            if (_plc != null)
                _plc.ConnectionChanged += OnPlcConnectionChanged;
            foreach (var cam in _cameras)
                cam.ConnectionChanged += OnCameraConnectionChanged;

            // 心跳定时器：先不跑（Infinite），Start() 里点火
            _timer = new System.Threading.Timer(Tick, null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        /// <summary>启动心跳与自动重连（紧跟在服务创建后调用）。</summary>
        public void Start()
        {
            _timer.Change(0, HeartbeatMs); // 立即首轮，之后每 2s
        }

        /// <summary>
        /// 心跳周期主循环（后台线程）。
        /// - 相机已连接 → 做 socket 级心跳探测；未连接 → 按节流后台静默重连；
        /// - PLC 从站监听未就绪 → 按节流后台重启监听；
        /// - 气压表/IO/送风机未连接 → 按节流后台静默重连，并触发边沿事件。
        /// </summary>
        private void Tick(object state)
        {
            if (_disposed) return;

            // ===== 每台相机各自心跳/重连 =====
            for (int i = 0; i < _cameras.Count; i++)
            {
                var cam = _cameras[i];
                if (cam.IsConnected)
                {
                    // 心跳：纯 socket 探测，不打扰拍摄；探测发现对端已关连接会由服务自动标记断开
                    cam.CheckConnection();
                }
                else if ((DateTime.Now - _lastCameraAttempt[i]).TotalMilliseconds >= ReconnectThrottleMs)
                {
                    _lastCameraAttempt[i] = DateTime.Now;
                    int idx = i; // 闭包锁副本
                    System.Threading.Tasks.Task.Run(() => _cameras[idx].EnsureConnected()); // 后台重连，不阻塞本周期
                }
            }

            // ===== PLC 从站（PlcRole.Slave 模式，_plc 非空）=====
            // 已就绪无须处理：业务层每 200ms 轮询"到位信号"（读自己 DataStore）本身就是心跳；
            // 从站监听未就绪则按节流后台重启监听（EnsureConnected 启动监听，端口占用/异常恢复后自动就绪）。
            if (_plc != null && !_plc.IsConnected &&
                (DateTime.Now - _lastPlcAttempt).TotalMilliseconds >= ReconnectThrottleMs)
            {
                _lastPlcAttempt = DateTime.Now;
                System.Threading.Tasks.Task.Run(() => _plc.EnsureConnected());
            }

            // ===== PLC 主站（PlcRole.Master 模式，_plcMaster 非空）=====
            // 边沿检测 + 节流重连，逻辑同下方三类主站设备；ModbusTcpMasterClient 内部轮询
            // 自带断连标记，这里只负责"断→按节流重连"，连上后由轮询自己维持连接。
            if (_plcMaster != null)
            {
                if (_plcMaster.IsConnected)
                {
                    if (!_wasPlcMasterConnected)
                    {
                        _wasPlcMasterConnected = true;
                        LogHelper.Info("PLC 已连接，通讯恢复正常");
                        PlcMasterConnectionChanged?.Invoke(this, true);
                    }
                }
                else
                {
                    if (_wasPlcMasterConnected)
                    {
                        _wasPlcMasterConnected = false;
                        LogHelper.Warn("PLC 通讯中断，后台将持续自动重连…");
                        PlcMasterConnectionChanged?.Invoke(this, false);
                    }
                    if ((DateTime.Now - _lastPlcMasterAttempt).TotalMilliseconds >= ReconnectThrottleMs)
                    {
                        _lastPlcMasterAttempt = DateTime.Now;
                        var svc = _plcMaster; var cfg = _plcMasterConfig;
                        System.Threading.Tasks.Task.Run(() => { if (cfg != null) svc.Connect(cfg); });
                    }
                }
            }

            // ===== 气压表 / IO 耦合器 / 送风机（主站设备，边沿检测 + 节流重连）=====

            // 气压表：未连接 → 按节流后台重连；状态变化 → 边沿事件 + 边沿日志
            if (_barometer != null)
            {
                if (_barometer.IsConnected)
                {
                    if (!_wasBaroConnected)
                    {
                        _wasBaroConnected = true;
                        LogHelper.Info("气压表已连接，通讯恢复正常");
                        BarometerConnectionChanged?.Invoke(this, true);
                    }
                }
                else
                {
                    if (_wasBaroConnected)
                    {
                        _wasBaroConnected = false;
                        LogHelper.Warn("气压表通讯中断，后台将持续自动重连…");
                        BarometerConnectionChanged?.Invoke(this, false);
                    }
                    if ((DateTime.Now - _lastBaroAttempt).TotalMilliseconds >= ReconnectThrottleMs)
                    {
                        _lastBaroAttempt = DateTime.Now;
                        var svc = _barometer; var cfg = _baroConfig;
                        System.Threading.Tasks.Task.Run(() => { if (cfg != null) svc.Connect(cfg); });
                    }
                }
            }

            // IO 耦合器：同上
            if (_io != null)
            {
                if (_io.IsConnected)
                {
                    if (!_wasIoConnected)
                    {
                        _wasIoConnected = true;
                        LogHelper.Info("IO 耦合器已连接，通讯恢复正常");
                        IoConnectionChanged?.Invoke(this, true);
                    }
                }
                else
                {
                    if (_wasIoConnected)
                    {
                        _wasIoConnected = false;
                        LogHelper.Warn("IO 耦合器通讯中断，后台将持续自动重连…");
                        IoConnectionChanged?.Invoke(this, false);
                    }
                    if ((DateTime.Now - _lastIoAttempt).TotalMilliseconds >= ReconnectThrottleMs)
                    {
                        _lastIoAttempt = DateTime.Now;
                        var svc = _io; var cfg = _ioConfig;
                        System.Threading.Tasks.Task.Run(() => { if (cfg != null) svc.Connect(cfg); });
                    }
                }
            }

            // 送风机：同上（其内部还有自身 10s 重连节流，与监控器 5s 节流叠加，互不冲突）
            if (_fan != null)
            {
                if (_fan.IsConnected)
                {
                    if (!_wasFanConnected)
                    {
                        _wasFanConnected = true;
                        LogHelper.Info("送风机已连接，通讯恢复正常");
                        FanConnectionChanged?.Invoke(this, true);
                    }
                }
                else
                {
                    if (_wasFanConnected)
                    {
                        _wasFanConnected = false;
                        LogHelper.Warn("送风机通讯中断，后台将持续自动重连…");
                        FanConnectionChanged?.Invoke(this, false);
                    }
                    if ((DateTime.Now - _lastFanAttempt).TotalMilliseconds >= ReconnectThrottleMs)
                    {
                        _lastFanAttempt = DateTime.Now;
                        var svc = _fan; var cfg = _fanConfig;
                        System.Threading.Tasks.Task.Run(() => { if (cfg != null) svc.Connect(cfg); });
                    }
                }
            }
        }

        // ===== 边沿日志：事件只在"状态变化"时触发，刚好对应一次明确的断连/恢复 =====

        private void OnPlcConnectionChanged(object sender, bool connected)
        {
            if (connected)
                LogHelper.Info("PLC 已连接，通讯恢复正常");
            else
                LogHelper.Warn("PLC 通讯中断，后台将持续自动重连…");
        }

        private void OnCameraConnectionChanged(object sender, bool connected)
        {
            // sender 具体是哪台，日志带 IP 方便区分（多相机）
            var cam = sender as KeyenceIV4Camera;
            string who = cam != null ? cam.IpLabel : "相机";
            if (connected)
                LogHelper.Info($"{who} 已连接，通讯恢复正常");
            else
                LogHelper.Warn($"{who} 通讯中断，后台将持续自动重连…");
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _timer.Dispose();
            if (_plc != null)
                _plc.ConnectionChanged -= OnPlcConnectionChanged;
            foreach (var cam in _cameras)
                cam.ConnectionChanged -= OnCameraConnectionChanged;
        }
    }
}