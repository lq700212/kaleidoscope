using System.Collections.Generic;

namespace CommonLib.Models
{
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
        /// <summary>PLC 通讯配置（Modbus TCP 从站监听参数 + 寄存器地址）</summary>
        public PlcConfig Plc { get; set; } = new PlcConfig();

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
