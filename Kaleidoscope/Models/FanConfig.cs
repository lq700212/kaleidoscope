using System.Collections.Generic;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// 冷却送风机控制屏（Modbus TCP）通讯配置。
    ///
    /// 【职责】上位机"定值启动 / 定值停止"送风机 + 周期读状态（温度/湿度）显示的全部参数。
    /// 由业务层塞进 <see cref="DeviceHubConfig.Fan"/>，DeviceHub 据此创建
    /// <see cref="Services.FanControllerClient"/>（真实）或 Mock。
    ///
    /// 【来源】从 AgingTestSystem.Models.DeviceConfig 的送风机字段拆分而来。
    /// 寄存器映射以 ModbusTCPFanControllerTest Demo 实测为准（默认值）：
    /// - 0x0000 组合状态（未用）；0x0001 控制/状态（写 0x0003=定值启动，0x0002=定值停止）
    /// - 0x0002 温度(/100=°C)、0x0003 湿度(/100=%RH)、0x0004 温度设定、0x0005 湿度设定
    /// - 物理层：TCP 端口 50000（非标准 502）、UnitId=1
    ///
    /// 【通用性（换厂商/换型号）】下方 Fan*Register/Fan*Offset/Fan*Command 等字段把【寄存器映射】
    /// 也全部配置化，默认值对齐现场实测——换其他厂商控制屏（寄存器地址/命令码不同）时
    /// 只需改配置，不必改库。读状态用"区块批量读 + 字段偏移"：FanStatusStartAddress 起连续读
    /// FanStatusCount 个寄存器，各字段按 Fan*Offset（相对区块起始的偏移）取值；若现场字段不连续，
    /// 把区块范围调大到覆盖最远字段并逐个配好偏移即可（偏移越界则该字段取 0，不崩）。
    ///
    /// 【接入要点】现场控制器 IP 可能是 192.168.1.220/.221/.222 中任意一个，默认自动识别：
    /// FanAutoDetectEnabled=true 时逐个尝试 FanIpAddress + FanIpCandidates，第一个连上即为真实 IP，
    /// 并写入 FanLastIp.cache 记忆（下次优先），换现场基本免配。
    /// </summary>
    public class FanConfig
    {
        /// <summary>
        /// 是否启用送风机接入。true=启动时尝试连接并周期轮询（送风机是可选设备，失败不影响整机）；
        /// false=完全跳过（不建连接、不轮询）。
        /// </summary>
        public bool FanEnabled { get; set; } = true;

        /// <summary>送风机控制屏 IP 地址（现场实测默认 192.168.1.220）。</summary>
        public string FanIpAddress { get; set; } = "192.168.1.220";

        /// <summary>送风机通讯端口（厂商控制屏实测 50000，非标准 502）。</summary>
        public int FanPort { get; set; } = 50000;

        /// <summary>送风机从站地址（UnitId，实测默认 1）。</summary>
        public byte FanUnitId { get; set; } = 1;

        /// <summary>送风机通讯超时（毫秒）：连接/读写共用，防止掉线时界面卡死，默认 3000。</summary>
        public int FanTimeoutMs { get; set; } = 3000;

        /// <summary>
        /// 送风机 IP 自动识别开关。true=按顺序尝试 FanIpAddress + FanIpCandidates + 缓存 IP，
        /// 第一个连上的即设备真实地址；false=只尝试 FanIpAddress（与旧行为一致）。
        /// </summary>
        public bool FanAutoDetectEnabled { get; set; } = true;

        /// <summary>送风机候选 IP 列表（FanAutoDetectEnabled=true 时生效），连接时按顺序逐个尝试。</summary>
        public List<string> FanIpCandidates { get; set; } = new List<string>();

        // ───────────────────── 寄存器映射（通用性关键，默认值 = 现场实测） ─────────────────────
        // 送风机控制屏厂商/型号不同，寄存器地址与命令码可能完全不一样；这些字段把映射全部
        // 配置化，换设备只改配置不改库。字段偏移为"相对 FanStatusStartAddress 的偏移"，
        // 偏移越界时对应字段按 0/Unknown 处理（不崩）。

        /// <summary>
        /// 读状态区块起始地址（默认 0x0000）：ReadStatus 一次从这里连续读 FanStatusCount 个
        /// 保持寄存器（功能码 0x03），字段按偏移从区块内取值。现场字段不连续时把本地址调至
        /// 覆盖所有字段的最前位置、FanStatusCount 调大覆盖到最远字段。
        /// </summary>
        public ushort FanStatusStartAddress { get; set; } = 0x0000;

        /// <summary>读状态区块长度（寄存器个数，默认 6 覆盖 0x0000~0x0005；现场字段更分散时调大）。</summary>
        public ushort FanStatusCount { get; set; } = 6;

        /// <summary>运行状态字段在区块内的偏移（默认 1 = 0x0001 控制/状态寄存器）。</summary>
        public ushort FanRunStateOffset { get; set; } = 1;

        /// <summary>当前温度字段偏移（默认 2 = 0x0002，值 / 100 = °C）。</summary>
        public ushort FanTemperatureOffset { get; set; } = 2;

        /// <summary>当前湿度字段偏移（默认 3 = 0x0003，值 / 100 = %RH）。</summary>
        public ushort FanHumidityOffset { get; set; } = 3;

        /// <summary>温度设定值字段偏移（默认 4 = 0x0004，值 / 100 = °C，只读）。</summary>
        public ushort FanTempSetpointOffset { get; set; } = 4;

        /// <summary>湿度设定值字段偏移（默认 5 = 0x0005，值 / 100 = %RH，只读）。</summary>
        public ushort FanHumSetpointOffset { get; set; } = 5;

        /// <summary>控制寄存器地址（默认 0x0001）：定值启动/定值停止都写这里。</summary>
        public ushort FanControlAddress { get; set; } = 0x0001;

        /// <summary>定值启动命令码（默认 0x0003，写入 FanControlAddress）。</summary>
        public ushort FanStartCommand { get; set; } = 0x0003;

        /// <summary>定值停止命令码（默认 0x0002，写入 FanControlAddress）。</summary>
        public ushort FanStopCommand { get; set; } = 0x0002;
    }
}
