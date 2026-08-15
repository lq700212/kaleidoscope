using System;
using System.Collections.Generic;

namespace Kaleidoscope.Models
{
    /// <summary>
    /// 图像保存配置（从 CommandCenter/Models/AppConfig.cs 抽取，原 ImageConfig 类）。
    ///
    /// 【目录结构】存图目录按【逐级目录列表】归档，文件名按【模板】生成。
    /// 默认结构（V2.12.1 起按相机分目录，上下相机同号点位互不干扰）：
    ///   根目录 / 年月日(2026年08月11日) / SN号 / OK|NG / 相机 / 点位号文件
    /// 注：年月日是【一个】目录名，不是年/月/日三级目录。
    ///
    /// 【定期清理】ImageStore.StartPeriodicCleanup 按 KeepDays 自动删除过期存图目录，
    /// 0 = 不自动清理（删除逻辑完整在 ImageStore，不在此配置类）。
    /// </summary>
    public class ImageConfig
    {
        /// <summary>图像保存根目录（不在则自动创建）</summary>
        public string SaveRootDir { get; set; } = @"E:\Images";

        /// <summary>
        /// 目录层级列表（可视化配置的主数据）：每个元素是一级目录名或生成规则，
        /// 按顺序逐级建目录。支持占位符（见下方占位符说明），固定文字原样保留。
        /// 默认：["{年月日}","{SN}","{相机}","{OKNG}"] → 根/2026年08月11日/SN-0001/上相机/OK/1.jpeg
        /// 说明：
        ///   {年月日} 是一个整体目录名，展开成"2026年08月11日"（不是年/月/日三级）；
        ///   {相机}   展开成相机名（如"上相机"/"下相机"）。**存图点位用的是相机点位号，
        ///     上下相机同号会重复，必须靠 {相机} 这一层目录把两家隔开**，否则文件互相覆盖；
        ///   {OKNG}   按本次判定展开成 OK 或 NG 两个并列目录之一，满足现场分开放习惯；
        ///   点位号进文件名（见 FileNameTemplate），不作为目录层级。
        /// </summary>
        public List<string> SubDirs { get; set; } = new List<string> { "{年月日}", "{SN}", "{相机}", "{OKNG}" };

        /// <summary>
        /// 文件名模板（不含扩展名）。支持的占位符（其余文字原样保留）：
        ///   {点位}   相机点位号；{相机}   相机名；{时间}   时间戳 yyyyMMdd_HHmmss_fff；{SN} 序列号
        /// 例：默认 "{点位}" → 1.png
        /// ⚠️ 双格式归档（SaveImageFilePair）已不使用本模板——现场定稿归档文件名 =
        ///   相机源文件名 + "_" + 时间戳（如 0084_20260814_102030_123.jpeg），
        ///   本字段仅旧版 TCP/BR 取图（SaveImage/SaveImageBytes）仍按模板命名。
        /// </summary>
        public string FileNameTemplate { get; set; } = "{点位}";

        /// <summary>
        /// 存图文件名是否追加时间戳后缀。
        /// true(默认)：双格式归档（SaveImageFilePair）最终文件名 =
        ///   相机源文件名 + "_" + 时间戳(yyyyMMdd_HHmmss_fff)；旧版 SaveImage 则是
        ///   模板渲染结果 + "_" + 时间戳。防止"同点位重复拍照/重复触发"时覆盖旧图。
        /// false：保持原名（模板带 {时间} 时基本不重名，此开关仅作保险）。
        /// </summary>
        public bool FileTimestampSuffix { get; set; } = true;

        /// <summary>保留天数，0 表示不自动清理（ImageStore.StartPeriodicCleanup 用）</summary>
        public int KeepDays { get; set; } = 30;

        /// <summary>相机 FTP 上传目录兜底（各相机未单独配 FtpUploadDir 时用它；多台务必分开配）</summary>
        public string FtpRootDir { get; set; } = @"D:\Kaleidoscope\Images\ftp";
    }
}
