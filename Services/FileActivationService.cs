using SightoHear.Helpers;
using SightoHear.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace SightoHear.Services
{
    /// <summary>
    /// 文件激活服务：处理命令行参数（从文件打开）、命名管道 IPC（单实例通信）、文件关联注册。
    /// 用于实现 Windows "打开方式" 功能。
    /// </summary>
    public static class FileActivationService
    {
        // 命名管道名称，用于单实例间的文件路径传递
        private const string PipeName = "SightoHear_FileActivation";

        // 文件扩展名 → 媒体类型映射（与 MediaScanner 保持一致）
        private static readonly Dictionary<string, string> MediaTypeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // 音乐文件
            { ".mp3", "Music" }, { ".flac", "Music" }, { ".wav", "Music" },
            { ".aac", "Music" }, { ".m4a", "Music" }, { ".ogg", "Music" },
            { ".wma", "Music" }, { ".opus", "Music" },
            // 视频文件
            { ".mp4", "Video" }, { ".mkv", "Video" }, { ".avi", "Video" },
            { ".mov", "Video" }, { ".wmv", "Video" }, { ".flv", "Video" },
            { ".webm", "Video" }, { ".m4v", "Video" }, { ".mpg", "Video" },
            { ".mpeg", "Video" }, { ".ts", "Video" }, { ".m2ts", "Video" },
            // 图片文件
            { ".jpg", "Image" }, { ".jpeg", "Image" }, { ".png", "Image" },
            { ".gif", "Image" }, { ".bmp", "Image" }, { ".webp", "Image" },
            { ".tiff", "Image" }, { ".heic", "Image" }, { ".raw", "Image" },
            { ".cr2", "Image" }, { ".nef", "Image" }
        };

        /// <summary>
        /// 获取文件路径对应的媒体类型（"Music" / "Video" / "Image"），
        /// 不支持的类型返回 "Unknown"。
        /// </summary>
        public static string GetMediaType(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return "Unknown";
            string ext = Path.GetExtension(filePath);
            return MediaTypeExtensions.TryGetValue(ext, out string? mediaType) ? mediaType : "Unknown";
        }

        // 从命令行参数解析出的待处理文件路径
        public static string? PendingFilePath { get; private set; }

        // 是否有待处理的文件
        public static bool HasPendingFile =>
            !string.IsNullOrEmpty(PendingFilePath) && File.Exists(PendingFilePath);

        // 防重入锁：同一时刻只处理一个文件激活请求，防止并发导致状态混乱
        private static readonly object _processLock = new();
        private static bool _isProcessing;
        // 待处理的队列：如果正在处理时收到新请求，排队等待
        private static readonly Queue<string> _pendingQueue = new();

        private static DispatcherQueue? _uiDispatcher;

        /// <summary>
        /// 初始化：设置 UI Dispatcher 引用（必须在 UI 线程调用）
        /// </summary>
        public static void Initialize()
        {
            _uiDispatcher = DispatcherQueue.GetForCurrentThread();
        }

        /// <summary>
        /// 解析命令行参数，提取文件路径
        /// </summary>
        public static void ParseCommandLineArgs()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                // args[0] 是可执行文件路径，从 args[1] 开始是传入的文件路径
                for (int i = 1; i < args.Length; i++)
                {
                    string path = args[i].Trim('"');
                    if (File.Exists(path) && IsSupportedFile(path))
                    {
                        PendingFilePath = Path.GetFullPath(path);
                        AppLogger.Info($"[文件激活] 检测到命令行文件路径: {PendingFilePath}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "[文件激活] 解析命令行参数失败");
            }
        }

        /// <summary>
        /// 判断文件扩展名是否支持（包括音乐、视频、图片）
        /// </summary>
        public static bool IsSupportedFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;
            string ext = Path.GetExtension(filePath);
            return MediaTypeExtensions.ContainsKey(ext);
        }

        /// <summary>
        /// 从文件路径创建 MediaItem，优先使用媒体库缓存的完整元数据。
        /// </summary>
        public static MediaItem CreateMediaItemFromFilePath(string filePath)
        {
            // ★ 优先从已扫描的媒体库缓存中查找完整 MediaItem（包含 Artist/Album/Duration 等元数据）
            try
            {
                var cached = MusicDataCache.AllMusic?.FirstOrDefault(
                    m => m.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                if (cached != null)
                {
                    AppLogger.Info($"[文件激活] 从媒体库缓存找到已有元数据: {filePath}");
                    return cached;
                }
            }
            catch { /* 缓存未就绪时忽略，走新建路径 */ }

            // ★ 缓存中不存在，创建基础 MediaItem，后续补全元数据
            var fileInfo = new FileInfo(filePath);
            return new MediaItem
            {
                FilePath = filePath,
                FileName = fileInfo.Name,
                Title = Path.GetFileNameWithoutExtension(filePath),
                MediaType = "Music",
                FileSize = fileInfo.Length,
                DateCreated = fileInfo.CreationTime,
                DateModified = fileInfo.LastWriteTime,
                DateScanned = DateTime.Now
            };
        }

        /// <summary>
        /// 补全 MediaItem 的音乐元数据（Artist、Album、Duration 等），
        /// 从文件自身读取，效果与媒体库扫描一致。
        /// </summary>
        private static async Task EnrichMediaItemAsync(MediaItem item)
        {
            // 如果已经有完整元数据（来自缓存），跳过
            if (item.MusicMetadataScanned)
                return;

            try
            {
                // 将当前 item 放入单元素列表，复用 MediaScanner 的批量元数据提取方法
                await MediaScanner.EnrichMusicMetadataAsync(new[] { item }, onlyUnscanned: true);
                AppLogger.Info($"[文件激活] 已补全音乐元数据: {item.FilePath}");
            }
            catch (Exception ex)
            {
                // 元数据提取失败不影响播放，使用文件名作为标题回退
                AppLogger.Warning($"[文件激活] 提取元数据失败（将继续使用文件名）: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理文件激活入口（防重入 + 排队，避免连续打开时状态冲突）。
        /// 线程安全：自动切换到 UI 线程执行。
        /// </summary>
        public static async Task ProcessFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                AppLogger.Warning($"[文件激活] 文件不存在: {filePath}");
                return;
            }

            if (!IsSupportedFile(filePath))
            {
                AppLogger.Warning($"[文件激活] 不支持的文件类型: {filePath}");
                return;
            }

            // 确保在 UI 线程执行
            if (_uiDispatcher != null && !_uiDispatcher.HasThreadAccess)
            {
                var tcs = new TaskCompletionSource();
                // ★ 重要：TryEnqueue 的回调是 async void，内部必须完整 try-catch
                // 否则任何未捕获异常都会导致进程崩溃
                if (!_uiDispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        await ProcessFileWithQueueAsync(filePath);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(ex, $"[文件激活] UI 线程处理文件失败: {filePath}");
                    }
                    finally
                    {
                        tcs.TrySetResult();
                    }
                }))
                {
                    AppLogger.Warning("[文件激活] TryEnqueue 失败，dispatcher 可能已关闭");
                    tcs.TrySetResult();
                }
                await tcs.Task;
            }
            else
            {
                await ProcessFileWithQueueAsync(filePath);
            }
        }

        /// <summary>
        /// 带排队机制的处理核心：同一时刻只处理一个文件，后续请求排队等候。
        /// 防止用户在短时间内连续通过"打开方式"打开多个文件时，
        /// PlayAsync 和 Frame.Navigate 的并发调用导致状态冲突/崩溃。
        /// </summary>
        private static async Task ProcessFileWithQueueAsync(string filePath)
        {
            // 将文件路径加入队列
            lock (_processLock)
            {
                _pendingQueue.Enqueue(filePath);
            }

            // 如果已经在处理中，让队列中的下一个等待（当前任务结束时会自动处理下一个）
            bool shouldProcess;
            lock (_processLock)
            {
                shouldProcess = !_isProcessing;
                if (shouldProcess)
                    _isProcessing = true;
            }

            if (!shouldProcess)
                return;

            // 循环处理队列中的所有请求
            while (true)
            {
                string currentFile;
                lock (_processLock)
                {
                    if (_pendingQueue.Count == 0)
                    {
                        _isProcessing = false;
                        return;
                    }
                    currentFile = _pendingQueue.Dequeue();
                }

                try
                {
                    await ProcessSingleFileAsync(currentFile);
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"[文件激活] 处理文件失败: {currentFile}");
                }
            }
        }

        /// <summary>
        /// 在 UI 线程上处理单个文件：根据文件类型路由到对应的播放器。
        /// </summary>
        /// <remarks>
        /// 支持音乐、视频、图片三种媒体类型。
        /// 音乐：通过 PlayAsync 切换歌曲，若播放器覆盖层已激活则跳过导航（事件驱动 UI 更新）。
        /// 视频：停止音乐播放，导航到 VideoPlayerPage。
        /// 图片：停止音乐播放，导航到 ImageViewerPage。
        /// </remarks>
        private static async Task ProcessSingleFileAsync(string filePath)
        {
            AppLogger.Info($"[文件激活] 开始处理文件: {filePath}");

            // ① 判断媒体类型
            string mediaType = GetMediaType(filePath);
            if (mediaType == "Unknown")
            {
                AppLogger.Warning($"[文件激活] 不支持的媒体类型: {filePath}");
                return;
            }

            // ② 创建 MediaItem，标记正确的媒体类型
            MediaItem item = CreateMediaItemFromFilePath(filePath);
            item.MediaType = mediaType;

            // ③ 根据媒体类型分发到不同的处理路径
            switch (mediaType)
            {
                case "Music":
                    await ProcessMusicFileActivationAsync(item, filePath);
                    break;
                case "Video":
                    await ProcessVideoFileActivationAsync(item, filePath);
                    break;
                case "Image":
                    await ProcessImageFileActivationAsync(item, filePath);
                    break;
            }
        }

        /// <summary>
        /// 处理音乐文件激活：补全元数据 → 播放 → 按需导航到播放器
        /// </summary>
        private static async Task ProcessMusicFileActivationAsync(MediaItem item, string filePath)
        {
            // ① 补全音乐元数据（Artist/Album/Duration）
            await EnrichMediaItemAsync(item);

            // ② 清除外部播放（视频）状态，防止干扰
            if (App.MusicPlayback.HasExternalPlayback)
                App.MusicPlayback.ClearExternalPlayback();

            // ③ 播放音乐（PlayAsync 内部先停止当前播放，再播放新文件）
            await App.MusicPlayback.PlayAsync(item, new[] { item });

            // ④ 检查 MainWindow
            if (App.MainWindow is MainWindow mainWindow)
            {
                // 如果覆盖层已激活且当前页面就是 MusicPlayerPage，跳过导航
                if (mainWindow.IsPlayerOverlayActive &&
                    mainWindow.CurrentPlayerPageType == typeof(MusicPlayerPage))
                {
                    // ★ 无需重新导航：PlayAsync 已切换歌曲，
                    // MusicPlayerPage 的 CurrentItemChanged 事件监听器会自动更新 UI。
                    AppLogger.Info($"[文件激活] 音乐播放器已激活，直接切换歌曲: {filePath}");
                }
                else
                {
                    // 覆盖层未激活或显示的是其他页面，导航到音乐播放器
                    mainWindow.NavigateMainFrame(typeof(MusicPlayerPage), new MusicPlayerArgs
                    {
                        CurrentItem = item,
                        Playlist = new List<MediaItem> { item },
                        CurrentIndex = 0
                    });
                    AppLogger.Info($"[文件激活] 成功播放文件并导航到播放器: {filePath}");
                }
            }
            else
            {
                AppLogger.Warning("[文件激活] MainWindow 尚未就绪，无法导航到音乐播放器");
            }
        }

        /// <summary>
        /// 处理视频文件激活：停止音乐 → 导航到视频播放器
        /// </summary>
        private static Task ProcessVideoFileActivationAsync(MediaItem item, string filePath)
        {
            AppLogger.Info($"[文件激活] 处理视频文件激活: 文件={item.FileName}, 路径={filePath}");

            // ① 停止音乐播放并清除外部播放状态
            var hadExternalPlayback = App.MusicPlayback.HasExternalPlayback;
            App.MusicPlayback.StopPlayback();
            if (hadExternalPlayback)
            {
                App.MusicPlayback.ClearExternalPlayback();
                AppLogger.Info("[文件激活] 已清除之前的外部播放状态");
            }

            // ② 导航到视频播放器（VideoPlayerPage.OnNavigatedTo 会调用 LoadVideo 自动播放）
            if (App.MainWindow is MainWindow mainWindow)
            {
                AppLogger.Info("[文件激活] MainWindow 就绪，开始导航到 VideoPlayerPage");
                mainWindow.NavigateMainFrame(typeof(VideoPlayerPage), new VideoPlayerArgs
                {
                    Playlist = new List<MediaItem> { item },
                    StartIndex = 0
                });
                AppLogger.Info($"[文件激活] 成功打开视频播放器: {filePath}");
            }
            else
            {
                AppLogger.Warning("[文件激活] MainWindow 尚未就绪，无法导航到视频播放器");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 处理图片文件激活：停止音乐 → 导航到图片查看器
        /// </summary>
        private static Task ProcessImageFileActivationAsync(MediaItem item, string filePath)
        {
            // ① 停止音乐播放并清除外部播放状态
            App.MusicPlayback.StopPlayback();
            if (App.MusicPlayback.HasExternalPlayback)
                App.MusicPlayback.ClearExternalPlayback();

            // ② 导航到图片查看器（ImageViewerPage.OnNavigatedTo 会调用 LoadImage 显示图片）
            if (App.MainWindow is MainWindow mainWindow)
            {
                mainWindow.OpenImageViewer(new ImageViewerArgs
                {
                    Playlist = new List<MediaItem> { item },
                    StartIndex = 0
                });
                AppLogger.Info($"[文件激活] 成功打开图片查看器: {filePath}");
            }
            else
            {
                AppLogger.Warning("[文件激活] MainWindow 尚未就绪，无法导航到图片查看器");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 启动时处理文件：直接导航到对应类型的播放器，页面加载后自动播放/显示。
        /// 音乐：直接显示 MusicPlayerPage，Loaded 事件自动开始播放（不先调用 PlayAsync，
        ///       避免 MiniPlayer 在覆盖层出现前闪烁）。
        /// 视频：直接显示 VideoPlayerPage，OnNavigatedTo 调用 LoadVideo（AutoPlay=true）。
        /// 图片：直接显示 ImageViewerPage，OnNavigatedTo 调用 LoadImage。
        /// </summary>
        /// <param name="filePath">媒体文件路径</param>
        /// <param name="animate">播放器覆盖层是否使用滑入动画。启动时为 false（无动画，由 Splash 过渡）</param>
        public static async Task ProcessFileForStartupAsync(string filePath, bool animate = false)
        {
            AppLogger.Info($"[文件激活] 启动时直接处理文件: {filePath}, animate={animate}");

            // ① 判断媒体类型
            string mediaType = GetMediaType(filePath);
            if (mediaType == "Unknown")
            {
                AppLogger.Warning($"[文件激活] 启动时遇到不支持的媒体类型: {filePath}");
                return;
            }

            // ② 创建 MediaItem，标记正确的媒体类型
            MediaItem item = CreateMediaItemFromFilePath(filePath);
            item.MediaType = mediaType;

            // ③ 检查 MainWindow 是否就绪
            if (App.MainWindow is not MainWindow mainWindow)
            {
                AppLogger.Warning("[文件激活] MainWindow 尚未就绪，无法导航到播放器页面");
                return;
            }

            // ④ 根据媒体类型导航到对应的播放器页面
            switch (mediaType)
            {
                case "Music":
                    // 补全音乐元数据（Artist/Album/Duration）
                    await EnrichMediaItemAsync(item);
                    // 导航到音乐播放器，MusicPlayerPage.Loaded 自动播放
                    mainWindow.NavigateMainFrame(typeof(MusicPlayerPage), new MusicPlayerArgs
                    {
                        CurrentItem = item,
                        Playlist = new List<MediaItem> { item },
                        CurrentIndex = 0
                    }, animate: animate);
                    AppLogger.Info($"[文件激活] 启动时成功导航到音乐播放器: {filePath}");
                    break;

                case "Video":
                    // 导航到视频播放器，VideoPlayerPage.OnNavigatedTo 调用 LoadVideo（AutoPlay=true）
                    mainWindow.NavigateMainFrame(typeof(VideoPlayerPage), new VideoPlayerArgs
                    {
                        Playlist = new List<MediaItem> { item },
                        StartIndex = 0
                    }, animate: animate);
                    AppLogger.Info($"[文件激活] 启动时成功导航到视频播放器: {filePath}");
                    break;

                case "Image":
                    // 导航到图片查看器，ImageViewerPage.OnNavigatedTo 调用 LoadImage
                    mainWindow.NavigateMainFrame(typeof(ImageViewerPage), new ImageViewerArgs
                    {
                        Playlist = new List<MediaItem> { item },
                        StartIndex = 0
                    }, animate: animate);
                    AppLogger.Info($"[文件激活] 启动时成功导航到图片查看器: {filePath}");
                    break;
            }
        }

        /// <summary>
        /// 将文件路径发送到已运行的主实例（通过命名管道 IPC）
        /// </summary>
        public static void SendToRunningInstance(string filePath)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(3000); // 3 秒超时
                if (client.IsConnected)
                {
                    byte[] buffer = Encoding.UTF8.GetBytes(filePath);
                    client.Write(buffer, 0, buffer.Length);
                    client.Flush();
                    AppLogger.Info($"[文件激活] 已通过命名管道发送文件路径到主实例: {filePath}");
                }
            }
            catch (TimeoutException)
            {
                AppLogger.Warning("[文件激活] 命名管道连接超时，主实例可能未响应");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "[文件激活] 通过命名管道发送文件路径失败");
            }
        }

        /// <summary>
        /// 启动命名管道服务器（在主实例的后台线程中运行）。
        /// 主实例启动后调用，循环等待新实例发送文件路径。
        /// </summary>
        public static void StartNamedPipeServer()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            1, // 最大实例数 1，串行处理
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        AppLogger.Info("[文件激活] 命名管道服务器已启动，等待连接...");
                        await server.WaitForConnectionAsync();

                        byte[] buffer = new byte[4096];
                        int bytesRead = server.Read(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            string filePath = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim('\0');
                            AppLogger.Info($"[文件激活] 命名管道收到文件路径: {filePath}");

                            // 切换到 UI 线程处理文件
                            // ★ 必须完整 try-catch：TryEnqueue 的回调是 async void，
                            // 任何未捕获异常都会直接导致进程崩溃
                            if (_uiDispatcher != null)
                            {
                                _uiDispatcher.TryEnqueue(async () =>
                                {
                                    try
                                    {
                                        await ProcessFileWithQueueAsync(filePath);
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLogger.Error(ex, $"[文件激活] 处理命名管道消息失败: {filePath}");
                                    }
                                });
                            }
                            else
                            {
                                AppLogger.Warning("[文件激活] _uiDispatcher 为 null，无法处理文件");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(ex, "[文件激活] 命名管道服务器异常，将重启");
                        await Task.Delay(1000);
                    }
                }
            });
        }

        /// <summary>
        /// 注册文件关联到当前用户注册表（HKCU）。
        /// 每次启动时调用，确保 exe 路径变更后仍然有效。
        /// </summary>
        public static void RegisterFileAssociations()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    AppLogger.Warning("[文件关联] 无法获取可执行文件路径，跳过注册");
                    return;
                }

                string command = $"\"{exePath}\" \"%1\"";

                // 注册 ProgID：SightoHear
                using var progIdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Classes\SightoHear");
                progIdKey.SetValue("", "SightoHear 媒体播放器");
                progIdKey.SetValue("AppUserModelID", "SightoHear");

                // 注册打开命令
                using var shellOpenKey = progIdKey.CreateSubKey(@"shell\open");
                shellOpenKey.SetValue("FriendlyAppName", "SightoHear");
                using var commandKey = shellOpenKey.CreateSubKey("command");
                commandKey.SetValue("", command);

                // 为每种支持的文件扩展名注册 OpenWithProgids（音乐、视频、图片）
                foreach (string ext in MediaTypeExtensions.Keys)
                {
                    using var extKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        $@"Software\Classes\{ext}\OpenWithProgids");
                    extKey.SetValue("SightoHear", Array.Empty<byte>()); // REG_NONE 类型
                }

                // 通知 Windows 刷新文件关联缓存
                NativeMethods.SHChangeNotify(
                    0x08000000, // SHCNE_ASSOCCHANGED
                    0x0000,     // SHCNF_IDLIST
                    IntPtr.Zero,
                    IntPtr.Zero);

                AppLogger.Info("[文件关联] 文件关联注册完成");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "[文件关联] 注册文件关联失败");
            }
        }

        /// <summary>
        /// 本地 Win32 P/Invoke 声明
        /// </summary>
        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("shell32.dll",
                CharSet = System.Runtime.InteropServices.CharSet.Auto)]
            public static extern void SHChangeNotify(
                uint wEventId,
                uint uFlags,
                IntPtr dwItem1,
                IntPtr dwItem2);
        }
    }
}
