using System.Collections.Generic;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// 上位机作【Modbus TCP 主站】时的连接与轮询配置（通用，可连 PLC / 远程 IO / 仪表等任意 Modbus TCP 从站设备）。
    ///
    /// 【为什么需要它 / 与 PlcConfig 的区别】
    /// PlcConfig 描述的是"上位机作从站"（监听 502 等 PLC 主站来读写）的协议；本类描述相反的
    /// "上位机作主站"（主动连对方、主动读写）的物理连接与轮询项。两者是同一物理设备、两种角色，
    /// 由 DeviceHubConfig.PlcRole 选择，DeviceHub 按角色装配 PlcService（从站）或 ModbusTcpMasterClient（主站）。
    ///
    /// 【调用方注意】
    /// - 寄存器地址一律填【Modbus 协议地址】（0x0000 起：协议 40001 = 地址 0，40002 = 地址 1，依此类推），
    ///   与设备手册的寄存器表对齐；本配置不区分 DataStore 索引（那是从站协议的约定）。
    /// - 轮询项可配多条（每条一个名称+功能码+地址+长度），ModbusTcpMasterClient.StartPolling 后
    ///   后台定时轮询，结果通过 PollDataUpdated 事件 / GetLastPollData 缓存取用，业务层无需写轮询线程。
    /// </summary>
    public class PlcMasterConfig
    {
        /// <summary>目标从站设备 IP（汇川 PLC / 远程 IO / 仪表）</summary>
        public string IpAddress { get; set; } = "192.168.1.30";

        /// <summary>目标从站端口，Modbus TCP 标准 502（送风机等特殊设备 50000 见其各自配置）</summary>
        public int Port { get; set; } = 502;

        /// <summary>Modbus 从站地址（UnitId），默认 1（多数 PLC/耦合器）</summary>
        public byte UnitId { get; set; } = 1;

        /// <summary>连接/读写超时（毫秒）。TCP 连接用 BeginConnect+WaitOne 强制超时，IP 错误不卡界面。</summary>
        public int TimeoutMs { get; set; } = 2000;

        /// <summary>
        /// 重连节流间隔（毫秒）：连接断开后两次重连尝试至少间隔这么久，避免对已断电设备高频无效连接。
        /// 默认 5000（与 DeviceHub 的 ConnectionMonitor 心跳节奏一致）。
        /// </summary>
        public int ReconnectIntervalMs { get; set; } = 5000;

        /// <summary>
        /// 自动轮询周期（毫秒）。大于 0 且 PollItems 非空时，DeviceHub.Start() 会调用
        /// ModbusTcpMasterClient.StartPolling 后台定时轮询；0=不自动轮询（业务层按需读写）。
        /// </summary>
        public int PollIntervalMs { get; set; } = 1000;

        /// <summary>自动轮询项列表（每条一个名称+功能码+起始地址+数量），见 <see cref="PlcPollItem"/>。</summary>
        public List<PlcPollItem> PollItems { get; set; } = new List<PlcPollItem>();
    }

    /// <summary>
    /// 一条自动轮询项：给一段连续的 Modbus 寄存器/位区起个名字，ModbusTcpMasterClient 定时读它。
    /// 例：{ Name="温度", Function=Holding, StartAddress=0x1000, Count=2 }
    /// 轮询结果通过事件/缓存按 Name 取用（读多寄存器的数据从 0 下标起依次排）。
    /// </summary>
    public class PlcPollItem
    {
        /// <summary>轮询项名称（结果缓存/事件里用它区分是哪一段数据）</summary>
        public string Name { get; set; } = "";

        /// <summary>读取的功能码：Holding=保持寄存器(0x03)、Input=输入寄存器(0x04)、Coil=线圈(0x01)、Discrete=离散输入(0x02)</summary>
        public PlcPollFunction Function { get; set; } = PlcPollFunction.Holding;

        /// <summary>起始 Modbus 地址（0x0000 起）</summary>
        public ushort StartAddress { get; set; } = 0;

        /// <summary>连续读取的数量（寄存器个数或位个数，默认 1）</summary>
        public ushort Count { get; set; } = 1;
    }

    /// <summary>Modbus 读取功能码类型（自动轮询与通用读取方法共用）</summary>
    public enum PlcPollFunction
    {
        /// <summary>保持寄存器，功能码 0x03，ushort 数组（读写均可）</summary>
        Holding,

        /// <summary>输入寄存器，功能码 0x04，ushort 数组（只读）</summary>
        Input,

        /// <summary>线圈，功能码 0x01，bool 数组（读写均可）</summary>
        Coil,

        /// <summary>离散输入，功能码 0x02，bool 数组（只读）</summary>
        Discrete
    }
}