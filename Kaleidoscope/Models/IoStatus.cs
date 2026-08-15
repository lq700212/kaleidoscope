using System;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// IO 状态数据模型：描述单个 IO 点的实时状态信息。
    /// 【来源】AgingTestSystem.Models.IoStatus 原样移植（字段与 Aging 保持一致）。
    /// </summary>
    public class IoStatus
    {
        /// <summary>
        /// IO 点内部编号（全局唯一，十进制连续编号）。
        /// 输入：1 ~ TotalInputs；输出：TotalInputs+1 ~ TotalInputs+TotalOutputs。
        /// 实际硬件地址请用 <see cref="PhysicalAddress"/>（如 X000/Y107）。
        /// </summary>
        public int IoId { get; set; }

        /// <summary>物理地址（三菱 PLC 八进制编址，如 X000/Y107），对应现场 IO 模块实际点位。</summary>
        public string PhysicalAddress { get; set; }

        /// <summary>IO 类型：输入或输出。</summary>
        public IoType Type { get; set; }

        /// <summary>IO 点名称/描述（如：真空负压表-1、真空电磁阀-1、载台上电-1）。</summary>
        public string Name { get; set; }

        /// <summary>所属气压表编号（1 ~ TotalBarometers），每个气压表对应 1 输入 + 2 输出。</summary>
        public int DeviceId { get; set; }

        /// <summary>IO 功能类型（真空负压表/真空电磁阀/载台上电），用于区分该 IO 点的业务功能。</summary>
        public IoFunction Function { get; set; }

        /// <summary>
        /// 电气类型（NPN/PNP）：输入用 NPN（漏型，传感器导通拉低到 0V）；
        /// 输出用 PNP（源型，导通输出 +24V 驱动中间继电器）。
        /// </summary>
        public ElectricalType Electrical { get; set; }

        /// <summary>在所属设备中的本地编号：输入固定 1；输出 1=真空电磁阀、2=载台上电。</summary>
        public int LocalIndex { get; set; }

        /// <summary>当前状态：true=高电平/导通，false=低电平/断开。</summary>
        public bool State { get; set; }

        /// <summary>状态更新时间。</summary>
        public DateTime UpdateTime { get; set; }

        /// <summary>是否有报警（业务层标记）。</summary>
        public bool HasAlarm { get; set; }
    }

    /// <summary>IO 类型枚举。</summary>
    public enum IoType
    {
        /// <summary>输入信号（现场 NPN 型，X 地址）</summary>
        Input,

        /// <summary>输出信号（现场 PNP 型，Y 地址）</summary>
        Output
    }

    /// <summary>IO 功能类型枚举（依据现场 IO 分配表定义每个 IO 点的业务功能）。</summary>
    public enum IoFunction
    {
        /// <summary>未定义/预留 IO 点</summary>
        Unknown,

        /// <summary>真空负压表信号（输入，NPN）：检测真空压力是否到达设定阈值</summary>
        VacuumPressure,

        /// <summary>真空电磁阀控制（输出，PNP）：控制真空回路通断，驱动中间继电器后控制电磁阀</summary>
        VacuumValve,

        /// <summary>载台上电控制（输出，PNP）：给被测载台（产品治具）供电，驱动中间继电器后控制载台电源</summary>
        CarrierPower
    }

    /// <summary>IO 电气类型枚举（区分现场 IO 模块输入输出的电气特性）。</summary>
    public enum ElectricalType
    {
        /// <summary>
        /// NPN 型（漏型/灌入式，输入采用）：传感器导通时将 IO 输入信号拉低到 0V（低电平有效）。
        /// 模块内部上拉，NPN 传感器导通时拉低电平，模块识别为"导通"。
        /// </summary>
        NPN,

        /// <summary>
        /// PNP 型（源型/拉出式，输出采用）：输出导通时输出 +24V 高电平，向外提供电流。
        /// 适合直接驱动中间继电器线圈（另一端接 0V），由继电器触点控制大功率负载（电磁阀、载台电源）。
        /// </summary>
        PNP
    }
}
