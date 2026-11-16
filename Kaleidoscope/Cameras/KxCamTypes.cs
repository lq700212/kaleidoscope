using System;
using System.Net;

namespace Kaleidoscope.Cameras
{
    /// <summary>相机品牌（库中性枚举；宿主业务枚举自行映射）。</summary>
    public enum KxCamBrand
    {
        /// <summary>大恒（GxIAPINET / Galaxy SDK）。</summary>
        DaHeng,
        /// <summary>海康 2D（MvCameraControl.Net / MVS，GigE+USB3 双传输层）。</summary>
        Hik2D,
        /// <summary>海康 3D 线激光（Mv3dLpNet）。</summary>
        Hik3D,
    }

    /// <summary>
    /// 发现到的一台相机（品牌 + SN + 型号 + 传输层 + 网络信息）。
    /// SN 已 Trim（海康/3D 原串带尾随 '\0'），可直接存配置/做相等比对。ToString 即下拉显示文本。
    /// （对标 HJVision CamSDK.CamFoundInfo 完整版；Net 层最小 DTO 由宿主自行投影。）
    /// </summary>
    public class KxCamFoundInfo
    {
        public KxCamBrand Brand;
        public string SN = "";
        public string Model = "";
        public string Transport = "";
        public string Ip = "";
        public string Mask = "";
        public string Gateway = "";
        public string Mac = "";
        public override string ToString()
        {
            string show = (SN ?? "").Trim();
            string extra = ((Model ?? "").Trim() + " " + (Transport ?? "").Trim()).Trim();
            return extra.Length > 0 ? (show + "  [" + extra + "]") : show;
        }
    }

    /// <summary>
    /// GigE 相机 IP 纯函数集（来源：HJVision Core/Net/CamIpConfig，一比一搬；无 SDK 引用）。
    /// 海康 UInt32 字节序：大端（最高字节在前，0xC0A8010A=192.168.1.10），
    /// 显示与回写走同一对函数，保证往返一致。
    /// 【校验与 SDK 执行分离】本类零 SDK 类型引用——GxIAPINET 是 C++/CLI 混合程序集，
    /// 缺驱动的机器上 JIT 一碰含 IGXFactory 引用的方法即 FileNotFound；
    /// 拆开后纯校验在任何机器可测，执行（KxGigEIpAdmin）缺驱动时走正常错误串。
    /// </summary>
    public static class KxCamIpUtil
    {
        /// <summary>海康 UInt32（大端）→点分串，如 0xC0A8010A→"192.168.1.10"。</summary>
        public static string GigEUintToString(UInt32 ip)
        {
            return ((ip >> 24) & 0xFF).ToString() + "." + ((ip >> 16) & 0xFF).ToString()
                + "." + ((ip >> 8) & 0xFF).ToString() + "." + (ip & 0xFF).ToString();
        }

        /// <summary>
        /// 点分串→海康 UInt32（大端）；非法返回 false（空串/非 IP/非 IPv4 一律拒，Trim 容忍空格）。
        /// 与 GigEUintToString 严格往返一致。
        /// </summary>
        public static bool TryParseIpUint(string text, out UInt32 ip)
        {
            ip = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            IPAddress addr;
            if (!IPAddress.TryParse(text.Trim(), out addr)) return false;
            byte[] b = addr.GetAddressBytes();
            if (b == null || b.Length != 4) return false;   // 只收 IPv4，IPv6 拒绝
            ip = ((UInt32)b[0] << 24) | ((UInt32)b[1] << 16) | ((UInt32)b[2] << 8) | b[3];
            return true;
        }

        /// <summary>
        /// 点分串合法性（IP 行输入校验用；IPv4 才算，Trim 容忍空格）。
        /// 注意 0.0.0.0 也算"合法格式"（DHCP 未分配时 SDK 就报这个），同网段判断另做。
        /// </summary>
        public static bool IsValidIpv4(string text)
        {
            UInt32 ip;
            return TryParseIpUint(text, out ip);
        }

        /// <summary>
        /// 目标 IP 与本机某网卡是否同网段（持久化写入前置检查；任一网卡同网段即过）。
        /// mask 非法/网卡取不到→false（宁可拦下让现场检查，不写了失联）。
        /// </summary>
        public static bool IsSameSubnetWithHost(string targetIp, string mask)
        {
            UInt32 ip, mk;
            if (!TryParseIpUint(targetIp, out ip) || !TryParseIpUint(mask, out mk) || mk == 0) return false;
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface nic
                    in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic == null || nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    System.Net.NetworkInformation.IPInterfaceProperties props = nic.GetIPProperties();
                    if (props == null || props.UnicastAddresses == null) continue;
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation addr in props.UnicastAddresses)
                    {
                        if (addr == null || addr.Address == null) continue;
                        byte[] b = addr.Address.GetAddressBytes();
                        if (b == null || b.Length != 4) continue;
                        UInt32 host = ((UInt32)b[0] << 24) | ((UInt32)b[1] << 16) | ((UInt32)b[2] << 8) | b[3];
                        if ((host & mk) == (ip & mk)) return true;
                    }
                }
            }
            catch { return false; }
            return false;
        }

        /// <summary>
        /// 掩码框自动补齐（纯函数）：掩码框已有内容→原样不动；为空且 IP 合法→补
        /// "255.255.255.0"；IP 非法→不动。调用时机：掩码框获得焦点时调一次。
        /// </summary>
        public static string SuggestMask(string ip, string mask)
        {
            string m = (mask ?? "").Trim();
            if (m.Length > 0) return m;
            if (!IsValidIpv4(ip)) return m;
            return "255.255.255.0";
        }

        /// <summary>
        /// 网关框自动补齐（纯函数）：网关框已有内容→不动；为空且 IP 合法→补"同网段 .1"
        /// （如 192.168.1.10→192.168.1.1）；IP 非法→不动。
        /// </summary>
        public static string SuggestGateway(string ip, string gateway)
        {
            string g = (gateway ?? "").Trim();
            if (g.Length > 0) return g;
            UInt32 nip;
            if (!TryParseIpUint(ip, out nip)) return g;
            return GigEUintToString((nip & 0xFFFFFF00) | 0x00000001);
        }
    }
}
