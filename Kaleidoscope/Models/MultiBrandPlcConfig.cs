using System.Collections.Generic;
using System.ComponentModel;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// 多品牌 PLC 协议类型（HslCommunication 库支持、且经过现场验证的协议）。
    ///
    /// 【为什么用自定义枚举而不是直接暴露 HSL 类型】
    /// Models（配置模型）必须保持零第三方依赖——配置编辑器/业务项目只引用本库元数据即可渲染
    /// 界面，不需要拖上 5MB 的 HSL DLL；由服务层（HslPlcClient 工厂）负责把枚举翻译成 HSL 客户端。
    ///
    /// 【扩展约定】新增品牌 = 本枚举加值（只许追加末尾，.kcfg 按枚举名序列化）+ MultiBrandPlcConfig
    /// 默认端口表加一行 + HslPlcClient.CreateDevice 加一个 case，读写链路零改动（一个 IReadWriteNet
    /// 接口吃掉所有品牌——该设计为现场验证过的优秀实践）。
    /// </summary>
    public enum MultiBrandPlcBrand
    {
        /// <summary>三菱 A1E 协议（A/Q 系列 A1E 兼容口），地址如 M10/D100，默认端口 6000</summary>
        MelsecA1E,

        /// <summary>三菱 MC 协议（Q/iQ-R 等二进制通讯），地址如 M10/D100/X20，默认端口 6000</summary>
        MelsecMc,

        /// <summary>欧姆龙 FINS/TCP（CS/CJ/CP 系列），地址如 CIO100/W10/D100，默认端口 9600</summary>
        OmronFins,

        /// <summary>欧姆龙 EtherNet/IP(CIP)（NJ/NX 系列，按标签名寻址），默认端口 44818</summary>
        OmronCip,

        /// <summary>汇川 H5U（走 Modbus TCP 兼容口），地址如 D100/M10，默认端口 502</summary>
        InovanceH5U,

        /// <summary>西门子 S7（ISO-on-TCP，型号由 SiemensModel 指定），地址如 DB1.DBW0/M10.0，默认端口 102</summary>
        SiemensS7,
    }

    /// <summary>多品牌 PLC 轮询项的数据类型（决定用 IReadWriteNet 的哪个读法解析数据）。</summary>
    public enum PlcDataType
    {
        /// <summary>位（bool），地址如 M10 / X0 / DB1.DBX0.0</summary>
        Bool,

        /// <summary>有符号 16 位整数（short），占 1 个字</summary>
        Int16,

        /// <summary>无符号 16 位整数（ushort），占 1 个字</summary>
        UInt16,

        /// <summary>有符号 32 位整数（int），占 2 个字</summary>
        Int32,

        /// <summary>单精度浮点（float），占 2 个字</summary>
        Float,
    }

    /// <summary>
    /// 多品牌 PLC（HSL）连接与轮询配置：上位机经三菱 MC/A1E、欧姆龙 FINS/CIP、汇川 H5U、
    /// 西门子 S7 等【非 Modbus 协议】主动读写 PLC 时使用（仅 DeviceHubConfig.PlcRole=MultiBrand 生效）。
    ///
    /// 【来源】协议能力来自商业通讯库 HslCommunication（V1.6.0 集成，授权码需自行购买）；
    /// 连接管理范式对齐库内 ModbusTcpMasterClient（惰性连接 + 锁串行化 + 断连边沿 + 节流重连 +
    /// 自动轮询），并自带心跳自愈线程（多品牌模式无 ConnectionMonitor 参与）。
    ///
    /// 【调用方注意】寄存器地址一律填【PLC 品牌自己的地址语法】字符串（三菱 "D100"、欧姆龙
    /// "C100"/"W100"、西门子 "DB1.DBW0"、汇川 "D100"），与各品牌手册对齐——这与 Modbus 主站
    /// 配置（纯数字协议地址）不同，勿混用。端口填 0 表示"按品牌默认端口"（运行时自动补）。
    /// </summary>
    public class MultiBrandPlcConfig
    {
        /// <summary>PLC 品牌/协议（决定 HSL 客户端类型与地址语法）</summary>
        [DisplayName("PLC 品牌")]
        [Description("协议品牌：MelsecA1E/MelsecMc=三菱；OmronFins/OmronCip=欧姆龙；InovanceH5U=汇川；SiemensS7=西门子")]
        public MultiBrandPlcBrand Brand { get; set; } = MultiBrandPlcBrand.MelsecMc;

        /// <summary>PLC IP 地址</summary>
        [DisplayName("PLC IP")]
        [Description("PLC 以太网口的 IP 地址")]
        public string IpAddress { get; set; } = "192.168.0.1";

        /// <summary>
        /// PLC 端口。填 0（默认）= 按品牌自动取默认端口：
        /// 三菱 A1E/MC=6000、欧姆龙 FINS=9600、欧姆龙 CIP=44818、汇川 H5U=502、西门子 S7=102。
        /// 现场改过端口的才需要显式填写。
        /// </summary>
        [DisplayName("端口")]
        [Description("0=按品牌默认（三菱6000/欧姆龙FINS 9600/CIP 44818/汇川502/西门子102）；现场改过端口才需填写")]
        public int Port { get; set; } = 0;

        /// <summary>连接/读写超时（毫秒）。连接超时由 HSL ConnectTimeOut 强制，IP 错误不长时间卡线程。</summary>
        [DisplayName("连接/读写超时(ms)")]
        [Description("连接与单次读写的超时毫秒数，默认 2000")]
        public int TimeoutMs { get; set; } = 2000;

        /// <summary>
        /// 重连节流间隔（毫秒）：断开后两次重连尝试至少间隔这么久，防止对断电设备高频无效连接。
        /// 默认 5000（与库内其它主站设备节奏一致）。
        /// </summary>
        [DisplayName("重连节流间隔(ms)")]
        [Description("断开后两次重连尝试的最小间隔毫秒，默认 5000")]
        public int ReconnectIntervalMs { get; set; } = 5000;

        /// <summary>
        /// 心跳探活周期（毫秒）。多品牌 PLC 无 ConnectionMonitor 参与，本服务自带后台心跳线程：
        /// 未连接时按重连节流尝试恢复；已连接且配置了 <see cref="HeartbeatAddress"/> 时周期读一次
        /// 该地址做应用层探活（能发现拔网线这类静默断连）。HeartbeatAddress 留空则不做应用层探活
        /// （只做连接态检查，适合不方便预留心跳地址的现场）。默认 3000。
        /// </summary>
        [DisplayName("心跳周期(ms)")]
        [Description("后台心跳探活周期毫秒，默认 3000；未连接时按它节流重连")]
        public int HeartbeatIntervalMs { get; set; } = 3000;

        /// <summary>
        /// 心跳探活地址（品牌地址语法，如 "D0"）。非空时心跳线程周期读它确认链路活着；
        /// 留空（默认）不做应用层探活。建议选一块【不会影响设备逻辑】的可读区（如 D 区首字）。
        /// </summary>
        [DisplayName("心跳地址")]
        [Description("周期读取探活的地址（品牌地址语法，如 D0）；留空=不应用层探活")]
        public string HeartbeatAddress { get; set; } = "";

        /// <summary>心跳地址的数据类型（默认 Int16 读一个字）</summary>
        [DisplayName("心跳数据类型")]
        [Description("心跳地址的读取数据类型，默认 Int16")]
        public PlcDataType HeartbeatDataType { get; set; } = PlcDataType.Int16;

        /// <summary>
        /// 自动轮询周期（毫秒）。大于 0 且 PollItems 非空时启动后台轮询（结果发 PollDataUpdated
        /// 事件并缓存）；0=不自动轮询（业务层按需读写）。轮询本身兼作链路探活。
        /// </summary>
        [DisplayName("轮询周期(ms)")]
        [Description(">0 且轮询项非空时自动后台轮询；0=不自动轮询")]
        public int PollIntervalMs { get; set; } = 1000;

        /// <summary>自动轮询项列表（每条 = 名称 + 地址 + 数据类型 + 数量），见 <see cref="MultiBrandPlcPollItem"/>。</summary>
        [DisplayName("自动轮询项")]
        [Description("自动轮询项列表（双击展开编辑）：每条 = 名称 + 地址(品牌语法) + 数据类型 + 数量")]
        public List<MultiBrandPlcPollItem> PollItems { get; set; } = new List<MultiBrandPlcPollItem>();

        // ────── 西门子 / CIP 专属参数（其它品牌忽略）──────

        /// <summary>
        /// 西门子 PLC 型号（仅 Brand=SiemensS7 生效）：S1200/S1500/S300/S400/S200Smart/S200。
        /// 决定 S7 报文的 Rack/Slot 默认值与寻址细节；非法值运行时回退 S1200 并告警。
        /// </summary>
        [DisplayName("西门子型号")]
        [Description("仅 SiemensS7 生效：S1200/S1500/S300/S400/S200Smart/S200")]
        public string SiemensModel { get; set; } = "S1200";

        /// <summary>
        /// 机架号 Rack（仅 SiemensS7 生效，默认 0）；同时用于欧姆龙 CIP 的 Slot（扫码枪/无机架设备填 0）。
        /// </summary>
        [DisplayName("机架/槽位")]
        [Description("西门子 Rack（默认0）；欧姆龙 CIP 时作 Slot 用（一般 0）")]
        public byte RackSlot { get; set; } = 0;

        /// <summary>
        /// 西门子槽位 Slot（仅 SiemensS7 生效，默认 0；S300/S400 常见 2）。与 RackSlot 分开设，
        /// 避免"Rack=0,Slot=2"这种组合没法用一个字节表达。
        /// </summary>
        [DisplayName("西门子槽位 Slot")]
        [Description("仅 SiemensS7 生效，默认 0（S300/S400 常 2）")]
        public byte SiemensSlot { get; set; } = 0;
    }

    /// <summary>
    /// 一条多品牌 PLC 自动轮询项：给一段连续数据起个名字，HslPlcClient 后台定时读它。
    /// 例：{ Name="温度", Address="D100", DataType=Float, Count=2 }
    /// 结果通过 PollDataUpdated 事件 / TryGetLastPollData 缓存按 Name 取用（数组下标 0 起）。
    /// </summary>
    public class MultiBrandPlcPollItem
    {
        /// <summary>轮询项名称（结果缓存/事件里用它区分是哪段数据）</summary>
        [DisplayName("轮询项名称")]
        [Description("结果缓存/事件里用它区分是哪段数据")]
        public string Name { get; set; } = "";

        /// <summary>起始地址（PLC 品牌地址语法，如三菱 "D100"、欧姆龙 "W10"、西门子 "DB1.DBW0"）</summary>
        [DisplayName("起始地址")]
        [Description("品牌地址语法的起始地址，如 D100 / W10 / DB1.DBW0")]
        public string Address { get; set; } = "";

        /// <summary>数据类型（Bool/Int16/UInt16/Int32/Float），决定解析宽度</summary>
        [DisplayName("数据类型")]
        [Description("Bool=位；Int16/UInt16=1字；Int32/Float=2字")]
        public PlcDataType DataType { get; set; } = PlcDataType.Int16;

        /// <summary>连续读取的数量（元素个数，至少 1；Bool 为位数、其余为字组数）</summary>
        [DisplayName("读取数量")]
        [Description("连续读取个数（至少 1）：Bool 计位，其余计元素个数")]
        public ushort Count { get; set; } = 1;
    }
}
