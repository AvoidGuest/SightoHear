using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using SightoHear.Helpers;
using SightoHear.Models;

namespace SightoHear.Services
{
    /// <summary>
    /// 媒体库文件夹管理服务：
    /// 在视频/音乐/图库页面中通过「媒体库管理」弹窗临时勾选要展示的文件夹，
    /// 与设置页「基础设置 → 数据库」共用同一份 settings.json 配置。
    /// 勾选状态（VideoEnabledLibraryPaths 等字段）独立持久化，不影响库路径本身。
    /// </summary>
    public static class MediaLibraryFolderManager
    {
        /// <summary>启用文件夹（勾选展示）变更事件，参数为媒体类型（Video/Music/Image）。</summary>
        public static event EventHandler<string>? EnabledFoldersChanged;

        /// <summary>根据媒体类型获取「启用文件夹」的 settings.json 键名。</summary>
        private static string GetEnabledKey(string mediaType) => mediaType switch
        {
            "Video" => "VideoEnabledLibraryPaths",
            "Music" => "MusicEnabledLibraryPaths",
            "Image" => "ImageEnabledLibraryPaths",
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
        };

        /// <summary>根据媒体类型获取库路径（文件夹列表）的 settings.json 键名。</summary>
        private static string GetLibraryKey(string mediaType) => mediaType switch
        {
            "Video" => "VideoLibraryPaths",
            "Music" => "MusicLibraryPaths",
            "Image" => "ImageLibraryPaths",
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
        };

        /// <summary>读取当前媒体库的库路径列表（即设置页「数据库」中配置的文件夹）。</summary>
        public static List<string> GetLibraryPaths(string mediaType)
            => MediaScanner.GetLibrarySettings(mediaType).Paths;

        /// <summary>
        /// 读取当前勾选（启用）展示的文件夹列表。
        /// 若从未设置过（字段不存在），返回全部库路径（默认全部展示）；
        /// 若用户已显式清空勾选，返回空列表（页面不展示任何内容）。
        /// </summary>
        public static List<string> GetEnabledFolders(string mediaType)
        {
            List<string> libraryPaths = GetLibraryPaths(mediaType);
            try
            {
                if (!File.Exists(MediaScanner.SettingsPath))
                    return libraryPaths;

                JsonNode? node = JsonNode.Parse(File.ReadAllText(MediaScanner.SettingsPath));
                JsonNode? enabledNode = node?[GetEnabledKey(mediaType)];

                // 字段不存在 → 从未设置过勾选，默认全部展示
                if (enabledNode == null)
                    return libraryPaths;

                var enabled = new List<string>();
                if (enabledNode is JsonArray arr)
                {
                    foreach (JsonNode? value in arr)
                    {
                        string? path = value?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(path))
                            enabled.Add(path);
                    }
                }

                // 仅保留仍存在于库路径中的文件夹（库路径被移除后自动失效）
                return libraryPaths
                    .Where(p => enabled.Contains(p, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"读取 {mediaType} 启用文件夹失败");
                return libraryPaths;
            }
        }

        /// <summary>保存勾选（启用）展示的文件夹列表，并通知订阅方（三个媒体库页面）刷新。</summary>
        public static void SetEnabledFolders(string mediaType, IEnumerable<string> folders)
        {
            try
            {
                JsonNode? node = null;
                if (File.Exists(MediaScanner.SettingsPath))
                    node = JsonNode.Parse(File.ReadAllText(MediaScanner.SettingsPath));
                node ??= new JsonObject();

                var arr = new JsonArray();
                foreach (string path in folders.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        arr.Add(path);
                }
                node[GetEnabledKey(mediaType)] = arr;

                Directory.CreateDirectory(Path.GetDirectoryName(MediaScanner.SettingsPath)!);
                File.WriteAllText(MediaScanner.SettingsPath, node.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                }));
                AppLogger.Info($"媒体库文件夹勾选状态已保存: {mediaType} 共 {arr.Count} 个");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"保存 {mediaType} 启用文件夹失败");
                return;
            }

            EnabledFoldersChanged?.Invoke(null, mediaType);
        }

        /// <summary>
        /// 主动通知订阅方重新加载媒体库数据（如弹窗中添加文件夹后的后台扫描完成时调用，
        /// 避免依赖 CacheUpdated 事件被页面内部跳过）。
        /// </summary>
        public static void TriggerRefresh(string mediaType)
            => EnabledFoldersChanged?.Invoke(null, mediaType);

        /// <summary>
        /// 判断文件是否位于某个启用文件夹之下（含子目录）。
        /// 未设置勾选（默认全部）时返回 true；用户显式清空勾选时返回 false。
        /// </summary>
        public static bool IsFileVisible(string mediaType, string filePath)
        {
            List<string> enabled = GetEnabledFolders(mediaType);
            if (enabled.Count == 0)
                return false;
            return enabled.Any(folder => IsPathInside(filePath, folder));
        }

        /// <summary>判断文件路径是否位于文件夹路径之下（含子目录，Windows 大小写不敏感）。</summary>
        private static bool IsPathInside(string filePath, string folder)
        {
            try
            {
                string relative = Path.GetRelativePath(folder, filePath);
                return !relative.StartsWith("..", StringComparison.Ordinal) &&
                       !Path.IsPathRooted(relative);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 按启用文件夹过滤媒体项列表。
        /// 未设置勾选（默认全部）时返回原列表；用户显式清空勾选时返回空列表。
        /// </summary>
        public static List<MediaItem> FilterByEnabledFolders(List<MediaItem>? items, string mediaType)
        {
            if (items == null || items.Count == 0)
                return new List<MediaItem>();

            List<string> enabled = GetEnabledFolders(mediaType);
            // 用户显式清空勾选（字段存在但为空）→ 不展示任何内容
            if (enabled.Count == 0)
                return new List<MediaItem>();

            return items
                .Where(item => enabled.Any(folder => IsPathInside(item.FilePath, folder)))
                .ToList();
        }

        /// <summary>
        /// 以「过滤后的可见列表」更新媒体库缓存，同时保留完整缓存中被过滤隐藏的项目。
        /// 页面在勾选过滤状态下执行删除/更新后直接调用 SaveToCache 会丢失其他文件夹的缓存数据，
        /// 此方法将当前可见项与旧缓存中的隐藏项合并后再保存，保证缓存始终为完整数据库。
        /// </summary>
        public static void SaveMergedCache(List<MediaItem> visibleItems, string mediaType)
        {
            try
            {
                List<string> enabled = GetEnabledFolders(mediaType);
                List<MediaItem> fullCache = MediaScanner.LoadFromCache(mediaType);

                // 旧缓存中「不属于启用文件夹（被过滤隐藏）」的项目需要保留；
                // 用户显式清空勾选（enabled 为空）时所有项均视为隐藏，完整保留缓存
                List<MediaItem> hiddenItems = fullCache
                    .Where(item => !IsVisibleByEnabledFolders(item.FilePath, enabled))
                    .ToList();

                var merged = visibleItems
                    .Concat(hiddenItems)
                    .GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                MediaScanner.SaveToCache(merged, mediaType);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"合并保存 {mediaType} 缓存失败");
            }
        }

        /// <summary>判断文件是否在启用文件夹集合中可见（enabled 为空表示用户清空勾选，视为不可见）。</summary>
        private static bool IsVisibleByEnabledFolders(string filePath, List<string> enabled)
        {
            if (enabled.Count == 0)
                return false;
            return enabled.Any(folder => IsPathInside(filePath, folder));
        }

        /// <summary>将文件夹添加到媒体库路径（写入 settings.json，与设置页「数据库」共用）。</summary>
        public static void AddLibraryFolder(string mediaType, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            List<string> paths = GetLibraryPaths(mediaType);
            if (paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                AppLogger.Warning($"媒体库文件夹已存在, 忽略重复添加: {path}");
                return;
            }

            SaveLibraryPaths(mediaType, paths.Concat(new[] { path }));
            AppLogger.Info($"添加媒体库文件夹: {mediaType} {path}");
        }

        /// <summary>从媒体库路径中移除文件夹，并同步从启用列表中移除。</summary>
        public static void RemoveLibraryFolder(string mediaType, string path)
        {
            List<string> paths = GetLibraryPaths(mediaType);
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                return;

            SaveLibraryPaths(mediaType,
                paths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
            AppLogger.Info($"移除媒体库文件夹: {mediaType} {path}");

            // 同步从启用列表中移除（若存在）
            List<string> enabled = GetEnabledFolders(mediaType);
            if (enabled.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                SetEnabledFolders(mediaType,
                    enabled.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
            }
        }

        /// <summary>保存媒体库路径（文件夹列表）到 settings.json。</summary>
        private static void SaveLibraryPaths(string mediaType, IEnumerable<string> paths)
        {
            try
            {
                JsonNode? node = null;
                if (File.Exists(MediaScanner.SettingsPath))
                    node = JsonNode.Parse(File.ReadAllText(MediaScanner.SettingsPath));
                node ??= new JsonObject();

                var arr = new JsonArray();
                foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        arr.Add(path);
                }
                node[GetLibraryKey(mediaType)] = arr;

                Directory.CreateDirectory(Path.GetDirectoryName(MediaScanner.SettingsPath)!);
                File.WriteAllText(MediaScanner.SettingsPath, node.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                }));
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"保存 {mediaType} 媒体库路径失败");
            }
        }
    }
}
