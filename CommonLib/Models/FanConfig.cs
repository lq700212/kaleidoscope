using System.Collections.Generic;

namespace CommonLib.Models
{
    /// <summary>
    /// 冷却送风机控制屏（Modbus TCP）通讯配置。
    ///
    /// 【职责】上位机"定值启动 / 定值停止"送风机 + 周期读状态（温度/湿度）显示的全部参数。
    /// 由业务层塞进 <see cref="DeviceHubConfig.Fan"/>，DeviceHub 据此创建
    /// <see cref="Services.FanControllerClient"/>（真实）或 Mock。
    ///
    /// 【来源】从 AgingTestSystem.Models.DeviceConfig 的送风机字段拆分而来。
    /// 寄存器映射以 ModbusTCPFanControllerTest Demo 实测为准：
    /// - 0x0000 组合状态（未用）；0x0001 控制/状态（写 0x0003=定值启动，0x0002=定值停止）
    /// - 0x0002 温度(/100=°C)、0x0003 湿度(/100=%RH)、0x0004 温度设定、0x0005 湿度设定
    /// - 物理层：TCP 端口 50000（非标准 502）、UnitId=1
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
    }
}
