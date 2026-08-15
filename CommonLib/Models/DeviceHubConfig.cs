using System.Collections.Generic;

namespace CommonLib.Models
{
    /// <summary>
    /// PLC 通讯角色（主站/从站）。同一台汇川 PLC 两种模式都支持，由本项目需要决定：
    /// - Slave ：上位机作 Modbus TCP【从站】，监听本机 502 等 PLC 主站来读写（请求-结果-复位三拍握手），
    ///           用 PlcService + PlcConfig（默认，老项目兼容）。
    /// - Master：上位机作 Modbus TCP【主站】，主动连 PLC 读写寄存器（通用主站，支持自动轮询），
    ///           用 ModbusTcpMasterClient + PlcMasterConfig。也可用于连其它 Modbus TCP 从站设备。
    /// </summary>
    public enum PlcRole { Slave, Master }

    /// <summary>
    /// 设备层总配置（DeviceHub 的入参）：把 PLC/相机/扫码枪/图片存储四类配置
    /// 集中成一个对象，DeviceHub 一次性接收并据此创建全部底层服务。
    ///
    /// 【为什么要有这个聚合配置】
    /// 每个底层服务（PlcService/KeyenceIV4Camera/ScannerXxxService/ImageStore）各自
    /// 只吃自己那份配置；但如果界面层要手动把它们一个个 new + 组装，就会把"设备编排"
    /// 的样板代码散落到每个新项目里。本配置作为 DeviceHub 的唯一入参，让新界面只需
    /// 构造一个 DeviceHubConfig 传给 DeviceHub，即可获得"已按约定组装好"的全部服务。
    ///
    /// 【与业务配置的关系】
    /// 本类只含【设备通讯 + 图片存储】相关配置，不含业务字段（如窗口布局/计数/安全）。
    /// 业务界面若还有自己的配置（显示行列、OK/NG 颜色等），可在项目自己的配置里
    /// 持有本类（组合而非继承），例如 AppConfig 里加一个属性 `DeviceHubConfig Devices`。
    /// </summary>
    public class DeviceHubConfig
    {
        /// <summary>
        /// PLC 通讯角色：Slave=上位机作从站（监听 502，默认，用 PlcConfig）；
        /// Master=上位机作主站（主动读写 PLC，用 PlcMasterConfig）。业务层用
        /// DeviceHub.IsPlcMaster 判断，取 hub.Plc（从站）或 hub.PlcMaster（主站）使用。
        /// </summary>
        public PlcRole PlcRole { get; set; } = PlcRole.Slave;

        /// <summary>PLC 通讯配置（Modbus TCP 从站监听参数 + 寄存器地址；仅 PlcRole=Slave 生效）</summary>
        public PlcConfig Plc { get; set; } = new PlcConfig();

        /// <summary>
        /// PLC（或其它 Modbus TCP 从站设备）主站连接与轮询配置（仅 PlcRole=Master 生效，
        /// 用 ModbusTcpMasterClient 主动读写）。字段见 <see cref="PlcMasterConfig"/>。
        /// </summary>
        public PlcMasterConfig PlcMaster { get; set; } = new PlcMasterConfig();

        /// <summary>
        /// 相机通讯配置列表（基恩士 IV4，支持多台）。每台的 IP/端口/指令/点位表/FTP 目录独立。
        /// 为空列表时 DeviceHub 会兜底用 CameraConfig.DefaultCameras()（通用示例默认值，
        /// 新项目应按现场实际 IP 修改这些默认值或直接配好本列表）。
        /// </summary>
        public List<CameraConfig> Cameras { get; set; } = new List<CameraConfig>();

        /// <summary>
        /// 扫码枪配置列表（多台，每台按 ScanConfig.Mode 选 TCP 或串口实现）。
        /// 为空列表则不留任何扫码枪（条码走手动输入/业务侧模拟）。
        /// </summary>
        public List<ScanConfig> Scanners { get; set; } = new List<ScanConfig>();

        /// <summary>图像存储配置（存图目录结构/文件名模板/保留天数/FTP 兜底目录）</summary>
        public ImageConfig Image { get; set; } = new ImageConfig();

        /// <summary>
        /// 气压表（真空负压表）通讯配置（Modbus RTU 主站，RS485→USB 读压力/写阈值）。
        /// 接入方式：业务层定时调 hub.Barometer.ReadAllData() 采集，写设备阈值调 SetAllThresholds。
        /// </summary>
        public BarometerConfig Barometer { get; set; } = new BarometerConfig();

        /// <summary>
        /// IO 耦合器通讯配置（Modbus TCP 主站，读 DI/写 DO 控制真空电磁阀/载台上电）。
        /// 接入方式：业务层定时调 hub.Io.ReadAllInputs()/ReadAllOutputs() 采集，控制输出调 WriteOutput。
        /// </summary>
        public IoConfig Io { get; set; } = new IoConfig();

        /// <summary>
        /// 冷却送风机控制屏通讯配置（Modbus TCP，定值启动/停止 + 读温度湿度）。
        /// 接入方式：业务层调 hub.Fan.ReadStatus()/StartFixedValue()/Stop()。
        /// </summary>
        public FanConfig Fan { get; set; } = new FanConfig();

        /// <summary>
        /// 是否使用模拟通讯（Mock）。
        /// - true：不接任何线，气压表/IO/送风机用随机数模拟，方便先把 UI/业务跑通；
        /// - false：启用真实通讯（气压表 Modbus RTU + IO Modbus TCP + 送风机 Modbus TCP），
        ///   需要现场接线与正确参数。扫码枪/PLC/相机不受此开关影响（各自按配置真实连接）。
        /// </summary>
        public bool UseMockCommunication { get; set; } = false;

        /// <summary>
        /// 当前产品型号（如 "U171"）：PLC 从站建站成功后会立即写进型号区（协议 40007~40012），
        /// 相机触发切程序也按它查各相机的 ModelStationPrograms 型号表。空串则跳过建站即写。
        /// </summary>
        public string ProductModel { get; set; } = "";

        /// <summary>
        /// 产品型号候选列表（切型号时用，如 ["U171","Z121"]）。仅作为"型号集合"的载体
        /// 提供给界面下拉/配置用；DeviceHub 本身不强依赖它（PLC 只写 ProductModel）。
        /// </summary>
        public List<string> ProductModels { get; set; } = new List<string>();

        /// <summary>
        /// 现场默认产品型号候选。返回全新列表实例，调用方可直接 AddRange/复制，不共享引用。
        /// </summary>
        public static List<string> DefaultProductModels() =>
            new List<string> { "U171", "Z121" };
    }
}
