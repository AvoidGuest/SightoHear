using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using SightoHear.Helpers;
using SightoHear.Models;

namespace SightoHear.Services
{
    /// <summary>
    /// 回收站管理服务，基于解析各盘符 $Recycle.Bin 下的 $I 元数据文件实现。
    /// 对应智谱 API 文档中的"方案二：直接解析 $Recycle.Bin 隐藏文件夹"。
    /// </summary>
    public static class RecycleBinService
    {
        private const string RecycleBinFolderName = "$Recycle.Bin";
        private const string MetaFilePrefix = "$I";
        private const string DataFilePrefix = "$R";

        /// <summary>
        /// 获取回收站中所有项的列表。
        /// </summary>
        public static List<RecycleBinItem> GetItems()
        {
            var items = new List<RecycleBinItem>();

            try
            {
                // 获取当前用户 SID
                string currentUserSid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;

                // 遍历所有逻辑驱动器
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .ToList();

                foreach (var drive in drives)
                {
                    string recycleBinRoot = Path.Combine(drive.RootDirectory.FullName, RecycleBinFolderName);
                    if (!Directory.Exists(recycleBinRoot))
                        continue;

                    // 遍历回收站根目录下的所有子目录（每个子目录对应一个用户 SID）
                    IEnumerable<string> sidDirectories;
                    try
                    {
                        sidDirectories = Directory.GetDirectories(recycleBinRoot);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    foreach (string sidDir in sidDirectories)
                    {
                        // 如果知道当前用户 SID，只扫描当前用户的回收站，避免权限问题
                        if (!string.IsNullOrEmpty(currentUserSid))
                        {
                            string dirName = Path.GetFileName(sidDir);
                            if (!string.Equals(dirName, currentUserSid, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        ParseRecycleBinDirectory(sidDir, items);
                    }
                }
            }
            catch (Exception ex)
            {
                // 整体失败时返回已收集到的项
                AppLogger.Error(ex, "回收站扫描整体失败");
            }

            return items;
        }

        /// <summary>
        /// 解析单个回收站用户目录下的所有 $I 文件。
        /// </summary>
        private static void ParseRecycleBinDirectory(string sidDirectory, List<RecycleBinItem> items)
        {
            IEnumerable<string> metaFiles;
            try
            {
                metaFiles = Directory.GetFiles(sidDirectory, MetaFilePrefix + "*");
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            foreach (string metaFile in metaFiles)
            {
                try
                {
                    RecycleBinItem? item = ParseMetaFile(metaFile);
                    if (item != null)
                    {
                        items.Add(item);
                    }
                }
                catch
                {
                    // 跳过无法解析的文件
                }
            }
        }

        /// <summary>
        /// 解析单个 $I 元数据文件。
        /// </summary>
        private static RecycleBinItem? ParseMetaFile(string metaFilePath)
        {
            // $I 文件前 24 字节为固定头部，之后是 UTF-16LE 编码的原路径
            const int HeaderSize = 24;

            try
            {
                byte[] buffer = File.ReadAllBytes(metaFilePath);
                if (buffer.Length < HeaderSize)
                    return null;

                using var ms = new MemoryStream(buffer);
                using var reader = new BinaryReader(ms);

                // 读取版本标识（8 字节，跳过）
                reader.ReadInt64();

                // 读取文件大小（8 字节，小端序）
                long size = reader.ReadInt64();

                // 读取删除时间（8 字节，FILETIME，小端序）
                long fileTime = reader.ReadInt64();
                DateTimeOffset deletedDate;
                try
                {
                    deletedDate = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
                }
                catch (ArgumentException)
                {
                    deletedDate = DateTimeOffset.UtcNow;
                }

                // 读取原路径（根据版本号正确处理头部与长度字段）
                int version = BitConverter.ToInt32(buffer, 0);
                string originalPath;
                if (version >= 2 && buffer.Length >= 28)
                {
                    int nameLength = BitConverter.ToInt32(buffer, 24);
                    int pathOffset = 28;
                    int availableBytes = buffer.Length - pathOffset;
                    int bytesToRead = Math.Min(nameLength * 2, availableBytes);

                    if (bytesToRead > 0)
                    {
                        originalPath = Encoding.Unicode.GetString(buffer, pathOffset, bytesToRead);
                        int nullIndex = originalPath.IndexOf('\0');
                        if (nullIndex >= 0)
                            originalPath = originalPath.Substring(0, nullIndex);
                    }
                    else
                    {
                        originalPath = DecodePath(buffer, 28);
                    }
                }
                else
                {
                    originalPath = DecodePath(buffer, 24);
                }
                if (string.IsNullOrWhiteSpace(originalPath))
                    return null;

                // 清理路径：移除末尾的空格和不可见字符
                originalPath = originalPath.TrimEnd(' ', '\0', '\r', '\n');

                // 构造对应的 $R 文件路径（同目录下，$I 换成 $R）
                string directory = Path.GetDirectoryName(metaFilePath)!;
                string fileName = Path.GetFileName(metaFilePath);
                string dataFileName = DataFilePrefix + fileName.Substring(MetaFilePrefix.Length);
                string recycleBinPath = Path.Combine(directory, dataFileName);

                // 从 $R 数据文件读取创建时间与文件属性（供属性详情弹窗展示）。
                // $I 元数据中不包含创建时间与属性，只能从实际数据文件获取。
                DateTimeOffset? creationTime = null;
                FileAttributes? attributes = null;
                if (File.Exists(recycleBinPath))
                {
                    try
                    {
                        creationTime = new DateTimeOffset(File.GetCreationTime(recycleBinPath));
                    }
                    catch
                    {
                        // 读取失败则保持未知，不影响整体解析
                    }

                    try
                    {
                        attributes = File.GetAttributes(recycleBinPath);
                    }
                    catch
                    {
                        // 读取失败则保持未知，不影响整体解析
                    }
                }

                // 提取文件名和盘符
                string fileNameOnly = Path.GetFileName(originalPath);
                string? drive = null;
                try
                {
                    if (originalPath.Length >= 2 && originalPath[1] == ':')
                    {
                        drive = originalPath.Substring(0, 2).ToUpperInvariant();
                    }
                }
                catch
                {
                    // 忽略
                }

                return new RecycleBinItem
                {
                    FileName = string.IsNullOrEmpty(fileNameOnly) ? Path.GetFileName(originalPath) : fileNameOnly,
                    OriginalPath = originalPath,
                    SourceDrive = drive,
                    DeletedDate = deletedDate,
                    Size = size > 0 ? size : (File.Exists(recycleBinPath) ? new FileInfo(recycleBinPath).Length : 0),
                    RecycleBinPath = File.Exists(recycleBinPath) ? recycleBinPath : null,
                    MetaFilePath = metaFilePath,
                    CreationTime = creationTime,
                    Attributes = attributes
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解码 $I 文件中的路径信息，优先 UTF-16LE，回退 UTF-8。
        /// </summary>
        private static string DecodePath(byte[] buffer, int headerSize)
        {
            // 确保长度合法
            if (buffer.Length <= headerSize)
                return string.Empty;

            int pathBytesLength = buffer.Length - headerSize;
            // 确保是 2 的倍数（UTF-16LE 要求）
            if (pathBytesLength % 2 != 0)
                pathBytesLength--;

            if (pathBytesLength <= 0)
                return string.Empty;

            // 先尝试 UTF-16LE
            string? path = TryDecodeUtf16Le(buffer, headerSize, pathBytesLength);
            if (IsValidPath(path))
                return path ?? string.Empty;

            // 回退 UTF-8
            path = TryDecodeUtf8(buffer, headerSize, buffer.Length - headerSize);
            if (IsValidPath(path))
                return path ?? string.Empty;

            // 如果都不合理，返回 UTF-16LE 结果（可能部分乱码，但至少不崩）
            return path ?? string.Empty;
        }

        /// <summary>
        /// 尝试 UTF-16LE 解码。
        /// </summary>
        private static string? TryDecodeUtf16Le(byte[] buffer, int offset, int length)
        {
            try
            {
                string path = Encoding.Unicode.GetString(buffer, offset, length);
                int nullIndex = path.IndexOf('\0');
                if (nullIndex >= 0)
                    path = path.Substring(0, nullIndex);
                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 尝试 UTF-8 解码。
        /// </summary>
        private static string? TryDecodeUtf8(byte[] buffer, int offset, int length)
        {
            try
            {
                string path = Encoding.UTF8.GetString(buffer, offset, length);
                int nullIndex = path.IndexOf('\0');
                if (nullIndex >= 0)
                    path = path.Substring(0, nullIndex);
                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 判断路径是否合理：非空、长度 >= 3、以盘符开头。
        /// </summary>
        private static bool IsValidPath(string? path)
        {
            return !string.IsNullOrEmpty(path)
                && path.Length >= 3
                && path[1] == ':'
                && (path[2] == '\\' || path[2] == '/');
        }

        /// <summary>
        /// 还原回收站项到原始位置。
        /// </summary>
        public static bool RestoreItem(RecycleBinItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.RecycleBinPath) || string.IsNullOrEmpty(item.OriginalPath))
            {
                AppLogger.Warning("还原失败: 回收站项数据不完整");
                return false;
            }

            try
            {
                string originalDir = Path.GetDirectoryName(item.OriginalPath)!;
                
                // 确保原始目录存在
                if (!Directory.Exists(originalDir))
                {
                    try
                    {
                        Directory.CreateDirectory(originalDir);
                    }
                    catch
                    {
                        AppLogger.Warning($"还原失败: 无法创建原始目录 {originalDir}");
                        return false;
                    }
                }

                string targetPath = item.OriginalPath;

                // 如果目标路径已存在同名文件，在文件名后追加后缀
                if (File.Exists(targetPath))
                {
                    string baseName = Path.GetFileNameWithoutExtension(targetPath);
                    string extension = Path.GetExtension(targetPath);
                    string directory = Path.GetDirectoryName(targetPath)!;
                    int counter = 1;

                    while (File.Exists(targetPath) && counter < 1000)
                    {
                        targetPath = Path.Combine(directory, $"{baseName} ({counter}){extension}");
                        counter++;
                    }
                }

                // 移动文件回原路径
                File.Move(item.RecycleBinPath, targetPath);

                // 删除元数据文件
                if (!string.IsNullOrEmpty(item.MetaFilePath) && File.Exists(item.MetaFilePath))
                {
                    try
                    {
                        File.Delete(item.MetaFilePath);
                    }
                    catch
                    {
                        // 元数据文件删除失败不影响还原结果
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"还原失败: {item.OriginalPath}");
                return false;
            }
        }

        /// <summary>
        /// 永久删除回收站项（不进入回收站）。
        /// </summary>
        public static bool PermanentlyDeleteItem(RecycleBinItem item)
        {
            if (item == null)
            {
                AppLogger.Warning("永久删除失败: 回收站项为空");
                return false;
            }

            try
            {
                if (!string.IsNullOrEmpty(item.RecycleBinPath) && File.Exists(item.RecycleBinPath))
                {
                    File.Delete(item.RecycleBinPath);
                }

                if (!string.IsNullOrEmpty(item.MetaFilePath) && File.Exists(item.MetaFilePath))
                {
                    File.Delete(item.MetaFilePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"永久删除失败: {item.FileName}");
                return false;
            }
        }

        /// <summary>
        /// 永久删除多个回收站项。
        /// </summary>
        public static int PermanentlyDeleteItems(IEnumerable<RecycleBinItem> items)
        {
            int count = 0;
            foreach (var item in items)
            {
                if (PermanentlyDeleteItem(item))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 还原多个回收站项。
        /// </summary>
        public static int RestoreItems(IEnumerable<RecycleBinItem> items)
        {
            int count = 0;
            foreach (var item in items)
            {
                if (RestoreItem(item))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 清空回收站（永久删除所有项）。
        /// </summary>
        public static int EmptyRecycleBin()
        {
            var allItems = GetItems();
            return PermanentlyDeleteItems(allItems);
        }
    }
}
