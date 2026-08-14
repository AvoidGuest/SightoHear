using Microsoft.UI.Xaml.Media.Imaging;
using SightoHear.Helpers;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SightoHear.Services
{
    /// <summary>
    /// 缩略图缓存服务：内存 LRU 缓存 + 磁盘持久缓存。
    /// 视频缩略图已在扫描时由 MediaScanner 预先提取到 Cache\Thumbnails\；
    /// 音乐封面由 MusicCoverService 提取到 Cache\MusicCovers\；
    /// 图片（图库）缩略图由本服务按需生成并缓存到 Cache\ImageThumbnails\。
    /// </summary>
    public static class ImageThumbnailService
    {
        // ── 常量 ──
        private const int DecodePixelHeight = 256;
        // ★ 上限从 2048 降至 512：每条缓存强引用一张 BitmapImage（256px 解码位图），
        //   浏览多个页面后若无限累积会显著抬升内存、加剧 GC，进而拖慢 Win2D 渲染。
        private const int MaxMemoryCache = 512;
        private const int DefaultThumbnailSize = 256;
        private const int ThumbnailJpegQuality = 82;

        // ── 磁盘缓存目录（图片缩略图按需生成） ──
        private static readonly string ImageThumbnailCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "Cache", "ImageThumbnails");

        // ── 内存 LRU 缓存 ──
        private sealed record CacheEntry(BitmapImage Image, LinkedListNode<string> Node);
        private static readonly Dictionary<string, CacheEntry> MemoryCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> AccessOrder = new();
        private static readonly object CacheLock = new();

        // ── 支持的图片扩展名（跳过非图片文件，避免 SKBitmap.Decode 无效调用） ──
        private static readonly HashSet<string> SupportedImageExtensions = new(
            StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif", ".ico", ".wbmp" };

        // ── 磁盘生成去重 ──
        private static readonly HashSet<string> GenerationInFlight =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object GenLock = new();

        static ImageThumbnailService()
        {
            try { Directory.CreateDirectory(ImageThumbnailCacheDir); } catch { }
        }

        // ══════════════════════════════════════════════════════
        //  公开 API
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// 从内存缓存获取 BitmapImage；未命中则通过 UriSource 从磁盘文件创建（XAML 后台解码）。
        /// 适用于已确认的磁盘缩略图（视频缩略图、音乐封面等）。
        /// </summary>
        public static BitmapImage? GetOrCreate(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            // 尝试内存缓存命中
            BitmapImage? cached = TryGetFromMemoryCache(filePath);
            if (cached != null)
                return cached;

            // 创建 BitmapImage（UriSource 方式 = 后台异步解码，不阻塞 UI）
            try
            {
                var bitmap = new BitmapImage
                {
                    DecodePixelHeight = DecodePixelHeight,
                    DecodePixelType = DecodePixelType.Physical
                };
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                AddToMemoryCache(filePath, bitmap);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 异步加载缩略图：内存 → 磁盘缓存 → 从原图按需生成。
        /// 用于图库页需要从原始大图缩放的场景。
        /// </summary>
        public static async Task<BitmapImage?> LoadAsync(
            string sourceFilePath,
            uint requestedSize,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return null;

            // 1. 内存缓存
            BitmapImage? cached = TryGetFromMemoryCache(sourceFilePath);
            if (cached != null)
                return cached;

            // 2. 确保磁盘缩略图已生成（后台线程执行 SkiaSharp 缩放，不会阻塞 UI）
            //    注意：不能使用 ConfigureAwait(false)，后续需要 UI 线程创建 BitmapImage
            string? thumbPath = await GetOrCreateDiskThumbnailAsync(
                sourceFilePath, requestedSize);

            if (cancellationToken.IsCancellationRequested)
                return null;

            // 3. 磁盘缓存存在 → 从缓存的缩略图文件创建 BitmapImage
            if (thumbPath != null)
            {
                cached = TryGetFromMemoryCache(thumbPath)
                      ?? TryGetFromMemoryCache(sourceFilePath);
                if (cached != null)
                    return cached;

                try
                {
                    var bitmap = new BitmapImage
                    {
                        DecodePixelHeight = (int)requestedSize,
                        DecodePixelType = DecodePixelType.Physical
                    };
                    bitmap.UriSource = new Uri(thumbPath, UriKind.Absolute);
                    AddToMemoryCache(thumbPath, bitmap);
                    AddToMemoryCache(sourceFilePath, bitmap);
                    return bitmap;
                }
                catch { }
            }

            // 4. ★ 降级回退：SkiaSharp 无法解码时（如 HEIC），
            //    直接通过 WIC（Windows Imaging Component）从原文件解码。
            //    DecodePixelHeight 确保解码后缩放到合适尺寸。
            cached = TryGetFromMemoryCache(sourceFilePath);
            if (cached != null)
                return cached;

            try
            {
                var fallback = new BitmapImage
                {
                    DecodePixelHeight = (int)requestedSize,
                    DecodePixelType = DecodePixelType.Physical
                };
                fallback.UriSource = new Uri(sourceFilePath, UriKind.Absolute);
                AddToMemoryCache(sourceFilePath, fallback);
                return fallback;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 获取磁盘缓存缩略图路径，不存在则自动生成（SkiaSharp 缩放）。
        /// </summary>
        public static async Task<string?> GetOrCreateDiskThumbnailAsync(
            string sourceFilePath, uint maxSize = DefaultThumbnailSize)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return null;

            string cacheKey = ComputeFileHash(sourceFilePath);
            string destPath = Path.Combine(
                ImageThumbnailCacheDir, $"{cacheKey}_{maxSize}.jpg");

            if (File.Exists(destPath))
                return destPath;

            // 去重：避免多个线程同时生成同一文件
            bool shouldGenerate = false;
            lock (GenLock)
            {
                if (!GenerationInFlight.Contains(destPath))
                {
                    GenerationInFlight.Add(destPath);
                    shouldGenerate = true;
                }
            }

            if (!shouldGenerate)
            {
                // 其他线程正在生成，等待完成
                for (int i = 0; i < 100; i++)
                {
                    // 保留调用者的同步上下文（UI 线程），不使用 ConfigureAwait(false)
                    await Task.Delay(50);
                    if (File.Exists(destPath))
                        return destPath;
                }
                return null;
            }

            try
            {
                // ★ 不使用 ConfigureAwait(false)：后续 BitmapImage 创建必须在调用者线程（UI 线程）
                await Task.Run(() => GenerateThumbnailToDisk(
                    sourceFilePath, destPath, (int)maxSize));
                return File.Exists(destPath) ? destPath : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                lock (GenLock)
                    GenerationInFlight.Remove(destPath);
            }
        }

        /// <summary>
        /// 内存 LRU 缓存当前条目数（供资源诊断服务统计内存去向）。
        /// </summary>
        public static int GetCacheCount()
        {
            lock (CacheLock) return MemoryCache.Count;
        }

        /// <summary>
        /// 内存缓存统计：当前条目数与容量上限（供资源诊断服务输出快照）。
        /// </summary>
        public static (int Count, int Capacity) GetCacheStats()
        {
            lock (CacheLock) return (MemoryCache.Count, MaxMemoryCache);
        }

        /// <summary>
        /// 源文件是否已在内存缓存中（可瞬间显示）。
        /// </summary>
        public static bool IsInMemoryCache(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            lock (CacheLock)
                return MemoryCache.ContainsKey(filePath);
        }

        /// <summary>
        /// 磁盘缩略图是否已存在。
        /// </summary>
        public static bool DiskCacheExists(string sourceFilePath, uint maxSize = DefaultThumbnailSize)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return false;
            string path = GetDiskCachePath(sourceFilePath, maxSize);
            return File.Exists(path);
        }

        /// <summary>
        /// 从内存缓存移除指定项。
        /// </summary>
        public static void Remove(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            lock (CacheLock)
            {
                if (MemoryCache.TryGetValue(filePath, out var entry))
                {
                    AccessOrder.Remove(entry.Node);
                    MemoryCache.Remove(filePath);
                }
            }
        }

        /// <summary>
        /// 清空所有缓存。
        /// </summary>
        public static void Clear()
        {
            int count;
            lock (CacheLock)
            {
                count = MemoryCache.Count;
                MemoryCache.Clear();
                AccessOrder.Clear();
            }
            // 诊断日志：记录清空数量（不携带堆栈，避免首次加载 System.Diagnostics.StackTrace 失败导致崩溃）
            AppLogger.Debug($"[ThumbCache] Clear() 被调用！清除了 {count} 条缓存");
        }

        /// <summary>
        /// 磁盘缩略图缓存文件数量（供设置页显示占用信息）。
        /// </summary>
        public static int GetDiskCacheCount()
        {
            try
            {
                if (!Directory.Exists(ImageThumbnailCacheDir)) return 0;
                return Directory.GetFiles(ImageThumbnailCacheDir, "*.jpg").Length;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 清除磁盘缩略图缓存（同时清空内存缓存）。供图库设置页「清理缩略图缓存」使用。
        /// </summary>
        public static void ClearDiskCache()
        {
            // 先清内存缓存，避免后续页面继续引用已删除的磁盘文件
            Clear();
            try
            {
                if (!Directory.Exists(ImageThumbnailCacheDir)) return;
                foreach (var file in Directory.GetFiles(ImageThumbnailCacheDir, "*.jpg"))
                {
                    try { File.Delete(file); } catch { }
                }
                AppLogger.Info($"[ThumbCache] 磁盘缩略图缓存已清理");
            }
            catch { }
        }

        /// <summary>
        /// 按最近使用顺序裁剪内存缓存，保留热数据，避免切换播放器时一次性清空全部图片。
        /// </summary>
        public static void TrimMemoryCache(int targetCount)
        {
            targetCount = Math.Clamp(targetCount, 64, MaxMemoryCache);
            int removed = 0;

            lock (CacheLock)
            {
                while (MemoryCache.Count > targetCount && AccessOrder.First != null)
                {
                    string oldest = AccessOrder.First.Value;
                    AccessOrder.RemoveFirst();
                    if (MemoryCache.Remove(oldest))
                        removed++;
                }
            }

            if (removed > 0)
                AppLogger.Debug($"[ThumbCache] 按需裁剪内存缓存，移除 {removed} 条，保留 {targetCount} 条");
        }

        /// <summary>
        /// 删除源文件对应的磁盘缩略图。
        /// </summary>
        public static void DeleteDiskCache(string sourceFilePath, uint maxSize = DefaultThumbnailSize)
        {
            string path = GetDiskCachePath(sourceFilePath, maxSize);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ══════════════════════════════════════════════════════
        //  内部方法
        // ══════════════════════════════════════════════════════

        /// <summary> 尝试从 LRU 内存缓存获取，命中时移至链表尾部（保活）。 </summary>
        private static BitmapImage? TryGetFromMemoryCache(string filePath)
        {
            lock (CacheLock)
            {
                if (MemoryCache.TryGetValue(filePath, out var entry))
                {
                    // 移至链表尾部 = 最近使用
                    AccessOrder.Remove(entry.Node);
                    var newNode = AccessOrder.AddLast(filePath);
                    MemoryCache[filePath] = entry with { Node = newNode };
                    return entry.Image;
                }
            }
            return null;
        }

        /// <summary> 加入内存缓存，达到上限时淘汰最久未使用的。 </summary>
        private static void AddToMemoryCache(string filePath, BitmapImage bitmap)
        {
            lock (CacheLock)
            {
                // 已存在：更新并移到尾部
                if (MemoryCache.TryGetValue(filePath, out var existing))
                {
                    AccessOrder.Remove(existing.Node);
                    var newNode = AccessOrder.AddLast(filePath);
                    MemoryCache[filePath] = existing with { Image = bitmap, Node = newNode };
                    return;
                }

                // 新增
                var node = AccessOrder.AddLast(filePath);
                MemoryCache[filePath] = new CacheEntry(bitmap, node);

                // LRU 淘汰
                while (MemoryCache.Count > MaxMemoryCache && AccessOrder.First != null)
                {
                    string oldest = AccessOrder.First.Value;
                    AccessOrder.RemoveFirst();
                    MemoryCache.Remove(oldest);
                }
            }
        }

        /// <summary> 计算磁盘缓存路径（公开供页面查询持久化缩略图）。 </summary>
        public static string GetDiskCachePath(string sourceFilePath, uint maxSize)
        {
            string key = ComputeFileHash(sourceFilePath);
            return Path.Combine(ImageThumbnailCacheDir, $"{key}_{maxSize}.jpg");
        }

        /// <summary>
        /// SkiaSharp 缩放到 maxSize 以内，保存为 JPEG。
        /// </summary>
        private static void GenerateThumbnailToDisk(
            string sourcePath, string destPath, int maxSize)
        {
            // ★ 跳过非图片文件，避免 SKBitmap.Decode 无效调用
            string ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(ext) || !SupportedImageExtensions.Contains(ext))
                return;

            try
            {
                // ★ 先完整读入内存再解码，避免与 WIC (BitmapImage.UriSource) 的文件锁冲突
                //   WIC 可能短暂锁定文件，使用重试等待锁释放
                byte[]? imageBytes = ReadFileWithRetry(sourcePath);
                if (imageBytes == null)
                {
                    AppLogger.Warning($"ImageThumbnailService: 无法读取源文件（被锁定）-> {sourcePath}");
                    return;
                }

                using SKBitmap? source = SKBitmap.Decode(imageBytes);
                if (source == null)
                {
                    AppLogger.Debug($"ImageThumbnailService: SKBitmap.Decode 返回空 -> 无法生成缩略图: {sourcePath}");
                    return;
                }

                double scale = Math.Min(1.0,
                    (double)maxSize / Math.Max(source.Width, source.Height));
                int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                int height = Math.Max(1, (int)Math.Round(source.Height * scale));

                // 使用 SKSurface 创建独立像素的图像，确保编码稳定性
                using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height));
                {
                    var canvas = surface.Canvas;
                    canvas.Clear(new SKColor(32, 32, 32));
                    using var paint = new SKPaint { IsAntialias = true };
#pragma warning disable CS0618
                    paint.FilterQuality = SKFilterQuality.High;
#pragma warning restore CS0618
                    canvas.DrawBitmap(source, new SKRect(0, 0, width, height), paint);
                }

                using SKImage image = surface.Snapshot();
                if (image == null)
                {
                    AppLogger.Debug($"ImageThumbnailService: surface.Snapshot 返回空 -> 无法生成缩略图: {sourcePath}");
                    return;
                }

                // 尝试 JPEG 编码；失败时降级为 PNG（对部分图片格式更稳定）
                SKData? encoded = image.Encode(
                    SKEncodedImageFormat.Jpeg, ThumbnailJpegQuality);
                string finalPath = destPath;
                if (encoded == null)
                {
                    AppLogger.Debug($"ImageThumbnailService: JPEG 编码失败，尝试 PNG 降级: {sourcePath}");
                    encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                    if (encoded == null)
                    {
                        AppLogger.Debug($"ImageThumbnailService: PNG 编码也失败 -> 无法生成缩略图: {sourcePath}");
                        return;
                    }
                    finalPath = Path.ChangeExtension(destPath, ".png");
                }

                // 直接写入最终路径——并发去重由 SemaphoreSlim 保证，不需要原子移动
                byte[] encodedBytes = encoded.ToArray();
                File.WriteAllBytes(finalPath, encodedBytes);
            }
            catch (Exception ex)
            {
                // 界面有降级策略（WIC 直接加载原文件），缩略图生成失败不致命，降为 Debug 级别避免日志污染
                AppLogger.Debug($"ImageThumbnailService: 生成缩略图异常 [{ex.GetType().Name}]: {ex.Message} -> {sourcePath}");
            }
        }

        /// <summary> 带重试的文件读取：允许与其他进程（如 Explorer）共享读取，避免文件锁冲突。 </summary>
        private static byte[]? ReadFileWithRetry(string filePath)
        {
            // 重试策略：先快速重试3次（200ms间隔），然后较慢重试（500ms间隔）
            int[] delays = { 200, 200, 200, 500, 500, 500, 1000, 1000, 1000, 2000 };
            for (int retry = 0; retry < delays.Length; retry++)
            {
                try
                {
                    // ★ 使用 FileShare.ReadWrite 替代 File.ReadAllBytes（内部用 FileShare.Read），
                    //   允许 Explorer 等进程同时读取/写入文件，不冲突。
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    return ms.ToArray();
                }
                catch (IOException)
                {
                    if (retry >= delays.Length - 1)
                        return null;
                    Thread.Sleep(delays[retry]);
                }
            }
            return null;
        }

        /// <summary> 文件内容指纹：路径 + 大小 + 修改时间 => MD5。 </summary>
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
