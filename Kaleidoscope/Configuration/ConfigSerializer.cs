using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Kaleidoscope.Models;

namespace Kaleidoscope.Configuration
{
    /// <summary>
    /// 设备配置（<see cref="DeviceHubConfig"/>）的 JSON 持久化工具：
    /// 保存到本地 .kcfg 文件 / 从文件读回，同时提供对象与字符串互转。
    ///
    /// 【为什么要有它】
    /// 旧范式下"读/写配置文件"是业务项目自己写（Demo 里就是用 Newtonsoft.Json 手写一套
    /// Load/Save）。设备一多、每个新界面都重抄一遍容易漏、还被迫引 Newtonsoft。本类把
    /// 序列化收进库内，业务侧从此一行搞定：
    ///   var cfg = ConfigSerializer.Load(path);   // 读配置（文件不存在 → 默认配置）
    ///   ConfigSerializer.Save(cfg, path);        // 存配置（自动建目录）
    ///   hub.ApplyConfig(cfg);                    // 转手给 DeviceHub 即可
    /// 配合 <see cref="DeviceHubConfigValidator"/>，可在保存前先校验、把坏配置挡在门外。
    ///
    /// 【为什么用 DataContractJsonSerializer（而不是引 Newtonsoft.Json）】
    /// 依赖红线：库不引第三方序列化库（libs 只放通讯依赖，离线可编译）。本类用 .NET
    /// Framework 内置的 System.Runtime.Serialization.Json（net472 自带；.NET Core 3.0+ 有同名
    /// 类型，迁库零改动），保持零第三方依赖。它的两个代价由本类补齐：
    ///   - 字段按字母序输出 + 非 ASCII 字符被转成 \uXXXX → 写盘后已把 \uXXXX 解码回真实字符，
    ///     中文目录/相机名在配置文件里直接可读；
    ///   - 输出紧凑 → 写盘前已美化缩进，配置文件可手工编辑。
    ///
    /// 【版本兼容（关键设计，勿破坏）】
    /// DataContractJsonSerializer 的行为天然保证向前兼容：
    ///   - 反序列化先走构造函数（字段初始化器的默认值全部生效），再只覆盖 JSON 里【出现】的
    ///     成员 → 旧版配置文件缺新版新增字段时，自动用新版默认值补齐，不抛异常；
    ///   - JSON 里出现类里【没有】的未知成员 → 直接忽略 → 新配置文件被旧版库读也不会崩。
    /// 唯一坑：JSON 里显式写 null 会【覆盖】默认值（把嵌套对象/列表置空），所以每次读回后
    /// 都经 <see cref="EnsureSafe"/> 兜底，把 null 的嵌套对象替换成默认实例，避免运行时
    /// Config.Plc.Port 这类访问抛 NullReferenceException。
    ///
    /// 【调用方注意】
    ///   - Save：目录不可写/磁盘满会抛 IOException 等，由调用方决定怎么处理（写坏配置比不写
    ///     更危险，库不静默吞掉）；
    ///   - Load：文件【不存在】返回全新默认配置（new DeviceHubConfig()，与 Demo 旧行为一致，
    ///     保证程序能起）；文件【存在但损坏】抛 InvalidDataException；
    ///   - 文件编码 UTF-8 无 BOM。
    /// </summary>
    public static class ConfigSerializer
    {
        /// <summary>配置文件默认扩展名（.kcfg = Kaleidoscope ConFiG）</summary>
        public const string DefaultExtension = ".kcfg";

        // 静态实例线程安全（MSDN：DataContractSerializer 实例可安全多线程使用），
        // 反复 new 会有反射开销，全库共享一个即可。
        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(DeviceHubConfig));

        /// <summary>
        /// 把配置保存到本地文件（自动建目录；UTF-8 无 BOM；输出已美化缩进 + 中文已解码）。
        /// </summary>
        /// <param name="config">要保存的设备配置（不允许 null，null 抛 ArgumentNullException）</param>
        /// <param name="filePath">目标文件完整路径，目录不存在会自动创建</param>
        /// <exception cref="ArgumentNullException">config 为 null</exception>
        /// <exception cref="IOException">目录不可写 / 磁盘满等 IO 错误（由调用方决定处理）</exception>
        public static void Save(DeviceHubConfig config, string filePath)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            // 先生成字符串（此时序列化错误就暴露，避免建了空目录）
            string json = ToJson(config);

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, json, new UTF8Encoding(false)); // false=不带 BOM
        }

        /// <summary>
        /// 从本地文件读配置。
        /// </summary>
        /// <param name="filePath">配置文件完整路径</param>
        /// <returns>
        /// 读出的配置（缺失字段已用默认值补齐、null 嵌套已兜底）；
        /// 文件不存在时返回全新默认配置（new DeviceHubConfig()，不抛异常）。
        /// </returns>
        /// <exception cref="InvalidDataException">文件存在但内容损坏/不是合法配置 JSON 时抛出</exception>
        public static DeviceHubConfig Load(string filePath)
        {
            if (!File.Exists(filePath)) return new DeviceHubConfig();

            string json = File.ReadAllText(filePath, Encoding.UTF8);
            return FromJson(json);
        }

        /// <summary>
        /// 把配置序列化成 JSON 字符串（已美化缩进、\uXXXX 已解码回真实字符，可直接查看/手工编辑）。
        /// </summary>
        /// <param name="config">要序列化的配置（不允许 null）</param>
        /// <returns>JSON 文本</returns>
        public static string ToJson(DeviceHubConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            using (var ms = new MemoryStream())
            {
                // CreateJsonWriter 第 4 参 indent=true 输出带缩进的 JSON；
                // 第 3 参 ownsStream=false（由外层 MemoryStream 统一释放）。
                using (XmlDictionaryWriter writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true))
                {
                    Serializer.WriteObject(writer, config);
                    writer.Flush();
                }
                string raw = Encoding.UTF8.GetString(ms.ToArray());
                // DataContractJsonSerializer 会把非 ASCII 字符转成 \uXXXX（如 "D:\IV存图" →
                // "D:\IV\u5B58\u56FE"），人工没法读，这里解码回真实字符。
                return DecodeUnicodeEscapes(raw);
            }
        }

        /// <summary>
        /// 把 JSON 字符串反序列化成配置。
        /// </summary>
        /// <param name="json">配置 JSON 文本（null/空白 → 返回全新默认配置）</param>
        /// <returns>配置对象（缺失字段用默认值补齐、null 嵌套已兜底）</returns>
        /// <exception cref="InvalidDataException">JSON 非法/类型不匹配时抛出</exception>
        public static DeviceHubConfig FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new DeviceHubConfig();

            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    // ReadObject 失败说明文件内容不是本库配置结构，向上抛让调用方/使用者知道
                    // （宁可明示"配置文件坏了"，也不要静默用默认值掩盖问题）。
                    var obj = Serializer.ReadObject(ms) as DeviceHubConfig;
                    return EnsureSafe(obj ?? new DeviceHubConfig());
                }
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Kaleidoscope 设备配置文件解析失败：" + ex.Message, ex);
            }
        }

        /// <summary>
        /// 把配置里为 null 的嵌套对象/列表兜底成默认实例，防止运行时访问空引用。
        /// 显式暴露给调用方：业务侧从别的来源（数据库/网络/手拼）拿到配置后，
        /// 只要传给本方法一次，后续访问 Config.Plc.Port 等就不会 NRE。
        /// </summary>
        /// <param name="config">原始配置（允许 null，null 也返回全新默认配置）</param>
        /// <returns>兜底后的同一实例（不新建，只是把 null 成员填默认）</returns>
        public static DeviceHubConfig EnsureSafe(DeviceHubConfig config)
        {
            if (config == null) return new DeviceHubConfig();

            config.Plc = config.Plc ?? new PlcConfig();
            config.PlcMaster = config.PlcMaster ?? new PlcMasterConfig();
            config.Image = config.Image ?? new ImageConfig();
            config.Barometer = config.Barometer ?? new BarometerConfig();
            config.Io = config.Io ?? new IoConfig();
            config.Fan = config.Fan ?? new FanConfig();

            config.Cameras = config.Cameras ?? new System.Collections.Generic.List<CameraConfig>();
            config.Scanners = config.Scanners ?? new System.Collections.Generic.List<ScanConfig>();
            config.ProductModels = config.ProductModels ?? new System.Collections.Generic.List<string>();

            config.Plc.ModelIndexes = config.Plc.ModelIndexes ?? new System.Collections.Generic.List<ModelIndexItem>();
            config.PlcMaster.PollItems = config.PlcMaster.PollItems ?? new System.Collections.Generic.List<PlcPollItem>();
            config.Fan.FanIpCandidates = config.Fan.FanIpCandidates ?? new System.Collections.Generic.List<string>();
            config.Io.IoBackupChannelMappings = config.Io.IoBackupChannelMappings ?? new System.Collections.Generic.List<IoOutputChannelRemap>();

            // 相机内部的点位映射表也要兜底（ProgramsFor 等访问会直接遍历它们）
            foreach (var cam in config.Cameras)
            {
                if (cam == null) continue;
                cam.StationPrograms = cam.StationPrograms ?? new System.Collections.Generic.List<StationProgramItem>();
                cam.ModelStationPrograms = cam.ModelStationPrograms ?? new System.Collections.Generic.List<ModelStationPrograms>();
                foreach (var m in cam.ModelStationPrograms)
                    if (m != null) m.Programs = m.Programs ?? new System.Collections.Generic.List<StationProgramItem>();
            }

            return config;
        }

        /// <summary>
        /// 把 DataContractJsonSerializer 输出的 \uXXXX 转义序列解码回真实 Unicode 字符。
        /// 只处理"单个反斜杠 + uXXXX"形式（那才是真正的非 ASCII 字符转义）；
        /// 字符串内容里本来就有反斜杠时（转义成 \\ 或 \\uXXXX）原样保留，不做误替换。
        /// </summary>
        /// <param name="json">含 \uXXXX 的 JSON 文本</param>
        /// <returns>解码后的文本</returns>
        private static string DecodeUnicodeEscapes(string json)
        {
            var sb = new StringBuilder(json.Length);
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                // 命中的形态：\u 且前面一个字符不是反斜杠（\uXXXX 是字符转义；\\u 是字面反斜杠+u）
                if (c == '\\' && i + 5 < json.Length && json[i + 1] == 'u'
                    && (i == 0 || json[i - 1] != '\\'))
                {
                    int code;
                    if (TryParseHex4(json, i + 2, out code))
                    {
                        sb.Append((char)code);
                        i += 5; // 跳过 "uXXXX" 共 5 个字符
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 解析 JSON 里 \uXXXX 的 4 位十六进制码点。
        /// </summary>
        /// <param name="s">源文本</param>
        /// <param name="start">码点起始下标（指向第 1 个十六进制字符）</param>
        /// <param name="code">解析出的码点值</param>
        /// <returns>4 个字符全部是十六进制数字才返回 true</returns>
        private static bool TryParseHex4(string s, int start, out int code)
        {
            code = 0;
            for (int k = 0; k < 4; k++)
            {
                char h = s[start + k];
                int v;
                if (h >= '0' && h <= '9') v = h - '0';
                else if (h >= 'a' && h <= 'f') v = h - 'a' + 10;
                else if (h >= 'A' && h <= 'F') v = h - 'A' + 10;
                else return false;
                code = code * 16 + v;
            }
            return true;
        }
    }
}
