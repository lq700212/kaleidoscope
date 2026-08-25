using System.ComponentModel;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// 扫码枪配置（从 CommandCenter/Models/AppConfig.cs 抽取，原 ScanConfig 类）。
    /// 支持两种通讯方式（见 Mode）；未启用则序列号走手动输入/模拟。
    /// </summary>
    public class ScanConfig
    {
        /// <summary>是否启用扫码枪。</summary>
        [DisplayName("启用")]
        [Description("是否启用扫码枪；未启用则序列号走手动输入/模拟")]
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// 通讯方式（大小写不敏感）：
        ///   "Tcp"   ：基恩士 SR 系列扫码枪以太网 TCP/IP 无协议通讯（默认，现场实测）——上位机作
        ///              TCP 客户端连扫码枪，扫码枪读到条码后主动推送文本行，上位机按行切分；
        ///   "Serial"：串口 RS-232 扫码枪（扫完发一行条码+CR/LF）；
        ///   "Eip"   ：EtherNet/IP(CIP) 扫码枪（V1.6.0 新增）——扫码枪作 EIP
        ///              适配器(Adapter)，上位机作扫描器(Scanner)周期读 Assembly 数据取条码，
        ///              适用基恩士 SR / 康耐视 DataMan 刷成 EIP 模式的机型。
        /// </summary>
        [DisplayName("通讯方式")]
        [Description("Tcp=以太网无协议通讯（默认）；Serial=串口 RS-232；Eip=EtherNet/IP 周期读 Assembly")]
        public string Mode { get; set; } = "Tcp";

        /// <summary>
        /// 串口名，如 COM3（仅 Mode=Serial 使用）。
        /// 留空（默认）则通过 WMI 按 <see cref="DeviceKeyword"/> 自动识别扫码枪串口（现场免配）；
        /// 填了具体端口则固定用该端口（WMI 识别不到时兜底）。
        /// </summary>
        [DisplayName("串口名")]
        [Description("仅串口方式；留空按设备关键词 WMI 自动识别，填具体端口则固定用该端口")]
        public string PortName { get; set; } = "";

        /// <summary>串口数据位（仅 Mode=Serial 使用，默认 8）。</summary>
        [DisplayName("数据位")]
        [Description("仅串口方式使用，默认 8")]
        public int DataBits { get; set; } = 8;

        /// <summary>
        /// 扫码枪设备识别关键词（仅 Mode=Serial 使用，PortName 留空时用于 WMI 自动识别串口）。
        /// 对应设备管理器里显示的设备名称中包含的关键字，如 Honeywell Xenon 1902（默认 "Xenon 1902"）。
        /// 心跳断连检测也复用同一套 WMI 搜索确认物理设备还在。
        /// </summary>
        [DisplayName("设备识别关键词")]
        [Description("仅串口方式；设备管理器名称里包含的关键字（如 Xenon 1902），WMI 自动识别用")]
        public string DeviceKeyword { get; set; } = "Xenon 1902";

        /// <summary>
        /// 心跳调试日志开关（仅 Mode=Serial 使用，默认 false）。
        /// true 时每个心跳周期把端口搜索结果（GetPortNames / WMI 匹配 / 判定）打到日志，
        /// 用于现场排查"扫码枪断连识别不到"问题。
        /// </summary>
        [DisplayName("心跳调试日志")]
        [Description("仅串口方式；true 时每个心跳周期打印端口搜索结果，排查断连识别不到用")]
        public bool DebugLog { get; set; } = false;

        /// <summary>波特率，扫码枪常见 115200 / 9600（仅 Mode=Serial 使用）</summary>
        [DisplayName("波特率")]
        [Description("仅串口方式；扫码枪常见 115200 / 9600")]
        public int BaudRate { get; set; } = 115200;

        /// <summary>停止位字符串，遵循项目约定："1"/"15"/"2"（仅 Mode=Serial 使用）</summary>
        [DisplayName("停止位")]
        [Description("仅串口方式；项目约定存字符串 1/15/2（1.5 位）")]
        public string StopBits { get; set; } = "1";

        /// <summary>校验位，标准枚举名 None/Odd/Even/Mark/Space（仅 Mode=Serial 使用）</summary>
        [DisplayName("校验位")]
        [Description("仅串口方式；标准枚举名 None/Odd/Even/Mark/Space")]
        public string Parity { get; set; } = "None";

        /// <summary>扫码枪 IP（仅 Mode=Tcp 使用）。基恩士 SR 系列无协议通讯的默认监听端口请查
        /// 《SR 系列通信指南》，常见 9005 左右，现场按扫码枪设置改。</summary>
        [DisplayName("扫码枪 IP")]
        [Description("仅 TCP 方式使用；基恩士 SR 无协议通讯 IP")]
        public string IpAddress { get; set; } = "192.168.0.100";

        /// <summary>扫码枪 TCP 端口（仅 Mode=Tcp 使用，基恩士 SR 无协议默认端口，现场确认）</summary>
        [DisplayName("TCP 端口")]
        [Description("仅 TCP 方式使用；基恩士 SR 无协议端口（现场确认）")]
        public int Port { get; set; } = 9004;

        /// <summary>
        /// TCP 模式连接成功后的"触发/启动读码"指令（仅 Mode=Tcp 使用）。
        /// 基恩士 SR 系列无协议通讯：上位机连接后要先发打开激光/开始读取的指令，扫码枪才会
        /// 开始读码并推送条码；本现场实测指令为 "LON"（Laser ON），帧尾补 CRLF。
        /// 发送时自动在该指令后补 "\r\n" 帧结束符。留空则不发送（对应扫码枪设为"上电自动连续读码"）。
        /// </summary>
        [DisplayName("触发指令")]
        [Description("仅 TCP 方式；连接成功后自动发送（如 LON）启动读码，自动补 CRLF；留空不发送")]
        public string TriggerCommand { get; set; } = "LON";

        // ────── EIP 模式专属参数（仅 Mode=Eip 使用，现场实测参数）──────

        /// <summary>
        /// EIP 读取地址：扫码枪 Assembly 实例号（T→O 数据），基恩士 SR / 康耐视 DataMan 常见
        /// 默认 "4"，现场不同型号按扫码枪手册修改（仅 Mode=Eip 使用）。
        /// </summary>
        [DisplayName("EIP Assembly 地址")]
        [Description("仅 EIP 方式；扫码枪 Assembly 实例号，基恩士 SR/DataMan 常见默认 4")]
        public string EipAssemblyAddress { get; set; } = "4";

        /// <summary>EIP 单次读取字节数 = 条码最大长度（Assembly 定长数据区，默认 64 够一维/二维码）。</summary>
        [DisplayName("EIP 读取字节数")]
        [Description("仅 EIP 方式；单次读取字节数=条码最大长度，默认 64")]
        public int EipReadLengthBytes { get; set; } = 64;

        /// <summary>
        /// EIP 轮询周期（毫秒）：上位机周期读一次 Assembly 取条码。默认 200ms——扫码响应足够快
        /// 且不占带宽。注意 Assembly 是【保持型】数据，服务内部已做去重（条码变化或中间出现过空
        /// 才上报），同一条码不会按周期重复触发（仅 Mode=Eip 使用）。
        /// </summary>
        [DisplayName("EIP 轮询周期(ms)")]
        [Description("仅 EIP 方式；周期读 Assembly 的间隔毫秒，默认 200")]
        public int EipPollingMs { get; set; } = 200;
    }
}
