using SightoHear.Helpers;
using SightoHear.Models;
using SightoHear.Services;
using SightoHear.Services.Lyrics;
using SightoHear.Controls;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Text;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;
using System.Text;

namespace SightoHear
{
    public sealed partial class MusicPlayerPage : Page, INotifyPropertyChanged
    {
        private CoverBackgroundRenderer _backgroundRenderer = new();
        private FluidBackgroundRenderer _fluidRenderer = new();
        private readonly MusicPlaybackService _playback = App.MusicPlayback;
        private readonly DispatcherTimer _playbackTimer;
        private readonly DispatcherTimer _controlsHideTimer;
        private DispatcherTimer? _playModeToolTipTimer;
        private ToolTip? _playModeToolTip;
        private const byte BackgroundDimOverlayAlpha = 77;
        private MusicPlayerArgs? _playerArgs;
        private string _currentCoverPath = string.Empty;
        private Windows.UI.Color[] _fluidColors =
        {
            Windows.UI.Color.FromArgb(255, 112, 52, 190),
            Windows.UI.Color.FromArgb(255, 170, 70, 128),
            Windows.UI.Color.FromArgb(255, 94, 48, 150),
            Windows.UI.Color.FromArgb(255, 194, 82, 50)
        };
        private bool _canvasResourcesReady;
        private bool _isLoadingCover;
        private bool _isImmersiveMode;
        private bool _isDraggingProgress;
        private bool _isUpdatingVolume;
        private double _durationSeconds = 1;

        // ── Win2D HUD 性能采集（仅 W2D 线程访问）──
        private readonly Stopwatch _hudUpdateSw = new();
        private readonly Stopwatch _hudDrawSw = new();
        private double _hudLastDrawMs;
        private double _progressSeconds;
        private CanvasLyricsRenderer _lyricsRenderer = new();
        // ★ 生命周期锁：保护 _lyricsRenderer 的 Dispose（UI 线程 Unloaded）与
        //   Draw（渲染线程回调）互斥，避免退出播放器时渲染线程访问已释放的 TextLayout。
        private readonly object _lyricsLifecycleLock = new();
        private readonly object _lyricsLayoutLock = new();
        private string _currentLyricsItemId = string.Empty;
        private bool _lyricsLoadInFlight;
        private string _loadedLyricsItemId = string.Empty;
        // 当前已加载的歌词数据（本地或网络来源），供"保存当前歌词文件"使用
        private LyricsData? _currentLyricsData;
        private CancellationTokenSource? _lyricsCts;
        private double _lyricsRenderStartX;
        private double _lyricsRenderStartY;
        private double _lyricsRenderWidth = 1;
        private double _lyricsRenderHeight = 1;
        private bool _lyricsRenderLayoutReady;
        private bool _hasLoggedLyricsUpdateError;
        private bool _hasLoggedLyricsDrawError;
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public IReadOnlyList<MediaItem> QueueItems => _playback.HasExternalPlayback ? _playback.ExternalPlayQueue : _playback.PlayQueue;

        // ===================== 播放队列 Flyout =====================
        // ★ 修复：改为可空类型，允许在 Unloaded 时置 null 断开"系统弹出层 → Flyout → 页面"引用链
        private Flyout? _queueFlyout;
        private ListView _queueList = null!;
        private TextBlock _queueEmptyText = null!;
        private DataTemplate? _queueDefaultTemplate;
        private DataTemplate? _queueNowPlayingTemplate;
        private bool _isQueueFlyoutOpen;

        // 播放队列卡片交互画刷
        private SolidColorBrush _queueNormalBgBrush = null!;
        private SolidColorBrush _queueHoverBgBrush = null!;
        private SolidColorBrush _queuePressedBgBrush = null!;
        private SolidColorBrush _queueNormalBorderBrush = null!;
        private SolidColorBrush _queueHoverBorderBrush = null!;
        private SolidColorBrush _queuePressedBorderBrush = null!;

        // 播放队列均衡器
        private bool _queueEqualizerRunning;
        private readonly Visual[] _queueBarVisuals = new Visual[5];
        private readonly Stopwatch _queueEqualizerStopwatch = new();

        public MusicPlayerPage()
        {
            InitializeComponent();
            // 歌词延迟按歌曲独立记忆（LyricsDelayStore），渲染器默认 0ms，换歌时在
            // SyncPlaybackState / Playback_CurrentItemChanged 中按当前歌曲恢复或重置。
            // 应用 Win2D GPU 选择（手动指定时使用自定义渲染设备；跟随系统时为 null 走共享设备）
            BackgroundCanvas.CustomDevice = Win2DDeviceManager.CustomDevice;
            BackgroundCanvas.MaxFps = 1000; // 限制最大帧率 1000 帧/秒，避免 GPU 空转
            Loaded += MusicPlayerPage_Loaded;
            Unloaded += MusicPlayerPage_Unloaded;
            // ★ 修复：改用具名方法订阅 SizeChanged，便于 Unloaded 退订。
            //   匿名 lambda 虽为页内自循环通常可回收，但为避免任何"视觉树 → 事件 → 页面"
            //   残留路径（日志中页面"已卸载仍存活"的潜在来源），保持订阅/退订严格对称。
            RootGrid.SizeChanged += RootGrid_SizeChanged;
            LyricsHost.SizeChanged += LyricsHost_SizeChanged;

            // ★ 资源诊断：登记活跃 Win2D 画布（BackgroundCanvas 渲染循环）
            ResourceDiagnosticsService.RegisterCanvas();

            _playbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _playbackTimer.Tick += PlaybackTimer_Tick;
            ResourceDiagnosticsService.RegisterDispatcherTimer();

            _controlsHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900)
            };
            // ★ 修复：改用具名方法订阅，便于 Unloaded 时退订（匿名 lambda 无法退订，
            //   且运行中的 Timer 会持续持有页面实例）
            _controlsHideTimer.Tick += ControlsHideTimer_Tick;
        }

        private void ControlsHideTimer_Tick(object? sender, object e)
        {
            _controlsHideTimer.Stop();
            if (!_isDraggingProgress)
                BottomControlsHost.Opacity = 0;
        }

        // ★ 修复：SizeChanged 具名处理器（构造函数订阅，Unloaded 退订，保持对称）
        private void RootGrid_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateLyricsRenderLayout();

        private void LyricsHost_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateLyricsRenderLayout();

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _playerArgs = e.Parameter as MusicPlayerArgs;
            EnterImmersiveMode();
            SyncPlaybackState();
            _ = ApplyBackgroundFromCurrentItemAsync();
        }

        private async void MusicPlayerPage_Loaded(object sender, RoutedEventArgs e)
        {
            // ★ 修复：缓存清理延迟到滑入动画（300ms）+ Win2D 首帧渲染完成之后执行。
            //   此前 Loaded 同步执行 TrimMemoryCache(64)，实测在动画开始后仅 35ms 触发，
            //   64 条 BitmapImage 的 GPU 纹理释放/重建与 Win2D 画布首帧渲染竞争，
            //   DirectX 上下文等待资源就绪导致帧时间飙升至 >16ms（滑入动画"抽搐"）。
            //   改为延迟 + 低优先级，清理只在空闲期进行（Unloaded 中仍有兜底清理）。
            _ = ScheduleDeferredCacheCleanupAsync();

            _playback.CurrentItemChanged += Playback_CurrentItemChanged;
            _playback.PlaybackStateChanged += Playback_PlaybackStateChanged;
            _playback.VolumeChanged += Playback_VolumeChanged;
            _playback.PlayModeChanged += Playback_PlayModeChanged;
            _playback.PlaybackFailed += Playback_PlaybackFailed;
            _playback.QueueChanged += Playback_QueueChanged;

            SyncPlaybackState();
            _playbackTimer.Start();
            UpdateLyricsRenderLayout();
            BuildQueueFlyout();

            // 确保封面转换器在全局资源中可用（供 XamlReader 模板解析 StaticResource 时使用）
            if (!Application.Current.Resources.ContainsKey("MusicCoverConverter"))
                Application.Current.Resources["MusicCoverConverter"] = new FilePathToImageConverter();

            if (_playback.CurrentItem == null &&
                _playerArgs?.CurrentItem != null)
            {
                await _playback.PlayAsync(
                    _playerArgs.CurrentItem,
                    _playerArgs.Playlist.Count > 0
                        ? _playerArgs.Playlist
                        : new[] { _playerArgs.CurrentItem });
            }

            _ = ApplyBackgroundFromCurrentItemAsync();
        }

        /// <summary>
        /// ★ 修复：将缩略图/封面缓存清理延迟到滑入动画与首帧渲染完成后的空闲期执行。
        /// 低优先级调度，避免 GPU 显存分配/释放操作阻塞渲染线程提交 Draw Call
        /// （DeepSeek 分析：Trim 与滑入动画重叠是"浏览多页面后播放器掉帧"的直接元凶）。
        /// 页面卸载时（Unloaded）另有兜底清理，此处卸载后会自动跳过。
        /// </summary>
        private async System.Threading.Tasks.Task ScheduleDeferredCacheCleanupAsync()
        {
            try
            {
                // 延迟 700ms：覆盖 300ms 滑入动画 + Win2D 首帧渲染稳定期
                await System.Threading.Tasks.Task.Delay(700);
                if (_backgroundRenderer == null || BackgroundCanvas == null)
                    return; // 页面已卸载，Unloaded 已做清理

                // 低优先级调度到 UI 线程，不打断渲染帧
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (_backgroundRenderer == null || BackgroundCanvas == null)
                            return;
                        AppLogger.Debug($"[MusicPlayer] 空闲期裁剪缓存, CurrentGen={PageLifetimeService.CurrentGeneration}");
                        ImageThumbnailService.TrimMemoryCache(64);
                        MusicCoverService.ClearCache();
                        NetworkLyricsService.ClearCache();
                    });
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "延迟裁剪缓存失败");
            }
        }

        private void MusicPlayerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // ★ 修复：停止播放模式 ToolTip 定时器。
            //   运行中的 DispatcherTimer 通过匿名 lambda（捕获 this）持有页面引用，
            //   是日志中 MusicPlayerPage"已卸载仍存活(泄漏!)"的来源之一。
            _playModeToolTipTimer?.Stop();
            _playModeToolTipTimer = null;

            _playback.CurrentItemChanged -= Playback_CurrentItemChanged;
            _playback.PlaybackStateChanged -= Playback_PlaybackStateChanged;
            _playback.VolumeChanged -= Playback_VolumeChanged;
            _playback.PlayModeChanged -= Playback_PlayModeChanged;
            _playback.PlaybackFailed -= Playback_PlaybackFailed;
            _playback.QueueChanged -= Playback_QueueChanged;

            if (_queueFlyout != null)
            {
                _queueFlyout.Hide();
                // ★ 修复：退订 Flyout 事件并断开引用，打破"系统弹出层 → Flyout → 页面"的
                //   外部强引用链，否则页面卸载后无法被 GC 回收（Win2D GPU 资源随之泄漏）。
                _queueFlyout.Opening -= QueueFlyout_Opening;
                _queueFlyout.Closed -= QueueFlyout_Closed;
                _queueList.ItemClick -= QueueList_ItemClick;
                _queueList.ItemsSource = null;
                _queueFlyout = null;
                QueueButton.Flyout = null;
            }
            // 强制停止队列均衡器，确保 CompositionTarget.Rendering handler 被移除
            StopQueueEqualizerAnimation();
            _playbackTimer.Tick -= PlaybackTimer_Tick;
            _playbackTimer.Stop();
            // ★ 修复：停止控制栏隐藏定时器并退订。
            //   运行中的 DispatcherTimer 会持续持有其 Tick 回调（匿名 lambda 捕获 this），
            //   使页面实例在卸载后仍存活一个引用窗口（日志中 MusicPlayerPage"已卸载仍存活"的来源之一）。
            _controlsHideTimer.Tick -= ControlsHideTimer_Tick;
            _controlsHideTimer.Stop();
            // ★ 修复：退订 SizeChanged（与构造函数对称），消除视觉树事件残留路径
            RootGrid.SizeChanged -= RootGrid_SizeChanged;
            LyricsHost.SizeChanged -= LyricsHost_SizeChanged;
            BackgroundCanvas.Paused = true;

            // ★ 温和裁剪缩略图缓存（保留热数据，避免一次性清空引发 GC 风暴），
            //    同时释放播放器自己创建的位图资源
            AppLogger.Debug($"[MusicPlayer] Unloaded → 即将 Trim 缓存, CurrentGen={PageLifetimeService.CurrentGeneration}");
            ImageThumbnailService.TrimMemoryCache(64);
            MusicCoverService.ClearCache();
            NetworkLyricsService.ClearCache();

            _fluidRenderer.Dispose();
            _fluidRenderer = null!;
            _backgroundRenderer.Dispose();
            _backgroundRenderer = null!;
            // ★ 生命周期锁：等渲染线程当前帧 Draw 结束后再 Dispose 并置 null，
            //   消除"检查通过后字段被置空/资源被释放"的竞态窗口。
            lock (_lyricsLifecycleLock)
            {
                _lyricsRenderer.Dispose();
                _lyricsRenderer = null!;
            }
            _lyricsCts?.Cancel();
            _lyricsCts?.Dispose();
            _lyricsCts = null;
            // ★ 重置歌词加载去重状态：重新打开播放器会新建空渲染器，必须允许重新加载
            _lyricsLoadInFlight = false;
            _loadedLyricsItemId = string.Empty;
            _currentLyricsItemId = string.Empty;

            // ★ 关键步骤：从可视化树中移除 FreeRunCanvas 并置空，
            //    停止渲染线程、释放交换链，打破渲染管线对页面的引用，使旧页面实例可被 GC 回收。
            BackgroundCanvas.RemoveFromVisualTree();
            BackgroundCanvas = null!;

            // ★ 资源诊断：注销 Win2D 画布与计时器
            ResourceDiagnosticsService.UnregisterCanvas();
            ResourceDiagnosticsService.UnregisterDispatcherTimer();

            // 清除所有 Pointer 事件订阅，避免旧 LyricsHost 的静态引用路径
            LyricsHost.PointerMoved -= LyricsHost_PointerMoved;
            LyricsHost.PointerExited -= LyricsHost_PointerExited;
            LyricsHost.PointerPressed -= LyricsHost_PointerPressed;
            LyricsHost.PointerWheelChanged -= LyricsHost_PointerWheelChanged;

            ExitImmersiveMode();
        }

        private void EnterImmersiveMode()
        {
            if (_isImmersiveMode)
                return;

            _isImmersiveMode = true;
            (App.MainWindow as MainWindow)?.EnterPlayerFullScreen();
        }

        private void ExitImmersiveMode()
        {
            if (!_isImmersiveMode)
                return;

            _isImmersiveMode = false;
            (App.MainWindow as MainWindow)?.ExitPlayerFullScreen();
        }

        private void SyncPlaybackState()
        {
            MediaItem? item = _playback.CurrentItem ?? _playerArgs?.CurrentItem;
            if (item != null)
                UpdateSongInfo(item);

            // 应用当前歌曲的歌词延迟（无记录则恢复默认 0ms）
            ApplyLyricsDelayForCurrentItem();

            UpdateProgress();
            UpdatePlaybackIcon();
            UpdateVolume();
            UpdatePlayModeIcon();
            ApplyAdaptiveControlBrush();
        }

        /// <summary>
        /// 按当前歌曲应用歌词延迟：仅对当前歌曲生效——
        /// 该歌曲有手动调整记录时恢复其延迟值，否则恢复默认 0ms。
        /// </summary>
        private void ApplyLyricsDelayForCurrentItem()
        {
            if (_lyricsRenderer == null)
                return;
            MediaItem? item = _playback.CurrentItem ?? _playerArgs?.CurrentItem;
            int delay = LyricsDelayStore.GetDelay(item?.FilePath);
            _lyricsRenderer.UserDelayMs = delay;
            AppLogger.Info($"切换歌曲歌词延迟应用: {delay} ms");
        }

        private void UpdateSongInfo(MediaItem item)
        {
            string title = string.IsNullOrWhiteSpace(item.Title) ? item.FileName : item.Title;
            TitleText.Text = title;
            TitleText.FontSize = ComputeTitleFontSize(title);
            ArtistText.Text = item.ArtistDisplay;
            AlbumText.Text = item.AlbumDisplay;

            string coverPath = MusicItemMenuHelper.ResolveDisplayCoverPath(item);
            CoverImage.Source = string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath)
                ? null
                : new BitmapImage(new Uri(coverPath));

            _ = LoadLyricsAsync(item);
        }

        // ===================== 播放信息右键菜单 =====================

        /// <summary>封面/标题/歌手/专辑区域右键菜单（与音乐库页面共用 MusicItemMenuHelper）。</summary>
        private void PlayerInfoArea_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            e.Handled = true;
            MediaItem? item = _playback.CurrentItem ?? _playerArgs?.CurrentItem;

            var menu = new MenuFlyout();
            if (item == null)
            {
                // 未播放时给出禁用提示项
                menu.Items.Add(new MenuFlyoutItem { Text = "暂无播放信息", IsEnabled = false });
                menu.ShowAt(element, e.GetPosition(element));
                return;
            }

            // 复用公共菜单项：查看封面 / 复制
            menu.Items.Add(MusicItemMenuHelper.BuildViewCoverMenuItem(item, XamlRoot));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MusicItemMenuHelper.BuildCopySubMenu(item));

            // 从音乐库页面菜单复用：使用其他应用打开 / 打开文件所在位置 / 添加到歌单 / 删除
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MusicItemMenuHelper.BuildOpenWithMenuItem(item));
            menu.Items.Add(MusicItemMenuHelper.BuildOpenLocationMenuItem(item));
            menu.Items.Add(MusicItemMenuHelper.BuildAddToPlaylistMenuItem(item));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MusicItemMenuHelper.BuildDeleteMenuItem(item, XamlRoot));

            menu.ShowAt(element, e.GetPosition(element));
        }

        // 标题在 404 宽的设计区域内最多显示两行；标题越长字号越小，避免被裁剪。
        // 中日韩等全角字符按两个单位计宽，以贴近实际占位。
        private static double ComputeTitleFontSize(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return 38;

            double weightedLength = 0;
            foreach (char ch in title)
                weightedLength += IsWideChar(ch) ? 2 : 1;

            return weightedLength switch
            {
                <= 16 => 38,
                <= 24 => 33,
                <= 34 => 28,
                <= 46 => 24,
                _ => 20
            };
        }

        private static bool IsWideChar(char ch) =>
            ch >= 'ᄀ' &&
            (ch <= 'ᅟ' ||                       // 韩文字母
             ch is >= '⺀' and <= '〾' ||    // CJK 部首、符号
             ch is >= 'ぁ' and <= '㏿' ||    // 假名、CJK 符号
             ch is >= '㐀' and <= '鿿' ||    // CJK 统一表意文字
             ch is >= '가' and <= '힣' ||    // 韩文音节
             ch is >= '豈' and <= '﫿' ||    // CJK 兼容表意文字
             ch is >= '＀' and <= '｠' ||    // 全角字符
             ch is >= '￠' and <= '￦');      // 全角符号

        private async Task LoadLyricsAsync(MediaItem item, bool forceReload = false)
        {
            // ★ 安全：如果页面已卸载，取消加载
            if (_lyricsRenderer == null)
                return;
            // ★ 统一使用 FilePath 作为去重 key：OnNavigatedTo（_playerArgs.CurrentItem）
            //   与 Loaded（_playback.CurrentItem）传入的 MediaItem 实例可能不同，
            //   若一方 Id 为空一方非空会导致 itemId 不一致、去重失效（歌词重复加载）。
            //   同一文件的 FilePath 始终一致，是稳定的唯一键。
            string itemId = string.IsNullOrWhiteSpace(item.FilePath) ? item.Id : item.FilePath;

            // ★ 同曲目去重：SyncPlaybackState（Loaded）与 CurrentItemChanged 会先后触发两次
            //   加载同一曲目（日志中两次 SetLyrics 即由此产生）。重复 SetLyrics 会与渲染线程
            //   竞争 _renderLines（Collection was modified → 当帧渲染中断 → 卡顿）。
            //   同一曲目正在加载或已成功加载且非强制刷新时直接跳过。
            if (!forceReload &&
                _currentLyricsItemId == itemId &&
                (_lyricsLoadInFlight || _loadedLyricsItemId == itemId))
                return;

            _currentLyricsItemId = itemId;
            // 开始新曲目加载时清空旧歌词，避免保存到上一首的歌词
            _currentLyricsData = null;
            _hasLoggedLyricsUpdateError = false;
            _hasLoggedLyricsDrawError = false;
            _lyricsRenderer.SetPlaceholder("正在加载歌词");
            ShowLyricsPlaceholder("正在加载歌词");

            // 取消上一首仍在进行的网络歌词检索，避免旧结果覆盖当前曲目。
            _lyricsCts?.Cancel();
            _lyricsCts?.Dispose();
            var cts = new CancellationTokenSource();
            _lyricsCts = cts;

            _lyricsLoadInFlight = true;
            try
            {
                double? duration = _playback.Duration.TotalSeconds > 0
                    ? _playback.Duration.TotalSeconds
                    : item.Duration?.TotalSeconds;

                LyricsData? lyricsData = null;

                // 网络优先：开关开启时先联网获取；网络无结果时按"回退本地"开关决定是否加载本地歌词。
                if (App.SettingsHelper.MusicUseNetworkLyricsSource)
                {
                    _lyricsRenderer.SetPlaceholder("正在从网络获取歌词");
                    ShowLyricsPlaceholder("正在从网络获取歌词");

                    lyricsData = await NetworkLyricsService.FetchAsync(
                        item,
                        duration,
                        NetworkLyricsService.ParsePreference(App.SettingsHelper.MusicLyricsSourcePreference),
                        cts.Token);

                    if (_currentLyricsItemId != itemId)
                        return;
                }

                if (lyricsData == null || lyricsData.LyricsLines.Count == 0)
                {
                    lyricsData = await LocalLyricsService.LoadAsync(item, duration);

                    if (_currentLyricsItemId != itemId)
                        return;
                }

                if (lyricsData == null || lyricsData.LyricsLines.Count == 0)
                {
                    _lyricsRenderer.SetPlaceholder("暂无歌词");
                    ShowLyricsPlaceholder("暂无歌词");
                    return;
                }

                _lyricsRenderer.SetLyrics(lyricsData.LyricsLines);
                // ★ 记录当前渲染使用的歌词数据（本地或网络来源），供"保存当前歌词文件"使用
                _currentLyricsData = lyricsData;
                // ★ 记录已成功加载的曲目，用于同曲目去重
                _loadedLyricsItemId = itemId;
            }
            catch (OperationCanceledException)
            {
                // 切歌导致的取消，无需处理。
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "加载歌词");
                if (_currentLyricsItemId == itemId)
                {
                    _lyricsRenderer.SetPlaceholder("歌词解析失败");
                    ShowLyricsPlaceholder("歌词解析失败");
                }
            }
            finally
            {
                _lyricsLoadInFlight = false;
            }
        }

        private void ShowLyricsPlaceholder(string text)
        {
            _lyricsRenderer.SetPlaceholder(text);
        }

        private async Task ApplyBackgroundFromCurrentItemAsync()
        {
            // ★ 修复：进入时先检查页面是否已卸载（渲染器/画布可能已被 Unloaded 置 null），
            //   避免后续访问 null 引用抛 NRE，以及加载的 CanvasBitmap 无法交接而泄漏。
            if (_backgroundRenderer == null || BackgroundCanvas == null || _canvasResourcesReady == false)
                return;

            MediaItem? item = _playback.CurrentItem ?? _playerArgs?.CurrentItem;
            string coverPath = MusicItemMenuHelper.ResolveDisplayCoverPath(item);
            if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath))
                coverPath = MusicItemMenuHelper.ResolveCoverPath(item);
            if (_isLoadingCover || coverPath == _currentCoverPath)
                return;

            _currentCoverPath = coverPath;

            if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath))
            {
                _backgroundRenderer.SetCoverBitmap(null);
                CoverImage.Source = null;
                return;
            }

            CanvasBitmap? bitmap = null;
            try
            {
                _isLoadingCover = true;
                string paletteCoverPath = MusicItemMenuHelper.ResolveDisplayCoverPath(item);
                if (string.IsNullOrWhiteSpace(paletteCoverPath) || !File.Exists(paletteCoverPath))
                    paletteCoverPath = coverPath;

                using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(
                    coverPath,
                    FileAccessMode.Read);
                bitmap = await CanvasBitmap.LoadAsync(BackgroundCanvas, stream);

                // ★ 修复：await 之后页面可能已卸载（Unloaded 已将渲染器/画布置 null），
                //   必须重新校验；若已卸载则释放刚加载的位图，避免 GPU 纹理泄漏。
                if (_backgroundRenderer == null || BackgroundCanvas == null)
                    return;

                _fluidColors = MusicCoverService.GetBackgroundAccentColors(paletteCoverPath, 4).ToArray();
                _backgroundRenderer.SetCoverBitmap(bitmap);
                bitmap = null; // 所有权已移交给渲染器，由渲染器负责 Dispose
                ApplyAdaptiveControlBrush();

                if (CoverImage.Source == null)
                    CoverImage.Source = new BitmapImage(new Uri(coverPath));
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "加载音乐播放器背景封面");
                if (_backgroundRenderer != null)
                    _backgroundRenderer.SetCoverBitmap(null);
            }
            finally
            {
                // ★ 修复：未成功交接给渲染器的位图必须显式释放，防止 GPU 纹理泄漏
                bitmap?.Dispose();
                _isLoadingCover = false;
            }
        }

        private void BackgroundCanvas_CreateResources(
            IRenderSurface sender,
            FreeRunCreateResourcesEventArgs args)
        {
            _canvasResourcesReady = true;
            _fluidRenderer.LoadResources();
            // 自建渲染管线在渲染线程同步触发 CreateResources，不阻塞等待异步封面加载
            _ = ApplyBackgroundFromCurrentItemAsync();
        }

        private void BackgroundCanvas_Update(
            IRenderSurface sender,
            FreeRunUpdateEventArgs e)
        {
            _hudUpdateSw.Restart();
            try
            {
                _fluidRenderer.Update(
                    sender,
                    e.ElapsedTime,
                    _fluidColors[0],
                    _fluidColors[1],
                    _fluidColors[2],
                    _fluidColors[3]);
                _backgroundRenderer.Update(sender, e.ElapsedTime);

                // ★ 生命周期锁：与 Unloaded 的 Dispose 互斥，防止页面卸载时
                //   渲染线程访问已释放的渲染器（_lyricsRenderer 字段置空/资源释放）。
                lock (_lyricsLifecycleLock)
                {
                    if (_lyricsRenderer == null)
                        return;
                    _lyricsRenderer.CurrentProgressMs = _playback.Position.TotalMilliseconds;
                    (double lyricsX, double lyricsY, double lyricsWidth, double lyricsHeight) = GetLyricsRenderLayout(sender.Size);
                    _lyricsRenderer.Update(sender, e.ElapsedTime, lyricsX, lyricsY, lyricsWidth, lyricsHeight);
                }
            }
            catch (Exception ex)
            {
                if (!_hasLoggedLyricsUpdateError)
                {
                    _hasLoggedLyricsUpdateError = true;
                    AppLogger.Error(ex, "更新歌词渲染");
                }
            }
            finally
            {
                // 性能采集：上报画布尺寸与帧数据（帧时长 / Update 耗时 / 上帧 Draw 耗时）
                // 帧时长用 FreeRunCanvas 的真实时钟（Present 间隔），绝不伪造
                _hudUpdateSw.Stop();
                Win2DPerformanceHud.ReportSurface(
                    sender.Size.Width, sender.Size.Height, sender.DpiScale);
                Win2DPerformanceHud.ReportFrame(
                    e.ElapsedTime.TotalMilliseconds,
                    _hudUpdateSw.Elapsed.TotalMilliseconds,
                    _hudLastDrawMs);
            }
        }

        private void BackgroundCanvas_Draw(
            IRenderSurface sender,
            FreeRunDrawEventArgs args)
        {
            _hudDrawSw.Restart();
            args.DrawingSession.Clear(Windows.UI.Color.FromArgb(255, 13, 11, 16));
            try
            {
                _fluidRenderer.Draw(sender, args.DrawingSession);
                _backgroundRenderer.Draw(sender, args.DrawingSession);
            }
            catch (ObjectDisposedException)
            {
                // Ignore: can occur during page teardown when renderers are disposed
                // while a Draw call is in-flight on the render thread
            }
            args.DrawingSession.FillRectangle(
                0,
                0,
                (float)sender.Size.Width,
                (float)sender.Size.Height,
                Windows.UI.Color.FromArgb(BackgroundDimOverlayAlpha, 0, 0, 0));

            (double lyricsX, double lyricsY, double lyricsWidth, double lyricsHeight) =
                GetLyricsRenderLayout(sender.Size);

            try
            {
                // ★ 生命周期锁：与 Unloaded 的 Dispose 互斥，防止渲染线程在
                //   页面卸载期间访问已被释放的 TextLayout（ObjectDisposedException）。
                lock (_lyricsLifecycleLock)
                {
                    if (_lyricsRenderer == null)
                        return;
                    _lyricsRenderer.Draw(
                        sender,
                        args.DrawingSession,
                        lyricsX,
                        lyricsY,
                        lyricsWidth,
                        lyricsHeight,
                        _playback.Position.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                if (!_hasLoggedLyricsDrawError)
                {
                    _hasLoggedLyricsDrawError = true;
                    AppLogger.Error(ex, "绘制歌词");
                }
            }
            finally
            {
                // 性能采集：记录本帧 Draw 耗时，供下一帧 Update 一并上报
                _hudDrawSw.Stop();
                _hudLastDrawMs = _hudDrawSw.Elapsed.TotalMilliseconds;
            }
        }

        private void UpdateLyricsRenderLayout()
        {
            try
            {
                if (!RootGrid.IsLoaded || !LyricsHost.IsLoaded)
                    return;

                double width = LyricsHost.ActualWidth;
                double height = LyricsHost.ActualHeight;
                if (width <= 0 || height <= 0)
                    return;

                Rect relativeRect = LyricsHost
                    .TransformToVisual(RootGrid)
                    .TransformBounds(new Rect(0, 0, width, height));

                lock (_lyricsLayoutLock)
                {
                    _lyricsRenderStartX = relativeRect.X;
                    _lyricsRenderStartY = relativeRect.Y;
                    _lyricsRenderWidth = width;
                    _lyricsRenderHeight = height;
                    _lyricsRenderLayoutReady = true;
                }
            }
            catch (COMException ex)
            {
                AppLogger.Error(ex, "更新歌词布局");
            }
            catch (InvalidOperationException ex)
            {
                AppLogger.Error(ex, "更新歌词布局");
            }
        }

        private (double X, double Y, double Width, double Height) GetLyricsRenderLayout(Size canvasSize)
        {
            lock (_lyricsLayoutLock)
            {
                if (_lyricsRenderLayoutReady)
                {
                    double lyricsX = Math.Max(
                        _lyricsRenderStartX + 60,
                        canvasSize.Width * 0.515);
                    double lyricsWidth = Math.Max(
                        280,
                        canvasSize.Width - lyricsX - 48);

                    return (
                        lyricsX,
                        0,
                        lyricsWidth,
                        Math.Max(1, canvasSize.Height));
                }
            }

            double fallbackX = canvasSize.Width * 0.515;
            return (
                fallbackX,
                0,
                Math.Max(1, canvasSize.Width - fallbackX - 48),
                Math.Max(1, canvasSize.Height));
        }

        private void PlaybackTimer_Tick(object? sender, object e)
        {
            // ★ 安全防护：如果页面已卸载（_lyricsRenderer 已被置 null），不再更新 UI
            if (_lyricsRenderer == null)
            {
                _playbackTimer.Stop();
                return;
            }
            UpdateProgress();
        }

        private void UpdateProgress()
        {
            TimeSpan duration = _playback.Duration;
            TimeSpan position = _playback.Position;

            PositionText.Text = FormatTime(position);
            DurationText.Text = duration.TotalSeconds > 0 ? FormatTime(duration) : "00:00";

            // ★ 安全防护：_lyricsRenderer 可能在页面卸载后仍被访问
            if (_lyricsRenderer != null)
                _lyricsRenderer.CurrentProgressMs = position.TotalMilliseconds;

            if (_isDraggingProgress)
                return;

            _durationSeconds = duration.TotalSeconds > 0 ? duration.TotalSeconds : 1;
            _progressSeconds = duration.TotalSeconds > 0
                ? Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds)
                : 0;
            UpdateProgressVisual();
        }

        private void UpdatePlaybackIcon()
        {
            PlayPauseIcon.Glyph = _playback.PlaybackState == MediaPlaybackState.Playing
                ? "\uE769"
                : "\uE768";
        }

        private void UpdateVolume()
        {
            _isUpdatingVolume = true;
            VolumeSlider.Value = _playback.VolumePercent;
            VolumeText.Text = $"{Math.Round(_playback.VolumePercent):0}";
            _isUpdatingVolume = false;

            bool muted = _playback.IsMuted || _playback.VolumePercent <= 0;
            string glyph = muted ? "\uE74F" : _playback.VolumePercent < 50 ? "\uE993" : "\uE994";
            VolumeButtonIcon.Glyph = glyph;
            MuteIcon.Glyph = glyph;
            ToolTipService.SetToolTip(MuteButton, muted ? "取消静音" : "静音");
        }

        private void UpdatePlayModeIcon()
        {
            PlayModeIcon.Glyph = _playback.PlayMode switch
            {
                1 => "\uE8EE",
                2 => "\uE8B1",
                _ => "\uE8AB"
            };
            ToolTipService.SetToolTip(PlayModeButton, _playback.PlayMode switch
            {
                1 => "单曲循环",
                2 => "随机播放",
                _ => "顺序播放"
            });
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            // ★ 覆盖层内还有上一页：先返回上一页（如从音乐播放器打开的图片查看器
            //   返回时由该页面先 GoBack 回到本页，本页返回按钮仅在覆盖层第一页时
            //   关闭整个覆盖层退出播放器）
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
                return;
            }

            // 如果当前在覆盖层中，直接隐藏覆盖层（退出播放器）
            if (App.MainWindow is MainWindow mw)
            {
                mw.HidePlayerOverlay();
                return;
            }
            // 回退：传统 Frame 导航
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private async void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            await _playback.PlayAdjacentAsync(-1);
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            await _playback.PlayAdjacentAsync(1);
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            _playback.TogglePlayPause();
        }

        private void PlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            _playback.CyclePlayMode();

            // 在按钮上方显示当前播放模式提示（首次点击时创建 ToolTip 并挂到按钮上）
            string modeText = _playback.PlayMode switch
            {
                1 => "单曲循环",
                2 => "随机播放",
                _ => "顺序播放"
            };
            _playModeToolTip ??= new ToolTip { Placement = PlacementMode.Top };
            ToolTipService.SetToolTip(PlayModeButton, _playModeToolTip);
            _playModeToolTip.Content = modeText;
            _playModeToolTip.IsOpen = true;

            // 1.5 秒后自动关闭
            _playModeToolTipTimer?.Stop();
            _playModeToolTipTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            _playModeToolTipTimer.Tick += (s, e) =>
            {
                _playModeToolTipTimer.Stop();
                if (_playModeToolTip != null)
                    _playModeToolTip.IsOpen = false;
            };
            _playModeToolTipTimer.Start();
        }

        // ===================== 播放队列 Flyout =====================

        private void BuildQueueFlyout()
        {
            if (_queueFlyout != null && _isQueueFlyoutOpen)
                _queueFlyout.Hide();

            // 创建两个 ItemTemplate
            (_queueDefaultTemplate, _queueNowPlayingTemplate) = CreateQueueItemTemplates();

            // 标题栏
            var header = new Grid { Padding = new Thickness(16, 12, 16, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = "播放队列",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            var clearBtn = new Button
            {
                Content = "清除全部",
                Height = 32,
                Padding = new Thickness(8, 0, 8, 0)
            };
            clearBtn.Click += ClearQueue_Click;
            Grid.SetColumn(clearBtn, 1);
            header.Children.Add(clearBtn);

            // 空状态文本
            _queueEmptyText = new TextBlock
            {
                Text = "播放队列为空",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            // 列表控件
            _queueList = new ListView
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true
            };
            _queueList.ItemClick += QueueList_ItemClick;

            var itemContainerStyle = new Style(typeof(ListViewItem));
            itemContainerStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
            itemContainerStyle.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(16, 0, 16, 4)));
            itemContainerStyle.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            _queueList.ItemContainerStyle = itemContainerStyle;

            // 整体布局
            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.Children.Add(header);
            Grid.SetRow(_queueList, 1);
            rootGrid.Children.Add(_queueList);
            Grid.SetRow(_queueEmptyText, 1);
            rootGrid.Children.Add(_queueEmptyText);

            var border = new Border
            {
                Width = 400,
                Height = 520,
                MaxHeight = 520,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = rootGrid
            };

            // 根据软件主题设置 Flyout 边框和背景材质
            bool isDark = ActualTheme == ElementTheme.Dark;
            var flyoutBackgroundColor = isDark ? Microsoft.UI.ColorHelper.FromArgb(255, 28, 28, 28) : Microsoft.UI.ColorHelper.FromArgb(255, 249, 249, 249);
            var flyoutBorderColor = isDark ? Microsoft.UI.ColorHelper.FromArgb(255, 58, 58, 58) : Microsoft.UI.ColorHelper.FromArgb(255, 208, 208, 208);

            // 初始化播放队列卡片的交互画刷
            _queueNormalBgBrush = new SolidColorBrush(isDark ? Microsoft.UI.ColorHelper.FromArgb(255, 28, 28, 28) : Microsoft.UI.ColorHelper.FromArgb(255, 249, 249, 249));
            _queueHoverBgBrush = new SolidColorBrush(isDark ? Microsoft.UI.ColorHelper.FromArgb(255, 58, 58, 58) : Microsoft.UI.ColorHelper.FromArgb(255, 232, 232, 232));
            _queuePressedBgBrush = new SolidColorBrush(isDark ? Microsoft.UI.ColorHelper.FromArgb(255, 74, 74, 74) : Microsoft.UI.ColorHelper.FromArgb(255, 216, 216, 216));
            _queueNormalBorderBrush = new SolidColorBrush(isDark ? Microsoft.UI.ColorHelper.FromArgb(255, 58, 58, 58) : Microsoft.UI.ColorHelper.FromArgb(255, 208, 208, 208));
            _queueHoverBorderBrush = new SolidColorBrush(isDark ? Microsoft.UI.ColorHelper.FromArgb(255, 90, 90, 90) : Microsoft.UI.ColorHelper.FromArgb(255, 184, 184, 184));
            _queuePressedBorderBrush = new SolidColorBrush(isDark ? Microsoft.UI.ColorHelper.FromArgb(255, 106, 106, 106) : Microsoft.UI.ColorHelper.FromArgb(255, 168, 168, 168));

            var acrylicBrush = new Microsoft.UI.Xaml.Media.AcrylicBrush
            {
                TintColor = flyoutBackgroundColor,
                FallbackColor = flyoutBackgroundColor,
                TintOpacity = 0.78,
                TintLuminosityOpacity = isDark ? 0.5 : 0.82
            };
            border.Background = acrylicBrush;
            border.BorderBrush = new SolidColorBrush(flyoutBorderColor);

            _queueFlyout = new Flyout
            {
                Content = border,
                Placement = FlyoutPlacementMode.Top,
                FlyoutPresenterStyle = CreateFlyoutPresenterStyle()
            };

            // ★ 修复：改用具名方法订阅，避免匿名 lambda 捕获 this（页面）导致页面泄漏。
            //   Flyout 打开后会被系统弹出层（Popup）强引用，若订阅者持有页面，
            //   页面在卸载后无法被 GC 回收（Win2D GPU 资源随之泄漏）。
            _queueFlyout.Opening += QueueFlyout_Opening;
            _queueFlyout.Closed += QueueFlyout_Closed;

            QueueButton.Flyout = _queueFlyout;
        }

        private void QueueFlyout_Opening(object? sender, object e)
        {
            _isQueueFlyoutOpen = true;
            RefreshQueueItems();
            _queueList.ItemTemplateSelector = new QueueTemplateSelector
            {
                DefaultTemplate = _queueDefaultTemplate!,
                NowPlayingTemplate = _queueNowPlayingTemplate!
            };

            // 如果正在播放，启动均衡器
            if (_playback.ActiveItem != null
                && _playback.PlaybackState == MediaPlaybackState.Playing)
                StartQueueEqualizerAnimation();
        }

        private void QueueFlyout_Closed(object? sender, object e)
        {
            _isQueueFlyoutOpen = false;
            StopQueueEqualizerAnimation();
        }

        private static Style CreateFlyoutPresenterStyle()
        {
            var style = new Style(typeof(FlyoutPresenter));
            style.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
            style.Setters.Add(new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
            return style;
        }

        private (DataTemplate defaultTemplate, DataTemplate nowPlayingTemplate) CreateQueueItemTemplates()
        {
            // 直接从 XAML 资源获取，避免 XamlReader.Load 导致的启动死锁和资源查找问题（尤其在 Flyout/Popup 中事件处理器无法触发）
            var defaultTemplate = (DataTemplate)Resources["QueueItemDefaultTemplate"];
            var nowPlayingTemplate = (DataTemplate)Resources["QueueItemNowPlayingTemplate"];
            return (defaultTemplate, nowPlayingTemplate);
        }

        private void RefreshQueueItems()
        {
            var items = GetDisplayQueueItems();
            _queueList.ItemsSource = items;
            _queueEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 播放队列卡片交互事件处理
        public void QueueItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = _queueHoverBgBrush;
                border.BorderBrush = _queueHoverBorderBrush;
            }
        }

        public void QueueItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = _queueNormalBgBrush;
                border.BorderBrush = _queueNormalBorderBrush;
            }
        }

        public void QueueItem_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = _queuePressedBgBrush;
                border.BorderBrush = _queuePressedBorderBrush;
            }
        }

        public void QueueItem_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.ReleasePointerCapture(e.Pointer);
                var point = e.GetCurrentPoint(border);
                bool isInside = point.Position.X >= 0 && point.Position.X <= border.ActualWidth &&
                                point.Position.Y >= 0 && point.Position.Y <= border.ActualHeight;
                if (isInside)
                {
                    border.Background = _queueHoverBgBrush;
                    border.BorderBrush = _queueHoverBorderBrush;
                }
                else
                {
                    border.Background = _queueNormalBgBrush;
                    border.BorderBrush = _queueNormalBorderBrush;
                }
            }
        }

        public void QueueItem_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.ReleasePointerCapture(e.Pointer);
                border.Background = _queueNormalBgBrush;
                border.BorderBrush = _queueNormalBorderBrush;
            }
        }

        private IReadOnlyList<MediaItem> GetDisplayQueueItems()
        {
            var queue = _playback.HasExternalPlayback ? _playback.ExternalPlayQueue : _playback.PlayQueue;
            if (queue.Count <= 1)
                return queue;

            int idx = _playback.HasExternalPlayback ? _playback.ExternalCurrentIndex : _playback.CurrentIndex;
            if (idx < 0 || idx >= queue.Count)
                return queue;

            var items = new List<MediaItem>();
            for (int i = idx; i < queue.Count; i++)
                items.Add(queue[i]);
            return items;
        }

        // ===================== 队列均衡器动画 =====================

        private void StartQueueEqualizerAnimation()
        {
            if (_queueEqualizerRunning) return;

            StopQueueEqualizerAnimation();

            DispatcherQueue.TryEnqueue(() =>
            {
                _queueList.UpdateLayout();
                FindQueueEqualizerElements();
                if (_queueBarVisuals[0] == null) return;

                _queueEqualizerRunning = true;
                _queueEqualizerStopwatch.Restart();
                CompositionTarget.Rendering += OnQueueEqualizerFrame;
                ResourceDiagnosticsService.RegisterRenderingHandler(); // ★ 诊断
            });
        }

        private void StopQueueEqualizerAnimation()
        {
            _queueEqualizerRunning = false;
            _queueEqualizerStopwatch.Stop();
            CompositionTarget.Rendering -= OnQueueEqualizerFrame;
            ResourceDiagnosticsService.UnregisterRenderingHandler(); // ★ 诊断

            for (int i = 0; i < 5; i++)
            {
                if (_queueBarVisuals[i] != null)
                    _queueBarVisuals[i].Scale = Vector3.One;
                _queueBarVisuals[i] = null!;
            }
        }

        private void OnQueueEqualizerFrame(object? sender, object e)
        {
            if (!_queueEqualizerRunning) return;
            var elapsed = _queueEqualizerStopwatch.Elapsed.TotalSeconds;
            for (int i = 0; i < 5; i++)
            {
                if (_queueBarVisuals[i] == null) continue;
                float distance = Math.Abs(i - 2);
                double phase = elapsed * Math.PI * 2 - distance * 0.8;
                float scaleY = (float)(0.3 + (Math.Sin(phase) + 1) * 0.35);
                _queueBarVisuals[i].Scale = new Vector3(1, scaleY, 1);
            }
        }

        private void FindQueueEqualizerElements()
        {
            if (_queueBarVisuals[0] != null) return;

            if (_queueList.ContainerFromIndex(0) is not ListViewItem container) return;
            var host = FindNamedElement(container, "QueueEqualizerHost");
            if (host == null) return;

            int childCount = VisualTreeHelper.GetChildrenCount(host);
            int barIndex = 0;
            for (int i = 0; i < childCount && barIndex < 5; i++)
            {
                if (VisualTreeHelper.GetChild(host, i) is Rectangle rect)
                {
                    _queueBarVisuals[barIndex] = ElementCompositionPreview.GetElementVisual(rect);
                    _queueBarVisuals[barIndex].CenterPoint = new Vector3(1.5f, 7, 0);
                    barIndex++;
                }
            }
        }

        private static DependencyObject? FindNamedElement(DependencyObject root, string name)
        {
            if (root is FrameworkElement fe && fe.Name == name)
                return fe;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var result = FindNamedElement(VisualTreeHelper.GetChild(root, i), name);
                if (result != null)
                    return result;
            }
            return null;
        }

        // ===================== 队列操作 =====================

        private async void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_playback.ActiveItem != null && _playback.PlaybackState == MediaPlaybackState.Playing)
            {
                var dialog = new ContentDialog
                {
                    Title = "确认清除",
                    Content = "当前正在播放音乐，确定要清除全部吗？",
                    CloseButtonText = "取消",
                    PrimaryButtonText = "确定",
                    XamlRoot = XamlRoot
                };
                var result = await DialogService.ShowAsync(dialog, XamlRoot, applyTheme: false);
                if (result != ContentDialogResult.Primary)
                    return;
            }

            // ★ 修复：页面卸载后 _queueFlyout 可能已置 null（断开引用），此时直接忽略
            _queueFlyout?.Hide();
            _playback.StopPlayback();
            _playback.ClearExternalPlayback();
        }

        private async void QueueList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MediaItem item)
                return;

            // ★ 修复：页面卸载后 _queueFlyout 可能已置 null（断开引用），此时直接忽略
            _queueFlyout?.Hide();

            if (_playback.HasExternalPlayback)
            {
                var list = _playback.ExternalPlayQueue.ToList();
                int index = list.FindIndex(
                    m => m.FilePath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    await _playback.PlayAdjacentAsync(index - _playback.ExternalCurrentIndex);
            }
            else
            {
                await _playback.PlayAsync(item, _playback.PlayQueue);
            }
        }

        // ===================== 播放队列数据绑定 =====================

        private void Playback_QueueChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isQueueFlyoutOpen)
                    RefreshQueueItems();
            });
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdatingVolume)
                return;

            _playback.SetVolumePercent(e.NewValue);
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            _playback.ToggleMute();
        }

        private bool _settingsDialogDark;

        private async void PlayerSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _settingsDialogDark = ActualTheme == ElementTheme.Dark;
            // 弹窗外框（标题/关闭按钮/固定底色）由 PlayerSettingsDialogHelper 统一构建，
            // 与视频播放器共用同一套外观。
            await PlayerSettingsDialogHelper.ShowPlayerSettingsDialogAsync(
                XamlRoot, _settingsDialogDark, BuildPlayerSettingsContent);
        }

        private UIElement BuildPlayerSettingsContent(ContentDialog owner)
        {
            const double DialogContentWidth = 520;

            // 顶部：居中标题 + 右上角关闭按钮（公共构建，与视频播放器一致）
            var header = PlayerSettingsDialogHelper.BuildDialogHeader(
                _settingsDialogDark, () => owner.Hide());

            // 胶囊分段 Tab 栏（仅"常规"一个分段，与视频播放器共用 PlayerSettingsDialogHelper）
            var selectorBarHost = PlayerSettingsDialogHelper.BuildSegmentBar(_settingsDialogDark);

            // 内容承载区
            UIElement lyricsPage = BuildLyricsSettingsContent();

            var contentHost = new Grid
            {
                // 容纳展开后的歌词源设置（内部 ScrollViewer 可滚动）。
                Height = 300
            };
            contentHost.Children.Add(lyricsPage);

            var root = new StackPanel
            {
                Width = DialogContentWidth
            };
            root.Children.Add(header);
            root.Children.Add(selectorBarHost);
            root.Children.Add(contentHost);

            return root;
        }

        private UIElement BuildLyricsSettingsContent()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(1, 1, 2, 1)
            };
            var panel = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            panel.Children.Add(BuildNetworkLyricsExpander());
            // 音频输出设备卡片由 PlayerSettingsDialogHelper 统一构建（与视频播放器共用），
            // 此处注入音乐播放器的设备保存与应用逻辑。
            panel.Children.Add(PlayerSettingsDialogHelper.BuildAudioOutputExpander(
                App.SettingsHelper.MusicOutputDeviceId ?? string.Empty,
                deviceId =>
                {
                    App.SettingsHelper.MusicOutputDeviceId = deviceId;
                    App.SettingsHelper.Save();
                    AppLogger.Info($"音乐播放器输出设备切换：" +
                        (string.IsNullOrEmpty(deviceId) ? "跟随系统默认设备" : deviceId));
                },
                deviceId => _playback.ApplyAudioDeviceAsync(deviceId)));
            panel.Children.Add(BuildLyricsDelayExpander());

            scroll.Content = panel;
            return scroll;
        }

        /// <summary>
        /// "歌词延迟"设置卡片：
        /// 手动微调当前歌曲歌词与声音的同步偏移（毫秒）。卡片右侧为调节行——
        /// 左侧"减 100ms"按钮 + 中间当前值（默认 0ms，点击可直接输入具体数值）+ 右侧"加 100ms"按钮。
        /// 语义：加延迟（正值）→ 歌词回退显示（歌词偏快时使用）；减延迟（负值）→ 歌词前进显示（歌词偏慢时使用）。
        /// 延迟仅对当前歌曲生效（<see cref="LyricsDelayStore"/> 按歌曲记忆），
        /// 换歌自动恢复 0ms，切回原歌时恢复该歌设置的延迟。
        /// 调整立即应用到歌词渲染器（<see cref="CanvasLyricsRenderer.UserDelayMs"/>）。
        /// </summary>
        private SettingsCard BuildLyricsDelayExpander()
        {
            const int MaxDelayMs = 10000; // 允许的最大/最小延迟绝对值（毫秒）

            // 当前歌曲的延迟记录（无记录默认 0ms）
            MediaItem? currentItem = _playback.CurrentItem ?? _playerArgs?.CurrentItem;
            int currentDelay = LyricsDelayStore.GetDelay(currentItem?.FilePath);

            // 中间数值显示（默认 0ms）：点击进入编辑模式
            var delayText = new TextBlock
            {
                Text = $"{currentDelay} ms",
                Width = 76,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // 手动输入编辑框（默认隐藏，回车/失焦提交）
            var delayInput = new TextBox
            {
                Text = currentDelay.ToString(),
                Width = 76,
                Visibility = Visibility.Collapsed,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            void ShowInput(bool show)
            {
                delayInput.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                delayText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            }

            // 应用延迟值：仅对当前歌曲生效（按歌曲记忆）、更新渲染器（立即生效）、刷新显示
            void ApplyDelay(int newValue)
            {
                int clamped = Math.Clamp(newValue, -MaxDelayMs, MaxDelayMs);
                MediaItem? item = _playback.CurrentItem ?? _playerArgs?.CurrentItem;
                LyricsDelayStore.SetDelay(item?.FilePath, clamped);
                if (_lyricsRenderer != null)
                    _lyricsRenderer.UserDelayMs = clamped;
                delayText.Text = $"{clamped} ms";
                delayInput.Text = clamped.ToString();
                ShowInput(false);
                AppLogger.Info($"歌词延迟调整为 {clamped} ms（{item?.Title ?? "无播放"}）");
            }

            // 提交手动输入：非法输入时恢复显示当前值
            void CommitInput()
            {
                if (int.TryParse(delayInput.Text.Trim(), out int parsed))
                    ApplyDelay(parsed);
                else
                    ShowInput(false);
            }

            // 点击数值 → 进入编辑模式并全选已有数值
            delayText.PointerPressed += (_, e) =>
            {
                ShowInput(true);
                delayInput.Focus(FocusState.Programmatic);
                delayInput.SelectAll();
                e.Handled = true;
            };
            delayInput.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    CommitInput();
                    e.Handled = true;
                }
                else if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    // 取消编辑，恢复显示当前值
                    ShowInput(false);
                    e.Handled = true;
                }
            };
            delayInput.LostFocus += (_, _) => CommitInput();

            // 加减按钮（使用手型光标按钮）
            var minusButton = new PlayerSettingsDialogHelper.SegmentButton
            {
                Content = "−100ms",
                Width = 72,
                Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            minusButton.Click += (_, _) =>
                ApplyDelay(LyricsDelayStore.GetDelay(currentItem?.FilePath) - 100);
            ToolTipService.SetToolTip(minusButton, "减少延迟，歌词前进");

            var plusButton = new PlayerSettingsDialogHelper.SegmentButton
            {
                Content = "+100ms",
                Width = 72,
                Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            plusButton.Click += (_, _) =>
                ApplyDelay(LyricsDelayStore.GetDelay(currentItem?.FilePath) + 100);
            ToolTipService.SetToolTip(plusButton, "增加延迟，歌词回退");

            var delayRow = new Grid
            {
                ColumnSpacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            delayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            delayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            delayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(minusButton, 0);
            Grid.SetColumn(delayText, 1);
            Grid.SetColumn(delayInput, 1);
            Grid.SetColumn(plusButton, 2);
            delayRow.Children.Add(minusButton);
            delayRow.Children.Add(delayText);
            delayRow.Children.Add(delayInput);
            delayRow.Children.Add(plusButton);

            var card = new SettingsCard
            {
                Header = "歌词延迟",
                Description = "部分歌曲歌词可能不同步，可手动纠正。",
                HeaderIcon = new FontIcon
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"),
                    FontSize = 16,
                    Glyph = "\uE823" // 计时器（时间微调）
                },
                Content = delayRow,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            // 窄弹窗下保持调节行在头部右侧，避免换行挤压布局
            card.Resources["SettingsCardWrapThreshold"] = 200.0;
            card.Resources["SettingsCardWrapNoIconThreshold"] = 160.0;
            return card;
        }

        /// <summary>
        /// 可展开的"使用网络歌词源"设置卡片：
        /// 复用软件设置页"外观设置 → 背景材质"卡片的样式
        /// （CommunityToolkit SettingsExpander + SettingsCard）：
        /// 头部为标题 + 描述 + 图标 + 主开关（Content 位）；展开后显示歌词源选择卡片。
        /// 弹窗宽度较窄，会触发 SettingsCard 的窄宽度换行布局（Content 被挤到标题下方），
        /// 因此通过覆盖 WrapThreshold 资源将断点调低，让主开关保持在头部右侧。
        /// </summary>
        private SettingsExpander BuildNetworkLyricsExpander()
        {
            // 主开关：位于 SettingsExpander 的 Content 位，即头部右侧
            // （与外观设置页"背景材质"卡片的 ComboBox 位置一致）。
            var toggle = new ToggleSwitch
            {
                IsOn = App.SettingsHelper.MusicUseNetworkLyricsSource,
                MinWidth = 0,
                OnContent = string.Empty,
                OffContent = string.Empty,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 歌词源选择（展开区设置卡片，对应背景材质卡片中"内容区域保持云母"设置项）
            var sourceCombo = new ComboBox
            {
                MinWidth = 168,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            var sourceOptions = new (string Name, NetworkLyricsSourcePreference Value)[]
            {
                ("自动匹配（推荐）", NetworkLyricsSourcePreference.Auto),
                ("QQ 音乐", NetworkLyricsSourcePreference.QQ),
                ("网易云音乐", NetworkLyricsSourcePreference.Netease),
                ("酷狗音乐", NetworkLyricsSourcePreference.Kugou),
                ("LrcLib", NetworkLyricsSourcePreference.LrcLib),
            };
            foreach (var (name, value) in sourceOptions)
                sourceCombo.Items.Add(new ComboBoxItem { Content = name, Tag = value });

            NetworkLyricsSourcePreference sourcePref =
                NetworkLyricsService.ParsePreference(App.SettingsHelper.MusicLyricsSourcePreference);
            for (int i = 0; i < sourceCombo.Items.Count; i++)
            {
                if ((NetworkLyricsSourcePreference)((ComboBoxItem)sourceCombo.Items[i]).Tag == sourcePref)
                {
                    sourceCombo.SelectedIndex = i;
                    break;
                }
            }
            sourceCombo.SelectionChanged += (_, _) =>
            {
                // 初始化时（未加入视觉树）设置的 SelectedIndex 也会触发，需跳过。
                if (!sourceCombo.IsLoaded ||
                    sourceCombo.SelectedItem is not ComboBoxItem item ||
                    item.Tag is not NetworkLyricsSourcePreference value)
                    return;

                App.SettingsHelper.MusicLyricsSourcePreference = value.ToString();
                App.SettingsHelper.Save();

                // 立即用新源重新拉取当前曲目歌词（缓存 key 含源偏好，会自动重新检索）。
                MediaItem? current = _playback.CurrentItem ?? _playerArgs?.CurrentItem;
                if (current != null)
                    _ = LoadLyricsAsync(current, forceReload: true);
            };

            // 可展开设置卡片主体：结构与外观设置页"背景材质"卡片一致。
            // 弹窗宽度较窄会触发 SettingsCard 的窄宽度换行布局（Content 被挤到标题下方），
            // 通过覆盖 WrapThreshold 资源把换行断点调低，让主开关保持右侧、不换行。
            var expander = new SettingsExpander
            {
                Header = "使用网络歌词源",
                Description = "启用后优先从网络获取歌词，展开可固定歌词源",
                HeaderIcon = new FontIcon
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"),
                    FontSize = 16,
                    Glyph = "\uE774" // 地球（网络）
                },
                Content = toggle, // 主开关显示在头部右侧
                // 首次进入默认收起，由用户手动展开（或打开主开关时自动展开）。
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            // 覆盖 CommunityToolkit 设置卡片的自适应换行断点（模板内通过 ThemeResource 动态查找）：
            // 将阈值调低，使弹窗宽度（约 500）下不再进入 RightWrapped 窄宽度布局，
            // 保证头部 Content（主开关）始终显示在右侧。两类键同时覆盖以兼容头部内部卡片。
            expander.Resources["SettingsCardWrapThreshold"] = 200.0;
            expander.Resources["SettingsCardWrapNoIconThreshold"] = 160.0;
            expander.Resources["SettingsExpanderWrapThreshold"] = 200.0;
            expander.Resources["SettingsExpanderWrapNoIconThreshold"] = 160.0;

            // 展开区：歌词源设置卡片（对应背景材质卡片的 Items 区）。
            // 省略 Description，避免窄弹窗内副描述换行；同时覆盖换行断点，让下拉保持右侧。
            var sourceCard = new SettingsCard
            {
                Header = "歌词源",
                Content = sourceCombo
            };
            sourceCard.Resources["SettingsCardWrapThreshold"] = 200.0;
            sourceCard.Resources["SettingsCardWrapNoIconThreshold"] = 160.0;
            expander.Items.Add(sourceCard);

            // 主开关与展开状态联动：开启自动展开，关闭自动收起。
            toggle.Toggled += (_, _) =>
            {
                App.SettingsHelper.MusicUseNetworkLyricsSource = toggle.IsOn;
                App.SettingsHelper.Save();

                expander.IsExpanded = toggle.IsOn;

                // 立即对当前曲目应用新设置：开启时可补拉网络歌词。
                MediaItem? current = _playback.CurrentItem ?? _playerArgs?.CurrentItem;
                if (current != null)
                    _ = LoadLyricsAsync(current, forceReload: true);
            };

            // 直接点击头部展开时（主开关仍关闭），先打开主开关保持状态一致。
            expander.Expanded += (_, _) =>
            {
                if (!toggle.IsOn)
                    toggle.IsOn = true; // 触发 Toggled 联动保存
            };

            return expander;
        }

        /// <summary>
        /// 简洁设置行：左侧标题 + 描述，右侧控件（开关/下拉等），无卡片背景。
        /// 用于展开设置区域，保持界面轻量。
        /// </summary>
        private Grid BuildSettingRow(string title, string description, FrameworkElement control)
        {
            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            var descriptionText = new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = PlayerSettingsDialogHelper.ThemedBrush(
                    _settingsDialogDark, "TextFillColorSecondaryBrush", 0xFF, 0xFF, 0xFF, 0xB0),
                TextWrapping = TextWrapping.Wrap
            };

            var textStack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            textStack.Children.Add(titleText);
            textStack.Children.Add(descriptionText);

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(textStack, 0);
            Grid.SetColumn(control, 1);
            grid.Children.Add(textStack);
            grid.Children.Add(control);
            return grid;
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();

            var speedMenu = new MenuFlyoutSubItem { Text = "播放速度" };
            foreach (double speed in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            {
                var speedItem = new ToggleMenuFlyoutItem
                {
                    Text = $"{speed:0.##}x",
                    IsChecked = Math.Abs(_playback.Player.PlaybackSession.PlaybackRate - speed) < 0.01
                };
                speedItem.Click += (_, _) => _playback.Player.PlaybackSession.PlaybackRate = speed;
                speedMenu.Items.Add(speedItem);
            }
            menu.Items.Add(speedMenu);

            var location = new MenuFlyoutItem
            {
                Text = "打开文件所在位置",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uED25" },
                IsEnabled = _playback.CurrentItem != null
            };
            location.Click += (_, _) =>
            {
                if (_playback.CurrentItem != null)
                    MusicItemMenuHelper.OpenFileLocation(_playback.CurrentItem);
            };
            menu.Items.Add(location);

            var properties = new MenuFlyoutItem
            {
                Text = "属性",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE946" },
                IsEnabled = _playback.CurrentItem != null
            };
            properties.Click += async (_, _) =>
            {
                if (_playback.CurrentItem != null)
                    await ShowPropertiesAsync(_playback.CurrentItem);
            };
            menu.Items.Add(properties);

            var saveLyrics = new MenuFlyoutItem
            {
                Text = "保存当前歌词文件",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE74E" },
                IsEnabled = _currentLyricsData is { LyricsLines.Count: > 0 }
            };
            saveLyrics.Click += async (_, _) => await SaveCurrentLyricsAsync();
            menu.Items.Add(saveLyrics);

            menu.ShowAt(MoreButton);
        }

        /// <summary>
        /// 当前歌词的原始内容载体：<see cref="RawText"/> 为歌词源获取到的未经解析的原始歌词文本
        /// （网络歌词源的 LRC/QRC/KRC/YRC 原文，或本地歌词文件/嵌入标签的原文），
        /// <see cref="Extension"/> 为推断的默认文件扩展名，<see cref="Description"/> 用于日志与提示。
        /// </summary>
        private sealed record LyricsSourceInfo(string RawText, string Extension, string Description);

        /// <summary>
        /// 保存当前使用的歌词原文件。
        /// 勾选"使用网络歌词"时保存歌词源（QQ/网易/酷狗/LrcLib）获取到的原始歌词文本，
        /// 否则保存本地歌词文件（或音频嵌入标签）的原始文本。均不做解析与渲染处理。
        /// </summary>
        private async Task SaveCurrentLyricsAsync()
        {
            MediaItem? item = _playback.CurrentItem ?? _playerArgs?.CurrentItem;
            if (item == null)
            {
                AppLogger.Info("保存当前歌词：当前无播放曲目");
                return;
            }

            LyricsSourceInfo? source = await ResolveCurrentRawLyricsAsync(item);
            if (source == null)
            {
                AppLogger.Info("保存当前歌词：暂无歌词可保存");
                var noLyricsDialog = new ContentDialog
                {
                    Title = "保存歌词",
                    Content = "当前曲目没有可保存的歌词。",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot
                };
                await DialogService.ShowAsync(noLyricsDialog, XamlRoot);
                return;
            }

            var picker = new FileSavePicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
            picker.FileTypeChoices.Add("歌词文件", new List<string> { source.Extension });
            picker.SuggestedFileName = SanitizeFileName(item.Title ?? "歌词") + source.Extension;

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                AppLogger.Info("保存当前歌词：用户取消");
                return;
            }

            // 原样写入原始歌词文本，不做任何解析/转换
            await FileIO.WriteTextAsync(file, source.RawText, Windows.Storage.Streams.UnicodeEncoding.Utf8);
            AppLogger.Info($"保存当前歌词文件成功: {file.Path}（来源：{source.Description}）");

            var successDialog = new ContentDialog
            {
                Title = "保存歌词",
                Content = $"歌词已保存到：{file.Path}\r\n来源：{source.Description}",
                CloseButtonText = "确定",
                XamlRoot = XamlRoot
            };
            await DialogService.ShowAsync(successDialog, XamlRoot);
        }

        /// <summary>
        /// 解析当前曲目实际使用的歌词来源并返回其原始文本，选择逻辑与 <see cref="LoadLyricsAsync"/> 一致：
        /// 勾选"使用网络歌词"时优先网络歌词源（缓存命中则直接返回原文），网络无结果时回退本地；
        /// 未勾选时仅本地歌词（文件系统优先，其次音频嵌入标签）。
        /// </summary>
        private async Task<LyricsSourceInfo?> ResolveCurrentRawLyricsAsync(MediaItem item)
        {
            double? duration = _playback.Duration.TotalSeconds > 0
                ? _playback.Duration.TotalSeconds
                : item.Duration?.TotalSeconds;

            // 网络歌词源优先：保存歌词源获取到的原始歌词文件
            if (App.SettingsHelper.MusicUseNetworkLyricsSource)
            {
                NetworkLyricsCandidate? candidate = await NetworkLyricsService.FetchRawAsync(
                    item,
                    duration,
                    NetworkLyricsService.ParsePreference(App.SettingsHelper.MusicLyricsSourcePreference));
                if (candidate is { HasLyrics: true })
                {
                    string extension = GuessNetworkLyricsExtension(candidate.Provider, candidate.Raw!);
                    return new LyricsSourceInfo(candidate.Raw!, extension, $"网络歌词（{candidate.Provider}）");
                }
            }

            // 回退/直接使用本地歌词：返回歌词文件原文或嵌入标签原文
            LocalLyricsService.LocalRawLyrics? local = await LocalLyricsService.GetRawLocalLyricsAsync(item);
            if (local != null)
            {
                string extension = local.SourcePath != null
                    ? System.IO.Path.GetExtension(local.SourcePath)
                    : ".lrc";
                string description = local.IsEmbedded
                    ? "音频文件嵌入标签"
                    : $"本地歌词文件（{local.SourcePath}）";
                return new LyricsSourceInfo(local.RawText, extension, description);
            }

            return null;
        }

        /// <summary>
        /// 根据网络歌词源与原文内容推断合适的文件扩展名：
        /// 酷狗为 KRC，网易云为逐字 YRC（内容带音节标记）或 LRC，其余默认为 LRC。
        /// </summary>
        private static string GuessNetworkLyricsExtension(NetworkLyricsProvider provider, string raw) =>
            provider switch
            {
                NetworkLyricsProvider.Kugou => ".krc",
                NetworkLyricsProvider.Netease when LooksLikeYrc(raw) => ".yrc",
                _ => ".lrc"
            };

        /// <summary>检测原文是否为网易云 YRC 逐字歌词格式（行内含音节时间标记，如 (0,150,0)）。</summary>
        private static bool LooksLikeYrc(string raw)
        {
            const string yrcSyllablePattern = @"\[\d{1,2}:\d{2}[\.:]\d{1,3}\][^\[\r\n]*\(\d+,\d+,\d+\)";
            return !string.IsNullOrWhiteSpace(raw) &&
                   Regex.IsMatch(raw, yrcSyllablePattern, RegexOptions.CultureInvariant);
        }

        /// <summary>清理文件名中的非法字符，空结果回退为"歌词"。</summary>
        private static string SanitizeFileName(string name)
        {
            char[] invalidChars = System.IO.Path.GetInvalidFileNameChars();
            var safe = new string((name ?? string.Empty).Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "歌词" : safe;
        }

        private void LyricsHost_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // ★ 安全：页面卸载后可能仍有事件残留
            if (_lyricsRenderer == null) return;
            Point point = e.GetCurrentPoint(RootGrid).Position;
            ProtectedCursor = _lyricsRenderer.TryGetLineStartAt(point, out _)
                ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand)
                : null;
        }

        private void LyricsHost_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ProtectedCursor = null;
            if (_lyricsRenderer == null) return;
            _lyricsRenderer.ClearHoverLine();
        }

        private void LyricsHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_lyricsRenderer == null) return;
            int wheelDelta = e.GetCurrentPoint(LyricsHost).Properties.MouseWheelDelta;
            if (wheelDelta == 0)
                return;

            _lyricsRenderer.ScrollBy(wheelDelta * 0.45);
            e.Handled = true;
        }

        private void LyricsHost_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_lyricsRenderer == null) return;
            Point point = e.GetCurrentPoint(RootGrid).Position;
            if (!_lyricsRenderer.TryGetLineStartAt(point, out TimeSpan start))
                return;

            _playback.SetPosition(start);
            _lyricsRenderer.JumpToLyricsTime(start.TotalMilliseconds);
            _lyricsRenderer.ResumeAutoFollow(animated: true);
            UpdateProgress();
            e.Handled = true;
        }

        private void BottomControlsHost_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _controlsHideTimer.Stop();
            BottomControlsHost.Opacity = 1;
        }

        private void BottomControlsHost_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingProgress)
                return;

            _controlsHideTimer.Stop();
            _controlsHideTimer.Start();
        }

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            BottomControlsHost.Opacity = 1;
            _controlsHideTimer.Stop();
            _isDraggingProgress = true;
            ProgressTrack.CapturePointer(e.Pointer);
            SetProgressFromPointer(e, commit: true);
            SetProgressDragVisual(true);
        }

        private void ProgressSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingProgress)
            {
                SetProgressFromPointer(e, commit: true);
                return;
            }

            UpdateProgressTooltip(e);
        }

        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            SetProgressFromPointer(e, commit: true);
            ProgressTrack.ReleasePointerCapture(e.Pointer);
            _isDraggingProgress = false;
            SetProgressDragVisual(false);
        }

        private void ProgressSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            ProgressTrack.ReleasePointerCapture(e.Pointer);
            _isDraggingProgress = false;
            SetProgressDragVisual(false);
            UpdateProgress();
        }

        private void ProgressSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Hand);
            ProgressPreviewFill.Opacity = 1;
            UpdateProgressTooltip(e);
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingProgress)
            {
                ProgressPreviewFill.Opacity = 0;
                ProgressTimeTip.Opacity = 0;
            }

            ProtectedCursor = null;
        }

        private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateProgressVisual();
        }

        private void SetProgressFromPointer(PointerRoutedEventArgs e, bool commit)
        {
            double width = Math.Max(1, ProgressTrack.ActualWidth);
            double x = e.GetCurrentPoint(ProgressTrack).Position.X;
            double ratio = Math.Clamp(x / width, 0, 1);
            _progressSeconds = ratio * _durationSeconds;

            TimeSpan target = TimeSpan.FromSeconds(_progressSeconds);
            PositionText.Text = FormatTime(target);
            UpdateProgressVisual();
            UpdateProgressTooltip(e);

            if (commit)
            {
                _playback.SetPosition(target);
                if (_lyricsRenderer != null)
                {
                    _lyricsRenderer.JumpToLyricsTime(target.TotalMilliseconds);
                    _lyricsRenderer.ResumeAutoFollow(animated: true);
                }
            }
        }

        private void UpdateProgressVisual()
        {
            double ratio = _durationSeconds > 0
                ? Math.Clamp(_progressSeconds / _durationSeconds, 0, 1)
                : 0;
            ProgressFill.Width = ProgressTrack.ActualWidth * ratio;
        }

        private void UpdateProgressTooltip(PointerRoutedEventArgs e)
        {
            double width = Math.Max(1, ProgressTrack.ActualWidth);
            double x = e.GetCurrentPoint(ProgressTrack).Position.X;
            double ratio = Math.Clamp(x / width, 0, 1);
            double seconds = ratio * _durationSeconds;
            ProgressPreviewFill.Width = ProgressTrack.ActualWidth * ratio;
            string text = FormatTime(TimeSpan.FromSeconds(seconds));
            ProgressTimeTipText.Text = text;
            ProgressTimeTip.Opacity = 1;

            double tipWidth = Math.Max(46, ProgressTimeTip.ActualWidth);
            double left = Math.Clamp(
                ProgressTrack.ActualWidth * ratio - tipWidth / 2,
                0,
                Math.Max(0, ProgressTrack.ActualWidth - tipWidth));
            ProgressTimeTip.Margin = new Thickness(left, 0, 0, 0);
        }

        private void SetProgressDragVisual(bool isDragging)
        {
            ProgressTrackRail.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(isDragging ? (byte)0x82 : (byte)0x66, 255, 255, 255));
            ProgressFill.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(isDragging ? (byte)0xFF : (byte)0xF0, 255, 255, 255));
        }

        private void ApplyAdaptiveControlBrush()
        {
            double luminance = _fluidColors
                .Select(color => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0)
                .DefaultIfEmpty(0)
                .Average();
            Windows.UI.Color iconColor = luminance > 0.58
                ? Windows.UI.Color.FromArgb(255, 18, 18, 18)
                : Windows.UI.Color.FromArgb(255, 255, 255, 255);

            var brush = new SolidColorBrush(iconColor);
            Resources["PlayerControlIconBrush"] = brush;
            foreach (FontIcon icon in EnumerateVisualChildren<FontIcon>(RootGrid))
                icon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        }

        private static IEnumerable<T> EnumerateVisualChildren<T>(DependencyObject parent)
            where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T target)
                    yield return target;

                foreach (T descendant in EnumerateVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        private void Playback_CurrentItemChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_playback.CurrentItem != null)
                    UpdateSongInfo(_playback.CurrentItem);
                // 换歌：恢复该歌曲的歌词延迟（无记录则回 0ms）
                ApplyLyricsDelayForCurrentItem();
                _ = ApplyBackgroundFromCurrentItemAsync();

                if (_isQueueFlyoutOpen)
                {
                    RefreshQueueItems();
                    if (_playback.ActiveItem != null && _playback.PlaybackState == MediaPlaybackState.Playing)
                        StartQueueEqualizerAnimation();
                    else
                        StopQueueEqualizerAnimation();
                }
            });
        }

        private void Playback_PlaybackStateChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdatePlaybackIcon();
                if (_isQueueFlyoutOpen)
                {
                    if (_playback.PlaybackState == MediaPlaybackState.Playing && _playback.ActiveItem != null)
                        StartQueueEqualizerAnimation();
                    else
                        StopQueueEqualizerAnimation();
                }
            });
        }

        private void Playback_VolumeChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(UpdateVolume);
        }

        private void Playback_PlayModeChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(UpdatePlayModeIcon);
        }

        private void Playback_PlaybackFailed(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PlayPauseIcon.Glyph = "\uE768";
            });
        }

        private static string FormatTime(TimeSpan time)
        {
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss")
                : time.ToString(@"mm\:ss");
        }

        private async Task ShowPropertiesAsync(MediaItem item)
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = $"标题：{item.Title}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"艺术家：{item.ArtistDisplay}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"专辑：{item.AlbumDisplay}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"时长：{item.DurationText}" });
            content.Children.Add(new TextBlock { Text = $"大小：{item.FileSizeText}" });
            content.Children.Add(new TextBlock { Text = $"路径：{item.FilePath}", TextWrapping = TextWrapping.Wrap });

            var dialog = new ContentDialog
            {
                Title = "音乐属性",
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot
            };
            await DialogService.ShowAsync(dialog, XamlRoot);
        }
    }
}
