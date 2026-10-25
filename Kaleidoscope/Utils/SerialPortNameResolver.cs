// =====================================================================================
// 【下沉注记】本文件 2026-10-25 自 HJVision-WPF 生产工程（第二轮仪器设备下沉）
// 原样搬入，口径与第一轮 PlcNetCommQue/TcpChannel 下沉相同：
//   ①来源 GYZVision.Core/Util/SerialPortNameResolver.cs（串口名动态识别器），零其它改动；
//   ②通用件（WMI/缓存/候选组装），放库内 Utils 与 SerialPortHelper 同层；
//   ③随 LaserMarkService 连带下沉，主项目经别名壳零改动引用。
//   其余算法/默认值/异常口径/注释全保留，公共 API 签名不变（主项目经
//   文件级 using 别名壳零改动引用，断点可跨工程打进本源码）。
// =====================================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace Kaleidoscope.Utils
{
    // =====================================================================================
    // 串口名动态识别器（老工程 SubClass/SerialPortNameResolver.cs，原样搬运）。
    // -------------------------------------------------------------------------------------
    // 打码下位机串口 COM 号不确定，按"缓存 → 配置 → WMI关键词 → 全口兜底"四层组装候选口，
    // 调用方全部打开监听（打码机问一句答一句，只能守株待兔，不做单口轮询）。
    // FindPortsByKeyword 返回 null = WMI 查询失败（无法判定，不误判），调用方只记日志不断连。
    // =====================================================================================
    public static class SerialPortNameResolver
    {
        /// <summary>
        /// 从设备名里提取 COM 口（"USB-SERIAL CH340 (COM3)"→"COM3"）。
        /// (?!\d) 防 "COM10" 里的 "COM1" 被误匹配。
        /// </summary>
        private static readonly Regex ComPortInNameRegex =
            new Regex(@"(COM\d{1,4})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>安全取系统串口列表（异常回空数组，绝不抛，后台心跳用）。</summary>
        public static string[] SafeGetPortNames()
        {
            try { return SerialPort.GetPortNames(); }
            catch { return new string[0]; }
        }

        /// <summary>
        /// 按设备关键词搜串口（WMI 查"名字同时含 COM 与关键词"的 PnP 设备，再与系统列表交叉确认）。
        /// null=WMI 失败；空=无匹配；非空=找到的串口。
        /// </summary>
        public static List<string> FindPortsByKeyword(string keyword)
        {
            List<string> matches = new List<string>();
            if (string.IsNullOrWhiteSpace(keyword)) return matches;
            string safe = keyword.Trim().Replace("'", "''");

            try
            {
                string[] portNames = SerialPort.GetPortNames();
                if (portNames == null || portNames.Length == 0) return matches;
                HashSet<string> portSet = new HashSet<string>(portNames, StringComparer.OrdinalIgnoreCase);

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%COM%' AND Name LIKE '%" + safe + "%'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"] as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        Match m = ComPortInNameRegex.Match(name);
                        while (m.Success)
                        {
                            string port = m.Value.ToUpperInvariant();
                            if (portSet.Contains(port) && !matches.Contains(port)) matches.Add(port);
                            m = m.NextMatch();
                        }
                    }
                }
                return matches;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 组装候选监听口（去重，越靠前越优先）。三者全空回落系统全口并置 usedFallbackAll。
        /// </summary>
        public static List<string> BuildCandidatePorts(string cachedPort, string configPort,
            string keyword, out bool usedFallbackAll)
        {
            List<string> list = new List<string>();
            usedFallbackAll = false;

            AddDistinct(list, cachedPort);
            AddDistinct(list, configPort);

            List<string> wmi = FindPortsByKeyword(keyword);
            if (wmi != null)
            {
                for (int i = 0; i < wmi.Count; i++) AddDistinct(list, wmi[i]);
            }

            if (list.Count == 0)
            {
                string[] all = SafeGetPortNames();
                for (int i = 0; i < all.Length; i++) AddDistinct(list, all[i]);
                usedFallbackAll = true;
            }
            return list;
        }

        /// <summary>候选口缓存文件名（每台打码机独立，存 exe 目录；存上次实际收到命令的口）。</summary>
        public static string CacheFileName(int deviceId)
        {
            return "LaserMark_ComPort" + deviceId + ".cache";
        }

        /// <summary>读缓存口（文件缺失/非法/已不在系统列表回 null，不阻塞连接）。</summary>
        public static string LoadCachedPort(int deviceId)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CacheFileName(deviceId));
                if (!File.Exists(path)) return null;
                string content = File.ReadAllText(path).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(content)) return null;
                string[] systemPorts = SafeGetPortNames();
                for (int i = 0; i < systemPorts.Length; i++)
                {
                    if (string.Equals(systemPorts[i], content, StringComparison.OrdinalIgnoreCase)) return content;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>保存本次实际收到命令的口（覆盖写，失败静默，缓存只加速不影响正确性）。</summary>
        public static void SaveCachedPort(int deviceId, string portName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(portName)) return;
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CacheFileName(deviceId));
                File.WriteAllText(path, portName.Trim().ToUpperInvariant());
            }
            catch { }
        }

        private static void AddDistinct(List<string> list, string port)
        {
            if (string.IsNullOrWhiteSpace(port)) return;
            string p = port.Trim().ToUpperInvariant();
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], p, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(p);
        }
    }
}
