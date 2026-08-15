namespace CommonLib.Models
{
    /// <summary>
    /// IO 点定义模型：描述单个 IO 点的静态配置信息（地址、功能、电气类型等）。
    /// 【来源】AgingTestSystem.Models.IoPointDefinition 原样移植。
    /// 用于建立"内部连续编号"与"物理地址"之间的映射（三菱八进制编址，见 Utils.IoMapBuilder）。
    /// </summary>
    public class IoPointDefinition
    {
        /// <summary>IO 点内部编号（全局唯一，十进制连续编号）：输入 1~TotalInputs，输出 TotalInputs+1~TotalInputs+TotalOutputs。</summary>
        public int IoId { get; set; }

        /// <summary>
        /// 物理地址（三菱 PLC 八进制编址）：输入 X000、X007、X010、X107；输出 Y000、Y107、Y110、Y217。
        /// 三菱 X/Y 点采用八进制编号（每位数字 0~7），如 X007 之后是 X010（非 X008）。
        /// </summary>
        public string PhysicalAddress { get; set; }

        /// <summary>IO 点设备名称（来自 IO 分配表）：如 真空负压表-1、真空电磁阀-1、载台上电-1。</summary>
        public string DeviceName { get; set; }

        /// <summary>所属气压表编号（1 ~ TotalBarometers）。</summary>
        public int DeviceId { get; set; }

        /// <summary>IO 类型（输入/输出）。</summary>
        public IoType Type { get; set; }

        /// <summary>IO 功能类型（真空负压表/真空电磁阀/载台上电）。</summary>
        public IoFunction Function { get; set; }

        /// <summary>电气类型（NPN/PNP）：输入点为 NPN，输出点为 PNP。</summary>
        public ElectricalType Electrical { get; set; }

        /// <summary>在所属设备中的本地编号：输入固定 1；输出 1=真空电磁阀、2=载台上电。</summary>
        public int LocalIndex { get; set; }
    }

    /// <summary>
    /// 单个气压表对应的 IO 点映射集合：每个气压表对应 1 个输入 + 2 个输出。
    /// </summary>
    public class DeviceIoMapping
    {
        /// <summary>真空负压表输入点（NPN，X 地址）。</summary>
        public IoPointDefinition VacuumPressureInput { get; set; }

        /// <summary>真空电磁阀输出点（PNP，Y 地址）。</summary>
        public IoPointDefinition VacuumValveOutput { get; set; }

        /// <summary>载台上电输出点（PNP，Y 地址）。</summary>
        public IoPointDefinition CarrierPowerOutput { get; set; }
    }
}
