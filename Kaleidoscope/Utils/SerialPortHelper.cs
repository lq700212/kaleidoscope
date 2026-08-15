using System.Collections.Generic;
using System.IO.Ports;
using System.Management;

namespace Kaleidoscope.Utils
{
    /// <summary>
    /// 串口识别辅助类（CH340 自动识别）。
    /// 【来源】AgingTestSystem.Services.SerialPortHelper 原样移植（命名空间改为 Kaleidoscope.Utils）。
    ///
    /// 【为什么需要它】气压表通过 RS485 转 USB（CH340 芯片）接入工控机，Windows 会把它识别成
    /// 一个"COM 口"，但具体是 COM 几不确定（取决于 USB 插口和历史驱动分配）。如果程序里写死 COM1，
    /// 现场换一台电脑/换一个 USB 口就连不上。用系统 WMI 查询出"名字里带 CH340"的串口，
    /// 从而自动找到气压表实际插在哪个 COM 口，现场不用改配置。
    ///
    /// 【调用方依赖 System.Management】：本类用 WMI 查询，Kaleidoscope.csproj 必须引用
    /// System.Management（见 csproj Reference）。
    /// </summary>
    public static class SerialPortHelper
    {
        /// <summary>获取第一个匹配的 CH340 串口名称（例如 "COM3"）；找不到返回 null。</summary>
        /// <returns>端口名称，未找到返回 null</returns>
        public static string GetCh340PortName()
        {
            List<string> ports = GetCh340Ports();
            return ports.Count > 0 ? ports[0] : null;
        }

        /// <summary>获取所有匹配的 CH340 串口名称列表（按系统枚举顺序）。</summary>
        /// <returns>CH340 端口列表（可能为空）</returns>
        public static List<string> GetCh340Ports()
        {
            var ch340Ports = new List<string>();

            try
            {
                // 用 WMI 查询系统 PnP 设备：名字里包含 "COM" 的都是串口类设备
                //（如 "USB-SERIAL CH340 (COM3)"）
                string query = "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'";

                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        string caption = device["Caption"]?.ToString();
                        string pnpId = device["PNPDeviceID"]?.ToString();

                        // 双重校验：设备描述含 CH340 + 硬件 ID 是 CH340 的 VID/PID
                        //（防止其它 USB 转串口芯片如 FTDI/CP2102 被误认）
                        if (string.IsNullOrEmpty(caption) || !caption.Contains("CH340")) continue;
                        if (string.IsNullOrEmpty(pnpId) ||
                            !pnpId.Contains("VID_1A86") ||  // CH340 厂商ID（WCH/沁恒）
                            !pnpId.Contains("PID_7523"))    // CH340 产品ID
                        {
                            continue;
                        }

                        // 从 Caption 中提取出 "COMx" 端口号：
                        // 格式一般是 "USB-SERIAL CH340 (COM3)"，取最后一对括号里的内容
                        int startIndex = caption.LastIndexOf('(') + 1;
                        int endIndex = caption.LastIndexOf(')');
                        if (startIndex > 0 && endIndex > startIndex)
                        {
                            string portName = caption.Substring(startIndex, endIndex - startIndex);
                            ch340Ports.Add(portName);
                        }
                    }
                }
            }
            catch
            {
                // WMI 查询失败（权限不足等）：返回空列表，上层回落配置的固定端口
            }

            return ch340Ports;
        }

        /// <summary>获取当前系统所有已存在的串口名称（如 COM1、COM3 ...），用于"判断配置里的固定端口是否存在"。</summary>
        /// <returns>系统串口名称数组</returns>
        public static string[] GetAllPortNames()
        {
            try
            {
                return SerialPort.GetPortNames();
            }
            catch
            {
                return new string[0];
            }
        }
    }
}