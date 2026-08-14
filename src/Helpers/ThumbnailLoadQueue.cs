using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SightoHear.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 阶梯式缩略图加载队列。
    /// 每张缩略图之间间隔 intervalMs，从上到下逐个加载，避免同时解码导致 UI 卡顿。
    /// 加载链路：内存缓存（瞬间）→ 磁盘缓存 → 从原图按需生成。
    ///
    /// 内部使用 Task.Delay 循环（而非 DispatcherQueueTimer），
    /// 避免 async void 导致的时序不可预测和"停滞"假象。
    /// </summary>
    public sealed class ThumbnailLoadQueue : IDisposable
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly int _intervalMs;
        private readonly Queue<LoadItem> _pending = new();
        private readonly Dictionary<Image, int> _imageGens = new();
        private readonly SemaphoreSlim _signal = new(0);
        private CancellationTokenSource _cts = new();
        private Task? _loopTask;
        private bool _disposed;

        public ThumbnailLoadQueue(int intervalMs = 80)
        {
            _intervalMs = intervalMs;
            _dispatcher = DispatcherQueue.GetForCurrentThread();
        }

        /// <summary>将一张缩略图加入加载队列。</summary>
        public void Enqueue(Image image, string filePath, uint decodeSize)
        {
            if (_disposed) return;

            // 每次为同一 Image 对象入队时递增代次，以便加载时判断控件是否已被回收
            int gen = _imageGens.TryGetValue(image, out int g) ? g + 1 : 1;
            _imageGens[image] = gen;
            _pending.Enqueue(new LoadItem(image, filePath, decodeSize, gen));
            _signal.Release();
            EnsureLoopStarted();
        }

        /// <summary>清空所有待加载项，停止循环。</summary>
        public void Clear()
        {
            // 停循环：发出取消信号
            _cts.Cancel();
            lock (_pending)
            {
                _pending.Clear();
            }
            _imageGens.Clear();

            // 消费掉信号量中的残余许可，避免下一轮入队错误触发旧循环
            try { while (_signal.CurrentCount > 0) _signal.Wait(0); } catch { }

            // 重置取消令牌（下一轮 Enqueue → EnsureLoopStarted 会用新的）
            _loopTask = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
            _signal.Dispose();
        }



        // ── 处理循环 ──

        private void EnsureLoopStarted()
        {
            if (_loopTask == null || _loopTask.IsCompleted)
            {
                // 如果上一轮的 CTS 已被取消（Clear 时），创建新的
                try { _ = _cts.Token; }
                catch (ObjectDisposedException)
                {
                    _cts.Dispose();
                    _cts = new CancellationTokenSource();
                }
                if (_cts.IsCancellationRequested)
                {
                    _cts.Dispose();
                    _cts = new CancellationTokenSource();
                }

                _loopTask = ProcessLoopAsync();
            }
        }

        private async Task ProcessLoopAsync()
        {
            while (!_disposed)
            {
                try
                {
                    await _signal.WaitAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return; // 被 Clear 或 Dispose 取消
                }
                if (_disposed) return;

                // 队列中可能有多项，一次取一个处理
                while (!_disposed)
                {
                    LoadItem? item;
                    lock (_pending)
                    {
                        if (_pending.Count == 0) break;
                        item = _pending.Dequeue();
                    }

                    // ★ 必须通过 UI 线程执行 LoadOneAsync：
                    //   BitmapImage 创建、Image.Source/Opacity 设置都是 UI 操作。
                    //   LoadAsync 内部的 await 链依赖初始上下文回到 UI 线程。
                    try
                    {
                        await RunOnUiThreadAsync(() => LoadOneAsyncCore(item));
                    }
                    catch
                    {
                        // 单个缩略图加载失败不应让整个队列崩溃；
                        // 继续处理队列中的后续项。
                    }

                    if (_disposed) return;

                    // 每项之间间隔 intervalMs
                    try
                    {
                        await Task.Delay(_intervalMs, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>将异步操作发派到 UI 线程执行，并等待完成。</summary>
        private async Task RunOnUiThreadAsync(Func<Task> func)
        {
            // 已在 UI 线程 → 直接执行
            if (_dispatcher.HasThreadAccess)
            {
                await func();
                return;
            }

            var tcs = new TaskCompletionSource();
            if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await func();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
            {
                tcs.SetException(new InvalidOperationException("发派到 UI 线程失败"));
            }
            await tcs.Task;
        }

        /// <summary>在 UI 线程上执行的核心加载逻辑。</summary>
        private async Task LoadOneAsyncCore(LoadItem item)
        {
            // 检查控件是否已被回收给其他项（代次不匹配）
            if (!_imageGens.TryGetValue(item.Image, out int currentGen) ||
                currentGen != item.Generation)
                return;

            // 1. 内存缓存命中 → 瞬间返回
            if (ImageThumbnailService.IsInMemoryCache(item.FilePath))
            {
                var bitmap = ImageThumbnailService.GetOrCreate(item.FilePath);
                if (bitmap != null && IsStillValid(item))
                    ApplyBitmap(item.Image, bitmap);
                return;
            }

            // 2. ★ 快速路径：文件已是盘上准备好的小缩略图文件（< 512KB）
            //    视频提取的缩略图、音乐封面等直接 UriSource 后台解码即可，
            //    无需经过 SkiaSharp 二次解码→缩放→编码。
            //    大图（如图库原图 > 512KB）走下面的 LoadAsync 磁盘缓存链路。
            if (IsSmallCachedFile(item.FilePath, maxSizeKB: 512))
            {
                var bitmap = ImageThumbnailService.GetOrCreate(item.FilePath);
                if (bitmap != null && IsStillValid(item))
                {
                    ApplyBitmap(item.Image, bitmap);
                    return;
                }
            }

            // 3. 异步加载：从原图缩放 → 存磁盘 → 加载（图库原图等）
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var bitmap = await ImageThumbnailService.LoadAsync(
                    item.FilePath, item.DecodeSize, cts.Token);
                if (bitmap != null && IsStillValid(item))
                    ApplyBitmap(item.Image, bitmap);
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>判断文件是否已是一张小尺寸缩略图（跳过 SkiaSharp 生成）。</summary>
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

        /// <summary>再次验证控件未被回收。</summary>
        private bool IsStillValid(LoadItem item)
        {
            return !_disposed &&
                   _imageGens.TryGetValue(item.Image, out int gen) &&
                   gen == item.Generation;
        }

        private static void ApplyBitmap(Image image, BitmapImage bitmap)
        {
            image.Source = bitmap;
            image.Opacity = 1.0;
        }

        private sealed record LoadItem(
            Image Image, string FilePath, uint DecodeSize, int Generation);
    }
}
