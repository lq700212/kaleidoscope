using System;
using System.ComponentModel;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// 气压表（真空负压表）通讯配置。
    ///
    /// 【职责】Modbus RTU 主站（RS485→USB，CH340）读气压表压力 / 写设备阈值的全部参数。
    /// 由业务层（新项目）构造并塞进 <see cref="DeviceHubConfig.Barometer"/>，DeviceHub 据此
    /// 创建 <see cref="Services.ModbusRtuBarometerReader"/>（真实）或 Mock（UseMockCommunication=true）。
    ///
    /// 【来源】从 AgingTestSystem.Models.DeviceConfig 的气压表/串口字段拆分而来。
    /// 协议以 ModbusRtuBarometerTest Demo 实测为准（默认值，均可配——换仪表型号改配置不改库）：
    /// - 读压力：Input Register 0x0001（功能码 0x04），一次读 BarometerReadRegisterCount（默认 2）
    ///   个寄存器（0x0001 压力 + 0x0002 小数位）
    /// - 写阈值：Holding Register 0x0010（功能码 0x06），值 = round(阈值 × 10^小数位)
    /// - 0x0002 小数位寄存器现场实测不可靠，换算一律用 BarometerDefaultDecimalPlaces（默认 1）
    ///
    /// 【接入要点】PortName 可留空：连接时优先用磁盘缓存 BarometerPort.cache（上次连上的端口），
    /// 其次配置端口，最后按 CH340 芯片自动识别（SerialPortHelper），现场基本免配。
    /// </summary>
    public class BarometerConfig
    {
        /// <summary>气压表总数（每台一个 Modbus 从站地址 slaveId=deviceId）。老化现场 72 台。</summary>
        [DisplayName("气压表总数")]
        [Description("气压表总数（每台一个 Modbus 从站地址），老化现场 72 台")]
        public int TotalBarometers { get; set; } = 72;

        /// <summary>
        /// 气压表串口名（如 "COM9"）。
        /// 留空（默认）则依靠 CH340 自动识别 + BarometerPort.cache 端口记忆；
        /// 填了具体端口则优先尝试该端口（WMI 识别不到时的兜底）。
        /// </summary>
        [DisplayName("串口名")]
        [Description("留空靠 CH340 自动识别 + 端口记忆；填具体端口则固定优先尝试该端口")]
        public string PortName { get; set; } = "";

        /// <summary>串口波特率（Demo 实测 19200）。</summary>
        [DisplayName("波特率")]
        [Description("串口波特率，Demo 实测 19200")]
        public int BaudRate { get; set; } = 19200;

        /// <summary>串口数据位（默认 8）。</summary>
        [DisplayName("数据位")]
        [Description("串口数据位，默认 8")]
        public int DataBits { get; set; } = 8;

        /// <summary>串口停止位，遵循项目约定：1=1 位、2=2 位、15=1.5 位（内部映射 StopBits 枚举）。</summary>
        [DisplayName("停止位")]
        [Description("项目约定存 1/15/2（1.5 位），内部映射 StopBits 枚举")]
        public int StopBits { get; set; } = 1;

        /// <summary>串口校验位，标准枚举名 None/Odd/Even/Mark/Space（解析大小写不敏感）。</summary>
        [DisplayName("校验位")]
        [Description("标准枚举名 None/Odd/Even/Mark/Space")]
        public string Parity { get; set; } = "None";

        /// <summary>串口读取超时（毫秒）：设备不响应/拔线时防止线程卡死。</summary>
        [DisplayName("读超时(ms)")]
        [Description("设备不响应/拔线时防止线程卡死")]
        public int SerialReadTimeoutMs { get; set; } = 1000;

        /// <summary>串口写入超时（毫秒）。</summary>
        [DisplayName("写超时(ms)")]
        [Description("串口写入超时毫秒")]
        public int SerialWriteTimeoutMs { get; set; } = 1000;

        /// <summary>
        /// 压力值寄存器起始地址（Input Register，功能码 0x04）。
        /// 0x0001 = 压力原始值（有符号 short，支持负压）；0x0002 = 小数位（不可靠，忽略）。
        /// </summary>
        [DisplayName("压力寄存器地址")]
        [Description("Input Register 功能码 0x04，默认 0x0001=压力原始值（有符号 short）")]
        public ushort BarometerPressureRegisterAddress { get; set; } = 0x0001;

        /// <summary>
        /// 读压力时一次连续读取的输入寄存器个数（默认 2：压力原始值 + 0x0002 小数位）。
        /// 部分仪表要求成对/成块读才回数据，现场按说明书调整；换算只信任第 1 个寄存器（压力值），
        /// 数量配置成 1 也能正常工作。防御：运行时自动夹到 1~125（Modbus 单帧上限）。
        /// </summary>
        [DisplayName("读寄存器数")]
        [Description("一次连续读的输入寄存器数（1~125），换算只信任第 1 个寄存器（压力值）")]
        public ushort BarometerReadRegisterCount { get; set; } = 2;

        /// <summary>
        /// 设备阈值寄存器地址（Holding Register，功能码 0x06，默认 0x0010）。
        /// 写入设备内部阈值、驱动硬件报警触点；换仪表型号地址不同时改本配置即可（旧实现写死 0x0010）。
        /// </summary>
        [DisplayName("阈值寄存器地址")]
        [Description("Holding Register 功能码 0x06，默认 0x0010；换仪表型号地址不同时改本配置")]
        public ushort BarometerThresholdRegisterAddress { get; set; } = 0x0010;

        /// <summary>
        /// 小数位固定换算值（默认 1，与 Demo 硬编码一致）。
        /// 寄存器原始值按有符号 short 解释后除以 10^小数位得到压力。
        /// 【为什么不用设备回传的 0x0002】现场实测该寄存器不可靠（72 台中 46 台返回 0，
        /// 但仪表实际按 1 位小数显示），按 0 换算会把压力显示错 10 倍。
        /// </summary>
        [DisplayName("小数位换算")]
        [Description("寄存器原始值 ÷ 10^小数位 = 压力；默认 1（不用设备回传的 0x0002，现场实测不可靠）")]
        public int BarometerDefaultDecimalPlaces { get; set; } = 1;

        /// <summary>压力缩放系数（原始值再乘本系数 = 最终压力）。默认 1，现场按需调整。</summary>
        [DisplayName("压力缩放系数")]
        [Description("换算后的压力再乘本系数；默认 1，现场按需调整")]
        public decimal BarometerPressureScale { get; set; } = 1m;

        /// <summary>软件报警压力阈值（单位 kPa，默认 -95）。真空压力通常为负，越接近 0 真空越差。</summary>
        [DisplayName("报警压力阈值(kPa)")]
        [Description("软件报警压力阈值（kPa，默认 -95）；真空压力为负，越接近 0 真空越差")]
        public decimal AlarmPressureThresholdKPa { get; set; } = -95m;

        /// <summary>
        /// 报警比较方向：true=pressure &gt; 阈值 触发报警（真空变差，负数变"大"，默认）；
        /// false=pressure &lt; 阈值 触发报警（少见，保留扩展）。
        /// 该阈值仅用于通讯层最基础的报警标记（UI 先显示红色），联动关阀/断电属业务职责。
        /// </summary>
        [DisplayName("报警比较方向")]
        [Description("true=压力>阈值触发报警（真空变差，默认）；false=压力<阈值触发报警（少见）")]
        public bool AlarmWhenPressureHigherThanThreshold { get; set; } = true;
    }
}
