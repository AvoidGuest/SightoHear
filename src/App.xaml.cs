using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.ViewManagement;
using SightoHear.Services;
using SightoHear.Helpers;
using IoPath = System.IO.Path;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SightoHear
{
    /// <summary>
    /// 程序入口（自定义 Main，替代 XAML 生成的 Main，csproj 已定义 DISABLE_XAML_GENERATED_MAIN）。
    /// 项目为框架依赖部署（WindowsAppSDKSelfContained=false），通过 PackageDependency 声明依赖
    /// Windows App Runtime 框架包（Microsoft.WindowsAppRuntime.2），启动时由 Bootstrap
    /// auto-initializer 在 Main 之前自动加载框架运行时（SelfContained=true 仅指 .NET 自包含，互不冲突）。
    /// 自定义 Main 可捕获 Application.Start 阶段的异常并输出启动诊断日志，
    /// 便于排查打包（MSIX）环境下"激活成功但进程瞬间退出"的问题。
    /// 诊断日志写入用户主目录根下（动态获取路径，不写死用户名，避免泄露隐私；runFullTrust 打包应用可写，不受 Known Folder 虚拟化影响）。
    /// </summary>
    public static class Program
    {
        internal static readonly string StartupLogPath =
            IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "sightohear-startup.log");

        [System.STAThread]
        static void Main(string[] args)
        {
            void Log(string message)
            {
                try { System.IO.File.AppendAllText(StartupLogPath, $"{System.DateTime.Now:HH:mm:ss.fff} {message}\r\n"); } catch { }
            }

            Log("Main 开始");
            try
            {
                global::WinRT.ComWrappersSupport.InitializeComWrappers();
                Log("ComWrappers 初始化完成");
                global::Microsoft.UI.Xaml.Application.Start((p) =>
                {
                    Log("Application.Start 回调（new App()）");
                    var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                    Log("new App() 返回");
                });
                Log("Application.Start 返回（消息循环结束）");
            }
            catch (Exception ex)
            {
                Log("Main 捕获异常: " + ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }
        public static MusicPlaybackService MusicPlayback { get; } = new();
        // 单实例互斥量，静态保持引用，防止被 GC 回收导致多开失效
        public static System.Threading.Mutex? InstanceMutex { get; private set; }

        // Win32 API 声明：用于单实例模式下切换到第一个窗口
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        // Win32 API 声明：设置进程的 Application User Model ID (AUMID)
        // 未打包应用必须显式设置 AUMID，否则系统媒体传输控件 (SMTC) 无法正确注册 SourceAppUserModelId
        // 其他软件（如 BetterLyrics）依赖此标识来识别播放源
        [DllImport("shell32.dll", SetLastError = true)]
        private static extern void SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);

        private const int SW_SHOW = 5;
        private const uint GW_OWNER = 4;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>
        /// 静态构造器：在一切用户代码之前注册 FirstChanceException 监听，
        /// 确保 MusicPlaybackService 等静态字段初始化时抛出的 IOException 也能被捕获。
        /// </summary>
        static App()
        {
            AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
            {
                if (e.Exception is IOException ioex)
                {
                    System.Diagnostics.Debug.WriteLine("");
                    System.Diagnostics.Debug.WriteLine($"========================================");
                    System.Diagnostics.Debug.WriteLine($"[FirstChance IOException] Thread={Environment.CurrentManagedThreadId} Msg={ioex.Message}");
                    System.Diagnostics.Debug.WriteLine($"  === e.Exception.StackTrace (throw点) ===");
                    System.Diagnostics.Debug.WriteLine($"  {ioex.StackTrace?.Replace("\n", "\n  ") ?? "(null)"}");
                    System.Diagnostics.Debug.WriteLine($"  === Environment.StackTrace (当前线程完整调用栈) ===");
                    System.Diagnostics.Debug.WriteLine($"  {Environment.StackTrace.Replace("\n", "\n  ")}");
                    System.Diagnostics.Debug.WriteLine($"========================================");
                    System.Diagnostics.Debug.WriteLine("");

                    // 如果日志系统已就绪，也写入日志文件（绕过 null 检查：AppLogger 是静态类）
                    try { Helpers.AppLogger.Warning($"FirstChance IOException(Thread={Environment.CurrentManagedThreadId}): {ioex.Message}"); } catch { }
                }
            };
        }

        public static event Action<string>? ThemeChanged;

        public static void TriggerThemeChanged(string mode)
        {
            ThemeChanged?.Invoke(mode);
        }

        public static readonly Windows.UI.Color[] AccentColors =
        {
            Windows.UI.Color.FromArgb(255, 232, 17, 35),
            Windows.UI.Color.FromArgb(255, 0, 120, 212),
            Windows.UI.Color.FromArgb(255, 16, 124, 16),
            Windows.UI.Color.FromArgb(255, 136, 23, 152),
            Windows.UI.Color.FromArgb(255, 216, 59, 1),
            Windows.UI.Color.FromArgb(255, 246, 55, 154)
        };

        public static Windows.UI.Color GetSystemAccentColor()
        {
            try
            {
                return new UISettings().GetColorValue(UIColorType.Accent);
            }
            catch
            {
                return AccentColors[1];
            }
        }

        public static void ApplyGlobalAccentColor(Windows.UI.Color accent)
        {
            var light1 = Blend(accent, 255, 0.2);
            var light2 = Blend(accent, 255, 0.4);
            var light3 = Blend(accent, 255, 0.6);
            var dark1 = Blend(accent, 0, 0.15);
            var dark2 = Blend(accent, 0, 0.3);
            var dark3 = Blend(accent, 0, 0.45);

            SetColorResource("SystemAccentColor", accent);
            SetColorResource("SystemAccentColorLight1", light1);
            SetColorResource("SystemAccentColorLight2", light2);
            SetColorResource("SystemAccentColorLight3", light3);
            SetColorResource("SystemAccentColorDark1", dark1);
            SetColorResource("SystemAccentColorDark2", dark2);
            SetColorResource("SystemAccentColorDark3", dark3);

            SetColorResource("AccentFillColorDefault", accent);
            SetColorResource("AccentFillColorSecondary", WithAlpha(accent, 230));
            SetColorResource("AccentFillColorTertiary", WithAlpha(accent, 204));
            SetColorResource("AccentFillColorDisabled", WithAlpha(accent, 102));
            SetColorResource("AccentTextFillColorPrimary", accent);
            SetColorResource("AccentTextFillColorSecondary", WithAlpha(accent, 230));
            SetColorResource("AccentTextFillColorTertiary", WithAlpha(accent, 204));
            SetColorResource("AccentTextFillColorDisabled", WithAlpha(accent, 102));

            // ── Brush 系列（App.xaml 预注册的 SolidColorBrush 实例，复用实例改 Color 即时生效）──
            SetBrushResource("SightoHearAccentBrush", accent);
            SetBrushResource("AccentFillColorDefaultBrush", accent);
            SetBrushResource("AccentFillColorSecondaryBrush", WithAlpha(accent, 230));
            SetBrushResource("AccentFillColorTertiaryBrush", WithAlpha(accent, 204));
            SetBrushResource("AccentFillColorDisabledBrush", WithAlpha(accent, 102));
            SetBrushResource("AccentTextFillColorPrimaryBrush", accent);
            SetBrushResource("AccentTextFillColorSecondaryBrush", WithAlpha(accent, 230));
            SetBrushResource("AccentTextFillColorTertiaryBrush", WithAlpha(accent, 204));
            SetBrushResource("AccentTextFillColorDisabledBrush", WithAlpha(accent, 102));
            SetBrushResource("SystemControlHighlightAccentBrush", accent);
            SetBrushResource("SystemControlBackgroundAccentBrush", accent);
            SetBrushResource("SystemControlForegroundAccentBrush", accent);
            SetBrushResource("SystemControlHighlightAccent3Brush", accent);
            SetBrushResource("ProgressBarForegroundThemeBrush", accent);

            // AccentButton 系列（ContentDialog 主按钮 / ImageCropDialog 的 AccentButtonStyle）
            SetBrushResource("AccentButtonBackground", accent);
            SetBrushResource("AccentButtonBackgroundPointerOver", accent);
            SetBrushResource("AccentButtonBackgroundPressed", accent);
            SetBrushResource("AccentButtonBackgroundDisabled", WithAlpha(accent, 102));
            // 前景保持白色（对比度高）
            SetBrushResource("AccentButtonBorderBrush", accent);
            SetBrushResource("AccentButtonBorderBrushPointerOver", accent);
            SetBrushResource("AccentButtonBorderBrushPressed", accent);
            SetBrushResource("AccentButtonBorderBrushDisabled", WithAlpha(accent, 102));

            // HyperlinkButton 前景
            SetBrushResource("HyperlinkButtonForeground", accent);
            SetBrushResource("HyperlinkButtonForegroundPointerOver", accent);
            SetBrushResource("HyperlinkButtonForegroundPressed", accent);
            SetBrushResource("HyperlinkButtonForegroundDisabled", WithAlpha(accent, 102));

            // Slider 轨道填充 + Thumb（迷你播放器进度条/音量条等）
            SetBrushResource("SliderTrackValueFill", accent);
            SetBrushResource("SliderTrackValueFillPointerOver", accent);
            SetBrushResource("SliderTrackValueFillPressed", accent);
            SetBrushResource("SliderTrackValueFillDisabled", WithAlpha(accent, 102));
            SetBrushResource("SliderThumbBackground", accent);
            SetBrushResource("SliderThumbBackgroundPointerOver", accent);
            SetBrushResource("SliderThumbBackgroundPressed", accent);
            SetBrushResource("SliderThumbBackgroundDisabled", WithAlpha(accent, 102));
            SetBrushResource("SliderThumbBorderBrush", accent);
            SetBrushResource("SliderThumbBorderBrushPointerOver", accent);
            SetBrushResource("SliderThumbBorderBrushPressed", accent);
            SetBrushResource("SliderThumbBorderBrushDisabled", WithAlpha(accent, 102));

            // ToggleSwitch 开启态（设置页 39 个开关）
            SetBrushResource("ToggleSwitchFillOn", accent);
            SetBrushResource("ToggleSwitchFillOnPointerOver", accent);
            SetBrushResource("ToggleSwitchFillOnPressed", accent);
            SetBrushResource("ToggleSwitchFillOnDisabled", WithAlpha(accent, 102));
            SetBrushResource("ToggleSwitchStrokeOn", accent);
            SetBrushResource("ToggleSwitchStrokeOnPointerOver", accent);
            SetBrushResource("ToggleSwitchStrokeOnPressed", accent);
            SetBrushResource("ToggleSwitchStrokeOnDisabled", WithAlpha(accent, 102));
            // Knob 保持白色（对比度高），Disabled 时用淡化主题色
            SetBrushResource("ToggleSwitchKnobFillOnDisabled", WithAlpha(accent, 102));

            // CheckBox 勾选态（多选复选框 47 个）
            SetBrushResource("CheckBoxCheckBackgroundFillChecked", accent);
            SetBrushResource("CheckBoxCheckBackgroundFillCheckedPointerOver", accent);
            SetBrushResource("CheckBoxCheckBackgroundFillCheckedPressed", accent);
            SetBrushResource("CheckBoxCheckBackgroundFillCheckedDisabled", WithAlpha(accent, 102));
            SetBrushResource("CheckBoxCheckBackgroundStrokeChecked", accent);
            SetBrushResource("CheckBoxCheckBackgroundStrokeCheckedPointerOver", accent);
            SetBrushResource("CheckBoxCheckBackgroundStrokeCheckedPressed", accent);
            SetBrushResource("CheckBoxCheckBackgroundStrokeCheckedDisabled", WithAlpha(accent, 102));
        }

        private static void SetColorResource(string key, Windows.UI.Color color)
        {
            Application.Current.Resources[key] = color;
        }

        private static void SetBrushResource(string key, Windows.UI.Color color)
        {
            if (Application.Current.Resources.ContainsKey(key) &&
                Application.Current.Resources[key] is SolidColorBrush brush)
            {
                brush.Color = color;
                return;
            }

            Application.Current.Resources[key] = new SolidColorBrush(color);
        }

        private static Windows.UI.Color Blend(Windows.UI.Color color, byte target, double amount)
        {
            static byte Mix(byte source, byte targetValue, double ratio) =>
                (byte)Math.Clamp(Math.Round(source + (targetValue - source) * ratio), 0, 255);

            return Windows.UI.Color.FromArgb(
                255,
                Mix(color.R, target, amount),
                Mix(color.G, target, amount),
                Mix(color.B, target, amount));
        }

        private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) =>
            Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

        public static class SettingsHelper
        {
            private static readonly string FilePath = IoPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SightoHear", "settings.json");

            public static bool UseWindowsTheme { get; set; } = true;
            public static int SelectedColorIndex { get; set; } = 1;
            public static string BackdropType { get; set; } = "Mica";
            public static bool KeepContentMica { get; set; }
            public static string ThemeMode { get; set; } = "System";
            public static bool RememberWindowSize { get; set; }
            public static int WindowWidth { get; set; }
            public static int WindowHeight { get; set; }
            public static bool RememberWindowPosition { get; set; }
            public static bool HasWindowPosition { get; set; }
            public static int WindowX { get; set; }
            public static int WindowY { get; set; }
            public static bool AllowMultiInstance { get; set; } = true;
            public static bool ConfirmDialogReverse { get; set; }
            public static bool MusicRefreshOnStartup { get; set; } = true;
            public static int MusicDefaultView { get; set; }
            public static int MusicDefaultSort { get; set; }
            public static bool MusicRememberView { get; set; } = true;
            public static bool MusicRememberSort { get; set; } = true;
            public static int MusicFileOpenMode { get; set; }
            public static double MusicVolume { get; set; } = 1.0;
            public static bool MusicMuted { get; set; }
            // 音乐播放器单独输出设备 ID（空字符串 = 跟随系统默认输出设备）
            public static string MusicOutputDeviceId { get; set; } = string.Empty;
            // 视频播放器单独输出设备 ID（空字符串 = 跟随系统默认输出设备）
            public static string VideoOutputDeviceId { get; set; } = string.Empty;
            public static bool MusicUseNetworkLyricsSource { get; set; }
            public static string MusicLyricsSourcePreference { get; set; } = "Auto";
            public static int PlaylistDefaultView { get; set; } = 1;
            public static int PlaylistDefaultSort { get; set; } = 1;
            public static bool PlaylistRememberView { get; set; } = true;
            public static bool PlaylistRememberSort { get; set; } = true;
            public static int ArtistDefaultView { get; set; } = 1;
            public static int ArtistDefaultSort { get; set; } = 1;
            public static bool ArtistRememberView { get; set; } = true;
            public static bool ArtistRememberSort { get; set; } = true;
            public static int AlbumDefaultView { get; set; } = 1;
            public static int AlbumDefaultSort { get; set; } = 1;
            public static bool AlbumRememberView { get; set; } = true;
            public static bool AlbumRememberSort { get; set; } = true;
            public static int FolderDefaultView { get; set; } = 0;
            public static int FolderDefaultSort { get; set; } = 0;
            public static bool FolderRememberView { get; set; } = true;
            public static bool FolderRememberSort { get; set; } = true;
            public static bool VideoRefreshOnStartup { get; set; } = true;
            public static int VideoDefaultView { get; set; } = 1;
            public static int VideoDefaultSort { get; set; } = 0;
            public static bool VideoRememberView { get; set; } = true;
            public static bool VideoRememberSort { get; set; } = true;
            public static string VideoDecoderBackend { get; set; } = "FFmpeg";
            public static string VideoDecodeMode { get; set; } = "Auto";
            // 视频播放模式：Normal = 普通模式（MediaPlayerElement，默认）；Mpv = 超分模式（libmpv + Anime4K，实验性）
            public static string VideoPlayerMode { get; set; } = "Normal";
            // 记忆播放位置（续播）：关闭播放器后记住每个视频的播放位置，下次打开时从上次观看处继续播放
            public static bool RememberVideoPosition { get; set; }
            // 记忆当前视频播放进度（播放器弹窗开关）：仅记录当前正在播放视频的进度，下次打开该视频自动续播
            public static bool RememberCurrentVideoPosition { get; set; }
            // 自动播放下一个：视频播放完毕后自动播放下一个视频（默认关闭）
            public static bool AutoPlayNextVideo { get; set; }
            // 自动播放：打开视频后自动开始播放（默认开启；关闭后进入播放器处于暂停状态）
            public static bool AutoPlayVideo { get; set; } = true;
            // 后台播放：播放视频时窗口最小化后继续播放（默认开启；关闭后最小化时暂停，还原窗口恢复播放）
            public static bool BackgroundPlayVideo { get; set; } = true;
            // 超分模式参数（仅 VideoPlayerMode == "Mpv" 时生效）
            public static bool VideoSuperResolutionEnabled { get; set; }
            // 超分质量档位：Low=低档（VL 模型，最快）/ Medium=中档（S 模型）/ High=高档（M 模型）/ Ultra=超高档（UL 模型，最高画质）
            public static string VideoSuperResolutionQuality { get; set; } = "Medium";
            public static string VideoSuperResolutionModel { get; set; } = "anime4k";
            // 运动补偿（补帧）参数（仅 VideoPlayerMode == "Mpv" 且 x64 平台时生效）
            public static bool VideoMotionCompensationEnabled { get; set; }
            // 运动补偿模式：MVT_LQ= MVTools 补帧-LQ（倍帧）/ MVT_STD= MVTools 补帧-STD（60fps）/ SVP_LQ= SVPFlow 补帧-LQ（倍帧）/ SVP_PRO= SVPFlow 补帧-PRO（60fps）
            public static string VideoMotionCompensationMode { get; set; } = "MVT_LQ";
            public static bool GalleryRefreshOnStartup { get; set; } = true;
            public static int GalleryDefaultView { get; set; }
            public static int GalleryDefaultSort { get; set; } = 1;
            public static bool GalleryRememberView { get; set; } = true;
            public static bool GalleryRememberSort { get; set; } = true;
            // 通用：删除本地媒体文件时移入回收站（而非永久删除）
            public static bool DeleteToRecycleBin { get; set; } = true;

            // ---- 图库设置（GallerySettingsPage） ----
            // 图库卡片高度（基准 146，决定缩略图显示大小）
            public static int GalleryThumbnailHeight { get; set; } = 146;
            // 磁盘缩略图生成分辨率（像素，缩略图质量：越大越清晰，越占磁盘/内存）
            public static uint GalleryThumbnailSize { get; set; } = 192;
            // 后台预热缩略图：进入图库页后后台填充缩略图内存缓存
            public static bool GalleryPreloadThumbnails { get; set; } = true;
            // 卡片上显示图片信息（文件名/分辨率）
            public static bool GalleryShowImageInfo { get; set; }
            // 查看器双击行为：0=放大/适应切换，1=左右切图，2=无操作
            public static int GalleryViewerDoubleClickAction { get; set; }
            // 打开查看器自动进入全屏
            public static bool GalleryAutoFullScreen { get; set; }
            // 查看器背景色：0=黑色，1=深灰，2=浅灰
            public static int GalleryViewerBackground { get; set; }
            // 查看器滑动切图动画
            public static bool GallerySlideAnimation { get; set; } = true;
            // 进入图库页/查看器时自动隐藏迷你播放器（默认开启，保持原有行为）
            public static bool GalleryHideMiniPlayerOnEnter { get; set; } = true;

            public static string LastVideoPath { get; set; } = string.Empty;
            public static string LastImagePath { get; set; } = string.Empty;
            public static string LastMusicPath { get; set; } = string.Empty;
            public static DateTime LastVideoTime { get; set; }
            public static DateTime LastImageTime { get; set; }
            public static DateTime LastMusicTime { get; set; }
            public static string Win2DGpuPreference { get; set; } = "Auto";
            public static string Win2DGpuAdapterLuid { get; set; } = "";
            public static Windows.UI.Color CustomAccentColor { get; set; } = Windows.UI.Color.FromArgb(255, 0, 120, 212);

            public static void Load()
            {
                if (!File.Exists(FilePath))
                {
                    Save();
                    return;
                }

                try
                {
                    var json = File.ReadAllText(FilePath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("UseWindowsTheme", out var ut))
                        UseWindowsTheme = ut.GetBoolean();
                    if (root.TryGetProperty("SelectedColorIndex", out var idx))
                        SelectedColorIndex = idx.GetInt32();
                    if (root.TryGetProperty("BackdropType", out var bt))
                        BackdropType = bt.GetString() ?? "Mica";
                    if (root.TryGetProperty("KeepContentMica", out var keepContentMica))
                        KeepContentMica = keepContentMica.GetBoolean();
                    if (root.TryGetProperty("ThemeMode", out var tm))
                        ThemeMode = tm.GetString() ?? "System";
                    if (root.TryGetProperty("RememberWindowSize", out var remember))
                        RememberWindowSize = remember.GetBoolean();
                    if (root.TryGetProperty("WindowWidth", out var width))
                        WindowWidth = width.GetInt32();
                    if (root.TryGetProperty("WindowHeight", out var height))
                        WindowHeight = height.GetInt32();
                    if (root.TryGetProperty("RememberWindowPosition", out var rememberPosition))
                        RememberWindowPosition = rememberPosition.GetBoolean();
                    if (root.TryGetProperty("HasWindowPosition", out var hasPosition))
                        HasWindowPosition = hasPosition.GetBoolean();
                    if (root.TryGetProperty("WindowX", out var x))
                        WindowX = x.GetInt32();
                    if (root.TryGetProperty("WindowY", out var y))
                        WindowY = y.GetInt32();
                    if (root.TryGetProperty("AllowMultiInstance", out var allowMulti))
                        AllowMultiInstance = allowMulti.GetBoolean();
                    if (root.TryGetProperty("ConfirmDialogReverse", out var confirmReverse))
                        ConfirmDialogReverse = confirmReverse.GetBoolean();
                    if (root.TryGetProperty("MusicRefreshOnStartup", out var musicRefresh))
                        MusicRefreshOnStartup = musicRefresh.GetBoolean();
                    if (root.TryGetProperty("MusicDefaultView", out var musicView))
                        MusicDefaultView = musicView.GetInt32();
                    if (root.TryGetProperty("MusicDefaultSort", out var musicSort))
                        MusicDefaultSort = musicSort.GetInt32();
                    if (root.TryGetProperty("MusicRememberView", out var musicRememberView))
                        MusicRememberView = musicRememberView.GetBoolean();
                    if (root.TryGetProperty("MusicRememberSort", out var musicRememberSort))
                        MusicRememberSort = musicRememberSort.GetBoolean();
                    if (root.TryGetProperty("MusicFileOpenMode", out var musicFileOpenMode))
                        MusicFileOpenMode = musicFileOpenMode.GetInt32();
                    if (root.TryGetProperty("MusicVolume", out var musicVolume))
                        MusicVolume = Math.Clamp(musicVolume.GetDouble(), 0, 1);
                    if (root.TryGetProperty("MusicMuted", out var musicMuted))
                        MusicMuted = musicMuted.GetBoolean();
                    if (root.TryGetProperty("MusicOutputDeviceId", out var musicOutputDeviceId))
                        MusicOutputDeviceId = musicOutputDeviceId.GetString() ?? string.Empty;
                    if (root.TryGetProperty("VideoOutputDeviceId", out var videoOutputDeviceId))
                        VideoOutputDeviceId = videoOutputDeviceId.GetString() ?? string.Empty;
                    if (root.TryGetProperty("MusicUseNetworkLyricsSource", out var musicUseNetLyrics))
                        MusicUseNetworkLyricsSource = musicUseNetLyrics.GetBoolean();
                    if (root.TryGetProperty("MusicLyricsSourcePreference", out var lyricsSourcePreference))
                        MusicLyricsSourcePreference = lyricsSourcePreference.GetString() ?? "Auto";
                    if (root.TryGetProperty("PlaylistDefaultView", out var playlistView))
                        PlaylistDefaultView = playlistView.GetInt32();
                    if (root.TryGetProperty("PlaylistDefaultSort", out var playlistSort))
                        PlaylistDefaultSort = playlistSort.GetInt32();
                    if (root.TryGetProperty("PlaylistRememberView", out var playlistRememberView))
                        PlaylistRememberView = playlistRememberView.GetBoolean();
                    if (root.TryGetProperty("PlaylistRememberSort", out var playlistRememberSort))
                        PlaylistRememberSort = playlistRememberSort.GetBoolean();
                    if (root.TryGetProperty("ArtistDefaultView", out var artistView))
                        ArtistDefaultView = artistView.GetInt32();
                    if (root.TryGetProperty("ArtistDefaultSort", out var artistSort))
                        ArtistDefaultSort = artistSort.GetInt32();
                    if (root.TryGetProperty("ArtistRememberView", out var artistRememberView))
                        ArtistRememberView = artistRememberView.GetBoolean();
                    if (root.TryGetProperty("ArtistRememberSort", out var artistRememberSort))
                        ArtistRememberSort = artistRememberSort.GetBoolean();
                    if (root.TryGetProperty("AlbumDefaultView", out var albumView))
                        AlbumDefaultView = albumView.GetInt32();
                    if (root.TryGetProperty("AlbumDefaultSort", out var albumSort))
                        AlbumDefaultSort = albumSort.GetInt32();
                    if (root.TryGetProperty("AlbumRememberView", out var albumRememberView))
                        AlbumRememberView = albumRememberView.GetBoolean();
                    if (root.TryGetProperty("AlbumRememberSort", out var albumRememberSort))
                        AlbumRememberSort = albumRememberSort.GetBoolean();
                    if (root.TryGetProperty("FolderDefaultView", out var folderView))
                        FolderDefaultView = folderView.GetInt32();
                    if (root.TryGetProperty("FolderDefaultSort", out var folderSort))
                        FolderDefaultSort = folderSort.GetInt32();
                    if (root.TryGetProperty("FolderRememberView", out var folderRememberView))
                        FolderRememberView = folderRememberView.GetBoolean();
                    if (root.TryGetProperty("FolderRememberSort", out var folderRememberSort))
                        FolderRememberSort = folderRememberSort.GetBoolean();
                    if (root.TryGetProperty("VideoRefreshOnStartup", out var videoRefresh))
                        VideoRefreshOnStartup = videoRefresh.GetBoolean();
                    if (root.TryGetProperty("VideoDefaultView", out var videoView))
                        VideoDefaultView = videoView.GetInt32();
                    if (root.TryGetProperty("VideoDefaultSort", out var videoSort))
                        VideoDefaultSort = videoSort.GetInt32();
                    if (root.TryGetProperty("VideoRememberView", out var videoRememberView))
                        VideoRememberView = videoRememberView.GetBoolean();
                    if (root.TryGetProperty("VideoRememberSort", out var videoRememberSort))
                        VideoRememberSort = videoRememberSort.GetBoolean();
                    if (root.TryGetProperty("VideoDecoderBackend", out var videoDecoder))
                        VideoDecoderBackend = videoDecoder.GetString() ?? "FFmpeg";
                    if (root.TryGetProperty("VideoDecodeMode", out var videoDecodeMode))
                        VideoDecodeMode = videoDecodeMode.GetString() ?? "Auto";
                    if (root.TryGetProperty("VideoPlayerMode", out var videoPlayerMode))
                        VideoPlayerMode = videoPlayerMode.GetString() ?? "Normal";
                    if (root.TryGetProperty("RememberVideoPosition", out var rememberVideoPosition))
                        RememberVideoPosition = rememberVideoPosition.GetBoolean();
                    if (root.TryGetProperty("RememberCurrentVideoPosition", out var rememberCurrentVideoPosition))
                        RememberCurrentVideoPosition = rememberCurrentVideoPosition.GetBoolean();
                    if (root.TryGetProperty("AutoPlayNextVideo", out var autoPlayNextVideo))
                        AutoPlayNextVideo = autoPlayNextVideo.GetBoolean();
                    if (root.TryGetProperty("AutoPlayVideo", out var autoPlayVideo))
                        AutoPlayVideo = autoPlayVideo.GetBoolean();
                    if (root.TryGetProperty("BackgroundPlayVideo", out var backgroundPlayVideo))
                        BackgroundPlayVideo = backgroundPlayVideo.GetBoolean();
                    if (root.TryGetProperty("VideoSuperResolutionEnabled", out var srEnabled))
                        VideoSuperResolutionEnabled = srEnabled.GetBoolean();
                    if (root.TryGetProperty("VideoSuperResolutionQuality", out var srQuality))
                        // 旧版档位向后兼容映射：旧"Speed"（S 模型）→ 中档；旧"Quality"（原实现误用 VL 模型）→ 高档（M 模型，保留高质量意图）
                        VideoSuperResolutionQuality = NormalizeSuperResolutionQuality(srQuality.GetString() ?? "Medium");
                    if (root.TryGetProperty("VideoSuperResolutionModel", out var srModel))
                        VideoSuperResolutionModel = srModel.GetString() ?? "anime4k";
                    if (root.TryGetProperty("VideoMotionCompensationEnabled", out var mcEnabled))
                        VideoMotionCompensationEnabled = mcEnabled.GetBoolean();
                    if (root.TryGetProperty("VideoMotionCompensationMode", out var mcMode))
                        VideoMotionCompensationMode = NormalizeMotionCompensationMode(mcMode.GetString());
                    if (root.TryGetProperty("GalleryRefreshOnStartup", out var galleryRefresh))
                        GalleryRefreshOnStartup = galleryRefresh.GetBoolean();
                    if (root.TryGetProperty("GalleryDefaultView", out var galleryView))
                        GalleryDefaultView = galleryView.GetInt32();
                    if (root.TryGetProperty("GalleryDefaultSort", out var gallerySort))
                        GalleryDefaultSort = gallerySort.GetInt32();
                    if (root.TryGetProperty("GalleryRememberView", out var galleryRememberView))
                        GalleryRememberView = galleryRememberView.GetBoolean();
                    if (root.TryGetProperty("GalleryRememberSort", out var galleryRememberSort))
                        GalleryRememberSort = galleryRememberSort.GetBoolean();
                    if (root.TryGetProperty("DeleteToRecycleBin", out var deleteToRecycleBin))
                        DeleteToRecycleBin = deleteToRecycleBin.GetBoolean();
                    if (root.TryGetProperty("GalleryThumbnailHeight", out var galleryThumbHeight))
                        GalleryThumbnailHeight = Math.Clamp(galleryThumbHeight.GetInt32(), 100, 240);
                    if (root.TryGetProperty("GalleryThumbnailSize", out var galleryThumbSize))
                        GalleryThumbnailSize = (uint)Math.Clamp(galleryThumbSize.GetInt32(), 128, 512);
                    if (root.TryGetProperty("GalleryPreloadThumbnails", out var galleryPreload))
                        GalleryPreloadThumbnails = galleryPreload.GetBoolean();
                    if (root.TryGetProperty("GalleryShowImageInfo", out var galleryShowInfo))
                        GalleryShowImageInfo = galleryShowInfo.GetBoolean();
                    if (root.TryGetProperty("GalleryViewerDoubleClickAction", out var galleryDoubleClick))
                        GalleryViewerDoubleClickAction = Math.Clamp(galleryDoubleClick.GetInt32(), 0, 2);
                    if (root.TryGetProperty("GalleryAutoFullScreen", out var galleryAutoFullScreen))
                        GalleryAutoFullScreen = galleryAutoFullScreen.GetBoolean();
                    if (root.TryGetProperty("GalleryViewerBackground", out var galleryViewerBg))
                        GalleryViewerBackground = Math.Clamp(galleryViewerBg.GetInt32(), 0, 2);
                    if (root.TryGetProperty("GallerySlideAnimation", out var gallerySlideAnimation))
                        GallerySlideAnimation = gallerySlideAnimation.GetBoolean();
                    if (root.TryGetProperty("GalleryHideMiniPlayerOnEnter", out var galleryHideMiniPlayer))
                        GalleryHideMiniPlayerOnEnter = galleryHideMiniPlayer.GetBoolean();
                    if (root.TryGetProperty("LastVideoPath", out var lastVideoPath))
                        LastVideoPath = lastVideoPath.GetString() ?? string.Empty;
                    if (root.TryGetProperty("LastImagePath", out var lastImagePath))
                        LastImagePath = lastImagePath.GetString() ?? string.Empty;
                    if (root.TryGetProperty("LastMusicPath", out var lastMusicPath))
                        LastMusicPath = lastMusicPath.GetString() ?? string.Empty;
                    if (root.TryGetProperty("LastVideoTime", out var lastVideoTime))
                        LastVideoTime = lastVideoTime.GetDateTime();
                    if (root.TryGetProperty("LastImageTime", out var lastImageTime))
                        LastImageTime = lastImageTime.GetDateTime();
                    if (root.TryGetProperty("LastMusicTime", out var lastMusicTime))
                        LastMusicTime = lastMusicTime.GetDateTime();
                    if (root.TryGetProperty("Win2DGpuPreference", out var win2dGpuPreference))
                        Win2DGpuPreference = win2dGpuPreference.GetString() ?? "Auto";
                    if (root.TryGetProperty("Win2DGpuAdapterLuid", out var win2dGpuLuid))
                        Win2DGpuAdapterLuid = win2dGpuLuid.GetString() ?? "";
                    if (root.TryGetProperty("CustomAccentColorA", out var cca) &&
                        root.TryGetProperty("CustomAccentColorR", out var ccr) &&
                        root.TryGetProperty("CustomAccentColorG", out var ccg) &&
                        root.TryGetProperty("CustomAccentColorB", out var ccb))
                        CustomAccentColor = Windows.UI.Color.FromArgb(
                            cca.GetByte(), ccr.GetByte(), ccg.GetByte(), ccb.GetByte());
                }
                catch
                {
                    // 如果解析失败，保留当前内存中的默认值
                }
            }

            /// <summary>
            /// 规范化超分质量档位值（读取设置时调用）。
            /// 支持四档：Low（低档，VL 模型）/ Medium（中档，S 模型）/ High（高档，M 模型）/ Ultra（超高档，UL 模型）。
            /// 兼容旧版两档值："Speed" → Medium（旧速度档 = S 模型）、"Quality" → High（旧质量档，保留高质量意图），
            /// 未知值一律回退为中档。
            /// </summary>
            private static string NormalizeSuperResolutionQuality(string? value)
                => value switch
                {
                    "Low" or "Medium" or "High" or "Ultra" => value,
                    "Speed" => "Medium",
                    "Quality" => "High",
                    _ => "Medium"
                };

            /// <summary>
            /// 规范化运动补偿模式值（读取 settings.json 时的防御），未知值一律回退为 MVT_LQ。
            /// </summary>
            private static string NormalizeMotionCompensationMode(string? value)
                => value switch
                {
                    "MVT_LQ" or "MVT_STD" or "SVP_LQ" or "SVP_PRO" => value,
                    _ => "MVT_LQ"
                };

            public static void Save()
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    JsonNode? node = null;
                    if (File.Exists(FilePath))
                    {
                        try { node = JsonNode.Parse(File.ReadAllText(FilePath)); } catch { }
                    }
                    if (node == null) node = new JsonObject();

                    node["UseWindowsTheme"] = UseWindowsTheme;
                    node["SelectedColorIndex"] = SelectedColorIndex;
                    node["BackdropType"] = BackdropType;
                    node["KeepContentMica"] = KeepContentMica;
                    node["ThemeMode"] = ThemeMode;
                    node["RememberWindowSize"] = RememberWindowSize;
                    node["WindowWidth"] = WindowWidth;
                    node["WindowHeight"] = WindowHeight;
                    node["RememberWindowPosition"] = RememberWindowPosition;
                    node["HasWindowPosition"] = HasWindowPosition;
                    node["WindowX"] = WindowX;
                    node["WindowY"] = WindowY;
                    node["AllowMultiInstance"] = AllowMultiInstance;
                    node["ConfirmDialogReverse"] = ConfirmDialogReverse;
                    node["MusicRefreshOnStartup"] = MusicRefreshOnStartup;
                    node["MusicDefaultView"] = MusicDefaultView;
                    node["MusicDefaultSort"] = MusicDefaultSort;
                    node["MusicRememberView"] = MusicRememberView;
                    node["MusicRememberSort"] = MusicRememberSort;
                    node["MusicFileOpenMode"] = MusicFileOpenMode;
                    node["MusicVolume"] = MusicVolume;
                    node["MusicMuted"] = MusicMuted;
                    node["MusicOutputDeviceId"] = MusicOutputDeviceId;
                    node["VideoOutputDeviceId"] = VideoOutputDeviceId;
                    node["MusicUseNetworkLyricsSource"] = MusicUseNetworkLyricsSource;
                    node["MusicLyricsSourcePreference"] = MusicLyricsSourcePreference;
                    node["PlaylistDefaultView"] = PlaylistDefaultView;
                    node["PlaylistDefaultSort"] = PlaylistDefaultSort;
                    node["PlaylistRememberView"] = PlaylistRememberView;
                    node["PlaylistRememberSort"] = PlaylistRememberSort;
                    node["ArtistDefaultView"] = ArtistDefaultView;
                    node["ArtistDefaultSort"] = ArtistDefaultSort;
                    node["ArtistRememberView"] = ArtistRememberView;
                    node["ArtistRememberSort"] = ArtistRememberSort;
                    node["AlbumDefaultView"] = AlbumDefaultView;
                    node["AlbumDefaultSort"] = AlbumDefaultSort;
                    node["AlbumRememberView"] = AlbumRememberView;
                    node["AlbumRememberSort"] = AlbumRememberSort;
                    node["FolderDefaultView"] = FolderDefaultView;
                    node["FolderDefaultSort"] = FolderDefaultSort;
                    node["FolderRememberView"] = FolderRememberView;
                    node["FolderRememberSort"] = FolderRememberSort;
                    node["VideoRefreshOnStartup"] = VideoRefreshOnStartup;
                    node["VideoDefaultView"] = VideoDefaultView;
                    node["VideoDefaultSort"] = VideoDefaultSort;
                    node["VideoRememberView"] = VideoRememberView;
                    node["VideoRememberSort"] = VideoRememberSort;
                    node["VideoDecoderBackend"] = VideoDecoderBackend;
                    node["VideoDecodeMode"] = VideoDecodeMode;
                    node["VideoPlayerMode"] = VideoPlayerMode;
                    node["RememberVideoPosition"] = RememberVideoPosition;
                    node["RememberCurrentVideoPosition"] = RememberCurrentVideoPosition;
                    node["AutoPlayNextVideo"] = AutoPlayNextVideo;
                    node["AutoPlayVideo"] = AutoPlayVideo;
                    node["BackgroundPlayVideo"] = BackgroundPlayVideo;
                    node["VideoSuperResolutionEnabled"] = VideoSuperResolutionEnabled;
                    node["VideoSuperResolutionQuality"] = VideoSuperResolutionQuality;
                    node["VideoSuperResolutionModel"] = VideoSuperResolutionModel;
                    node["VideoMotionCompensationEnabled"] = VideoMotionCompensationEnabled;
                    node["VideoMotionCompensationMode"] = VideoMotionCompensationMode;
                    node["GalleryRefreshOnStartup"] = GalleryRefreshOnStartup;
                    node["GalleryDefaultView"] = GalleryDefaultView;
                    node["GalleryDefaultSort"] = GalleryDefaultSort;
                    node["GalleryRememberView"] = GalleryRememberView;
                    node["GalleryRememberSort"] = GalleryRememberSort;
                    node["DeleteToRecycleBin"] = DeleteToRecycleBin;
                    node["GalleryThumbnailHeight"] = GalleryThumbnailHeight;
                    node["GalleryThumbnailSize"] = GalleryThumbnailSize;
                    node["GalleryPreloadThumbnails"] = GalleryPreloadThumbnails;
                    node["GalleryShowImageInfo"] = GalleryShowImageInfo;
                    node["GalleryViewerDoubleClickAction"] = GalleryViewerDoubleClickAction;
                    node["GalleryAutoFullScreen"] = GalleryAutoFullScreen;
                    node["GalleryViewerBackground"] = GalleryViewerBackground;
                    node["GallerySlideAnimation"] = GallerySlideAnimation;
                    node["GalleryHideMiniPlayerOnEnter"] = GalleryHideMiniPlayerOnEnter;
                    node["LastVideoPath"] = LastVideoPath;
                    node["LastImagePath"] = LastImagePath;
                    node["LastMusicPath"] = LastMusicPath;
                    node["LastVideoTime"] = LastVideoTime;
                    node["LastImageTime"] = LastImageTime;
                    node["LastMusicTime"] = LastMusicTime;
                    node["Win2DGpuPreference"] = Win2DGpuPreference;
                    node["Win2DGpuAdapterLuid"] = Win2DGpuAdapterLuid;
                    node["CustomAccentColorA"] = CustomAccentColor.A;
                    node["CustomAccentColorR"] = CustomAccentColor.R;
                    node["CustomAccentColorG"] = CustomAccentColor.G;
                    node["CustomAccentColorB"] = CustomAccentColor.B;

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        PropertyNamingPolicy = null
                    };
                    File.WriteAllText(FilePath, node.ToJsonString(options));
                }
                catch
                {
                    // 静默失败，不影响应用运行
                }
            }
        }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            try { System.IO.File.AppendAllText(Program.StartupLogPath, $"{System.DateTime.Now:HH:mm:ss.fff} App 构造函数开始\r\n"); } catch { }
            InitializeComponent();
            Helpers.AppLogger.Initialize();
            Helpers.AppLogger.Info("日志系统初始化完成");
            this.UnhandledException += App_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            // ★ 崩溃日志增强：AppDomain 级未处理异常（覆盖非 UI 线程的托管崩溃），
            //   配合 WER LocalDumps 转储与启动自检，弥补"日志只记录到中断点"的痛点
            AppDomain.CurrentDomain.UnhandledException += AppDomain_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // ★ 崩溃日志增强：Fatal 始终记录完整堆栈（见 AppLogger），并确保立即落盘
            Helpers.AppLogger.Fatal(e.Exception, "全局未捕获异常/闪退崩溃");
            Helpers.AppLogger.FlushAndClose();
        }

        private static void AppDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception
                ?? new Exception($"非托管异常对象: {e.ExceptionObject}");
            Helpers.AppLogger.Fatal(ex, "AppDomain 未处理异常/闪退崩溃");
            Helpers.AppLogger.FlushAndClose();
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Helpers.AppLogger.Fatal(e.Exception, "未观测任务异常/闪退崩溃");
            Helpers.AppLogger.FlushAndClose();
            e.SetObserved();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 设置进程的 Application User Model ID (AUMID)
            // 未打包应用必须在启动时显式设置，否则 SMTC 的 SourceAppUserModelId 为空
            // 其他软件（如 BetterLyrics）依赖此标识来发现和识别播放源
            try { SetCurrentProcessExplicitAppUserModelID("SightoHear"); }
            catch { }

            // 先加载用户设置，多开检查依赖 AllowMultiInstance 的持久化值
            try { SettingsHelper.Load(); } catch { }

            // ★ 崩溃日志增强：配置 WER 崩溃转储（Windows 在任意崩溃时自动保存 MiniDump）
            //   并自检上次崩溃（读事件日志解析异常码/模块，写入日志 + DebugPage 展示）
            try { Services.CrashReportService.ConfigureLocalDumps(); } catch { }
            try { Services.CrashReportService.CheckAndLogPreviousCrash(); } catch { }

            // x86（32 位）平台不支持 libmpv（超分模式，仅 x64/ARM64 有 dll），
            // 启动时检测到已启用超分模式则自动回退为普通模式，避免播放器初始化崩溃
            if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X86
                && SettingsHelper.VideoPlayerMode == "Mpv")
            {
                SettingsHelper.VideoPlayerMode = "Normal";
                SettingsHelper.Save();
                Helpers.AppLogger.Warning("检测到 x86（32 位）平台：libmpv 不支持该架构，libmpv已自动回退为 media player");
            }

            // ── 文件激活服务初始化 ──
            // 必须先于多开检查，这样第二个实例才能将文件路径传递给主实例
            try { FileActivationService.Initialize(); } catch { }
            try { FileActivationService.ParseCommandLineArgs(); } catch { }

            // ── 多开检查 ──
            // 所有实例都尝试创建命名的互斥量，用于检测是否已有实例在运行
            bool createdNew;
            InstanceMutex = new System.Threading.Mutex(true, "SightoHear_SingleInstance_Mutex", out createdNew);
            if (!createdNew && !SettingsHelper.AllowMultiInstance)
            {
                // 已有实例在运行，且多开设置已关闭
                // 如果有待处理的文件，先通过命名管道发送给主实例
                if (FileActivationService.HasPendingFile)
                {
                    try { FileActivationService.SendToRunningInstance(FileActivationService.PendingFilePath!); } catch { }
                }

                // 将焦点切换到第一个实例的窗口
                try
                {
                    var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                    var firstInstance = System.Diagnostics.Process.GetProcessesByName("SightoHear")
                        .Where(p => p.Id != currentProcess.Id)
                        .OrderBy(p => p.StartTime)
                        .FirstOrDefault();

                    if (firstInstance != null)
                    {
                        uint targetPid = (uint)firstInstance.Id;
                        IntPtr targetHwnd = IntPtr.Zero;

                        EnumWindows((hWnd, lParam) =>
                        {
                            GetWindowThreadProcessId(hWnd, out uint pid);
                            if (pid == targetPid)
                            {
                                // 查找无所有者、可见的顶层窗口
                                if (GetWindow(hWnd, GW_OWNER) == IntPtr.Zero && IsWindowVisible(hWnd))
                                {
                                    targetHwnd = hWnd;
                                    return false;
                                }
                            }
                            return true;
                        }, IntPtr.Zero);

                        if (targetHwnd != IntPtr.Zero)
                        {
                            // 如果窗口最小化，先恢复
                            if (IsIconic(targetHwnd))
                            {
                                ShowWindow(targetHwnd, SW_SHOW);
                            }
                            SetForegroundWindow(targetHwnd);
                        }
                    }
                }
                catch { }

                AppLogger.Info("检测到已有实例在运行，且多开设置已关闭，已切换至第一个窗口");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                return;
            }
            // InstanceMutex 静态保持引用，直到进程退出

            // ── 主实例：启动命名管道服务器（接收后续文件打开请求） ──
            try { FileActivationService.StartNamedPipeServer(); } catch (Exception ex) { Helpers.AppLogger.Error(ex, "启动命名管道服务器失败"); }
            // ── 注册文件关联（确保 exe 路径变更后仍然有效） ──
            try { FileActivationService.RegisterFileAssociations(); } catch (Exception ex) { Helpers.AppLogger.Error(ex, "注册文件关联失败"); }

            // ── 启动计时器 ──
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Helpers.AppLogger.Info("========== 应用启动开始 ==========");

            // ============================================================
            // 步骤一：快速加载设置（获取主题模式）
            // ============================================================
            sw.Restart();
            Helpers.AppLogger.Info("[步骤1/6] 开始初始化媒体库默认设置...");
            try { MediaScanner.InitializeDefaultLibrarySettings(); Helpers.AppLogger.Info($"[步骤1/6] MediaScanner.InitializeDefaultLibrarySettings 完成 ({sw.ElapsedMilliseconds}ms)"); }
            catch (Exception ex) { Helpers.AppLogger.Error(ex, "[步骤1/6] MediaScanner.InitializeDefaultLibrarySettings 失败"); }

            sw.Restart();
            Helpers.AppLogger.Info("[步骤1/6] 开始加载用户设置...");
            try { SettingsHelper.Load(); Helpers.AppLogger.Info($"[步骤1/6] SettingsHelper.Load 完成 ({sw.ElapsedMilliseconds}ms)"); }
            catch (Exception ex) { Helpers.AppLogger.Error(ex, "[步骤1/6] SettingsHelper.Load 失败"); }

            sw.Restart();
            Helpers.AppLogger.Info("[步骤1/6] 开始应用播放器设置...");
            try { MusicPlayback.ApplySettings(); Helpers.AppLogger.Info($"[步骤1/6] MusicPlayback.ApplySettings 完成 ({sw.ElapsedMilliseconds}ms)"); }
            catch (Exception ex) { Helpers.AppLogger.Error(ex, "[步骤1/6] MusicPlayback.ApplySettings 失败"); }

            // 恢复用户保存的单独输出设备。
            // 注意：MusicPlayback 静态属性在 App 类型初始化时即被创建，早于本方法中的
            // SettingsHelper.Load()，因此其构造函数中无法读到已保存的设备 ID（当时还是
            // 默认空字符串，会应用到"跟随系统设备"）。设置加载完成后必须在此重新应用，
            // 否则重启后实际播放会回退到跟随系统设备，而设置界面仍显示"特定设备"。
            sw.Restart();
            Helpers.AppLogger.Info("[步骤1/6] 开始应用音频输出设备...");
            try { _ = MusicPlayback.ApplyAudioDeviceAsync(App.SettingsHelper.MusicOutputDeviceId); Helpers.AppLogger.Info($"[步骤1/6] ApplyAudioDeviceAsync 已触发 ({sw.ElapsedMilliseconds}ms)"); }
            catch (Exception ex) { Helpers.AppLogger.Error(ex, "[步骤1/6] 应用音频输出设备失败"); }

            sw.Restart();
            Helpers.AppLogger.Info("[步骤1/6] 开始加载调试设置...");
            try { LoadDebugSettings(); Helpers.AppLogger.Info($"[步骤1/6] LoadDebugSettings 完成 ({sw.ElapsedMilliseconds}ms)"); }
            catch (Exception ex) { Helpers.AppLogger.Error(ex, "[步骤1/6] LoadDebugSettings 失败"); }

            sw.Restart();
            Helpers.AppLogger.Info("[步骤1/6] 开始初始化 Win2D GPU 设备...");
            try { Win2DDeviceManager.Initialize(); Helpers.AppLogger.Info($"[步骤1/6] Win2DDeviceManager.Initialize 完成 ({sw.ElapsedMilliseconds}ms)"); }
            catch (Exception ex) { Helpers.AppLogger.Error(ex, "[步骤1/6] Win2DDeviceManager.Initialize 失败"); }
            Helpers.AppLogger.Info("应用启动");

            // ============================================================
            // 步骤二：主题色
            // ============================================================
            sw.Restart();
            int accentIndex = SettingsHelper.SelectedColorIndex;
            var accent = SettingsHelper.UseWindowsTheme
                ? GetSystemAccentColor()
                : accentIndex >= 0 && accentIndex < AccentColors.Length
                    ? AccentColors[accentIndex]
                    : AccentColors[1];
            try { ApplyGlobalAccentColor(accent); Helpers.AppLogger.Info($"[步骤2/6] 主题色应用完成 ({sw.ElapsedMilliseconds}ms)"); }
            catch (Exception ex) { Helpers.AppLogger.Error(ex, "[步骤2/6] 主题色应用失败"); }

            // ============================================================
            // 步骤三：创建主窗口（此时 HWND 已创建但窗口未显示）
            // ============================================================
            sw.Restart();
            Helpers.AppLogger.Info("[步骤3/6] 开始创建 MainWindow...");
            try
            {
                MainWindow = new MainWindow();
                Helpers.AppLogger.Info($"[步骤3/6] MainWindow 构造函数完成 ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                Helpers.AppLogger.Fatal(ex, "[步骤3/6] MainWindow 创建失败");
                throw;
            }

            if (MainWindow != null)
            {
                MainWindow.Closed += (s, e) =>
                {
                    Helpers.AppLogger.Info("应用退出");
                    Win2DDeviceManager.Shutdown();
                    Helpers.AppLogger.FlushAndClose();
                };
            }

            // ============================================================
            // 步骤四：在窗口显示前设置 XAML 启动画面遮罩（窗口隐藏，不闪白屏）
            // ============================================================
            sw.Restart();
            if (MainWindow is SightoHear.MainWindow mw)
            {
                mw.ShowSplash(SettingsHelper.ThemeMode);
                Helpers.AppLogger.Info($"[步骤4/6] 启动画面遮罩已就绪 ({sw.ElapsedMilliseconds}ms)");
            }
            else
            {
                Helpers.AppLogger.Info("[步骤4/6] MainWindow 类型异常，跳过启动画面");
            }

            // ============================================================
            // 步骤五：窗口暂不显示。通过 DispatcherQueue 调度，等 WinUI
            // 事件循环启动、Compositor 就绪后再显示窗口（防止提前刷白屏）
            // ============================================================
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                if (MainWindow is SightoHear.MainWindow mw2)
                    mw2.ShowWindowNow();
                Helpers.AppLogger.Info($"[步骤5/6] 主窗口激活完成 (排队后耗时 {sw2.ElapsedMilliseconds}ms)");
            });

            // ============================================================
            // 步骤六：splash 的停留与渐隐由 MainWindow.ShowWindowNow 内部计时
            // （窗口出现后才启动，避免 Loaded 过早触发导致计时偏移）
            // ============================================================
            // 兜底超时：15 秒后强制隐藏 splash（极端异常保护）
            var dqSplash = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _ = Task.Run(async () =>
            {
                await Task.Delay(15000);
                try
                {
                    if (MainWindow is SightoHear.MainWindow mw3)
                    {
                        dqSplash.TryEnqueue(() => _ = mw3.HideSplashAsync(150));
                        Helpers.AppLogger.Info("[步骤6/6] 启动画面超时强制关闭");
                    }
                }
                catch { }
            });

            Helpers.AppLogger.Info($"当前主题: {SettingsHelper.ThemeMode}, 背景: {SettingsHelper.BackdropType}");

            // ── 如果以打开方式启动，在 Splash 背后直接准备播放器 ──
            // 利用 XAML 中重新排列的 Z-order（SplashOverlay 在 PlayerOverlay 之上），
            // 在 ShowWindowNow 之前以 Normal 优先级设置播放器覆盖层到 Y=0（正常位置），
            // 此时窗口尚未显示或 Splash 覆盖在播放器之上，用户不可见。
            // Splash 渐隐后播放器自然露出，用户看到的是 Splash → 播放器的无缝过渡。
            // 不再需要等到 SplashHidden 事件，播放器在窗口出现前就已经就位。
            //
            // 实现方式：借助 DispatcherQueue 优先级排序
            //   1. Normal：ProcessFileForStartupAsync（准备 MediaItem + 导航播放器）
            //   2. Low：    ShowWindowNow（窗口出现 + Splash 展示）
            // 即使第 1 步因 Enrich 异步 I/O 而暂缓，Splash 也会覆盖过渡期。
            if (FileActivationService.HasPendingFile)
            {
                string pendingFile = FileActivationService.PendingFilePath!;
                if (MainWindow is SightoHear.MainWindow mainWnd)
                {
                    dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, async () =>
                    {
                        try
                        {
                            await FileActivationService.ProcessFileForStartupAsync(pendingFile, animate: false);
                            Helpers.AppLogger.Info($"[文件激活] Splash 背后播放器已准备就绪: {pendingFile}");
                        }
                        catch (Exception ex)
                        {
                            Helpers.AppLogger.Error(ex, "在 Splash 背后准备播放器失败");
                        }
                    });
                    Helpers.AppLogger.Info($"[文件激活] 已在 Splash 背后排队准备播放器: {pendingFile}");
                }
            }

            // 每次启动都刷新三类媒体库；配置缺失时上方会先重建默认路径。
            _ = Task.Run(async () =>
            {
                try
                {
                    if (SettingsHelper.VideoRefreshOnStartup)
                        await MediaScanner.RefreshLibraryAsync("Video");
                    if (SettingsHelper.MusicRefreshOnStartup)
                        await MediaScanner.RefreshLibraryAsync("Music");
                    if (SettingsHelper.GalleryRefreshOnStartup)
                        await MediaScanner.RefreshLibraryAsync("Image");
                }
                catch (Exception ex)
                {
                    Helpers.AppLogger.Error(ex, "启动刷新媒体库失败");
                }
            });

            Helpers.AppLogger.Info($"========== 应用启动 OnLaunched 完成，总耗时 {sw.ElapsedMilliseconds}ms ==========");
        }

        private void LoadDebugSettings()
        {
            try
            {
                var settingsPath = IoPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SightoHear", "settings.json");
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    var node = JsonNode.Parse(json);
                    if (node?["DebugSettings"] is JsonObject debug)
                    {
                        Helpers.AppLogger.IsEnabled = debug["LogEnabled"]?.GetValue<bool>() ?? false;
                        Helpers.AppLogger.CurrentLevel = (Helpers.LogLevel)(debug["LogLevel"]?.GetValue<int>() ?? 2);
            Helpers.AppLogger.ProtectSensitiveInfo = debug["ProtectSensitive"]?.GetValue<bool>() ?? true;
            Helpers.AppLogger.IsDevMode = debug["DevMode"]?.GetValue<bool>() ?? false;
            Helpers.AppLogger.UpdateLevel();

                        bool autoClean = debug["AutoClean"]?.GetValue<bool>() ?? true;
                        if (autoClean)
                        {
                            int cleanDays = 7;
                            try { cleanDays = (int)(debug["CleanDays"]?.GetValue<double>() ?? 7); } catch { }
                            Helpers.AppLogger.CleanupOldLogs(cleanDays);
                        }

                        // ── Win2D 性能监测悬浮窗设置（供 MainWindow 构造时读取）──
                        Win2DPerformanceHud.IsEnabled = debug["Win2DPerfEnabled"]?.GetValue<bool>() ?? false;
                        Win2DPerformanceHud.ShowFps = debug["Win2DShowFps"]?.GetValue<bool>() ?? true;
                        Win2DPerformanceHud.ShowAvgFps = debug["Win2DShowAvgFps"]?.GetValue<bool>() ?? false;
                        Win2DPerformanceHud.ShowFrameTime = debug["Win2DShowFrameTime"]?.GetValue<bool>() ?? false;
                        Win2DPerformanceHud.ShowUpdateTime = debug["Win2DShowUpdateTime"]?.GetValue<bool>() ?? false;
                        Win2DPerformanceHud.ShowDrawTime = debug["Win2DShowDrawTime"]?.GetValue<bool>() ?? false;
                        Win2DPerformanceHud.ShowFrameJitter = debug["Win2DShowFrameJitter"]?.GetValue<bool>() ?? false;
                        Win2DPerformanceHud.ShowDroppedFrames = debug["Win2DShowDroppedFrames"]?.GetValue<bool>() ?? false;
                        Win2DPerformanceHud.ShowMemory = debug["Win2DShowMemory"]?.GetValue<bool>() ?? false;
                        Win2DPerformanceHud.ShowResolution = debug["Win2DShowResolution"]?.GetValue<bool>() ?? false;
                        Win2DPerformanceHud.ShowGpuMode = debug["Win2DShowGpuMode"]?.GetValue<bool>() ?? false;
                    }
                }
            }
            catch { }
        }
    }
}
