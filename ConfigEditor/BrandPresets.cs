using System;
using System.Collections.Generic;
using Kaleidoscope.Models;

namespace Kaleidoscope.ConfigEditor
{
    /// <summary>
    /// 设备树节点种类（MainForm 树节点 Tag 用）：区分"这个节点对应哪一段配置"。
    /// 编辑器根据节点种类决定：PropertyGrid 绑定什么对象、品牌预设给哪些选项、
    /// 添加/删除设备按钮对哪些节点生效。
    /// </summary>
    public enum NodeKind
    {
        /// <summary>树根（设备配置）</summary>
        Root,

        /// <summary>全局设置（DeviceHubConfig 顶层：PLC 角色/型号/Mock）</summary>
        Global,

        /// <summary>PLC 分组节点（仅导航，无对象）</summary>
        PlcGroup,

        /// <summary>PLC 下 ModBus-TCP 协议分组（仅导航，无对象；主站/从站归其下）</summary>
        PlcModbusGroup,

        /// <summary>胡工科技通讯库（HSL）分组（仅导航，无对象；主站/从站归其下）</summary>
        PlcHslGroup,

        /// <summary>PLC 从站（PlcConfig）</summary>
        PlcSlave,

        /// <summary>PLC 主站（PlcMasterConfig）</summary>
        PlcMaster,

        /// <summary>PLC 多品牌主站（MultiBrandPlcConfig，三菱/欧姆龙/西门子/汇川，V1.6.0）</summary>
        PlcMultiBrand,

        /// <summary>相机分组节点（仅导航，无对象）</summary>
        CameraGroup,

        /// <summary>单台相机（CameraConfig，Index 定位）</summary>
        Camera,

        /// <summary>扫码枪分组节点（仅导航，无对象）</summary>
        ScannerGroup,

        /// <summary>单台扫码枪（ScanConfig，Index 定位）</summary>
        Scanner,

        /// <summary>气压表（BarometerConfig）</summary>
        Barometer,

        /// <summary>IO 耦合器（IoConfig）</summary>
        Io,

        /// <summary>送风机（FanConfig）</summary>
        Fan,

        /// <summary>图像存储（ImageConfig）</summary>
        Image
    }

    /// <summary>
    /// 一个"品牌预设"：某个设备类型下，某个厂商/型号的一整套默认参数。
    /// 使用者在树里选中设备后，从下拉选品牌 → 点"应用预设"，即用该品牌的
    /// 默认配置整段替换当前设备参数（之后可继续微调、再保存）。
    ///
    /// 【设计意图】品牌差异多数是"参数差异"（寄存器地址/命令码/端口/指令不同），
    /// 预设把这些差异收敛成一次选择；少数组件的"协议差异"仍需改库（见 AGENTS.md 边界）。
    /// </summary>
    public class BrandPreset
    {
        /// <summary>品牌/型号显示名（下拉里展示），如 "基恩士 IV4"</summary>
        public string Name { get; private set; }

        /// <summary>该预设的用途说明（下拉 tooltip/日志用）</summary>
        public string Description { get; private set; }

        /// <summary>返回该品牌的全新默认配置实例（每次调用返回新对象，不共享引用）</summary>
        public Func<object> CreateDefault { get; private set; }

        /// <summary>构造一个品牌预设</summary>
        /// <param name="name">显示名</param>
        /// <param name="description">说明</param>
        /// <param name="createDefault">创建该品牌默认配置实例的委托</param>
        public BrandPreset(string name, string description, Func<object> createDefault)
        {
            Name = name;
            Description = description;
            CreateDefault = createDefault;
        }

        /// <summary>重载 ToString 便于 ComboBox 显示</summary>
        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// 内置品牌预设库：按设备种类返回可选品牌列表。
    ///
    /// 【现状】V1.3.0 第一版内置各设备"现场实测厂商"的默认参数（与库默认值对齐），
    /// 并给送风机造了一个"另一厂商示例映射"演示参数差异；后续接新品牌时在此追加，
    /// 编辑器代码不用改（新增设备类型/品牌只在数据层）。
    /// </summary>
    public static class BrandPresets
    {
        /// <summary>
        /// 按设备种类返回该设备可选的品牌预设列表。
        /// 分组节点/全局/图像等没有品牌差异的返回空列表（MainForm 会隐藏预设区）。
        /// </summary>
        /// <param name="kind">设备树节点种类</param>
        /// <returns>品牌预设列表（可能为空）</returns>
        public static List<BrandPreset> For(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.PlcSlave:
                    return new List<BrandPreset>
                    {
                        new BrandPreset("汇川 PLC 从站（标准 502）",
                            "上位机作 Modbus TCP 从站，监听 502 等 PLC 主站来读写；请求-结果-复位三拍握手",
                            () => new PlcConfig()),
                    };

                case NodeKind.PlcMaster:
                    return new List<BrandPreset>
                    {
                        new BrandPreset("汇川 PLC 主站（标准 502）",
                            "上位机作 Modbus TCP 主站，主动连 PLC 读写 + 自动轮询",
                            () => new PlcMasterConfig()),
                    };

                case NodeKind.PlcMultiBrand:
                    // 多品牌主站预设：协议能力来自 HslCommunication 库（V1.6.0 集成）。
                    // 各品牌差异=端口默认值+地址语法，预设把端口填成该品牌的行业默认（Port=0 也等效）。
                    return new List<BrandPreset>
                    {
                        new BrandPreset("三菱 MC 协议（Q/iQ-R，6000）",
                            "二进制通讯；地址如 D100/M10/X20，默认端口 6000",
                            () => new MultiBrandPlcConfig { Brand = MultiBrandPlcBrand.MelsecMc, Port = 6000 }),
                        new BrandPreset("三菱 A1E 协议（A/Q 兼容口，6000）",
                            "A1E 兼容口；地址如 D100/M10，默认端口 6000",
                            () => new MultiBrandPlcConfig { Brand = MultiBrandPlcBrand.MelsecA1E, Port = 6000 }),
                        new BrandPreset("欧姆龙 FINS/TCP（CS/CJ/CP，9600）",
                            "PLC 工程师指定的视觉上位机↔欧姆龙通讯方式；地址如 CIO100/W10/D100，端口 9600",
                            () => new MultiBrandPlcConfig { Brand = MultiBrandPlcBrand.OmronFins, Port = 9600 }),
                        new BrandPreset("欧姆龙 EtherNet/IP(CIP)（NJ/NX，44818）",
                            "按 CIP 标签名寻址（备用通道）；Slot=0，端口 44818",
                            () => new MultiBrandPlcConfig { Brand = MultiBrandPlcBrand.OmronCip, Port = 44818 }),
                        new BrandPreset("汇川 H5U（Modbus TCP 兼容口，502）",
                            "H5U 走 Modbus TCP 兼容口；地址如 D100/M10，端口 502",
                            () => new MultiBrandPlcConfig { Brand = MultiBrandPlcBrand.InovanceH5U, Port = 502 }),
                        new BrandPreset("西门子 S7-1200（ISO-on-TCP，102）",
                            "S1200/S1500 走 ISO-on-TCP；地址如 DB1.DBW0/M10.0，Rack=0 Slot=0，端口 102",
                            () => new MultiBrandPlcConfig { Brand = MultiBrandPlcBrand.SiemensS7, SiemensModel = "S1200", Port = 102 }),
                        new BrandPreset("西门子 S7-300/400（102，槽位2）",
                            "S300/S400 常见 Rack=0 Slot=2；改型号请同步改 SiemensModel 字段",
                            () => new MultiBrandPlcConfig { Brand = MultiBrandPlcBrand.SiemensS7, SiemensModel = "S300", SiemensSlot = 2, Port = 102 }),
                    };

                case NodeKind.Camera:
                    return new List<BrandPreset>
                    {
                        new BrandPreset("基恩士 IV4（无协议 TCP + FTP 取图）",
                            "触发/判定走 TCP 无协议指令（T1/T2/RT），图走 FTP 推送，判定即写 PLC",
                            () => new CameraConfig()),
                    };

                case NodeKind.Scanner:
                    return new List<BrandPreset>
                    {
                        new BrandPreset("基恩士 SR（TCP 无协议）",
                            "以太网无协议通讯，连上后发 LON 触发读码",
                            () => new ScanConfig { Mode = "Tcp" }),
                        new BrandPreset("基恩士 SR / 康耐视 DataMan（EtherNet/IP）",
                            "V1.6.0 新增：扫码枪作 EIP Adapter，上位机周期读 Assembly 取码（默认实例 4，200ms），无需触发指令",
                            () => new ScanConfig { Mode = "Eip" }),
                        new BrandPreset("Honeywell Xenon 1902（串口）",
                            "串口 RS-232，上电即读码，WMI 按关键词自动识别端口",
                            () => new ScanConfig
                            {
                                Mode = "Serial",
                                PortName = "",
                                DeviceKeyword = "Xenon 1902",
                                BaudRate = 115200,
                                DataBits = 8,
                                StopBits = "1",
                                Parity = "None",
                            }),
                    };

                case NodeKind.Barometer:
                    return new List<BrandPreset>
                    {
                        new BrandPreset("通用气压表（Modbus RTU）",
                            "RS485→USB(CH340)，读压力 0x04@0x0001、写阈值 0x06@0x0010，kPa 一位小数",
                            () => new BarometerConfig()),
                    };

                case NodeKind.Io:
                    return new List<BrandPreset>
                    {
                        new BrandPreset("三菱 GX-CL140（Modbus TCP）",
                            "DI 0x04@0x1000、DO 0x03/0x06@0x2000，16 点/寄存器，读-改-写单点",
                            () => new IoConfig()),
                    };

                case NodeKind.Fan:
                    return new List<BrandPreset>
                    {
                        new BrandPreset("现场实测控制屏（默认映射）",
                            "端口 50000；0x0001 控制字（0x0003=定值启动/0x0002=定值停止），0x0002~0x0005 温湿度及设定",
                            () => new FanConfig()),
                        new BrandPreset("另一厂商示例（映射不同）",
                            "示例：控制寄存器换到 0x0100、命令码换 1/2、温湿度字段偏移不变——演示「换品牌只改配置」",
                            () => new FanConfig
                            {
                                FanControlAddress = 0x0100,
                                FanStartCommand = 1,
                                FanStopCommand = 2,
                            }),
                    };

                default:
                    // 分组节点/全局/图像存储无品牌差异
                    return new List<BrandPreset>();
            }
        }
    }
}