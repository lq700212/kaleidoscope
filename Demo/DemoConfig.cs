using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Kaleidoscope.Configuration;
using Kaleidoscope.Models;

namespace KaleidoscopeDemo
{
    /// <summary>
    /// Demo 程序的持久化配置（存 Config/demo.json，Newtonsoft.Json 序列化）。
    ///
    /// 【分层（V1.2.4 起设备配置改用库内置 ConfigSerializer）】
    /// Demo 的配置拆成两个文件：
    ///   - Config/devices.kcfg：设备层总配置（DeviceHubConfig，Kaleidoscope 唯一入参），
    ///     由库的 <see cref="ConfigSerializer"/> 读写（零第三方依赖、缺字段自动补齐、中文直读）；
    ///   - Config/demo.json：仅 Demo 自己用的界面记忆（窗口坐标/上次选择），Newtonsoft 序列化。
    /// 这样示范了业务项目的标准接法：设备配置不用自己写 JSON 逻辑，一行 Load 就拿到
    /// 强类型 DeviceHubConfig，直接交给 DeviceHub.ApplyConfig。
    ///
    /// 【旧配置自动迁移】旧版 demo.json 里内嵌了 Devices 字段；首次运行新版本时，
    /// 若发现 devices.kcfg 不存在而旧 demo.json 里带设备配置，会自动落成 devices.kcfg，
    /// 旧现场参数不丢。
    /// </summary>
    public class DemoConfig
    {
        /// <summary>当前产品型号（默认 U171，PLC 建站成功会写进型号区）</summary>
        public string ProductModel { get; set; } = "U171";

        /// <summary>
        /// 设备层总配置（Kaleidoscope 唯一入参，Demo 直接交给 DeviceHub）。
        /// [JsonIgnore]：不再序列化进 demo.json（已独立成 devices.kcfg），
        /// 但反序列化旧 demo.json 时仍会被填上（用于一次性迁移）。
        /// </summary>
        [JsonIgnore]
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

        /// <summary>界面记忆配置文件完整路径：程序目录下 Config/demo.json</summary>
        public static string ConfigFilePath
        {
            get
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
                return Path.Combine(dir, "demo.json");
            }
        }

        /// <summary>设备配置（DeviceHubConfig）文件完整路径：程序目录下 Config/devices.kcfg，由 ConfigSerializer 管理。</summary>
        public static string DevicesFilePath
        {
            get
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
                return Path.Combine(dir, "devices.kcfg");
            }
        }

        /// <summary>从磁盘加载配置；文件不存在或解析失败返回默认配置（不抛异常，保证程序能起）。</summary>
        public static DemoConfig Load()
        {
            try
            {
                string path = ConfigFilePath;
                DemoConfig cfg = null;
                bool embeddedDevices = false;

                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                    // 旧版 demo.json 会内嵌 "Devices" 字段，新版不会——据此判断是否需要迁移
                    embeddedDevices = json.IndexOf("\"Devices\"", StringComparison.OrdinalIgnoreCase) >= 0;
                    var old = JsonConvert.DeserializeObject<DemoConfig>(json, JsonSettings);
                    if (old != null) cfg = old;
                }

                if (cfg == null) cfg = new DemoConfig();

                // 设备配置：优先读 devices.kcfg（新版标准位置）
                if (File.Exists(DevicesFilePath))
                {
                    cfg.Devices = ConfigSerializer.Load(DevicesFilePath);
                }
                // 旧版迁移：kcfg 不存在但旧 demo.json 里带设备配置 → 落成 kcfg，一次完成
                else if (embeddedDevices && cfg.Devices != null)
                {
                    ConfigSerializer.Save(cfg.Devices, DevicesFilePath);
                }

                if (cfg.Devices == null) cfg.Devices = new DeviceHubConfig();
                return cfg;
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
                // 设备配置交给库的 ConfigSerializer（零第三方依赖 + 缺字段兼容）
                ConfigSerializer.Save(Devices ?? new DeviceHubConfig(), DevicesFilePath);

                // 界面记忆仍用 Newtonsoft 写 demo.json（仅本 Demo 自己用）
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
