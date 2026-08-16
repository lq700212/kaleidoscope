using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// PLC 通讯配置（从 CommandCenter/Models/AppConfig.cs 抽取，原 PlcConfig 类）。
    ///
    /// 【协议模式：Modbus TCP 从站（角色反转）】
    /// 现场汇川 PLC 做 Modbus TCP 主站，上位机做从站——上位机监听本机 Port 端口（标准 502），
    /// 等汇川主站 TCP 连入并读写上位机的保持寄存器区。上位机【不主动发起任何 Modbus 请求】。
    /// IpAddress = 上位机监听绑定 IP（"0.0.0.0"=监听所有网卡，现场主机多网卡时可绑指定 IP）。
    ///
    /// 【V2.7 协议（docs/CommandCenter.md §5.5）】请求-结果-复位三拍式握手：
    ///   PLC只写（上位机读）：40001 扫码请求(0/1)、40002 上相机拍照请求(1~255=点位)、40003 下相机拍照请求；
    ///   PLC只读（上位机写）：40004 扫码结果(0/1/2)、40005 上相机结果、40006 下相机结果、
    ///                        40007~40011 产品型号(10 字符 ASCII，每寄存器 2 字符，高字节在前)。
    ///   流程：PLC 写请求=非0 → 上位机处理完写结果≠0 → PLC 读结果并复位请求=0 →
    ///         上位机看到请求=0 再复位结果=0，进入下一请求。
    ///   【地址说明】配置里的地址字段统一存【DataStore 索引】，PLC 侧协议号 = 索引 + 40000
    ///   （协议 40002 上相机请求 → 索引 2）。现场实测：PLC 写协议 40002 → 从站 DataStore[2]。
    ///   索引就是汇川 PLC D 区习惯叫的 "D2/D3/D5" 这类数字，填 2 就是 2，业务层【零换算】。
    /// </summary>
    public class PlcConfig
    {
        /// <summary>上位机从站监听绑定 IP（"0.0.0.0"=所有网卡；多网卡可填 19.87.6.230 绑定指定网卡）</summary>
        [DisplayName("监听 IP")]
        [Description("上位机从站监听绑定 IP；0.0.0.0=监听所有网卡（现场主机多网卡时可填指定 IP 绑定单网卡）")]
        public string IpAddress { get; set; } = "0.0.0.0";

        /// <summary>上位机从站监听端口，Modbus TCP 标准 502</summary>
        [DisplayName("监听端口")]
        [Description("上位机从站监听端口，Modbus TCP 标准 502")]
        public int Port { get; set; } = 502;

        /// <summary>上位机从站 UnitId（需与汇川主站通讯指令里的 UnitId 一致，默认 1）</summary>
        [DisplayName("从站单元号 UnitId")]
        [Description("需与 PLC 主站通讯指令里的 UnitId 一致，默认 1")]
        public byte UnitId { get; set; } = 1;

        /// <summary>单次读写超时（毫秒，从站模式主要用于日志/容错，不再阻塞主动连接）</summary>
        [DisplayName("读写超时(ms)")]
        [Description("从站模式主要用于日志/容错，不阻塞主动连接")]
        public int TimeoutMs { get; set; } = 2000;

        // ─── 寄存器地址映射（协议，见类注释）───
        // 设计原则：定长请求放前面，结果与变长数据（型号）放后面，地址可向后扩展。

        /// <summary>PLC→上位机：扫码请求。PLC 写 1=请求扫码、0=无请求；上位机读到 1 触发扫码枪。配置=DataStore 索引 1（协议 40001）。</summary>
        [DisplayName("扫码请求地址")]
        [Description("PLC→上位机扫码请求寄存器（DataStore 索引，协议号=索引+40000）；PLC 写 1 触发扫码")]
        public ushort ScanRequestAddress { get; set; } = 1;

        /// <summary>上位机→PLC：扫码结果。0=默认/复位，1=扫码OK，2=扫码NG（超时）。配置=索引 4（协议 40004）。</summary>
        [DisplayName("扫码结果地址")]
        [Description("上位机→PLC 扫码结果寄存器：0=复位/1=OK/2=NG（超时）")]
        public ushort ScanResultAddress { get; set; } = 4;

        /// <summary>上位机→PLC：产品型号序号地址（协议 40007）。每次写型号时先把"该型号对应的序号"
        /// 写入本寄存器（型号序号来自 ModelIndexes 映射）；PLC 拿 40007 的序号即可快速区分型号，
        /// 不必解析型号字符串。</summary>
        [DisplayName("型号序号地址")]
        [Description("上位机→PLC 产品型号序号寄存器（协议 40007），写 ModelIndexes 里查到的序号")]
        public ushort ProductModelIndexAddress { get; set; } = 7;

        /// <summary>上位机→PLC：产品型号起始地址（连续写 ProductModelLen 个寄存器，最多 10 字符）。
        /// 配置=索引 8（协议 40008）——40007 已让给型号序号，型号字符串整体后移一位从 40008 起写。</summary>
        [DisplayName("型号起始地址")]
        [Description("上位机→PLC 产品型号字符串起始寄存器（协议 40008），连续写 ProductModelLen 个")]
        public ushort ProductModelAddress { get; set; } = 8;

        /// <summary>产品型号寄存器数（每个寄存器 2 字符，默认 5 个=10 字符；超 10 字符按文档
        /// 从索引 13（协议 40013）扩展地址后调整本值，40007 序号位不受影响）。</summary>
        [DisplayName("型号寄存器数")]
        [Description("每个寄存器 2 字符，默认 5 个=10 字符；型号超 10 字符时改本值与起始地址")]
        public int ProductModelLen { get; set; } = 5;

        /// <summary>
        /// 产品型号 → PLC 型号序号 映射表：
        /// 每个产品型号对应一个 PLC 序号（写 40007），运行时 PlcService.WriteProductModel
        /// 按型号名（忽略大小写）查本表得序号写 40007；型号没配序号时写 0（PLC 端视为未配置）。
        /// </summary>
        [DisplayName("型号→序号映射表")]
        [Description("产品型号名 → PLC 序号 映射（双击展开编辑）；型号没配序号时 PLC 端写 0")]
        public List<ModelIndexItem> ModelIndexes { get; set; } = new List<ModelIndexItem>();

        /// <summary>现场默认"产品型号 → PLC 序号"映射（如 Z121=1、U171=2）。
        /// 返回全新列表实例，调用方可直接修改/复制，不共享引用。</summary>
        public static List<ModelIndexItem> DefaultModelIndexes() =>
            new List<ModelIndexItem>
            {
                new ModelIndexItem { ModelName = "Z121", ModelIndex = 1 },
                new ModelIndexItem { ModelName = "U171", ModelIndex = 2 },
            };
    }

    /// <summary>
    /// 型号→PLC 序号映射项（JSON 数组元素）：一个产品型号对应一个 PLC 序号。
    /// - ModelName：产品型号名（与产品型号候选对应，匹配忽略大小写）；
    /// - ModelIndex：该型号在 PLC 40007 寄存器里的序号（&gt;0 有效，0=未配置/不写序号）。
    /// </summary>
    public class ModelIndexItem
    {
        /// <summary>产品型号名（匹配忽略大小写）</summary>
        [DisplayName("型号名")]
        [Description("产品型号名，与型号候选一致，匹配忽略大小写")]
        public string ModelName { get; set; } = "";

        /// <summary>该型号的 PLC 型号序号（写 40007，&gt;0 有效，0=未配置）</summary>
        [DisplayName("PLC 序号")]
        [Description("该型号对应的 PLC 40007 序号，>0 有效，0=未配置")]
        public int ModelIndex { get; set; }
    }
}
