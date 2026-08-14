using SightoHear.Helpers;
using SightoHear.Services.Lyrics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace SightoHear.Services
{
    /// <summary>
    /// 资源诊断服务：让"浏览多个页面时软件到底发生了什么"透明化。
    ///
    /// 目标：定位浏览页面后 Win2D（歌词/图片查看器）渲染卡顿的内存与资源累积根源。
    /// 采集维度：
    ///   1. 进程内存快照（工作集 / 私有内存 / 托管堆 / LOH / GC 计数 / GC 暂停时长）
    ///   2. 各缓存服务条目数（缩略图 LRU / 封面路径 / 歌词 / 媒体库 / 转换器弱引用）
    ///   3. 页面实例追踪（WeakReference 检测页面卸载后是否仍存活 → 泄漏检测）
    ///   4. Win2D 画布与渲染回调计数（活跃 FreeRunCanvas / CompositionTarget.Rendering 订阅数）
    ///
    /// 用法：
    ///   - 页面 Loaded 时调用 <see cref="TrackPage(object,string)"/>，Unloaded 时 <see cref="UntrackPage(object)"/>
    ///   - 任意时刻调用 <see cref="LogSnapshot(string)"/> 输出完整快照到日志
    ///   - MainWindow 启动周期快照定时器，自动记录页面浏览过程中的资源变化
    /// </summary>
    public static class ResourceDiagnosticsService
    {
        // ── 总开关：DebugPage / 设置可关闭，避免生产环境日志污染 ──
        public static bool IsEnabled { get; set; } = true;

        // ── 页面实例追踪（WeakReference 泄漏检测）──
        private sealed class PageTrack
        {
            public string PageId = "";
            public WeakReference? WeakRef;
            public DateTime CreatedAt;
            public DateTime? UnloadedAt;
            public bool IsActive;
        }

        private static readonly object _pagesLock = new();
        private static readonly List<PageTrack> _pageTracks = new();

        // ── Win2D 画布 / 渲染回调计数 ──
        private static int _activeCanvasCount;          // 活跃 FreeRunCanvas 数量
        private static int _renderingHandlerCount;      // CompositionTarget.Rendering 订阅数
        private static int _dispatcherTimerCount;       // 活跃 DispatcherTimer（页面自行上报）
        private static readonly object _counterLock = new();

        // ── 会话统计 ──
        private static long _navigationCount;           // 会话导航次数
        private static long _pageLoadedCount;           // 会话页面加载次数
        private static long _snapshotCount;             // 快照次数
        private static DateTime _firstSnapshotTime = DateTime.MinValue;

        // ═════════════════════════════ 页面实例追踪 ═════════════════════════════

        /// <summary>
        /// 登记页面实例（页面 Loaded 时调用）。页面卸载后若实例仍存活（WeakReference 仍指向），
        /// 说明存在泄漏——快照会明确报告。
        /// </summary>
        public static void TrackPage(object page, string pageId)
        {
            if (!IsEnabled || page == null) return;

            lock (_pagesLock)
            {
                // 同一实例重复登记时先移除旧记录，避免重复计数
                _pageTracks.RemoveAll(t => t.WeakRef != null && ReferenceEquals(t.WeakRef.Target, page));

                var track = new PageTrack
                {
                    PageId = pageId,
                    WeakRef = new WeakReference(page),
                    CreatedAt = DateTime.Now,
                    IsActive = true
                };
                _pageTracks.Add(track);
                Interlocked.Increment(ref _pageLoadedCount);
            }
        }

        /// <summary>
        /// 注销页面实例（页面 Unloaded 时调用）。
        /// </summary>
        public static void UntrackPage(object page)
        {
            if (!IsEnabled || page == null) return;

            lock (_pagesLock)
            {
                var track = _pageTracks.FirstOrDefault(t =>
                    t.WeakRef != null && ReferenceEquals(t.WeakRef.Target, page));
                if (track != null)
                {
                    track.IsActive = false;
                    track.UnloadedAt = DateTime.Now;
                }
            }
        }

        // ═════════════════════════════ Win2D / 渲染资源计数 ═════════════════════════════

        /// <summary>登记一个活跃的 Win2D 渲染画布（FreeRunCanvas 创建时调用）。</summary>
        public static void RegisterCanvas() { if (IsEnabled) lock (_counterLock) _activeCanvasCount++; }

        /// <summary>注销一个 Win2D 渲染画布（FreeRunCanvas 销毁/卸载时调用）。</summary>
        public static void UnregisterCanvas() { if (IsEnabled) lock (_counterLock) if (_activeCanvasCount > 0) _activeCanvasCount--; }

        /// <summary>登记一个 CompositionTarget.Rendering 订阅（订阅时调用）。</summary>
        public static void RegisterRenderingHandler() { if (IsEnabled) lock (_counterLock) _renderingHandlerCount++; }

        /// <summary>注销一个 CompositionTarget.Rendering 订阅（退订时调用）。</summary>
        public static void UnregisterRenderingHandler() { if (IsEnabled) lock (_counterLock) if (_renderingHandlerCount > 0) _renderingHandlerCount--; }

        /// <summary>登记一个活跃 DispatcherTimer（页面创建计时器时调用）。</summary>
        public static void RegisterDispatcherTimer() { if (IsEnabled) lock (_counterLock) _dispatcherTimerCount++; }

        /// <summary>注销一个 DispatcherTimer（页面销毁计时器时调用）。</summary>
        public static void UnregisterDispatcherTimer() { if (IsEnabled) lock (_counterLock) if (_dispatcherTimerCount > 0) _dispatcherTimerCount--; }

        /// <summary>记录一次页面导航（MainWindow 导航完成时调用）。</summary>
        public static void RecordNavigation()
        {
            if (IsEnabled) Interlocked.Increment(ref _navigationCount);
        }

        // ═════════════════════════════ 快照采集 ═════════════════════════════

        /// <summary>
        /// 输出一份完整资源快照到日志。用于：页面导航、播放器打开/关闭、周期定时器等时机。
        /// </summary>
        /// <param name="reason">触发原因描述（如"浏览图库页后打开播放器"）。</param>
        /// <param name="level">日志级别，默认 Info。周期快照建议 Debug 避免刷屏。</param>
        public static void LogSnapshot(string reason, LogLevel level = LogLevel.Info)
        {
            if (!IsEnabled) return;

            long snap = Interlocked.Increment(ref _snapshotCount);
            if (_firstSnapshotTime == DateTime.MinValue)
                _firstSnapshotTime = DateTime.Now;

            string report = BuildFullReport(snap, reason);

            switch (level)
            {
                case LogLevel.Debug: AppLogger.Debug(report); break;
                case LogLevel.Trace: AppLogger.Trace(report); break;
                default: AppLogger.Info(report); break;
            }
        }

        /// <summary>构建完整诊断报告（内存 + 缓存 + 页面 + Win2D 资源）。</summary>
        public static string BuildFullReport(long? snapshotNumber = null, string reason = "")
        {
            var sb = new System.Text.StringBuilder();
            long snap = snapshotNumber ?? Interlocked.Increment(ref _snapshotCount);
            if (_firstSnapshotTime == DateTime.MinValue)
                _firstSnapshotTime = DateTime.Now;

            sb.AppendLine("══════════════════════ 资源诊断快照 ══════════════════════");
            sb.AppendLine($"  快照 #{snap} | 时间: {DateTime.Now:HH:mm:ss} | 已运行: {DateTime.Now - _firstSnapshotTime:hh\\:mm\\:ss}");
            if (!string.IsNullOrEmpty(reason))
                sb.AppendLine($"  触发原因: {reason}");
            sb.AppendLine();

            sb.AppendLine("── 进程内存 ──");
            AppendMemoryReport(sb);
            sb.AppendLine();

            sb.AppendLine("── 缓存条目数 ──");
            AppendCacheReport(sb);
            sb.AppendLine();

            sb.AppendLine("── 页面实例追踪（卸载后仍存活 = 泄漏） ──");
            AppendPageReport(sb);
            sb.AppendLine();

            sb.AppendLine("── Win2D / 渲染资源 ──");
            lock (_counterLock)
            {
                sb.AppendLine($"  活跃 Win2D 渲染画布(FreeRunCanvas): {_activeCanvasCount}");
                sb.AppendLine($"  CompositionTarget.Rendering 订阅: {_renderingHandlerCount}");
                sb.AppendLine($"  活跃 DispatcherTimer: {_dispatcherTimerCount}");
            }
            sb.AppendLine($"  会话导航次数: {Volatile.Read(ref _navigationCount)} | 页面加载次数: {Volatile.Read(ref _pageLoadedCount)}");
            sb.AppendLine("══════════════════════ 快照结束 ══════════════════════");

            return sb.ToString();
        }

        /// <summary>仅采集进程内存与 GC 统计。</summary>
        private static void AppendMemoryReport(System.Text.StringBuilder sb)
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                long workingSet = proc.WorkingSet64 / 1024 / 1024;
                long privateMem = proc.PrivateMemorySize64 / 1024 / 1024;
                long managedHeap = GC.GetTotalMemory(false) / 1024 / 1024;

                // GC 详细统计（.NET 8）
                GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
                long heapSize = gcInfo.HeapSizeBytes / 1024 / 1024;
                long lohSize = 0;
                long gen0Size = 0, gen1Size = 0, gen2Size = 0;
                try
                {
                    var gens = gcInfo.GenerationInfo;
                    if (gens.Length > 0) gen0Size = gens[0].SizeAfterBytes / 1024 / 1024;
                    if (gens.Length > 1) gen1Size = gens[1].SizeAfterBytes / 1024 / 1024;
                    if (gens.Length > 2) gen2Size = gens[2].SizeAfterBytes / 1024 / 1024;
                    if (gens.Length > 3) lohSize = gens[3].SizeAfterBytes / 1024 / 1024;
                }
                catch { /* 个别字段不可用时忽略 */ }

                // GC 暂停总时长（毫秒）
                double pauseMs = 0;
                try
                {
                    foreach (var pause in gcInfo.PauseDurations)
                        pauseMs += pause.TotalMilliseconds;
                }
                catch { }

                sb.AppendLine($"  工作集: {workingSet} MB | 私有内存: {privateMem} MB | 托管堆总量: {managedHeap} MB");
                sb.AppendLine($"  GC 堆大小: {heapSize} MB (Gen0: {gen0Size} | Gen1: {gen1Size} | Gen2: {gen2Size} | LOH: {lohSize})");
                sb.AppendLine($"  GC 次数: Gen0={GC.CollectionCount(0)} Gen1={GC.CollectionCount(1)} Gen2={GC.CollectionCount(2)}");
                sb.AppendLine($"  GC 累计暂停: {pauseMs:F1} ms | 已提交: {gcInfo.TotalCommittedBytes / 1024 / 1024} MB");

                // 线程数（过多的线程暗示泄漏的 Timer / 后台任务）
                sb.AppendLine($"  线程数: {proc.Threads.Count}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  内存采集失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>采集各缓存服务条目数。</summary>
        private static void AppendCacheReport(System.Text.StringBuilder sb)
        {
            try
            {
                var thumb = ImageThumbnailService.GetCacheStats();
                sb.AppendLine($"  缩略图 LRU 缓存: {thumb.Count}/{thumb.Capacity} 条 BitmapImage");

                var cover = MusicCoverService.GetCacheStats();
                sb.AppendLine($"  音乐封面路径缓存: {cover.ResolvedPaths} 条 (进行中: 列表={cover.InFlight} 背景={cover.BackgroundInFlight} 原图={cover.OriginalInFlight})");

                sb.AppendLine($"  歌词缓存: {NetworkLyricsService.GetCacheCount()} 条");

                var media = MediaScanner.GetCacheStats();
                sb.AppendLine($"  媒体库内存缓存: 视频={media.Video} 图片={media.Image} 音乐={media.Music} 合计={media.Total} 条");

                var musicCache = MusicDataCache.GetCacheStats();
                sb.AppendLine($"  音乐库数据缓存: 歌曲={musicCache.Music} 歌单={musicCache.Playlists} 歌手={musicCache.Artists} 专辑={musicCache.Albums} 文件夹={musicCache.Folders} (已初始化={musicCache.Initialized})");

                sb.AppendLine($"  转换器弱引用缓存: {FilePathToImageConverter.GetCacheCount()} 条");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  缓存统计失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>页面实例追踪报告：列出每个已登记页面，标记卸载后是否仍存活（泄漏）。</summary>
        private static void AppendPageReport(System.Text.StringBuilder sb)
        {
            lock (_pagesLock)
            {
                if (_pageTracks.Count == 0)
                {
                    sb.AppendLine("  （无已登记页面）");
                    return;
                }

                int active = 0, leaked = 0, released = 0;
                foreach (var track in _pageTracks)
                {
                    bool alive = track.WeakRef?.IsAlive == true;
                    if (track.IsActive)
                    {
                        active++;
                        sb.AppendLine($"  ● {track.PageId} | 活跃中 | 创建: {track.CreatedAt:HH:mm:ss} | 存活={(alive ? "是" : "否")}");
                    }
                    else
                    {
                        if (alive)
                        {
                            leaked++;
                            sb.AppendLine($"  ✖ {track.PageId} | 已卸载但仍存活(泄漏!) | 卸载: {track.UnloadedAt:HH:mm:ss}");
                        }
                        else
                        {
                            released++;
                            sb.AppendLine($"  ○ {track.PageId} | 已卸载且已回收 | 卸载: {track.UnloadedAt:HH:mm:ss}");
                        }
                    }
                }

                sb.AppendLine($"  合计: {_pageTracks.Count} 条 | 活跃={active} 已回收={released} 疑似泄漏={leaked}");
            }
        }
    }
}
