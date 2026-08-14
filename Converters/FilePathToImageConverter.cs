using SightoHear.Services;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;

namespace SightoHear
{
    public sealed partial class FilePathToImageConverter : IValueConverter
    {
        // ★ 小型 BitmapImage 复用缓存：避免容器虚拟化回收时重复创建 BitmapImage 导致 GC 压力。
        //   容器被回收后，旧的 BitmapImage 失去引用，由 GC 回收；
        //   而新数据绑定时又创建新的 BitmapImage。
        //   此缓存以 WeakReference 持有最近使用的 BitmapImage，优先复用而非新建。
        private static readonly Dictionary<string, WeakReference<BitmapImage>> ImageCache =
            new(StringComparer.OrdinalIgnoreCase);
        private const int MaxCacheSize = 4096;

        /// <summary>转换器弱引用缓存条目数（供资源诊断服务输出快照）。</summary>
        public static int GetCacheCount()
        {
            lock (ImageCache) return ImageCache.Count;
        }

        /// <summary>
        /// 从缓存获取或创建 BitmapImage。优先走 ImageThumbnailService 内存缓存，
        /// 未命中则创建新 BitmapImage 并缓存，避免重复分配。
        /// </summary>
        public object? Convert(
            object value,
            Type targetType,
            object parameter,
            string language)
        {
            if (value is not string filePath || string.IsNullOrWhiteSpace(filePath))
                return null;

            // ★ 先查小缓存：是否刚从 ImageThumbnailService 创建过 → 复用
            if (TryGetFromImageCache(filePath, out var cached))
                return cached;

            BitmapImage? bitmap = null;

            if (IsMusicFile(filePath))
            {
                // 快速路径：仅检查内存缓存，不触发文件 I/O
                string? cachedPath = MusicCoverService.TryGetCachedPath(filePath);
                if (cachedPath != null)
                {
                    // 已缓存：cachedPath 空字符串表示"无封面"，非空才是有效路径
                    if (string.IsNullOrWhiteSpace(cachedPath))
                    {
                        string? originalCached = MusicCoverService.TryGetCachedOriginalPath(filePath);
                        if (string.IsNullOrWhiteSpace(originalCached))
                            return null;
                        bitmap = ImageThumbnailService.GetOrCreate(originalCached);
                    }
                    else
                    {
                        bitmap = ImageThumbnailService.GetOrCreate(cachedPath);
                    }
                }

                if (bitmap == null)
                {
                    // 未缓存：走完整提取（同步，仅首次）
                    string imagePath = MusicCoverService.GetOrCreate(filePath);
                    if (string.IsNullOrWhiteSpace(imagePath))
                    {
                        imagePath = MusicCoverService.GetOrCreateOriginal(filePath);
                        if (string.IsNullOrWhiteSpace(imagePath))
                            return null;
                    }
                    bitmap = ImageThumbnailService.GetOrCreate(imagePath);
                }
            }
            else
            {
                bitmap = ImageThumbnailService.GetOrCreate(filePath);
            }

            // ★ 缓存结果，下次直接复用
            if (bitmap != null)
                AddToImageCache(filePath, bitmap);

            return bitmap;
        }

        // ════════════════════════════════════════════════
        //  BitmapImage 复用缓存（减少 GC 压力）
        // ════════════════════════════════════════════════

        private static bool TryGetFromImageCache(string filePath, out BitmapImage? bitmap)
        {
            lock (ImageCache)
            {
                if (ImageCache.TryGetValue(filePath, out var weakRef) &&
                    weakRef.TryGetTarget(out bitmap!))
                {
                    return true;
                }
                ImageCache.Remove(filePath); // 清理失效引用
            }
            bitmap = null;
            return false;
        }

        private static void AddToImageCache(string filePath, BitmapImage bitmap)
        {
            lock (ImageCache)
            {
                // 超过上限时清除全部（这是一个小型热缓存，重建成本低）
                if (ImageCache.Count >= MaxCacheSize)
                    ImageCache.Clear();

                ImageCache[filePath] = new WeakReference<BitmapImage>(bitmap);
            }
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            string language)
        {
            throw new NotSupportedException();
        }

        private static bool IsMusicFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension is ".mp3" or ".flac" or ".wav" or ".aac" or ".m4a" or ".ogg" or ".wma" or ".opus";
        }
    }
}
