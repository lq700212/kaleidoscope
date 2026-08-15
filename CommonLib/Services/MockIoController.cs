using System;
using System.Threading;
using CommonLib.Models;

namespace CommonLib.Services
{
    /// <summary>
    /// IO 控制器模拟实现：用于开发和演示阶段（UseMockCommunication=true 时启用），
    /// 模拟 IO 输入输出操作，让新界面没接耦合器也能先跑通 UI 和业务流程。
    /// 【来源】AgingTestSystem.Services.MockIoController 原样移植，配置换成 IoConfig。
    ///
    /// 【IO 点编号规则】（依据现场 IO 分配表）
    /// - 输入点(NPN, X 地址)：1 ~ TotalInputs，物理地址 X000~X107(三菱八进制)
    /// - 输出点(PNP, Y 地址)：TotalInputs+1 ~ TotalInputs+TotalOutputs
    ///   真空电磁阀(内部 TotalInputs+1~+TotalBarometers)：Y000~Y107
    ///   载台上电(内部 TotalInputs+TotalBarometers+1~+2×TotalBarometers)：Y110~Y217
    ///
    /// 【线程安全】Random 非线程安全，用 _randomLock 保护（避免并发访问导致返回 0 或抛异常）。
    /// </summary>
    public class MockIoController : IIoController
    {
        /// <summary>连接状态标志。</summary>
        private bool _isConnected;

        /// <summary>设备配置（由 Connect 方法赋值）。</summary>
        private IoConfig _config;

        /// <summary>存储所有输入点状态（索引 0 对应输入点 1）。</summary>
        private bool[] _inputStates;

        /// <summary>存储所有输出点状态（索引 0 对应输出点 TotalInputs+1）。</summary>
        private bool[] _outputStates;

        /// <summary>随机数生成器（线程安全用 _randomLock 保护）。</summary>
        private readonly Random _random = new Random();

        /// <summary>随机数生成器的锁对象。</summary>
        private readonly object _randomLock = new object();

        public bool IsConnected => _isConnected;

        public event EventHandler<string> OnError;

        public bool Connect(IoConfig config)
        {
            _config = config;

            // 初始化输入状态数组（随机初始值，模拟设备真实上电随机状态）
            _inputStates = new bool[config.TotalInputs];
            lock (_randomLock)
            {
                for (int i = 0; i < config.TotalInputs; i++)
                {
                    _inputStates[i] = _random.Next(0, 2) == 1;
                }
            }

            // 初始化输出状态数组（默认全断开）
            _outputStates = new bool[config.TotalOutputs];

            Thread.Sleep(300);   // 模拟连接耗时，让 UI 有"正在连接"反馈
            _isConnected = true;
            return true;
        }

        public void Disconnect()
        {
            _isConnected = false;
        }

        /// <summary>读取单个输入点状态（模拟：5% 概率翻转，制造肉眼可见的状态变化）。</summary>
        public bool ReadInput(int inputId)
        {
            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接");
                return false;
            }

            if (inputId < 1 || inputId > _config.TotalInputs)
            {
                OnError?.Invoke(this, $"无效的输入点编号: {inputId}");
                return false;
            }

            lock (_randomLock)
            {
                if (_random.Next(0, 100) < 5)
                {
                    _inputStates[inputId - 1] = !_inputStates[inputId - 1];
                }
            }

            return _inputStates[inputId - 1];
        }

        /// <summary>批量读取所有输入点状态（模拟：更新所有输入状态，3% 概率翻转）。</summary>
        public bool[] ReadAllInputs()
        {
            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接");
                return new bool[0];
            }

            lock (_randomLock)
            {
                for (int i = 0; i < _inputStates.Length; i++)
                {
                    if (_random.Next(0, 100) < 3)
                    {
                        _inputStates[i] = !_inputStates[i];
                    }
                }
            }

            // 返回副本，避免外部修改内部状态
            return (bool[])_inputStates.Clone();
        }

        /// <summary>写入单个输出点状态（输出点编号范围动态按配置计算）。</summary>
        public void WriteOutput(int outputId, bool state)
        {
            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接");
                return;
            }

            int outputStart = _config.TotalInputs + 1;
            int outputEnd = _config.TotalInputs + _config.TotalOutputs;

            if (outputId < outputStart || outputId > outputEnd)
            {
                OnError?.Invoke(this, $"无效的输出点编号: {outputId}（合法范围 {outputStart}-{outputEnd}）");
                return;
            }

            _outputStates[outputId - outputStart] = state;
        }

        /// <summary>批量写入多个输出点状态。</summary>
        public void WriteOutputs(int[] outputIds, bool[] states)
        {
            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接");
                return;
            }

            if (outputIds == null || states == null)
            {
                OnError?.Invoke(this, "参数不能为空");
                return;
            }

            if (outputIds.Length != states.Length)
            {
                OnError?.Invoke(this, "输出点编号和状态数量不一致");
                return;
            }

            for (int i = 0; i < outputIds.Length; i++)
            {
                WriteOutput(outputIds[i], states[i]);
            }
        }

        /// <summary>读取单个输出点状态。</summary>
        public bool ReadOutput(int outputId)
        {
            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接");
                return false;
            }

            int outputStart = _config.TotalInputs + 1;
            int outputEnd = _config.TotalInputs + _config.TotalOutputs;

            if (outputId < outputStart || outputId > outputEnd)
            {
                OnError?.Invoke(this, $"无效的输出点编号: {outputId}（合法范围 {outputStart}-{outputEnd}）");
                return false;
            }

            return _outputStates[outputId - outputStart];
        }

        /// <summary>批量读取所有输出点状态（返回副本，避免外部修改内部状态）。</summary>
        public bool[] ReadAllOutputs()
        {
            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接");
                return new bool[0];
            }

            return (bool[])_outputStates.Clone();
        }

        /// <summary>释放资源（断开连接）。</summary>
        public void Dispose() { Disconnect(); }
    }
}