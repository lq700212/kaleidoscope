using System;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// 冷却送风机实时数据模型。
    /// 【来源】AgingTestSystem.Models.FanData 原样移植。数据来自厂商控制屏（Modbus TCP），
    /// 寄存器映射以 ModbusTCPFanControllerTest Demo 实测为准（见 FanConfig 类注释）。
    /// </summary>
    public class FanData
    {
        /// <summary>运行状态（从 0x0001 读到的值，见 FanRunState 枚举）。</summary>
        public FanRunState RunState { get; set; }

        /// <summary>当前温度（单位：°C，寄存器值 / 100）。</summary>
        public float Temperature { get; set; }

        /// <summary>当前湿度（单位：%RH，寄存器值 / 100）。</summary>
        public float Humidity { get; set; }

        /// <summary>温度设定值（单位：°C）。设定值由厂商控制屏设定，上位机只读不写。</summary>
        public float TempSetpoint { get; set; }

        /// <summary>湿度设定值（单位：%RH）。</summary>
        public float HumSetpoint { get; set; }

        /// <summary>本次是否成功从设备读到数据：true=通讯正常字段有效；false=本帧为默认值（离线）。</summary>
        public bool IsOnline { get; set; }

        /// <summary>采集时间戳。</summary>
        public DateTime CollectTime { get; set; }

        /// <summary>深拷贝：缓存数据对外返回副本，避免外部修改污染缓存。</summary>
        public FanData Clone()
        {
            return new FanData
            {
                RunState = this.RunState,
                Temperature = this.Temperature,
                Humidity = this.Humidity,
                TempSetpoint = this.TempSetpoint,
                HumSetpoint = this.HumSetpoint,
                IsOnline = this.IsOnline,
                CollectTime = this.CollectTime
            };
        }
    }

    /// <summary>
    /// 冷却送风机运行状态枚举。
    /// 寄存器 0x0001 读到的值直接对应命令码（实测）：
    /// 0x0000=程式停止、0x0001=程式启动、0x0002=定值停止、0x0003=定值启动。
    /// 本上位机只用到"定值启动/定值停止"；程式模式是设备自带能力，保留枚举便于识别显示。
    /// Unknown 是本库自定义哨兵值（读失败/未初始化），不会出现在设备寄存器里。
    /// </summary>
    public enum FanRunState
    {
        /// <summary>未知（读失败或未初始化，本库自定义）</summary>
        Unknown = -1,

        /// <summary>程式停止</summary>
        ProgramStopped = 0x0000,

        /// <summary>程式启动</summary>
        ProgramRunning = 0x0001,

        /// <summary>定值停止</summary>
        FixedValueStopped = 0x0002,

        /// <summary>定值启动</summary>
        FixedValueRunning = 0x0003
    }
}
