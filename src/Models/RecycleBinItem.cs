using System;
using System.Collections.Generic;
using System.IO;

namespace SightoHear.Models
{
    /// <summary>
    /// 回收站项数据模型，对应 Windows $Recycle.Bin 中的 $I 元数据文件解析结果。
    /// </summary>
    public class RecycleBinItem
    {
        /// <summary>
        /// 常见文件扩展名 → 类型描述映射表（用于属性弹窗展示"文件类型"）。
        /// </summary>
        private static readonly Dictionary<string, string> FileTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // 文本与文档
            [".txt"] = "文本文档",
            [".log"] = "日志文件",
            [".md"] = "Markdown 文档",
            [".rtf"] = "RTF 文档",
            [".json"] = "JSON 文件",
            [".xml"] = "XML 文件",
            [".html"] = "HTML 文档",
            [".htm"] = "HTML 文档",
            [".css"] = "CSS 样式表",
            [".js"] = "JavaScript 文件",
            [".ts"] = "TypeScript 文件",
            // 图像
            [".jpg"] = "JPEG 图像",
            [".jpeg"] = "JPEG 图像",
            [".png"] = "PNG 图像",
            [".gif"] = "GIF 图像",
            [".bmp"] = "位图图像",
            [".webp"] = "WebP 图像",
            [".tif"] = "TIFF 图像",
            [".tiff"] = "TIFF 图像",
            [".ico"] = "图标文件",
            [".svg"] = "SVG 图像",
            // 音频
            [".mp3"] = "MP3 音频",
            [".wav"] = "WAV 音频",
            [".flac"] = "FLAC 音频",
            [".ogg"] = "OGG 音频",
            [".aac"] = "AAC 音频",
            [".m4a"] = "M4A 音频",
            [".wma"] = "Windows Media 音频",
            [".opus"] = "Opus 音频",
            // 视频
            [".mp4"] = "MP4 视频",
            [".mkv"] = "Matroska 视频",
            [".avi"] = "AVI 视频",
            [".mov"] = "QuickTime 视频",
            [".wmv"] = "Windows Media 视频",
            [".flv"] = "FLV 视频",
            [".webm"] = "WebM 视频",
            [".m4v"] = "M4V 视频",
            // 程序与脚本
            [".exe"] = "应用程序",
            [".msi"] = "Windows 安装程序",
            [".dll"] = "应用程序扩展",
            [".bat"] = "Windows 批处理文件",
            [".cmd"] = "Windows 命令脚本",
            [".ps1"] = "PowerShell 脚本",
            [".sys"] = "系统文件",
            // 压缩包
            [".zip"] = "压缩 (zipped) 文件夹",
            [".rar"] = "RAR 压缩文件",
            [".7z"] = "7-Zip 压缩文件",
            [".tar"] = "TAR 压缩文件",
            [".gz"] = "GZ 压缩文件",
            // Office 文档
            [".doc"] = "Word 97-2003 文档",
            [".docx"] = "Word 文档",
            [".xls"] = "Excel 97-2003 工作表",
            [".xlsx"] = "Excel 工作表",
            [".ppt"] = "PowerPoint 97-2003 演示文稿",
            [".pptx"] = "PowerPoint 演示文稿",
            [".csv"] = "CSV 文件",
            // 其他
            [".pdf"] = "PDF 文档",
            [".iso"] = "光盘映像文件",
            [".lnk"] = "快捷方式",
            [".ttf"] = "TrueType 字体",
            [".otf"] = "OpenType 字体",
            [".ini"] = "配置设置",
            [".cfg"] = "配置文件",
            [".db"] = "数据库文件",
            [".sql"] = "SQL 文件"
        };

        /// <summary>
        /// 文件名（从原路径提取，不含目录）。
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// 原始完整路径（删除前的位置）。
        /// </summary>
        public string OriginalPath { get; set; } = string.Empty;

        /// <summary>
        /// 来源盘符（如 "C:"、 "D:"）。
        /// </summary>
        public string? SourceDrive { get; set; }

        /// <summary>
        /// 删除时间（UTC 时间，由 $I 文件中的 FILETIME 转换而来）。
        /// </summary>
        public DateTimeOffset DeletedDate { get; set; }

        /// <summary>
        /// 文件大小（字节），由 $I 文件中解析得到。
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// 回收站中实际数据文件的完整路径（$R 文件路径）。
        /// </summary>
        public string? RecycleBinPath { get; set; }

        /// <summary>
        /// 对应的元数据文件路径（$I 文件路径）。
        /// </summary>
        public string? MetaFilePath { get; set; }

        /// <summary>
        /// 获取易读的文件大小文本（如 "1.5 MB"）。
        /// </summary>
        public string SizeText
        {
            get
            {
                if (Size < 1024)
                    return $"{Size} B";
                if (Size < 1024 * 1024)
                    return $"{Size / 1024.0:F1} KB";
                if (Size < 1024 * 1024 * 1024)
                    return $"{Size / (1024.0 * 1024.0):F1} MB";
                return $"{Size / (1024.0 * 1024.0 * 1024.0):F2} GB";
            }
        }

        /// <summary>
        /// 获取易读的删除日期文本。
        /// </summary>
        public string DeletedDateText => DeletedDate.LocalDateTime.ToString("yyyy/MM/dd HH:mm");

        /// <summary>
        /// 创建时间（从回收站数据文件 $R 获取；数据文件缺失时为空表示未知）。
        /// </summary>
        public DateTimeOffset? CreationTime { get; set; }

        /// <summary>
        /// 文件属性（从回收站数据文件 $R 获取；数据文件缺失时为空表示未知）。
        /// </summary>
        public FileAttributes? Attributes { get; set; }

        /// <summary>
        /// 获取易读的创建时间文本。
        /// </summary>
        public string CreationTimeText =>
            CreationTime?.LocalDateTime.ToString("yyyy/MM/dd HH:mm") ?? "未知";

        /// <summary>
        /// 获取文件类型描述（如 "文本文档"、"PNG 图像"、"文件夹"）。
        /// 目录依据文件属性判断，其余按扩展名查表，未知扩展名显示为 "XXX 文件"。
        /// </summary>
        public string FileTypeText
        {
            get
            {
                // 目录优先判断
                if (Attributes.HasValue && Attributes.Value.HasFlag(FileAttributes.Directory))
                    return "文件夹";

                string extension = Path.GetExtension(FileName);
                if (string.IsNullOrEmpty(extension))
                    return "文件";

                if (FileTypeMap.TryGetValue(extension, out string? typeName))
                    return typeName;

                // 未知扩展名：模仿 Windows 显示 "XXX 文件"（如 ".abc" → "ABC 文件"）
                return $"{extension.TrimStart('.').ToUpperInvariant()} 文件";
            }
        }

        /// <summary>
        /// 获取文件属性文本（如 "只读、存档、隐藏"），未获取到时返回"未知"。
        /// Windows 普通文件默认带有"存档"属性。
        /// </summary>
        public string AttributesText
        {
            get
            {
                if (!Attributes.HasValue)
                    return "未知";

                FileAttributes attrs = Attributes.Value;
                var names = new List<string>();
                if (attrs.HasFlag(FileAttributes.ReadOnly)) names.Add("只读");
                if (attrs.HasFlag(FileAttributes.Hidden)) names.Add("隐藏");
                if (attrs.HasFlag(FileAttributes.System)) names.Add("系统");
                if (attrs.HasFlag(FileAttributes.Archive)) names.Add("存档");
                if (attrs.HasFlag(FileAttributes.Compressed)) names.Add("压缩");
                if (attrs.HasFlag(FileAttributes.Encrypted)) names.Add("加密");
                if (attrs.HasFlag(FileAttributes.Temporary)) names.Add("临时");
                if (attrs.HasFlag(FileAttributes.Offline)) names.Add("脱机");
                if (attrs.HasFlag(FileAttributes.NotContentIndexed)) names.Add("不编制索引");
                if (attrs.HasFlag(FileAttributes.SparseFile)) names.Add("稀疏文件");
                if (attrs.HasFlag(FileAttributes.ReparsePoint)) names.Add("重解析点");
                if (attrs.HasFlag(FileAttributes.IntegrityStream)) names.Add("完整性流");
                if (attrs.HasFlag(FileAttributes.NoScrubData)) names.Add("不擦除数据");

                return names.Count == 0 ? "无" : string.Join("、", names);
            }
        }
    }
}
