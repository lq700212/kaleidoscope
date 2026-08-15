using System;
using System.Collections.Generic;
using System.Threading;
using Kaleidoscope.Models;

namespace Kaleidoscope.Services
{
    /// <summary>
    /// 气压表数据读取模拟实现：用于开发和演示阶段（DeviceHubConfig.UseMockCommunication=true 时启用），
    /// 模拟真实气压表数据，让新界面没接设备也能先跑通整套 UI 和业务流程。
    /// 【来源】AgingTestSystem.Services.MockBarometerReader 原样移植，配置换成 BarometerConfig。
    ///
    /// 【线程安全】Random 非线程安全，用 _randomLock 保护所有访问（避免多线程访问导致内部状态损坏）。
    /// </summary>
    public class MockBarometerReader : IBarometerReader
    {
        /// <summary>连接状态标志。</summary>
        private bool _isConnected;

        /// <summary>设备配置（由 Connect 方法赋值）。</summary>
        private BarometerConfig _config;

        /// <summary>随机数生成器（Random 非线程安全，用 _randomLock 保护）。</summary>
        private readonly Random _random = new Random();

        /// <summary>随机数生成器的锁对象（保护 _random 的所有访问）。</summary>
        private readonly object _randomLock = new object();

        public bool IsConnected => _isConnected;

        /// <summary>当前实际使用的串口名称（模拟实现：返回配置里填的端口）。</summary>
        public string CurrentPortName => _config?.PortName;

        public event EventHandler<string> OnError;

        public bool Connect(BarometerConfig config)
        {
            _config = config;
            Thread.Sleep(500);   // 模拟连接耗时，让 UI 有"正在连接"反馈
            _isConnected = true;
            return true;
        }

        public void Disconnect()
        {
            _isConnected = false;
        }

        /// <summary>读取单个气压表数据（模拟：85% 概率真空良好，15% 概率真空较差触发报警演示）。</summary>
        public BarometerData ReadData(int deviceId)
        {
            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接");
                return null;
            }

            if (_config == null || deviceId < 1 || deviceId > _config.TotalBarometers)
            {
                OnError?.Invoke(this, $"设备编号 {deviceId} 超出合法范围 [1, {_config?.TotalBarometers ?? 0}]");
                return null;
            }

            int pressureInt;
            int delayStartMin, delayStartSec;
            int delayArriveMin, delayArriveSec;
            bool vacuumPressureInput;
            bool vacuumValveOutput;
            bool carrierPowerOutput;

            lock (_randomLock)
            {
                // 模拟生成气压数据（单位 kPa，与真实读取器/报警阈值一致）：
                // - 85% 概率"真空良好"：-96 ~ -100 kPa（低于报警阈值 -95，不报警），
                //   让"启动运行"后真空正常建立，演示"测试中→老化计时→自动停止"完整流程
                // - 15% 概率"真空较差"：-1 ~ -90 kPa（高于阈值，触发报警联动演示）
                if (_random.Next(100) < 85)
                {
                    pressureInt = -_random.Next(96, 101);
                }
                else
                {
                    pressureInt = -_random.Next(1, 91);
                }

                delayStartMin = _random.Next(0, 30);
                delayStartSec = _random.Next(0, 60);
                delayArriveMin = _random.Next(0, 60);
                delayArriveSec = _random.Next(0, 60);
                vacuumPressureInput = _random.Next(0, 2) == 1;
                vacuumValveOutput = _random.Next(0, 2) == 1;
                carrierPowerOutput = _random.Next(0, 2) == 1;
            }

            return new BarometerData
            {
                DeviceId = deviceId,
                VacuumPressure = pressureInt,
                SerialNumber = $"SN{deviceId:D4}",
                RecipeName = $"配方{deviceId % 5 + 1}",
                // 状态统一由业务层根据测试状态/报警判定来写，Mock 读取器只提供压力数据，
                // 避免随机状态误导 Demo。
                Status = DeviceStatus.Idle,
                DelayTime = new TimeSpan(0, delayStartMin, delayStartSec),
                StartTime = new TimeSpan(0, delayArriveMin, delayArriveSec),
                CollectTime = DateTime.Now,
                InputStatus = new[] { vacuumPressureInput },
                OutputStatus = new[] { vacuumValveOutput, carrierPowerOutput }
            };
        }

        /// <summary>
        /// 批量读取所有气压表数据（逐台调用 ReadData）。
        /// 未连接时返回"全 null 数组"（对齐真实实现：串口断开时返回全 null 数组，
        /// 让业务层逐台循环能累加失败次数并触发"通讯故障"联动）。
        /// </summary>
        public BarometerData[] ReadAllData()
        {
            if (_config == null)
            {
                OnError?.Invoke(this, "未连接，请先调用 Connect 方法");
                return new BarometerData[0];
            }

            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接（等待自动重连）");
                return new BarometerData[_config.TotalBarometers];
            }

            var data = new BarometerData[_config.TotalBarometers];
            for (int i = 0; i < _config.TotalBarometers; i++)
            {
                data[i] = ReadData(i + 1);
            }
            return data;
        }

        /// <summary>模拟写入单台气压表的设备阈值：Mock 无真实设备，固定返回 true（仅保证接口一致）。</summary>
        public bool SetThreshold(int deviceId, decimal thresholdValue)
        {
            // 与 ReadData 一致：未连接 / 越界按失败处理，便于上层验证边界逻辑
            if (!_isConnected || _config == null || deviceId < 1 || deviceId > _config.TotalBarometers)
            {
                OnError?.Invoke(this, $"设备编号 {deviceId} 超出合法范围 [1, {_config?.TotalBarometers ?? 0}]");
                return false;
            }
            return true; // Mock 模拟成功
        }

        /// <summary>模拟批量写入所有气压表的设备阈值：逐台调用 SetThreshold。未连接时返回空字典。</summary>
        public Dictionary<int, bool> SetAllThresholds(decimal thresholdValue)
        {
            var result = new Dictionary<int, bool>();
            if (_config == null || !_isConnected)
            {
                OnError?.Invoke(this, "未连接，请先调用 Connect 方法");
                return result;
            }

            for (int i = 1; i <= _config.TotalBarometers; i++)
            {
                result[i] = SetThreshold(i, thresholdValue);
            }
            return result;
        }

        /// <summary>释放资源（断开连接）。</summary>
        public void Dispose() { Disconnect(); }
    }
}