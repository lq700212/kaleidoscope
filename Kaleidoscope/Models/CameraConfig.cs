using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// 相机通讯配置（从 CommandCenter/Models/AppConfig.cs 抽取，原 CameraConfig 类）。
    /// 适用：基恩士 IV4 系列，"TCP/IP 无协议通信"（最多 2 路连接）：
    ///   - 触发拍摄：上位机往相机的 CommandPort 发 ASCII 指令（T1/T2/RT，判定结果走指令回帧）；
    ///   - 图像回传（ImageSource 两种来源二选一）：
    ///       Ftp：相机作为 FTP 客户端把照片推到 ImageConfig.FtpRootDir / 本机 FtpUploadDir，
    ///            上位机用 ImageStore 监听新文件（默认，成熟）；
    ///       Tcp：上位机发 BR 指令直接从相机读最新图像（24bit 位图），免 FTP 落盘中转。
    /// 具体指令帧格式需以《IV4 系列通信、连接指南》为准，本模型可配帧字符串。
    /// </summary>
    public class CameraConfig
    {
        /// <summary>
        /// 相机编号/相机ID：基恩士相机真正的编号，常与存图目录号一一对应（现场惯例）。
        /// 纯标识/展示字段，不参与通讯逻辑。若业务需要"窗口↔点位反查唯一键"，用它关联。
        /// </summary>
        [DisplayName("相机编号")]
        [Description("基恩士相机真正的编号，常与存图目录号一一对应；纯标识字段")]
        public int CameraId { get; set; }

        /// <summary>相机名称/位置（界面与日志显示用），如"上相机"/"下相机"。</summary>
        [DisplayName("相机名称")]
        [Description("相机名称/位置（界面与日志显示用），如 上相机/下相机")]
        public string Name { get; set; } = "";

        /// <summary>相机 IP</summary>
        [DisplayName("相机 IP")]
        [Description("相机 IP 地址")]
        public string IpAddress { get; set; } = "192.168.0.10";

        /// <summary>
        /// 该相机的 FTP 上传目录（相机作为 FTP 客户端把照片推到这台，独立监听）。
        /// 为空时回退用全局 ImageConfig.FtpRootDir——多台相机务必各自配不同目录，否则图会混。
        /// </summary>
        [DisplayName("FTP 上传目录")]
        [Description("该相机 FTP 上传目录（独立监听）；留空回退全局 FtpRootDir，多台务必分开配")]
        public string FtpUploadDir { get; set; } = "";

        /// <summary>控制指令发送端口（基恩士无协议通信常用 8500，按现场实际改）</summary>
        [DisplayName("指令端口")]
        [Description("控制指令发送端口（基恩士无协议通信常用 8500）")]
        public int CommandPort { get; set; } = 8500;

        /// <summary>
        /// 本相机在 PLC 从站的"拍照请求" DataStore 索引（每台相机一路 PLC 通道）。
        /// PLC 触发本相机拍照时写该地址=点位编号（1~255）、读走结果后复位写 0。
        /// 配置=DataStore 索引（PLC 协议号 = 索引 + 40000，填 2 就是协议 40002）。
        /// 0=未配置该相机通道——运行时不轮询此相机（请求恒视为无），
        /// 新增相机必须与 PLC 梯形图协商好寄存器后在此显式填写。
        /// </summary>
        [DisplayName("PLC 拍照请求地址")]
        [Description("PLC 从站 DataStore 索引（协议号=索引+40000）；0=未配置该相机通道，运行时不轮询")]
        public int PlcRequestAddress { get; set; }

        /// <summary>
        /// 本相机在 PLC 从站的"拍照结果" DataStore 索引（上位机写：0=复位、1=OK、2=NG、3=点位禁用跳过）。
        /// 0=未配置结果通道（PlcService.WriteCameraResult 跳过该台不写）。
        /// </summary>
        [DisplayName("PLC 拍照结果地址")]
        [Description("PLC 从站 DataStore 索引：0=复位/1=OK/2=NG/3=点位禁用跳过；0=未配置结果通道")]
        public int PlcResultAddress { get; set; }

        // ─── IV4 无协议通信指令表（《IV4 通信、连接指南》）───
        // 指令均以 CR(0x0D) 终止；T 系列指令含义见服务类注释

        /// <summary>仅触发拍摄指令（T1[CR]），响应回显 T1。用于"只触发、判定另取"场景。</summary>
        [DisplayName("仅触发指令")]
        [Description("仅触发拍摄指令（T1[CR]），响应回显 T1；用于只触发、判定另取场景")]
        public string TriggerCommand { get; set; } = "T1";

        /// <summary>触发＋读取判定结果指令（T2[CR]），响应 RT, 工具结果(标准/详细)[CR]。</summary>
        [DisplayName("触发+判定指令")]
        [Description("触发＋读取判定结果指令（T2[CR]），响应 RT, 工具结果[CR]")]
        public string TriggerAndReadCommand { get; set; } = "T2";

        /// <summary>单独读取判定结果指令（RT[CR]），响应同 T2。</summary>
        [DisplayName("读判定结果指令")]
        [Description("单独读取判定结果指令（RT[CR]），响应同 T2")]
        public string ReadResultCommand { get; set; } = "RT";

        /// <summary>
        /// 本相机的"点位→相机程序号"映射表（每相机一张表 + 按型号分表见 ModelStationPrograms）。
        /// 【为什么用"每相机一张表"】现场多台相机分工拍摄不同点位（如 28 个点位上相机拍一部分、
        /// 下相机拍另一部分），不是每台相机都拍全部点位。某相机表里配了哪些点位 == 这台相机负责
        /// 拍哪些点位；且不同相机的同名程序（P000/P001…）是各相机自己的程序库，互相独立。
        /// 本字段作为"无型号/默认"的表兜底——运行时优先查本相机 ModelStationPrograms 里与当前
        /// 产品型号同名的那张表，没配该型号的表才回退本默认表。
        /// 【触发逻辑】触发前查出"本轮该相机要填的窗口"对应点位号，在本表查该点位对应程序号：
        /// 命中→发 PW 切换后再触发；未命中→不切换（该点位不归本相机/还没配映射）。
        /// </summary>
        [DisplayName("默认点位→程序映射表")]
        [Description("本相机默认的点位→相机程序号映射表（双击展开编辑）；未按型号分表时用它")]
        public List<StationProgramItem> StationPrograms { get; set; } = new List<StationProgramItem>();

        /// <summary>
        /// 本相机按【产品型号】分组的"点位→相机程序号"映射表。
        /// 现场同一台相机的程序库分型号：不同产品型号对应的相机程序号不同、点位归属也不同。
        /// 切型号后查对应型号的表切程序，型号没配的表就回退 StationPrograms 默认表。
        /// </summary>
        [DisplayName("按型号分程序表")]
        [Description("按产品型号分组的点位→程序号映射表（双击展开编辑）；型号没配表时回退默认表")]
        public List<ModelStationPrograms> ModelStationPrograms { get; set; } = new List<ModelStationPrograms>();

        /// <summary>
        /// 当前产品型号下的"点位→程序号"映射表：
        ///   ① 优先在 ModelStationPrograms 里找与指定型号同名的那张表（大小写不敏感），命中即返回它；
        ///   ② 型号没配表 / ModelStationPrograms 为空 → 回退默认表 StationPrograms（旧兼容 + 不区分型号）。
        /// 返回的列表可能为空（该相机在当前型号下没有任何点位配置），调用方按空表处理即可。
        /// 自适应窗口布局、运行时"PLC点位→窗口"解析、点位矩阵相机标签展示三处共用本查表逻辑。
        /// </summary>
        public List<StationProgramItem> ProgramsFor(string productModel)
        {
            if (!string.IsNullOrWhiteSpace(productModel) && ModelStationPrograms != null)
            {
                foreach (var m in ModelStationPrograms)
                {
                    if (m != null && m.Programs != null
                        && string.Equals(m.ModelName, productModel, StringComparison.OrdinalIgnoreCase))
                        return m.Programs;
                }
            }
            return StationPrograms ?? new List<StationProgramItem>();
        }

        /// <summary>
        /// 判定结果输出格式（OF 指令）：留空/非法则不发送（相机用默认标准格式）。
        /// 可选值（固定 2 字符）：
        ///   "00" 标准（多主控无效/分类）——T2 响应 "RT,工具结果(标准)[CR]"（默认，8 位判定位）；
        ///   "01" 详细（多主控无效/分类）——T2 响应 "RT,工具结果(详细)[CR]"；
        ///   "02" 标准（主控编号）；"03" 详细（主控编号）。
        /// 触发前若配置则先发 "OF,nn[CR]"（响应 "OF[CR]"）再切程序/触发。
        /// </summary>
        [DisplayName("判定结果输出格式")]
        [Description("OF 指令参数：留空/非法则不发送（默认标准格式）；可选 00/01/02/03")]
        public string OutputFormat { get; set; } = "";

        /// <summary>
        /// 是否让相机直接回传判定结果（T2）。
        /// true(默认)：判定 OK/NG 直接来自 IV4 内部判定，准确；
        /// false：退化为"FTP 图到达即记 OK"的旧逻辑（仅现场未配判定时用）。
        /// </summary>
        [DisplayName("相机直接回传判定")]
        [Description("true=判定来自 IV4 内部（准确）；false=退化为图到达即记 OK 的旧逻辑")]
        public bool ReadResultFromCamera { get; set; } = true;

        /// <summary>
        /// 判定合格字符：标准结果里 8 位中的"合格位"。默认 '0' 表示该工具 OK。
        /// IV4 约定：'0'=OK、'1'=NG；另有 '4'(未进行) / '-'(该工具未启用)。
        /// 遇 '4'/'-'/未知一律保守判 NG，避免漏放不良。
        /// </summary>
        [DisplayName("判定合格字符")]
        [Description("判定合格位：IV4 约定 0=OK、1=NG；遇 4/-/未知一律保守判 NG")]
        public string OkChar { get; set; } = "0";

        /// <summary>等待一条指令响应（除 Connect 外，如 T2/RT 的拍摄+判定耗时）毫秒数</summary>
        [DisplayName("指令响应超时(ms)")]
        [Description("等待一条指令响应（如 T2/RT 拍摄+判定耗时）的毫秒数")]
        public int ResponseTimeoutMs { get; set; } = 5000;

        /// <summary>单次收发包超时（毫秒），防相机掉线后调用线程卡死</summary>
        [DisplayName("收发超时(ms)")]
        [Description("单次收发包超时，防相机掉线后调用线程卡死")]
        public int TimeoutMs { get; set; } = 3000;

        /// <summary>触发后等相机 FTP 新图的最长毫秒数（超时视为取像失败）</summary>
        [DisplayName("等图超时(ms)")]
        [Description("触发后等相机 FTP 新图的最长毫秒数，超时视为取像失败")]
        public int ImageWaitMs { get; set; } = 10000;

        /// <summary>
        /// 取图来源（大小写不敏感）：
        ///   "Ftp"（默认）：相机作 FTP 客户端把照片推到上位机目录，上位机监听新图（现方案，成熟稳定）；
        ///   "Tcp"       ：上位机发 BR 指令直接从相机读最新图像（24bit 位图），触发后同步读回，
        ///                  链路更短（不经过 FTP 服务器落盘中转），依赖相机的 TCP/IP 无协议通信；
        /// 其他取值一律按 Ftp 兜底（旧配置无需迁移）。
        /// </summary>
        [DisplayName("取图来源")]
        [Description("Ftp=相机 FTP 推图上位机监听（默认）；Tcp=上位机发 BR 直接读图；其他值按 Ftp 兜底")]
        public string ImageSource { get; set; } = "Ftp";

        /// <summary>读取图像数据指令名（仅 ImageSource=="Tcp" 时使用）：IV4 手册原文 "BR,m[CR]"。</summary>
        [DisplayName("读图指令名")]
        [Description("仅取图来源=Tcp 时使用；IV4 手册原文 BR,m[CR]")]
        public string ReadImageCommand { get; set; } = "BR";

        /// <summary>BR 指令的数据格式参数 m（拼成 "BR,m" 发送）。m=压缩率："0"=无压缩；"1"=1/2 压缩。</summary>
        [DisplayName("读图压缩率")]
        [Description("仅取图来源=Tcp 时使用：0=无压缩；1=1/2 压缩")]
        public string ReadImageMode { get; set; } = "1";

        /// <summary>相机 FTP 主动上传目录 = 上位机 ImageConfig.FtpRootDir，上位机用 FileSystemWatcher 监听新图。</summary>
        [DisplayName("启用 FTP 监听")]
        [Description("相机 FTP 上传目录 = 上位机 ImageConfig.FtpRootDir，用 FileSystemWatcher 监听新图")]
        public bool EnableFtpMonitor { get; set; } = true;

        /// <summary>
        /// 构建一张"点位→程序号"映射表（默认相机预置表用）。每个参数是 (点位, 程序号) 元组，
        /// 直接转成 StationProgramItem。程序号范围 0~127（0 是合法程序 P000）。
        /// </summary>
        private static List<StationProgramItem> Table(params (int station, int program)[] rows)
        {
            var list = new List<StationProgramItem>();
            if (rows != null)
            {
                foreach (var r in rows)
                    list.Add(new StationProgramItem { StationNo = r.station, ProgramNo = r.program });
            }
            return list;
        }

        /// <summary>
        /// 现场默认相机（两台，IP/编号/存图目录按 CommandCenter 现场定稿；DeviceHub 在
        /// DeviceHubConfig.Cameras 为空列表时用本方法兜底）。返回全新实例列表，调用方可
        /// 直接 AddRange/遍历，不共享引用。
        ///
        /// 【为什么默认值写在这里而不是写死在 DeviceHub】
        /// 现场换相机 IP/目录时只需改这一处（或直接配好 DeviceHubConfig.Cameras，本方法
        /// 根本不会被调用）。基恩士真编号 = 存图目录号：上相机=2（D:\IV存图\2）、下相机=1（\1），
        /// 上相机的 PLC 请求/结果通道=2/5、下相机=3/6（协议号=索引+40000）。
        /// </summary>
        public static List<CameraConfig> DefaultCameras()
        {
            return new List<CameraConfig>
            {
                new CameraConfig
                {
                    Name = "上相机",
                    CameraId = 2,
                    IpAddress = "19.87.6.213",
                    FtpUploadDir = @"D:\IV存图\2",
                    PlcRequestAddress = 2,
                    PlcResultAddress = 5,
                    ModelStationPrograms = new List<ModelStationPrograms>
                    {
                        new ModelStationPrograms { ModelName = "U171", Programs = Table(
                            (1, 0), (2, 1), (3, 2), (4, 2), (5, 2), (6, 2), (7, 3), (8, 4),
                            (9, 5), (10, 6), (11, 7), (12, 8), (13, 9), (14, 10), (15, 10),
                            (16, 11), (17, 12), (18, 10), (19, 10), (20, 1)) },
                        new ModelStationPrograms { ModelName = "Z121", Programs = Table(
                            (1, 13), (2, 14), (3, 14), (4, 28), (5, 15), (6, 15), (7, 15),
                            (8, 15), (9, 15), (10, 16), (11, 17), (12, 18), (13, 18),
                            (14, 19), (15, 20), (16, 21), (17, 21), (18, 22), (19, 23),
                            (20, 19), (21, 24), (22, 25), (23, 26), (24, 26), (25, 27), (26, 27)) }
                    }
                },
                new CameraConfig
                {
                    Name = "下相机",
                    CameraId = 1,
                    IpAddress = "19.87.6.212",
                    FtpUploadDir = @"D:\IV存图\1",
                    PlcRequestAddress = 3,
                    PlcResultAddress = 6,
                    ModelStationPrograms = new List<ModelStationPrograms>
                    {
                        new ModelStationPrograms { ModelName = "U171", Programs = Table(
                            (1, 0), (2, 1), (3, 2), (4, 3)) },
                        new ModelStationPrograms { ModelName = "Z121", Programs = Table(
                            (1, 5), (2, 6), (3, 7)) }
                    }
                }
            };
        }
    }

    /// <summary>
    /// 单个"点位→相机程序号"映射条目（装在 CameraConfig.StationPrograms 列表里）。
    /// 每个条目含义：本相机在拍照"点位 StationNo"前，先发 PW 把相机切到"程序 ProgramNo"。
    /// - StationNo：拍照点位号（1~9999，相机局部点位，各相机独立）；
    /// - ProgramNo：相机程序号（0~127 合法，0 也是真实程序——注意别把 0 当"未设置"）。
    /// </summary>
    public class StationProgramItem
    {
        /// <summary>拍照点位号（1~9999，相机局部点位：各相机各自从 1 起、会重复）</summary>
        [DisplayName("点位号")]
        [Description("拍照点位号（1~9999，相机局部点位，各相机独立）")]
        public int StationNo { get; set; }

        /// <summary>该点位在本相机上对应的相机程序号（0~127，0 合法）</summary>
        [DisplayName("相机程序号")]
        [Description("该点位在本相机上对应的相机程序号（0~127，0 也是合法程序）")]
        public int ProgramNo { get; set; } = -1;
    }

    /// <summary>
    /// 某个【产品型号】下，本相机的"点位→相机程序号"映射表（装在
    /// CameraConfig.ModelStationPrograms 列表里，每个型号一张表）。
    /// - ModelName：产品型号名（运行时按名称匹配，大小写不敏感）；
    /// - Programs：与 StationPrograms 相同结构的点位→程序号表（StationProgramItem 列表）。
    /// 触发切程序时按当前型号查本表；型号没配表 → 回退 CameraConfig.StationPrograms 默认表。
    /// </summary>
    public class ModelStationPrograms
    {
        /// <summary>产品型号名（匹配忽略大小写）</summary>
        [DisplayName("型号名")]
        [Description("产品型号名，按名称匹配（大小写不敏感）")]
        public string ModelName { get; set; } = "";

        /// <summary>该型号下本相机的"点位→程序号"映射表（结构同 StationPrograms）</summary>
        [DisplayName("点位→程序映射表")]
        [Description("该型号下本相机的点位→程序号映射表（双击展开编辑）")]
        public List<StationProgramItem> Programs { get; set; } = new List<StationProgramItem>();
    }
}
