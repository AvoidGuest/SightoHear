using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
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
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Media.Playback;
using WinRT.Interop;
using SightoHear.Helpers;
using SightoHear.Models;
using SightoHear.Services;
using SightoHear.Services.Lyrics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using SkiaSharp;
using IO = System.IO;

namespace SightoHear
{
    public sealed partial class MainWindow : Window
    {
        private AppWindow _appWindow = null!;
        private FrameworkElement? _navigationPaneBackground;
        private SplitView? _navigationSplitView;
        private readonly SubclassProc _subclassProc;
        private bool _isClosed;
        private CancellationTokenSource? _playerCleanupCts;
        // ★ 页面切换后的延迟清理任务（治理浏览阶段的 GC 余波，避免累积到播放器渲染时）
        private CancellationTokenSource? _pageCleanupCts;

        /// <summary>
        /// Splash 画面隐藏后触发，用于延迟执行文件激活等操作
        /// </summary>
        public event EventHandler? SplashHidden;

        // 全局迷你播放器
        private readonly DispatcherTimer _miniPlayerTimer;
        private readonly MusicPlaybackService _playback = App.MusicPlayback;
        private bool _updatingProgress;
        private bool _updatingVolume;
        private bool _isMiniPlayerManuallyHidden;
        private DateTime _lastMiniPlayerPositionLogTime = DateTime.MinValue;
        private readonly Visual[] _barVisuals = new Visual[5];
        private bool _equalizerRunning;

        // Win2D 性能监测悬浮窗（HUD）：定时刷新性能数值
        private readonly DispatcherTimer _hudTimer;
        private readonly Stopwatch _equalizerStopwatch = new Stopwatch();
        // 资源诊断：周期快照定时器
        private readonly DispatcherTimer _diagnosticsTimer;
        private bool _miniPlayerPausedForOverlay;
        private bool _miniPlayerTimerWasRunningBeforeOverlay;
        private bool _equalizerWasRunningBeforeOverlay;
        private bool _isDragging;
        private double _dragStartPointerY;
        private double _dragStartTranslateY;
        private bool _isPointerOverCover;
        // 封面悬停效果：模糊 sigma（SkiaSharp 高斯模糊半径，52px 封面）
        private const float CoverHoverBlurSigma = 4f;
        private const float CoverPressedBlurSigma = 8f;

        // 侧边栏自适应阈值：窗口宽度低于此值时自动收起侧边栏并切换为悬浮样式
        private const double SidebarCollapseThreshold = 800.0;
        // 侧边栏宽度（展开时）
        private const double SidebarExpandedWidth = 320.0;
        // 侧边栏宽度（紧凑模式，仅图标）
        private const double SidebarCompactWidth = 48.0;
        private BitmapImage? _hoverBlurCover;    // 悬停模糊封面（SkiaSharp 预生成）
        private BitmapImage? _pressedBlurCover;  // 按下模糊封面（比悬停更模糊）
        private CancellationTokenSource? _coverBrightnessCts;
        private DispatcherTimer? _playModeToolTipTimer;

        // HUD 拖动状态
        private bool _isHudDragging;
        private Point _hudDragStartPointer;
        private double _hudDragStartX;
        private double _hudDragStartY;

        // 播放队列 Flyout
        private Flyout _queueFlyout = null!;
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

        private delegate IntPtr SubclassProc(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr subclassId,
            IntPtr referenceData);

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(
            IntPtr hWnd,
            SubclassProc subclassProc,
            UIntPtr subclassId,
            IntPtr referenceData);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        private const int IDC_ARROW = 32512;
        private const int IDC_SIZENWSE = 32642;
        private const int IDC_SIZENESW = 32643;
        private const int IDC_SIZEWE = 32644;
        private const int IDC_SIZENS = 32645;

        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        private const int GCL_HBRBACKGROUND = -10;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int color);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private IntPtr _hwnd;
        private IntPtr _splashBgBrush = IntPtr.Zero;

        // 播放器覆盖层（Overlay）字段
        /// <summary>覆盖层是否处于活跃状态（播放器正在全屏显示）</summary>
        public bool IsPlayerOverlayActive => _isPlayerOverlayActive;
        private bool _isPlayerOverlayActive;

        /// <summary>视频播放器是否处于画中画模式（由 VideoPlayerPage 进入/退出时置位，MainWindow 用于关闭时判断）。</summary>
        public bool IsPictureInPictureActive { get; set; }
        /// <summary>当前覆盖层中显示的页面类型（如 MusicPlayerPage / VideoPlayerPage / ImageViewerPage）</summary>
        public Type? CurrentPlayerPageType => PlayerFrame?.CurrentSourcePageType;
        private Visual? _overlayVisual;
        private Compositor? _compositor;
        private bool _contentSuspendedForPlayer;

        // 迷你播放器按钮折叠阈值
        private const double MiniPlayerNarrowThreshold = 700.0;
        // 被折叠进"更多"菜单的按钮状态
        private bool _isPlayModeHidden;
        private bool _isQueueHidden;
        private bool _isVolumeHidden;
        private bool _isHidePlayerHidden;
        private bool _isCloseHidden;

        public MainWindow()
        {
            _subclassProc = WindowSubclassProc;
            var ctorSw = System.Diagnostics.Stopwatch.StartNew();
            AppLogger.Info("[MainWindow.ctor] InitializeComponent 开始...");
            InitializeComponent();
            AppLogger.Info($"[MainWindow.ctor] InitializeComponent 完成 ({ctorSw.ElapsedMilliseconds}ms)");

            // ========== 标题栏 AnimatedIcon：使用 AddHandler(handledEventsToo:true) 注册指针事件 ==========
            // 原因：Button/AppBarButton 会将 PointerPressed/PointerReleased 标记为 Handled（以支持 Click），
            // XAML 声明的 PointerPressed="..." 收不到左键消息。AddHandler + handledEventsToo=true 可绕过。
            // 详见：https://learn.microsoft.com/en-us/windows/apps/design/controls/animated-icon
            // 颜色方向由 LeftButtons.Resources 中覆盖的 AppBarButtonBackgroundPointerOver/Pressed 控制。
            PaneToggleButton.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(PaneToggleButton_PointerPressed), handledEventsToo: true);
            PaneToggleButton.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(PaneToggleButton_PointerReleased), handledEventsToo: true);
            TitleBarBackButton.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(BackButton_PointerPressed), handledEventsToo: true);
            TitleBarBackButton.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(BackButton_PointerReleased), handledEventsToo: true);

            ctorSw.Restart();
            SetupWindow();
            AppLogger.Info($"[MainWindow.ctor] SetupWindow 完成 ({ctorSw.ElapsedMilliseconds}ms)");

            ctorSw.Restart();
            if (MainNavigationView.SettingsItem is NavigationViewItem settingsItem)
            {
                settingsItem.Content = "设置";
            }
            if (!FileActivationService.HasPendingFile)
            {
                MainNavigationView.SelectedItem = MainNavigationView.MenuItems[0];
                ContentFrame.Navigate(typeof(HomePage), null, new SuppressNavigationTransitionInfo());
            }
            AppLogger.Info($"[MainWindow.ctor] 首次导航 HomePage 完成 ({ctorSw.ElapsedMilliseconds}ms)");

            if (Application.Current.Resources.ContainsKey("SightoHearAccentBrush"))
            {
                MainNavigationView.Resources["NavigationViewSelectionIndicatorForeground"] = Application.Current.Resources["SightoHearAccentBrush"];
            }
            ContentFrame.Navigated += ContentFrame_Navigated;
            ApplyTheme(App.SettingsHelper.ThemeMode);
            ApplyBackdrop(App.SettingsHelper.BackdropType);
            Closed += (_, _) =>
            {
                _isClosed = true;
                CancelPlayerCleanup();
                Win2DPerformanceHud.Changed -= OnWin2DHudChanged;
                _hudTimer?.Stop();
                _diagnosticsTimer?.Stop();
                CompositionTarget.Rendering -= OnEqualizerFrame;
                CompositionTarget.Rendering -= OnQueueEqualizerFrame;
            };

            UpdateBackButtonState(false);

            if (Content is FrameworkElement rootElement)
            {
                rootElement.ActualThemeChanged += (_, _) =>
                {
                    // 播放器覆盖层激活时保持按钮为白色，不跟随主题变化
                    if (!_isPlayerOverlayActive)
                        TitleBarHelper.ApplySystemThemeToCaptionButtons(this, rootElement.ActualTheme);
                };
                TitleBarHelper.ApplySystemThemeToCaptionButtons(this, rootElement.ActualTheme);
            }

            // ── MiniPlayer 初始化 ──
            _miniPlayerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _miniPlayerTimer.Tick += MiniPlayerTimer_Tick;
            // 迷你播放器宽度变化时折叠/展开按钮
            MiniPlayer.SizeChanged += MiniPlayer_SizeChanged;
            _playback.CurrentItemChanged += OnMiniPlayer_CurrentItemChanged;
            _playback.PlaybackStateChanged += OnMiniPlayer_PlaybackStateChanged;
            _playback.VolumeChanged += OnMiniPlayer_VolumeChanged;
            _playback.PlayModeChanged += OnMiniPlayer_PlayModeChanged;
            _playback.PlaybackFailed += OnMiniPlayer_PlaybackFailed;
            _playback.ExternalPlaybackChanged += OnMiniPlayer_ExternalPlaybackChanged;

            if (!Application.Current.Resources.ContainsKey("MusicCoverConverter"))
                Application.Current.Resources["MusicCoverConverter"] = new FilePathToImageConverter();

            if (Application.Current.Resources.ContainsKey("SightoHearAccentBrush"))
            {
                MainNavigationView.Resources["NavigationViewSelectionIndicatorForeground"] = Application.Current.Resources["SightoHearAccentBrush"];
            }

            ApplyMiniPlayerBackdrop();
            SyncMiniPlayerFromService();
            BuildQueueFlyout();

            // ── 侧边栏固定快捷方式：订阅变更事件并重建分界线下方的快捷方式区域 ──
            SidebarShortcutService.Changed += OnSidebarShortcutsChanged;
            RebuildShortcutItems();

            RootGrid.SizeChanged += (_, _) =>
            {
                if (CapsulePopup.IsOpen)
                    PositionCapsule();
                ClampHudInWindow();
                UpdateSidebarLayout(); // 窗口大小变化时更新侧边栏布局
            };

            AppLogger.Info($"========== MainWindow 构造函数完成，总耗时 {ctorSw.Elapsed.TotalMilliseconds:F0}ms ==========");

            CustomTitleBar.SizeChanged += (_, _) => SetTitleBarDragRegions();

            // ── Win2D 性能监测悬浮窗（HUD）初始化 ──
            Win2DPerformanceHud.Changed += OnWin2DHudChanged;
            PlayerFrame.Navigated += PlayerFrame_Navigated;
            _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _hudTimer.Tick += HudTimer_Tick;
            _hudTimer.Start();
            RefreshWin2DHudSurfaceState();

            // ── 资源诊断：周期快照定时器（每 30 秒 Debug 级输出，可在调试页查看）──
            _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _diagnosticsTimer.Tick += (_, _) =>
            {
                if (_isClosed) return;
                if (ResourceDiagnosticsService.IsEnabled)
                    ResourceDiagnosticsService.LogSnapshot("周期快照(30s)", LogLevel.Debug);
            };
            _diagnosticsTimer.Start();
            ResourceDiagnosticsService.LogSnapshot("MainWindow 构造完成（初始快照）");

            // 初始化侧边栏布局（根据当前窗口大小决定是否收起）
            UpdateSidebarLayout();
        }

        private void SetupWindow()
        {
            _appWindow = GetAppWindowForCurrentWindow();

            // 设置窗口大小为工作区面积的 25%（16:10 比例）
            int w;
            int h;
            if (App.SettingsHelper.RememberWindowSize &&
                App.SettingsHelper.WindowWidth >= 640 &&
                App.SettingsHelper.WindowHeight >= 480)
            {
                w = App.SettingsHelper.WindowWidth;
                h = App.SettingsHelper.WindowHeight;
            }
            else
            {
                var workArea = DisplayArea.Primary.WorkArea;
                double targetArea = workArea.Width * workArea.Height * 0.25;
                h = (int)Math.Sqrt(targetArea / 1.6);
                w = (int)(h * 1.6);
            }
            // 将窗口大小限制在可用工作区范围内（防止因显示器变更导致超界）
            var primaryArea = DisplayArea.Primary.WorkArea;
            if (w > primaryArea.Width) w = (int)(primaryArea.Width * 0.85);
            if (h > primaryArea.Height) h = (int)(primaryArea.Height * 0.85);
            _appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = w, Height = h });

            // 校验已保存的窗口位置是否在当前可用显示器的工作区内
            // （如果上次退出时使用外接显示器，拔掉后坐标会指向屏幕外 → 窗口不可见但任务栏有图标）
            if (App.SettingsHelper.RememberWindowPosition &&
                App.SettingsHelper.HasWindowPosition)
            {
                var savedPos = new Windows.Graphics.PointInt32
                {
                    X = App.SettingsHelper.WindowX,
                    Y = App.SettingsHelper.WindowY
                };
                bool inAnyDisplay = InAnyDisplayWorkArea(savedPos);
                if (inAnyDisplay)
                    _appWindow.Move(savedPos);
                else
                    App.SettingsHelper.RememberWindowPosition = false;
            }
            _appWindow.Closing += AppWindow_Closing;
            SetWindowSubclass(
                WindowNative.GetWindowHandle(this),
                _subclassProc,
                new UIntPtr(1),
                IntPtr.Zero);

            // 扩展内容到标题栏，并用 SetDragRectangles 把按钮区排除在拖拽区域外，
            // 否则系统会拦截按钮区域的鼠标事件导致 AnimatedIcon 无法收到 Pressed 状态
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            SetTitleBarDragRegions();
            _appWindow.SetIcon("Assets/SightoHear.ico");
        }

        /// <summary>
        /// 判断指定坐标是否位于任一可用显示器的工作区内。
        /// 优先使用 DisplayArea.FindAll() 枚举，如果 WinRT COM 枚举失败则回退到主显示器 WorkArea 判断。
        /// </summary>
        private static bool InAnyDisplayWorkArea(Windows.Graphics.PointInt32 pt)
        {
            try
            {
                foreach (var area in DisplayArea.FindAll())
                {
                    var wa = area.WorkArea;
                    if (pt.X + 100 > wa.X && pt.X < wa.X + wa.Width - 100 &&
                        pt.Y + 100 > wa.Y && pt.Y < wa.Y + wa.Height - 100)
                        return true;
                }
                return false;
            }
            catch
            {
                // 如果 DisplayArea.FindAll() 的 COM 枚举失败，回退到主显示器校验
                var primary = DisplayArea.Primary.WorkArea;
                return pt.X + 100 > primary.X && pt.X < primary.X + primary.Width - 100 &&
                       pt.Y + 100 > primary.Y && pt.Y < primary.Y + primary.Height - 100;
            }
        }

        /// <summary>
        /// 通过 SetDragRectangles 精确划定可拖拽区域，把左侧按钮区排除在外，
        /// 防止系统拦截按钮区域的鼠标事件（否则 AnimatedIcon 收不到 PointerPressed/PointerReleased）。
        /// </summary>
        private void SetTitleBarDragRegions()
        {
            // 窗口已关闭则不再排队：排队中的延迟回调会在窗口销毁后执行，
            // 访问 Window.Content 等已失效的 XAML 对象会触发 AccessViolation（历史 9 次崩溃根因）
            if (_isClosed) return;

            DispatcherQueue.TryEnqueue(async () =>
            {
                // ★ 回调是 async void，任何逃逸异常都会直接终止进程（0xc000027b / 0xc0000005）
                // 必须完整 try-catch，且延迟后需再次检查窗口存活状态
                try
                {
                    await Task.Delay(50);

                    if (_isClosed) return;

                    // 捕获本地引用后再使用，避免 await 之后访问已失效的对象
                    var appWindow = _appWindow;
                    var content = Content;
                    var titleBar = CustomTitleBar;
                    var leftButtons = LeftButtons;
                    if (appWindow == null || content == null || titleBar == null || leftButtons == null)
                    {
                        return;
                    }

                    var xamlRoot = content.XamlRoot;
                    if (xamlRoot == null) return;

                    var scale = xamlRoot.RasterizationScale;
                    double leftButtonsWidth = leftButtons.ActualWidth;
                    double iconWidth = 16 + 12;
                    double titleLeft = leftButtonsWidth + iconWidth + 8;
                    double titleBarHeight = titleBar.ActualHeight;
                    double rightInset = 138;

                    if (titleBarHeight <= 0) return;

                    var dragRect = new Windows.Graphics.RectInt32
                    {
                        X = (int)((titleLeft + 8) * scale),
                        Y = 0,
                        Width = (int)((titleBar.ActualWidth - titleLeft - rightInset - 8) * scale),
                        Height = (int)(titleBarHeight * scale)
                    };

                    if (dragRect.Width > 0)
                    {
                        appWindow.TitleBar.SetDragRectangles(new[] { dragRect });
                    }
                }
                catch (Exception ex)
                {
                    // 窗口销毁/布局竞态等任何异常都不应逃逸（防 async void 崩溃）
                    AppLogger.Debug($"设置标题栏拖拽区域失败（窗口可能已关闭）: {ex.Message}");
                }
            });
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            // 全屏 / 画中画模式下关闭窗口：不保存窗口尺寸/位置，
            // 避免用小窗或全屏尺寸覆盖用户设置的常规窗口尺寸
            if (sender.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                return;
            if (sender.Presenter.Kind == AppWindowPresenterKind.CompactOverlay)
                return;
            if (IsPictureInPictureActive)
                return;

            if (App.SettingsHelper.RememberWindowSize)
            {
                App.SettingsHelper.WindowWidth = sender.Size.Width;
                App.SettingsHelper.WindowHeight = sender.Size.Height;
            }

            if (App.SettingsHelper.RememberWindowPosition)
            {
                App.SettingsHelper.WindowX = sender.Position.X;
                App.SettingsHelper.WindowY = sender.Position.Y;
                App.SettingsHelper.HasWindowPosition = true;
            }

            App.SettingsHelper.Save();
        }

        private AppWindow GetAppWindowForCurrentWindow()
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(windowId);
        }

        /// <summary>
        /// 不自定义拖拽区域，让整个标题栏作为默认拖拽区域，
        /// 左侧按钮依靠 XAML 控件自身接收输入，不拦截任何系统标题栏事件。
        /// </summary>

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            var prevPage = ContentFrame.Content?.GetType().Name ?? "null";
            AppLogger.Debug($"[MainWindow] ContentFrame 导航完成: 从={prevPage} 到={e.SourcePageType.Name}, 覆盖层激活={_isPlayerOverlayActive}");

            // ★ 资源诊断：追踪新页面实例（WeakReference），订阅 Unloaded 自动注销
            TrackFramePage(ContentFrame, e.Content);

            if (ContentFrame.Parent == RootGrid)
            {
                AppLogger.Info("[MainWindow] ContentFrame 在 RootGrid 中，退出播放器全屏布局");
                ExitPlayerFullScreen();
            }

            // 根据导航栈控制返回按钮 + 图标标题对齐状态
            UpdateBackButtonState(ContentFrame.CanGoBack);

            if (e.SourcePageType == typeof(HomePage))
                MainNavigationView.SelectedItem = MainNavigationView.MenuItems[0];
            else if (e.SourcePageType == typeof(VideoPage))
                MainNavigationView.SelectedItem = MainNavigationView.MenuItems[1];
            else if (e.SourcePageType == typeof(MusicPage))
                MainNavigationView.SelectedItem = MainNavigationView.MenuItems[2];
            else if (e.SourcePageType == typeof(GalleryPage))
                MainNavigationView.SelectedItem = MainNavigationView.MenuItems[3];
            else if (e.SourcePageType == typeof(SettingsPage))
                MainNavigationView.SelectedItem = MainNavigationView.SettingsItem;

            UpdateMiniPlayerVisibility();

            // ★ 页面切换后延迟清理：旧页面销毁产生大量待回收对象（BitmapImage、绑定等），
            //   若放任累积，多次浏览后 GC 频繁，打开 Win2D 播放器时会引发帧抖动。
            //   在导航完成、页面加载峰值过后用低优先级执行 Trim + 非阻塞 GC，把余波清干净。
            SchedulePageCleanup();
        }

        /// <summary>
        /// 安排页面切换后的延迟清理（Trim 缩略图缓存 + 非阻塞 GC）。
        /// 多次快速导航时取消上一次未执行的清理，只保留最近一次。
        /// </summary>
        private void SchedulePageCleanup()
        {
            _pageCleanupCts?.Cancel();
            _pageCleanupCts?.Dispose();
            var cts = new CancellationTokenSource();
            _pageCleanupCts = cts;
            _ = RunPageCleanupAsync(cts.Token);
        }

        private async Task RunPageCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                // 避开页面加载峰值（如 VideoPage 缩略图批量解码），等余波沉淀后再清理
                await Task.Delay(800, cancellationToken);
                if (_isClosed || cancellationToken.IsCancellationRequested)
                    return;

                // ★ GPU 资源清理必须在后台线程执行（Trim 涉及 WaitForPendingFinalizers
                //   和驱动同步，阻塞 UI 线程会导致明显卡顿）。
                await Task.Run(() =>
                {
                    if (_isClosed || cancellationToken.IsCancellationRequested)
                        return;

                    // 裁剪缩略图 CPU 内存缓存
                    ImageThumbnailService.TrimMemoryCache(512);

                    // ★ 核心修复：强制清理 GPU 侧碎片化的 D3D 资源记录。
                    //   浏览多页面后 Win2D CanvasBitmap 的 COM 释放累积在驱动延迟销毁队列中，
                    //   导致后续 Draw 每次都要在碎片化的分配记录中搜索，耗时从 4ms → 30-55ms。
                    //   TrimGpuResources 会 GC→WaitForPendingFinalizers→GC→CanvasDevice.Trim()
                    //   四步走，彻底清空驱动内部的碎片。
                    Win2DDeviceManager.TrimGpuResources();
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 被新的导航清理任务取代，属正常取消
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "页面切换后延迟清理");
            }
        }

        // ───────────────────────── 资源诊断：页面实例追踪 ─────────────────────────

        /// <summary>
        /// 追踪 Frame 导航产生的新页面实例：登记到 ResourceDiagnosticsService，
        /// 并订阅其 Unloaded 事件，在页面卸载时自动注销（用于泄漏检测）。
        /// </summary>
        private void TrackFramePage(Frame frame, object? pageContent)
        {
            if (pageContent is FrameworkElement page)
            {
                ResourceDiagnosticsService.TrackPage(page, page.GetType().Name);
                // 先退订再订阅，防止重复订阅同一实例
                page.Unloaded -= FramePage_UnloadedForTracking;
                page.Unloaded += FramePage_UnloadedForTracking;
            }
            _ = frame; // 保留参数以备后续扩展（区分 ContentFrame / PlayerFrame）
        }

        /// <summary>页面卸载时自动注销追踪（订阅于 TrackFramePage）。</summary>
        private void FramePage_UnloadedForTracking(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement page)
            {
                page.Unloaded -= FramePage_UnloadedForTracking;
                ResourceDiagnosticsService.UntrackPage(page);
                ResourceDiagnosticsService.LogSnapshot($"页面卸载: {page.GetType().Name}", LogLevel.Debug);
            }
        }

        /// <summary>
        /// PlayerFrame 导航完成：刷新 HUD 界面状态，并追踪播放器页面实例（Win2D 画布页面）。
        /// </summary>
        private void PlayerFrame_Navigated(object sender, NavigationEventArgs e)
        {
            RefreshWin2DHudSurfaceState();
            TrackFramePage(PlayerFrame, e.Content);
            ResourceDiagnosticsService.LogSnapshot($"播放器导航: {e.SourcePageType.Name}", LogLevel.Debug);
        }

        // ───────────────────────── Win2D 性能监测悬浮窗（HUD） ─────────────────────────

        /// <summary>
        /// HUD 全局状态变化（设置页开关变更等）→ 刷新悬浮窗。
        /// </summary>
        private void OnWin2DHudChanged() => RefreshWin2DHudSurfaceState();

        /// <summary>
        /// 更新"当前是否为 Win2D 界面"状态并刷新悬浮窗显示。
        /// Win2D 界面：图片查看器（ImageViewerPage）与音乐播放器（MusicPlayerPage，
        /// 背景画布 / 歌词画布），二者均以播放器覆盖层承载。
        /// </summary>
        private void RefreshWin2DHudSurfaceState()
        {
            bool isWin2D = false;
            if (_isPlayerOverlayActive && PlayerFrame?.CurrentSourcePageType is Type pageType)
            {
                isWin2D = pageType == typeof(MusicPlayerPage) || pageType == typeof(ImageViewerPage);
            }
            if (Win2DPerformanceHud.IsWin2DSurface != isWin2D)
            {
                // 进出 Win2D 界面时重置采样，保证平均帧率 / 掉帧统计从当前会话开始
                Win2DPerformanceHud.IsWin2DSurface = isWin2D;
                Win2DPerformanceHud.ResetSampling();
                AppLogger.Debug($"[Win2D HUD] Win2D 界面状态: {isWin2D}");
            }
            UpdateWin2DHud();
        }

        /// <summary>
        /// HUD 行数据种类：决定定时刷新时如何生成数值文本。
        /// </summary>
        private enum HudRowKind
        {
            Info,           // 纯提示文本，不参与数值刷新
            Fps,
            AvgFps,
            FrameTime,
            UpdateTime,
            DrawTime,
            Jitter,
            DroppedFrames,
            Memory,
            Resolution,
            Gpu
        }

        /// <summary>
        /// 刷新悬浮窗内容。
        /// 总开关关闭时不显示；非 Win2D 界面只显示一行提示；
        /// Win2D 界面按监测行开关逐行显示（默认完整统计）。
        /// 行结构（开关变化时）重建，数值由 <see cref="HudTimer_Tick"/> 定时刷新。
        /// </summary>
        private void UpdateWin2DHud()
        {
            if (Win2DHud == null || Win2DHudContent == null) return;

            bool visible = Win2DPerformanceHud.IsEnabled;
            Win2DHud.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) return;

            ClampHudInWindow();

            Win2DHudContent.Children.Clear();

            // 非 Win2D 界面：只显示一行提示
            if (!Win2DPerformanceHud.IsWin2DSurface)
            {
                AddWin2DHudRow(HudRowKind.Info, "当前不是 Win2D 界面");
                return;
            }

            // Win2D 界面：按监测行开关逐行构建（默认完整统计）
            AddWin2DHudRow(Win2DPerformanceHud.ShowFps, HudRowKind.Fps, "FPS: --");
            AddWin2DHudRow(Win2DPerformanceHud.ShowAvgFps, HudRowKind.AvgFps, "平均帧率: --");
            AddWin2DHudRow(Win2DPerformanceHud.ShowFrameTime, HudRowKind.FrameTime, "帧时长: -- ms");
            AddWin2DHudRow(Win2DPerformanceHud.ShowUpdateTime, HudRowKind.UpdateTime, "Update: -- ms");
            AddWin2DHudRow(Win2DPerformanceHud.ShowDrawTime, HudRowKind.DrawTime, "Draw: -- ms");
            AddWin2DHudRow(Win2DPerformanceHud.ShowFrameJitter, HudRowKind.Jitter, "帧率波动: -- ms");
            AddWin2DHudRow(Win2DPerformanceHud.ShowDroppedFrames, HudRowKind.DroppedFrames, "掉帧: --");
            AddWin2DHudRow(Win2DPerformanceHud.ShowMemory, HudRowKind.Memory, "内存: -- MB");
            AddWin2DHudRow(Win2DPerformanceHud.ShowResolution, HudRowKind.Resolution, "分辨率: --");
            AddWin2DHudRow(Win2DPerformanceHud.ShowGpuMode, HudRowKind.Gpu, "GPU: --");

            if (Win2DHudContent.Children.Count == 0)
                AddWin2DHudRow(HudRowKind.Info, "未选择监测项");

            AppLogger.Debug($"[Win2D HUD] 刷新: ShowFps={Win2DPerformanceHud.ShowFps}, 行数={Win2DHudContent.Children.Count}");

            // 立即刷新一次数值（HUD 定时器保持运行）
            RefreshHudValues();
        }

        /// <summary>
        /// HUD 数值定时刷新（UI 线程，500ms 一次）：从全局控制器读取性能快照，
        /// 按每行 <see cref="HudRowKind"/> 生成最新文本，不重建行结构（避免闪烁/抖动）。
        /// </summary>
        private void HudTimer_Tick(object? sender, object e)
        {
            if (_isClosed) return;
            if (Win2DHud?.Visibility != Visibility.Visible) return;
            if (!Win2DPerformanceHud.IsWin2DSurface) return;
            RefreshHudValues();
        }

        /// <summary>用最新性能快照更新 HUD 各行数值文本。</summary>
        private void RefreshHudValues()
        {
            if (Win2DHudContent == null) return;

            var snap = Win2DPerformanceHud.GetSnapshot();
            foreach (var child in Win2DHudContent.Children)
            {
                if (child is not TextBlock tb || tb.Tag is not HudRowKind kind) continue;
                tb.Text = kind switch
                {
                    HudRowKind.Fps => $"FPS: {snap.Fps:F1}",
                    HudRowKind.AvgFps => $"平均帧率: {snap.AvgFps:F1}",
                    HudRowKind.FrameTime => $"帧时长: {snap.FrameTimeMs:F1} ms",
                    HudRowKind.UpdateTime => $"Update: {snap.UpdateMs:F2} ms",
                    HudRowKind.DrawTime => $"Draw: {snap.DrawMs:F2} ms",
                    HudRowKind.Jitter => $"帧率波动: {snap.JitterMs:F1} ms",
                    HudRowKind.DroppedFrames => $"掉帧: {snap.DroppedFrames}",
                    HudRowKind.Memory => $"内存: {snap.MemoryMb:F0} MB",
                    HudRowKind.Resolution => $"分辨率: {snap.Resolution}",
                    HudRowKind.Gpu => $"GPU: {snap.GpuInfo}",
                    _ => tb.Text
                };
            }
        }

        /// <summary>根据开关状态决定是否添加一行 HUD 文本。</summary>
        private bool AddWin2DHudRow(bool show, HudRowKind kind, string text) => show && AddWin2DHudRow(kind, text);

        /// <summary>向 HUD 添加一行等宽字体文本（Tag 标记数据种类，供定时刷新定位）。</summary>
        private bool AddWin2DHudRow(HudRowKind kind, string text)
        {
            if (Win2DHudContent == null) return false;
            Win2DHudContent.Children.Add(new TextBlock
            {
                Text = text,
                Tag = kind,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Colors.White)
            });
            return true;
        }

        // ───────────────────────── HUD 拖动（鼠标按住拖动，RenderTransform 平滑跟随） ─────────────────────────

        /// <summary>
        /// 按下开始拖动：记录按下瞬间的指针位置与当前偏移，
        /// 并捕获指针，保证快速移动时事件不丢失（避免拖动中断导致的抽搐）。
        /// </summary>
        private void Win2DHud_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
            if (Win2DHudTransform == null) return;

            _isHudDragging = true;
            _hudDragStartPointer = e.GetCurrentPoint(RootGrid).Position;
            _hudDragStartX = Win2DHudTransform.X;
            _hudDragStartY = Win2DHudTransform.Y;
            Win2DHud.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        /// <summary>
        /// 移动：新偏移 = 起始偏移 + 指针增量（始终基于按下时的相对关系，元素不会跳变）。
        /// 全部使用 DIP 布局坐标（GetCurrentPoint(RootGrid) 与 TranslateTransform 单位一致），
        /// 最后钳制在窗口内部，避免悬浮窗拖出窗口。
        /// </summary>
        private void Win2DHud_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isHudDragging) return;
            if (Win2DHudTransform == null) return;

            var pos = e.GetCurrentPoint(RootGrid).Position;
            double newX = _hudDragStartX + (pos.X - _hudDragStartPointer.X);
            double newY = _hudDragStartY + (pos.Y - _hudDragStartPointer.Y);

            // 元素实际左上角 = Margin + RenderTransform，需确保其始终位于窗口内
            double minX = -Win2DHud.Margin.Left;
            double minY = -Win2DHud.Margin.Top;
            double maxX = RootGrid.ActualWidth - Win2DHud.Margin.Left - Win2DHud.ActualWidth;
            double maxY = RootGrid.ActualHeight - Win2DHud.Margin.Top - Win2DHud.ActualHeight;

            Win2DHudTransform.X = Math.Clamp(newX, minX, Math.Max(minX, maxX));
            Win2DHudTransform.Y = Math.Clamp(newY, minY, Math.Max(minY, maxY));
            e.Handled = true;
        }

        private void Win2DHud_PointerReleased(object sender, PointerRoutedEventArgs e) => EndHudDrag(e);

        private void Win2DHud_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndHudDrag(e);

        private void Win2DHud_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndHudDrag(null);

        private void EndHudDrag(PointerRoutedEventArgs? e)
        {
            if (!_isHudDragging) return;
            _isHudDragging = false;
            if (e?.Pointer != null)
                Win2DHud.ReleasePointerCapture(e.Pointer);
        }

        /// <summary>
        /// 窗口尺寸变化后将 HUD 偏移重新钳制在窗口内部（防止窗口缩小后悬浮窗被挤出）。
        /// </summary>
        private void ClampHudInWindow()
        {
            if (Win2DHud == null || Win2DHudTransform == null) return;
            if (Win2DHud.Visibility != Visibility.Visible) return;

            double minX = -Win2DHud.Margin.Left;
            double minY = -Win2DHud.Margin.Top;
            double maxX = RootGrid.ActualWidth - Win2DHud.Margin.Left - Win2DHud.ActualWidth;
            double maxY = RootGrid.ActualHeight - Win2DHud.Margin.Top - Win2DHud.ActualHeight;

            Win2DHudTransform.X = Math.Clamp(Win2DHudTransform.X, minX, Math.Max(minX, maxX));
            Win2DHudTransform.Y = Math.Clamp(Win2DHudTransform.Y, minY, Math.Max(minY, maxY));
        }

        /// <summary>
        /// 控制自定义返回按钮显示/隐藏。
        /// </summary>
        private void UpdateBackButtonState(bool canGoBack)
        {
            TitleBarBackButton.Visibility = canGoBack ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 供子页面（如 VideoPage）调用，更新返回按钮状态。
        /// </summary>
        public void UpdateTitleBarBackButton(bool canGoBack)
        {
            UpdateBackButtonState(canGoBack);
        }

        /// <summary>
        /// 更新视频页面的返回按钮状态。
        /// 同时考虑文件夹内部导航和主框架导航栈，
        /// 确保切换 Tab 时不会丢失从主页到视频页的返回历史。
        /// </summary>
        public void UpdateVideoPageBackButtonState(bool folderCanGoBack)
        {
            UpdateBackButtonState(folderCanGoBack || ContentFrame.CanGoBack);
        }

        /// <summary>
        /// 更新图库页面的返回按钮状态。
        /// 同时考虑文件夹内部导航和主框架导航栈。
        /// </summary>
        public void UpdateGalleryPageBackButtonState(bool folderCanGoBack)
        {
            UpdateBackButtonState(folderCanGoBack || ContentFrame.CanGoBack);
        }

        /// <summary>
        /// 由子页面（如 MusicPage）通过主导航 Frame 打开一个新页面。
        /// 如果目标页面是播放器类页面（MusicPlayerPage/VideoPlayerPage/ImageViewerPage），
        /// 则自动使用覆盖层（Overlay）代替 Frame 导航。
        /// </summary>
        /// <param name="animate">播放器覆盖层是否从底部滑入启动时为 false，直接定位</param>
        public void NavigateMainFrame(Type pageType, object? parameter, bool animate = true)
        {
            // 播放器类页面走覆盖层（Overlay）模式，不再导航 ContentFrame
            if (IsPlayerPageType(pageType))
            {
                ShowPlayerOverlay(pageType, parameter, animate);
                return;
            }
            ContentFrame.Navigate(pageType, parameter,
                animate ? new DrillInNavigationTransitionInfo() : new SuppressNavigationTransitionInfo());
        }

        /// <summary>
        /// 自定义返回按钮 → 导航回上一页。
        /// </summary>
        private void TitleBarBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is SettingsPage settingsPage && settingsPage.CanGoBack)
            {
                settingsPage.GoBack();
            }
            else if (ContentFrame.Content is VideoPage videoPage && videoPage.CanNavigateBack)
            {
                videoPage.NavigateBack();
            }
            else if (ContentFrame.Content is GalleryPage galleryPage && galleryPage.CanNavigateBack)
            {
                galleryPage.NavigateBack();
            }
            else if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
            }
        }

        /// <summary>
        /// 自定义汉堡按钮 → 展开/收起导航面板。
        /// </summary>
        private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
        {
            MainNavigationView.IsPaneOpen = !MainNavigationView.IsPaneOpen;
        }

        /// <summary>
        /// 根据窗口宽度更新侧边栏布局：
        /// - 窗口宽度 >= 阈值：展开模式（Left），侧边栏正常显示
        /// - 窗口宽度 < 阈值：紧凑模式（LeftCompact），侧边栏仅显示图标，悬浮在内容上方
        /// </summary>
        private void UpdateSidebarLayout()
        {
            if (_isClosed || MainNavigationView == null) return;

            double windowWidth = RootGrid.ActualWidth;
            if (windowWidth <= 0) return;

            bool shouldCollapse = windowWidth < SidebarCollapseThreshold;
            bool isCurrentlyCompact = MainNavigationView.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftCompact;

            // 无需切换
            if (shouldCollapse == isCurrentlyCompact) return;

            if (shouldCollapse)
            {
                MainNavigationView.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact;
                MainNavigationView.IsPaneOpen = false;
                UpdateSidebarPaneBackground();
            }
            else
            {
                MainNavigationView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
                MainNavigationView.IsPaneOpen = true;
                UpdateSidebarPaneBackground();
            }
        }

        // ===================================================================================
        // [已修复] 标题栏 AnimatedIcon — 使用 AppBarButton + AddHandler
        // ===================================================================================
        //
        // 最终解决方案：
        // 1. XAML 中改用 AppBarButton（模板与 AnimatedIcon 兼容，无 PointerDownThemeAnimation）
        // 2. 通过 AddHandler(..., handledEventsToo: true) 注册 PointerPressed/Released
        //    （因为 Button/AppBarButton 会将这两个事件标记为 Handled 以支持 Click）
        // 3. 颜色由 LeftButtons.Resources 中覆盖的 AppBarButtonBackgroundPointerOver/Pressed 控制
        //
        // 颜色方向：
        //   深色模式：悬停 = #18FFFFFF（变亮），按下 = #2AFFFFFF（更亮）
        //   浅色模式：悬停 = #0C000000（变暗），按下 = #18000000（更暗）
        // ===================================================================================

        private void PaneToggleButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(HamburgerAnimatedIcon, "PointerOver");
        }

        private void PaneToggleButton_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(HamburgerAnimatedIcon, "Normal");
        }

        private void PaneToggleButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(HamburgerAnimatedIcon, "Pressed");
        }

        private void PaneToggleButton_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(HamburgerAnimatedIcon, "PointerOver");
        }

        private void BackButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(BackButtonAnimatedIcon, "PointerOver");
        }

        private void BackButton_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(BackButtonAnimatedIcon, "Normal");
        }

        private void BackButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(BackButtonAnimatedIcon, "Pressed");
        }

        private void BackButton_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(BackButtonAnimatedIcon, "PointerOver");
        }

        /// <summary>
        /// NavigationView BackRequested（硬件返回键/Alt+← 等手势）。
        /// </summary>
        private void MainNavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (ContentFrame.Content is SettingsPage settingsPage && settingsPage.CanGoBack)
            {
                settingsPage.GoBack();
            }
            else if (ContentFrame.Content is VideoPage videoPage && videoPage.CanNavigateBack)
            {
                videoPage.NavigateBack();
            }
            else if (ContentFrame.Content is GalleryPage galleryPage && galleryPage.CanNavigateBack)
            {
                galleryPage.NavigateBack();
            }
            else if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
            }
        }

        private void MainNavigationView_Loaded(object sender, RoutedEventArgs e)
        {
            _navigationSplitView = FindDescendant<SplitView>(MainNavigationView);
            _navigationPaneBackground =
                FindNamedElement(MainNavigationView, "PaneContentGrid") ??
                FindNamedElement(MainNavigationView, "PaneRoot");
            ApplyBackdrop(App.SettingsHelper.BackdropType);
        }

        private void MainNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                if (ContentFrame.Content?.GetType() != typeof(SettingsPage))
                {
                    AppLogger.Info("主窗口导航: 设置页面");
                    ContentFrame.Navigate(typeof(SettingsPage), null, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
                }
                return;
            }

            if (args.SelectedItem is NavigationViewItem item)
            {
                string? tag = item.Tag as string;
                Type? targetType = tag switch
                {
                    "HomePage" => typeof(HomePage),
                    "VideoPage" => typeof(VideoPage),
                    "MusicPage" => typeof(MusicPage),
                    "GalleryPage" => typeof(GalleryPage),
                    "RecycleBinPage" => typeof(RecycleBinPage),
                    _ => null
                };

                if (targetType != null && ContentFrame.Content?.GetType() != targetType)
                {
                    AppLogger.Info($"主窗口导航: {tag}");
                    ContentFrame.Navigate(targetType, null, new SuppressNavigationTransitionInfo());
                }
            }
        }

        // ===================================================================================
        // 侧边栏固定快捷方式（分界线下方区域）
        // ===================================================================================

        /// <summary>快捷方式 Id → NavigationViewItem 的映射，用于增量更新。</summary>
        private readonly Dictionary<string, NavigationViewItem> _shortcutNavItems = new();

        /// <summary>服务集合变化时刷新侧边栏 UI（回到 UI 线程执行）。</summary>
        private void OnSidebarShortcutsChanged()
        {
            DispatcherQueue.TryEnqueue(RebuildShortcutItems);
        }

        /// <summary>
        /// 重建侧边栏快捷方式区域：
        /// 清空旧项，按固定时间顺序在分界线下方重新添加快捷方式 NavigationViewItem。
        /// </summary>
        private void RebuildShortcutItems()
        {
            foreach (var item in _shortcutNavItems.Values.ToList())
            {
                if (MainNavigationView.MenuItems.Contains(item))
                    MainNavigationView.MenuItems.Remove(item);
            }
            _shortcutNavItems.Clear();

            var shortcuts = SidebarShortcutService.Shortcuts
                .OrderBy(s => s.DateCreated)
                .ToList();

            // 无固定项时隐藏分界线
            ShortcutSeparator.Visibility = shortcuts.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            foreach (var shortcut in shortcuts)
            {
                var item = CreateShortcutItem(shortcut);
                _shortcutNavItems[shortcut.Id] = item;
                MainNavigationView.MenuItems.Add(item);
            }
        }

        /// <summary>创建单个快捷方式 NavigationViewItem（含点击打开与右键取消固定）。</summary>
        private NavigationViewItem CreateShortcutItem(SidebarShortcut shortcut)
        {
            var item = new NavigationViewItem
            {
                Content = shortcut.Title,
                Tag = "Shortcut:" + shortcut.Id,
                Icon = new FontIcon
                {
                    FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"),
                    Glyph = GetShortcutGlyph(shortcut.Type),
                    FontSize = 16
                }
            };
            ToolTipService.SetToolTip(item, shortcut.Title);
            item.Tapped += ShortcutItem_Tapped;

            // 右键菜单：打开 / 取消固定
            var menu = new MenuFlyout();
            var openItem = new MenuFlyoutItem
            {
                Text = "打开",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8A7" }
            };
            openItem.Click += (_, _) => OpenSidebarShortcut(shortcut);
            menu.Items.Add(openItem);

            var unpinItem = new MenuFlyoutItem
            {
                Text = "取消固定",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE74D" }
            };
            unpinItem.Click += (_, _) => SidebarShortcutService.Remove(shortcut.Id);
            menu.Items.Add(unpinItem);
            item.ContextFlyout = menu;

            return item;
        }

        /// <summary>
        /// 根据快捷方式所属模块返回统一的系统图标字形（Segoe MDL2 Assets）。
        /// 音乐相关 → 音乐图标；视频相关 → 视频图标；图库相关 → 图库图标。
        /// </summary>
        private static string GetShortcutGlyph(SidebarShortcutType type)
        {
            return type switch
            {
                // 音乐模块
                SidebarShortcutType.MusicPlaylist => "\uE8D6",
                SidebarShortcutType.MusicArtist => "\uE8D6",
                SidebarShortcutType.MusicAlbum => "\uE8D6",
                SidebarShortcutType.MusicFolder => "\uE8D6",
                // 视频模块（与基础设置页“视频库”图标一致）
                SidebarShortcutType.VideoFolder => "\uE8B2",
                SidebarShortcutType.VideoFavorite => "\uE8B2",
                // 图库模块（与基础设置页“图库”图标一致）
                SidebarShortcutType.GalleryFolder => "\uEB9F",
                SidebarShortcutType.GalleryAlbum => "\uEB9F",
                _ => "\uE8D6"
            };
        }

        /// <summary>点击快捷方式项 → 打开对应详情页。</summary>
        private void ShortcutItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is NavigationViewItem { Tag: string tag } &&
                tag.StartsWith("Shortcut:", StringComparison.Ordinal))
            {
                string id = tag.Substring("Shortcut:".Length);
                var shortcut = SidebarShortcutService.Shortcuts
                    .FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
                if (shortcut != null)
                    OpenSidebarShortcut(shortcut);
            }
        }

        /// <summary>
        /// 打开侧边栏快捷方式对应的详情页。
        /// 数据可能不在内存中，各分支自行从磁盘缓存/JSON 加载后构建导航参数。
        /// </summary>
        private void OpenSidebarShortcut(SidebarShortcut shortcut)
        {
            if (shortcut == null)
                return;
            try
            {
                // 打开前用最新数据同步名称/标题（覆盖内容在详情页重命名后未同步的场景）
                SyncShortcutTitle(shortcut);
                switch (shortcut.Type)
                {
                    case SidebarShortcutType.MusicPlaylist:
                        OpenMusicPlaylistShortcut(shortcut);
                        break;
                    case SidebarShortcutType.MusicArtist:
                        OpenMusicArtistShortcut(shortcut);
                        break;
                    case SidebarShortcutType.MusicAlbum:
                        OpenMusicAlbumShortcut(shortcut);
                        break;
                    case SidebarShortcutType.MusicFolder:
                        OpenMusicFolderShortcut(shortcut);
                        break;
                    case SidebarShortcutType.VideoFolder:
                        OpenVideoFolderShortcut(shortcut);
                        break;
                    case SidebarShortcutType.VideoFavorite:
                        OpenVideoFavoriteShortcut(shortcut);
                        break;
                    case SidebarShortcutType.GalleryFolder:
                        OpenGalleryFolderShortcut(shortcut);
                        break;
                    case SidebarShortcutType.GalleryAlbum:
                        OpenGalleryAlbumShortcut(shortcut);
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "打开侧边栏快捷方式失败");
            }
        }

        /// <summary>加载音乐库数据：优先使用 MusicDataCache，否则读取磁盘缓存。</summary>
        private static List<MediaItem> LoadMusicItems()
        {
            return MusicDataCache.IsInitialized
                ? MusicDataCache.AllMusic
                : MediaScanner.LoadFromCache("Music");
        }

        /// <summary>加载指定收藏文件（视频收藏 / 图库相册）到内存。</summary>
        private static List<Playlist> LoadFavoritesFromFile(string fileName)
        {
            var path = IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SightoHear", fileName);
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<List<Playlist>>(File.ReadAllText(path))
                           ?? new List<Playlist>();
            }
            catch { }
            return new List<Playlist>();
        }

        /// <summary>将收藏列表写回指定收藏文件。</summary>
        private static void SaveFavoritesToFile(string fileName, List<Playlist> favorites)
        {
            var path = IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SightoHear", fileName);
            try
            {
                var dir = IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(favorites));
            }
            catch { }
        }

        /// <summary>快捷方式指向的内容已不存在：移除快捷方式并提示用户。</summary>
        private async void ShowShortcutGoneDialogAndRemove(SidebarShortcut shortcut)
        {
            SidebarShortcutService.Remove(shortcut.Id);
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "快捷方式已失效",
                    Content = $"“{shortcut.Title}”对应的内容已不存在，已自动从侧边栏移除。",
                    CloseButtonText = "确定"
                };
                await DialogService.ShowAsync(dialog, RootGrid.XamlRoot);
            }
            catch { }
        }

        // ── 各类型快捷方式的打开逻辑 ──

        /// <summary>按歌单 Id 查找持久化歌单（加载到 MusicDataCache）。</summary>
        private static Playlist? FindPlaylistById(string id)
        {
            MusicDataCache.LoadPlaylists();
            return MusicDataCache.AllPlaylists.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>按 Id 查找指定收藏文件中的收藏夹 / 相册。</summary>
        private static Playlist? FindFavoriteById(string fileName, string id)
        {
            return LoadFavoritesFromFile(fileName).FirstOrDefault(f =>
                string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 同步快捷方式的名称与标题（内容在详情页被重命名后调用）。
        /// <paramref name="knownName"/> 为调用方已知的最新名称（避免重新加载数据源）；
        /// 未提供时按类型从数据源读取。数据不可用或名称未变化时不修改任何内容。
        /// </summary>
        private void SyncShortcutTitle(SidebarShortcut shortcut, string? knownName = null)
        {
            if (shortcut == null)
                return;

            string? newName = knownName;
            if (newName == null)
            {
                newName = shortcut.Type switch
                {
                    SidebarShortcutType.MusicPlaylist => FindPlaylistById(shortcut.Key)?.Name,
                    SidebarShortcutType.VideoFavorite => FindFavoriteById("video_favorites.json", shortcut.Key)?.Name,
                    SidebarShortcutType.GalleryAlbum => FindFavoriteById("gallery_favorites.json", shortcut.Key)?.Name,
                    _ => null
                };
            }

            if (newName != null && !string.Equals(newName, shortcut.Name, StringComparison.Ordinal))
            {
                shortcut.Name = newName;
                shortcut.Title = shortcut.DisplayTitle;
                SidebarShortcutService.Save();
                RebuildShortcutItems();
            }
        }

        /// <summary>
        /// 详情页保存后调用：若对应内容已固定到侧边栏，同步其名称/标题。
        /// 无论从哪个入口（侧边栏快捷方式 / 各模块主页）进入详情页均生效。
        /// </summary>
        public static void NotifyDetailSaved(SidebarShortcutType type, string id, string name)
        {
            try
            {
                if (App.MainWindow is MainWindow window)
                    window.SyncSidebarShortcutTitle(type, id, name);
            }
            catch { }
        }

        /// <summary>按类型与内容 Id 找到侧边栏中对应的快捷方式并同步其名称/标题。</summary>
        private void SyncSidebarShortcutTitle(SidebarShortcutType type, string id, string name)
        {
            var shortcut = SidebarShortcutService.Shortcuts.FirstOrDefault(s =>
                s.Type == type && string.Equals(s.Key, id, StringComparison.OrdinalIgnoreCase));
            if (shortcut == null)
                return;
            SyncShortcutTitle(shortcut, name);
        }

        /// <summary>音乐歌单：按歌单 Id 从持久化歌单中查找。</summary>
        private void OpenMusicPlaylistShortcut(SidebarShortcut shortcut)
        {
            var playlist = FindPlaylistById(shortcut.Key);
            if (playlist == null)
            {
                ShowShortcutGoneDialogAndRemove(shortcut);
                return;
            }
            NavigateMainFrame(typeof(PlaylistDetailPage), new PlaylistDetailArgs
            {
                Playlist = playlist,
                SaveChanges = () =>
                {
                    MusicDataCache.SavePlaylists();
                    // 歌单在详情页重命名后同步侧边栏名称/标题
                    SyncShortcutTitle(shortcut, playlist.Name);
                }
            });
        }

        /// <summary>音乐歌手：按歌手名过滤音乐库。</summary>
        private void OpenMusicArtistShortcut(SidebarShortcut shortcut)
        {
            var songs = LoadMusicItems()
                .Where(m => string.Equals(m.ArtistDisplay, shortcut.Key, StringComparison.OrdinalIgnoreCase) ||
                            (string.IsNullOrWhiteSpace(m.Artist) && shortcut.Key == "未知艺术家"))
                .ToList();
            if (songs.Count == 0)
            {
                HandleMusicEmptyResult(shortcut);
                return;
            }
            NavigateMainFrame(typeof(ArtistDetailPage), new ArtistDetailArgs
            {
                ArtistName = shortcut.Key,
                Songs = songs
            });
        }

        /// <summary>音乐专辑：按专辑名（+艺术家）过滤音乐库。</summary>
        private void OpenMusicAlbumShortcut(SidebarShortcut shortcut)
        {
            var songs = LoadMusicItems()
                .Where(m => string.Equals(m.AlbumDisplay, shortcut.Name, StringComparison.OrdinalIgnoreCase) ||
                            (string.IsNullOrWhiteSpace(m.Album) && shortcut.Name == "未知专辑"))
                .OrderBy(m => m.TrackNumber)
                .ToList();
            if (songs.Count == 0)
            {
                HandleMusicEmptyResult(shortcut);
                return;
            }
            NavigateMainFrame(typeof(AlbumDetailPage), new AlbumDetailArgs
            {
                AlbumName = shortcut.Name,
                Artist = shortcut.SubName,
                Songs = songs
            });
        }

        /// <summary>音乐文件夹：按文件夹路径过滤音乐库。</summary>
        private void OpenMusicFolderShortcut(SidebarShortcut shortcut)
        {
            var songs = LoadMusicItems()
                .Where(m => string.Equals(IO.Path.GetDirectoryName(m.FilePath), shortcut.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (songs.Count == 0)
            {
                HandleMusicEmptyResult(shortcut);
                return;
            }
            NavigateMainFrame(typeof(FolderDetailPage), new FolderDetailArgs
            {
                FolderPath = shortcut.Key,
                Songs = songs
            });
        }

        /// <summary>
        /// 处理音乐类快捷方式打开时结果为空的情况：
        /// 音乐库尚未加载（MusicPage 未初始化且磁盘缓存为空）时提示用户先打开音乐页，
        /// 否则视为内容已不存在，自动移除快捷方式。
        /// </summary>
        private async void HandleMusicEmptyResult(SidebarShortcut shortcut)
        {
            if (!MusicDataCache.IsInitialized)
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = "音乐库未加载",
                        Content = "音乐库数据尚未加载，请先打开“音乐”页面完成扫描后再试。",
                        CloseButtonText = "确定"
                    };
                    await DialogService.ShowAsync(dialog, RootGrid.XamlRoot);
                }
                catch { }
                return;
            }
            ShowShortcutGoneDialogAndRemove(shortcut);
        }

        /// <summary>视频文件夹：按文件夹路径过滤视频缓存。</summary>
        private void OpenVideoFolderShortcut(SidebarShortcut shortcut)
        {
            var videos = MediaScanner.LoadFromCache("Video");
            var allUnderFolder = videos
                .Where(v => v.FilePath.StartsWith(shortcut.Key + IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (allUnderFolder.Count == 0)
            {
                ShowShortcutGoneDialogAndRemove(shortcut);
                return;
            }
            NavigateMainFrame(typeof(VideoFolderDetailPage), new VideoFolderDetailArgs
            {
                FolderPath = shortcut.Key,
                Videos = allUnderFolder
            });
        }

        /// <summary>视频收藏夹：按收藏夹 Id 从 video_favorites.json 查找。</summary>
        private void OpenVideoFavoriteShortcut(SidebarShortcut shortcut)
        {
            var favorites = LoadFavoritesFromFile("video_favorites.json");
            var favorite = favorites.FirstOrDefault(f =>
                string.Equals(f.Id, shortcut.Key, StringComparison.OrdinalIgnoreCase));
            if (favorite == null)
            {
                ShowShortcutGoneDialogAndRemove(shortcut);
                return;
            }
            NavigateMainFrame(typeof(VideoFavoriteDetailPage), new VideoFavoriteDetailArgs
            {
                Favorite = favorite,
                SaveChanges = () =>
                {
                    SaveFavoritesToFile("video_favorites.json", favorites);
                    // 收藏夹在详情页重命名后同步侧边栏名称/标题
                    SyncShortcutTitle(shortcut, favorite.Name);
                }
            });
        }

        /// <summary>图库文件夹：按文件夹路径过滤图片缓存。</summary>
        private void OpenGalleryFolderShortcut(SidebarShortcut shortcut)
        {
            var images = MediaScanner.LoadFromCache("Image");
            var allUnderFolder = images
                .Where(v => v.FilePath.StartsWith(shortcut.Key + IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (allUnderFolder.Count == 0)
            {
                ShowShortcutGoneDialogAndRemove(shortcut);
                return;
            }
            NavigateMainFrame(typeof(GalleryFolderDetailPage), new GalleryFolderDetailArgs
            {
                FolderPath = shortcut.Key,
                Images = allUnderFolder
            });
        }

        /// <summary>图库相册：按相册 Id 从 gallery_favorites.json 查找。</summary>
        private void OpenGalleryAlbumShortcut(SidebarShortcut shortcut)
        {
            var favorites = LoadFavoritesFromFile("gallery_favorites.json");
            var favorite = favorites.FirstOrDefault(f =>
                string.Equals(f.Id, shortcut.Key, StringComparison.OrdinalIgnoreCase));
            if (favorite == null)
            {
                ShowShortcutGoneDialogAndRemove(shortcut);
                return;
            }
            NavigateMainFrame(typeof(GalleryAlbumDetailPage), new GalleryAlbumDetailArgs
            {
                Favorite = favorite,
                SaveChanges = () =>
                {
                    SaveFavoritesToFile("gallery_favorites.json", favorites);
                    // 相册在详情页重命名后同步侧边栏名称/标题
                    SyncShortcutTitle(shortcut, favorite.Name);
                }
            });
        }

        /// <summary>
        /// 打开图片查看器（通过覆盖层 Overlay）。
        /// 供 GalleryPage/HomePage 调用。
        /// </summary>
        public void OpenImageViewer(ImageViewerArgs args)
        {
            ShowPlayerOverlay(typeof(ImageViewerPage), args);
        }

        /// <summary>
        /// 恢复主窗口标题栏默认拖拽区域。
        /// 图片查看器退出时调用（图片查看器激活期间会用自身的精确拖拽矩形替换此区域）。
        /// </summary>
        public void RestoreTitleBarDragRegions()
        {
            SetTitleBarDragRegions();
        }

        private IntPtr WindowSubclassProc(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr subclassId,
            IntPtr referenceData)
        {
            // 根据 hit-test 码设置对应光标：窗口边缘显示调整尺寸光标，其余区域为箭头
            if (message == 0x0020) // WM_SETCURSOR
            {
                int hitTest = (int)(lParam.ToInt64() & 0xFFFF);
                int cursorId;
                switch (hitTest)
                {
                    case HTLEFT:
                    case HTRIGHT:
                        cursorId = IDC_SIZEWE;
                        break;
                    case HTTOP:
                    case HTBOTTOM:
                        cursorId = IDC_SIZENS;
                        break;
                    case HTTOPLEFT:
                    case HTBOTTOMRIGHT:
                        cursorId = IDC_SIZENWSE;
                        break;
                    case HTTOPRIGHT:
                    case HTBOTTOMLEFT:
                        cursorId = IDC_SIZENESW;
                        break;
                    default:
                        cursorId = IDC_ARROW;
                        break;
                }
                SetCursor(LoadCursor(IntPtr.Zero, cursorId));
                return (IntPtr)1;
            }

            return DefSubclassProc(hWnd, message, wParam, lParam);
        }

        /// <summary>
        /// 显示 XAML 启动画面遮罩层。在 Activate() 之前调用，确保首帧可见。
        /// </summary>
        /// <param name="themeMode">"Dark" = 深色，"Light" = 浅色。</param>
        public void ShowSplash(string themeMode)
        {
            if (SplashOverlay != null)
            {
                bool isDark = themeMode?.Equals("Dark", StringComparison.OrdinalIgnoreCase) == true;
                var bgColor = isDark ?
                    Windows.UI.Color.FromArgb(255, 44, 44, 44) :
                    Windows.UI.Color.FromArgb(255, 232, 232, 232);
                SplashOverlay.Background = new SolidColorBrush(bgColor);
                SplashOverlay.Visibility = Visibility.Visible;
                SplashOverlay.Opacity = 1.0;

                // ── 自适应缩放：根据当前窗口大小调整图标尺寸 ──
                UpdateSplashLogoSize();

                // ── 强制隐藏窗口 ──
                // WinUI 框架可能会在创建窗口后自动显示它。
                // 我们要确保在准备好之前窗口绝对不可见。
                try
                {
                    _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    ShowWindow(_hwnd, SW_HIDE);
                }
                catch { }
            }
        }

        /// <summary>
        /// 根据窗口当前大小自适应调整 Splash 图标尺寸。
        /// 窗口大时图标放大，窗口小时图标缩小。
        /// </summary>
        private void UpdateSplashLogoSize()
        {
            if (SplashLogo == null || SplashOverlay == null) return;

            // 基准尺寸（缩小30%后）：154x154，参考窗口宽度 1920px
            const double baseSize = 154.0;
            const double referenceWidth = 1920.0;

            double currentWidth = SplashOverlay.ActualWidth;
            if (currentWidth <= 0) currentWidth = 1920.0;

            // 按窗口宽度比例缩放
            double scale = currentWidth / referenceWidth;
            double newSize = baseSize * scale;

            // 限制范围：最小 80px，最大 300px
            newSize = Math.Max(80.0, Math.Min(300.0, newSize));

            SplashLogo.Width = newSize;
            SplashLogo.Height = newSize;
        }

        /// <summary>
        /// Splash 遮罩层尺寸变化时触发，自适应调整图标大小。
        /// </summary>
        private void SplashOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSplashLogoSize();
        }

        /// <summary>
        /// 在最合适的时机显示窗口（在 Compositor 就绪之后调用）。
        /// 窗口出现后才启动 splash 停留计时器。
        /// </summary>
        /// <param name="splashDisplayMs">splash 停留毫秒数，默认 500。</param>
        public void ShowWindowNow(int splashDisplayMs = 500)
        {
            // 获取 HWND（如果还没获取）
            if (_hwnd == IntPtr.Zero)
            {
                try { _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this); }
                catch { }
            }

            // 显示窗口（首次出现）
            if (_hwnd != IntPtr.Zero)
                ShowWindow(_hwnd, SW_SHOW);
            Activate();

            // ── 窗口已出现，启动 splash 停留计时 ──
            // 用后台线程等 Delay，再切回 UI 线程渐隐 splash。
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _ = Task.Run(async () =>
            {
                await Task.Delay(splashDisplayMs);
                dq.TryEnqueue(() => _ = HideSplashAsync(250));
            });
        }

        /// <summary>
        /// 渐隐并隐藏启动画面遮罩层。
        /// </summary>
        /// <param name="fadeDurationMs">渐隐持续时间（毫秒）。</param>
        public async Task HideSplashAsync(int fadeDurationMs = 250)
        {
            if (SplashOverlay == null || SplashOverlay.Visibility != Visibility.Visible)
                return;

            // 使用 Composition API 的 ScalarAnimation 做平滑渐隐
            var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SplashOverlay).Compositor;
            var fadeAnimation = compositor.CreateScalarKeyFrameAnimation();
            fadeAnimation.InsertKeyFrame(1.0f, 0.0f);
            fadeAnimation.Duration = TimeSpan.FromMilliseconds(fadeDurationMs);

            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SplashOverlay);
            visual.StartAnimation("Opacity", fadeAnimation);

            await Task.Delay(fadeDurationMs);
            SplashOverlay.Visibility = Visibility.Collapsed;
            SplashOverlay.Opacity = 1.0; // 复位供后续使用

            // 通知文件激活服务等延迟操作：splash 已隐藏，可以进行导航了
            SplashHidden?.Invoke(this, EventArgs.Empty);
        }

        public void ApplyTheme(string mode)
        {
            if (Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = mode switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }
            ApplyBackdrop(App.SettingsHelper.BackdropType);
            ApplyMiniPlayerBackdrop();

            // 播放器覆盖层激活时保持按钮为白色，不跟随主题变化
            if (_isPlayerOverlayActive)
            {
                ApplyPlayerCaptionButtonColors();
            }
            else
            {
                RestoreCaptionButtonColors();

                // 立即同步系统标题栏按钮颜色
                var currentTheme = mode switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => Content is FrameworkElement root ? root.ActualTheme : ElementTheme.Default
                };
                TitleBarHelper.ApplySystemThemeToCaptionButtons(this, currentTheme);
            }

            // QueueButton 在 MiniPlayer 内（x:Load="False"），未加载时为 null
            if (QueueButton?.Flyout != null)
                BuildQueueFlyout();

            AppLogger.Debug($"应用主题模式: {mode}");
        }

        public void ApplyBackdrop(string type)
        {
            bool keepContentMica = App.SettingsHelper.KeepContentMica;
            switch (type)
            {
                case "Acrylic":
                    SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
                    break;
                case "None":
                    SystemBackdrop = null;
                    break;
                case "Mica":
                default:
                    SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                    break;
            }
            ApplyRegionalBackdrop(type);
            ContentMicaLayer.Background = keepContentMica
                ? CreateMicaSurfaceBrush()
                : new SolidColorBrush(Colors.Transparent);
            AppLogger.Debug($"应用背景效果: {type}");
        }

        private void ApplyRegionalBackdrop(string type)
        {
            Brush brush = type switch
            {
                "None" => new SolidColorBrush(
                    IsDarkTheme()
                        ? ColorHelper.FromArgb(255, 32, 32, 32)
                        : ColorHelper.FromArgb(255, 243, 243, 243)),
                _ => new SolidColorBrush(Colors.Transparent)
            };

            // 紧凑模式下不覆盖背景（由 UpdateSidebarPaneBackground 独立管理）
            if (MainNavigationView?.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftCompact)
                return;

            if (_navigationSplitView != null)
                _navigationSplitView.PaneBackground = brush;
            SetElementBackground(_navigationPaneBackground, brush);
        }

        /// <summary>
        /// 更新侧边栏面板背景：紧凑模式下使用不透明背景（悬浮在内容上方时需要）。
        /// 展开模式下恢复为透明（由 ApplyRegionalBackdrop 管理）。
        /// </summary>
        private void UpdateSidebarPaneBackground()
        {
            if (_navigationPaneBackground == null) return;

            bool isCompactMode = MainNavigationView?.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftCompact;
            if (isCompactMode)
            {
                // 紧凑模式：使用不透明背景，确保悬浮时内容不透出
                bool isDark = IsDarkTheme();
                var compactBackground = new SolidColorBrush(
                    isDark ? ColorHelper.FromArgb(245, 32, 32, 32) : ColorHelper.FromArgb(245, 249, 249, 249));

                if (_navigationSplitView != null)
                    _navigationSplitView.PaneBackground = compactBackground;
                SetElementBackground(_navigationPaneBackground, compactBackground);
            }
            else
            {
                // 展开模式：清除紧凑模式设置的不透明背景，恢复为透明
                // （真正的背景由 ApplyRegionalBackdrop / ApplyBackdrop 管理）
                if (_navigationSplitView != null)
                    _navigationSplitView.PaneBackground = new SolidColorBrush(Colors.Transparent);
                SetElementBackground(_navigationPaneBackground, new SolidColorBrush(Colors.Transparent));
            }
        }

        private Brush CreateMicaSurfaceBrush()
        {
            return new SolidColorBrush(
                IsDarkTheme()
                    ? ColorHelper.FromArgb(255, 39, 39, 39)   // #272727 - 接近真云母深色
                    : ColorHelper.FromArgb(255, 249, 249, 249)); // #F9F9F9 - 浅色不变
        }



        private void ApplyMiniPlayerBackdrop()
        {
            // MiniPlayer / MiniPlayerRestoreButton 是 x:Load="False" 的延迟元素，
            // 调用此方法时可能尚未加载。使用 FindName 惰性访问，避免提前触发加载。
            if (RootGrid.FindName("MiniPlayer") is not Border miniPlayer) return;

            bool isDark = Content is FrameworkElement fe && fe.ActualTheme == ElementTheme.Dark;
            Windows.UI.Color baseColor = isDark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                : Windows.UI.Color.FromArgb(255, 250, 250, 250);

            // 直接使用亚克力材质
            miniPlayer.Background = new AcrylicBrush
            {
                TintColor = baseColor,
                TintOpacity = isDark ? 0.58 : 0.72,
                TintLuminosityOpacity = isDark ? 0.55 : 0.82,
                FallbackColor = baseColor
            };

            if (RootGrid.FindName("MiniPlayerRestoreButton") is Border restoreButton)
            {
                if (restoreButton.Background is AcrylicBrush existingAcrylic)
                {
                    existingAcrylic.TintColor = baseColor;
                    existingAcrylic.FallbackColor = baseColor;
                }
                else
                {
                    var acrylicBrush = new Microsoft.UI.Xaml.Media.AcrylicBrush
                    {
                        TintColor = baseColor,
                        TintOpacity = isDark ? 0.58 : 0.72,
                        TintLuminosityOpacity = isDark ? 0.55 : 0.82,
                        FallbackColor = baseColor
                    };
                    restoreButton.Background = acrylicBrush;
                }
            }
        }

        private static FrameworkElement? FindNamedElement(
            DependencyObject parent,
            string name)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is FrameworkElement element && element.Name == name)
                    return element;

                FrameworkElement? result = FindNamedElement(child, name);
                if (result != null)
                    return result;
            }

            return null;
        }

        private static T? FindDescendant<T>(DependencyObject parent)
            where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match)
                    return match;

                T? result = FindDescendant<T>(child);
                if (result != null)
                    return result;
            }

            return null;
        }

        private static void SetElementBackground(
            FrameworkElement? element,
            Brush brush)
        {
            switch (element)
            {
                case Panel panel:
                    panel.Background = brush;
                    break;
                case Border border:
                    border.Background = brush;
                    break;
                case Control control:
                    control.Background = brush;
                    break;
            }
        }

        private bool IsDarkTheme()
        {
            if (_isClosed)
                return false;

            try
            {
                return Content is FrameworkElement root &&
                    (root.RequestedTheme == ElementTheme.Dark ||
                     root.RequestedTheme == ElementTheme.Default &&
                     root.ActualTheme == ElementTheme.Dark);
            }
            catch (COMException)
            {
                return false;
            }
        }

        // ===================================================================================
        // 播放器覆盖层（Overlay）系统
        // 取代旧的 NavigateMainFrame + EnterPlayerFullScreen/ExitPlayerFullScreen 模式。
        // 全屏播放器（MusicPlayerPage/VideoPlayerPage/ImageViewerPage）不再在 ContentFrame 中
        // 导航，而是作为覆盖层叠加在窗口最上方，配合 Composition API 实现从下方滑入的平滑动画。
        // ===================================================================================

        /// <summary>
        /// 判断页面类型是否为播放器类页面（走覆盖层模式）。
        /// </summary>
        private static bool IsPlayerPageType(Type pageType)
        {
            return pageType == typeof(MusicPlayerPage)
                || pageType == typeof(VideoPlayerPage)
                || pageType == typeof(ImageViewerPage);
        }

        /// <summary>
        /// 显示播放器覆盖层，支持动画/无动画模式。
        /// </summary>
        /// <param name="pageType">播放器页面类型（MusicPlayerPage / VideoPlayerPage / ImageViewerPage）</param>
        /// <param name="parameter">导航参数</param>
        /// <param name="animate">是否使用从底部滑入的动画。启动时为 false（直接定位到 Y=0，由 Splash 覆盖过渡）</param>
        public void ShowPlayerOverlay(Type pageType, object? parameter, bool animate = true)
        {
            if (_isClosed)
                return;

            if (_isPlayerOverlayActive)
            {
                // 覆盖层已显示：直接导航到新页面（切换歌曲/视频时不重复动画）
                AppLogger.Info($"[Overlay] 覆盖层已激活，导航到: {pageType.Name}");
                PlayerFrame.Navigate(pageType, parameter);
                return;
            }

            AppLogger.Info($"[Overlay] 显示播放器覆盖层: {pageType.Name}, animate={animate}, 当前内容={ContentFrame.Content?.GetType().Name ?? "null"}");
            _isPlayerOverlayActive = true;

            // ★ 资源诊断：进入播放器前输出一次完整快照，对比"浏览页面后"的资源累积
            ResourceDiagnosticsService.LogSnapshot($"打开播放器 {pageType.Name}（进入前）");

            // ★ 修复：打开播放器（Win2D 渲染）前主动清理浏览页面累积的残留资源。
            //   页面/控件的 Win2D 非托管 GPU 资源（CanvasBitmap、CanvasTextLayout、
            //   PixelShaderEffect 等）依赖终结器释放；仅做非阻塞 GC 不会等待终结器，
            //   显存不会归还。此处"回收 → 等待终结器 → 再回收"彻底清干净，
            //   避免"浏览很多页面后打开播放器渲染卡顿、私有内存持续膨胀"。
            ForceCollectGarbageBeforePlayer();

            // 播放器背景均为深色，将标题栏按钮设为白色
            ApplyPlayerCaptionButtonColors();

            // ① 让覆盖层可见
            PlayerOverlay.Visibility = Visibility.Visible;
            EnsureCompositor();
            SuspendMainContentForPlayer();
            PauseMiniPlayerForOverlay();

            if (animate)
            {
                // 动画立即启动，让点击后马上有视觉反馈；页面内容随后渐进加载。
                double windowHeight = _appWindow.Size.Height;
                if (windowHeight <= 0)
                    windowHeight = RootGrid.ActualHeight;
                _overlayVisual!.Offset = new Vector3(0, (float)windowHeight, 0);
                AnimateOverlaySlideIn();
            }
            else
            {
                // ③ 无动画模式（启动时使用）：直接定位到正常位置
                //    此时播放器被 SplashOverlay 覆盖，用户不可见。
                //    Splash 渐隐后播放器自然露出。
                _overlayVisual!.Offset = new Vector3(0, 0, 0);
            }

            // 导航到播放器页面
            PlayerFrame.Navigate(pageType, parameter);

            // ★ 关键修复：必须在 Navigate 之后清空 BackStack。
            //   Frame.Content 被置 null 后，Frame.CurrentSourcePageType 不会重置，
            //   仍残留上一次的页面类型。再次 Navigate 新页面时，Frame 会把该残留类型
            //   当作"当前页"压入 BackStack（幽灵条目）——这正是"第一次打开的播放器
            //   成为永久返回残留"的根源（无论后来打开视频/看图器，返回都会回到它）。
            //   在 Navigate 之后 Clear，能把这一幽灵条目连同历史一并清除，
            //   使本次打开的页面成为干净的"第一页"（CanGoBack=false）。
            //   注意：覆盖层内部导航（播放器 → 看图器）走的是上面的"覆盖层已激活"
            //   分支，不会执行到这里，因此不影响播放器内多级返回。
            PlayerFrame.BackStack.Clear();
        }

        /// <summary>
        /// ★ 修复：打开 Win2D 播放器前的资源整理。
        /// Win2D 非托管 GPU 资源（CanvasBitmap、CanvasTextLayout 等）通过终结器归还显存，
        /// 非阻塞 GC 不等待终结器，导致浏览页面累积的 GPU 资源长期滞留、私有内存膨胀。
        /// 但阻塞式回收（WaitForPendingFinalizers 可能耗时数百毫秒）若与播放器滑入动画
        /// 同时发生，会阻塞 UI 线程造成"抽搐"（DeepSeek P2 结论）。
        /// 因此此处仅做毫秒级的轻量缓存裁剪（同步），重量级 GC 调度到动画完成后的
        /// 空闲期（后台线程低优先级执行），两全其美。
        /// </summary>
        private void ForceCollectGarbageBeforePlayer()
        {
            try
            {
                // 轻量同步：裁剪缩略图/封面缓存（仅移除引用，毫秒级，不阻塞动画）
                ImageThumbnailService.TrimMemoryCache(256);
                MusicCoverService.ClearCache();
                NetworkLyricsService.ClearCache();
                AppLogger.Info($"[GC] 打开播放器前缓存裁剪完成, 托管堆≈{GC.GetTotalMemory(false) / 1024 / 1024}MB");

                // 重量级回收：延迟到滑入动画 + 播放器首帧渲染完成后的空闲期
                ScheduleIdleGarbageCollect();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "打开播放器前整理资源");
            }
        }

        /// <summary>
        /// 调度"空闲期"重量级 GC：等滑入动画（300ms）与播放器首帧渲染错开，
        /// 在后台线程执行 回收 → 等待终结器 → 再回收，让 Win2D GPU 显存归还。
        /// 带防重入标志，多次打开播放器只会保留一次调度。
        /// </summary>
        private volatile bool _playerGcScheduled;
        private void ScheduleIdleGarbageCollect()
        {
            if (_playerGcScheduled)
                return;
            _playerGcScheduled = true;

            // 后台延迟 800ms：避开滑入动画（300ms）与播放器首帧渲染的 CPU/GPU 争抢
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(800);
                    if (_isClosed)
                        return;

                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
                    AppLogger.Info($"[GC] 播放器空闲期回收完成, 托管堆≈{GC.GetTotalMemory(false) / 1024 / 1024}MB");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "播放器空闲期回收资源");
                }
                finally
                {
                    _playerGcScheduled = false;
                }
            });
        }

        /// <summary>
        /// Frame 导航完成 → 获取页面实例 → 等待页面 Loaded 事件。
        /// </summary>
        /// <summary>
        /// 隐藏播放器覆盖层（带向下滑出动画）。
        /// 滑出完成后自动清理页面资源。
        /// </summary>
        public void HidePlayerOverlay()
        {
            if (_isClosed || !_isPlayerOverlayActive)
                return;

            AppLogger.Info("[Overlay] 隐藏播放器覆盖层");
            CancelPlayerCleanup();
            AnimateOverlaySlideOut();
        }

        /// <summary>
        /// 导航到设置页，可指定直达的子页面类型（如 <see cref="VideoSettingsPage"/>）。
        /// 供播放器内"视频设置"等超链接跳转使用：先关闭播放器覆盖层，再打开设置页。
        /// </summary>
        /// <param name="targetSubPage">设置页子页面类型；null 时停留在设置首页。</param>
        public void NavigateToSettings(Type? targetSubPage = null)
        {
            if (_isClosed)
                return;

            AppLogger.Info($"[导航] 跳转到设置页{(targetSubPage != null ? "（直达 " + targetSubPage.Name + "）" : string.Empty)}");
            ContentFrame.Navigate(typeof(SettingsPage), targetSubPage,
                new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }

        private void AnimateOverlaySlideOut()
        {
            EnsureCompositor();

            double endY = _appWindow.Size.Height;
            if (endY <= 0)
                endY = RootGrid.ActualHeight;
            double startY = _overlayVisual?.Offset.Y ?? 0;

            // 如果已经在下方的终点附近，直接隐藏
            if (startY >= endY - 1)
            {
                PlayerOverlay.Visibility = Visibility.Collapsed;
                _isPlayerOverlayActive = false;
                PlayerFrame.Content = null;
                // ★ 清空导航栈，避免残留条目导致下次打开播放器返回误判
                PlayerFrame.BackStack.Clear();
                ResumeMainContentAfterPlayer();
                ResumeMiniPlayerAfterOverlay();
                RefreshHomePageAfterOverlay();
                AppLogger.Info("[Overlay] 滑出动画跳过（已在终点）");
                RestoreCaptionButtonColors();
                RefreshWin2DHudSurfaceState();
                return;
            }

            // 匀速直线动画，与滑入一致，没有缓动曲线
            var animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0, (float)startY);
            animation.InsertKeyFrame(1, (float)endY);
            animation.Duration = TimeSpan.FromMilliseconds(300);

            // 使用 ScopedBatch 监听动画完成
            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (_, _) =>
            {
                // 滑出完成后清理
                PlayerOverlay.Visibility = Visibility.Collapsed;
                _isPlayerOverlayActive = false;

                AppLogger.Info("[Overlay] 滑出动画完成，覆盖层已隐藏");
                RefreshWin2DHudSurfaceState();
                // ★ 资源诊断：退出播放器后输出快照，观察页面卸载后资源是否释放
                ResourceDiagnosticsService.LogSnapshot("退出播放器（覆盖层隐藏后）");

                // 卸载页面触发页面的 Unloaded 事件（释放 Win2D/GPU 资源）
                PlayerFrame.Content = null;
                // ★ 清空导航栈，避免残留条目导致下次打开播放器返回误判
                PlayerFrame.BackStack.Clear();
                ResumeMainContentAfterPlayer();
                ResumeMiniPlayerAfterOverlay();
                // ★ 覆盖层退出不会触发主页 OnNavigatedTo，主动刷新主页"上次打开"大卡片
                RefreshHomePageAfterOverlay();

                // ★ 如果 ContentFrame 为空（启动时通过打开方式直接进入了播放器，
                //    没有导航过主页），则退出播放器后回退到主页，
                //    避免用户看到空白的内容区域。
                if (ContentFrame.Content == null)
                {
                    ContentFrame.Navigate(typeof(HomePage), null, new SuppressNavigationTransitionInfo());
                    MainNavigationView.SelectedItem = MainNavigationView.MenuItems[0];
                    AppLogger.Info("[Overlay] ContentFrame 为空，导航到 HomePage");
                }

                // 恢复标题栏按钮颜色为当前主题色
                RestoreCaptionButtonColors();
            };

            _overlayVisual!.StartAnimation("Offset.Y", animation);
            batch.End();
        }

        /// <summary>
        /// 从底部滑入动画：PlayerOverlay 从窗口高度下方滑到正常位置。
        /// 使用 CompositionAPI 的 ScalarKeyFrameAnimation，纯 GPU 执行，不触发 XAML 布局。
        /// </summary>
        private void AnimateOverlaySlideIn()
        {
            EnsureCompositor();

            double startY = _overlayVisual!.Offset.Y;
            double endY = 0;

            // 如果起点已经是 0（位置异常），不执行动画
            if (startY <= 1)
            {
                return;
            }

            AppLogger.Debug($"[Overlay] 滑入动画: startY={startY}, windowHeight={_appWindow.Size.Height}");

            // 匀速直线动画，没有缓动曲线，全程恒定速度
            var animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0, (float)startY);
            animation.InsertKeyFrame(1, (float)endY);
            animation.Duration = TimeSpan.FromMilliseconds(300);

            _overlayVisual.StartAnimation("Offset.Y", animation);
        }

        /// <summary>
        /// 确保 Composition 相关资源已初始化。
        /// </summary>
        private void EnsureCompositor()
        {
            if (_compositor == null)
            {
                _compositor = ElementCompositionPreview.GetElementVisual(RootGrid).Compositor;
            }
            if (_overlayVisual == null)
            {
                _overlayVisual = ElementCompositionPreview.GetElementVisual(PlayerOverlay);
            }
        }

        /// <summary>
        /// 播放器覆盖层打开时暂停主内容树，停止当前页面的缩略图加载、布局和后台刷新，
        /// 避免它们与歌词 Canvas 共用 UI 线程和 GPU。
        /// </summary>
        private void SuspendMainContentForPlayer()
        {
            if (_contentSuspendedForPlayer)
                return;

            _contentSuspendedForPlayer = true;
            ContentFrame.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 播放器关闭后恢复主内容树，页面会按现有生命周期重新加载数据。
        /// </summary>
        private void ResumeMainContentAfterPlayer()
        {
            if (!_contentSuspendedForPlayer)
                return;

            _contentSuspendedForPlayer = false;
            ContentFrame.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 播放器覆盖层关闭后刷新主页（若主页当前可见）。
        /// 主页一直存活在 ContentFrame 中，覆盖层退出不会触发其 OnNavigatedTo，
        /// 因此需要主动调用，让"上次打开"大卡片即时反映播放器/看图器中的最新记录。
        /// 若 ContentFrame 为空（启动即打开播放器），后续导航到主页时会由 OnNavigatedTo 刷新。
        /// </summary>
        private void RefreshHomePageAfterOverlay()
        {
            if (ContentFrame?.Content is HomePage homePage)
                homePage.RefreshAfterPlayerOverlayClosed();
        }

        private void PauseMiniPlayerForOverlay()
        {
            if (_miniPlayerPausedForOverlay)
                return;

            _miniPlayerPausedForOverlay = true;
            _miniPlayerTimerWasRunningBeforeOverlay = _miniPlayerTimer.IsEnabled;
            _equalizerWasRunningBeforeOverlay = _equalizerRunning;
            _miniPlayerTimer.Stop();
            StopEqualizerAnimation();
        }

        private void ResumeMiniPlayerAfterOverlay()
        {
            if (!_miniPlayerPausedForOverlay)
                return;

            _miniPlayerPausedForOverlay = false;
            if (_miniPlayerTimerWasRunningBeforeOverlay &&
                !_isMiniPlayerManuallyHidden &&
                _playback.ActiveItem != null)
            {
                _miniPlayerTimer.Start();
            }

            if (_equalizerWasRunningBeforeOverlay &&
                !_isMiniPlayerManuallyHidden &&
                _playback.PlaybackState == MediaPlaybackState.Playing)
            {
                StartEqualizerAnimation();
            }

            _miniPlayerTimerWasRunningBeforeOverlay = false;
            _equalizerWasRunningBeforeOverlay = false;
        }

        /// <summary>
        /// PlayerFrame 导航事件处理。
        /// 拦截 Frame.GoBack() 操作（用户点击播放器页面的返回按钮），
        /// 改为隐藏覆盖层而非真正的导航回退。
        /// ★ 例外：当覆盖层内还有上一页（如"音乐播放器 → 图片查看器"）时放行真实返回，
        ///   使返回回到上一页而不是直接关闭整个覆盖层（回到音乐库主页）。
        /// </summary>
        private void PlayerFrame_Navigating(object sender, NavigatingCancelEventArgs e)
        {
            // 只有覆盖层激活时才拦截
            if (!_isPlayerOverlayActive)
                return;

            // 用户点击了页面中的返回按钮 → Frame.GoBack()
            if (e.NavigationMode == NavigationMode.Back)
            {
                // 覆盖层内还有上一页：放行真实返回（PlayerFrame.GoBack 会从 BackStack 恢复）
                if (PlayerFrame.CanGoBack)
                    return;

                e.Cancel = true; // 取消实际导航
                HidePlayerOverlay(); // 改为滑出覆盖层
            }

            // 其他导航模式（如新页面导航）正常放行
        }


        public void EnterPlayerFullScreen()
        {
            if (_isClosed)
                return;
            // 覆盖层激活时，由覆盖层提供全屏效果，无需此处布局切换
            if (_isPlayerOverlayActive)
                return;
            
            AppLogger.Info("[MainWindow] 进入播放器全屏布局");
            CapsulePopup.IsOpen = false;
            StopEqualizerAnimation();
            if (!_isMiniPlayerManuallyHidden && _playback.ActiveItem != null)
            {
                MiniPlayer.Visibility = Visibility.Visible;
                _miniPlayerTimer.Start();
                AppLogger.Debug("[MainWindow] 全屏布局：迷你播放器保持可见");
            }
            else
            {
                MiniPlayer.Visibility = Visibility.Collapsed;
                _miniPlayerTimer.Stop();
                AppLogger.Debug("[MainWindow] 全屏布局：迷你播放器隐藏");
            }
            if (ContentHostGrid.Children.Contains(MiniPlayer))
                ContentHostGrid.Children.Remove(MiniPlayer);
            if (!RootGrid.Children.Contains(MiniPlayer))
            {
                RootGrid.Children.Add(MiniPlayer);
                Grid.SetRow(MiniPlayer, 1);
            }
            ApplyPlayerCaptionButtonColors();
            MainNavigationView.Visibility = Visibility.Collapsed;

            MainNavigationView.Content = null;
            ContentHostGrid.Children.Remove(ContentFrame);
            if (!RootGrid.Children.Contains(ContentFrame))
            {
                RootGrid.Children.Add(ContentFrame);
                Grid.SetRow(ContentFrame, 0);
                Grid.SetRowSpan(ContentFrame, 2);
            }
        }

        public void ExitPlayerFullScreen()
        {
            if (_isClosed)
                return;
            // 覆盖层激活时，由覆盖层管理退出，无需此处布局恢复
            if (_isPlayerOverlayActive)
                return;
            
            AppLogger.Info("[MainWindow] 退出播放器全屏布局");
            RestoreCaptionButtonColors();
            if (RootGrid.Children.Contains(ContentFrame))
            {
                RootGrid.Children.Remove(ContentFrame);
            }
            if (!ContentHostGrid.Children.Contains(ContentFrame))
                ContentHostGrid.Children.Add(ContentFrame);
            if (RootGrid.Children.Contains(MiniPlayer))
                RootGrid.Children.Remove(MiniPlayer);
            if (!ContentHostGrid.Children.Contains(MiniPlayer))
                ContentHostGrid.Children.Add(MiniPlayer);
            MainNavigationView.Content = ContentHostGrid;

            MainNavigationView.Visibility = Visibility.Visible;

            if (_isMiniPlayerManuallyHidden && _playback.ActiveItem != null)
            {
                CapsulePopup.IsOpen = true;
                PositionCapsule();
                if (_playback.PlaybackState == MediaPlaybackState.Playing)
                    StartEqualizerAnimation();
            }

            AppLogger.Debug("[MainWindow] 退出全屏布局完成，更新迷你播放器可见性");
            UpdateMiniPlayerVisibility();
        }

        private void ApplyPlayerCaptionButtonColors()
        {
            if (_isClosed)
                return;
            AppWindowTitleBar titleBar = _appWindow.TitleBar;
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(
                0x99, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(
                0x33, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(
                0x55, 0xFF, 0xFF, 0xFF);
        }

        private void RestoreCaptionButtonColors()
        {
            if (_isClosed)
                return;
            bool useLightButtons = IsDarkTheme();
            Windows.UI.Color foreground =
                useLightButtons ? Colors.White : Colors.Black;

            AppWindowTitleBar titleBar = _appWindow.TitleBar;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(
                0x99,
                foreground.R,
                foreground.G,
                foreground.B);
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(
                0x18,
                foreground.R,
                foreground.G,
                foreground.B);
            titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(
                0x2A,
                foreground.R,
                foreground.G,
                foreground.B);
        }

        // ================== 全局迷你播放器 ==================

        private void UpdateMiniPlayerVisibility()
        {
            if (_isMiniPlayerManuallyHidden)
            {
                AppLogger.Debug("[MiniPlayer] UpdateMiniPlayerVisibility: 用户手动隐藏中，跳过更新");
                return;
            }

            if (ContentFrame.Parent == RootGrid)
            {
                AppLogger.Debug("[MiniPlayer] UpdateMiniPlayerVisibility: ContentFrame 仍在 RootGrid（全屏模式），跳过更新");
                return;
            }

            bool hasContent = _playback.ActiveItem != null;

            var currentPage = ContentFrame.Content?.GetType();
            // 是否需要在图库页/查看器隐藏迷你播放器（可在图库设置中关闭）
            bool isGalleryPage = (currentPage == typeof(GalleryPage)
                               || currentPage == typeof(ImageViewerPage))
                              && App.SettingsHelper.GalleryHideMiniPlayerOnEnter;

            if (hasContent && !isGalleryPage)
            {
                MiniPlayerHost.SetMediaPlayer(_playback.ActivePlayer);
                MiniPlayer.Visibility = Visibility.Visible;
                _miniPlayerTimer.Start();
                ApplyMiniPlayerBackdrop();
                AttachQueueFlyout();
                CapsulePopup.IsOpen = false;
                StopEqualizerAnimation();
                AppLogger.Info($"[MiniPlayer] 迷你播放器显示: 文件={_playback.ActiveItem?.FileName}, 当前页面={currentPage?.Name ?? "null"}, 状态={_playback.PlaybackState}");
            }
            else if (hasContent && isGalleryPage)
            {
                MiniPlayer.Visibility = Visibility.Collapsed;
                _miniPlayerTimer.Stop();
                CapsulePopup.IsOpen = true;
                PositionCapsule();
                if (_playback.PlaybackState == MediaPlaybackState.Playing)
                    StartEqualizerAnimation();
                AppLogger.Info($"[MiniPlayer] 迷你播放器隐藏（画廊页面）: 文件={_playback.ActiveItem?.FileName}, 当前页面={currentPage?.Name}");
            }
            else
            {
                MiniPlayer.Visibility = Visibility.Collapsed;
                _miniPlayerTimer.Stop();
                CapsulePopup.IsOpen = false;
                StopEqualizerAnimation();
                AppLogger.Info("[MiniPlayer] 迷你播放器隐藏（无内容）");
            }
        }

        private void ShowMiniPlayer()
        {
            if (_playback.ActiveItem != null && ContentFrame.Parent != RootGrid)
            {
                MiniPlayerHost.SetMediaPlayer(_playback.ActivePlayer);
                MiniPlayer.Visibility = Visibility.Visible;
                _miniPlayerTimer.Start();
                ApplyMiniPlayerBackdrop();
                AttachQueueFlyout();
                CapsulePopup.IsOpen = false;
                _isMiniPlayerManuallyHidden = false;
                StopEqualizerAnimation();
                AppLogger.Info($"[MiniPlayer] ShowMiniPlayer: 文件={_playback.ActiveItem?.FileName}, 状态={_playback.PlaybackState}");
            }
            else
            {
                AppLogger.Debug($"[MiniPlayer] ShowMiniPlayer: 未执行, ActiveItem={_playback.ActiveItem?.FileName}, ContentFrame.Parent={(ContentFrame.Parent?.GetType().Name ?? "null")}");
            }
        }

        private void HideMiniPlayer()
        {
            MiniPlayer.Visibility = Visibility.Collapsed;
            _miniPlayerTimer.Stop();
            CapsulePopup.IsOpen = true;
            PositionCapsule();
            _isMiniPlayerManuallyHidden = true;
            if (_playback.PlaybackState == MediaPlaybackState.Playing)
            {
                StartEqualizerAnimation();
            }
            AppLogger.Info($"[MiniPlayer] HideMiniPlayer: 文件={_playback.ActiveItem?.FileName}, 状态={_playback.PlaybackState}");
        }

        private void InitBarVisuals()
        {
            if (_barVisuals[0] != null) return;
            _barVisuals[0] = ElementCompositionPreview.GetElementVisual(Bar1);
            _barVisuals[1] = ElementCompositionPreview.GetElementVisual(Bar2);
            _barVisuals[2] = ElementCompositionPreview.GetElementVisual(Bar3);
            _barVisuals[3] = ElementCompositionPreview.GetElementVisual(Bar4);
            _barVisuals[4] = ElementCompositionPreview.GetElementVisual(Bar5);
            foreach (var v in _barVisuals)
                v.CenterPoint = new Vector3(1, 5, 0);
        }

        private void StartEqualizerAnimation()
        {
            if (_equalizerRunning) return;
            InitBarVisuals();
            _equalizerRunning = true;
            _equalizerStopwatch.Restart();
            CompositionTarget.Rendering += OnEqualizerFrame;
            ResourceDiagnosticsService.RegisterRenderingHandler(); // ★ 诊断
        }

        private void OnEqualizerFrame(object? sender, object e)
        {
            if (!_equalizerRunning) return;
            var elapsed = _equalizerStopwatch.Elapsed.TotalSeconds;
            for (int i = 0; i < 5; i++)
            {
                float distance = Math.Abs(i - 2);
                double phase = elapsed * Math.PI * 2 - distance * 0.8;
                float scaleY = (float)(0.3 + (Math.Sin(phase) + 1) * 0.35);
                _barVisuals[i].Scale = new Vector3(1, scaleY, 1);
            }
        }

        private void StopEqualizerAnimation()
        {
            _equalizerRunning = false;
            _equalizerStopwatch.Stop();
            CompositionTarget.Rendering -= OnEqualizerFrame;
            ResourceDiagnosticsService.UnregisterRenderingHandler(); // ★ 诊断
            for (int i = 0; i < 5; i++)
            {
                if (_barVisuals[i] != null)
                    _barVisuals[i].Scale = Vector3.One;
            }
        }

        private void UpdateMiniPlayer(MediaItem item)
        {
            bool isVideo = string.Equals(item.MediaType, "Video", StringComparison.OrdinalIgnoreCase)
                        || _playback.HasExternalPlayback;

            if (isVideo)
            {
                MiniPlayerTitle.Text = item.FileName;
                MiniPlayerTitle.HorizontalAlignment = HorizontalAlignment.Left;
                MiniPlayerArtist.Text = "";
                MiniPlayerArtist.Visibility = Visibility.Collapsed;
            }
            else
            {
                MiniPlayerTitle.Text = item.Title;
                MiniPlayerTitle.HorizontalAlignment = HorizontalAlignment.Left;
                MiniPlayerArtist.Text = item.ArtistDisplay;
                MiniPlayerArtist.Visibility = Visibility.Visible;
            }

            string coverPath = MusicCoverService.GetOrCreate(item.FilePath);
            if (string.IsNullOrWhiteSpace(coverPath) && !string.IsNullOrWhiteSpace(item.ThumbnailPath))
                coverPath = item.ThumbnailPath;
            MiniPlayerCover.Source = ImageThumbnailService.GetOrCreate(coverPath);
            // 切歌：重置模糊层（新资源生成中），避免旧封面模糊图残留
            MiniPlayerBlurCover.Source = null;
            MiniPlayerBlurCover.Opacity = 0;
            MiniPlayerPressedBlurCover.Source = null;
            MiniPlayerPressedBlurCover.Opacity = 0;
            // 后台准备封面悬停资源（图标颜色 + 两档模糊位图）；取消上一次未完成的任务
            _coverBrightnessCts?.Cancel();
            _coverBrightnessCts = new CancellationTokenSource();
            _ = PrepareCoverEffectsAsync(coverPath, _coverBrightnessCts.Token);
            CancelPlayerCleanup();
        }

        private async void MiniPlayerPreviousButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info($"[MiniPlayer] 上一曲按钮被点击, 文件={_playback.ActiveItem?.FileName}, 外部播放={_playback.HasExternalPlayback}");
            await _playback.PlayAdjacentAsync(-1);
        }

        private async void MiniPlayerNextButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info($"[MiniPlayer] 下一曲按钮被点击, 文件={_playback.ActiveItem?.FileName}, 外部播放={_playback.HasExternalPlayback}");
            await _playback.PlayAdjacentAsync(1);
        }

        private void MiniPlayerPlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Debug($"[MiniPlayer] 播放/暂停按钮被点击, 文件={_playback.ActiveItem?.FileName}, 当前状态={_playback.PlaybackState}");
            _playback.TogglePlayPause();
        }

        private void MiniPlayerMuteButton_Click(object sender, RoutedEventArgs e)
        {
            _playback.ToggleMute();
        }

        private void MiniPlayerVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingVolume)
                return;

            if (VolumeText != null)
                VolumeText.Text = $"{Math.Round(e.NewValue):0}";
            _playback.SetVolumePercent(e.NewValue);
        }

        private void UpdateVolumeIcon()
        {
            bool muted = _playback.IsMuted || _playback.VolumePercent <= 0;
            string glyph = muted ? "" : _playback.VolumePercent < 50 ? "" : "";
            MuteIcon.Glyph = glyph;
            VolumeButtonIcon.Glyph = glyph;
            ToolTipService.SetToolTip(MuteButton, muted ? "取消静音" : "静音");
        }

        private void MiniPlayerPlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            _playback.CyclePlayMode();
            UpdatePlayModeIcon();

            // 使用原生 ToolTip 在按钮上方显示当前播放模式
            string modeText = _playback.PlayMode switch
            {
                1 => "单曲循环",
                2 => "随机播放",
                _ => "顺序播放"
            };
            PlayModeToolTipText.Text = modeText;
            PlayModeToolTip.IsOpen = true;

            // 1.5 秒后自动关闭
            _playModeToolTipTimer?.Stop();
            _playModeToolTipTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            _playModeToolTipTimer.Tick += (s, e) =>
            {
                _playModeToolTipTimer.Stop();
                PlayModeToolTip.IsOpen = false;
            };
            _playModeToolTipTimer.Start();
        }

        private void UpdatePlayModeIcon()
        {
            PlayModeIcon.Glyph = _playback.PlayMode switch
            {
                1 => "",
                2 => "",
                _ => ""
            };
        }

        private void MiniPlayerMoreButton_Click(object sender, RoutedEventArgs e)
        {
            ShowMoreMenu(MoreButton);
        }

        /// <summary>
        /// 迷你播放器宽度变化时，根据宽度折叠/展开按钮。
        /// 窗口过窄时将部分按钮隐藏，并在"更多"菜单中显示。
        /// </summary>
        private void MiniPlayer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateMiniPlayerButtonVisibility();
        }

        /// <summary>
        /// 根据迷你播放器宽度决定哪些按钮可见，被隐藏的按钮会出现在"更多"菜单中。
        /// 窗口窄时：播放顺序始终隐藏；关闭 → 隐藏播放器 从右到左依次折叠。
        /// 最小保留：音量、播放队列。
        /// </summary>
        private void UpdateMiniPlayerButtonVisibility()
        {
            double width = MiniPlayer.ActualWidth;
            if (width <= 0) return;

            bool narrow = width < MiniPlayerNarrowThreshold;

            // 窗口宽：所有按钮可见
            if (!narrow)
            {
                SetCollapsedButtons(false, false, false, false, false);
                return;
            }

            // 窗口窄：播放顺序始终隐藏，其余从右到左折叠
            // 最小空间：封面(52) + 间距(12) + 歌名(190) + 间距 + 播放控制(~130) + 间距 + 模式按钮(36) + 更多按钮(36) + 间距 ≈ 500
            // 每多隐藏一个按钮可省约 42px
            double available = width - 500;
            int hiddenCount = 0;

            // 只剩 2 个可折叠按钮：关闭 → 隐藏播放器
            if (available < 1 * 42) { hiddenCount = 2; }
            else if (available < 2 * 42) { hiddenCount = 1; }
            else { hiddenCount = 0; }

            SetCollapsedButtons(
                hidePlayMode: true,
                hideQueue: false,
                hideVolume: false,
                hidePlayer: hiddenCount >= 2,
                hideClose: hiddenCount >= 1);
        }

        /// <summary>
        /// 设置可折叠按钮的可见性。
        /// </summary>
        private void SetCollapsedButtons(bool hidePlayMode, bool hideQueue, bool hideVolume, bool hidePlayer, bool hideClose)
        {
            _isPlayModeHidden = hidePlayMode;
            _isQueueHidden = hideQueue;
            _isVolumeHidden = hideVolume;
            _isHidePlayerHidden = hidePlayer;
            _isCloseHidden = hideClose;

            PlayModeButton.Visibility = hidePlayMode ? Visibility.Collapsed : Visibility.Visible;
            QueueButton.Visibility = hideQueue ? Visibility.Collapsed : Visibility.Visible;
            VolumeButton.Visibility = hideVolume ? Visibility.Collapsed : Visibility.Visible;
            HideMiniPlayerButton.Visibility = hidePlayer ? Visibility.Collapsed : Visibility.Visible;
            CloseMiniPlayerButton.Visibility = hideClose ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowMoreMenu(FrameworkElement target)
        {
            var menu = new MenuFlyout();
            var font = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons");

            // ── 被折叠的按钮：窗口窄时才会出现在这里 ──
            if (_isPlayModeHidden)
            {
                string modeText = _playback.PlayMode switch
                {
                    1 => "单曲循环",
                    2 => "随机播放",
                    _ => "顺序播放"
                };
                string modeIcon = _playback.PlayMode switch
                {
                    1 => "\uE8CB",
                    2 => "\uE8D1",
                    _ => "\uE8AB"
                };
                var playModeItem = new MenuFlyoutItem
                {
                    Text = $"播放模式（{modeText}）",
                    Icon = new FontIcon { FontFamily = font, Glyph = modeIcon }
                };
                playModeItem.Click += (_, _) =>
                {
                    _playback.CyclePlayMode();
                    UpdatePlayModeIcon();
                    // 刷新菜单以更新显示
                    ShowMoreMenu(target);
                };
                menu.Items.Add(playModeItem);
            }

            if (_isQueueHidden)
            {
                var queueItem = new MenuFlyoutItem
                {
                    Text = "播放队列",
                    Icon = new FontIcon { FontFamily = font, Glyph = "\uE8FD" }
                };
                queueItem.Click += (_, _) => _queueFlyout?.ShowAt(MiniPlayer);
                menu.Items.Add(queueItem);
            }

            if (_isVolumeHidden)
            {
                var volumeItem = new MenuFlyoutItem
                {
                    Text = _playback.IsMuted ? "取消静音" : "静音",
                    Icon = new FontIcon { FontFamily = font, Glyph = "\uE767" }
                };
                volumeItem.Click += (_, _) => _playback.ToggleMute();
                menu.Items.Add(volumeItem);
            }

            if (_isHidePlayerHidden)
            {
                var hideItem = new MenuFlyoutItem
                {
                    Text = "隐藏播放器",
                    Icon = new FontIcon { FontFamily = new FontFamily("/Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"), Glyph = "\uE5F6" }
                };
                hideItem.Click += (_, _) => HideMiniPlayer();
                menu.Items.Add(hideItem);
            }

            if (_isCloseHidden)
            {
                var closeItem = new MenuFlyoutItem
                {
                    Text = "关闭播放器",
                    Icon = new FontIcon { FontFamily = font, Glyph = "\uE711" }
                };
                closeItem.Click += (_, _) =>
                {
                    if (_playback.HasExternalPlayback)
                        _playback.ClearExternalPlayback();
                    else
                        _playback.StopPlayback();
                    _isMiniPlayerManuallyHidden = false;
                    UpdateMiniPlayerVisibility();
                };
                menu.Items.Add(closeItem);
            }

            // ── 分隔线：折叠按钮与原有菜单项之间 ──
            if (menu.Items.Count > 0)
                menu.Items.Add(new MenuFlyoutSeparator());

            // ── 原有菜单项 ──
            var speedMenu = new MenuFlyoutSubItem { Text = "播放速度" };
            foreach (double speed in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            {
                var speedItem = new ToggleMenuFlyoutItem
                {
                    Text = $"{speed:0.##}x",
                    IsChecked = Math.Abs(_playback.ActivePlayer.PlaybackSession.PlaybackRate - speed) < 0.01
                };
                speedItem.Click += (_, _) => _playback.ActivePlayer.PlaybackSession.PlaybackRate = speed;
                speedMenu.Items.Add(speedItem);
            }
            menu.Items.Add(speedMenu);

            var properties = new MenuFlyoutItem
            {
                Text = "属性",
                IsEnabled = _playback.ActiveItem != null
            };
            properties.Click += async (_, _) =>
            {
                if (_playback.ActiveItem != null)
                    await ShowPropertiesAsync(_playback.ActiveItem);
            };
            menu.Items.Add(properties);

            var location = new MenuFlyoutItem
            {
                Text = "打开文件所在位置",
                IsEnabled = _playback.ActiveItem != null,
                Icon = new FontIcon { FontFamily = font, Glyph = "\uE8A7" }
            };
            location.Click += (_, _) =>
            {
                if (_playback.ActiveItem != null)
                    OpenFileLocation(_playback.ActiveItem);
            };
            menu.Items.Add(location);

            menu.ShowAt(target);
        }

        // ================== 封面悬停反馈动画（高斯模糊 + 居中放大图标） ==================

        /// <summary>
        /// 对任意依赖属性做平滑缓动动画（XAML Storyboard + CubicEase）。
        /// 每次调用创建新 Storyboard，WinUI 会自动接替旧动画，从当前值平滑过渡。
        /// </summary>
        private void AnimateDouble(DependencyObject target, string propertyPath, double toValue, int durationMs = 180)
        {
            if (target == null) return;

            var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

            var animation = new DoubleAnimation
            {
                To = toValue,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = easing
            };

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, propertyPath);

            storyboard.Begin();
        }

        private void MiniPlayerCover_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOverCover = true;
            // 悬停：悬停档模糊封面淡入覆盖清晰封面 + 居中放大图标淡入
            AnimateDouble(MiniPlayerBlurCover, "Opacity", 1, 150);
            AnimateDouble(MiniPlayerHoverIcon, "Opacity", 1, 150);
        }

        private void MiniPlayerCover_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOverCover = false;
            // 移出：两档模糊封面与图标淡出，还原干净封面
            AnimateDouble(MiniPlayerBlurCover, "Opacity", 0, 150);
            AnimateDouble(MiniPlayerPressedBlurCover, "Opacity", 0, 150);
            AnimateDouble(MiniPlayerHoverIcon, "Opacity", 0, 150);
        }

        private void MiniPlayerCover_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 按下：按下档（更模糊）淡入盖住悬停档，提供即时按压反馈
            AnimateDouble(MiniPlayerPressedBlurCover, "Opacity", 1, 80);
        }

        private void MiniPlayerCover_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isPointerOverCover)
            {
                // 仍在封面内：按下档淡出，露出悬停档模糊
                AnimateDouble(MiniPlayerPressedBlurCover, "Opacity", 0, 120);
            }
            else
            {
                // 已移出：完全还原
                AnimateDouble(MiniPlayerBlurCover, "Opacity", 0, 100);
                AnimateDouble(MiniPlayerPressedBlurCover, "Opacity", 0, 100);
                AnimateDouble(MiniPlayerHoverIcon, "Opacity", 0, 100);
            }
        }

        /// <summary>
        /// 点击封面区域 → 打开全屏播放器。
        /// 由封面 Border 的 Tapped 事件触发（覆盖整个封面区域，点击响应灵敏）。
        /// </summary>
        private void MiniPlayerCover_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var item = _playback.ActiveItem;
            if (item == null) return;

            AppLogger.Info($"[MiniPlayer] 封面被点击, 文件={item.FileName}, 路径={item.FilePath}, 外部播放={_playback.HasExternalPlayback}, CurrentGen={PageLifetimeService.CurrentGeneration}");

            // 点击后立即还原两档模糊与图标（平滑过渡，给用户即时反馈）
            AnimateDouble(MiniPlayerBlurCover, "Opacity", 0, 100);
            AnimateDouble(MiniPlayerPressedBlurCover, "Opacity", 0, 100);
            AnimateDouble(MiniPlayerHoverIcon, "Opacity", 0, 100);

            // 全局递增 generation，使当前所有页面的陈旧异步操作立即失效
            PageLifetimeService.OnNavigatingAway();

            // ★ 修复（迷你播放器再次打开时打开了音乐播放器）：
            //   此前用 HasExternalPlayback 判断——普通模式下视频退出后注册为外部播放
            //   （ExternalPlayer != null）能正确打开视频播放器；但超分模式下退出视频
            //   播放器时无法转移 MediaPlayer，视频被转交给内部播放器（PlayAsync）继续
            //   播放，HasExternalPlayback=false → 误打开音乐播放器。
            //   改为按媒体类型分发：视频 → 视频播放器；音乐/其他 → 音乐播放器。
            if (string.Equals(item.MediaType, "Video", StringComparison.OrdinalIgnoreCase))
            {
                var queue = _playback.HasExternalPlayback
                    ? _playback.ExternalPlayQueue
                    : _playback.PlayQueue;
                var videoQueue = queue.ToList();
                int startIndex = videoQueue.FindIndex(
                    m => m.FilePath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase));
                ShowPlayerOverlay(typeof(VideoPlayerPage), new VideoPlayerArgs
                {
                    Playlist = videoQueue.Count > 0 ? videoQueue : new List<MediaItem> { item },
                    StartIndex = Math.Max(0, startIndex)
                });
            }
            else
            {
                ShowPlayerOverlay(typeof(MusicPlayerPage), new MusicPlayerArgs
                {
                    CurrentItem = _playback.CurrentItem,
                    Playlist = _playback.PlayQueue.ToList(),
                    CurrentIndex = _playback.CurrentIndex
                });
            }

            // 播放器先响应，导航栈和图片缓存等低优先级资源在首屏动画稳定后再处理
            _playerCleanupCts = new CancellationTokenSource();
            _ = CleanupAfterPlayerOpenedAsync(_playerCleanupCts.Token);
        }

        /// <summary>
        /// 后台准备封面的悬停效果资源：
        /// 1. 计算封面平均亮度 → 悬停图标颜色（背景亮用黑、背景暗用白）；
        /// 2. 生成两档高斯模糊封面位图（悬停 / 按下）。
        /// 在后台线程完成重活（解码/模糊/编码），回 UI 线程创建 BitmapImage。
        /// </summary>
        private async Task PrepareCoverEffectsAsync(string? coverPath, CancellationToken cancellationToken)
        {
            double luminance;
            byte[]? hoverBytes;
            byte[]? pressedBytes;

            if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath))
            {
                luminance = 0; // 无封面视为暗背景，默认白色图标
                hoverBytes = null;
                pressedBytes = null;
            }
            else
            {
                (luminance, hoverBytes, pressedBytes) = await Task.Run(
                    () =>
                    {
                        double lum = ComputeCoverAverageLuminance(coverPath);
                        byte[]? h = BlurCoverImage(coverPath, CoverHoverBlurSigma);
                        byte[]? p = BlurCoverImage(coverPath, CoverPressedBlurSigma);
                        return (lum, h, p);
                    },
                    cancellationToken);
            }

            // 切歌导致任务取消：直接丢弃结果
            if (cancellationToken.IsCancellationRequested) return;

            // 图标颜色（背景偏亮 → 黑色，偏黑 → 白色，带轻微透明更柔和）
            if (MiniPlayerHoverIcon != null)
            {
                MiniPlayerHoverIcon.Foreground = new SolidColorBrush(
                    luminance > 0.5
                        ? Windows.UI.Color.FromArgb(230, 0, 0, 0)      // 背景偏亮 → 黑色图标
                        : Windows.UI.Color.FromArgb(230, 255, 255, 255)); // 背景偏黑 → 白色图标
            }

            // 模糊封面位图：一次性固定到两档模糊层，交互时仅用 Opacity 切换（避免 Source 切换闪烁）
            _hoverBlurCover = await CreateBitmapFromBytesAsync(hoverBytes);
            _pressedBlurCover = await CreateBitmapFromBytesAsync(pressedBytes);
            if (cancellationToken.IsCancellationRequested) return;

            MiniPlayerBlurCover.Source = _hoverBlurCover;
            MiniPlayerPressedBlurCover.Source = _pressedBlurCover;
        }

        /// <summary>
        /// 用 SkiaSharp 将封面缩小到 24x24 采样，计算 Rec.709 平均相对亮度，返回 0~1。
        /// </summary>
        private static double ComputeCoverAverageLuminance(string imagePath)
        {
            try
            {
                using SKBitmap? source = SKBitmap.Decode(imagePath);
                if (source is null) return 0;

                using var small = new SKBitmap(24, 24, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(small))
#pragma warning disable CS0618
                using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
#pragma warning restore CS0618
                {
                    canvas.Clear(SKColors.Black);
                    canvas.DrawBitmap(source, new SKRect(0, 0, 24, 24), paint);
                    canvas.Flush();
                }

                double sum = 0;
                int n = 0;
                for (int y = 0; y < small.Height; y++)
                {
                    for (int x = 0; x < small.Width; x++)
                    {
                        SKColor c = small.GetPixel(x, y);
                        sum += (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255.0;
                        n++;
                    }
                }
                return n > 0 ? sum / n : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 用 SkiaSharp 对封面做高斯模糊并编码为 PNG 字节（与封面显示尺寸一致）。
        /// </summary>
        private static byte[]? BlurCoverImage(string imagePath, float sigma)
        {
            try
            {
                using SKBitmap? source = SKBitmap.Decode(imagePath);
                if (source is null) return null;

                const int size = 52;
                using var small = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(small))
#pragma warning disable CS0618
                using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
#pragma warning restore CS0618
                {
                    canvas.Clear(SKColors.Black);
                    canvas.DrawBitmap(source, new SKRect(0, 0, size, size), paint);
                    canvas.Flush();
                }

                using var blurred = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(blurred))
                using (var filter = SKImageFilter.CreateBlur(sigma, sigma))
                using (var paint = new SKPaint { ImageFilter = filter })
                {
                    // 先铺不透明底色，避免模糊边缘产生透明像素导致下层清晰图透出
                    canvas.Clear(SKColors.Black);
                    canvas.DrawBitmap(small, 0, 0, paint);
                    canvas.Flush();
                }

                using SKImage image = SKImage.FromBitmap(blurred);
                using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
                return data.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 在 UI 线程将 PNG 字节创建为 BitmapImage（BitmapImage 必须在 UI 线程构造）。
        /// </summary>
        private static async Task<BitmapImage?> CreateBitmapFromBytesAsync(byte[]? bytes)
        {
            if (bytes is null || bytes.Length == 0) return null;
            try
            {
                using var ms = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private async Task CleanupAfterPlayerOpenedAsync(CancellationToken cancellationToken)
        {
            try
            {
                // 避开覆盖层滑入和 Canvas 首次建资源阶段，减少首秒帧抖动。
                await Task.Delay(700, cancellationToken);
                if (_isClosed || !_isPlayerOverlayActive || cancellationToken.IsCancellationRequested)
                    return;

                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (_isClosed || !_isPlayerOverlayActive || cancellationToken.IsCancellationRequested)
                            return;

                        // 清空导航 BackStack（释放中间页面），但保留根页面供退出播放器后返回。
                        while (ContentFrame.BackStack.Count > 1)
                            ContentFrame.BackStack.RemoveAt(ContentFrame.BackStack.Count - 1);

                        // 仅裁剪旧缩略图，不清空当前播放器需要的全部缓存。
                        ImageThumbnailService.TrimMemoryCache(512);

                        // 非阻塞 GC 提示，不等待终结器，避免暂停 Canvas 渲染线程。
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
                    });
            }
            catch (OperationCanceledException)
            {
                // Expected when the overlay closes before the delayed cleanup starts.
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "延后清理播放器背景资源");
            }
        }

        private void CancelPlayerCleanup()
        {
            CancellationTokenSource? cleanupCts = _playerCleanupCts;
            _playerCleanupCts = null;
            if (cleanupCts == null)
                return;

            cleanupCts.Cancel();
            cleanupCts.Dispose();
        }

        private void MiniPlayerCloseButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info($"[MiniPlayer] 关闭按钮被点击, 文件={_playback.ActiveItem?.FileName}, 外部播放={_playback.HasExternalPlayback}");
            if (_playback.HasExternalPlayback)
            {
                _playback.ClearExternalPlayback();
            }
            else
            {
                _playback.StopPlayback();
            }
            _isMiniPlayerManuallyHidden = false;
            UpdateMiniPlayerVisibility();
        }

        private void HideMiniPlayerButton_Click(object sender, RoutedEventArgs e)
        {
            HideMiniPlayer();
        }

        private bool IsGalleryPage()
        {
            var page = ContentFrame.Content?.GetType();
            return page == typeof(GalleryPage)
                || page == typeof(ImageViewerPage);
        }

        private void PositionCapsule()
        {
            if (RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0) return;
            // CapsulePopup 是 x:Load="False" 延迟元素，未加载时跳过
            if (RootGrid.FindName("CapsulePopup") is not Popup popup) return;
            popup.HorizontalOffset = RootGrid.ActualWidth - 54;
            popup.VerticalOffset = RootGrid.ActualHeight - 40 - 72;
            MiniPlayerRestoreButton.Translation = new Vector3(0, 0, 30);
        }

        private void MiniPlayerRestoreButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var border = (Border)sender;
            border.CapturePointer(e.Pointer);
            _isDragging = true;
            _dragStartPointerY = e.GetCurrentPoint(RootGrid).Position.Y;
            _dragStartTranslateY = border.Translation.Y;
            StopEqualizerAnimation();
        }

        private void MiniPlayerRestoreButton_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            var border = (Border)sender;
            var currentY = e.GetCurrentPoint(RootGrid).Position.Y;
            var deltaY = currentY - _dragStartPointerY;
            border.Translation = new Vector3(0, (float)(_dragStartTranslateY + deltaY), 30);
        }

        private void MiniPlayerRestoreButton_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            var border = (Border)sender;
            border.ReleasePointerCaptures();
            var currentY = e.GetCurrentPoint(RootGrid).Position.Y;
            if (Math.Abs(currentY - _dragStartPointerY) < 2)
            {
                ShowMiniPlayer();
                return;
            }
            if (_playback.PlaybackState == MediaPlaybackState.Playing)
                StartEqualizerAnimation();
        }

        private void MiniPlayerRestoreButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            var border = (Border)sender;
            border.ReleasePointerCaptures();
            if (_playback.PlaybackState == MediaPlaybackState.Playing)
                StartEqualizerAnimation();
        }

        private void MiniPlayerRestoreButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (MiniPlayerRestoreButton.Background is AcrylicBrush acrylic)
                acrylic.TintOpacity = 0.85;
        }

        private void MiniPlayerRestoreButton_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging) return;
            if (MiniPlayerRestoreButton.Background is AcrylicBrush acrylic)
                acrylic.TintOpacity = 0.7;
        }

        private void MiniPlayerRestoreButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ShowMiniPlayer();
        }

        private void MiniPlayerProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingProgress)
                return;

            if (MiniPositionText != null)
                MiniPositionText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
            _playback.SetPosition(TimeSpan.FromSeconds(e.NewValue));
        }

        private void MiniPlayerProgressSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            double width = Math.Max(1, PlaybackProgressSlider.ActualWidth);
            double x = e.GetCurrentPoint(PlaybackProgressSlider).Position.X;
            double ratio = Math.Clamp(x / width, 0, 1);
            double seconds = ratio * Math.Max(1, PlaybackProgressSlider.Maximum);
            MiniProgressTimeTipText.Text = FormatTime(TimeSpan.FromSeconds(seconds));
            MiniProgressTimeTip.Opacity = 1;

            double tipWidth = Math.Max(44, MiniProgressTimeTip.ActualWidth);
            double left = Math.Clamp(
                PlaybackProgressSlider.ActualWidth * ratio - tipWidth / 2,
                0,
                Math.Max(0, PlaybackProgressSlider.ActualWidth - tipWidth));
            MiniProgressTimeTip.Margin = new Thickness(left, 0, 0, 0);
        }

        private void MiniPlayerProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            MiniProgressTimeTip.Opacity = 0;
        }

        private void MiniPlayerTimer_Tick(object? sender, object e)
        {
            TimeSpan duration = _playback.Duration;
            if (duration.TotalSeconds <= 0)
            {
                MiniPositionText.Text = "00:00";
                MiniDurationText.Text = "00:00";
                return;
            }

            _updatingProgress = true;
            PlaybackProgressSlider.Maximum = duration.TotalSeconds;
            var position = _playback.Position.TotalSeconds;
            var clampedPosition = Math.Clamp(position, 0, duration.TotalSeconds);
            PlaybackProgressSlider.Value = clampedPosition;
            MiniPositionText.Text = FormatTime(_playback.Position);
            MiniDurationText.Text = FormatTime(duration);
            _updatingProgress = false;

            // 每 2 秒记录一次迷你播放器进度
            if (_playback.PlaybackState == MediaPlaybackState.Playing)
            {
                var now = DateTime.Now;
                if ((now - _lastMiniPlayerPositionLogTime).TotalSeconds >= 2)
                {
                    AppLogger.Debug($"[MiniPlayer] 进度更新: 文件={_playback.ActiveItem?.FileName}, 位置={clampedPosition:F1}s, 时长={duration.TotalSeconds:F1}s, 状态={_playback.PlaybackState}");
                    _lastMiniPlayerPositionLogTime = now;
                    
                    // 检测异常：位置接近末尾但仍在播放
                    if (clampedPosition >= duration.TotalSeconds - 0.5 && duration.TotalSeconds > 5)
                    {
                        AppLogger.Warning($"[MiniPlayer] 进度接近末尾异常: 文件={_playback.ActiveItem?.FileName}, 位置={clampedPosition:F1}s, 时长={duration.TotalSeconds:F1}s");
                    }
                }
            }
        }

        private void OnMiniPlayer_CurrentItemChanged(object? sender, EventArgs e)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                if (_playback.ActiveItem == null)
                {
                    AppLogger.Debug("[MiniPlayer] CurrentItemChanged: ActiveItem 为 null");
                    return;
                }

                AppLogger.Info($"[MiniPlayer] CurrentItemChanged: 文件={_playback.ActiveItem.FileName}, 路径={_playback.ActiveItem.FilePath}, 类型={_playback.ActiveItem.MediaType}, 外部播放={_playback.HasExternalPlayback}");
                UpdateMiniPlayer(_playback.ActiveItem);
                UpdateMiniPlayerVisibility();
            }
            else
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_playback.ActiveItem == null)
                    {
                        AppLogger.Debug("[MiniPlayer] CurrentItemChanged: ActiveItem 为 null");
                        return;
                    }

                    AppLogger.Info($"[MiniPlayer] CurrentItemChanged: 文件={_playback.ActiveItem.FileName}, 路径={_playback.ActiveItem.FilePath}, 类型={_playback.ActiveItem.MediaType}, 外部播放={_playback.HasExternalPlayback}");
                    UpdateMiniPlayer(_playback.ActiveItem);
                    UpdateMiniPlayerVisibility();
                });
            }
        }

        private void OnMiniPlayer_PlaybackStateChanged(object? sender, EventArgs e)
        {
            var state = _playback.PlaybackState;
            var item = _playback.ActiveItem;
            AppLogger.Debug($"[MiniPlayer] PlaybackStateChanged: 文件={item?.FileName}, 状态={state}, 外部播放={_playback.HasExternalPlayback}, 手动隐藏={_isMiniPlayerManuallyHidden}");

            DispatcherQueue.TryEnqueue(() =>
            {
                PlayPauseIcon.Glyph =
                    _playback.PlaybackState == MediaPlaybackState.Playing
                        ? ""
                        : "";

                if (_isMiniPlayerManuallyHidden)
                {
                    if (_playback.PlaybackState == MediaPlaybackState.Playing)
                        StartEqualizerAnimation();
                    else
                        StopEqualizerAnimation();
                }

                if (_isQueueFlyoutOpen)
                {
                    if (_playback.PlaybackState == MediaPlaybackState.Playing && _playback.ActiveItem != null)
                        StartQueueEqualizerAnimation();
                    else
                        StopQueueEqualizerAnimation();
                }
            });
        }

        private void OnMiniPlayer_VolumeChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _updatingVolume = true;
                VolumeSlider.Value = _playback.VolumePercent;
                VolumeText.Text = $"{Math.Round(_playback.VolumePercent):0}";
                _updatingVolume = false;
                UpdateVolumeIcon();
            });
        }

        private void OnMiniPlayer_PlayModeChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(UpdatePlayModeIcon);
        }

        private void OnMiniPlayer_PlaybackFailed(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PlayPauseIcon.Glyph = "";
            });
        }

        private void OnMiniPlayer_ExternalPlaybackChanged(object? sender, EventArgs e)
        {
            AppLogger.Info($"[MiniPlayer] ExternalPlaybackChanged: 文件={_playback.ActiveItem?.FileName}, 外部播放={_playback.HasExternalPlayback}, 状态={_playback.PlaybackState}");

            if (DispatcherQueue.HasThreadAccess)
            {
                MiniPlayerHost.SetMediaPlayer(_playback.ActivePlayer);
                if (_playback.ActiveItem != null)
                    UpdateMiniPlayer(_playback.ActiveItem);
                UpdateMiniPlayerVisibility();
            }
            else
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    MiniPlayerHost.SetMediaPlayer(_playback.ActivePlayer);
                    if (_playback.ActiveItem != null)
                        UpdateMiniPlayer(_playback.ActiveItem);
                    UpdateMiniPlayerVisibility();
                });
            }
        }

        private void SyncMiniPlayerFromService()
        {
            if (_playback.ActiveItem != null)
            {
                UpdateMiniPlayer(_playback.ActiveItem);
                UpdateMiniPlayerVisibility();
            }

            _updatingVolume = true;
            VolumeSlider.Value = _playback.VolumePercent;
            VolumeText.Text = $"{Math.Round(_playback.VolumePercent):0}";
            _updatingVolume = false;
            UpdateVolumeIcon();
            UpdatePlayModeIcon();
            PlayPauseIcon.Glyph = _playback.PlaybackState == MediaPlaybackState.Playing
                ? ""
                : "";
        }



        private static string FormatTime(TimeSpan time)
        {
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss")
                : time.ToString(@"mm\:ss");
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
                Visibility = Visibility.Collapsed,
                Foreground = new SolidColorBrush(IsDarkTheme() ? ColorHelper.FromArgb(255, 200, 200, 200) : ColorHelper.FromArgb(255, 80, 80, 80))
            };

            // 列表控件（ItemTemplateSelector 在 Opening 时动态设置）
            _queueList = new ListView
            {
                Background = new SolidColorBrush(Colors.Transparent),
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
                Height = 520,   // 固定高度，空状态时不会坍缩
                MaxHeight = 520,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = rootGrid
            };

            // 根据软件主题设置 Flyout 边框和背景材质，不跟随系统主题
            bool isDark = IsDarkTheme();
            var flyoutBackgroundColor = isDark ? ColorHelper.FromArgb(255, 28, 28, 28) : ColorHelper.FromArgb(255, 249, 249, 249);
            var flyoutBorderColor = isDark ? ColorHelper.FromArgb(255, 58, 58, 58) : ColorHelper.FromArgb(255, 208, 208, 208);

            // 初始化播放队列卡片的交互画刷
            _queueNormalBgBrush = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 28, 28, 28) : ColorHelper.FromArgb(255, 249, 249, 249));
            _queueHoverBgBrush = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 58, 58, 58) : ColorHelper.FromArgb(255, 232, 232, 232));
            _queuePressedBgBrush = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 74, 74, 74) : ColorHelper.FromArgb(255, 216, 216, 216));
            _queueNormalBorderBrush = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 58, 58, 58) : ColorHelper.FromArgb(255, 208, 208, 208));
            _queueHoverBorderBrush = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 90, 90, 90) : ColorHelper.FromArgb(255, 184, 184, 184));
            _queuePressedBorderBrush = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 106, 106, 106) : ColorHelper.FromArgb(255, 168, 168, 168));

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

            _queueFlyout.Opening += (_, _) =>
            {
                _isQueueFlyoutOpen = true;

                // 刷新列表并设置模板选择器
                RefreshQueueItems();
                _queueList.ItemTemplateSelector = new QueueTemplateSelector
                {
                    DefaultTemplate = _queueDefaultTemplate!,
                    NowPlayingTemplate = _queueNowPlayingTemplate!
                };

                // 如果正在播放，启动均衡器（与 Temp 实现一致，放在 Opening 事件中，通过 DispatcherQueue 延迟到视觉树就绪后执行）
                if (_playback.ActiveItem != null
                    && _playback.PlaybackState == MediaPlaybackState.Playing)
                    StartQueueEqualizerAnimation();
            };

            // _queueFlyout.Opened 事件中不再启动均衡器，避免与 Opening 中的调用重复

            _queueFlyout.Closed += (_, _) =>
            {
                _isQueueFlyoutOpen = false;
                StopQueueEqualizerAnimation();
            };

            // QueueButton 在 MiniPlayer 内（x:Load="False"），可能尚未加载
            if (QueueButton != null)
                QueueButton.Flyout = _queueFlyout;
        }

        /// <summary>
        /// MiniPlayer 首次加载后，将已构建的播放队列 Flyout 绑定到 QueueButton。
        /// </summary>
        private void AttachQueueFlyout()
        {
            if (_queueFlyout != null && QueueButton != null)
                QueueButton.Flyout = _queueFlyout;
        }

        private static Style CreateFlyoutPresenterStyle()
        {
            var style = new Style(typeof(FlyoutPresenter));
            style.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
            style.Setters.Add(new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
            return style;
        }

        private (DataTemplate defaultTemplate, DataTemplate nowPlayingTemplate) CreateQueueItemTemplates()
        {
            // 直接从 XAML 资源获取，避免 XamlReader.Load 导致的启动死锁和资源查找问题
            var defaultTemplate = (DataTemplate)RootGrid.Resources["QueueItemDefaultTemplate"];
            var nowPlayingTemplate = (DataTemplate)RootGrid.Resources["QueueItemNowPlayingTemplate"];
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
                    XamlRoot = Content.XamlRoot
                };
                var result = await DialogService.ShowAsync(dialog, Content.XamlRoot);
                if (result != ContentDialogResult.Primary)
                    return;
            }

            _queueFlyout.Hide();
            _playback.StopPlayback();
            _playback.ClearExternalPlayback();
            MiniPlayerTitle.Text = "暂无播放";
            MiniPlayerArtist.Text = "无";
            MiniPlayerArtist.Visibility = Visibility.Visible;
            MiniPlayerCover.Source = null;
            // 重置悬停状态（封面已清除，还原干净状态）
            MiniPlayerBlurCover.Source = null;
            MiniPlayerBlurCover.Opacity = 0;
            MiniPlayerPressedBlurCover.Source = null;
            MiniPlayerPressedBlurCover.Opacity = 0;
            AnimateDouble(MiniPlayerHoverIcon, "Opacity", 0, 0);
            _hoverBlurCover = null;
            _pressedBlurCover = null;
            if (MiniPlayerHoverIcon != null)
            {
                MiniPlayerHoverIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            }
        }

        private async void QueueList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MediaItem item)
                return;

            _queueFlyout.Hide();

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
        private static void OpenFileLocation(MediaItem item)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer",
                    Arguments = $"/select,\"{item.FilePath}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private async Task ShowPropertiesAsync(MediaItem item)
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = $"标题：{item.Title}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"艺术家：{item.ArtistDisplay}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"时长：{item.DurationText}" });
            content.Children.Add(new TextBlock { Text = $"大小：{item.FileSizeText}" });
            content.Children.Add(new TextBlock { Text = $"路径：{item.FilePath}", TextWrapping = TextWrapping.Wrap });

            var dialog = new ContentDialog
            {
                Title = "属性",
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = Content.XamlRoot
            };
            await DialogService.ShowAsync(dialog, Content.XamlRoot);
        }

    }
}
