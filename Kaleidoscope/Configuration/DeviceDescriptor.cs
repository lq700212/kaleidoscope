using System.Collections.Generic;

namespace Kaleidoscope.Configuration
{
    /// <summary>
    /// 一个配置模型（设备段）的结构化描述：设备中文名 + 字段清单。
    ///
    /// 【干什么】把 `Models/*.cs` 里的"字段即文档"元数据（DisplayName/Description/Category）
    /// 收敛成可编程访问的强类型描述符，供：配置说明书导出（Markdown/将来 Excel）、智能渲染、
    /// 业务层自文档化（日志/帮助里打印"这台设备的配置含义"）、以及后续"描述符驱动的校验器"。
    ///
    /// 【为什么这么设计】不手工为每个模型写一份字段清单（重复劳动且随字段增删漂移），
    /// 而是由 <see cref="DeviceDescriptorRegistry"/> 用反射 + 现有特性【自动构建】——
    /// 库新增配置字段时描述符自动跟上，维持"加字段不用改编辑器/文档生成器"的既有承诺。
    ///
    /// 【调用方注意】本类只描述"字段语义"，不含"当前值"；字段默认值来自模型构造后的属性值
    /// （即字段初始化器 `= xxx` 的默认配置）。线程安全（Registry 缓存 + 锁）。
    /// </summary>
    public class DeviceDescriptor
    {
        /// <summary>配置模型的 .NET 类型名（如 CameraConfig）</summary>
        public string TypeName { get; set; }

        /// <summary>设备中文名（如 "相机"；由注册表登记，缺省回退为类型名）</summary>
        public string DisplayName { get; set; }

        /// <summary>字段清单（按模型声明顺序，稳定）</summary>
        public List<DeviceFieldDescriptor> Fields { get; set; }

        /// <summary>构造：初始化空字段清单</summary>
        public DeviceDescriptor()
        {
            Fields = new List<DeviceFieldDescriptor>();
        }
    }

    /// <summary>
    /// 一个配置字段的结构化描述：属性名 / 中文名 / 说明 / 分组 / 类型 / 默认值 / 是否集合。
    ///
    /// - 中文名、说明、分组来自字段上的 [DisplayName]/[Description]/[Category] 特性；
    /// - 类型、默认值来自反射（默认值 = new 模型后该字段的值，即字段初始化器默认配置）；
    /// - 是否集合用于界面区分"基础字段"和"可展开的子表"（List 类字段）。
    /// </summary>
    public class DeviceFieldDescriptor
    {
        /// <summary>属性名（模型里的字段名，如 IpAddress）</summary>
        public string Name { get; set; }

        /// <summary>中文名（[DisplayName]，缺省回退为属性名）</summary>
        public string DisplayName { get; set; }

        /// <summary>字段说明（[Description]，可为空字符串）</summary>
        public string Description { get; set; }

        /// <summary>分组（[Category]，可为空字符串）</summary>
        public string Category { get; set; }

        /// <summary>友好类型名（如 Int32 / String / List&lt;CameraConfig&gt;）</summary>
        public string TypeName { get; set; }

        /// <summary>默认值文本（集合字段显示 "（集合）List&lt;...&gt;"）</summary>
        public string DefaultValueText { get; set; }

        /// <summary>是否集合字段（List/数组等，用于界面区分基础字段与子表）</summary>
        public bool IsCollection { get; set; }
    }
}