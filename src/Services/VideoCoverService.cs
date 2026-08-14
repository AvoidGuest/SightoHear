using SightoHear.Helpers;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FFmpegInteropX;
using Windows.Storage;

namespace SightoHear.Services
{
    /// <summary>
    /// 视频封面服务：为视频文件提取高清封面帧。
    /// 高清封面用于主页"上次打开"大卡片等需要高分辨率的场景，
    /// 与扫描时生成的 320x180 小缩略图（ThumbnailPath）分开管理。
    /// </summary>
    public static class VideoCoverService
    {
        private const int HeroCoverWidth = 1920;
        private const int HeroCoverHeight = 1080;
        private const int ExtractTimeoutSeconds = 30;

        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear",
            "Cache",
            "VideoCovers");

        private static readonly ConcurrentDictionary<string, Lazy<string>> InFlight =
            new(StringComparer.OrdinalIgnoreCase);

        // 缓存已解析的封面路径：避免每次都重新提取
        private static readonly ConcurrentDictionary<string, string> ResolvedCoverPaths =
            new(StringComparer.OrdinalIgnoreCase);

        static VideoCoverService()
        {
            try { Directory.CreateDirectory(CacheDirectory); } catch { }
        }

        /// <summary>
        /// 获取视频的高清封面路径（用于主页大卡片等场景）。
        /// 优先从缓存读取，不存在时提取视频帧并缓存。
        /// </summary>
        /// <param name="videoFilePath">视频文件路径。</param>
        /// <returns>高清封面文件路径，无封面时返回空字符串。</returns>
        public static string GetOrCreateOriginal(string videoFilePath)
        {
            string? cacheKey = BuildCacheKey(videoFilePath, "hero");
            if (TryGetResolvedPath(cacheKey, out string cached))
                return cached;

            // 尝试提取高清帧
            string hash = ComputeFileHash(videoFilePath);
            string outputPath = Path.Combine(CacheDirectory, $"{hash}_hero.jpg");
            if (File.Exists(outputPath))
                return CacheResolvedPath(cacheKey, outputPath);

            var pending = InFlight.GetOrAdd(
                hash,
                _ => new Lazy<string>(() => ExtractHeroFrameAsync(videoFilePath, outputPath).GetAwaiter().GetResult(), true));

            try
            {
                string coverPath = pending.Value;
                return CacheResolvedPath(cacheKey, coverPath);
            }
            finally
            {
                InFlight.TryRemove(hash, out _);
            }
        }

        /// <summary>
        /// 异步获取视频的高清封面路径。
        /// </summary>
        public static Task<string> GetOrCreateOriginalAsync(string videoFilePath)
        {
            return Task.Run(() => GetOrCreateOriginal(videoFilePath));
        }

        /// <summary>
        /// 快速检查：高清封面是否已解析过，返回缓存的封面路径。
        /// </summary>
        public static string? TryGetCachedPath(string videoFilePath)
        {
            string? key = BuildCacheKey(videoFilePath, "hero");
            if (key == null) return null;
            if (TryGetResolvedPath(key, out string coverPath))
                return coverPath;
            return null;
        }

        // ══════════════════════════════════════════════════════
        //  内部方法
        // ══════════════════════════════════════════════════════

        private static async Task<string> ExtractHeroFrameAsync(
            string videoFilePath, string outputPath)
        {
            if (!File.Exists(videoFilePath))
                return string.Empty;

            try
            {
                // 先尝试使用 FFmpegInteropX 提取高清帧
                return await ExtractWithFFmpegAsync(videoFilePath, outputPath);
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"VideoCoverService: FFmpeg 提取高清封面失败: {videoFilePath}, {ex.Message}");
                return string.Empty;
            }
        }

        private static async Task<string> ExtractWithFFmpegAsync(
            string videoFilePath, string outputPath)
        {
            using FileStream fileStream = File.OpenRead(videoFilePath);
            using var randomAccessStream = fileStream.AsRandomAccessStream();

            var grabber = await FrameGrabber
                .CreateFromStreamAsync(randomAccessStream)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(ExtractTimeoutSeconds));

            // 设置高清解码尺寸
            grabber.DecodePixelWidth = HeroCoverWidth;
            grabber.DecodePixelHeight = HeroCoverHeight;

            // 在视频的 10% 位置提取帧（通常是内容画面，避免片头黑帧）
            TimeSpan position = grabber.Duration > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromTicks(Math.Min(
                    grabber.Duration.Ticks / 10,
                    TimeSpan.FromSeconds(10).Ticks))
                : TimeSpan.Zero;

            // exactSeek=false：解码最近的关键帧即可，更快且更稳妥
            VideoFrame frame = await grabber
                .ExtractVideoFrameAsync(position, false)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(ExtractTimeoutSeconds));

            using FileStream output = File.Create(outputPath);
            await frame.EncodeAsJpegAsync(output.AsRandomAccessStream());

            return File.Exists(outputPath) ? outputPath : string.Empty;
        }

        private static string? BuildCacheKey(string videoFilePath, string kind)
        {
            try
            {
                var info = new FileInfo(videoFilePath);
                if (!info.Exists)
                    return null;
                return $"{videoFilePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{kind}";
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetResolvedPath(string? key, out string coverPath)
        {
            coverPath = string.Empty;
            if (key is null)
                return false;

            if (!ResolvedCoverPaths.TryGetValue(key, out string? cached))
                return false;

            if (cached.Length == 0)
            {
                coverPath = string.Empty;
                return true;
            }

            if (File.Exists(cached))
            {
                coverPath = cached;
                return true;
            }

            // 缓存文件已被删除，剔除记录以便下次重新生成
            ResolvedCoverPaths.TryRemove(key, out _);
            return false;
        }

        private static string CacheResolvedPath(string? key, string coverPath)
        {
            if (key is not null)
                ResolvedCoverPaths[key] = coverPath ?? string.Empty;
            return coverPath ?? string.Empty;
        }

        private static string ComputeFileHash(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                string key = $"{filePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch
            {
                byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(filePath));
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
        }
    }
}
