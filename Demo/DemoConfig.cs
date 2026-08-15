using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using CommonLib.Models;

namespace CommonLibDemo
{
    /// <summary>
    /// Demo 程序的持久化配置（存 Config/demo.json，Newtonsoft.Json 序列化）。
    ///
    /// 【为什么 Demo 要自己的配置类，而不是直接用 DeviceHubConfig】
    /// DeviceHubConfig 是 CommonLib 强类型入参（本类内部持有它），但 Demo 还要存几项
    /// "仅 Demo 自己用"的界面记忆（如窗口坐标、上次选的相机/扫码枪下标），
    /// 混进 DeviceHubConfig 会污染库的配置语义。所以外层套一层 DemoConfig，
    /// 序列化时只关心自己这层 + 内嵌的 DeviceHubConfig（两层都 Newtonsoft 可序列化）。
    ///
    /// 【配置文件位置】程序目录下 Config/demo.json。首次运行不存在 → 用默认配置
    /// （默认 PLC/相机/扫码枪参数来自各 Config 类的字段默认值 + CommonLib 默认相机），
    /// 现场改 IP 后点"保存配置"落盘，下次启动自动加载。
    /// </summary>
    public class DemoConfig
    {
        /// <summary>当前产品型号（默认 U171，PLC 建站成功会写进型号区）</summary>
        public string ProductModel { get; set; } = "U171";

        /// <summary>设备层总配置（CommonLib 唯一入参，Demo 直接交给 DeviceHub）</summary>
        public DeviceHubConfig Devices { get; set; } = new DeviceHubConfig();

        /// <summary>上次保存时主窗体位置（可选，简单记忆用）</summary>
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;

        // ────── 序列化存取 ──────

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>配置文件完整路径：程序目录下 Config/demo.json</summary>
        public static string ConfigFilePath
        {
            get
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
                return Path.Combine(dir, "demo.json");
            }
        }

        /// <summary>从磁盘加载配置；文件不存在或解析失败返回默认配置（不抛异常，保证程序能起）。</summary>
        public static DemoConfig Load()
        {
            try
            {
                string path = ConfigFilePath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                    var cfg = JsonConvert.DeserializeObject<DemoConfig>(json, JsonSettings);
                    if (cfg != null && cfg.Devices != null)
                        return cfg;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("DemoConfig.Load 失败，用默认配置：" + ex.Message);
            }
            return new DemoConfig();
        }

        /// <summary>保存到磁盘（自动建目录）。失败仅写调试输出，不影响主流程。</summary>
        public void Save()
        {
            try
            {
                string path = ConfigFilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = JsonConvert.SerializeObject(this, JsonSettings);
                File.WriteAllText(path, json, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("DemoConfig.Save 失败：" + ex.Message);
            }
        }
    }
}
