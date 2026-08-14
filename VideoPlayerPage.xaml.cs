using FFmpegInteropX;
using SightoHear.Models;
using SightoHear.Mpv;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.System.Display;
using WinRT.Interop;
using SightoHear.Helpers;
using Microsoft.UI.Input;
using System.Reflection;
using Windows.Devices.Enumeration;

namespace SightoHear
{
    public sealed partial class VideoPlayerPage : Page
    {
        private MediaItem? _currentItem;
        private List<MediaItem> _playlist = new();
        private int _currentIndex = -1;
        private DispatcherTimer? _positionTimer;
        private DispatcherTimer? _introTimer;
        private DispatcherTimer? _delayedHideTimer;
        private double _lastPointerY = double.NaN;
        private bool _isDraggingSlider = false;
        private bool _isControlsVisible = false;
        private DisplayRequest? _displayRequest;
        private bool _wasPlayingBeforeDrag = false;
        private bool _isWindowFullScreen = false;
        private bool _isSystemFullScreen = false;
        // 画中画（CompactOverlay）状态：进入后主窗口变为置顶 16:9 小窗，退出播放器时需恢复
        private bool _isPictureInPicture;
        // 进入画中画前的窗口尺寸（物理像素）：退出时恢复原大小（避免关闭小窗后窗口被缩小/放大）
        private int _pipRestoreWidth;
        private int _pipRestoreHeight;
        private double _previousVolume = 1.0;
        private bool _isVolumeMuted = false;
        private bool _isVolumeFlyoutOpen = false;
        private bool _isSyncingVolumeUi = false;
        private double _playbackRate = 1.0;
        // 当前画面比例（null = 适应；双内核共用——普通模式经 ScaleTransform 非等比拉伸，
        // 超分模式经 mpv video-aspect-override 属性；切换视频后保持用户选择）
        private string? _aspectRatio;

        // ===================== 播放队列 Flyout =====================
        // ★ 可空：Unloaded 时置 null 断开"系统弹出层 → Flyout → 页面"引用链，防止页面泄漏
        private Flyout? _queueFlyout;
        private ListView _queueList = null!;
        private TextBlock _queueEmptyText = null!;
        private DataTemplate? _queueDefaultTemplate;
        private DataTemplate? _queueNowPlayingTemplate;
        private bool _isQueueFlyoutOpen;
        // 播放队列卡片交互画刷（悬停/按下反馈）
        private SolidColorBrush _queueNormalBgBrush = null!;
        private SolidColorBrush _queueHoverBgBrush = null!;
        private SolidColorBrush _queuePressedBgBrush = null!;
        private SolidColorBrush _queueNormalBorderBrush = null!;
        private SolidColorBrush _queueHoverBorderBrush = null!;
        private SolidColorBrush _queuePressedBorderBrush = null!;
        private FFmpegMediaSource? _ffmpegMediaSource;
        private IRandomAccessStream? _ffmpegStream;
        private int _loadGeneration;
        private bool _playerTransferred;
        // ★ 修复：记录当前视频 MediaPlayer，供 SMTC 停止按钮事件使用。
        //   页面卸载（Unloaded）后 PlayerElement.MediaPlayer 会被置空/转移，
        //   不能再依赖它定位播放器；同时方便在卸载时统一退订 SMTC 事件，
        //   避免 MediaPlayer 被服务转移后仍通过事件引用本页面导致泄漏。
        private MediaPlayer? _smtcOwner;

        private bool _isCursorHidden = false;
        // 光标"静止隐藏"计时器：播放中指针停在原地超过阈值后隐藏光标
        private DispatcherTimer? _cursorIdleTimer;
        // 光标静止隐藏阈值（毫秒）
        private const double CursorIdleHideMs = 1500;
        // 播放器设置弹窗是否为深色主题（与音乐播放器一致的弹窗外框底色判断）
        private bool _settingsDialogDark;
        // 播放器设置弹窗宿主：超分辨率提示中的「视频设置」超链接跳转时需显式关闭弹窗
        private ContentDialog? _playerSettingsDialog;
        // 超分辨率禁用提示（TeachingTip）：跳转时需一并关闭
        private TeachingTip? _superResolutionTeachingTip;
        // 运动补偿禁用提示（TeachingTip）：跳转时需一并关闭
        private TeachingTip? _motionCompensationTeachingTip;
        // ★ 调试信息悬浮窗轮询取消令牌（替代 DispatcherTimer）：
        //   原 DispatcherTimer 在 UI 线程同步读取 mpv 属性（12 次 P/Invoke），
        //   每次阻塞 UI 线程 200-330ms → 每秒卡死 25-33% → 抽搐/定格。
        //   现改用后台线程轮询 + DispatcherQueue 回传 UI 更新。
        private CancellationTokenSource? _debugInfoCts;

        // ---- 普通模式（MediaPlayer）调试信息：文件元数据缓存 ----
        // MediaPlayer 不暴露码率/编码格式/帧率（无实时 API），改用 MediaEncodingProfile
        // 在打开面板时静态解析一次并缓存（普通模式无补帧，帧率恒定 = 原始帧率）。
        private string _normalInfoFilePath = string.Empty;   // 已解析元数据的文件路径（换文件后重新解析）
        private bool _normalInfoResolving;                   // 解析中标志，防止并发重复解析
        private double _normalBitrateKbps = -1;              // 视频码率 kbps（-1 = 未解析，0 = 无信息）
        private double _normalAudioBitrateKbps = -1;         // 音频码率 kbps（-1 = 未解析，0 = 无信息）
        private string _normalVideoCodec = string.Empty;     // 视频编码格式（Subtype，如 H264/HEVC）
        private double? _normalFrameRate;                    // 容器原始帧率

        // ===== 超分模式（libmpv）相关 =====
        // libmpv 是否支持当前架构（Endpne.LibMPV.Windows 仅提供 x64/ARM64 dll，x86 不支持）
        private static readonly bool IsMpvArchitectureSupported =
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X86;
        // 是否为超分模式（从设置读取，构造时确定，进入播放器后不变；x86 自动回退普通模式）
        private readonly bool _isMpvMode;
        // 超分模式播放器内核（mpv）
        private SightoHear.Mpv.MpvVideoPlayer? _mpvVideo;
        // 超分模式待恢复的转场断点位置（秒，-1 = 无）
        private double _mpvResumePosition = -1;
        // 记忆播放位置（续播）：本次加载视频要恢复的上次观看位置（秒，-1 = 无）
        private double _savedResumePosition = -1;
        // 记忆播放位置保存节流：上次成功保存时的播放位置（秒，-1 = 尚未保存）
        // 用于避免高频位置回调（普通模式 250ms 轮询 / mpv PositionChanged 事件）反复写盘
        private double _lastSavedResumePosition = -1;
        // 后台播放：窗口是否处于最小化状态
        private bool _isWindowMinimized;
        // 后台播放：因"最小化暂停"而暂停的标志（还原窗口时恢复播放）
        private bool _pauseOnMinimize;
        // ★ 空格键处理：RootGrid 以 handledEventsToo 监听 Space（绕过按钮聚焦时事件被类处理器
        //   标记为已处理的情况），将空格保留为可设置的快捷键而非按钮"确定"键
        private bool _spaceKeyHandlersAttached;
        private readonly KeyEventHandler _spaceKeyDownHandler;
        private readonly KeyEventHandler _spaceKeyUpHandler;
        // 超分模式 SMTC 控制器（mpv 非 MediaPlayer，无法自动上报，需手动集成）
        private SightoHear.Mpv.MpvSmtcController? _mpvSmtc;

        public VideoPlayerPage()
        {
            InitializeComponent();
            _isMpvMode = App.SettingsHelper.VideoPlayerMode == "Mpv" && IsMpvArchitectureSupported;
            if (_isMpvMode)
            {
                AppLogger.Info("视频播放器已启用 libmpv（libmpv + Anime4K，实验性）");
            }
            else if (App.SettingsHelper.VideoPlayerMode == "Mpv")
            {
                AppLogger.Warning("x86（32 位）平台不支持 libmpv libmpv，已回退为 media player");
            }
            // 空格键处理（handledEventsToo，绕过按钮类处理器）
            _spaceKeyDownHandler = new KeyEventHandler(RootGrid_SpaceKeyDown);
            _spaceKeyUpHandler = new KeyEventHandler(RootGrid_SpaceKeyUp);
            this.Loaded += VideoPlayerPage_Loaded;
            this.Unloaded += VideoPlayerPage_Unloaded;
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is VideoPlayerArgs args)
            {
                _playlist = args.Playlist ?? new List<MediaItem>();
                _currentIndex = args.StartIndex;
                if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
                {
                    _currentItem = _playlist[_currentIndex];

                    var externalPlayer = App.MusicPlayback.ExternalPlayer;
                    var externalItem = App.MusicPlayback.ExternalItem;
                    if (_isMpvMode)
                    {
                        // 超分模式：无法转移 MediaPlayer 实例，改为"记录断点 + 重新用 mpv 打开"。
                        if (externalPlayer != null && externalItem != null &&
                            string.Equals(externalItem.FilePath, _currentItem.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            // 记录音乐播放器（外部播放器）当前播放位置，mpv 加载后恢复
                            try
                            {
                                _mpvResumePosition = externalPlayer.PlaybackSession.Position.TotalSeconds;
                                AppLogger.Info($"libmpv转场：记录外部播放器断点位置 {_mpvResumePosition:0.00}s");
                            }
                            catch { }
                            App.MusicPlayback.ClearExternalPlayback();
                        }
                        // ★ 修复（从迷你播放器重新打开视频）：超分模式下退出视频播放器时，
                        //   视频被转交给内部播放器（MusicPlaybackService.Player）继续播放
                        //   （ExternalPlayer 为 null，HasExternalPlayback=false）。此时从
                        //   内部播放器读取断点并停止内部播放，避免 mpv 与内部播放器同时出声。
                        else if (App.MusicPlayback.CurrentItem is { MediaType: "Video" } &&
                            App.MusicPlayback.CanAccessPlaybackSession)
                        {
                            var internalVideo = App.MusicPlayback.CurrentItem;
                            if (string.Equals(internalVideo.FilePath, _currentItem.FilePath,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    double pos = App.MusicPlayback.Position.TotalSeconds;
                                    if (pos > 0)
                                    {
                                        _mpvResumePosition = pos;
                                        AppLogger.Info($"libmpv转场：记录内部播放器断点位置 {_mpvResumePosition:0.00}s");
                                    }
                                }
                                catch { }
                            }
                            App.MusicPlayback.StopPlayback();
                        }
                        LoadVideo(_currentItem);
                    }
                    else if (externalPlayer != null && externalItem != null &&
                        string.Equals(externalItem.FilePath, _currentItem.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        _playerTransferred = true;
                        App.MusicPlayback.DetachExternalPlayback();
                        PlayerElement.SetMediaPlayer(externalPlayer);
                        // 转场路径不经过 LoadVideo，需手动设置左上角标题（否则保持占位符"视频标题"）
                        TitleText.Text = _currentItem.FileName;
                        // 同步外部播放器当前的倍速，保持 UI 显示与实际播放一致
                        _playbackRate = externalPlayer.PlaybackSession.PlaybackRate;
                        UpdateSpeedDisplay(_playbackRate);
                        externalPlayer.MediaOpened += MediaPlayer_MediaOpened;
                        externalPlayer.MediaEnded += MediaPlayer_MediaEnded;
                        externalPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
                        SyncVolumeUi(externalPlayer.Volume);
                        // 播放器已处于已打开状态，MediaOpened 不会再触发，手动初始化进度 UI
                        try
                        {
                            var duration = externalPlayer.PlaybackSession.NaturalDuration;
                            if (duration.TotalSeconds > 0)
                            {
                                TotalTimeText.Text = FormatTime(duration);
                                ProgressSlider.Maximum = duration.TotalSeconds;
                                UpdatePlayPauseIcon(externalPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
                            }
                        }
                        catch { }                    }
                    else
                    {
                        if (App.MusicPlayback.HasExternalPlayback)
                            App.MusicPlayback.ClearExternalPlayback();
                        else
                            App.MusicPlayback.StopPlayback();
                        LoadVideo(_currentItem);
                    }
                }
            }

            EnterWindowFullScreen();
            AppLogger.Info($"进入播放器: 列表{_playlist.Count}项, 起始索引{_currentIndex}, 当前文件={_currentItem?.FileName}");
        }

        private void VideoPlayerPage_Loaded(object sender, RoutedEventArgs e)
        {
            RootGrid.PointerMoved += RootGrid_PointerMoved;
            RootGrid.PointerExited += RootGrid_PointerExited;
            PlayerElement.PointerPressed += PlayerElement_PointerPressed;
            if (_isMpvMode)
            {
                MpvVideoRender.PointerPressed += MpvVideoRender_PointerPressed;
            }
            this.KeyDown += VideoPlayerPage_KeyDown;
            // 自定义快捷键（松开执行模式）：KeyUp 时触发
            this.KeyUp += VideoPlayerPage_KeyUp;

            _positionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            // 后台播放：订阅窗口状态变化（检测最小化，设置关闭时最小化暂停、还原恢复）
            SubscribeWindowStateChanged();

            // ★ 空格键：拦截所有按钮的 Space 触发（防止聚焦按钮时空格触发确定），
            //   并用 handledEventsToo 在 RootGrid 统一处理空格快捷键
            AttachSpaceKeyHandlers();

            // 构建播放队列 Flyout（数据源 _playlist + _currentIndex）
            BuildQueueFlyout();

            // 音量 Flyout 的事件已由 XAML 中的 Opened/Closed 处理

            ProgressSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed), true);
            ProgressSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased), true);
            ProgressSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ProgressSlider_PointerCaptureLost), true);

            // 画中画简化控制栏的进度条（Slider 会内部处理指针事件，同样需要 handledEventsToo）
            PipProgressSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed), true);
            PipProgressSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased), true);
            PipProgressSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ProgressSlider_PointerCaptureLost), true);

            // 首次进入播放器：先短暂显示 UI（1.5 秒），让用户看到控制栏，
            // 随后根据鼠标位置决定继续显示（靠近边缘）还是隐藏（在中间）。
            ShowControls();
            _introTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _introTimer.Tick += IntroTimer_Tick;
            _introTimer.Start();
        }

        protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);
            AppLogger.Info("离开播放器页面");
        }

        private void VideoPlayerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _loadGeneration++;
            // 退订窗口状态变化（后台播放），避免页面卸载后仍响应最小化事件
            UnsubscribeWindowStateChanged();
            // 移除 RootGrid 的空格键处理器（页面卸载后不再拦截）
            if (_spaceKeyHandlersAttached)
            {
                RootGrid.RemoveHandler(UIElement.KeyDownEvent, _spaceKeyDownHandler);
                RootGrid.RemoveHandler(UIElement.KeyUpEvent, _spaceKeyUpHandler);
                _spaceKeyHandlersAttached = false;
            }
            _positionTimer?.Stop();
            _introTimer?.Stop();
            _delayedHideTimer?.Stop();
            _cursorIdleTimer?.Stop();

            // 退出播放器时若处于画中画（CompactOverlay）模式，恢复默认窗口（避免残留置顶小窗）
            ExitPictureInPictureIfActive();

            // 清理播放队列 Flyout（退订事件并断开引用链，防止页面泄漏）
            if (_queueFlyout != null)
            {
                _queueFlyout.Opening -= QueueFlyout_Opening;
                _queueFlyout.Closed -= QueueFlyout_Closed;
                _queueFlyout = null;
            }
            QueueButton.Flyout = null;
            // ★ 页面卸载时取消后台调试信息轮询并关闭悬浮窗
            CancelDebugInfoLoop();
            if (DebugInfoPanel != null)
            {
                DebugInfoPanel.Visibility = Visibility.Collapsed;
            }

            // 页面卸载时恢复默认光标
            if (_isCursorHidden)
            {
                try { SetProtectedCursor(RootGrid, null); } catch { }
                _isCursorHidden = false;
            }

            ProgressSlider.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed));
            ProgressSlider.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased));
            ProgressSlider.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ProgressSlider_PointerCaptureLost));

            PipProgressSlider.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed));
            PipProgressSlider.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased));
            PipProgressSlider.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ProgressSlider_PointerCaptureLost));

            RootGrid.PointerMoved -= RootGrid_PointerMoved;
            RootGrid.PointerExited -= RootGrid_PointerExited;
            PlayerElement.PointerPressed -= PlayerElement_PointerPressed;
            if (_isMpvMode)
            {
                MpvVideoRender.PointerPressed -= MpvVideoRender_PointerPressed;
            }
            this.KeyDown -= VideoPlayerPage_KeyDown;
            this.KeyUp -= VideoPlayerPage_KeyUp;

            if (_displayRequest != null)
            {
                try { _displayRequest.RequestRelease(); } catch { }
                _displayRequest = null;
            }

            // ===== 超分模式：释放 mpv 内核并断点续播到音乐播放器 =====
            if (_isMpvMode)
            {
                // 释放超分模式 SMTC 会话（关闭系统媒体会话，避免残留）
                _mpvSmtc?.Dispose();
                _mpvSmtc = null;

                var mpv = _mpvVideo;
                _mpvVideo = null;
                if (mpv != null)
                {
                    // ★ 修复（页面泄漏）：退订页面订阅的 mpv 事件，断开
                    //   MpvVideoPlayer（事件源）→ 本页面实例的引用链。
                    //   此前只 DisposeAsync 不退订：mpv 对象在异步销毁期间（甚至销毁后
                    //   残留的事件循环/回调）仍持有本页面委托引用，导致 VideoPlayerPage
                    //   无法被 GC 回收——实测每次进出 mpv 播放器泄漏一个页面实例。
                    mpv.PlaybackStateChanged -= OnMpvPlaybackStateChanged;
                    mpv.PositionChanged -= OnMpvPositionChanged;
                    mpv.Ended -= OnMpvEnded;
                    mpv.LogMessage -= OnMpvLogMessage;

                    // ★ 诊断（泄漏排查）：退订后统计剩余订阅者数（正常应为 0）。
                    //   若 > 0 说明存在未退订的页面委托（lambda/闭包订阅路径），
                    //   需据此定位真正的泄漏引用源。
                    mpv.LogSubscriptionDiagnostics();

                    // 正在播放/暂停中且有媒体 → 转交给音乐播放器（断点续播）
                    bool hasMedia = mpv.IsMediaLoaded || mpv.Duration > 0;
                    double position = mpv.Position;
                    if (hasMedia && position > 0 && _currentItem != null)
                    {
                        // 记忆播放位置（续播）：退出播放器时保存当前位置
                        TrySaveResumePosition(position, mpv.Duration);
                        var item = _currentItem;
                        var queue = _playlist.ToList();
                        _ = TransferVideoToMusicAsync(item, queue, position);
                    }
                    _ = mpv.DisposeAsync();
                }
                ShowCursorSafe();
                return;
            }

            // ★ 修复（健壮性）：无论 PlayerElement.MediaPlayer 是否为 null，
            //   都确保 _smtcOwner 指向的播放器事件已退订。覆盖异常路径
            //   （LoadVideo 未完成即卸载、MediaPlayer 已被外部置空等），
            //   避免 MediaPlayer 仍通过 CommandManager/SMTC 事件引用本页面。
            if (_smtcOwner != null)
            {
                UnsubscribeMediaCommands(_smtcOwner);
                _smtcOwner = null;
            }

            var player = PlayerElement.MediaPlayer;
            if (player != null)
            {
                // ★ 修复：先退订 CommandManager/SMTC 事件，再销毁或转移播放器。
                //   播放器转移给服务后仍存活，其 CommandManager 事件若还指向本页面
                //   （VideoCommandManager_* 是实例方法），页面将无法被 GC 回收，
                //   Win2D/GPU 资源随之泄漏。
                UnsubscribeMediaCommands(player);
                _smtcOwner = null;

                player.MediaOpened -= MediaPlayer_MediaOpened;
                player.MediaEnded -= MediaPlayer_MediaEnded;

                // ★ 修复：检查 PlaybackSession 是否为 null，避免在 Unloaded 时
                //   访问已释放 MediaPlayer 的 PlaybackSession/Source 导致 COMException。
                var playbackSession = player.PlaybackSession;
                if (playbackSession == null)
                {
                    PlayerElement.SetMediaPlayer(null);
                    try { player.Source = null; } catch { }
                    player.Dispose();
                    return;
                }

                bool wasPlaying = playbackSession.PlaybackState
                    is MediaPlaybackState.Playing or MediaPlaybackState.Paused
                    or MediaPlaybackState.Buffering or MediaPlaybackState.Opening;
                bool hasSource = player.Source != null
                    && playbackSession.NaturalDuration.TotalSeconds > 0;

                if (wasPlaying && hasSource && _currentItem != null)
                {
                    // 记忆播放位置（续播）：退出播放器时保存当前位置
                    // （无论是否转交给音乐播放器继续播放，都记录一次，确保下次打开能续播）
                    try
                    {
                        TrySaveResumePosition(playbackSession.Position.TotalSeconds,
                            playbackSession.NaturalDuration.TotalSeconds);
                    }
                    catch { }

                    // ★ 检查服务是否已经在播放其他媒体项（例如从视频切换到音乐时，
                    //   PlayAsync 已在内部 Player 上开始播放音乐）。
                    //   如果是，则直接销毁当前播放器，而非转移给服务，
                    //   否则 ActivePlayer 会指向外部（视频）播放器，导致进度条/控制错乱。
                    var activeItem = App.MusicPlayback.ActiveItem;
                    bool serviceAlreadyHasOtherItem = activeItem != null &&
                        !string.Equals(activeItem.FilePath, _currentItem.FilePath,
                            StringComparison.OrdinalIgnoreCase);

                    if (!serviceAlreadyHasOtherItem)
                    {
                        _playerTransferred = true;
                        playbackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
                        App.MusicPlayback.RegisterExternalPlayback(player, _currentItem,
                            _playlist.Count > 0 ? _playlist : null, _currentIndex);
                        // 将 FFmpeg 资源所有权转交给服务，避免页面 GC 时关闭流
                        App.MusicPlayback.RegisterExternalFfmpegResources(_ffmpegMediaSource, _ffmpegStream);
                        _ffmpegMediaSource = null;
                        _ffmpegStream = null;
                        PlayerElement.SetMediaPlayer(null);
                    }
                    else
                    {
                        // 服务已在播放其他媒体 → 直接销毁当前播放器
                        playbackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
                        PlayerElement.SetMediaPlayer(null);
                        try { player.Source = null; } catch { }
                        player.Dispose();
                    }
                }
                else
                {
                    playbackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
                    PlayerElement.SetMediaPlayer(null);
                    try { player.Source = null; } catch { }
                    player.Dispose();
                }
            }

            if (!_playerTransferred)
            {
                _ffmpegMediaSource = null;
                _ffmpegStream?.Dispose();
                _ffmpegStream = null;
            }
            ShowCursorSafe();
        }

        private async void LoadVideo(MediaItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
                return;

            // 超分模式：走 libmpv 播放内核
            if (_isMpvMode)
            {
                await LoadVideoMpvAsync(item);
                return;
            }

            // 记忆播放位置（续播）：加载视频时读取上次观看位置（转场路径不走此方法，不会误恢复）
            _savedResumePosition = IsResumePositionEnabled
                ? SightoHear.Services.VideoResumePositionService.GetPosition(item.FilePath) ?? -1
                : -1;
            // 切换新视频时重置保存节流，确保首次保存不会被旧视频的节流值跳过
            _lastSavedResumePosition = -1;

            int loadGeneration = ++_loadGeneration;
            TitleText.Text = item.FileName;
            App.SettingsHelper.LastVideoPath = item.FilePath;
            App.SettingsHelper.LastVideoTime = DateTime.Now;
            App.SettingsHelper.Save();

            // 加载新视频时隐藏中央播放按钮，避免上一个视频的按钮残留闪烁
            CenterPlayButton.Visibility = Visibility.Collapsed;
            CenterPlayButton.Opacity = 0;
            CenterPlayButton.IsHitTestVisible = false;

            var oldPlayer = PlayerElement.MediaPlayer;
            if (oldPlayer != null)
            {
                oldPlayer.Pause();
                // ★ 修复：销毁旧播放器前退订媒体命令事件，避免旧播放器存活期间
                //   仍通过 CommandManager/SMTC 事件引用本页面（切换视频时页面未卸载，
                //   订阅会随实例方法跨多次 LoadVideo 累积，造成事件处理器堆积）。
                UnsubscribeMediaCommands(oldPlayer);
                oldPlayer.MediaOpened -= MediaPlayer_MediaOpened;
                oldPlayer.MediaEnded -= MediaPlayer_MediaEnded;
                oldPlayer.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
                oldPlayer.Source = null;
                PlayerElement.SetMediaPlayer(null);
                oldPlayer.Dispose();
            }

            _ffmpegMediaSource = null;
            _ffmpegStream?.Dispose();
            _ffmpegStream = null;

            var mediaPlayer = new MediaPlayer
            {
                // "自动播放"设置：开启时打开视频自动开始播放；关闭时进入播放器处于暂停状态
                AutoPlay = App.SettingsHelper.AutoPlayVideo,
                Volume = _isVolumeMuted ? 0 : Math.Clamp(_previousVolume, 0, 1)
            };
            // 注意：不能在此处设置 PlaybackRate —— MediaPlayer 在赋值 Source 时
            // 会把 PlaybackSession 重置（含 PlaybackRate 回到 1.0），
            // 因此必须在 Source 赋值之后（以及 MediaOpened 中兜底）再应用倍速。

            // 配置系统媒体传输控件 (SMTC)，实现 Windows 任务栏媒体预览。
            // 重要：不要设置 CommandManager.IsEnabled = false，必须保持 MediaPlayer 默认的
            // 自动 SMTC 集成，这样播放状态/时间线/媒体属性会自动上报给系统，
            // 外部软件（如歌词软件 BetterLyrics，通过 GlobalSystemMediaTransportControlsSessionManager
            // 监听）才能读取到本软件的视频播放信息。
            var smtc = mediaPlayer.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsPreviousEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsStopEnabled = true;
            // 通过 CommandManager 事件自定义系统媒体按钮行为（自动集成模式下推荐方式），
            // 并强制启用上一曲/下一曲按钮。
            ConfigureVideoMediaCommands(mediaPlayer);

            mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            mediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;

            PlayerElement.SetMediaPlayer(mediaPlayer);
            SyncVolumeUi(mediaPlayer.Volume);

            // 应用视频播放器保存的音频输出设备（跟随系统则无需处理，
            // AudioDevice 默认即为系统默认输出设备）
            if (!string.IsNullOrEmpty(App.SettingsHelper.VideoOutputDeviceId))
                _ = ApplyVideoAudioDeviceAsync(App.SettingsHelper.VideoOutputDeviceId);

            IRandomAccessStream? pendingStream = null;
            try
            {
                if (App.SettingsHelper.VideoDecoderBackend == "System")
                {
                    // 通过 MediaPlaybackItem 的显示属性上报媒体元数据（自动 SMTC 集成模式）
                    var playbackItem = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(item.FilePath)));
                    await ApplyVideoItemDisplayPropertiesAsync(playbackItem, item);
                    mediaPlayer.Source = playbackItem;
                    AppLogger.Info($"使用 Windows 系统解码器: {item.FileName}");
                }
                else
                {
                    var config = new MediaSourceConfig();
                    config.Video.VideoDecoderMode = App.SettingsHelper.VideoDecodeMode == "Software"
                        ? VideoDecoderMode.ForceFFmpegSoftwareDecoder
                        : VideoDecoderMode.Automatic;
                    // 关键修复：v2.1.0 必须通过 General 子配置显式声明最高倍速，
                    // MediaStreamSource 才会正确上报倍速支持（官方 PR #464）。
                    // 否则 PlaybackRate 设置为 2x 后媒体实际不变速
                    // （IsSupportedPlaybackRateRange 返回 False）。
                    config.General.MaxSupportedPlaybackRate = 4.0;

                    var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                    pendingStream = await file.OpenReadAsync();
                    var ffmpegSource = await FFmpegMediaSource.CreateFromStreamAsync(
                        pendingStream, config);

                    if (loadGeneration != _loadGeneration ||
                        PlayerElement.MediaPlayer != mediaPlayer)
                    {
                        pendingStream.Dispose();
                        // ★ 修复：加载已被更新的请求取代（页面卸载或切换视频），
                        //   清理刚创建但未使用的 MediaPlayer 及其事件订阅，
                        //   否则该播放器无人接管，其 CommandManager/SMTC 事件
                        //   将持续引用本页面导致泄漏（Win2D/GPU 资源无法回收）。
                        CleanupAbandonedMediaPlayer(mediaPlayer);
                        return;
                    }

                    _ffmpegMediaSource = ffmpegSource;
                    _ffmpegStream = pendingStream;
                    pendingStream = null;
                    // 通过 MediaPlaybackItem 的显示属性上报媒体元数据（自动 SMTC 集成模式）
                    var playbackItem = ffmpegSource.CreateMediaPlaybackItem();
                    await ApplyVideoItemDisplayPropertiesAsync(playbackItem, item);
                    mediaPlayer.Source = playbackItem;
                    AppLogger.Info(
                        $"使用内置 FFmpeg 解码器({App.SettingsHelper.VideoDecodeMode}): {item.FileName}");
                }

                UpdatePlayPauseIcon(true);

                // 修复倍速失效：设置 Source 会重置播放会话的 PlaybackRate，
                // 必须在 Source 赋值完成后重新应用用户选择的倍速。
                if (Math.Abs(_playbackRate - 1.0) > 0.01)
                    mediaPlayer.PlaybackSession.PlaybackRate = _playbackRate;
            }
            catch (Exception ex)
            {
                pendingStream?.Dispose();
                AppLogger.Error(ex, $"内置 FFmpeg 解码失败，切换到系统解码器: {item.FileName}");

                    if (loadGeneration == _loadGeneration &&
                        PlayerElement.MediaPlayer == mediaPlayer)
                    {
                        _ffmpegMediaSource = null;
                        // 通过 MediaPlaybackItem 的显示属性上报媒体元数据（自动 SMTC 集成模式）
                        var playbackItem = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(item.FilePath)));
                        await ApplyVideoItemDisplayPropertiesAsync(playbackItem, item);
                        mediaPlayer.Source = playbackItem;
                        // 修复倍速失效：切换 Source 后同样会重置 PlaybackRate，需重新应用
                        if (Math.Abs(_playbackRate - 1.0) > 0.01)
                            mediaPlayer.PlaybackSession.PlaybackRate = _playbackRate;
                        UpdatePlayPauseIcon(true);
                    }
                    else
                    {
                        // ★ 修复：解码失败且当前加载已被取代（页面卸载/切视频），
                        //   清理未被接管的 MediaPlayer，避免事件订阅持有页面导致泄漏。
                        CleanupAbandonedMediaPlayer(mediaPlayer);
                    }
            }
        }

        // ==================== 超分模式（libmpv）实现 ====================

        /// <summary>当前是否正在播放（兼容两种内核）。</summary>
        private bool GetIsPlaying()
        {
            if (_isMpvMode)
            {
                // ★ 优先读取 mpv 真实 pause 属性：缓冲（paused-for-cache）、外部暂停等
                //   未触发 PlaybackStateChanged 事件的暂停也能正确反映，避免按钮图标与
                //   实际状态不同步。属性读取失败（媒体未加载等）回退到 State 事件。
                bool? paused = _mpvVideo?.IsPausedNow;
                if (paused.HasValue)
                {
                    return !paused.Value;
                }
                return _mpvVideo?.State == SightoHear.Mpv.Enums.Player.PlaybackState.Playing;
            }
            return PlayerElement.MediaPlayer?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        }

        /// <summary>当前播放位置（秒，兼容两种内核）。</summary>
        private double GetPositionSeconds()
        {
            if (_isMpvMode)
            {
                return _mpvVideo?.Position ?? 0;
            }
            return PlayerElement.MediaPlayer?.PlaybackSession.Position.TotalSeconds ?? 0;
        }

        /// <summary>当前媒体时长（秒，兼容两种内核）。</summary>
        private double GetDurationSeconds()
        {
            if (_isMpvMode)
            {
                return _mpvVideo?.Duration ?? 0;
            }
            return PlayerElement.MediaPlayer?.PlaybackSession.NaturalDuration.TotalSeconds ?? 0;
        }

        /// <summary>暂停当前播放（兼容两种内核）。</summary>
        private void PauseCurrent()
        {
            if (_isMpvMode)
            {
                _mpvVideo?.Pause();
            }
            else
            {
                PlayerElement.MediaPlayer?.Pause();
            }
        }

        /// <summary>恢复当前播放（兼容两种内核）。</summary>
        private void PlayCurrent()
        {
            if (_isMpvMode)
            {
                _mpvVideo?.Play();
            }
            else
            {
                PlayerElement.MediaPlayer?.Play();
            }
        }

        /// <summary>seek 到指定位置（秒，兼容两种内核）。</summary>
        private void SeekCurrent(double seconds)
        {
            if (_isMpvMode)
            {
                _mpvVideo?.Seek(seconds);
            }
            else if (PlayerElement.MediaPlayer?.PlaybackSession is { } session)
            {
                session.Position = TimeSpan.FromSeconds(seconds);
            }
        }

        /// <summary>
        /// 超分模式加载视频：初始化 mpv 内核（首次）、加载文件、恢复转场断点。
        /// </summary>
        private async Task LoadVideoMpvAsync(MediaItem item)
        {
            // 记忆播放位置（续播）：加载视频时读取上次观看位置
            _savedResumePosition = IsResumePositionEnabled
                ? SightoHear.Services.VideoResumePositionService.GetPosition(item.FilePath) ?? -1
                : -1;
            // 切换新视频时重置保存节流，确保首次保存不会被旧视频的节流值跳过
            _lastSavedResumePosition = -1;

            int loadGeneration = ++_loadGeneration;
            TitleText.Text = item.FileName;
            App.SettingsHelper.LastVideoPath = item.FilePath;
            App.SettingsHelper.LastVideoTime = DateTime.Now;
            App.SettingsHelper.Save();

            // 加载新视频时隐藏中央播放按钮，避免上一个视频的按钮残留闪烁
            CenterPlayButton.Visibility = Visibility.Collapsed;
            CenterPlayButton.Opacity = 0;
            CenterPlayButton.IsHitTestVisible = false;

            try
            {
                if (_mpvVideo == null)
                {
                    _mpvVideo = new SightoHear.Mpv.MpvVideoPlayer();
                    _mpvVideo.PlaybackStateChanged += OnMpvPlaybackStateChanged;
                    _mpvVideo.PositionChanged += OnMpvPositionChanged;
                    _mpvVideo.Ended += OnMpvEnded;
                    _mpvVideo.LogMessage += OnMpvLogMessage;

                    // 创建超分模式 SMTC 控制器（视图级 SMTC，手动上报播放信息给系统媒体会话）。
                    // WinUI 3 无 CoreWindow，必须通过 ISystemMediaTransportControlsInterop 绑定窗口句柄
                    _mpvSmtc = new SightoHear.Mpv.MpvSmtcController(
                        _mpvVideo, DispatcherQueue, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                    AppLogger.Info("libmpv：SMTC 控制器已创建");

                    // 切换到 mpv 渲染宿主，隐藏普通 MediaPlayerElement
                    MpvVideoRender.Visibility = Visibility.Visible;
                    PlayerElement.Visibility = Visibility.Collapsed;

                    // 等待渲染宿主完成布局（初始 Collapsed 时尺寸为 0，需要布局后才能创建帧缓冲），
                    // 确保 mpv 渲染上下文初始化时 GL 帧缓冲已就绪
                    for (int i = 0; i < 20 && MpvVideoRender.ActualWidth <= 0; i++)
                    {
                        await Task.Delay(50);
                    }
                    AppLogger.Info($"libmpv：渲染宿主布局完成，尺寸 {MpvVideoRender.ActualWidth:0}x{MpvVideoRender.ActualHeight:0}");

                    await _mpvVideo.InitializeAsync(MpvVideoRender);
                    AppLogger.Info($"libmpv mpv 内核初始化完成, 解码后端=FFmpeg(libmpv), 硬解=auto-safe");

                    // 同步音量状态到 mpv
                    if (_isVolumeMuted)
                    {
                        _mpvVideo.SetVolume(0);
                        _mpvVideo.SetMuted(true);
                    }
                    else
                    {
                        _mpvVideo.SetVolume((int)Math.Round(_previousVolume * 100));
                    }
                }

                // ★ 修复（崩溃）：防御并发初始化竞态——快速双击播放按钮/视频卡片时，
                //   第二次 LoadVideoMpvAsync 可能在首次 InitializeAsync 尚未完成时就执行
                //   LoadAsync（loadfile 撞上 mpv_initialize / render_context 创建），导致
                //   mpv 核心状态混乱、渲染与同步命令互相等待（实测 0xc000027b 崩溃）。
                //   这里等待 mpv 内核初始化真正完成后再加载文件（5 秒超时兜底，
                //   避免初始化异常时永久等待；超时后继续尝试，loadfile 失败会被捕获）。
                int readyWaitCount = 0;
                while (!_mpvVideo.IsReady)
                {
                    if (loadGeneration != _loadGeneration)
                    {
                        return;
                    }
                    if (++readyWaitCount > 100)
                    {
                        AppLogger.Warning("libmpv等待 mpv 初始化就绪超时（5 秒），继续尝试加载");
                        break;
                    }
                    await Task.Delay(50);
                }

                if (loadGeneration != _loadGeneration)
                {
                    return;
                }

                await _mpvVideo.LoadAsync(item.FilePath);
                UpdatePlayPauseIcon(true);

                // 同步 SMTC 媒体元数据（标题/封面/时间线），供任务栏媒体预览与外部软件读取
                _mpvSmtc?.SetMediaItemAsync(item);

                // 恢复播放位置：转场断点优先，其次恢复记忆播放位置（续播）
                // （两者均在 mpv 文件加载完成后延迟 seek，见 DelayedMpvSeekAsync）
                // 转场断点（_mpvResumePosition > 0）表示从音乐播放器/迷你播放器转场回来，
                // 原本正在播放，应保持播放状态，不受"自动播放"设置影响
                bool isTransitionResume = _mpvResumePosition > 0;
                double resumePosition = -1;
                if (_mpvResumePosition > 0)
                {
                    resumePosition = _mpvResumePosition;
                    _mpvResumePosition = -1;
                }
                else if (_savedResumePosition > 0)
                {
                    resumePosition = _savedResumePosition;
                    _savedResumePosition = -1;
                }
                if (resumePosition > 0)
                {
                    _ = DelayedMpvSeekAsync(resumePosition, loadGeneration);
                }
                // "自动播放"设置：关闭时加载完成后暂停（转场场景保持播放）
                if (!App.SettingsHelper.AutoPlayVideo && !isTransitionResume)
                {
                    _ = DelayedMpvPauseAsync(loadGeneration);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"libmpv加载视频失败: {item.FileName}");
            }
        }

        /// <summary>延迟 seek 到指定播放位置（等待 mpv 文件加载完成）。</summary>
        private async Task DelayedMpvSeekAsync(double position, int generation)
        {
            try
            {
                // 等待媒体打开（mpv 加载完成且时长已知）。
                // ★ 修复（断点续播失效）：此前在 !IsMediaLoaded 时直接 return，
                //   但 loadfile 是异步命令——LoadAsync 返回后文件往往仍在加载中，
                //   IsMediaLoaded 为 false，导致断点 seek 被立即放弃、永远不执行，
                //   每次重开视频都从 0:00 开始播放。现在改为"就绪前继续等待"。
                for (int i = 0; i < 50; i++)
                {
                    if (generation != _loadGeneration || _mpvVideo == null)
                    {
                        return;
                    }
                    if (_mpvVideo.IsMediaLoaded && _mpvVideo.Duration > 0)
                    {
                        break;
                    }
                    await Task.Delay(100);
                }
                if (generation != _loadGeneration || _mpvVideo == null)
                {
                    return;
                }
                // 记忆位置已接近结尾（最后 10 秒）视为已看完 → 清除记录，从头播放
                if (_mpvVideo.Duration > 0 && position >= _mpvVideo.Duration - 10)
                {
                    if (_currentItem != null)
                        SightoHear.Services.VideoResumePositionService.ClearPosition(_currentItem.FilePath);
                    AppLogger.Info($"libmpv恢复位置已接近结尾，清除记忆从头播放: {position:0.00}s / {_mpvVideo.Duration:0.00}s");
                    return;
                }
                _mpvVideo.Seek(position);
                AppLogger.Info($"libmpv已恢复播放位置: {position:0.00}s");
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"libmpv恢复位置失败: {ex.Message}");
            }
        }

        /// <summary>延迟暂停（等待 mpv 文件加载完成后执行）：「自动播放」关闭时进入播放器保持暂停。</summary>
        private async Task DelayedMpvPauseAsync(int generation)
        {
            try
            {
                // 等待媒体打开（loadfile 异步，命令返回后文件可能仍在加载中）
                for (int i = 0; i < 50; i++)
                {
                    if (generation != _loadGeneration || _mpvVideo == null)
                    {
                        return;
                    }
                    if (_mpvVideo.IsMediaLoaded)
                    {
                        break;
                    }
                    await Task.Delay(100);
                }
                if (generation != _loadGeneration || _mpvVideo == null)
                {
                    return;
                }
                if (_mpvVideo.State == SightoHear.Mpv.Enums.Player.PlaybackState.Playing)
                {
                    _mpvVideo.Pause();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdatePlayPauseIcon(false);
                        ShowControls();
                        UpdateCenterPlayButton();
                        AppLogger.Info("libmpv自动播放已关闭，视频打开后处于暂停状态");
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"libmpv自动暂停失败: {ex.Message}");
            }
        }

        /// <summary>超分模式播放状态变化（mpv 事件，后台线程触发）。</summary>
        private void OnMpvPlaybackStateChanged(object? sender, SightoHear.Mpv.Enums.Player.PlaybackState state)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                bool isPlaying = state == SightoHear.Mpv.Enums.Player.PlaybackState.Playing;

                // 播放时保持屏幕常亮
                if (isPlaying)
                {
                    if (_displayRequest == null)
                        _displayRequest = new DisplayRequest();
                    try { _displayRequest.RequestActive(); } catch { }
                }
                else
                {
                    if (_displayRequest != null)
                    {
                        try { _displayRequest.RequestRelease(); } catch { }
                        _displayRequest = null;
                    }
                }

                UpdatePlayPauseIcon(isPlaying);

                if (isPlaying)
                {
                    if (_isControlsVisible)
                        ResetHideControlsTimer();
                    RestartCursorIdleTimer();
                }
                else
                {
                    // 画中画模式：UI 仅由点击切换，暂停不强制显示控制栏
                    if (!_isPictureInPicture)
                        ShowControls();
                    _cursorIdleTimer?.Stop();
                }

                UpdateCenterPlayButton();
            });
        }

        /// <summary>超分模式播放位置变化（mpv 事件，后台线程触发）。</summary>
        private void OnMpvPositionChanged(double position, double duration)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isDraggingSlider)
                {
                    return;
                }
                if (duration > 0)
                {
                    ProgressSlider.Maximum = duration;
                    ProgressSlider.Value = position;
                    TotalTimeText.Text = FormatTime(duration);
                    SyncPipProgress(position, duration);
                    // 记忆播放位置（续播）：节流保存
                    TrySaveResumePosition(position, duration);
                }
                CurrentTimeText.Text = FormatTime(position);
            });
        }

        /// <summary>超分模式播放结束：自动播放下一曲。</summary>
        private void OnMpvEnded()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // 播放结束：清除该视频的记忆播放位置（下次从头播放）
                if (_currentItem != null)
                    SightoHear.Services.VideoResumePositionService.ClearPosition(_currentItem.FilePath);
                // "自动播放下一个"设置：开启时播放完毕自动播放下一个；关闭时停留在当前视频
                if (_currentIndex + 1 < _playlist.Count && App.SettingsHelper.AutoPlayNextVideo)
                {
                    _currentIndex++;
                    _currentItem = _playlist[_currentIndex];
                    LoadVideo(_currentItem);
                    // 自动播放下一个后刷新队列（从新的当前项开始排序）
                    RefreshQueueIfOpen();
                }
                else
                {
                    UpdatePlayPauseIcon(false);
                    ShowControls();
                    UpdateCenterPlayButton();
                    // 播放到最后一个：刷新队列，让首项变为最后正在播放的视频（类似音乐播放器）
                    RefreshQueueIfOpen();
                }
            });
        }

        /// <summary>超分模式 mpv 日志（带级别，按需记录，避免 V 级细节日志刷爆日志文件）。</summary>
        private void OnMpvLogMessage(SightoHear.Mpv.Enums.Client.MpvLogLevel level, string message)
        {
            switch (level)
            {
                // 警告/错误：始终记录（便于定位播放异常，如解码失败、设备丢失等）
                case SightoHear.Mpv.Enums.Client.MpvLogLevel.Error:
                case SightoHear.Mpv.Enums.Client.MpvLogLevel.Fatal:
                case SightoHear.Mpv.Enums.Client.MpvLogLevel.Warn:
                    AppLogger.Warning($"mpv: {message}");
                    break;
                // V/Info 等详细信息：仅当全局日志级别为 Debug/Trace 时才落盘（排查用），
                // 默认 Info 级别下不写入文件，避免单次播放产生数千行日志
                default:
                    AppLogger.Debug($"mpv: {message}");
                    break;
            }
        }

        /// <summary>
        /// 超分模式断点续播：页面卸载时把当前视频转交给音乐播放器继续播放（记录断点位置）。
        /// </summary>
        private async Task TransferVideoToMusicAsync(MediaItem item, List<MediaItem> queue, double position)
        {
            try
            {
                await App.MusicPlayback.PlayAsync(item, queue);
                // 等待媒体打开后恢复断点位置
                for (int i = 0; i < 30; i++)
                {
                    var session = App.MusicPlayback.ActivePlayer?.PlaybackSession;
                    if (session != null && session.NaturalDuration.TotalSeconds > 0)
                    {
                        App.MusicPlayback.SetPosition(TimeSpan.FromSeconds(position));
                        break;
                    }
                    await Task.Delay(100);
                }
                AppLogger.Info($"libmpv断点续播到音乐播放器: {item.FileName}, 位置={position:0.00}s");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"libmpv断点续播到音乐播放器失败: {item.FileName}");
            }
        }

        private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (PlayerElement.MediaPlayer != sender) return;
                    var duration = sender.PlaybackSession.NaturalDuration;
                    TotalTimeText.Text = FormatTime(duration);
                    ProgressSlider.Maximum = duration.TotalSeconds;
                    SyncPipProgress(sender.PlaybackSession.Position.TotalSeconds, duration.TotalSeconds);
                    // 修复倍速失效：Source 打开完成后，兜底重新应用用户选择的倍速，
                    // 防止 MediaPlayer 在加载阶段重置 PlaybackRate 为 1.0。
                    if (Math.Abs(sender.PlaybackSession.PlaybackRate - _playbackRate) > 0.01)
                        sender.PlaybackSession.PlaybackRate = _playbackRate;
                    AppLogger.Info(
                        $"MediaOpened: 目标倍速(_playbackRate)={_playbackRate:0.##}x, " +
                        $"实际 PlaybackRate={sender.PlaybackSession.PlaybackRate:0.##}x, " +
                        $"状态={sender.PlaybackSession.PlaybackState}");

                    // 画面比例：重新应用用户选择的比例（新视频原始宽高比不同，需按新尺寸重算拉伸）
                    ApplyNormalAspectRatio(_aspectRatio);

                    // 记忆播放位置（续播）：打开后 seek 到上次观看位置（转场路径不走 LoadVideo，
                    // _savedResumePosition 为 -1，不会误恢复，位置保持连续）
                    if (_savedResumePosition > 0)
                    {
                        var resume = _savedResumePosition;
                        _savedResumePosition = -1;
                        if (duration.TotalSeconds > 0 && resume >= duration.TotalSeconds - 10)
                        {
                            // 记忆位置已接近结尾视为已看完 → 清除记录，从头播放
                            if (_currentItem != null)
                                SightoHear.Services.VideoResumePositionService.ClearPosition(_currentItem.FilePath);
                            AppLogger.Info($"恢复位置已接近结尾，清除记忆从头播放: {resume:0.00}s / {duration.TotalSeconds:0.00}s");
                        }
                        else if (duration.TotalSeconds > 0)
                        {
                            sender.PlaybackSession.Position = TimeSpan.FromSeconds(resume);
                            AppLogger.Info($"已恢复记忆播放位置: {resume:0.00}s");
                        }
                    }

                    // "自动播放"设置：开启时打开视频自动开始播放；关闭时进入播放器保持暂停
                    // （转场路径不走 LoadVideo，MediaOpened 仅 LoadVideo 路径触发，不会误影响转场播放状态）
                    if (App.SettingsHelper.AutoPlayVideo)
                    {
                        sender.Play();
                        UpdatePlayPauseIcon(true);
                    }
                    else
                    {
                        sender.Pause();
                        UpdatePlayPauseIcon(false);
                        ShowControls();
                        UpdateCenterPlayButton();
                        AppLogger.Info("自动播放已关闭，视频打开后处于暂停状态");
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // 忽略已释放的 COM 对象访问
                }
            });
        }

        private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (PlayerElement.MediaPlayer != sender) return;
                    // 播放结束：清除该视频的记忆播放位置（下次从头播放）
                    if (_currentItem != null)
                        SightoHear.Services.VideoResumePositionService.ClearPosition(_currentItem.FilePath);
                    // "自动播放下一个"设置：开启时播放完毕自动播放下一个；关闭时停留在当前视频
                    // （中央播放按钮可点击重播，用户手动点"下一曲"仍可切换）
                    if (_currentIndex + 1 < _playlist.Count && App.SettingsHelper.AutoPlayNextVideo)
                    {
                        _currentIndex++;
                        _currentItem = _playlist[_currentIndex];
                        LoadVideo(_currentItem);
                        // 自动播放下一个后刷新队列（从新的当前项开始排序）
                        RefreshQueueIfOpen();
                    }
                    else
                    {
                        UpdatePlayPauseIcon(false);
                        ShowControls();
                        // 列表播放完毕（或关闭自动播放下一个）：确保中央播放按钮显示（允许点击重播）
                        UpdateCenterPlayButton();
                        // 播放到最后一个：刷新队列，让首项变为最后正在播放的视频（类似音乐播放器）
                        RefreshQueueIfOpen();
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // 忽略已释放的 COM 对象访问
                }
            });
        }

        private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (PlayerElement.MediaPlayer?.PlaybackSession != sender) return;
                    var session = sender;
                    if (session != null && session.NaturalVideoHeight != 0)
                    {
                        if (session.PlaybackState == MediaPlaybackState.Playing)
                        {
                            if (_displayRequest == null)
                                _displayRequest = new DisplayRequest();
                            try { _displayRequest.RequestActive(); } catch { }
                        }
                        else
                        {
                            if (_displayRequest != null)
                            {
                                try { _displayRequest.RequestRelease(); } catch { }
                                _displayRequest = null;
                            }
                        }
                    }

                    bool isPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
                    UpdatePlayPauseIcon(isPlaying);

                    // 播放状态/时间线已由 MediaPlayer 自动上报给系统 SMTC，这里无需手动更新。

                    if (isPlaying)
                    {
                        if (_isControlsVisible)
                            ResetHideControlsTimer();
                        // 开始播放：显示光标并重启"静止隐藏"计时
                        RestartCursorIdleTimer();
                    }
                    else
                    {
                        // 画中画模式：UI 仅由点击切换，暂停不强制显示控制栏
                        if (!_isPictureInPicture)
                            ShowControls();
                        // 暂停：停止静止隐藏计时，始终显示光标方便操作
                        _cursorIdleTimer?.Stop();
                    }

                    // 暂停显示中央播放按钮、恢复播放后隐藏。
                    // 此处无条件调用：ShowControls 在控件已可见时会短路返回，
                    // 不能依赖它来更新中央按钮，否则暂停后按钮会不显示。
                    UpdateCenterPlayButton();
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // 忽略已释放的 COM 对象访问
                }
            });
        }

        /// <summary>
        /// 配置视频播放器的系统媒体命令（CommandManager）事件处理。
        /// 保持 SMTC 自动集成（播放状态/时间线/媒体属性自动上报给系统）的同时，
        /// 自定义上一曲/下一曲/播放/暂停等按钮行为。
        /// </summary>
        private void ConfigureVideoMediaCommands(MediaPlayer player)
        {
            var commandManager = player.CommandManager;
            // 单个媒体项（非 MediaPlaybackList）时，系统默认禁用上一曲/下一曲按钮，
            // 强制启用，便于通过系统媒体控件/键盘媒体键切换视频。
            commandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;

            commandManager.PlayReceived += VideoCommandManager_PlayReceived;
            commandManager.PauseReceived += VideoCommandManager_PauseReceived;
            commandManager.NextReceived += VideoCommandManager_NextReceived;
            commandManager.PreviousReceived += VideoCommandManager_PreviousReceived;

            // 系统媒体命令管理器没有"停止"事件，通过 ButtonPressed 事件单独处理停止按钮。
            // 注意：仅响应 Stop，播放/暂停/上一曲/下一曲均由上面的 CommandManager 事件处理，
            // 避免同一命令被重复执行。
            // ★ 修复：改用具名方法订阅（不再捕获 player 闭包/this），
            //   以便页面卸载时能够退订，防止 MediaPlayer 转移给服务后仍引用本页面。
            _smtcOwner = player;
            player.SystemMediaTransportControls.ButtonPressed += VideoSmtc_ButtonPressed;
        }

        /// <summary>
        /// ★ 修复：退订视频播放器的系统媒体命令（CommandManager）与 SMTC 停止按钮事件。
        /// 在页面卸载或播放器被销毁/转移前调用，打破"MediaPlayer → 事件 → 页面"的
        /// 强引用链，否则页面实例无法被 GC 回收，其持有的 Win2D/GPU 资源持续泄漏。
        /// </summary>
        private void UnsubscribeMediaCommands(MediaPlayer player)
        {
            try
            {
                var commandManager = player.CommandManager;
                commandManager.PlayReceived -= VideoCommandManager_PlayReceived;
                commandManager.PauseReceived -= VideoCommandManager_PauseReceived;
                commandManager.NextReceived -= VideoCommandManager_NextReceived;
                commandManager.PreviousReceived -= VideoCommandManager_PreviousReceived;
                player.SystemMediaTransportControls.ButtonPressed -= VideoSmtc_ButtonPressed;
            }
            catch (Exception ex)
            {
                // 播放器已释放时访问 CommandManager 可能抛 COM 异常，忽略即可
                AppLogger.Warning($"退订视频媒体命令事件失败（播放器可能已释放）: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ 修复：清理"加载被取代后遗留"的 MediaPlayer（退订事件、断开 UI 引用并释放）。
        /// 用于 LoadVideo 的 await 竞态路径：页面卸载/切换视频导致当前加载失效时，
        /// 该播放器无人接管，若不清理，其 CommandManager/SMTC 事件订阅会持续引用本页面，
        /// 造成页面与 Win2D/GPU 资源泄漏。
        /// </summary>
        private void CleanupAbandonedMediaPlayer(MediaPlayer mediaPlayer)
        {
            try
            {
                UnsubscribeMediaCommands(mediaPlayer);
                mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
                mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
                mediaPlayer.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
                mediaPlayer.Source = null;
                if (PlayerElement.MediaPlayer == mediaPlayer)
                    PlayerElement.SetMediaPlayer(null);
                mediaPlayer.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"清理遗留 MediaPlayer 失败: {ex.Message}");
            }
            finally
            {
                if (_smtcOwner == mediaPlayer)
                    _smtcOwner = null;
            }
        }

        /// <summary>
        /// 仅处理系统媒体控件的"停止"按钮（等价于暂停，与之前行为一致）。
        /// 该事件在后台线程触发，需回到 UI 线程执行操作。
        /// </summary>
        private void VideoSmtc_ButtonPressed(object? sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            if (args.Button != SystemMediaTransportControlsButton.Stop) return;
            var player = _smtcOwner;
            if (player == null) return;
            DispatcherQueue.TryEnqueue(() => player.Pause());
        }

        /// <summary>
        /// 处理视频播放器的系统媒体播放命令（该事件在后台线程触发，需回到 UI 线程执行操作）。
        /// </summary>
        private void VideoCommandManager_PlayReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerPlayReceivedEventArgs args)
        {
            args.Handled = true;
            DispatcherQueue.TryEnqueue(() => PlayerElement.MediaPlayer?.Play());
        }

        private void VideoCommandManager_PauseReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerPauseReceivedEventArgs args)
        {
            args.Handled = true;
            DispatcherQueue.TryEnqueue(() => PlayerElement.MediaPlayer?.Pause());
        }

        private void VideoCommandManager_NextReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerNextReceivedEventArgs args)
        {
            args.Handled = true;
            DispatcherQueue.TryEnqueue(() => NextButton_Click(this, new RoutedEventArgs()));
        }

        private void VideoCommandManager_PreviousReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerPreviousReceivedEventArgs args)
        {
            args.Handled = true;
            DispatcherQueue.TryEnqueue(() => PreviousButton_Click(this, new RoutedEventArgs()));
        }

        /// <summary>
        /// 为视频 MediaPlaybackItem 应用系统媒体传输控件 (SMTC) 的显示属性（标题、封面等）。
        /// 这是自动 SMTC 集成模式下官方推荐的方式，元数据随媒体项一起上报给系统，
        /// 外部软件通过 GlobalSystemMediaTransportControlsSession.TryGetMediaPropertiesAsync()
        /// 即可读取。注意：不要改用 smtc.DisplayUpdater 手动设置——那会被 MediaPlayer
        /// 在加载 Source 时覆盖。
        /// </summary>
        private static async Task ApplyVideoItemDisplayPropertiesAsync(MediaPlaybackItem playbackItem, MediaItem item)
        {
            try
            {
                var props = playbackItem.GetDisplayProperties();
                props.Type = MediaPlaybackType.Video;
                props.VideoProperties.Title = string.IsNullOrEmpty(item.Title) ? item.FileName : item.Title;

                // 设置封面缩略图
                if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.ThumbnailPath);
                    var stream = await file.OpenReadAsync();
                    props.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                }

                playbackItem.ApplyDisplayProperties(props);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"应用视频 SMTC 显示属性失败: {item.FileName}");
            }
            // 确保 async 方法始终包含 await，避免编译器警告 CS1998。
            await Task.CompletedTask;
        }

        private void PositionTimer_Tick(object? sender, object e)
        {
            // 超分模式：位置由 mpv PositionChanged 事件驱动，无需轮询
            if (_isMpvMode)
                return;

            if (PlayerElement.MediaPlayer == null || _isDraggingSlider)
                return;

            var session = PlayerElement.MediaPlayer.PlaybackSession;
            if (session.NaturalDuration.TotalSeconds > 0)
            {
                ProgressSlider.Value = session.Position.TotalSeconds;
                CurrentTimeText.Text = FormatTime(session.Position);
                SyncPipProgress(session.Position.TotalSeconds, session.NaturalDuration.TotalSeconds);
                // 记忆播放位置（续播）：节流保存
                TrySaveResumePosition(session.Position.TotalSeconds, session.NaturalDuration.TotalSeconds);
            }
        }

        /// <summary>同步画中画简化控制栏的进度显示（进度条/时间文本，仅在画中画模式更新）。</summary>
        private void SyncPipProgress(double position, double duration)
        {
            if (PipControls.Visibility != Visibility.Visible) return;
            if (duration > 0)
            {
                PipProgressSlider.Maximum = duration;
                PipProgressSlider.Value = position;
                PipTotalTimeText.Text = FormatTime(duration);
            }
            PipCurrentTimeText.Text = FormatTime(position);
        }

        /// <summary>
        /// 是否启用播放进度记忆（视频设置"记忆全部视频播放进度"或播放器弹窗"记忆当前视频播放进度"任一开启）。
        /// </summary>
        private bool IsResumePositionEnabled
            => App.SettingsHelper.RememberVideoPosition || App.SettingsHelper.RememberCurrentVideoPosition;

        /// <summary>
        /// 记忆播放位置（续播）：保存当前视频的播放位置（节流，避免高频写盘）。
        /// 仅在"记忆播放位置"设置开启、已播放到一定进度（≥5 秒）时记录；
        /// 播放到接近结尾（最后 10 秒）视为已看完，清除记录。
        /// </summary>
        private void TrySaveResumePosition(double position, double duration)
        {
            if (!IsResumePositionEnabled) return;
            if (_currentItem == null || duration <= 0 || position <= 0) return;

            // 接近结尾（最后 10 秒）视为已看完 → 清除记忆，下次从头播放
            if (position >= duration - 10)
            {
                SightoHear.Services.VideoResumePositionService.ClearPosition(_currentItem.FilePath);
                return;
            }
            // 播放不足 5 秒不记录（未真正开始观看，避免误开即记）
            if (position < 5) return;
            // 节流：与上次保存位置相差不足 5 秒则跳过（普通模式 250ms 轮询 / mpv 事件高频触发）
            if (_lastSavedResumePosition > 0 && position - _lastSavedResumePosition < 5)
                return;

            SightoHear.Services.VideoResumePositionService.SavePosition(
                _currentItem.FilePath, position, duration);
            _lastSavedResumePosition = position;
        }

        // 靠近上下边缘时显示 UI 的判定阈值（像素）：
        // 值越大，显示 UI 的区域越靠近屏幕中间，鼠标不必贴着边缘也能唤出控制栏
        private const double TopEdgeRevealHeight = 200;
        private const double BottomEdgeRevealHeight = 260;
        // 鼠标移到屏幕中间后延迟隐藏 UI 的时长（毫秒）
        private const double DelayedHideDelayMs = 1100;

        /// <summary>
        /// 根据鼠标纵向位置控制播放器 UI 的显示/隐藏（播放中）：
        /// 鼠标靠近上下边缘时显示 UI；移到屏幕中间区域时延迟 1.1 秒后再隐藏。
        /// 暂停/未播放时始终显示，方便操作。
        /// </summary>
        private void UpdateControlsByPointerPosition(double y)
        {
            // 画中画模式：悬停不显示/不隐藏 UI，仅由点击视频区域切换（见 TogglePipControls）
            if (_isPictureInPicture)
            {
                CancelDelayedHide();
                return;
            }

            bool isPlaying = GetIsPlaying();

            // 暂停/未播放时始终显示控件
            if (!isPlaying)
            {
                ShowControls();
                CancelDelayedHide();
                return;
            }

            // 拖动进度条时保持控件可见，避免拖到中间被隐藏
            if (_isDraggingSlider)
            {
                ShowControls();
                CancelDelayedHide();
                return;
            }

            bool nearTop = y <= TopEdgeRevealHeight;
            bool nearBottom = y >= RootGrid.ActualHeight - BottomEdgeRevealHeight;
            if (nearTop || nearBottom)
            {
                // 靠近边缘：立即显示，并取消待执行的延迟隐藏
                ShowControls();
                CancelDelayedHide();
            }
            else
            {
                // 中间区域：不立即隐藏，延迟 1.1 秒后再隐藏
                StartDelayedHide();
            }
        }

        /// <summary>
        /// 启动延迟隐藏定时器：鼠标停在中间 1.1 秒后自动隐藏 UI。
        /// 定时器已在运行时不重复启动，避免鼠标在中间移动时不断重置计时。
        /// </summary>
        private void StartDelayedHide()
        {
            if (_delayedHideTimer == null)
            {
                _delayedHideTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(DelayedHideDelayMs)
                };
                _delayedHideTimer.Tick += DelayedHideTimer_Tick;
            }
            if (_delayedHideTimer.IsEnabled) return;
            _delayedHideTimer.Start();
        }

        private void CancelDelayedHide()
        {
            _delayedHideTimer?.Stop();
        }

        private void DelayedHideTimer_Tick(object? sender, object e)
        {
            _delayedHideTimer?.Stop();
            HideControls();
        }

        /// <summary>
        /// 首次进入播放器时的过渡定时器：UI 短暂显示 1.5 秒后，
        /// 根据鼠标位置决定保持显示（靠近边缘）或自动隐藏（在中间/未移动）。
        /// 首次过渡结束的隐藏不经过延迟定时器，直接按位置判定。
        /// </summary>
        private void IntroTimer_Tick(object? sender, object e)
        {
            _introTimer?.Stop();

            // 鼠标从未移动过（位置未知）则视为位于屏幕中间，播放中自动隐藏 UI
            if (double.IsNaN(_lastPointerY))
                _lastPointerY = RootGrid.ActualHeight / 2;

            var session = PlayerElement.MediaPlayer?.PlaybackSession;
            bool isPlaying = _isMpvMode ? GetIsPlaying() : session?.PlaybackState == MediaPlaybackState.Playing;

            // 暂停/未播放或拖动中：保持显示
            if (!isPlaying || _isDraggingSlider)
            {
                ShowControls();
                return;
            }

            bool nearTop = _lastPointerY <= TopEdgeRevealHeight;
            bool nearBottom = _lastPointerY >= RootGrid.ActualHeight - BottomEdgeRevealHeight;
            if (nearTop || nearBottom)
                ShowControls();
            else
                HideControls();
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            ShowCursorSafe();
            // 记录最近一次鼠标纵向位置，供首次进入的过渡定时器判断使用
            _lastPointerY = e.GetCurrentPoint(RootGrid).Position.Y;
            // 位置驱动：中心隐藏 UI，只有靠近上下边缘时才显示
            UpdateControlsByPointerPosition(_lastPointerY);
            // 指针移动即显示光标，并重启"静止隐藏"计时
            RestartCursorIdleTimer();
        }

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_isVolumeFlyoutOpen) return;

            // 指针离开窗口：停止空闲隐藏计时，避免在窗口外触发
            _cursorIdleTimer?.Stop();
            CancelDelayedHide();
            if (GetIsPlaying() && !_isDraggingSlider)
            {
                HideControls();
            }
        }

        /// <summary>画中画模式点击视频区域：切换简化控制栏的显示/隐藏（不再触发播放/暂停）。</summary>
        private void TogglePipControls()
        {
            if (_isControlsVisible)
            {
                HideControls();
            }
            else
            {
                ShowControls();
            }
        }

        private void PlayerElement_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(PlayerElement).Properties;
            if (!props.IsLeftButtonPressed) return;

            // 画中画模式：点击视频区域 = 切换 UI 显示/隐藏（悬停不显示）
            if (_isPictureInPicture)
            {
                TogglePipControls();
                return;
            }

            // 防止点击窗口右上角（最小化/关闭按钮区域）时误触播放/暂停
            // 在全屏模式下 PlayerElement 填满整个窗口，窗口控制按钮覆盖在顶层
            // 但 PointerPressed 仍可能穿透到 PlayerElement
            try
            {
                var pos = e.GetCurrentPoint(RootGrid).Position;
                double winW = RootGrid.ActualWidth;
                double winH = RootGrid.ActualHeight;
                // 排除右上角 140×52 区域（窗口控制按钮区）
                if (pos.X > winW - 140 && pos.Y < 52)
                    return;
            }
            catch { }

            // 如果点击在控件区（底部控制栏可见时），让底部按钮处理，不在此切换
            var bottomBounds = BottomControls.IsHitTestVisible
                ? new Windows.Foundation.Rect(0, RootGrid.ActualHeight - 120, RootGrid.ActualWidth, 120)
                : Windows.Foundation.Rect.Empty;
            try
            {
                var pt = e.GetCurrentPoint(RootGrid).Position;
                if (bottomBounds.Width > 0 && bottomBounds.Contains(pt))
                    return;
            }
            catch { }

            TogglePlayPause();
        }

        /// <summary>超分模式下渲染宿主点击事件（与 PlayerElement 点击行为一致）。</summary>
        private void MpvVideoRender_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(MpvVideoRender).Properties;
            if (!props.IsLeftButtonPressed) return;

            // 画中画模式：点击视频区域 = 切换 UI 显示/隐藏（悬停不显示）
            if (_isPictureInPicture)
            {
                TogglePipControls();
                return;
            }

            TogglePlayPause();
        }

        private void PlayerBackButton_Click(object sender, RoutedEventArgs e)
        {
            // 画中画模式：左上角按钮为"关闭画中画"——退出画中画恢复完整窗口（视频继续播放），
            // 而不是返回主页（返回主页会退出播放器）
            if (_isPictureInPicture)
            {
                ExitPictureInPicture();
                return;
            }

            // ★ 覆盖层内还有上一页：先返回上一页（如从音乐播放器切换过来的场景），
            //   仅当覆盖层第一页时才关闭整个覆盖层退出播放器
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
                return;
            }

            // 在覆盖层中，直接隐藏覆盖层退出播放器
            if (App.MainWindow is MainWindow mw)
            {
                mw.HidePlayerOverlay();
                return;
            }
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private void VideoPlayerPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // 空格键由 RootGrid 的 handledEventsToo 处理器统一处理（见 RootGrid_SpaceKeyDown）
            if (e.Key == VirtualKey.Space)
                return;
            // 自定义快捷键：优先于内置快捷键处理
            if (TryExecuteShortcut(e, isKeyUp: false))
                return;

            switch (e.Key)
            {
                case VirtualKey.Escape:
                    if (_isSystemFullScreen)
                    {
                        ExitFullScreen();
                    }
                    // ★ 覆盖层内还有上一页：先返回上一页
                    else if (Frame.CanGoBack)
                    {
                        Frame.GoBack();
                    }
                    else if (App.MainWindow is MainWindow mw && mw.IsPlayerOverlayActive)
                    {
                        mw.HidePlayerOverlay();
                    }
                    else if (Frame.CanGoBack)
                    {
                        Frame.GoBack();
                    }
                    e.Handled = true;
                    break;
                case VirtualKey.Left:
                    Skip(-10);
                    e.Handled = true;
                    break;
                case VirtualKey.Right:
                    Skip(10);
                    e.Handled = true;
                    break;
                case VirtualKey.F:
                    ToggleSystemFullScreen();
                    e.Handled = true;
                    break;
                case VirtualKey.Up:
                    ChangeVolume(0.1);
                    e.Handled = true;
                    break;
                case VirtualKey.Down:
                    ChangeVolume(-0.1);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>松开按键时的键盘处理：触发"松开执行"模式的自定义快捷键。</summary>
        private void VideoPlayerPage_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            // 空格键由 RootGrid 的 handledEventsToo 处理器统一处理（见 RootGrid_SpaceKeyDown/Up）
            if (e.Key == VirtualKey.Space)
                return;
            TryExecuteShortcut(e, isKeyUp: true);
        }

        /// <summary>
        /// 空格键处理挂接（Loaded 时调用）：
        /// 1. 遍历全部按钮挂 handledEventsToo，按下空格瞬间临时禁用按钮、松开恢复，
        ///    阻止聚焦按钮时空格触发 Click（空格保留为播放器快捷键，而非"确定"键）；
        /// 2. RootGrid 以 handledEventsToo 接收空格键（含被按钮类处理器标记已处理的情况），
        ///    统一执行空格绑定的快捷键。
        /// </summary>
        private void AttachSpaceKeyHandlers()
        {
            if (_spaceKeyHandlersAttached)
                return;
            _spaceKeyHandlersAttached = true;

            DisableButtonSpaceActivation(RootGrid);
            RootGrid.AddHandler(UIElement.KeyDownEvent, _spaceKeyDownHandler, true);
            RootGrid.AddHandler(UIElement.KeyUpEvent, _spaceKeyUpHandler, true);
        }

        /// <summary>递归遍历视觉树，为所有 Button 挂接空格拦截（仅焦点在该按钮时禁用，松开恢复）。</summary>
        private static void DisableButtonSpaceActivation(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button button)
                {
                    // handledEventsToo：按钮类处理器已处理 Space（触发 Click），需监听已处理事件。
                    // 仅当焦点就在该按钮上时才临时禁用，避免播放中按空格导致整排按钮闪烁禁用态。
                    button.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
                    {
                        if (e.Key == VirtualKey.Space &&
                            ReferenceEquals(FocusManager.GetFocusedElement(button.XamlRoot), button))
                            button.IsEnabled = false;
                    }), true);
                    button.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, e) =>
                    {
                        if (e.Key == VirtualKey.Space)
                            button.IsEnabled = true;
                    }), true);
                }
                DisableButtonSpaceActivation(child);
            }
        }

        /// <summary>空格键按下：执行"按下执行"模式的空格快捷键（handledEventsToo，可收到按钮已处理的按键）。</summary>
        private void RootGrid_SpaceKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Space)
                return;
            TryExecuteShortcutCore(e.Key, isKeyUp: false);
            e.Handled = true;
        }

        /// <summary>空格键松开：执行"松开执行"模式的空格快捷键。</summary>
        private void RootGrid_SpaceKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Space)
                return;
            TryExecuteShortcutCore(e.Key, isKeyUp: true);
            e.Handled = true;
        }

        /// <summary>
        /// 匹配并执行自定义快捷键（视频设置 → 快捷键）。
        /// 需同时匹配主键、修饰键（Ctrl/Alt/Shift）与触发时机（按下/松开）。
        /// </summary>
        private bool TryExecuteShortcut(KeyRoutedEventArgs e, bool isKeyUp)
        {
            bool handled = TryExecuteShortcutCore(e.Key, isKeyUp);
            if (handled)
                e.Handled = true;
            return handled;
        }

        /// <summary>快捷键匹配核心：遍历绑定列表执行匹配的快捷键行为。</summary>
        private bool TryExecuteShortcutCore(VirtualKey key, bool isKeyUp)
        {
            // 纯修饰键本身不作为快捷键触发（等待主键）
            if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
                or VirtualKey.LeftWindows or VirtualKey.RightWindows)
                return false;

            var (ctrl, alt, shift) = SightoHear.Helpers.ShortcutKeyHelper.GetModifierState();
            foreach (var item in SightoHear.Services.VideoShortcutService.GetAllBindings())
            {
                if (!item.Enabled || !item.HasKey)
                    continue;
                if (item.KeyCode != (int)key)
                    continue;
                if (item.Ctrl != ctrl || item.Alt != alt || item.Shift != shift)
                    continue;
                if (item.ExecuteOnKeyUp != isKeyUp)
                    continue;

                ExecuteShortcutAction(item.ActionId);
                return true;
            }
            return false;
        }

        /// <summary>执行指定快捷键行为。</summary>
        private void ExecuteShortcutAction(string actionId)
        {
            switch (actionId)
            {
                case "TogglePlayPause":
                    TogglePlayPause();
                    break;
                case "VolumeUp":
                    ChangeVolume(0.1);
                    break;
                case "VolumeDown":
                    ChangeVolume(-0.1);
                    break;
                case "NextVideo":
                    NextButton_Click(this, new RoutedEventArgs());
                    break;
                case "PreviousVideo":
                    PreviousButton_Click(this, new RoutedEventArgs());
                    break;
                case "ToggleFullScreen":
                    ToggleSystemFullScreen();
                    break;
                case "Forward10":
                    Skip(10);
                    break;
                case "Backward10":
                    Skip(-10);
                    break;
            }
            AppLogger.Info($"视频快捷键触发: {SightoHear.Services.VideoShortcutService.GetActionName(actionId)}");
        }

        /// <summary>
        /// 隐藏鼠标指针（WinUI 3 已验证方案，见 WindowsAppSDK Discussion #3601）：
        /// 把系统箭头光标设置给页面根元素的 ProtectedCursor，然后立即 Dispose 销毁该光标对象。
        /// 光标对象被销毁后 XAML 不再渲染它，指针随之消失；下次隐藏需重新 Create。
        /// 注意：ShowCursor(FALSE) 等传统 GDI API 对 WinUI 3 客户端区域无效，
        /// 必须通过 UIElement.ProtectedCursor 控制。
        /// </summary>
        private void HideCursor()
        {
            // 画中画模式：光标常驻可见（小窗需要精确操作，且 UI 仅由点击切换）
            if (_isPictureInPicture) return;
            if (_isCursorHidden) return;
            var cursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            SetProtectedCursor(RootGrid, cursor);
            // 关键步骤：销毁刚设置的光标对象使指针消失
            cursor.Dispose();
            _isCursorHidden = true;
        }

        private void ShowCursorSafe()
        {
            if (_isCursorHidden)
            {
                // 设为 null 即恢复系统默认光标
                SetProtectedCursor(RootGrid, null);
                _isCursorHidden = false;
            }
        }

        // ---- 光标相关辅助 ----

        /// <summary>
        /// 通过反射设置 UIElement 的 protected 属性 ProtectedCursor。
        /// 这是 WinUI 3 官方推荐的光标切换方式（见 GitHub WindowsAppSDK#1816），
        /// 但该属性是 protected，只能通过反射访问。
        /// </summary>
        private static void SetProtectedCursor(UIElement element, InputCursor? cursor)
        {
            typeof(UIElement).InvokeMember(
                "ProtectedCursor",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.SetProperty,
                null,
                element,
                new object?[] { cursor });
        }

        /// <summary>
        /// 重启"光标静止隐藏"计时器：
        /// 播放中指针移动时调用，指针停止移动超过 CursorIdleHideMs 后自动隐藏光标；
        /// 暂停/未播放时停止计时并保持光标可见。
        /// </summary>
        private void RestartCursorIdleTimer()
        {
            // 画中画模式：光标常驻可见，不做静止隐藏
            if (_isPictureInPicture) return;

            var session = PlayerElement.MediaPlayer?.PlaybackSession;
            bool isPlaying = session?.PlaybackState == MediaPlaybackState.Playing;

            if (!isPlaying)
            {
                _cursorIdleTimer?.Stop();
                ShowCursorSafe();
                return;
            }

            if (_cursorIdleTimer == null)
            {
                _cursorIdleTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(CursorIdleHideMs)
                };
                _cursorIdleTimer.Tick += CursorIdleTimer_Tick;
            }
            _cursorIdleTimer.Stop();
            _cursorIdleTimer.Start();
        }

        private void CursorIdleTimer_Tick(object? sender, object e)
        {
            _cursorIdleTimer?.Stop();

            var session = PlayerElement.MediaPlayer?.PlaybackSession;
            bool isPlaying = session?.PlaybackState == MediaPlaybackState.Playing;
            // 拖动进度条时保持光标可见，便于精确操作
            if (isPlaying && !_isDraggingSlider)
                HideCursor();
        }

        private void ShowControls()
        {
            ShowCursorSafe();
            if (_isControlsVisible) return;
            _isControlsVisible = true;

            if (_isPictureInPicture)
            {
                // 画中画模式：只显示简化控制栏 + 底部阴影（视频区域小，仍需阴影增强对比度）+ 关闭画中画按钮
                AnimateOpacity(PipControls, 1);
                PipControls.IsHitTestVisible = true;
                AnimateOpacity(BottomGradient, 1);
                AnimateOpacity(PlayerBackButton, 1);
                PlayerBackButton.IsHitTestVisible = true;
                return;
            }

            AnimateOpacity(TopBar, 1);
            AnimateOpacity(BottomControls, 1);
            BottomControls.IsHitTestVisible = true;
            AnimateOpacity(BottomGradient, 1);
            AnimateOpacity(PlayerBackButton, 1);
            PlayerBackButton.IsHitTestVisible = true;

            // 暂停时显示中央大播放按钮，播放时隐藏
            UpdateCenterPlayButton();
        }

        private void HideControls()
        {
            if (!_isControlsVisible) return;
            if (_isVolumeFlyoutOpen) return;

            // 停止延迟隐藏定时器，避免重复触发
            _delayedHideTimer?.Stop();
            _isControlsVisible = false;

            if (_isPictureInPicture)
            {
                // 画中画模式：简化控制栏与关闭按钮一起淡出（阴影同步淡出，保持对比度一致性）
                AnimateOpacity(PipControls, 0, 1000);
                PipControls.IsHitTestVisible = false;
                AnimateOpacity(BottomGradient, 0, 1000);
                AnimateOpacity(PlayerBackButton, 0, 1000);
                PlayerBackButton.IsHitTestVisible = false;
                return;
            }

            // 渐隐动画调慢为 1000ms，整个 UI 一起缓慢淡出，避免鼠标移到中心时消失过于突兀
            AnimateOpacity(TopBar, 0, 1000);
            AnimateOpacity(BottomControls, 0, 1000);
            BottomControls.IsHitTestVisible = false;
            AnimateOpacity(BottomGradient, 0, 1000);
            AnimateOpacity(PlayerBackButton, 0, 1000);
            PlayerBackButton.IsHitTestVisible = false;
            UpdateCenterPlayButton();
            HideCursor();
        }

        private static void AnimateOpacity(UIElement element, double opacity, double durationMs = 200)
        {
            var animation = new DoubleAnimation
            {
                To = opacity,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        /// <summary>
        /// 兼容保留：原先的 2 秒定时隐藏已由"鼠标位置驱动"（中心隐藏、边缘显示）替代，
        /// 该方法不再需要启动任何定时器，保留空实现以避免改动所有调用点。
        /// </summary>
        private void ResetHideControlsTimer()
        {
            // 无操作：UI 显示/隐藏完全由 UpdateControlsByPointerPosition 按鼠标位置驱动
        }

        private void TogglePlayPause()
        {
            if (_isMpvMode)
            {
                var mpv = _mpvVideo;
                if (mpv == null) return;

                // ★ 根据 mpv 真实暂停状态切换，不再依赖 State 事件：
                //   此前 State 事件在部分暂停场景（缓冲、外部暂停等）不会同步更新，
                //   导致按钮显示"播放中"但实际已暂停，此时 TogglePlayPause 走 Pause()
                //   变成无操作（本来就暂停），表现为"点击无反应"。
                bool? paused = mpv.IsPausedNow;
                if (paused == true)
                {
                    mpv.Play();
                }
                else if (paused == false)
                {
                    mpv.Pause();
                }
                else
                {
                    // 无法读取真实状态（媒体未加载等）：回退到 State 事件切换
                    mpv.TogglePlayPause();
                }

                // ★ 关键时序：mpv 的 pause 属性设置是异步生效的——命令发出后立即读
                //   IsPausedNow 会拿到旧值，导致图标与实际播放状态相反（点击播放却显示
                //   暂停图标、点击暂停却显示播放图标）。因此不在此处立即刷新，
                //   而是延迟 ~120ms 待属性生效后再按真实状态刷新图标与中央按钮；
                //   PlaybackStateChanged 事件在 mpv 状态真实变化后也会正常驱动图标更新。
                _ = DelayedRefreshPlaybackUiAsync();
                return;
            }

            var player = PlayerElement.MediaPlayer;
            if (player == null) return;

            if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                player.Pause();
            }
            else
            {
                player.Play();
            }
        }

        /// <summary>
        /// mpv 播放/暂停点击后延迟刷新 UI：等待 pause 属性异步生效后再读取真实状态，
        /// 避免立即读取拿到旧值导致图标与播放状态不符。
        /// </summary>
        private async Task DelayedRefreshPlaybackUiAsync()
        {
            try
            {
                await Task.Delay(120);
            }
            catch
            {
                return;
            }
            // 页面已卸载或已切换内核：不再刷新
            if (!_isMpvMode || _mpvVideo == null) return;
            bool isPlaying = GetIsPlaying();
            UpdatePlayPauseIcon(isPlaying);
            UpdateCenterPlayButton();
        }

        private void UpdatePlayPauseIcon(bool isPlaying)
        {
            // 仅更新底部播放/暂停按钮的图标
            // 中间大播放按钮的显示/隐藏由 UpdateCenterPlayButton 独立管理
            PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
            // 画中画简化控制栏的播放/暂停按钮图标同步
            PipPlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
        }

        /// <summary>
        /// 屏幕中央大播放按钮的显示/隐藏逻辑（重写）：
        /// 规则：视频暂停/未播放时在屏幕中央显示圆形播放按钮，恢复播放后隐藏。
        /// 只依赖播放状态，不依赖底部控件可见性，避免暂停时按钮不显示。
        /// </summary>
        private void UpdateCenterPlayButton()
        {
            // 画中画模式：隐藏中央大播放按钮（暂停时由简化控制栏的播放按钮承担）
            if (_isPictureInPicture)
            {
                CenterPlayButton.Visibility = Visibility.Collapsed;
                CenterPlayButton.IsHitTestVisible = false;
                return;
            }

            bool isPlaying = GetIsPlaying();

            if (!isPlaying)
            {
                // 暂停/未播放：显示中央播放按钮
                if (CenterPlayButton.Visibility != Visibility.Visible)
                {
                    CenterPlayButton.Visibility = Visibility.Visible;
                    CenterPlayButton.Opacity = 0;
                }
                AnimateOpacity(CenterPlayButton, 1);
                CenterPlayButton.IsHitTestVisible = true;
            }
            else
            {
                // 播放中：隐藏中央播放按钮
                CenterPlayButton.Visibility = Visibility.Collapsed;
                CenterPlayButton.Opacity = 0;
                CenterPlayButton.IsHitTestVisible = false;
            }
        }

        private void PlayPauseKeyboardAccelerator_Invoked(
            KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            TogglePlayPause();
            ShowControls();
            ResetHideControlsTimer();
            args.Handled = true;
        }

        private void Skip(double seconds)
        {
            if (_isMpvMode)
            {
                _mpvVideo?.Seek(Math.Clamp(GetPositionSeconds() + seconds, 0, Math.Max(GetDurationSeconds(), 0)));
                return;
            }

            var session = PlayerElement.MediaPlayer?.PlaybackSession;
            if (session == null) return;

            var newPosition = session.Position.Add(TimeSpan.FromSeconds(seconds));
            newPosition = TimeSpan.FromSeconds(Math.Clamp(newPosition.TotalSeconds, 0, session.NaturalDuration.TotalSeconds));
            session.Position = newPosition;
        }

        private void ChangeVolume(double delta)
        {
            if (_isMpvMode)
            {
                double mpvVol = Math.Clamp(_previousVolume + delta, 0, 1);
                _isVolumeMuted = mpvVol <= 0;
                if (mpvVol > 0)
                    _previousVolume = mpvVol;
                _mpvVideo?.SetVolume((int)Math.Round(mpvVol * 100));
                _mpvVideo?.SetMuted(mpvVol <= 0);
                SyncVolumeUi(mpvVol);
                return;
            }

            var player = PlayerElement.MediaPlayer;
            if (player == null) return;

            double vol = Math.Clamp(player.Volume + delta, 0, 1);
            player.Volume = vol;
            _isVolumeMuted = vol <= 0;
            if (vol > 0)
                _previousVolume = vol;
            SyncVolumeUi(vol);
        }

        private void SyncVolumeUi(double volume)
        {
            volume = Math.Clamp(volume, 0, 1);
            string glyph = GetVolumeGlyph(volume);
            int percent = (int)Math.Round(volume * 100);

            _isSyncingVolumeUi = true;
            try
            {
                VolumeIcon.Glyph = glyph;
                if (VolumeFlyoutIcon != null)
                    VolumeFlyoutIcon.Glyph = glyph;
                if (VolumePercentText != null)
                    VolumePercentText.Text = $"{percent}%";
                if (VolumeSlider != null && Math.Abs(VolumeSlider.Value - percent) > 0.1)
                    VolumeSlider.Value = percent;
            }
            finally
            {
                _isSyncingVolumeUi = false;
            }
        }

        private static string GetVolumeGlyph(double volume)
        {
            if (volume <= 0)
                return "\uE74F"; // Mute
            if (volume < 0.5)
                return "\uE993"; // Low volume
            return "\uE994"; // High volume
        }

        private void UpdateVolumeIcon()
        {
            if (_isMpvMode)
            {
                SyncVolumeUi(_isVolumeMuted ? 0 : _previousVolume);
                return;
            }
            SyncVolumeUi(PlayerElement.MediaPlayer?.Volume ?? 0);
        }

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingSlider = true;
            _wasPlayingBeforeDrag = GetIsPlaying();
            PauseCurrent();
            _positionTimer?.Stop();
        }

        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var slider = sender as Slider ?? ProgressSlider;
            SeekCurrent(slider.Value);
            CurrentTimeText.Text = FormatTime(GetPositionSeconds());
            if (PipControls.Visibility == Visibility.Visible)
                PipCurrentTimeText.Text = CurrentTimeText.Text;

            _isDraggingSlider = false;
            if (_wasPlayingBeforeDrag)
                PlayCurrent();
            ResetHideControlsTimer();

            _positionTimer?.Start();
        }

        private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingSlider)
            {
                var slider = sender as Slider ?? ProgressSlider;
                SeekCurrent(slider.Value);
                CurrentTimeText.Text = FormatTime(GetPositionSeconds());
                if (PipControls.Visibility == Visibility.Visible)
                    PipCurrentTimeText.Text = CurrentTimeText.Text;

                _isDraggingSlider = false;
                if (_wasPlayingBeforeDrag)
                    PlayCurrent();
                ResetHideControlsTimer();

                _positionTimer?.Start();
            }
        }

        private void ProgressSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_isDraggingSlider) return;

            SeekCurrent(e.NewValue);
            CurrentTimeText.Text = FormatTime(GetPositionSeconds());
            if (PipControls.Visibility == Visibility.Visible)
                PipCurrentTimeText.Text = CurrentTimeText.Text;
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
            ResetHideControlsTimer();
        }

        private void CenterPlayButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        private void SkipBackButton_Click(object sender, RoutedEventArgs e)
        {
            Skip(-10);
            ResetHideControlsTimer();
        }

        private void SkipForwardButton_Click(object sender, RoutedEventArgs e)
        {
            Skip(10);
            ResetHideControlsTimer();
        }

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            ResetHideControlsTimer();
        }

        private void VolumeFlyout_Opened(object sender, object e)
        {
            _isVolumeFlyoutOpen = true;
            if (_isMpvMode)
            {
                SyncVolumeUi(_isVolumeMuted ? 0 : _previousVolume);
            }
            else
            {
                SyncVolumeUi(PlayerElement.MediaPlayer?.Volume ?? 0);
            }
            ResetHideControlsTimer();
        }

        private void VolumeFlyout_Closed(object sender, object e)
        {
            _isVolumeFlyoutOpen = false;
        }

        // ====== 音量滑块（WinUI 3 原生 Slider）======

        private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isSyncingVolumeUi) return;

            double vol = Math.Clamp(e.NewValue / 100.0, 0, 1);
            if (_isMpvMode)
            {
                _isVolumeMuted = vol <= 0;
                if (vol > 0)
                    _previousVolume = vol;
                _mpvVideo?.SetVolume((int)Math.Round(vol * 100));
                _mpvVideo?.SetMuted(vol <= 0);
                SyncVolumeUi(vol);
                ResetHideControlsTimer();
                return;
            }

            var player = PlayerElement.MediaPlayer;
            if (player == null) return;

            player.Volume = vol;
            _isVolumeMuted = vol <= 0;
            if (vol > 0)
                _previousVolume = vol;
            SyncVolumeUi(vol);
            ResetHideControlsTimer();
        }

        private void VolumeMuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isMpvMode)
            {
                if (!_isVolumeMuted && _previousVolume > 0)
                {
                    _isVolumeMuted = true;
                    _mpvVideo?.SetVolume(0);
                    _mpvVideo?.SetMuted(true);
                }
                else
                {
                    double restore = Math.Clamp(_previousVolume > 0 ? _previousVolume : 1.0, 0, 1);
                    _isVolumeMuted = false;
                    _mpvVideo?.SetVolume((int)Math.Round(restore * 100));
                    _mpvVideo?.SetMuted(false);
                }
                SyncVolumeUi(_isVolumeMuted ? 0 : _previousVolume);
                ResetHideControlsTimer();
                return;
            }

            var player = PlayerElement.MediaPlayer;
            if (player == null) return;

            if (!_isVolumeMuted && player.Volume > 0)
            {
                _previousVolume = Math.Clamp(player.Volume, 0, 1);
                _isVolumeMuted = true;
                player.Volume = 0;
            }
            else
            {
                double restore = Math.Clamp(_previousVolume > 0 ? _previousVolume : 1.0, 0, 1);
                _isVolumeMuted = false;
                player.Volume = restore;
            }

            SyncVolumeUi(player.Volume);
            ResetHideControlsTimer();
        }

        // ====== 音量滑块结束 ======

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex > 0)
            {
                _currentIndex--;
                _currentItem = _playlist[_currentIndex];
                LoadVideo(_currentItem);
                RefreshQueueIfOpen();
            }
            ResetHideControlsTimer();
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex + 1 < _playlist.Count)
            {
                _currentIndex++;
                _currentItem = _playlist[_currentIndex];
                LoadVideo(_currentItem);
                RefreshQueueIfOpen();
            }
            ResetHideControlsTimer();
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleSystemFullScreen();
            ResetHideControlsTimer();
        }

        /// <summary>更新倍速按钮文字：字符较长（如 0.75x / 1.25x）时缩小字号，避免 X 被挤掉。</summary>
        private void UpdateSpeedDisplay(double speed)
        {
            string text = $"{speed:0.##}x";
            SpeedText.Text = text;
            // 5 字符文本（0.75x/1.25x）在 48px 按钮宽度下会溢出，缩小字号保证完整显示
            SpeedText.FontSize = text.Length >= 5 ? 11 : 13;
        }

        private void SpeedButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();
            foreach (double speed in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = $"{speed:0.##}x",
                    IsChecked = Math.Abs(_playbackRate - speed) < 0.01
                };
                item.Click += (_, _) =>
                {
                    _playbackRate = speed;
                    // 先更新按钮显示，保证 libmpv 分支 return 前图标已同步（此前仅普通模式更新，超分模式图标卡在 1x）
                    UpdateSpeedDisplay(speed);
                    if (_isMpvMode)
                    {
                        _mpvVideo?.SetSpeed(speed);
                        AppLogger.Info($"libmpv倍速变更: {speed:0.##}x");
                        return;
                    }
                    var player = PlayerElement.MediaPlayer;
                    if (player != null)
                    {
                        var session = player.PlaybackSession;
                        double before = session.PlaybackRate;
                        try
                        {
                            session.PlaybackRate = speed;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error(ex, $"设置倍速失败: {speed}x");
                        }
                        double after = session.PlaybackRate;
                        bool supported = false;
                        try { supported = session.IsSupportedPlaybackRateRange(speed, speed); } catch { }
                        AppLogger.Info(
                            $"倍速指令: 目标={speed}x, 播放器存在={player != null}, " +
                            $"设置前={before:0.##}x, 设置后={after:0.##}x, " +
                            $"媒体是否支持该倍速范围={supported}, " +
                            $"状态={session.PlaybackState}");
                    }
                    else
                    {
                        AppLogger.Info($"倍速指令: 目标={speed}x, 但 PlayerElement.MediaPlayer 为 null，指令未发送");
                    }
                };
                menu.Items.Add(item);
            }

            menu.ShowAt(SpeedButton);
            ResetHideControlsTimer();
        }

        /// <summary>画面比例菜单：适应 / 4:3 / 16:9 / 16:10 / 21:9 / 1:1。
        /// 使用 ToggleMenuFlyoutItem，选中项左侧显示主题色圆形标记。</summary>
        private void AspectRatioButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();
            // 第一项"适应"（原始比例），其余为常见行业规范比例
            string[] ratios = { "适应", "4:3", "16:9", "16:10", "21:9", "1:1" };
            foreach (string ratio in ratios)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = ratio,
                    IsChecked = (_aspectRatio ?? "适应") == ratio
                };
                string selected = ratio;
                item.Click += (_, _) =>
                {
                    _aspectRatio = selected == "适应" ? null : selected;
                    ApplyAspectRatio(_aspectRatio);
                    AppLogger.Info($"画面比例变更: {_aspectRatio ?? "适应"}");
                };
                menu.Items.Add(item);
            }

            menu.ShowAt(AspectRatioButton);
            ResetHideControlsTimer();
        }

        /// <summary>应用画面比例（双内核分发）：超分模式走 mpv video-aspect-override，普通模式走 ScaleTransform。</summary>
        private void ApplyAspectRatio(string? ratio)
        {
            if (_isMpvMode)
            {
                _mpvVideo?.SetAspectRatio(ratio);
            }
            else
            {
                ApplyNormalAspectRatio(ratio);
            }
        }

        /// <summary>普通模式（MediaPlayerElement）应用画面比例：
        /// 以视频原始宽高比为基准，对播放器元素做非等比缩放（不裁切画面，只拉伸），
        /// 使显示比例达到目标（"适应"恢复 ScaleX/Y = 1）。</summary>
        private void ApplyNormalAspectRatio(string? ratio)
        {
            var player = PlayerElement.MediaPlayer;
            if (player == null || player.PlaybackSession == null)
            {
                return;
            }

            double naturalW, naturalH;
            try
            {
                naturalW = player.PlaybackSession.NaturalVideoWidth;
                naturalH = player.PlaybackSession.NaturalVideoHeight;
            }
            catch
            {
                // 会话尚未就绪（如加载中），MediaOpened 后会重应用
                return;
            }
            if (naturalW <= 0 || naturalH <= 0)
            {
                return;
            }

            double scaleX = 1, scaleY = 1;
            if (ratio != null && TryParseAspectRatio(ratio, out double targetRatio))
            {
                double originalRatio = naturalW / naturalH;
                if (targetRatio > originalRatio)
                {
                    // 目标更宽 → 横向拉伸
                    scaleX = targetRatio / originalRatio;
                }
                else if (targetRatio < originalRatio)
                {
                    // 目标更窄 → 纵向拉伸
                    scaleY = originalRatio / targetRatio;
                }
            }

            // 缩放围绕元素中心（RenderTransformOrigin 为相对坐标 0.5,0.5，ScaleTransform 自身 Center 保持 0）
            PlayerElement.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            PlayerElement.RenderTransform = new ScaleTransform { ScaleX = scaleX, ScaleY = scaleY };
        }

        /// <summary>解析 "W:H" 字符串为数值比例，失败返回 false。</summary>
        private static bool TryParseAspectRatio(string ratio, out double value)
        {
            value = 0;
            var parts = ratio.Split(':');
            if (parts.Length != 2)
            {
                return false;
            }
            if (double.TryParse(parts[0], out double w) && double.TryParse(parts[1], out double h)
                && w > 0 && h > 0)
            {
                value = w / h;
                return true;
            }
            return false;
        }

        // ===================== 播放队列 Flyout =====================

        /// <summary>构建播放队列 Flyout（参考音乐播放器/迷你播放器设计，数据源为 _playlist）。</summary>
        private void BuildQueueFlyout()
        {
            if (_queueFlyout != null && _isQueueFlyoutOpen)
            {
                _queueFlyout.Hide();
            }

            // 从 XAML 资源获取项模板（避免 XamlReader.Load 的启动死锁与 Popup 事件处理器失效问题）
            _queueDefaultTemplate = (DataTemplate)Resources["QueueItemDefaultTemplate"];
            _queueNowPlayingTemplate = (DataTemplate)Resources["QueueItemNowPlayingTemplate"];

            // 标题栏：播放队列 + 清除全部
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

            // 整体布局：标题 + 列表
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

            // 根据主题设置 Flyout 材质与卡片交互画刷
            bool isDark = ActualTheme == ElementTheme.Dark;
            var flyoutBackgroundColor = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 28, 28, 28)
                : Microsoft.UI.ColorHelper.FromArgb(255, 249, 249, 249);
            var flyoutBorderColor = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 58, 58, 58)
                : Microsoft.UI.ColorHelper.FromArgb(255, 208, 208, 208);

            _queueNormalBgBrush = new SolidColorBrush(flyoutBackgroundColor);
            _queueHoverBgBrush = new SolidColorBrush(isDark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 58, 58, 58)
                : Microsoft.UI.ColorHelper.FromArgb(255, 232, 232, 232));
            _queuePressedBgBrush = new SolidColorBrush(isDark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 74, 74, 74)
                : Microsoft.UI.ColorHelper.FromArgb(255, 216, 216, 216));
            _queueNormalBorderBrush = new SolidColorBrush(flyoutBorderColor);
            _queueHoverBorderBrush = new SolidColorBrush(isDark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 90, 90, 90)
                : Microsoft.UI.ColorHelper.FromArgb(255, 184, 184, 184));
            _queuePressedBorderBrush = new SolidColorBrush(isDark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 106, 106, 106)
                : Microsoft.UI.ColorHelper.FromArgb(255, 168, 168, 168));

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
                FlyoutPresenterStyle = CreateQueueFlyoutPresenterStyle()
            };

            // ★ 用具名方法订阅，避免匿名 lambda 捕获 this 导致页面泄漏
            _queueFlyout.Opening += QueueFlyout_Opening;
            _queueFlyout.Closed += QueueFlyout_Closed;

            QueueButton.Flyout = _queueFlyout;
        }

        private static Style CreateQueueFlyoutPresenterStyle()
        {
            var style = new Style(typeof(FlyoutPresenter));
            style.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
            style.Setters.Add(new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
            return style;
        }

        private void QueueFlyout_Opening(object? sender, object e)
        {
            _isQueueFlyoutOpen = true;
            RefreshQueueItems();
            _queueList.ItemTemplateSelector = new VideoQueueTemplateSelector(this)
            {
                DefaultTemplate = _queueDefaultTemplate!,
                NowPlayingTemplate = _queueNowPlayingTemplate!
            };
        }

        private void QueueFlyout_Closed(object? sender, object e)
        {
            _isQueueFlyoutOpen = false;
        }

        private void RefreshQueueItems()
        {
            var items = GetDisplayQueueItems();
            _queueList.ItemsSource = items;
            _queueEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 播放队列显示排序（复用迷你播放器/音乐播放器的逻辑）：
        /// 第一个 = 当前正在播放的视频，第二个 = 下一个播放的视频，以此类推
        /// （从 _currentIndex 开始截取，播放到最后一个后自然显示从最后一项开始的新队列）。
        /// </summary>
        private IReadOnlyList<MediaItem> GetDisplayQueueItems()
        {
            var queue = _playlist;
            if (queue.Count <= 1)
            {
                return queue;
            }
            if (_currentIndex < 0 || _currentIndex >= queue.Count)
            {
                return queue;
            }

            var items = new List<MediaItem>();
            for (int i = _currentIndex; i < queue.Count; i++)
            {
                items.Add(queue[i]);
            }
            return items;
        }

        /// <summary>当前索引变化后刷新队列（仅当队列 Flyout 打开时，避免无谓重建）。</summary>
        private void RefreshQueueIfOpen()
        {
            if (_isQueueFlyoutOpen)
            {
                RefreshQueueItems();
            }
        }

        // 播放队列卡片交互：悬停/按下反馈
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
                border.Background = isInside ? _queueHoverBgBrush : _queueNormalBgBrush;
                border.BorderBrush = isInside ? _queueHoverBorderBrush : _queueNormalBorderBrush;
            }
        }

        public void QueueItem_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = _queueNormalBgBrush;
                border.BorderBrush = _queueNormalBorderBrush;
            }
        }

        private void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            // 清除队列：仅保留当前视频（"下一个"逻辑仍可继续）
            if (_currentItem != null)
            {
                _playlist.Clear();
                _playlist.Add(_currentItem);
                _currentIndex = 0;
            }
            else
            {
                _playlist.Clear();
                _currentIndex = -1;
            }
            AppLogger.Info("视频播放队列已清除");
            RefreshQueueItems();
        }

        private void QueueList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MediaItem item)
            {
                return;
            }
            if (_currentItem == item)
            {
                // 点击当前项：无需重新加载，仅保持高亮
                return;
            }
            int index = _playlist.IndexOf(item);
            if (index < 0)
            {
                return;
            }
            _queueFlyout?.Hide();
            _currentIndex = index;
            _currentItem = item;
            AppLogger.Info($"播放队列切换视频: {item.FileName}");
            LoadVideo(item);
        }

        /// <summary>视频播放队列模板选择器：当前播放项使用 NowPlayingTemplate（"正在播放"高亮）。</summary>
        private sealed partial class VideoQueueTemplateSelector : DataTemplateSelector
        {
            private readonly VideoPlayerPage _page;
            public DataTemplate? DefaultTemplate { get; set; }
            public DataTemplate? NowPlayingTemplate { get; set; }

            public VideoQueueTemplateSelector(VideoPlayerPage page) => _page = page;

            protected override DataTemplate? SelectTemplateCore(object item)
                => item is MediaItem media && _page._currentItem == media
                    ? NowPlayingTemplate
                    : DefaultTemplate;
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();

            var debugItem = new MenuFlyoutItem { Text = "显示调试信息" };
            debugItem.Click += (_, _) => ToggleDebugInfoPanel();
            menu.Items.Add(debugItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var openLocationItem = new MenuFlyoutItem { Text = "打开文件所在位置" };
            openLocationItem.Click += (s, args) => OpenFileLocation();
            menu.Items.Add(openLocationItem);

            var propertiesItem = new MenuFlyoutItem { Text = "属性" };
            propertiesItem.Click += (s, args) => _ = ShowPropertiesAsync();
            menu.Items.Add(propertiesItem);

            menu.ShowAt(MoreButton);
            ResetHideControlsTimer();
        }

        /// <summary>
        /// 开关"调试信息"悬浮窗：
        /// 超分模式（libmpv）下轮询 mpv 属性显示分辨率/码率/帧率/补帧状态；
        /// 普通模式（MediaPlayer）下显示 MediaPlayer 可获取的有限信息
        /// （分辨率实时、码率/编码/帧率经 MediaEncodingProfile 静态解析）——
        /// MediaPlayer 不暴露实时码率/帧率 API，普通模式无补帧，帧率恒等于原始帧率。
        /// </summary>
        private void ToggleDebugInfoPanel()
        {
            try
            {
                if (DebugInfoPanel.Visibility == Visibility.Visible)
                {
                    CloseDebugInfoPanel();
                    return;
                }

                DebugInfoPanel.Visibility = Visibility.Visible;
                UpdateDebugInfo();
                // 普通模式：异步解析文件元数据（码率/编码格式/帧率），解析完成后立即刷新一次显示
                if (!_isMpvMode)
                {
                    _ = ResolveNormalMediaInfoAsync();
                }

                // ★ 启动后台线程轮询（替代原 DispatcherTimer 的 UI 线程同步阻塞）
                CancelDebugInfoLoop();
                _debugInfoCts = new CancellationTokenSource();
                RunDebugInfoLoopAsync(_debugInfoCts.Token);
                AppLogger.Info(_isMpvMode ? "调试信息悬浮窗已开启（libmpv）" : "调试信息悬浮窗已开启（普通模式）");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "开启调试信息悬浮窗失败");
            }
        }

        /// <summary>关闭调试信息悬浮窗（叉号按钮或再次点击菜单项）。</summary>
        private void CloseDebugInfoPanel()
        {
            CancelDebugInfoLoop();
            DebugInfoPanel.Visibility = Visibility.Collapsed;
            AppLogger.Info("调试信息悬浮窗已关闭");
        }

        /// <summary>取消后台调试信息轮询循环（幂等，可重复调用）。</summary>
        private void CancelDebugInfoLoop()
        {
            try
            {
                _debugInfoCts?.Cancel();
                _debugInfoCts?.Dispose();
            }
            catch { /* 取消/释放可能抛异常（如已 Dispose），安全忽略 */ }
            finally { _debugInfoCts = null; }
        }

        /// <summary>叉号关闭调试信息悬浮窗。</summary>
        private void DebugInfoCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseDebugInfoPanel();
            ResetHideControlsTimer();
        }

        /// <summary>
        /// 轮询更新调试信息悬浮窗内容（按播放模式分路径取数）：
        /// 超分模式（libmpv）读取 mpv 属性（分辨率/码率/帧率/补帧状态实时）；
        /// 普通模式（MediaPlayer）使用实时 PlaybackSession 分辨率 + MediaEncodingProfile 静态元数据。
        /// </summary>
        private void UpdateDebugInfo()
        {
            try
            {
                if (_isMpvMode)
                {
                    UpdateMpvDebugInfo();
                }
                else
                {
                    UpdateNormalDebugInfo();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "更新调试信息失败");
            }
        }

        /// <summary>超分模式（libmpv）：读取 mpv 属性更新调试信息。</summary>
        private void UpdateMpvDebugInfo()
        {
            if (_mpvVideo == null || !_mpvVideo.IsMediaLoaded)
            {
                return;
            }

            var info = _mpvVideo.GetDebugInfo();
            ApplyMpvDebugInfo(info);
        }

        /// <summary>
        /// 将已读取的 mpv 调试信息应用到 UI 控件（仅 UI 线程操作）。
        /// 与 UpdateMpvDebugInfo 分离：后台线程读取 mpv 属性后，经 DispatcherQueue 回传此方法更新 UI。
        /// </summary>
        private void ApplyMpvDebugInfo(MpvVideoDebugInfo info)
        {
            // 分辨率：编码尺寸，若显示尺寸不同则追加（如旋转/缩放后）
            if (info.VideoWidth > 0 && info.VideoHeight > 0)
            {
                if (info.DisplayWidth > 0 && info.DisplayHeight > 0 &&
                    (info.DisplayWidth != info.VideoWidth || info.DisplayHeight != info.VideoHeight))
                {
                    DebugResText.Text = $"{info.VideoWidth} × {info.VideoHeight}（显示 {info.DisplayWidth} × {info.DisplayHeight}）";
                }
                else
                {
                    DebugResText.Text = $"{info.VideoWidth} × {info.VideoHeight}";
                }
            }
            else
            {
                DebugResText.Text = "--";
            }

            // 视频码率（实测 mpv 0.41 返回单位 bps → Mbps 需 ÷1_000_000；
            // 曾误按 kbps ÷1000 显示，导致 11210kbps 的视频显示成 1.1 万 Mbps）
            if (info.VideoBitrate > 0)
            {
                DebugBitrateText.Text = $"{info.VideoBitrate / 1_000_000:0.0} Mbps";
            }
            else
            {
                DebugBitrateText.Text = "--";
            }

            // 音频码率（同样 bps → Mbps）
            if (info.AudioBitrate > 0)
            {
                DebugAudioBitrateText.Text = $"{info.AudioBitrate / 1_000_000:0.00} Mbps";
            }
            else
            {
                DebugAudioBitrateText.Text = "--";
            }

            // 帧率：优先显示实时渲染帧率（vo-passes，卡顿掉帧时下降）；
            // 不可用时回退：开启补帧显示"原始 / 补帧后"，否则仅显示原始帧率
            double containerFps = info.ContainerFps;
            double vfFps = info.EstimatedVfFps;
            bool mcEnabled = App.SettingsHelper.VideoMotionCompensationEnabled;
            if (info.RealTimeFps > 0)
            {
                DebugFpsText.Text = FormatFps(info.RealTimeFps);
            }
            else if (containerFps > 0)
            {
                if (mcEnabled)
                {
                    string after = vfFps > 0 ? FormatFps(vfFps) : "--";
                    DebugFpsText.Text = $"{FormatFps(containerFps)} / {after}";
                }
                else
                {
                    DebugFpsText.Text = FormatFps(containerFps);
                }
            }
            else
            {
                DebugFpsText.Text = "--";
            }

            // 补帧状态行：仅超分模式（libmpv）展示
            DebugMcStateRow.Visibility = Visibility.Visible;

            // 补帧状态：以 mpv vf 滤镜链的真实状态为准（设置开关只代表意图）。
            // ★ 修复（2026-08-12）：此前只看设置开关，导致"设置已关闭但滤镜残留、
            //   实际仍在补帧"时仍显示"未开启"（真实与意图不一致被掩盖）。现优先
            //   读实际滤镜链（info.MotionCompensationActive），能直观暴露残留异常。
            //   帧率对比（estimated-vf-fps vs container-fps，差 ≥ 10%）判定补帧是否真正提升帧率。
            bool mcActive = info.MotionCompensationActive;
            if (!mcEnabled && !mcActive)
            {
                // 设置关闭 + 链中无滤镜：正常关闭
                DebugMcStateText.Text = "未开启";
                DebugMcStateText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 178, 178, 178));
            }
            else if (!mcEnabled && mcActive)
            {
                // 设置已关闭但滤镜仍在链中：异常（移除失败/残留），应立即排查
                DebugMcStateText.Text = "异常（滤镜残留）";
                DebugMcStateText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 235, 110, 110));
            }
            else if (mcActive && containerFps > 0 && vfFps > 0 && vfFps > containerFps * 1.1)
            {
                DebugMcStateText.Text = $"已生效（{App.SettingsHelper.VideoMotionCompensationMode}）";
                DebugMcStateText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 108));
            }
            else if (mcActive)
            {
                // 滤镜已加载但帧率未提升：VapourSynth 懒初始化中 / 滤镜初始化失败
                DebugMcStateText.Text = "已加载（帧率未提升）";
                DebugMcStateText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 87));
            }
            else
            {
                // 设置开启但滤镜不在链中：加载失败（脚本缺失/平台不支持等）
                DebugMcStateText.Text = "未生效（滤镜未加载）";
                DebugMcStateText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 87));
            }

            // 超分状态：对比 glsl-shaders 是否已加载 Anime4K shader（WHEN 条件满足才执行，
            // 渲染尺寸 ≤ 原分辨率 1.2 倍时 shader 不执行——与补帧"是否生效"同理）
            DebugSrStateRow.Visibility = Visibility.Visible;
            bool srEnabled = info.SuperResolutionEnabled;
            string glsl = info.GlslShaders ?? string.Empty;
            bool srLoaded = glsl.IndexOf("Anime4K", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            glsl.IndexOf("Upscale_CNN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            glsl.IndexOf("Restore_CNN", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!srEnabled)
            {
                DebugSrStateText.Text = "未开启";
                DebugSrStateText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 178, 178, 178));
            }
            else if (srLoaded)
            {
                string model = string.IsNullOrEmpty(info.SuperResolutionModel)
                    ? "Anime4K" : info.SuperResolutionModel;
                string quality = info.SuperResolutionQuality switch
                {
                    "Low" => "低档",
                    "High" => "高档",
                    "Ultra" => "超高档",
                    _ => "中档",
                };
                DebugSrStateText.Text = $"已生效（{model}·{quality}）";
                DebugSrStateText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 108));
            }
            else
            {
                // 超分开启但 shader 未执行：窗口渲染尺寸不足（< 原分辨率 1.2 倍）时 Anime4K 不启用
                DebugSrStateText.Text = "未生效（渲染尺寸不足）";
                DebugSrStateText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 87));
            }

            // 编码格式 / 硬解状态：
            //   hwdec-current 返回实际解码器名（d3d11va-copy/dxva2-copy = 硬解；no = 软解）。
            //   ★ 曾用 hwdec-active（yes/no）判断，实测 libmpv 场景返回 NULL 导致硬解被误判为软解。
            if (!string.IsNullOrEmpty(info.VideoFormat))
            {
                string hwdec;
                if (info.HwdecCurrent == "no" || string.IsNullOrEmpty(info.HwdecCurrent))
                {
                    hwdec = "软解";
                }
                else
                {
                    // 显示实际解码器（如 d3d11va-copy），直观确认硬解链路
                    hwdec = $"硬解({info.HwdecCurrent})";
                }
                DebugCodecText.Text = $"{info.VideoFormat.ToUpperInvariant()} · {hwdec}";
            }
            else
            {
                DebugCodecText.Text = "--";
            }
        }

        /// <summary>
        /// 普通模式（MediaPlayer）调试信息：
        /// 分辨率取 PlaybackSession 实时值；码率/编码格式/帧率取 MediaEncodingProfile
        /// 静态解析结果（普通模式无补帧，帧率恒等于原始帧率）；解码方式取设置。
        /// MediaPlayer 不暴露实时码率/帧率 API，此为当前可获得的最全信息。
        /// </summary>
        private void UpdateNormalDebugInfo()
        {
            // 分辨率：播放会话实时值，回退缓存
            uint videoW = 0, videoH = 0;
            try
            {
                var session = PlayerElement.MediaPlayer?.PlaybackSession;
                if (session != null)
                {
                    videoW = session.NaturalVideoWidth;
                    videoH = session.NaturalVideoHeight;
                }
            }
            catch { }
            if (videoW > 0 && videoH > 0)
            {
                DebugResText.Text = $"{videoW} × {videoH}";
            }
            else
            {
                DebugResText.Text = _currentItem?.VideoResolutionText ?? "--";
            }

            // 视频码率（MediaEncodingProfile.Video.Bitrate 单位 bps → 缓存为 kbps，显示 ÷1000 = Mbps）
            if (_normalBitrateKbps > 0)
            {
                DebugBitrateText.Text = $"{_normalBitrateKbps / 1000:0.0} Mbps";
            }
            else
            {
                DebugBitrateText.Text = "--";
            }

            // 音频码率（MediaEncodingProfile.Audio.Bitrate，单位 bps）
            if (_normalAudioBitrateKbps > 0)
            {
                DebugAudioBitrateText.Text = $"{_normalAudioBitrateKbps / 1000:0.00} Mbps";
            }
            else
            {
                DebugAudioBitrateText.Text = "--";
            }

            // 帧率：普通模式无补帧，仅显示原始帧率（如 30）
            double? fps = _normalFrameRate ?? _currentItem?.FrameRate;
            if (fps is double f && f > 0)
            {
                DebugFpsText.Text = FormatFps(f);
            }
            else
            {
                DebugFpsText.Text = "--";
            }

            // 补帧状态行 / 超分状态行：普通模式（MediaPlayer）均不支持，隐藏
            DebugMcStateRow.Visibility = Visibility.Collapsed;
            DebugSrStateRow.Visibility = Visibility.Collapsed;

            // 编码格式 / 实际解码方式（运行时检测）：
            //   FFmpeg 后端 → 读取 FFmpegInteropX CurrentVideoStream.DecoderEngine 实际引擎
            //     （Automatic 尝试 D3D11 硬解，显卡/驱动不支持时自动回退软解，必须运行时检测而非推断）；
            //   System 后端 → Media Foundation 硬件加速优先（预期硬解）。
            string codec = string.IsNullOrEmpty(_normalVideoCodec) ? "--" : _normalVideoCodec.ToUpperInvariant();
            DebugCodecText.Text = $"{codec} · {GetNormalHwdecState()}";
        }

        /// <summary>
        /// 普通模式实际解码方式（硬解/软解）运行时检测。
        /// FFmpeg 后端读取 FFmpegInteropX 的解码引擎（Automatic 模式可能硬解成功也可能回退软解）；
        /// System 后端由 Media Foundation 自动管理（硬件加速优先），显示预期状态"硬解"。
        /// </summary>
        private string GetNormalHwdecState()
        {
            try
            {
                if (App.SettingsHelper.VideoDecoderBackend == "System")
                {
                    // Media Foundation 硬件加速优先（硬件不支持时系统自动回退软解，此为预期状态）
                    return "硬解";
                }

                // FFmpeg 后端：读取 FFmpegInteropX 实际解码引擎（播放开始后流信息有效）
                var stream = _ffmpegMediaSource?.CurrentVideoStream;
                if (stream != null)
                {
                    switch (stream.DecoderEngine)
                    {
                        case DecoderEngine.FFmpegD3D11HardwareDecoder:
                            return "硬解";
                        case DecoderEngine.SystemDecoder:
                            return "系统解码";
                        default: // DecoderEngine.FFmpegSoftwareDecoder
                            return "软解";
                    }
                }

                // 流尚未打开（未播放/加载中）：FFmpegInteropX 默认回退软解
                return "软解";
            }
            catch
            {
                return "软解";
            }
        }

        /// <summary>
        /// ★ 后台线程轮询 mpv 调试信息（替代原 DispatcherTimer）。
        /// 原 DispatcherTimer 在 UI 线程同步读取 12 个 mpv 属性（含 vo-passes 完整 Node 树），
        /// 每次 P/Invoke 往返阻塞 UI 线程 200-330ms → 每秒卡死 25-33% → 抽搐/定格。
        /// 现改为：
        ///   1. 后台线程通过 Task.Run 调用 _mpvVideo.GetDebugInfo() 批量读取 mpv 属性
        ///   2. 读完后通过 DispatcherQueue.TryEnqueue 回传 UI 线程更新 TextBlock
        /// 这样 mpv 同步 P/Invoke 阻塞的是后台线程，UI 线程完全不受影响。
        /// </summary>
        private async void RunDebugInfoLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 每秒轮询一次（与原来 DispatcherTimer Interval 一致）
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (ct.IsCancellationRequested) { break; }

                try
                {
                    // ── 在后台线程读取 mpv 属性（避免阻塞 UI 线程） ──
                    MpvVideoDebugInfo? info = null;
                    var mpv = _mpvVideo; // 捕获引用，防止 Dispose 期间变 null
                    if (_isMpvMode && mpv != null && mpv.IsMediaLoaded)
                    {
                        info = await Task.Run(() =>
                        {
                            try { return mpv.GetDebugInfo(); }
                            catch { return new MpvVideoDebugInfo(); }
                        }, ct).ConfigureAwait(false);
                    }

                    // ── 回传 UI 线程更新显示 ──
                    if (!ct.IsCancellationRequested)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!ct.IsCancellationRequested &&
                                DebugInfoPanel.Visibility == Visibility.Visible)
                            {
                                if (_isMpvMode && info != null && _mpvVideo != null)
                                {
                                    ApplyMpvDebugInfo(info);
                                }
                                else if (!_isMpvMode)
                                {
                                    UpdateNormalDebugInfo();
                                }
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "调试信息后台轮询异常");
                }
            }
        }

        /// <summary>
        /// 普通模式调试信息文件元数据解析（码率/编码格式/帧率，MediaEncodingProfile）。
        /// 仅解析一次并按文件路径缓存（换文件自动重新解析）；失败时字段取默认值（--）。
        /// </summary>
        private async Task ResolveNormalMediaInfoAsync()
        {
            if (_currentItem == null || _isMpvMode)
            {
                return;
            }

            // 换文件后重置缓存并重新解析
            if (!string.Equals(_normalInfoFilePath, _currentItem.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _normalInfoFilePath = _currentItem.FilePath;
                _normalBitrateKbps = -1;
                _normalAudioBitrateKbps = -1;
                _normalVideoCodec = string.Empty;
                _normalFrameRate = null;
            }

            if (_normalBitrateKbps >= 0 || _normalInfoResolving)
            {
                return;
            }

            _normalInfoResolving = true;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(_currentItem.FilePath);
                var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
                var v = profile?.Video;
                _normalBitrateKbps = (v != null && v.Bitrate > 0) ? v.Bitrate / 1000.0 : 0;
                _normalVideoCodec = v?.Subtype ?? string.Empty;
                if (v != null && v.FrameRate.Denominator > 0)
                {
                    _normalFrameRate = (double)v.FrameRate.Numerator / v.FrameRate.Denominator;
                }
                // 音频码率（bps → kbps 缓存）
                var a = profile?.Audio;
                _normalAudioBitrateKbps = (a != null && a.Bitrate > 0) ? a.Bitrate / 1000.0 : 0;
            }
            catch
            {
                // 解析失败（如格式不支持）→ 标记为已解析但无信息，避免每次轮询重复解析
                _normalBitrateKbps = 0;
                _normalAudioBitrateKbps = 0;
            }
            finally
            {
                _normalInfoResolving = false;
                // 解析完成后若面板仍可见，立即刷新显示（await 后已回到 UI 线程）
                if (DebugInfoPanel.Visibility == Visibility.Visible && !_isMpvMode)
                {
                    try { UpdateNormalDebugInfo(); } catch { }
                }
            }
        }

        /// <summary>格式化帧率显示（整数优先，否则保留两位小数）。</summary>
        private static string FormatFps(double fps)
        {
            if (fps <= 0) return "--";
            if (Math.Abs(fps - Math.Round(fps)) < 0.01)
            {
                return Math.Round(fps).ToString("0");
            }
            return fps.ToString("0.00");
        }

        // ====== 播放器设置弹窗（复用音乐播放器的弹窗外框与"音频输出设备"卡片）======

        private async void PlayerSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _settingsDialogDark = ActualTheme == ElementTheme.Dark;
            // 弹窗外框（标题/关闭按钮/固定底色）由 PlayerSettingsDialogHelper 统一构建，
            // 与音乐播放器完全一致；弹窗内容仅保留"音频输出设备"一项。
            try
            {
                await PlayerSettingsDialogHelper.ShowPlayerSettingsDialogAsync(
                    XamlRoot, _settingsDialogDark, BuildVideoSettingsContent);
            }
            catch (Exception ex)
            {
                // async void 中的异常逃逸会直接终止进程，这里兜底记录日志，避免弹窗异常导致崩溃
                AppLogger.Error(ex, "播放器设置弹窗打开/显示失败");
            }
        }

        /// <summary>
        /// 构建视频播放器设置弹窗内容：
        /// 上方为与音乐播放器一致的胶囊分段 Tab 栏（仅"常规"一个分段），
        /// 内容区仅包含"音频输出设备"设置卡片（其他音乐播放器设置项不复用）。
        /// </summary>
        private UIElement BuildVideoSettingsContent(ContentDialog owner)
        {
            const double DialogContentWidth = 520;

            // ★ 保存弹窗宿主引用：超分辨率提示中的「视频设置」超链接跳转时需显式关闭弹窗
            _playerSettingsDialog = owner;

            var header = PlayerSettingsDialogHelper.BuildDialogHeader(
                _settingsDialogDark, () => owner.Hide());

            // 与音乐播放器弹窗一致的胶囊分段 Tab 栏（仅"常规"一个分段）
            var selectorBarHost = PlayerSettingsDialogHelper.BuildSegmentBar(_settingsDialogDark);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(1, 1, 2, 1),
                // 与音乐播放器弹窗一致的内容区高度
                Height = 300
            };
            var panel = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            // 音频输出设备卡片由 PlayerSettingsDialogHelper 统一构建（与音乐播放器共用），
            // 此处注入视频播放器的设备保存与应用逻辑。
            panel.Children.Add(PlayerSettingsDialogHelper.BuildAudioOutputExpander(
                App.SettingsHelper.VideoOutputDeviceId ?? string.Empty,
                deviceId =>
                {
                    App.SettingsHelper.VideoOutputDeviceId = deviceId;
                    App.SettingsHelper.Save();
                    AppLogger.Info($"视频播放器输出设备切换：" +
                        (string.IsNullOrEmpty(deviceId) ? "跟随系统默认设备" : deviceId));
                },
                deviceId => ApplyVideoAudioDeviceAsync(deviceId)));
            // "记忆当前视频播放进度"设置卡片：仅记录当前正在播放视频的进度；
            // 当视频设置里"记忆全部视频播放进度"开启时本开关禁用（已全局记忆，无需单独设置）
            var currentVideoToggle = new ToggleSwitch
            {
                IsOn = App.SettingsHelper.RememberCurrentVideoPosition,
                IsEnabled = !App.SettingsHelper.RememberVideoPosition,
                MinWidth = 0,
                OnContent = "",
                OffContent = ""
            };
            currentVideoToggle.Toggled += (_, _) =>
            {
                App.SettingsHelper.RememberCurrentVideoPosition = currentVideoToggle.IsOn;
                App.SettingsHelper.Save();
                AppLogger.Info($"记忆当前视频播放进度变更: {App.SettingsHelper.RememberCurrentVideoPosition}");
            };
            var currentVideoCard = new SettingsCard
            {
                Header = "记忆当前视频播放进度",
                Description = App.SettingsHelper.RememberVideoPosition
                    ? "已开启记忆全部视频播放进度，本开关已禁用"
                    : "记录当前正在播放视频的进度，下次打开该视频时自动续播",
                HeaderIcon = new FontIcon
                {
                    FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"),
                    FontSize = 16,
                    Glyph = "\uE823" // 历史记录（与视频设置页"记忆播放位置"图标一致）
                },
                Content = currentVideoToggle
            };
            panel.Children.Add(currentVideoCard);
            // "超分辨率"设置卡片放在列表后方：始终显示（普通模式为灰色禁用态 + 蓝色提示按钮）
            panel.Children.Add(BuildSuperResolutionCard());
            // "运动补偿"（补帧）设置卡片：始终显示（普通模式/非 x64 平台为灰色禁用态 + 蓝色提示按钮）
            panel.Children.Add(BuildMotionCompensationCard());
            scroll.Content = panel;

            var root = new StackPanel
            {
                Width = DialogContentWidth
            };
            root.Children.Add(header);
            root.Children.Add(selectorBarHost);
            root.Children.Add(scroll);
            return root;
        }

        /// <summary>
        /// 构建"超分辨率"设置卡片：
        /// 超分模式（libmpv）下可正常展开设置（开关控制超分，展开子设置：超分质量 / 超分模型）；
        /// 普通模式下卡片整体灰色禁用，右上角叠加蓝色提示按钮（样式同回收站标题右侧提示按钮），
        /// 点击提示"当前处于普通模式，不支持超分，请在视频设置里开启超分"。
        /// </summary>
        private UIElement BuildSuperResolutionCard()
        {
            // 超分质量下拉框：四档（低/中/高/超高），对应 Anime4K 的 VL/S/M/UL 模型
            // 低档 = 模型最低画质最快速度；超高档 = 最高画质（UL 模型），充分发挥 GPU 极限性能
            string savedQuality = App.SettingsHelper.VideoSuperResolutionQuality switch
            {
                "Low" => "Low",
                "High" => "High",
                "Ultra" => "Ultra",
                _ => "Medium"
            };
            var qualityCombo = new ComboBox
            {
                MinWidth = 150,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            qualityCombo.Items.Add(new ComboBoxItem { Content = "低档（最快）", Tag = "Low" });
            qualityCombo.Items.Add(new ComboBoxItem { Content = "中档（均衡）", Tag = "Medium" });
            qualityCombo.Items.Add(new ComboBoxItem { Content = "高档（画质）", Tag = "High" });
            qualityCombo.Items.Add(new ComboBoxItem { Content = "超高档（极限）", Tag = "Ultra" });
            qualityCombo.SelectedIndex = savedQuality switch
            {
                "Low" => 0,
                "High" => 2,
                "Ultra" => 3,
                _ => 1
            };

            // 超分模型：目前仅支持 anime4k，后续可扩展其他模型
            var modelCombo = new ComboBox
            {
                MinWidth = 150,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            modelCombo.Items.Add(new ComboBoxItem { Content = "Anime4K", Tag = "anime4k" });
            modelCombo.SelectedIndex = 0;

            var qualityCard = new SettingsCard
            {
                Header = "超分质量",
                Description = "低档最快；超高档画质最佳（Anime4K VL/S/M/UL 模型）",
                Content = qualityCombo
            };
            var modelCard = new SettingsCard
            {
                Header = "超分模型",
                Description = "目前仅支持 Anime4K，后续可扩展其他模型",
                Content = modelCombo
            };

            var expander = new SettingsExpander
            {
                Header = "超分辨率",
                Description = "利用模型进行视频超分",
                HeaderIcon = new FontIcon
                {
                    FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"),
                    FontSize = 16,
                    Glyph = "\uE740" // 全屏放大（双箭头）
                },
                IsExpanded = App.SettingsHelper.VideoSuperResolutionEnabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            expander.Resources["SettingsCardWrapThreshold"] = 200.0;
            expander.Resources["SettingsCardWrapNoIconThreshold"] = 160.0;
            expander.Resources["SettingsExpanderWrapThreshold"] = 200.0;
            expander.Resources["SettingsExpanderWrapNoIconThreshold"] = 160.0;
            expander.Items.Add(qualityCard);
            expander.Items.Add(modelCard);

            // 普通模式：不支持超分，卡片整体灰色禁用，并叠加蓝色提示按钮
            if (!_isMpvMode)
            {
                expander.IsEnabled = false;
                expander.IsExpanded = false;

                // 蓝色提示按钮：样式同回收站页面左上角标题右侧的提示按钮（圆形蓝色底 + 白色斜体 i）
                var infoButton = new InfoButton
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    // ★ 调整：上边距 24 → 25，按钮下移 1px
                    Margin = new Thickness(0, 25, 44, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Content = new TextBlock
                    {
                        Text = "i",
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        FontFamily = new FontFamily("Segoe UI"),
                        FontStyle = Windows.UI.Text.FontStyle.Italic,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };
                // 复用 App.xaml 中回收站提示按钮的全局样式（蓝色圆形 + 手型光标）
                if (Application.Current.Resources.TryGetValue("StaticInfoButtonStyle", out var styleObj) &&
                    styleObj is Style infoStyle)
                {
                    infoButton.Style = infoStyle;
                }

                // 点击提示：当前处于普通模式，不支持超分
                var teachingTip = new TeachingTip
                {
                    Target = infoButton,
                    PreferredPlacement = TeachingTipPlacementMode.Bottom
                };
                // ★ 保存引用：超链接跳转时需关闭提示
                _superResolutionTeachingTip = teachingTip;
                // ★ 提示内容直接复用回收站页面标题右侧提示按钮的标准写法：
                //   标题 FontSize=14 SemiBold + 底部 6px 间距，描述 FontSize=12 换行
                //   "视频设置"为可点击超链接，点击后关闭播放器并直达视频设置页
                var tipDesc = new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                };
                tipDesc.Inlines.Add(new Run { Text = "当前播放模式为 MediaPlayer（普通模式），不支持超分辨率。请前往「" });
                var videoSettingsLink = new Hyperlink
                {
                    UnderlineStyle = UnderlineStyle.None
                };
                videoSettingsLink.Inlines.Add(new Run { Text = "视频设置" });
                videoSettingsLink.Click += (_, _) => OpenVideoSettingsHyperlink();
                tipDesc.Inlines.Add(videoSettingsLink);
                tipDesc.Inlines.Add(new Run { Text = "」页面，将「视频播放模式」切换为「libmpv（实验性）」，返回播放器后即可使用超分辨率。" });

                teachingTip.Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "超分辨率处于禁用状态",
                            FontSize = 14,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Margin = new Thickness(0, 0, 0, 6)
                        },
                        tipDesc
                    }
                };
                infoButton.Click += (_, _) => teachingTip.IsOpen = true;

                // 卡片与提示按钮叠放：按钮悬浮在卡片右上角（标题右侧），TeachingTip 挂入同一容器
                var host = new Grid();
                host.Children.Add(expander);
                host.Children.Add(infoButton);
                host.Children.Add(teachingTip);
                return host;
            }

            // 超分模式：右侧开关控制超分开关，展开/收起子设置
            var toggle = new ToggleSwitch
            {
                IsOn = App.SettingsHelper.VideoSuperResolutionEnabled,
                OnContent = "开",
                OffContent = "关",
                MinWidth = 110,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            expander.Content = toggle;

            // 开关：保存设置 + 立即应用超分 + 同步展开状态
            // ★ 防御：Toggled 由 XAML 原生栈触发，处理器内异常逃逸会导致进程崩溃（e0434e49），
            //   因此整体 try/catch 兜底；展开/收起子设置放到最后执行，避免与 mpv 命令交错。
            toggle.Toggled += (_, _) =>
            {
                try
                {
                    App.SettingsHelper.VideoSuperResolutionEnabled = toggle.IsOn;
                    App.SettingsHelper.Save();
                    _ = _mpvVideo?.ApplySuperResolutionAsync(toggle.IsOn, App.SettingsHelper.VideoSuperResolutionQuality);
                    AppLogger.Info($"超分辨率切换: {toggle.IsOn}（{App.SettingsHelper.VideoSuperResolutionQuality} 档）");
                    expander.IsExpanded = toggle.IsOn;
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"超分辨率开关切换处理异常: {toggle.IsOn}");
                }
            };

            // 质量档切换：保存 + 若超分开启则重新应用 shader 链
            qualityCombo.SelectionChanged += (_, _) =>
            {
                try
                {
                    if (qualityCombo.SelectedItem is ComboBoxItem item && item.Tag is string quality)
                    {
                        App.SettingsHelper.VideoSuperResolutionQuality = quality;
                        App.SettingsHelper.Save();
                        if (App.SettingsHelper.VideoSuperResolutionEnabled)
                        {
                            _ = _mpvVideo?.ApplySuperResolutionAsync(true, quality);
                        }
                        AppLogger.Info($"超分质量档切换: {quality}");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "超分质量档切换处理异常");
                }
            };

            // 模型切换：保存（当前仅 anime4k）
            modelCombo.SelectionChanged += (_, _) =>
            {
                try
                {
                    if (modelCombo.SelectedItem is ComboBoxItem item && item.Tag is string model)
                    {
                        App.SettingsHelper.VideoSuperResolutionModel = model;
                        App.SettingsHelper.Save();
                        AppLogger.Info($"超分模型切换: {model}");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "超分模型切换处理异常");
                }
            };

            return expander;
        }

        /// <summary>
        /// 构建"运动补偿"（补帧）设置卡片：
        /// 超分模式（libmpv）且 x64 平台下可正常展开设置（开关控制补帧，展开子设置：补帧模式）；
        /// 普通模式（MediaPlayer 内核不支持 vf 滤镜）或非 x64 平台（VapourSynth 运行时为 x64 二进制）
        /// 时卡片整体灰色禁用，右上角叠加蓝色提示按钮，点击提示原因并可跳转「视频设置」。
        /// </summary>
        private UIElement BuildMotionCompensationCard()
        {
            // 补帧模式下拉框：四档（MVTools 倍帧/60fps、SVPFlow 倍帧/60fps）
            string savedMode = App.SettingsHelper.VideoMotionCompensationMode switch
            {
                "MVT_STD" => "MVT_STD",
                "SVP_LQ" => "SVP_LQ",
                "SVP_PRO" => "SVP_PRO",
                _ => "MVT_LQ"
            };
            var modeCombo = new ComboBox
            {
                MinWidth = 170,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            modeCombo.Items.Add(new ComboBoxItem { Content = "MVTools 补帧-LQ（倍帧）", Tag = "MVT_LQ" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "MVTools 补帧-STD（60fps）", Tag = "MVT_STD" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "SVPFlow 补帧-LQ（倍帧）", Tag = "SVP_LQ" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "SVPFlow 补帧-PRO（60fps）", Tag = "SVP_PRO" });
            modeCombo.SelectedIndex = savedMode switch
            {
                "MVT_STD" => 1,
                "SVP_LQ" => 2,
                "SVP_PRO" => 3,
                _ => 0
            };

            var modeCard = new SettingsCard
            {
                Header = "补帧模式",
                Description = "MVTools/SVPFlow 光流补帧，帧率提升可显著减少卡顿",
                Content = modeCombo
            };

            var expander = new SettingsExpander
            {
                Header = "运动补偿",
                Description = "对视频进行补帧，提升播放流畅度（需 libmpv 内核）",
                HeaderIcon = new FontIcon
                {
                    FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"),
                    FontSize = 16,
                    Glyph = "\uE81D" // 同步/刷新（帧率插值语义）
                },
                IsExpanded = App.SettingsHelper.VideoMotionCompensationEnabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            expander.Resources["SettingsCardWrapThreshold"] = 200.0;
            expander.Resources["SettingsCardWrapNoIconThreshold"] = 160.0;
            expander.Resources["SettingsExpanderWrapThreshold"] = 200.0;
            expander.Resources["SettingsExpanderWrapNoIconThreshold"] = 160.0;
            expander.Items.Add(modeCard);

            // 是否可用：需超分模式（libmpv 内核）且 x64 平台（VapourSynth 便携运行时仅 x64 二进制）
            bool supported = _isMpvMode && Mpv.MpvVideoPlayer.IsMotionCompensationSupported;
            if (!supported)
            {
                expander.IsEnabled = false;
                expander.IsExpanded = false;

                // 蓝色提示按钮：样式同回收站页面左上角标题右侧的提示按钮（圆形蓝色底 + 白色斜体 i）
                var infoButton = new InfoButton
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(0, 25, 44, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Content = new TextBlock
                    {
                        Text = "i",
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        FontFamily = new FontFamily("Segoe UI"),
                        FontStyle = Windows.UI.Text.FontStyle.Italic,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };
                if (Application.Current.Resources.TryGetValue("StaticInfoButtonStyle", out var styleObj) &&
                    styleObj is Style infoStyle)
                {
                    infoButton.Style = infoStyle;
                }

                var teachingTip = new TeachingTip
                {
                    Target = infoButton,
                    PreferredPlacement = TeachingTipPlacementMode.Bottom
                };
                // ★ 保存引用：超链接跳转时需关闭提示
                _motionCompensationTeachingTip = teachingTip;

                // 提示内容：区分"普通模式"与"非 x64 平台"两种原因
                var tipDesc = new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                };
                string tipTitle;
                if (_isMpvMode)
                {
                    // 超分模式但非 x64（ARM64）：VapourSynth 运行时与 MVTools/SVPFlow 插件均为 x64 二进制
                    tipTitle = "运动补偿处于禁用状态";
                    tipDesc.Text = "运动补偿依赖内置的 VapourSynth 运行时（MVTools / SVPFlow），当前仅支持 x64 平台。本机为 ARM64 架构，暂不支持运动补偿，超分辨率等其余功能不受影响。";
                }
                else
                {
                    tipTitle = "运动补偿处于禁用状态";
                    tipDesc.Inlines.Add(new Run { Text = "当前播放模式为 MediaPlayer（普通模式），不支持运动补偿。请前往「" });
                    var videoSettingsLink = new Hyperlink
                    {
                        UnderlineStyle = UnderlineStyle.None
                    };
                    videoSettingsLink.Inlines.Add(new Run { Text = "视频设置" });
                    videoSettingsLink.Click += (_, _) => OpenVideoSettingsHyperlink();
                    tipDesc.Inlines.Add(videoSettingsLink);
                    tipDesc.Inlines.Add(new Run { Text = "」页面，将「视频播放模式」切换为「libmpv（实验性）」，返回播放器后即可使用运动补偿。" });
                }

                teachingTip.Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = tipTitle,
                            FontSize = 14,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Margin = new Thickness(0, 0, 0, 6)
                        },
                        tipDesc
                    }
                };
                infoButton.Click += (_, _) => teachingTip.IsOpen = true;

                // 卡片与提示按钮叠放：按钮悬浮在卡片右上角（标题右侧），TeachingTip 挂入同一容器
                var host = new Grid();
                host.Children.Add(expander);
                host.Children.Add(infoButton);
                host.Children.Add(teachingTip);
                return host;
            }

            // 超分模式 + x64：右侧开关控制运动补偿，展开/收起子设置
            var toggle = new ToggleSwitch
            {
                IsOn = App.SettingsHelper.VideoMotionCompensationEnabled,
                OnContent = "开",
                OffContent = "关",
                MinWidth = 110,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            expander.Content = toggle;

            // 开关：保存设置 + 立即应用补帧滤镜 + 同步展开状态
            // ★ 防御：Toggled 由 XAML 原生栈触发，处理器内异常逃逸会导致进程崩溃（e0434e49），
            //   因此整体 try/catch 兜底；展开/收起子设置放到最后执行，避免与 mpv 命令交错。
            toggle.Toggled += (_, _) =>
            {
                try
                {
                    App.SettingsHelper.VideoMotionCompensationEnabled = toggle.IsOn;
                    App.SettingsHelper.Save();
                    _ = _mpvVideo?.ApplyMotionCompensationAsync(toggle.IsOn, App.SettingsHelper.VideoMotionCompensationMode);
                    AppLogger.Info($"运动补偿切换: {toggle.IsOn}（{App.SettingsHelper.VideoMotionCompensationMode} 档）");
                    expander.IsExpanded = toggle.IsOn;
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"运动补偿开关切换处理异常: {toggle.IsOn}");
                }
            };

            // 补帧模式切换：保存 + 若运动补偿开启则重新应用滤镜
            modeCombo.SelectionChanged += (_, _) =>
            {
                try
                {
                    if (modeCombo.SelectedItem is ComboBoxItem item && item.Tag is string mode)
                    {
                        App.SettingsHelper.VideoMotionCompensationMode = mode;
                        App.SettingsHelper.Save();
                        if (App.SettingsHelper.VideoMotionCompensationEnabled)
                        {
                            _ = _mpvVideo?.ApplyMotionCompensationAsync(true, mode);
                        }
                        AppLogger.Info($"运动补偿模式切换: {mode}");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "运动补偿模式切换处理异常");
                }
            };

            return expander;
        }

        /// <summary>
        /// 超分辨率禁用提示中的「视频设置」超链接点击处理：
        /// 先关闭提示与设置弹窗，再关闭播放器覆盖层（触发页面卸载/断点转移），
        /// 最后跳转到设置页直达「视频」设置。
        /// </summary>
        private void OpenVideoSettingsHyperlink()
        {
            try
            {
                // ★ 关闭超分辨率/运动补偿提示与播放器设置弹窗（否则跳转后弹窗悬浮残留）
                if (_superResolutionTeachingTip != null)
                {
                    _superResolutionTeachingTip.IsOpen = false;
                    _superResolutionTeachingTip = null;
                }
                if (_motionCompensationTeachingTip != null)
                {
                    _motionCompensationTeachingTip.IsOpen = false;
                    _motionCompensationTeachingTip = null;
                }
                if (_playerSettingsDialog != null)
                {
                    try { _playerSettingsDialog.Hide(); } catch { }
                    _playerSettingsDialog = null;
                }

                AppLogger.Info("超分辨率提示链接被点击，跳转到视频设置");
                if (App.MainWindow is MainWindow mw)
                {
                    mw.HidePlayerOverlay();
                    mw.NavigateToSettings(typeof(VideoSettingsPage));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "跳转视频设置失败");
            }
        }

        /// <summary>
        /// 将视频播放器单独输出到指定的音频渲染设备。
        /// 传入空字符串或 null 时跟随系统默认输出设备（置空 AudioDevice）。
        /// 遵循 Windows API 规范：通过 DeviceInformation.CreateFromIdAsync 创建设备对象，
        /// 再赋值给 MediaPlayer.AudioDevice，实现软件级单独输出（不影响其他应用）。
        /// </summary>
        /// <param name="deviceId">音频渲染设备的设备 ID，空字符串表示跟随系统默认设备。</param>
        private async Task ApplyVideoAudioDeviceAsync(string? deviceId)
        {
            // 超分模式：通过 mpv 的 audio-device 属性切换 WASAPI 输出设备
            if (_isMpvMode)
            {
                if (_mpvVideo == null)
                {
                    AppLogger.Info($"libmpv输出设备指令未应用：mpv 尚未初始化 ({deviceId})");
                    return;
                }
                await _mpvVideo.SetAudioDeviceAsync(deviceId);
                return;
            }

            try
            {
                var player = PlayerElement.MediaPlayer;
                if (player == null)
                {
                    AppLogger.Info($"视频播放器输出设备指令未应用：播放器尚未创建 ({deviceId})");
                    return;
                }

                // 记录当前播放状态，切换设备后恢复，避免切换瞬间产生静音或卡顿。
                bool wasPlaying = player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
                TimeSpan? keepPosition = null;
                if (wasPlaying && player.PlaybackSession.CanSeek)
                    keepPosition = player.PlaybackSession.Position;

                DeviceInformation? device = null;
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    device = await DeviceInformation.CreateFromIdAsync(deviceId);
                    if (device == null)
                    {
                        AppLogger.Warning($"视频输出设备 ID 无效，回退到系统默认设备: {deviceId}");
                        deviceId = string.Empty;
                    }
                }

                // 设备切换需要在媒体管线空闲时进行，先暂停再切换，完成后恢复播放状态。
                if (wasPlaying)
                    player.Pause();
                player.AudioDevice = device;
                if (wasPlaying)
                {
                    if (keepPosition.HasValue && player.PlaybackSession.CanSeek)
                        player.PlaybackSession.Position = keepPosition.Value;
                    player.Play();
                }

                if (string.IsNullOrEmpty(deviceId))
                    AppLogger.Info("视频播放器输出设备：跟随系统默认设备");
                else
                    AppLogger.Info($"视频播放器输出设备：{device?.Name ?? deviceId}");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"应用视频音频输出设备失败: {deviceId}");
            }
        }

        private void EnterWindowFullScreen()
        {
            if (_isWindowFullScreen) return;
            _isWindowFullScreen = true;
            (App.MainWindow as MainWindow)?.EnterPlayerFullScreen();
            PlayerElement.HorizontalAlignment = HorizontalAlignment.Stretch;
            PlayerElement.VerticalAlignment = VerticalAlignment.Stretch;
        }

        private void ExitWindowFullScreen()
        {
            if (!_isWindowFullScreen) return;
            _isWindowFullScreen = false;
            (App.MainWindow as MainWindow)?.ExitPlayerFullScreen();
            PlayerElement.HorizontalAlignment = HorizontalAlignment.Center;
            PlayerElement.VerticalAlignment = VerticalAlignment.Center;
        }

        private void ToggleSystemFullScreen()
        {
            if (_isSystemFullScreen)
                ExitFullScreen();
            else
                EnterFullScreen();
        }

        // ====== 画中画 ======
        // ★ 实现说明：CompactOverlayPresenter 不支持用户拖拽调整大小（微软已知限制），
        //   改用微软官方推荐的 workaround：OverlappedPresenter + IsAlwaysOnTop=true
        //   + 不可最大化/最小化 + PreferredMaximum/Minimum 限制尺寸范围，
        //   窗口带标准边框 → 鼠标放边缘即可调整大小，且不会拉得过大。

        // 画中画窗口尺寸范围（DIP）
        private const int PipMinWidth = 320;
        private const int PipMinHeight = 180;
        private const int PipMaxWidth = 960;
        private const int PipMaxHeight = 540;
        // 进入画中画时的初始尺寸（DIP）
        private const int PipInitWidth = 480;
        private const int PipInitHeight = 270;

        private void PictureInPictureButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePictureInPicture();
            ResetHideControlsTimer();
        }

        /// <summary>
        /// 切换画中画模式：进入后主窗口变为"置顶 + 可调整大小 + 无最大/最小按钮"的小窗
        /// （OverlappedPresenter，尺寸限制在 PipMin~PipMax 内），视频继续播放；
        /// 同时切换到简化控制栏（只保留进度条与核心播放按钮）；再次点击或退出播放器时恢复。
        /// </summary>
        private void TogglePictureInPicture()
        {
            if (_isPictureInPicture)
            {
                ExitPictureInPicture();
            }
            else
            {
                EnterPictureInPicture();
            }
        }

        private void EnterPictureInPicture()
        {
            var appWindow = GetAppWindow();
            if (appWindow == null) return;

            // 若处于系统全屏（FullScreenPresenter），先退出——画中画使用 OverlappedPresenter 实现
            if (_isSystemFullScreen)
            {
                ExitFullScreen();
            }

            try
            {
                // ★ 记录进入画中画前的窗口尺寸（物理像素），退出时恢复，避免关闭小窗后窗口尺寸改变
                var curSize = appWindow.Size;
                _pipRestoreWidth = curSize.Width;
                _pipRestoreHeight = curSize.Height;

                // 创建可调整大小的置顶画中画 presenter（微软官方推荐 workaround）
                var presenter = OverlappedPresenter.Create();
                presenter.IsMaximizable = false;   // 画中画小窗不允许最大化
                presenter.IsMinimizable = false;   // 不允许最小化（避免误操作）
                presenter.IsAlwaysOnTop = true;    // 置顶
                presenter.IsResizable = true;      // 允许用户拖拽边缘调整大小
                // 限制尺寸范围：不能拉得太大，也不能缩得太小
                presenter.PreferredMinimumWidth = PipMinWidth;
                presenter.PreferredMinimumHeight = PipMinHeight;
                presenter.PreferredMaximumWidth = PipMaxWidth;
                presenter.PreferredMaximumHeight = PipMaxHeight;
                appWindow.SetPresenter(presenter);

                // 设置初始画中画尺寸
                appWindow.Resize(new Windows.Graphics.SizeInt32(PipInitWidth, PipInitHeight));

                _isPictureInPicture = true;
                // 通知 MainWindow（关闭窗口时不保存画中画小窗尺寸覆盖用户设置）
                if (App.MainWindow is MainWindow mwPip)
                {
                    mwPip.IsPictureInPictureActive = true;
                }
                PictureInPictureIcon.Glyph = "\uE73F"; // 图标切换为"返回窗口"
                UpdatePictureInPictureUi(isPip: true);
                AppLogger.Info($"进入画中画（可调整大小置顶窗口，初始 {PipInitWidth}x{PipInitHeight}，上限 {PipMaxWidth}x{PipMaxHeight}）");
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"进入画中画失败: {ex.Message}");
            }
        }

        private void ExitPictureInPicture()
        {
            if (!_isPictureInPicture) return;
            try
            {
                var appWindow = GetAppWindow();
                if (appWindow != null)
                {
                    appWindow.SetPresenter(AppWindowPresenterKind.Default);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"退出画中画失败: {ex.Message}");
            }
            _isPictureInPicture = false;
            // 通知 MainWindow 画中画已退出
            if (App.MainWindow is MainWindow mwPip)
            {
                mwPip.IsPictureInPictureActive = false;
            }
            // ★ 恢复进入画中画前的窗口尺寸：不能同步立即 Resize——
            //   SetPresenter 切换是异步的，立即 Resize 会导致窗口视觉尺寸已变、
            //   XAML 布局/命中测试区域未同步（表现：退出小窗后底部按钮点不了，
            //   手动拖动/调整窗口后才恢复）。延迟恢复 + 强制布局刷新修复。
            if (_pipRestoreWidth > 0 && _pipRestoreHeight > 0)
            {
                _ = RestoreWindowSizeAndLayoutAsync();
            }
            PictureInPictureIcon.Glyph = "\uE7FA";
            UpdatePictureInPictureUi(isPip: false);
            AppLogger.Info("退出画中画");
        }

        /// <summary>
        /// 恢复进入画中画前的窗口尺寸并强制 XAML 重新布局。
        /// SetPresenter(Default) 与 AppWindow.Resize 均为异步生效，
        /// 需等待 presenter 切换完成后再 Resize，并在窗口应用新尺寸后
        /// 强制 RootGrid 布局，保证命中测试区域与视觉位置同步。
        /// </summary>
        private async Task RestoreWindowSizeAndLayoutAsync()
        {
            try
            {
                // 等待 presenter 切换完成（避免 Resize 与 presenter 切换竞态）
                await Task.Delay(120);
                // 若期间又进入了画中画（快速来回切换），跳过本次恢复
                if (_isPictureInPicture) return;
                var appWindow = GetAppWindow();
                if (appWindow == null) return;

                appWindow.Resize(new Windows.Graphics.SizeInt32(_pipRestoreWidth, _pipRestoreHeight));
                // 等待窗口应用新尺寸，再强制布局刷新（命中测试随布局同步）
                await Task.Delay(60);
                RootGrid.InvalidateMeasure();
                RootGrid.UpdateLayout();
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"恢复画中画窗口尺寸失败: {ex.Message}");
            }
        }

        /// <summary>退出播放器时若处于画中画模式，恢复默认窗口（避免残留置顶小窗）。</summary>
        private void ExitPictureInPictureIfActive()
        {
            ExitPictureInPicture();
        }

        /// <summary>
        /// 画中画 UI 切换：画中画窗口很小，隐藏完整控制栏（音量/速度/比例/设置/全屏/更多/标题栏等），
        /// 只显示简化控制栏 PipControls（进度条 + 上一个/后退/播放/前进/下一个，控件尺寸缩小）；
        /// 左上角按钮切换为"关闭画中画"（点击退出画中画而非返回主页）。
        /// </summary>
        private void UpdatePictureInPictureUi(bool isPip)
        {
            if (isPip)
            {
                // 隐藏完整控制栏与顶部栏（用 Visibility 硬切换，避免与 Opacity 动画互相干扰）
                TopBar.Visibility = Visibility.Collapsed;
                BottomControls.Visibility = Visibility.Collapsed;
                BottomControls.IsHitTestVisible = false;
                CenterPlayButton.Visibility = Visibility.Collapsed;
                CenterPlayButton.IsHitTestVisible = false;
                DebugInfoPanel.Visibility = Visibility.Collapsed;
                _isControlsVisible = false;

                // 简化控制栏与左上角"关闭画中画"按钮：显示但遵循自动显隐逻辑
                // （鼠标靠近边缘显示、移入中间延迟隐藏；底部阴影 BottomGradient 保留以增强对比度）
                PipControls.Visibility = Visibility.Visible;
                // 画中画窗口很小（初始 270 高），阴影高度收窄，避免盖住过多视频画面
                BottomGradient.Height = 80;
                PlayerBackButton.Visibility = Visibility.Visible;
                PlayerBackButtonIcon.Glyph = "\uE711"; // 关闭
                PlayerBackButton.SetValue(ToolTipService.ToolTipProperty, "关闭画中画");
                // ★ 关闭按钮直接悬浮在视频画面上，加半透明深色圆形底（悬浮按钮风格）
                //   提高可见度，避免与亮色视频内容重叠时看不清
                PlayerBackButton.Background = new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0x8C, 0x00, 0x00, 0x00));
                PlayerBackButton.BorderBrush = new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                PlayerBackButton.BorderThickness = new Thickness(1);
                PlayerBackButton.CornerRadius = new CornerRadius(21); // 42×42 圆形
                // 初始显示简化控制栏（悬停不显示、不自动隐藏；点击视频区域切换显示/隐藏）
                ShowControls();
            }
            else
            {
                PipControls.Visibility = Visibility.Collapsed;
                PipControls.IsHitTestVisible = false;

                // 恢复完整控制栏（显隐继续由鼠标位置驱动）
                TopBar.Visibility = Visibility.Visible;
                BottomControls.Visibility = Visibility.Visible;
                // ★ 修复：进入画中画时 BottomControls 被置为 Collapsed + IsHitTestVisible=false，
                //   退出时必须显式恢复命中测试——否则视觉可见但点不中（按钮"看得到点不了"，
                //   需等一次自动隐藏/显示才恢复）。
                BottomControls.IsHitTestVisible = true;
                BottomGradient.Visibility = Visibility.Visible;
                BottomGradient.Height = 200; // 恢复标准阴影高度
                PlayerBackButtonIcon.Glyph = "\uE72B"; // 返回
                PlayerBackButton.SetValue(ToolTipService.ToolTipProperty, "返回");
                // 恢复正常模式（按钮位于顶部标题栏内，无需悬浮背景）
                PlayerBackButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                PlayerBackButton.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                PlayerBackButton.BorderThickness = new Thickness(0);
                PlayerBackButton.CornerRadius = new CornerRadius(8);
                // ★ 强制重新进入显示流程：绕过 _isControlsVisible 短路，
                //   确保 Opacity 动画与 IsHitTestVisible 全部恢复到正常模式状态
                _isControlsVisible = false;
                ShowControls();
                UpdateCenterPlayButton();
            }
        }

        private void EnterFullScreen()
        {
            if (_isSystemFullScreen) return;
            _isSystemFullScreen = true;
            var appWindow = GetAppWindow();
            if (appWindow != null)
            {
                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            FullScreenIcon.Glyph = "\uE73F";
        }

        private void ExitFullScreen()
        {
            if (!_isSystemFullScreen) return;
            _isSystemFullScreen = false;
            var appWindow = GetAppWindow();
            if (appWindow != null)
            {
                appWindow.SetPresenter(AppWindowPresenterKind.Default);
            }
            FullScreenIcon.Glyph = "\uE740";
        }

        private AppWindow? GetAppWindow()
        {
            var window = App.MainWindow;
            if (window == null) return null;
            IntPtr hWnd = WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(windowId);
        }

        /// <summary>订阅窗口状态变化（后台播放：检测最小化）。</summary>
        private void SubscribeWindowStateChanged()
        {
            var appWindow = GetAppWindow();
            if (appWindow != null)
            {
                appWindow.Changed -= OnAppWindowChanged;
                appWindow.Changed += OnAppWindowChanged;
            }
        }

        /// <summary>退订窗口状态变化。</summary>
        private void UnsubscribeWindowStateChanged()
        {
            var appWindow = GetAppWindow();
            if (appWindow != null)
            {
                appWindow.Changed -= OnAppWindowChanged;
            }
        }

        /// <summary>
        /// 后台播放：窗口最小化/还原处理。
        /// "后台播放"设置开启（默认）：最小化后继续播放，不干预；
        /// 关闭：最小化时暂停播放，还原窗口时自动恢复播放。
        /// </summary>
        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidPresenterChange)
            {
                return;
            }

            bool minimized = sender.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized;
            if (minimized == _isWindowMinimized)
            {
                return;
            }
            _isWindowMinimized = minimized;

            // 后台播放设置关闭时才干预；开启（默认）时保持原有"最小化继续播放"行为
            if (!App.SettingsHelper.BackgroundPlayVideo)
            {
                if (minimized)
                {
                    // 最小化：正在播放则暂停并记录，还原时恢复
                    if (GetIsPlaying())
                    {
                        _pauseOnMinimize = true;
                        PauseCurrent();
                        AppLogger.Info("后台播放已关闭，窗口最小化暂停播放");
                    }
                }
                else if (_pauseOnMinimize)
                {
                    // 还原窗口：恢复因最小化而暂停的播放
                    _pauseOnMinimize = false;
                    PlayCurrent();
                    AppLogger.Info("窗口还原，恢复播放");
                }
            }
        }

        private void RequestDisplayActive()
        {
            if (_displayRequest == null)
                _displayRequest = new DisplayRequest();
            try
            {
                _displayRequest.RequestActive();
            }
            catch { }
        }

        private void ReleaseDisplayRequest()
        {
            if (_displayRequest != null)
            {
                try
                {
                    _displayRequest.RequestRelease();
                }
                catch { }
                _displayRequest = null;
            }
        }

        private void OpenFileLocation()
        {
            if (_currentItem == null) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer",
                    Arguments = $"/select,\"{_currentItem.FilePath}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private async Task ShowPropertiesAsync()
        {
            if (_currentItem == null) return;

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = "名称：" + _currentItem.FileName,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = "路径：" + _currentItem.FilePath,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = "大小：" + _currentItem.FileSizeText
            });
            content.Children.Add(new TextBlock
            {
                Text = "修改日期：" +
                    _currentItem.DateModified.ToString("yyyy-MM-dd HH:mm:ss")
            });

            TimeSpan duration =
                PlayerElement.MediaPlayer?.PlaybackSession.NaturalDuration ??
                _currentItem.Duration ??
                TimeSpan.Zero;
            if (duration > TimeSpan.Zero)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "时长：" + FormatTime(duration)
                });
            }

            // ★ 分辨率：优先从播放会话获取实时分辨率，其次使用缓存数据
            string? resolutionText = null;
            try
            {
                var session = PlayerElement.MediaPlayer?.PlaybackSession;
                if (session != null)
                {
                    uint videoW = session.NaturalVideoWidth;
                    uint videoH = session.NaturalVideoHeight;
                    if (videoW > 0 && videoH > 0)
                        resolutionText = $"{videoW}×{videoH}";
                }
            }
            catch { }
            resolutionText ??= _currentItem.VideoResolutionText;
            if (!string.IsNullOrEmpty(resolutionText))
            {
                content.Children.Add(new TextBlock
                {
                    Text = "分辨率：" + resolutionText
                });
            }

            // ★ 帧率：优先使用缓存数据（扫描时已提取）
            if (!string.IsNullOrEmpty(_currentItem.FrameRateText))
            {
                content.Children.Add(new TextBlock
                {
                    Text = "帧率：" + _currentItem.FrameRateText
                });
            }

            var dialog = new ContentDialog
            {
                Title = "视频属性",
                Content = content,
                CloseButtonText = "确定"
            };
            await DialogService.ShowAsync(dialog, XamlRoot);
        }

        private static string FormatTime(TimeSpan time)
        {
            if (time.TotalHours >= 1)
                return $"{time.Hours}:{time.Minutes:D2}:{time.Seconds:D2}";
            return $"{time.Minutes}:{time.Seconds:D2}";
        }

        /// <summary>格式化秒数为时间文本（兼容超分模式传递的秒数值）。</summary>
        private static string FormatTime(double seconds)
            => FormatTime(TimeSpan.FromSeconds(seconds));
    }

    public class VideoPlayerArgs
    {
        public List<MediaItem> Playlist { get; set; } = new();
        public int StartIndex { get; set; } = 0;
    }
}
