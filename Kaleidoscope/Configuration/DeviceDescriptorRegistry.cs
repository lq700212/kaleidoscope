using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Kaleidoscope.Models;

namespace Kaleidoscope.Configuration
{
    /// <summary>
    /// 设备描述符注册表：登记"哪些配置模型 + 设备中文名"，并按需【反射自动构建】描述符。
    ///
    /// 【干什么】第三步（配置自文档化）的入口：调用方
    ///   - DeviceDescriptorRegistry.GetAll()            → 全部设备段的字段说明（导出说明书用）；
    ///   - DeviceDescriptorRegistry.Get(typeof(CameraConfig)) → 单段描述符（智能渲染/日志自描述用）。
    ///
    /// 【为什么这样设计】
    /// - 字段清单不手工写：基于 Models 的 DisplayName/Description/Category 特性反射构建，
    ///   新增字段自动出现、与库同步演进，不重复劳动；
    /// - 只手工登记"设备中文名 + 类型"（一行一个），避免类型级特性改动波及序列化；
    /// - 构建结果缓存（反射有成本），带锁保证多线程安全。
    ///
    /// 【边界】描述符是"字段语义"元数据，不含校验规则——寄存器重叠/地址越界等跨字段逻辑
    /// 校验仍在 <see cref="DeviceHubConfigValidator"/> 手写（自动推导不了），两者职责互补。
    /// </summary>
    public static class DeviceDescriptorRegistry
    {
        private static readonly object _sync = new object();
        private static readonly Dictionary<Type, string> _displayNames = new Dictionary<Type, string>();
        private static readonly List<Type> _order = new List<Type>();
        private static readonly Dictionary<Type, DeviceDescriptor> _cache = new Dictionary<Type, DeviceDescriptor>();

        static DeviceDescriptorRegistry()
        {
            // 设备中文名登记：键=配置模型类型，值=设备中文名（字段自动反射，不用手工列）。
            // 新增配置模型时在这里加一行即可；集合元素类（子表）也登记，说明书会包含它们。
            Register(typeof(DeviceHubConfig), "全局设备配置（DeviceHub 入参）");
            Register(typeof(PlcConfig), "PLC 从站");
            Register(typeof(PlcMasterConfig), "PLC 主站");
            Register(typeof(CameraConfig), "相机");
            Register(typeof(ScanConfig), "扫码枪");
            Register(typeof(ImageConfig), "图像存储");
            Register(typeof(BarometerConfig), "气压表");
            Register(typeof(IoConfig), "IO 耦合器");
            Register(typeof(FanConfig), "送风机");
            Register(typeof(IoOutputChannelRemap), "IO 备用通道映射（子表）");
            Register(typeof(PlcPollItem), "PLC 轮询项（子表）");
            Register(typeof(StationProgramItem), "相机点位→程序号（子表）");
            Register(typeof(ModelStationPrograms), "相机型号程序分表（子表）");
        }

        /// <summary>登记一个配置模型的设备中文名（静态构造里调用）</summary>
        private static void Register(Type modelType, string displayName)
        {
            _displayNames[modelType] = displayName;
            _order.Add(modelType);
        }

        /// <summary>
        /// 取指定配置模型的描述符（首次构建后缓存）。
        /// </summary>
        /// <param name="modelType">配置模型类型（Models 命名空间下的类）</param>
        /// <returns>该模型的设备描述符（未登记的设备中文名回退为类型名）</returns>
        public static DeviceDescriptor Get(Type modelType)
        {
            if (modelType == null) throw new ArgumentNullException(nameof(modelType));
            lock (_sync)
            {
                DeviceDescriptor cached;
                if (_cache.TryGetValue(modelType, out cached)) return cached;
                DeviceDescriptor built = Build(modelType);
                _cache[modelType] = built;
                return built;
            }
        }

        /// <summary>
        /// 取全部已登记设备段的描述符（按登记顺序，稳定）。
        /// </summary>
        /// <returns>描述符列表（不含 null）</returns>
        public static List<DeviceDescriptor> GetAll()
        {
            lock (_sync)
            {
                var result = new List<DeviceDescriptor>(_order.Count);
                foreach (Type t in _order) result.Add(Get(t));
                return result;
            }
        }

        /// <summary>
        /// 反射构建单个配置模型的描述符（不缓存，调用方一般用 <see cref="Get"/>）。
        /// 字段按声明顺序（MetadataToken 排序）输出，默认值取"new 模型后该属性的值"。
        /// </summary>
        /// <param name="modelType">配置模型类型</param>
        /// <returns>描述符</returns>
        public static DeviceDescriptor Build(Type modelType)
        {
            if (modelType == null) throw new ArgumentNullException(nameof(modelType));

            string disp;
            var d = new DeviceDescriptor
            {
                TypeName = modelType.Name,
                DisplayName = _displayNames.TryGetValue(modelType, out disp) ? disp : modelType.Name,
            };

            // 无参数构造器存在才取默认值样本（Models 都是字段初始化器，new 即得默认配置）
            object sample = null;
            if (modelType.GetConstructor(Type.EmptyTypes) != null)
            {
                try { sample = Activator.CreateInstance(modelType); }
                catch { sample = null; } // 构造抛异常时默认值列显示 "—"，不阻断构建
            }

            var props = modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.GetGetMethod() != null)
                .OrderBy(p => p.MetadataToken); // MetadataToken 按元数据顺序 → 声明顺序，稳定可复现

            foreach (var p in props)
            {
                var dispName = p.GetCustomAttribute<DisplayNameAttribute>(true);
                var desc = p.GetCustomAttribute<DescriptionAttribute>(true);
                var cat = p.GetCustomAttribute<CategoryAttribute>(true);

                var f = new DeviceFieldDescriptor
                {
                    Name = p.Name,
                    DisplayName = (dispName != null && !string.IsNullOrEmpty(dispName.DisplayName))
                        ? dispName.DisplayName
                        : p.Name,
                    Description = (desc != null) ? desc.Description : "",
                    Category = (cat != null) ? cat.Category : "",
                    TypeName = GetFriendlyTypeName(p.PropertyType),
                    IsCollection = p.PropertyType != typeof(string)
                        && typeof(IEnumerable).IsAssignableFrom(p.PropertyType),
                    DefaultValueText = ReadDefaultValue(sample, p),
                };
                d.Fields.Add(f);
            }
            return d;
        }

        /// <summary>
        /// 读默认值样本的属性值；getter 异常（如某个属性内部抛错）返回 "—"，
        /// 不让单个字段拖垮整段描述符构建。
        /// </summary>
        /// <param name="sample">默认值样本实例（可能为 null）</param>
        /// <param name="p">属性</param>
        /// <returns>默认值文本</returns>
        private static string ReadDefaultValue(object sample, PropertyInfo p)
        {
            if (sample == null) return "—";
            try { return FormatValue(p.GetValue(sample)); }
            catch { return "—"; }
        }

        /// <summary>
        /// 把默认值格式化为可读文本：null/空串/集合/枚举/布尔各自有明确表示。
        /// </summary>
        /// <param name="value">属性值</param>
        /// <returns>文本</returns>
        private static string FormatValue(object value)
        {
            if (value == null) return "null";
            var s = value as string;
            if (s != null) return s.Length == 0 ? "（空字符串）" : s;
            if (value is IEnumerable) return "（集合）" + GetFriendlyTypeName(value.GetType());
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is Enum) return value.ToString();
            // 其它引用类型（如嵌套对象 PlcConfig）显示友好类名，避免全限定名刷屏
            if (value.GetType().IsClass) return "（对象）" + GetFriendlyTypeName(value.GetType());
            if (value is IFormattable) return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            return value.ToString();
        }

        /// <summary>
        /// 友好类型名：泛型 List 显示 "List&lt;T&gt;"，其它显示 Type.Name。
        /// </summary>
        /// <param name="t">类型</param>
        /// <returns>友好名</returns>
        private static string GetFriendlyTypeName(Type t)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                return "List<" + t.GetGenericArguments()[0].Name + ">";
            return t.Name;
        }
    }
}