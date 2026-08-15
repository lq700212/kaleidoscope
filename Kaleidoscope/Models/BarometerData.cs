using System;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// 气压表实时数据模型。
    /// 【来源】AgingTestSystem.Models.BarometerData 原样移植（Kaleidoscope 独立于业务项目，
    /// 数据模型字段与 Aging 保持一致，换新界面接入时数据口径不变）。
    /// </summary>
    public class BarometerData
    {
        /// <summary>气压表编号（从 1 开始，即 Modbus 从站地址）。</summary>
        public int DeviceId { get; set; }

        /// <summary>真空压力值（单位：kPa，与气压表读数一致；早期版本用 Pa，V1.19.9 起为 kPa）。</summary>
        public decimal VacuumPressure { get; set; }

        /// <summary>设备序列号（扫码/绑定结果，业务层填充）。</summary>
        public string SerialNumber { get; set; }

        /// <summary>当前使用的配方名称（业务层填充）。</summary>
        public string RecipeName { get; set; }

        /// <summary>设备状态枚举：空闲/测试中/故障（通讯层只标 Idle/Fault 的报警基础判定，业务层覆盖）。</summary>
        public DeviceStatus Status { get; set; }

        /// <summary>延时开启时间（时:分:秒，业务层填充）。</summary>
        public TimeSpan DelayTime { get; set; }

        /// <summary>延时到达时间（时:分:秒，业务层填充）。</summary>
        public TimeSpan StartTime { get; set; }

        /// <summary>采集时间戳。</summary>
        public DateTime CollectTime { get; set; }

        /// <summary>IO 输入状态列表（每个气压表 1 个输入：真空负压表信号，索引 0）。</summary>
        public bool[] InputStatus { get; set; } = new bool[1];

        /// <summary>IO 输出状态列表（每个气压表 2 个输出：索引 0=真空电磁阀、索引 1=载台上电）。</summary>
        public bool[] OutputStatus { get; set; } = new bool[2];

        /// <summary>深拷贝：业务层缓存数据对外返回副本，避免外部修改污染缓存；数组字段一并复制。</summary>
        public BarometerData Clone()
        {
            return new BarometerData
            {
                DeviceId = this.DeviceId,
                VacuumPressure = this.VacuumPressure,
                SerialNumber = this.SerialNumber,
                RecipeName = this.RecipeName,
                Status = this.Status,
                DelayTime = this.DelayTime,
                StartTime = this.StartTime,
                CollectTime = this.CollectTime,
                InputStatus = (bool[])this.InputStatus?.Clone(),
                OutputStatus = (bool[])this.OutputStatus?.Clone()
            };
        }
    }

    /// <summary>设备运行状态枚举。</summary>
    public enum DeviceStatus
    {
        /// <summary>空闲状态</summary>
        Idle,

        /// <summary>测试中</summary>
        Testing,

        /// <summary>故障状态</summary>
        Fault
    }
}
