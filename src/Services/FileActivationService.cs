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

        /// <summary>本应用在注册表中的 ProgID（"打开方式" / 默认应用均使用它）。</summary>
        public const string ProgId = "SightoHear";

        /// <summary>注册表中记录的显示名称。</summary>
        public const string DisplayName = "SightoHear";

        /// <summary>支持的全部扩展名（供默认应用设置页横幅判断使用）。</summary>
        public static IEnumerable<string> SupportedExtensions => MediaTypeExtensions.Keys;

        /// <summary>
        /// 注册文件关联到当前用户注册表（HKCU）。
        /// 每次启动时调用（幂等），首次启动即把本软件注入到右键"打开方式"列表中，
        /// 同时保证 exe 路径变更后关联仍然有效。
        /// </summary>
        /// <remarks>
        /// 让软件出现在资源管理器右键"打开方式"里，需要同时写三处：
        /// <list type="number">
        /// <item>HKCU\Software\Classes\Applications\&lt;exe&gt;：FriendlyAppName + SupportedTypes
        ///       （"打开方式"列表的主要数据源，SupportedTypes 决定该扩展名是否列出本程序）</item>
        /// <item>HKCU\Software\Classes\&lt;ext&gt;\OpenWithProgids：注册 ProgID（备用枚举路径）</item>
        /// <item>HKCU\Software\Classes\&lt;ext&gt;\OpenWithList：加入"打开方式"历史列表</item>
        /// </list>
        /// 另外写 HKCU\Software\RegisteredApplications + Capabilities，
        /// 让本软件出现在「Windows 设置 → 默认应用」的可选应用列表里。
        /// </remarks>
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
                string exeName = Path.GetFileName(exePath);

                // ① ProgID：SightoHear（shell\open\command + 图标 + 友好名）
                using (var progIdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{ProgId}"))
                {
                    progIdKey.SetValue("", "SightoHear 媒体播放器");
                    progIdKey.SetValue("AppUserModelID", "SightoHear");
                    using var shellOpenKey = progIdKey.CreateSubKey(@"shell\open");
                    shellOpenKey.SetValue("FriendlyAppName", DisplayName);
                    using var commandKey = shellOpenKey.CreateSubKey("command");
                    commandKey.SetValue("", command);
                    using var iconKey = progIdKey.CreateSubKey("DefaultIcon");
                    iconKey.SetValue("", $"\"{exePath}\",0");
                }

                // ② Applications\<exe>：右键"打开方式"菜单的直接数据源
                using (var appKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\Applications\{exeName}"))
                {
                    appKey.SetValue("FriendlyAppName", DisplayName);
                    appKey.SetValue("ApplicationCompany", "AvoidGuest");
                    using (var appIconKey = appKey.CreateSubKey("DefaultIcon"))
                        appIconKey.SetValue("", $"\"{exePath}\",0");
                    using (var appCommandKey = appKey.CreateSubKey(@"shell\open\command"))
                        appCommandKey.SetValue("", command);

                    // SupportedTypes 决定"打开方式"里本程序对哪些扩展名可见
                    using var supportedKey = appKey.CreateSubKey("SupportedTypes");
                    foreach (string ext in MediaTypeExtensions.Keys)
                        supportedKey.SetValue(ext, string.Empty);

                    // "打开方式"对话框中的程序友好名
                    using var openWithKey = appKey.CreateSubKey(@"shell\open");
                    openWithKey.SetValue("FriendlyAppName", DisplayName);
                }

                // ③ 每个扩展名：OpenWithProgids + OpenWithList
                foreach (string ext in MediaTypeExtensions.Keys)
                {
                    using var extKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        $@"Software\Classes\{ext}\OpenWithProgids");
                    extKey.SetValue(ProgId, Array.Empty<byte>()); // REG_NONE 类型

                    using var openWithListKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        $@"Software\Classes\{ext}\OpenWithList");
                    openWithListKey.SetValue(exeName, string.Empty);
                }

                // ④ 注册到「默认应用」列表（设置 → 默认应用 → 按应用选择默认值）
                using (var capabilitiesKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\SightoHear\Capabilities"))
                {
                    capabilitiesKey.SetValue("ApplicationName", DisplayName);
                    capabilitiesKey.SetValue("ApplicationDescription", "统合 Windows 的媒体增强体验");
                    using var fileAssocKey = capabilitiesKey.CreateSubKey("FileAssociations");
                    foreach (string ext in MediaTypeExtensions.Keys)
                        fileAssocKey.SetValue(ext, ProgId);
                }

                using (var registeredAppsKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\RegisteredApplications"))
                {
                    registeredAppsKey.SetValue(ProgId, @"Software\SightoHear\Capabilities");
                }

                // 通知 Windows 刷新文件关联缓存（右键"打开方式"立即生效，无需重启资源管理器）
                NativeMethods.SHChangeNotify(
                    0x08000000, // SHCNE_ASSOCCHANGED
                    0x0000,     // SHCNF_IDLIST
                    IntPtr.Zero,
                    IntPtr.Zero);

                AppLogger.Info(
                    $"[文件关联] 已完成\"打开方式\"注入与默认应用注册（{MediaTypeExtensions.Count} 种扩展名，exe={exePath}）");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "[文件关联] 注册文件关联失败");
            }
        }

        // ==================== 默认应用（设置页横幅） ====================

        /// <summary>
        /// 默认应用判定结果，按 Windows「默认应用」页面的三个类别拆分。
        /// </summary>
        /// <remarks>
        /// ★ 为什么按类别判定而不是按扩展名逐项判定：
        /// <para>
        /// Windows 10 / 11 的「设置 → 默认应用」页面里，媒体类型只有三个可选类别：
        /// <b>音乐播放器 / 照片查看器 / 视频播放器</b>（其余是 Web 浏览器、电子邮件、地图）。
        /// 用户在那一页点是改不掉单个扩展名的，一改就是整类归一。
        /// </para>
        /// <para>
        /// 因此判定必须与这个 UI 对齐：每一类取一个「代表格式」看它归谁，
        /// 三类都归本应用才算真正的默认应用。这样：
        /// <list type="bullet">
        /// <item>用户把三类全改成别的播放器 → 三类都不归本应用 → <b>横幅重新出现</b>（正确）；</item>
        /// <item>只把视频改成别的 → 音乐/照片仍归本应用，但视频不归 → <b>横幅仍然出现</b>（正确，
        ///       提醒用户本软件还没拿全）；</item>
        /// <item>不会因为 .flac/.mkv 等冷门格式恰好归本应用就误判成"已是默认"（旧实现的问题）。</item>
        /// </list>
        /// </para>
        /// </remarks>
        public sealed class DefaultAppStatus
        {
            /// <summary>音乐播放器是否已归本应用。</summary>
            public bool MusicDefault { get; init; }

            /// <summary>照片查看器是否已归本应用。</summary>
            public bool PhotoDefault { get; init; }

            /// <summary>视频播放器是否已归本应用。</summary>
            public bool VideoDefault { get; init; }

            /// <summary>已归本应用的类别数量（0~3）。</summary>
            public int OwnedCount =>
                (MusicDefault ? 1 : 0) + (PhotoDefault ? 1 : 0) + (VideoDefault ? 1 : 0);

            /// <summary>类别总数（固定 3）。</summary>
            public int TotalCount => 3;

            /// <summary>三类是否全部已归本应用（全部满足才算"已是默认应用"）。</summary>
            public bool IsDefault => OwnedCount == TotalCount;
        }

        /// <summary>
        /// 每个类别用于判定的「代表格式」。
        /// </summary>
        /// <remarks>
        /// 选最常见的那一个（.mp3 / .jpg / .mp4），它们在任何 Windows 上都存在且必然有归属，
        /// 也正是 Windows「默认应用」页面三个类别背后实际读取的扩展名。
        /// Fallback 仅在代表格式从未被设置（无 UserChoice 键）时启用，
        /// 避免"用户只设过 .flac 没设过 .mp3"被误判为未设置。
        /// </remarks>
        private static readonly (string Name, string[] Primary, string[] Fallback)[] DefaultAppCategories =
        {
            ("音乐", new[] { ".mp3" }, new[] { ".flac", ".wav", ".m4a" }),
            ("照片", new[] { ".jpg" }, new[] { ".jpeg", ".png" }),
            ("视频", new[] { ".mp4" }, new[] { ".mkv", ".avi", ".mov" })
        };

        /// <summary>
        /// 读取三个类别的默认应用归属，判断本应用是否已成为默认应用。
        /// 只读注册表，不写任何东西；UI 线程可安全调用（耗时 &lt; 10ms）。
        /// </summary>
        /// <remarks>
        /// Windows 从 Win8 起用 UserChoice + 哈希保护默认应用，程序无法（也不应）绕过哈希伪造成"默认"，
        /// 只能读取来判断当前状态，真正的设置动作交给系统「默认应用」页面由用户确认。
        /// <para>
        /// ★ UserChoice 的 ProgId 有两种合法形态，必须都能识别，否则会误判为"未设置"：
        /// 经「默认应用」页面设置 → <c>SightoHear</c>；
        /// 经右键「打开方式」勾选"始终使用" → <c>Applications\SightoHear.exe</c>。
        /// </para>
        /// </remarks>
        public static DefaultAppStatus GetDefaultAppStatus()
        {
            return new DefaultAppStatus
            {
                MusicDefault = IsCategoryOwned(DefaultAppCategories[0]),
                PhotoDefault = IsCategoryOwned(DefaultAppCategories[1]),
                VideoDefault = IsCategoryOwned(DefaultAppCategories[2])
            };
        }

        /// <summary>
        /// 判断某个类别的默认应用是否为本应用：先看代表格式，代表格式从未设置时再看备选格式。
        /// </summary>
        private static bool IsCategoryOwned((string Name, string[] Primary, string[] Fallback) category)
        {
            bool primaryHadValue = false;

            foreach (string ext in category.Primary)
            {
                string? progId = ReadUserChoiceProgId(ext, out bool hadValue);
                primaryHadValue |= hadValue;
                if (IsOwnedProgId(progId))
                    return true;
            }

            // 代表格式已被设置为其他应用 → 这一类明确不归本应用，不再看备选格式
            if (primaryHadValue)
                return false;

            // 代表格式从未被设置过（全新系统常见）→ 用备选格式兜底判断
            foreach (string ext in category.Fallback)
            {
                string? progId = ReadUserChoiceProgId(ext, out _);
                if (IsOwnedProgId(progId))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 读取指定扩展名 UserChoice 下的 ProgId。
        /// </summary>
        /// <param name="extension">扩展名（含点，如 ".mp3"）。</param>
        /// <param name="hadValue">该扩展名是否已被设置过默认应用（UserChoice 键存在且有 ProgId）。</param>
        private static string? ReadUserChoiceProgId(string extension, out bool hadValue)
        {
            hadValue = false;
            try
            {
                using var userChoice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice");
                if (userChoice?.GetValue("ProgId") is not string progId || string.IsNullOrWhiteSpace(progId))
                    return null;

                hadValue = true;
                return progId;
            }
            catch (Exception ex)
            {
                // UserChoice 受 ACL 保护，极端情况下可能读取失败；按"未设置"处理并记录
                AppLogger.Warning($"[默认应用] 读取 {extension} 的 UserChoice 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 判断一个 UserChoice ProgId 是否指向本应用（兼容 "SightoHear" 与 "Applications\SightoHear.exe" 两种形态）。
        /// </summary>
        private static bool IsOwnedProgId(string? progId)
        {
            if (string.IsNullOrWhiteSpace(progId))
                return false;

            if (string.Equals(progId, ProgId, StringComparison.OrdinalIgnoreCase))
                return true;

            // 右键"打开方式"路径写入的是 Applications\<exe> 形式
            string exeName = Path.GetFileName(Environment.ProcessPath ?? "SightoHear.exe");
            string appForm = $@"Applications\{exeName}";
            return string.Equals(progId, appForm, StringComparison.OrdinalIgnoreCase)
                || string.Equals(progId, $@"Applications\{ProgId}.exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 打开系统「设置 → 默认应用」页面。
        /// </summary>
        /// <returns>是否成功唤起系统设置页面。</returns>
        /// <remarks>
        /// ★ 实测结论（2026-11-15，Windows 11 24H2）：
        /// <list type="bullet">
        /// <item><c>?registeredAppUser=</c> / <c>?registeredAUMID=</c> 两个深链参数均被系统忽略，
        ///       只会落到默认应用总览页 —— 不再尝试"直达应用条目"。</item>
        /// <item><c>IAssocHandler::MakeDefault</c> 返回 S_OK 但**不写 UserChoice**
        ///       （方法返回成功、注册表无任何变化），无法用它实现真正的一键设置默认应用。</item>
        /// </list>
        /// 结论：Windows 强制「默认应用必须由用户在系统设置里确认」，应用侧只能把用户送到正确页面。
        /// 这也是 Chrome / 各类播放器的通行做法。
        /// </remarks>
        public static async Task<bool> OpenDefaultAppsSettingsAsync()
        {
            // 依次尝试：默认应用总览 → 应用功能页（系统异常时兜底）
            string[] uris =
            {
                "ms-settings:defaultapps",
                "ms-settings:appsfeatures"
            };

            foreach (string uri in uris)
            {
                try
                {
                    if (await Windows.System.Launcher.LaunchUriAsync(new Uri(uri)))
                    {
                        AppLogger.Info($"[默认应用] 已唤起系统默认应用设置: {uri}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning($"[默认应用] 唤起 {uri} 失败: {ex.Message}");
                }
            }

            AppLogger.Warning("[默认应用] 无法唤起系统默认应用设置页面");
            return false;
        }

        /// <summary>
        /// 「设为默认」按钮的完整流程：先补写注册表（保证系统认得本应用），
        /// 再打开系统默认应用页面由用户确认。
        /// </summary>
        /// <returns>是否成功唤起系统设置页面。</returns>
        public static async Task<bool> SetAsDefaultAsync()
        {
            try
            {
                // 幂等重注册：确保 Capabilities / RegisteredApplications 一定存在，
                // 否则「默认应用」页面里根本找不到本应用，按钮点了也无处可点。
                RegisterFileAssociations();
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"[默认应用] 重注册文件关联失败（继续打开系统设置）: {ex.Message}");
            }

            return await OpenDefaultAppsSettingsAsync();
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
