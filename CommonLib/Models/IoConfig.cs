using System.Collections.Generic;

namespace CommonLib.Models
{
    /// <summary>
    /// IO 耦合器（GX-CL140）Modbus TCP 通讯配置。
    ///
    /// 【职责】上位机作 Modbus TCP 主站，周期读 DI/DO、按业务写 DO 的全部参数。
    /// 由业务层塞进 <see cref="DeviceHubConfig.Io"/>，DeviceHub 据此创建
    /// <see cref="Services.ModbusTcpIoController"/>（真实）或 Mock。
    ///
    /// 【来源】从 AgingTestSystem.Models.DeviceConfig 的耦合器字段拆分而来。
    /// 寄存器区与位序已按 ModbusTCPTest Demo 实测固化（GX-CL140）：
    /// - DI：起始 0x1000，读 Input Registers（功能码 0x04），16 点/寄存器，bit0=第 1 路
    /// - DO：起始 0x2000，读 Holding Registers（功能码 0x03）、写单寄存器（功能码 0x06），
    ///   读-改-写单点，16 点/寄存器，bit0=第 1 路
    ///
    /// 【接入要点】内部连续编号规则：输入 1~TotalInputs、输出 TotalInputs+1~TotalInputs+TotalOutputs；
    /// 物理地址（三菱八进制 X/Y）映射见 Utils.IoMapBuilder。
    /// </summary>
    public class IoConfig
    {
        /// <summary>
        /// IO 输入通道总数（耦合器提供的 DI 通道数，不一定要等于气压表数）。
        /// 老化现场：3 个输入模块（2×DI50N-S + 1×DI40N-S）= 80 路，前 72 路用于真空负压表，其余预留。
        /// </summary>
        public int TotalInputs { get; set; } = 80;

        /// <summary>
        /// IO 输出通道总数（耦合器提供的 DO 通道数）。
        /// 老化现场：5×DQ50P-S = 160 路，业务实际用 144（真空电磁阀 72 + 载台上电 72），其余预留。
        /// </summary>
        public int TotalOutputs { get; set; } = 160;

        /// <summary>IO 耦合器 IP 地址（GX-CL140 默认 192.168.1.20）。</summary>
        public string PlcAddress { get; set; } = "192.168.1.20";

        /// <summary>IO 耦合器 Modbus TCP 端口（标准 502）。</summary>
        public int PlcPort { get; set; } = 502;

        /// <summary>IO 耦合器从站地址（UnitId，默认 1）。</summary>
        public byte IoUnitId { get; set; } = 1;

        /// <summary>DI 输入寄存器起始地址（功能码 0x04），默认 0x1000。</summary>
        public ushort IoInputRegisterStartAddress { get; set; } = 0x1000;

        /// <summary>DO 输出寄存器起始地址（Holding Register），默认 0x2000。</summary>
        public ushort IoOutputRegisterStartAddress { get; set; } = 0x2000;

        /// <summary>
        /// 输入点逻辑取反开关：现场线路 NPN（低有效）但耦合器映射后有的"1=ON"、有的"0=ON"，
        /// 需现场实测。false=bit=1 认为 ON（默认），true=bit=0 认为 ON。
        /// </summary>
        public bool InvertInputs { get; set; } = false;

        /// <summary>输出点逻辑取反开关，语义同 InvertInputs。</summary>
        public bool InvertOutputs { get; set; } = false;

        /// <summary>TCP 发送超时（毫秒），默认 3000。</summary>
        public int TcpSendTimeoutMs { get; set; } = 3000;

        /// <summary>TCP 接收超时（毫秒），同时用于 BeginConnect 手动超时，默认 3000。</summary>
        public int TcpReceiveTimeoutMs { get; set; } = 3000;

        /// <summary>
        /// IO 输出"备用通道映射"总开关。
        /// 现场某 DQ 通道烧毁后把该通道信号改写到备用通道；false 时所有行为不变（默认）。
        /// 业务侧输出编号完全不变，只重定向物理寄存器+bit。
        /// </summary>
        public bool IoBackupChannelMappingEnabled { get; set; } = false;

        /// <summary>IO 输出备用通道映射表（IoBackupChannelMappingEnabled=true 时生效），解析见 IoOutputChannelRemap.ParseAll。</summary>
        public List<IoOutputChannelRemap> IoBackupChannelMappings { get; set; } = new List<IoOutputChannelRemap>();
    }
}
