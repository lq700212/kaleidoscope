using System;
using System.Threading;
using CommonLib.Models;

namespace CommonLib.Services
{
    /// <summary>
    /// 冷却送风机模拟实现：用于开发和演示阶段（DeviceHubConfig.UseMockCommunication=true 时启用）。
    /// 【来源】AgingTestSystem.Services.MockFanController 原样移植，配置换成 FanConfig。
    ///
    /// 【与真实实现的区别】
    /// - 真实实现 FanControllerClient：走 Modbus TCP，与厂商控制屏通讯
    /// - 本模拟实现：不连任何硬件，温度随机波动，命令直接生效
    ///
    /// 【设计说明】有了 Mock，即使现场没有接线、没有送风机，也可以先跑通整套 UI 和业务流程：
    /// 点"送风机定值启动" → 状态变"定值运行中" → 温度开始波动；点"定值停止" → 状态变"已停止"。
    ///
    /// 【线程安全】Random 非线程安全，用 _randomLock 保护（与 MockIoController 一致）。
    /// </summary>
    public class MockFanController : IFanController
    {
        /// <summary>连接状态标志。</summary>
        private bool _isConnected;

        /// <summary>设备配置（由 Connect 方法赋值）。</summary>
        private FanConfig _config;

        /// <summary>模拟"运行中"标志：true=定值启动后正在运行，false=已停止。</summary>
        private bool _running;

        /// <summary>模拟当前温度（°C）：起始 35°C（老化箱常见温度），运行后在其附近随机波动。</summary>
        private float _currentTemp = 35f;

        /// <summary>模拟当前湿度（%RH）。</summary>
        private float _currentHumidity = 60f;

        /// <summary>温度设定值（°C，模拟值；真实设备由厂商控制屏设定）。</summary>
        private float _tempSetpoint = 35f;

        /// <summary>湿度设定值（%RH，模拟值）。</summary>
        private float _humiditySetpoint = 60f;

        /// <summary>随机数生成器（线程安全用 _randomLock 保护）。</summary>
        private readonly Random _random = new Random();

        /// <summary>随机数生成器的锁对象。</summary>
        private readonly object _randomLock = new object();

        public bool IsConnected => _isConnected;

        /// <summary>实际连接成功的送风机 IP（模拟实现：无真实设备，返回配置里的主 IP）。</summary>
        public string ActiveIp => _config?.FanIpAddress;

        public event EventHandler<string> OnError;

        public bool Connect(FanConfig config)
        {
            _config = config;
            Thread.Sleep(200);   // 模拟连接耗时，让 UI 有"正在连接"反馈
            _isConnected = true;
            return true;
        }

        public void Disconnect()
        {
            _isConnected = false;
        }

        /// <summary>按需重连（模拟实现：直接返回"已连接"，Mock 没有真实掉线概念，重新 Connect 即恢复）。</summary>
        public bool ReconnectNow()
        {
            if (_isConnected) return true;
            Connect(_config);
            return _isConnected;
        }

        /// <summary>读取送风机当前状态（模拟：温度在运行态围绕设定值小幅波动，更接近真实设备表现）。</summary>
        public FanData ReadStatus()
        {
            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接");
                return null;
            }

            lock (_randomLock)
            {
                // 运行中：温度围绕设定值波动 ±0.3°C；停止时：温度缓慢向室温（30°C）方向回归
                if (_running)
                {
                    _currentTemp += (float)(_random.NextDouble() - 0.5) * 0.6f;
                }
                else
                {
                    _currentTemp += (30f - _currentTemp) * 0.05f;
                }
                // 湿度小幅波动
                _currentHumidity += (float)(_random.NextDouble() - 0.5) * 0.4f;
            }

            return new FanData
            {
                RunState = _running ? FanRunState.FixedValueRunning : FanRunState.FixedValueStopped,
                Temperature = _currentTemp,
                Humidity = _currentHumidity,
                TempSetpoint = _tempSetpoint,
                HumSetpoint = _humiditySetpoint,
                IsOnline = true,
                CollectTime = DateTime.Now
            };
        }

        /// <summary>定值启动（模拟：直接把运行标志置为 true）。</summary>
        public bool StartFixedValue()
        {
            _running = true;
            return true;
        }

        /// <summary>定值停止（模拟：直接把运行标志置为 false）。</summary>
        public bool Stop()
        {
            _running = false;
            return true;
        }

        /// <summary>释放资源（断开连接）。</summary>
        public void Dispose()
        {
            Disconnect();
        }
    }
}