using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using SightoHear.Models;
using Windows.Storage;
using SightoHear.Helpers;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using FFmpegInteropX;

namespace SightoHear.Services
{
    public static class MediaScanner
    {
        public static event EventHandler<string>? CacheUpdated;

        public static string SettingsPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear",
            "settings.json");

        // 已知文件夹 GUID
        private static readonly Guid FOLDERID_Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
        private static readonly Guid FOLDERID_Music = new("4BD8D571-6D19-48D3-BE97-422220080E43");
        private static readonly Guid FOLDERID_Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
            uint dwFlags,
            IntPtr hToken,
            out IntPtr ppszPath);

        private static string GetKnownFolderPath(Guid folderId)
        {
            IntPtr pathPtr = IntPtr.Zero;
            try
            {
                int hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out pathPtr);
                if (hr == 0)
                    return Marshal.PtrToStringUni(pathPtr) ?? string.Empty;
            }
            finally
            {
                if (pathPtr != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(pathPtr);
            }
            return string.Empty;
        }

        public static string GetVideosPath() => GetKnownFolderPath(FOLDERID_Videos);

        public static string GetDefaultVideoPath()
        {
            var path = GetVideosPath();
            if (string.IsNullOrEmpty(path))
                path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return path;
        }

        /// <summary>
        /// 兼容旧调用入口，统一初始化视频、音乐和图库路径。
        /// </summary>
        public static void EnsureDefaultSettings() =>
            InitializeDefaultLibrarySettings();
        public static string GetMusicPath() => GetKnownFolderPath(FOLDERID_Music);
        public static string GetDefaultMusicPath()
        {
            var path = GetMusicPath();
            if (string.IsNullOrEmpty(path))
                path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            return path;
        }

        public static string GetPicturesPath() => GetKnownFolderPath(FOLDERID_Pictures);
        public static string GetDefaultPicturesPath()
        {
            string path = GetPicturesPath();
            if (string.IsNullOrWhiteSpace(path))
                path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return path;
        }

        public static void InitializeDefaultLibrarySettings()
        {
            string settingsDirectory = Path.GetDirectoryName(SettingsPath)!;

            // Existing empty arrays are intentional user choices. Seed defaults only
            // when the application has never created a configuration file.
            if (File.Exists(SettingsPath))
                return;

            try
            {
                Directory.CreateDirectory(settingsDirectory);
                var settings = new JsonObject
                {
                    ["VideoLibraryPaths"] = CreateDefaultPathArray(GetDefaultVideoPath()),
                    ["MusicLibraryPaths"] = CreateDefaultPathArray(GetDefaultMusicPath()),
                    ["ImageLibraryPaths"] = CreateDefaultPathArray(GetDefaultPicturesPath()),
                    ["RecursiveScan"] = true,
                    ["MusicRecursiveScan"] = true,
                    ["ImageRecursiveScan"] = true
                };

                File.WriteAllText(
                    SettingsPath,
                    settings.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                    }));

                AppLogger.Info("首次运行: 已初始化视频、音乐和图库默认路径");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "首次运行初始化媒体库路径失败");
            }
        }

        private static JsonArray CreateDefaultPathArray(string path)
        {
            var paths = new JsonArray();
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                paths.Add(path);
            return paths;
        }

        public static (List<string> Paths, bool Recursive) GetLibrarySettings(
            string mediaType)
        {
            InitializeDefaultLibrarySettings();

            string pathsKey = mediaType switch
            {
                "Video" => "VideoLibraryPaths",
                "Music" => "MusicLibraryPaths",
                "Image" => "ImageLibraryPaths",
                _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
            };
            string recursiveKey = mediaType switch
            {
                "Video" => "RecursiveScan",
                "Music" => "MusicRecursiveScan",
                "Image" => "ImageRecursiveScan",
                _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
            };

            var paths = new List<string>();
            bool recursive = true;
            try
            {
                JsonNode? node = JsonNode.Parse(File.ReadAllText(SettingsPath));
                if (node?[pathsKey] is JsonArray pathsArray)
                {
                    foreach (JsonNode? value in pathsArray)
                    {
                        string? path = value?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                            paths.Add(path);
                    }
                }

                recursive = node?[recursiveKey]?.GetValue<bool>() ?? true;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"读取 {mediaType} 媒体库设置失败");
            }

            return (paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), recursive);
        }

        public static async Task<List<MediaItem>> RefreshLibraryAsync(string mediaType)
        {
            var (paths, recursive) = GetLibrarySettings(mediaType);
            SearchOption searchOption = recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;
            var allItems = new List<MediaItem>();
            IReadOnlyDictionary<string, MediaItem>? existingItems = LoadFromCache(mediaType)
                .GroupBy(
                    item => item.FilePath,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                allItems.AddRange(await ScanFolderAsync(
                    path,
                    mediaType,
                    searchOption,
                    existingItems));
            }

            var uniqueItems = allItems
                .GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            SaveToCache(uniqueItems, mediaType);
            return uniqueItems;
        }

        public static async Task RefreshAllLibrariesAsync()
        {
            foreach (string mediaType in new[] { "Video", "Music", "Image" })
            {
                try
                {
                    await RefreshLibraryAsync(mediaType);
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"启动刷新 {mediaType} 媒体库失败");
                }
            }
        }

        private static readonly string[] VideoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts" };
        private static readonly string[] MusicExtensions = new[] { ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".wma", ".opus" };
        private static readonly string[] ImageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".heic", ".raw", ".cr2", ".nef" };

        public static async Task<int> ScanAsync(string path, bool recursive)
        {
            AppLogger.Info($"扫描媒体库: 路径={path}, 递归={recursive}");
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var items = await ScanFolderAsync(path, "Video", searchOption);
            SaveToCache(items, "Video");
            AppLogger.Info($"扫描完成: 找到{items.Count}个媒体文件");
            return items.Count;
        }

        public static async Task<(List<MediaItem> items, int addedCount, int scannedCount)> ScanWithStatsAsync(string path, bool recursive)
        {
            AppLogger.Info($"增量扫描媒体库: 路径={path}, 递归={recursive}");
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var oldCache = LoadFromCache("Video");
            var oldPaths = new HashSet<string>(oldCache.Select(m => m.FilePath));
            var newItems = await ScanFolderAsync(path, "Video", searchOption);
            var newPaths = new HashSet<string>(newItems.Select(m => m.FilePath));
            var addedCount = newPaths.Except(oldPaths).Count();
            SaveToCache(newItems, "Video");
            AppLogger.Info($"增量扫描完成: 扫描{newItems.Count}个文件, 新增{addedCount}个文件");
            return (newItems, addedCount, newItems.Count);
        }

        public static async Task<List<MediaItem>> ScanFolderAsync(
            string folderPath,
            string mediaType,
            SearchOption searchOption = SearchOption.AllDirectories,
            IReadOnlyDictionary<string, MediaItem>? existingItems = null)
        {
            var items = new List<MediaItem>();
            if (!Directory.Exists(folderPath))
                return items;

            string[] extensions = mediaType switch
            {
                "Video" => VideoExtensions,
                "Music" => MusicExtensions,
                "Image" => ImageExtensions,
                _ => Array.Empty<string>()
            };

            await Task.Run(() =>
            {
                try
                {
                    var files = Directory.EnumerateFiles(folderPath, "*.*", searchOption)
                        .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

                    foreach (var file in files)
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            DateTime scannedAt = DateTime.Now;
                            MediaItem? existingItem = null;
                            existingItems?.TryGetValue(file, out existingItem);

                            // 文件是否与上次扫描时一致（大小和修改时间未变）
                            bool fileUnchanged = existingItem != null &&
                                existingItem.FileSize == info.Length &&
                                existingItem.DateModified == info.LastWriteTime;

                            var item = new MediaItem
                            {
                                FilePath = file,
                                FileName = Path.GetFileNameWithoutExtension(file),
                                Title = Path.GetFileNameWithoutExtension(file),
                                FileSize = info.Length,
                                DateCreated = info.CreationTime,
                                DateModified = info.LastWriteTime,
                                DateScanned = fileUnchanged &&
                                    existingItem!.DateScanned != default
                                        ? existingItem.DateScanned
                                        : scannedAt,
                                MediaType = mediaType,
                                // ★ 保留已验证的缩略图路径，避免因增量扫描丢失已提取的封面
                                ThumbnailPath = fileUnchanged && !string.IsNullOrEmpty(existingItem?.ThumbnailPath)
                                    ? existingItem.ThumbnailPath
                                    : string.Empty
                            };

                            // ★ 图片/视频：文件未变时保留已提取的像素尺寸与帧率
                            if (fileUnchanged &&
                                existingItem!.PixelWidth > 0 &&
                                existingItem.PixelHeight > 0)
                            {
                                item.PixelWidth = existingItem.PixelWidth;
                                item.PixelHeight = existingItem.PixelHeight;
                            }

                            if (fileUnchanged && existingItem!.FrameRate.HasValue)
                                item.FrameRate = existingItem.FrameRate;

                            if (fileUnchanged && !string.IsNullOrEmpty(existingItem?.VideoCodec))
                                item.VideoCodec = existingItem.VideoCodec;

                            items.Add(item);
                        }
                        catch { }
                    }

                }
                catch { }
            });

            if (mediaType == "Video")
            {
                // ★ 只对尚未有缩略图路径的项进行提取（已在增量扫描中保留的跳过）
                var itemsNeedingThumbnails = items
                    .Where(i => string.IsNullOrEmpty(i.ThumbnailPath))
                    .ToList();

                if (itemsNeedingThumbnails.Count > 0)
                {
                    await Parallel.ForEachAsync(
                        itemsNeedingThumbnails,
                        new ParallelOptions { MaxDegreeOfParallelism = 3 },
                        async (item, _) =>
                        {
                            item.ThumbnailPath =
                                await ExtractThumbnailAsync(item.FilePath);
                        });
                }

                // ★ 提取视频时长、分辨率与帧率（对所有缺失数据的项进行补全）
                var itemsNeedingMetadata = items
                    .Where(i => !i.Duration.HasValue ||
                                i.PixelWidth <= 0 ||
                                i.PixelHeight <= 0 ||
                                !i.FrameRate.HasValue)
                    .ToList();

                if (itemsNeedingMetadata.Count > 0)
                {
                    await Parallel.ForEachAsync(
                        itemsNeedingMetadata,
                        new ParallelOptions { MaxDegreeOfParallelism = 3 },
                        async (item, _) =>
                        {
                            var (duration, width, height, frameRate) =
                                await GetVideoMetadataAsync(item.FilePath);
                            if (!item.Duration.HasValue)
                                item.Duration = duration;
                            if (item.PixelWidth <= 0)
                                item.PixelWidth = width;
                            if (item.PixelHeight <= 0)
                                item.PixelHeight = height;
                            if (!item.FrameRate.HasValue)
                                item.FrameRate = frameRate;
                        });
                }
            }
            // 对图片文件读取原始像素尺寸（在 UI 线程上下文外读取）
            else if (mediaType == "Image")
            {
                await ReadImageDimensionsAsync(items);
            }
            else if (mediaType == "Music")
            {
                await EnrichMusicMetadataAsync(items);
            }

            return items;
        }

        public static Task EnrichMusicMetadataAsync(
            IEnumerable<MediaItem> items,
            bool onlyUnscanned = false)
        {
            var musicItems = onlyUnscanned
                ? items.Where(item => !item.MusicMetadataScanned).ToList()
                : items.ToList();

            return Parallel.ForEachAsync(
                musicItems,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                async (item, _) =>
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.FilePath);

                    // 某些格式（如 M4A/AAC）在缺少系统编解码器的机器上读取属性会抛异常甚至长时间挂起，
                    // 这里加超时保护，避免单个文件拖垮整个并行扫描导致音乐扫描不出结果。
                    var properties = await file.Properties
                        .GetMusicPropertiesAsync()
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(15));

                    if (!string.IsNullOrWhiteSpace(properties.Title))
                        item.Title = properties.Title.Trim();

                    item.Artist = properties.Artist?.Trim() ?? string.Empty;
                    item.Album = properties.Album?.Trim() ?? string.Empty;
                    item.TrackNumber = properties.TrackNumber;

                    if (properties.Duration > TimeSpan.Zero)
                        item.Duration = properties.Duration;
                }
                catch
                {
                    // Unsupported or unreadable metadata keeps the filename fallback.
                }
                finally
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(item.ThumbnailPath) ||
                            !File.Exists(item.ThumbnailPath))
                        {
                            item.ThumbnailPath = MusicCoverService.GetOrCreate(item.FilePath);
                        }
                    }
                    catch
                    {
                        // 封面提取失败不应影响该曲目被扫描到。
                    }

                    item.MusicMetadataScanned = true;
                }
            });
        }

        /// <summary>
        /// 批量读取图片的原始像素尺寸，为 LinedFlowLayout 提供宽高比数据
        /// </summary>
        private static async Task ReadImageDimensionsAsync(List<MediaItem> items)
        {
            foreach (var item in items.Where(
                         item => item.PixelWidth <= 0 || item.PixelHeight <= 0))
            {
                try
                {
                    using var fileStream = File.OpenRead(item.FilePath);
                    using var randomAccessStream = fileStream.AsRandomAccessStream();
                    var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                    item.PixelWidth = (int)decoder.PixelWidth;
                    item.PixelHeight = (int)decoder.PixelHeight;
                }
                catch
                {
                    // 无法读取尺寸时使用默认值 1:1
                    item.PixelWidth = 1;
                    item.PixelHeight = 1;
                }
            }
        }

        /// <summary>
        /// 为缺失时长的视频项异步补全时长数据。
        /// 用于已有缓存中 Duration 为 null 的场景（例如首次扫描时未提取时长）。
        /// </summary>
        public static async Task EnrichVideoDurationsAsync(IEnumerable<MediaItem> items)
        {
            var itemsNeedingDuration = items
                .Where(i => !i.Duration.HasValue && i.MediaType == "Video")
                .ToList();

            if (itemsNeedingDuration.Count == 0) return;

            await Parallel.ForEachAsync(
                itemsNeedingDuration,
                new ParallelOptions { MaxDegreeOfParallelism = 3 },
                async (item, _) =>
                {
                    item.Duration = await GetVideoDurationAsync(item.FilePath);
                });
        }

        /// <summary>
        /// 异步获取视频文件的时长。
        /// 先尝试系统 MediaComposition（依赖系统编解码器），失败后回退到内置 FFmpeg。
        /// </summary>
        private static async Task<TimeSpan?> GetVideoDurationAsync(string videoFilePath)
        {
            // 文件不存在时直接返回（缓存中可能残留已删除/移动的文件记录）
            if (!File.Exists(videoFilePath))
                return null;

            // 尝试系统 MediaComposition（快速路径）
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(videoFilePath);
                var clip = await MediaClip.CreateFromFileAsync(file);
                return clip.OriginalDuration;
            }
            catch { }

            // 回退到 FFmpeg（支持 MKV 等更多格式）
            try
            {
                using FileStream fileStream = File.OpenRead(videoFilePath);
                using IRandomAccessStream randomAccessStream =
                    fileStream.AsRandomAccessStream();

                var grabber = await FrameGrabber
                    .CreateFromStreamAsync(randomAccessStream)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(15));

                return grabber.Duration;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 异步获取视频文件的综合元数据（时长、分辨率、帧率）。
        /// 先尝试系统 API（依赖系统编解码器），失败后回退到内置 FFmpeg 抽帧获取分辨率。
        /// </summary>
        private static async Task<(TimeSpan? Duration, int Width, int Height, double? FrameRate)>
            GetVideoMetadataAsync(string videoFilePath)
        {
            if (!File.Exists(videoFilePath))
                return (null, 0, 0, null);

            // 尝试系统 API（快速路径，可获取完整元数据）
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(videoFilePath);
                int width = 0, height = 0;
                TimeSpan? duration = null;
                double? frameRate = null;

                // 通过文件属性获取基本视频信息
                var videoProps = await file.Properties.GetVideoPropertiesAsync();
                width = (int)videoProps.Width;
                height = (int)videoProps.Height;

                // 通过 MediaClip 获取时长
                try
                {
                    var clip = await MediaClip.CreateFromFileAsync(file);
                    duration = clip.OriginalDuration;
                }
                catch { }

                // 通过 MediaEncodingProfile 获取帧率
                try
                {
                    var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
                    if (profile?.Video != null &&
                        profile.Video.FrameRate.Denominator > 0)
                    {
                        frameRate = (double)profile.Video.FrameRate.Numerator /
                                    profile.Video.FrameRate.Denominator;
                    }
                }
                catch { }

                return (duration, width, height, frameRate);
            }
            catch { }

            // 回退到 FFmpeg（支持 MKV 等更多格式）
            TimeSpan? ffDuration = null;
            int ffWidth = 0, ffHeight = 0;
            try
            {
                using FileStream fileStream = File.OpenRead(videoFilePath);
                using IRandomAccessStream randomAccessStream =
                    fileStream.AsRandomAccessStream();

                var grabber = await FrameGrabber
                    .CreateFromStreamAsync(randomAccessStream)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(15));

                ffDuration = grabber.Duration;

                // 不设置 DecodePixelWidth/Height，默认 0 即按原始分辨率解码
                var frame = await grabber
                    .ExtractVideoFrameAsync(TimeSpan.Zero, false)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(15));

                if (frame != null)
                {
                    ffWidth = (int)frame.PixelWidth;
                    ffHeight = (int)frame.PixelHeight;
                }
            }
            catch { }

            return (ffDuration, ffWidth, ffHeight, null);
        }

        /// <summary>
        /// 为缺失分辨率/帧率的已缓存视频项异步补全数据。
        /// 用于已有缓存中 PixelWidth/PixelHeight 或 FrameRate 为空的场景。
        /// </summary>
        public static async Task EnrichVideoDimensionsAsync(IEnumerable<MediaItem> items)
        {
            var itemsNeedingData = items
                .Where(i => i.MediaType == "Video" &&
                            (i.PixelWidth <= 0 || i.PixelHeight <= 0 || !i.FrameRate.HasValue))
                .ToList();

            if (itemsNeedingData.Count == 0) return;

            await Parallel.ForEachAsync(
                itemsNeedingData,
                new ParallelOptions { MaxDegreeOfParallelism = 3 },
                async (item, _) =>
                {
                    var (_, width, height, frameRate) =
                        await GetVideoMetadataAsync(item.FilePath);
                    if (item.PixelWidth <= 0)
                        item.PixelWidth = width;
                    if (item.PixelHeight <= 0)
                        item.PixelHeight = height;
                    if (!item.FrameRate.HasValue)
                        item.FrameRate = frameRate;
                });
        }

        /// <summary>
        /// 为视频文件提取缩略图并保存到缓存目录。
        /// 先尝试系统 MediaComposition（依赖系统编解码器），失败后回退到内置 FFmpeg 抽帧，
        /// 从而在没有安装任何编解码器的机器上也能为 MKV 等格式生成缩略图。
        /// </summary>
        private static async Task<string> ExtractThumbnailAsync(
            string videoFilePath)
        {
            string thumbnailPath;
            try
            {
                EnsureThumbnailCacheDir();
                var hash = GetFileHash(videoFilePath);
                thumbnailPath = Path.Combine(ThumbnailCacheDir, $"{hash}.jpg");

                if (File.Exists(thumbnailPath))
                    return thumbnailPath;
            }
            catch
            {
                return string.Empty;
            }

            if (await TryExtractThumbnailWithMediaCompositionAsync(videoFilePath, thumbnailPath))
                return thumbnailPath;

            if (await TryExtractThumbnailWithFFmpegAsync(videoFilePath, thumbnailPath))
                return thumbnailPath;

            AppLogger.Debug($"无法为视频生成缩略图: {videoFilePath}");
            return string.Empty;
        }

        private static async Task<bool> TryExtractThumbnailWithMediaCompositionAsync(
            string videoFilePath,
            string thumbnailPath)
        {
            try
            {
                StorageFile file =
                    await StorageFile.GetFileFromPathAsync(videoFilePath);
                MediaClip clip = await MediaClip.CreateFromFileAsync(file);
                var composition = new MediaComposition();
                composition.Clips.Add(clip);
                TimeSpan position = clip.OriginalDuration > TimeSpan.FromSeconds(1)
                    ? TimeSpan.FromTicks(Math.Min(
                        clip.OriginalDuration.Ticks / 10,
                        TimeSpan.FromSeconds(10).Ticks))
                    : TimeSpan.Zero;

                using IRandomAccessStreamWithContentType thumbnail =
                    await composition.GetThumbnailAsync(
                        position,
                        320,
                        180,
                        VideoFramePrecision.NearestFrame);
                thumbnail.Seek(0);
                using Stream input = thumbnail.AsStreamForRead();
                using FileStream output = File.Create(thumbnailPath);
                await input.CopyToAsync(output);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> TryExtractThumbnailWithFFmpegAsync(
            string videoFilePath,
            string thumbnailPath)
        {
            FrameGrabber? grabber = null;
            try
            {
                using FileStream fileStream = File.OpenRead(videoFilePath);
                using IRandomAccessStream randomAccessStream =
                    fileStream.AsRandomAccessStream();

                grabber = await FrameGrabber
                    .CreateFromStreamAsync(randomAccessStream)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(30));

                grabber.DecodePixelWidth = 320;
                grabber.DecodePixelHeight = 180;

                TimeSpan position = grabber.Duration > TimeSpan.FromSeconds(1)
                    ? TimeSpan.FromTicks(Math.Min(
                        grabber.Duration.Ticks / 10,
                        TimeSpan.FromSeconds(10).Ticks))
                    : TimeSpan.Zero;

                // exactSeek=false：解码最近的关键帧即可，更快且更稳妥。
                VideoFrame frame = await grabber
                    .ExtractVideoFrameAsync(position, false)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(30));

                using FileStream output = File.Create(thumbnailPath);
                await frame.EncodeAsJpegAsync(output.AsRandomAccessStream());

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"FFmpeg 抽帧缩略图失败: {videoFilePath}, {ex.Message}");
                try
                {
                    if (File.Exists(thumbnailPath) && new FileInfo(thumbnailPath).Length == 0)
                        File.Delete(thumbnailPath);
                }
                catch { }
                return false;
            }
            finally
            {
                (grabber as IDisposable)?.Dispose();
            }
        }

        public static async Task<List<MediaItem>> ScanAllAsync()
        {
            var allItems = new List<MediaItem>();
            var videoPath = GetVideosPath();
            var musicPath = GetMusicPath();
            var picturesPath = GetPicturesPath();

            if (!string.IsNullOrEmpty(videoPath))
                allItems.AddRange(await ScanFolderAsync(videoPath, "Video"));
            if (!string.IsNullOrEmpty(musicPath))
                allItems.AddRange(await ScanFolderAsync(musicPath, "Music"));
            if (!string.IsNullOrEmpty(picturesPath))
                allItems.AddRange(await ScanFolderAsync(picturesPath, "Image"));

            return allItems;
        }

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "Cache");

        private static readonly string ThumbnailCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "Cache", "Thumbnails");

        /// <summary>
        /// 计算文件路径的短哈希（用于缩略图文件名）
        /// </summary>
        private static string GetFileHash(string filePath)
        {
            using var md5 = MD5.Create();
            var info = new FileInfo(filePath);
            string cacheKey =
                $"{filePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(cacheKey));
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLower();
        }

        /// <summary>
        /// 确保缩略图缓存目录存在
        /// </summary>
        private static void EnsureThumbnailCacheDir()
        {
            if (!Directory.Exists(ThumbnailCacheDir))
                Directory.CreateDirectory(ThumbnailCacheDir);
        }

        /// <summary>
        /// 保存媒体项到隔离的类型缓存文件
        /// </summary>
        /// <param name="items">媒体项列表</param>
        /// <param name="mediaType">媒体类型（Video/Image/Music）</param>
        // 各媒体类型的内存快照，避免每次加载都从磁盘反序列化 JSON
        private static readonly object CacheMemoryLock = new();
        private static readonly Dictionary<string, List<MediaItem>> CacheMemory =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 媒体库内存缓存统计（供资源诊断服务输出快照）：
        /// 返回各媒体类型缓存条目数与合计条目数。
        /// </summary>
        public static (int Video, int Image, int Music, int Total) GetCacheStats()
        {
            lock (CacheMemoryLock)
            {
                int video = CacheMemory.TryGetValue("Video", out var v) ? v.Count : 0;
                int image = CacheMemory.TryGetValue("Image", out var i) ? i.Count : 0;
                int music = CacheMemory.TryGetValue("Music", out var m) ? m.Count : 0;
                return (video, image, music, video + image + music);
            }
        }

        public static void SaveToCache(List<MediaItem> items, string mediaType)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var cacheFile = GetCacheFilePath(mediaType);
                var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                });
                File.WriteAllText(cacheFile, json);

                lock (CacheMemoryLock)
                {
                    CacheMemory[mediaType] = new List<MediaItem>(items);
                }

                CacheUpdated?.Invoke(null, mediaType);
            }
            catch { }
        }

        /// <summary>
        /// 从隔离的类型缓存文件加载媒体项
        /// </summary>
        /// <param name="mediaType">媒体类型（Video/Image/Music）</param>
        /// <returns>媒体项列表</returns>
        public static List<MediaItem> LoadFromCache(string mediaType)
        {
            lock (CacheMemoryLock)
            {
                if (CacheMemory.TryGetValue(mediaType, out var snapshot))
                    return new List<MediaItem>(snapshot);
            }

            try
            {
                var cacheFile = GetCacheFilePath(mediaType);
                if (File.Exists(cacheFile))
                {
                    var json = File.ReadAllText(cacheFile);
                    var items = JsonSerializer.Deserialize<List<MediaItem>>(json, new JsonSerializerOptions
                    {
                        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                    }) ?? new List<MediaItem>();

                    lock (CacheMemoryLock)
                    {
                        CacheMemory[mediaType] = new List<MediaItem>(items);
                    }

                    return items;
                }
            }
            catch { }
            return new List<MediaItem>();
        }

        /// <summary>
        /// 根据媒体类型获取对应的缓存文件路径
        /// </summary>
        private static string GetCacheFilePath(string mediaType)
        {
            return Path.Combine(CacheDir, $"cache_{mediaType}.json");
        }

        public static async Task<List<MediaItem>> RefreshAsync()
        {
            AppLogger.Info("刷新媒体库缓存");

            var paths = new List<string>();
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SightoHear", "settings.json");

            if (File.Exists(settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(settingsPath);
                    var node = JsonNode.Parse(json);
                    var pathsArray = node?["VideoLibraryPaths"]?.AsArray();
                    if (pathsArray != null)
                    {
                        foreach (var item in pathsArray)
                        {
                            var path = item?.GetValue<string>();
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                paths.Add(path);
                        }
                    }
                }
                catch { }
            }

            // 不再自动填充默认路径：如果用户已清空列表，则完全保持空状态
            // 仅在首次安装时由 InitializeDefaultLibrarySettings() 写入默认路径

            var allItems = new List<MediaItem>();
            foreach (var path in paths)
            {
                var items = await ScanFolderAsync(path, "Video", SearchOption.AllDirectories);
                allItems.AddRange(items);
            }

            var uniqueItems = allItems.GroupBy(x => x.FilePath).Select(g => g.First()).ToList();
            SaveToCache(uniqueItems, "Video");
            AppLogger.Info($"刷新完成: 缓存{uniqueItems.Count}个视频文件");
            return uniqueItems;
        }
    }
}
