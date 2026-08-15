using System;
using System.Collections.Generic;
using Kaleidoscope.Models;

namespace Kaleidoscope.Services
{
    /// <summary>
    /// 气压表数据读取接口（真实 / Mock 共用同一声明，业务层只依赖本接口）。
    ///
    /// 【协议口径（已定论，现场实测）】
    /// - 读压力：Input Register 0x0001（功能码 0x04），同时读 0x0002 取小数位（不可靠，换算固定用配置小数位）
    /// - 写阈值：Holding Register 0x0010（功能码 0x06），值 = round(阈值 × 10^小数位)
    /// 真实实现 <see cref="ModbusRtuBarometerReader"/>；模拟实现 <see cref="MockBarometerReader"/>
    /// （DeviceHubConfig.UseMockCommunication=true 时使用）。
    ///
    /// 【线程说明】实现类必须保证线程安全：SerialPort/Modbus Master 不支持并发读写，
    /// 真实实现用 _syncRoot 互斥锁串行化所有请求（含 SetThreshold）。
    ///
    /// 【接入要点】由 DeviceHub 创建并持有，业务层通过 hub.Barometer 属性拿到实例：
    ///   定时采集：读 <see cref="ReadAllData"/>（未连接时返回"全 null 数组"，便于业务层累加失败数触发联动）；
    ///   写设备阈值：<see cref="SetAllThresholds"/>（72 台连写 + 坏设备会阻塞较久，务必后台线程调用）。
    /// </summary>
    public interface IBarometerReader : IDisposable
    {
        /// <summary>连接状态：true=已连接，false=未连接（串口级故障会自动置 false 等待自动重连）。</summary>
        bool IsConnected { get; }

        /// <summary>当前实际使用的串口名称（自动识别或配置的结果；未连接时为空），供日志/诊断显示。</summary>
        string CurrentPortName { get; }

        /// <summary>连接气压表设备（内部自动：断开旧连→按"缓存端口→配置端口→CH340 识别"顺序尝试）。</summary>
        /// <param name="config">气压表配置</param>
        /// <returns>是否连接成功（失败通过 OnError 通知）</returns>
        bool Connect(BarometerConfig config);

        /// <summary>断开连接（释放串口与主站对象，连接状态置 false）。</summary>
        void Disconnect();

        /// <summary>读取单个气压表数据（失败返回 null，不抛异常；串口级故障自动标记断开）。</summary>
        /// <param name="deviceId">气压表编号（1~TotalBarometers，即 Modbus 从站地址）</param>
        /// <returns>气压表数据；读取失败返回 null</returns>
        BarometerData ReadData(int deviceId);

        /// <summary>
        /// 批量读取所有气压表数据（逐台调用 ReadData，单台失败不影响其它台）。
        /// 未连接时返回"全 null 数组"（长度=TotalBarometers），让业务层逐台循环仍能累加失败次数、
        /// 触发通讯故障联动（关阀+断电）的安全兜底。
        /// </summary>
        /// <returns>包含所有气压表数据的数组</returns>
        BarometerData[] ReadAllData();

        /// <summary>
        /// 写入单台气压表的设备阈值（Holding Register 0x0010）。
        /// 【单位说明】thresholdValue 是"设备单位"（与压力读数同单位同小数位，寄存器值=round(阈值×10^小数位)），
        /// 不等于软件报警阈值 AlarmPressureThresholdKPa。写进设备内部、驱动硬件报警触点（→GX-CL140 的 DI）。
        /// </summary>
        /// <param name="deviceId">气压表编号（1~TotalBarometers）</param>
        /// <param name="thresholdValue">设备单位阈值（如 -95.0）</param>
        /// <returns>是否写入成功（设备不响应返回 false，不抛异常）</returns>
        bool SetThreshold(int deviceId, decimal thresholdValue);

        /// <summary>
        /// 批量写入所有气压表的设备阈值：逐台调用 SetThreshold，单台失败不影响其它台。
        /// 返回 deviceId→是否成功。72 台连写 + 坏设备会阻塞较久（每台坏设备约一个读超时），
        /// 调用方应在后台线程执行。未连接时返回空字典（让上层走"未连接"提示分支）。
        /// </summary>
        /// <param name="thresholdValue">设备单位阈值（与压力读数同单位同小数位）</param>
        /// <returns>写入结果字典（deviceId → 是否成功）</returns>
        Dictionary<int, bool> SetAllThresholds(decimal thresholdValue);

        /// <summary>通讯错误事件（连接失败、读写失败等；工作线程触发，UI 订阅方需自行 Invoke）。</summary>
        event EventHandler<string> OnError;
    }
}
