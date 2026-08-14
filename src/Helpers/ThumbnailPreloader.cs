using SightoHear.Models;
using SightoHear.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 后台缩略图预加载器。
    /// 职责单一：遍历媒体项列表，逐个将缩略图填入 ImageThumbnailService 内存缓存。
    /// 不操作任何 UI 控件（不设置 Image.Source），与 ThumbnailLoadQueue 互补。
    /// 
    /// 预加载器在后台线程上以阶梯速率（intervalMs 间隔）逐张加载，
    /// 确保不阻塞 UI。页面卸载时取消。
    /// </summary>
    public sealed class ThumbnailPreloader : IDisposable
    {
        private readonly int _intervalMs;
        private readonly uint _thumbnailSize;
        private CancellationTokenSource _cts = new();
        private Task? _preloadTask;
        private bool _disposed;

        /// <summary>当前预加载状态。</summary>
        public bool IsRunning => _preloadTask != null && !_preloadTask.IsCompleted;

        public ThumbnailPreloader(int intervalMs = 0, uint thumbnailSize = 260)
        {
            _intervalMs = intervalMs;
            _thumbnailSize = thumbnailSize;
        }

        /// <summary>
        /// 启动预加载。如果之前有正在运行的预加载任务，先取消。
        /// </summary>
        public void Start(IReadOnlyList<MediaItem> items)
        {
            Cancel();

            _cts.Dispose();
            _cts = new CancellationTokenSource();
            _preloadTask = PreloadLoopAsync(items, _cts.Token);
        }

        /// <summary>取消正在进行的预加载。</summary>
        public void Cancel()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cancel();
            _cts.Dispose();
        }

        /// <summary>
        /// 后台预加载循环：遍历所有项，跳过已在内存缓存中的，
        /// 对其余项依次调用 ImageThumbnailService 填充缓存。
        /// </summary>
        private async Task PreloadLoopAsync(
            IReadOnlyList<MediaItem> items,
            CancellationToken ct)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                // 确定加载路径：优先使用已提取的缩略图，否则用原始文件路径
                string loadPath = !string.IsNullOrEmpty(item.ThumbnailPath) &&
                                  File.Exists(item.ThumbnailPath)
                    ? item.ThumbnailPath
                    : item.FilePath;

                if (string.IsNullOrEmpty(loadPath) || !File.Exists(loadPath))
                    continue;

                // 已在内存缓存中 → 跳过
                if (ImageThumbnailService.IsInMemoryCache(loadPath))
                    continue;

                try
                {
                    // ★ 预加载器运行在后台线程，不能直接调用 GetOrCreate / LoadAsync
                    //   （它们内部会创建 BitmapImage，DependencyObject 必须在 UI 线程创建）。
                    //    预加载器的职责是预热磁盘缓存（SkiaSharp 生成缩略图文件），
                    //    让 UI 线程上的 ThumbnailLoadQueue 能瞬间从磁盘缓存加载。
                    // 
                    // 小文件（视频缩略图/音乐封面）：已经是盘上的缓存文件，无需处理。
                    // 大图原文件：调用 GetOrCreateDiskThumbnailAsync 生成磁盘缩略图。
                    if (!IsSmallCachedFile(loadPath))
                    {
                        await ImageThumbnailService.GetOrCreateDiskThumbnailAsync(
                            loadPath, _thumbnailSize);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return; // 正常取消，退出循环
                }
                catch
                {
                    // 单个文件失败不影响后续
                }

                // 阶梯间隔（intervalMs <= 0 时跳过，后台线程可全速运行）
                if (_intervalMs > 0)
                {
                    try
                    {
                        await Task.Delay(_intervalMs, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>判断文件是否为已准备就绪的小缩略图（视频提取 / 音乐封面）。</summary>
        private static bool IsSmallCachedFile(string filePath, int maxSizeKB = 512)
        {
            try
            {
                var info = new FileInfo(filePath);
                return info.Exists && info.Length < maxSizeKB * 1024L;
            }
            catch
            {
                return false;
            }
        }
    }
}
