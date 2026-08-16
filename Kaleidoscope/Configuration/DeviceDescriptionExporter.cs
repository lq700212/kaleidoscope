using System.IO;
using System.Text;

namespace Kaleidoscope.Configuration
{
    /// <summary>
    /// 设备配置说明书导出：把全部配置模型的字段说明（来自 Models 元数据）导出成 Markdown。
    ///
    /// 【干什么】第三步"字段说明=文档"的落地出口：跑一次（或编辑器点一下"导出说明书"），
    /// 就得到一份所有设备段的字段表（中文名/类型/默认值/说明），现场对参数、交接文档直接用。
    /// 库新增配置字段后重新生成即可，文档不会过期。
    ///
    /// 【调用方式】
    ///   string md = DeviceDescriptionExporter.ExportToMarkdown();   // 拿文本自行处理
    ///   DeviceDescriptionExporter.ExportToMarkdownFile(path);       // 直接写 .md（UTF-8 无 BOM）
    ///
    /// 【注意】导出的是"字段语义说明书"（每个字段是什么、默认值多少），不含某台设备当前的
    /// 具体配置值；要"当前值"请用 <see cref="ConfigSerializer.ToJson"/>。
    /// </summary>
    public static class DeviceDescriptionExporter
    {
        /// <summary>
        /// 生成全部设备段的 Markdown 说明书文本。
        /// </summary>
        /// <returns>Markdown 文本（含标题 + 说明 + 每设备一张字段表）</returns>
        public static string ExportToMarkdown()
        {
            var sb = new StringBuilder(8192);
            sb.AppendLine("# Kaleidoscope 设备配置说明书（自动生成）");
            sb.AppendLine();
            sb.AppendLine("> 本文由 `DeviceDescriptionExporter` 基于配置模型（`Models/*.cs`）的");
            sb.AppendLine("> System.ComponentModel 元数据（DisplayName/Description/Category）自动生成。");
            sb.AppendLine("> 库新增/修改配置字段后重新生成即可，**无需手工维护本文**。");
            sb.AppendLine();
            sb.AppendLine("> 默认值 = 代码里的字段初始化器默认配置；集合字段默认显示 `（集合）List<...>`。");
            sb.AppendLine();

            foreach (var d in DeviceDescriptorRegistry.GetAll())
            {
                sb.AppendLine("## " + d.DisplayName + "（" + d.TypeName + "）");
                sb.AppendLine();
                sb.AppendLine("| 字段 | 中文名 | 类型 | 默认值 | 说明 |");
                sb.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var f in d.Fields)
                {
                    sb.Append("| ").Append(EscapeCell(f.Name))
                      .Append(" | ").Append(EscapeCell(f.DisplayName))
                      .Append(" | ").Append(EscapeCell(f.TypeName))
                      .Append(" | ").Append(EscapeCell(f.DefaultValueText))
                      .Append(" | ").Append(EscapeCell(f.Description))
                      .AppendLine(" |");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>
        /// 导出 Markdown 说明书到文件（UTF-8 无 BOM，目录不存在自动创建）。
        /// </summary>
        /// <param name="filePath">目标 .md 文件完整路径</param>
        public static void ExportToMarkdownFile(string filePath)
        {
            string md = ExportToMarkdown();
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, md, new UTF8Encoding(false));
        }

        /// <summary>Markdown 表格单元格转义：| 与换行会被表格破坏，统一替换</summary>
        private static string EscapeCell(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}