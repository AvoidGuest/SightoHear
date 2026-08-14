using SightoHear.Helpers;
using SightoHear.Models;
using SightoHear.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace SightoHear
{
    public sealed partial class MusicPage : Page
    {
        // ---- 封面/卡片尺寸（固定值：卡片宽 176、高 224，封面区 152（圆角卡片），文本区约 72） ----
        public static double MusicCardWidth => 176;
        public static double MusicCardHeight => 224;
        // 封面行高（RowDefinition.Height 为 GridLength 类型）
        public static Microsoft.UI.Xaml.GridLength MusicCoverRowHeight =>
            new(152);

        private readonly Random _random = new();
        private List<MediaItem> _allMusic = new();
        private List<MediaItem> _filteredMusic = new();
        private List<Playlist> _allPlaylists = new();
        private List<Playlist> _filteredPlaylists = new();
        private List<ArtistGroup> _artistGroups = new();
        private List<ArtistGroup> _filteredArtistGroups = new();
        private List<AlbumGroup> _albumGroups = new();
        private List<AlbumGroup> _filteredAlbumGroups = new();
        private List<FolderGroup> _folderGroups = new();
        private List<FolderGroup> _filteredFolderGroups = new();
        private DispatcherTimer? _debounceTimer;
        private ThumbnailLoadQueue? _thumbnailQueue;
        private ThumbnailPreloader? _thumbnailPreloader;
        private CancellationTokenSource? _memoryPreloadCts;
        private int _musicFilterGeneration;
        private int _playlistFilterGeneration;
        private int _reloadGeneration;
        private int _containerGeneration;
        private bool _initializing = true;
        private string? _pendingLocatePath;
        private bool _playlistSortInitialized;
        private bool _isMultiSelectMode;
        private readonly HashSet<string> _multiSelectedPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _selectAllChanging;
        private const double MusicCheckboxInputSuppressMs = 800;
        private string? _lastMusicCheckboxPointerPath;
        private bool? _lastMusicCheckboxPointerTarget;
        private long _lastMusicCheckboxPointerTimestamp;
        // 音乐复选框事件序号计数器：用于日志中追踪连续点按的完整事件序列（PointerPressed → Checked/Unchecked → ItemClick → DoubleTapped）
        private int _musicCheckboxEventSeq;
        private const double TabFixedWidth = 60;
        private int _selectedTabIndex;
        private int _multiSelectActiveTab = -1; // -1=none, 0=music, 1=playlist, 2=artist, 3=album, 4=folder
        private bool _playlistMultiSelectMode;
        private readonly HashSet<string> _playlistMultiSelectedIds = new(StringComparer.OrdinalIgnoreCase);
        private bool _playlistSelectAllChanging;
        private bool _artistMultiSelectMode;
        private readonly HashSet<string> _artistMultiSelectedNames = new(StringComparer.OrdinalIgnoreCase);
        private bool _artistSelectAllChanging;
        private bool _albumMultiSelectMode;
        private readonly HashSet<string> _albumMultiSelectedKeys = new(StringComparer.OrdinalIgnoreCase);
        private bool _albumSelectAllChanging;
        private bool _folderMultiSelectMode;
        private readonly HashSet<string> _folderMultiSelectedPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _folderSelectAllChanging;
        private bool _tabIsDragging;
        private int _hoveredTabIndex = -1; // 跟踪当前 hover 的 tab，主题切换时重新应用颜色
        public MusicPage()
        {
            InitializeComponent();
            SetupTabBar();

            MusicToolbarGrid.Visibility = Visibility.Visible;
            MultiSelectToolbarGrid.Visibility = Visibility.Collapsed;
            PlaylistToolbarGrid.Visibility = Visibility.Collapsed;
            ArtistToolbarGrid.Visibility = Visibility.Collapsed;
            AlbumToolbarGrid.Visibility = Visibility.Collapsed;
            FolderToolbarGrid.Visibility = Visibility.Collapsed;

            MusicViewModeComboBox.SelectedIndex = App.SettingsHelper.MusicRememberView
                ? Math.Clamp(App.SettingsHelper.MusicDefaultView, 0, 2)
                : 0;
            MusicSortComboBox.SelectedIndex = App.SettingsHelper.MusicRememberSort
                ? Math.Clamp(App.SettingsHelper.MusicDefaultSort, 0, 3)
                : -1;
            PlaylistViewModeComboBox.SelectedIndex = App.SettingsHelper.PlaylistRememberView
                ? Math.Clamp(App.SettingsHelper.PlaylistDefaultView, 0, 2)
                : 1;
            PlaylistSortComboBox.SelectedIndex = App.SettingsHelper.PlaylistRememberSort
                ? Math.Clamp(App.SettingsHelper.PlaylistDefaultSort, 0, 1)
                : -1;
            ArtistViewModeComboBox.SelectedIndex = App.SettingsHelper.ArtistRememberView
                ? Math.Clamp(App.SettingsHelper.ArtistDefaultView, 0, 2)
                : 1;
            ArtistSortComboBox.SelectedIndex = App.SettingsHelper.ArtistRememberSort
                ? Math.Clamp(App.SettingsHelper.ArtistDefaultSort, 0, 1)
                : -1;
            AlbumViewModeComboBox.SelectedIndex = App.SettingsHelper.AlbumRememberView
                ? Math.Clamp(App.SettingsHelper.AlbumDefaultView, 0, 2)
                : 2;
            AlbumSortComboBox.SelectedIndex = App.SettingsHelper.AlbumRememberSort
                ? Math.Clamp(App.SettingsHelper.AlbumDefaultSort, 0, 2)
                : -1;
            FolderViewModeComboBox.SelectedIndex = App.SettingsHelper.FolderRememberView
                ? Math.Clamp(App.SettingsHelper.FolderDefaultView, 0, 2)
                : 0;
            FolderSortComboBox.SelectedIndex = App.SettingsHelper.FolderRememberSort
                ? Math.Clamp(App.SettingsHelper.FolderDefaultSort, 0, 1)
                : -1;
            _initializing = false;

            Loaded += MusicPage_Loaded;
            Unloaded += MusicPage_Unloaded;
            ActualThemeChanged += MusicPage_ActualThemeChanged;
        }

        private async void MusicPage_Loaded(object sender, RoutedEventArgs e)
        {
            var sw = Stopwatch.StartNew();
            _containerGeneration = PageLifetimeService.CurrentGeneration;
            PageLifetimeService.OnNavigatedTo("MusicPage");
            AppLogger.Info($"[MusicPage] Loaded 触发 | IsInitialized={MusicDataCache.IsInitialized} | CurrentGen={PageLifetimeService.CurrentGeneration} | ContainerGen={_containerGeneration}");
            _thumbnailQueue?.Dispose();
            _thumbnailQueue = new ThumbnailLoadQueue(intervalMs: 80);
            _thumbnailPreloader?.Dispose();
            _thumbnailPreloader = new ThumbnailPreloader(intervalMs: 0, thumbnailSize: 260);
            _memoryPreloadCts?.Cancel();
            _memoryPreloadCts = new CancellationTokenSource();
            MediaScanner.CacheUpdated -= MediaScanner_CacheUpdated;
            MediaScanner.CacheUpdated += MediaScanner_CacheUpdated;
            // 订阅音乐库数据变更（播放器等外部删除歌曲时刷新当前视图）
            MusicDataCache.MusicLibraryChanged -= MusicDataCache_MusicLibraryChanged;
            MusicDataCache.MusicLibraryChanged += MusicDataCache_MusicLibraryChanged;
            // 订阅媒体库文件夹勾选变更（媒体库管理弹窗）
            MediaLibraryFolderManager.EnabledFoldersChanged -= MediaLibraryFolderManager_EnabledFoldersChanged;
            MediaLibraryFolderManager.EnabledFoldersChanged += MediaLibraryFolderManager_EnabledFoldersChanged;

            // ★ 按需订阅容器事件（仅当前可见 tab 的当前视图）
            SubscribeActiveContainerEvents();
            AppLogger.Debug($"[MusicPage] 初始化完成: {sw.ElapsedMilliseconds}ms");

            // ★ 如果缓存已有数据，直接恢复，避免重复磁盘 I/O
            if (MusicDataCache.IsInitialized)
            {
                AppLogger.Info($"[MusicPage] 【缓存命中路径】MusicDataCache.IsInitialized=true，从缓存恢复数据");
                _allMusic = MediaLibraryFolderManager.FilterByEnabledFolders(MusicDataCache.AllMusic, "Music");
                _allPlaylists = MusicDataCache.AllPlaylists;
                _artistGroups = MusicDataCache.ArtistGroups;
                _albumGroups = MusicDataCache.AlbumGroups;
                _folderGroups = MusicDataCache.FolderGroups;
                // 重建各 tab 视图（按当前 tab 恢复）
                ApplyMusicSortAndFilter();
                ApplyPlaylistSortAndFilter();
                ApplyArtistSortAndFilter();
                ApplyAlbumSortAndFilter();
                ApplyFolderSortAndFilter();
                AppLogger.Debug($"[MusicPage] 数据恢复+排序完成: {sw.ElapsedMilliseconds}ms | 歌曲数: {_allMusic.Count}");
                // ★ 后台启动预加载器（磁盘缓存）+ 内存预加载
                _thumbnailPreloader?.Start(_allMusic);
                _ = PreloadMemoryCacheAsync(_allMusic, _memoryPreloadCts?.Token ?? CancellationToken.None);
                AppLogger.Debug($"[MusicPage] 预加载已触发 (缓存路径): {sw.ElapsedMilliseconds}ms");
                return;
            }

            AppLogger.Info($"[MusicPage] 【磁盘加载路径】MusicDataCache.IsInitialized=false，从磁盘 LoadFromCache");
            int generation = ++_reloadGeneration;
            _allMusic = await Task.Run(() =>
                MediaLibraryFolderManager.FilterByEnabledFolders(MediaScanner.LoadFromCache("Music"), "Music"));
            if (generation != _reloadGeneration)
            {
                AppLogger.Warning($"[MusicPage] LoadFromCache 完成但 generation 已过期，中止加载");
                return;
            }
            AppLogger.Debug($"[MusicPage] LoadFromCache 完成: {sw.ElapsedMilliseconds}ms | 歌曲数: {_allMusic.Count}");
            // 保存到缓存
            MusicDataCache.AllMusic = _allMusic;
            MusicDataCache.LoadPlaylists();
            _allPlaylists = MusicDataCache.AllPlaylists;
            MusicDataCache.RebuildDerivedGroups();
            _artistGroups = MusicDataCache.ArtistGroups;
            _albumGroups = MusicDataCache.AlbumGroups;
            _folderGroups = MusicDataCache.FolderGroups;
            ApplyMusicSortAndFilter();
            AppLogger.Debug($"[MusicPage] 排序/分组完成: {sw.ElapsedMilliseconds}ms");
            // ★ 后台启动预加载器（磁盘缓存）+ 内存预加载
            _thumbnailPreloader?.Start(_allMusic);
            _ = PreloadMemoryCacheAsync(_allMusic, _memoryPreloadCts?.Token ?? CancellationToken.None);
            AppLogger.Debug($"[MusicPage] 预加载已触发 (非缓存路径): {sw.ElapsedMilliseconds}ms");

            if (_allMusic.Any(item => !item.MusicMetadataScanned))
            {
                AppLogger.Debug($"[MusicPage] 开始补全元数据: {sw.ElapsedMilliseconds}ms | 未扫描数: {_allMusic.Count(i => !i.MusicMetadataScanned)}");
                MusicScanStatusText.Text = "正在补全音乐信息和封面...";
                await MediaScanner.EnrichMusicMetadataAsync(_allMusic, onlyUnscanned: true);
                if (generation != _reloadGeneration)
                {
                    AppLogger.Warning($"[MusicPage] 元数据补全完成但 generation 已过期，中止");
                    return;
                }
                await Task.Run(() => MediaLibraryFolderManager.SaveMergedCache(_allMusic, "Music"));
                if (generation != _reloadGeneration)
                {
                    AppLogger.Warning($"[MusicPage] SaveToCache 完成但 generation 已过期，中止");
                    return;
                }
                ApplyMusicSortAndFilter();
                MusicScanStatusText.Text = string.Empty;
                AppLogger.Debug($"[MusicPage] 元数据补全完成: {sw.ElapsedMilliseconds}ms");
            }
            AppLogger.Debug($"[MusicPage] 加载总耗时: {sw.ElapsedMilliseconds}ms");
        }

        private void MusicPage_Unloaded(object sender, RoutedEventArgs e)
        {
            MediaScanner.CacheUpdated -= MediaScanner_CacheUpdated;
            MusicDataCache.MusicLibraryChanged -= MusicDataCache_MusicLibraryChanged;
            MediaLibraryFolderManager.EnabledFoldersChanged -= MediaLibraryFolderManager_EnabledFoldersChanged;
            ActualThemeChanged -= MusicPage_ActualThemeChanged;

            // 解除容器事件订阅
            UnsubscribeAllContainerEvents();

            // ★ 将当前数据保存到全局缓存，供下次实例恢复
            MusicDataCache.AllMusic = _allMusic;
            MusicDataCache.AllPlaylists = _allPlaylists;
            MusicDataCache.ArtistGroups = _artistGroups;
            MusicDataCache.AlbumGroups = _albumGroups;
            MusicDataCache.FolderGroups = _folderGroups;

            // ★ 清空页面持有的引用，使 GC 可以回收旧页面（但缓存保留数据）
            _allMusic = null!;
            _allPlaylists = null!;
            _artistGroups = null!;
            _albumGroups = null!;
            _folderGroups = null!;
            _filteredMusic = null!;
            _filteredPlaylists = null!;
            _filteredArtistGroups = null!;
            _filteredAlbumGroups = null!;
            _filteredFolderGroups = null!;

            // ★ 清空 ItemsSource（释放 UI 对数据的引用）
            MusicList.ItemsSource = null;
            MusicGrid.ItemsSource = null;
            if (MusicWaterfallGrid != null) MusicWaterfallGrid.ItemsSource = null;
            PlaylistList.ItemsSource = null;
            PlaylistGrid.ItemsSource = null;
            if (PlaylistWaterfallGrid != null) PlaylistWaterfallGrid.ItemsSource = null;
            ArtistList.ItemsSource = null;
            ArtistGrid.ItemsSource = null;
            if (ArtistWaterfallGrid != null) ArtistWaterfallGrid.ItemsSource = null;
            AlbumList.ItemsSource = null;
            AlbumGrid.ItemsSource = null;
            if (AlbumWaterfallGrid != null) AlbumWaterfallGrid.ItemsSource = null;
            FolderList.ItemsSource = null;
            FolderGrid.ItemsSource = null;
            if (FolderWaterfallGrid != null) FolderWaterfallGrid.ItemsSource = null;

            // ★ 修复：页面离开后其封面/缩略图不再"热"，裁剪 ImageThumbnailService
            //   强引用 LRU 缓存（保留最近 192 条热数据）。
            //   否则离开页面后 BitmapImage 仍被缓存强引用，GPU 解码显存滞留不释放，
            //   浏览多页面后累积可达数百 MB（显存碎片化 → Win2D 卡顿）。
            //   注意：内存预加载（_memoryPreloadCts）会提前把所有封面塞入缓存，
            //   离开时裁剪能显著回落显存占用。
            ImageThumbnailService.TrimMemoryCache(192);

            // 取消内存预加载
            _memoryPreloadCts?.Cancel();
            _memoryPreloadCts = null;

            // 清空阶梯加载队列和预加载器
            _thumbnailPreloader?.Dispose();
            _thumbnailPreloader = null;
            _thumbnailQueue?.Dispose();
            _thumbnailQueue = null;

            // 递增 generation，使所有陈旧异步操作立即失效
            _reloadGeneration++;
            _musicFilterGeneration++;
            _playlistFilterGeneration++;
            PageLifetimeService.OnNavigatingAway();

            // 停止防抖定时器
            _debounceTimer?.Stop();
            _debounceTimer = null;
        }

        /// <summary>音乐库数据变更（播放器等外部删除/添加歌曲）后刷新当前视图。</summary>
        private void MusicDataCache_MusicLibraryChanged()
        {
            // 页面 Unloaded 后 _allMusic 被置空，此时应忽略外部变更事件
            if (_allMusic == null)
                return;
            // 缓存中的 AllMusic/AllPlaylists 与页面持有的是同一 List 实例，变更已同步，
            // 这里只需重新过滤排序刷新绑定源
            ApplyMusicSortAndFilter();
            ApplyPlaylistSortAndFilter();
        }

        private void MusicPage_ActualThemeChanged(FrameworkElement sender, object args)
        {
            // 主题切换时，重新应用当前 hover tab 的颜色
            if (_hoveredTabIndex >= 0 && _hoveredTabIndex < 5 && _hoveredTabIndex != _selectedTabIndex)
            {
                SetHoverState(_hoveredTabIndex, true);
            }
        }

        // ★ 按需订阅当前可见 tab 的当前可见视图的 ContainerContentChanging
        private void SubscribeActiveContainerEvents()
        {
            // 先全部取消
            UnsubscribeAllContainerEvents();

            int index = _selectedTabIndex;
            int mode = GetActiveViewMode(index);
            int altMode1 = (mode + 1) % 3;
            int altMode2 = (mode + 2) % 3;

            // 只订阅当前可见视图的事件
            // 但为简化，订阅当前 tab 下所有视图的事件（切换视图时由 SelectionChanged 重新订阅）
            SubscribeTabContainerEvents(index);
        }

        private int GetActiveViewMode(int tabIndex)
        {
            return tabIndex switch
            {
                0 => MusicViewModeComboBox?.SelectedIndex ?? 0,
                1 => PlaylistViewModeComboBox?.SelectedIndex ?? 1,
                2 => ArtistViewModeComboBox?.SelectedIndex ?? 1,
                3 => AlbumViewModeComboBox?.SelectedIndex ?? 2,
                4 => FolderViewModeComboBox?.SelectedIndex ?? 0,
                _ => 0
            };
        }

        private void SubscribeTabContainerEvents(int tabIndex)
        {
            // 每次订阅前先取消，防止重复
            switch (tabIndex)
            {
                case 0: // 音乐
                    MusicList.ContainerContentChanging -= MusicList_ContainerContentChanging;
                    MusicList.ContainerContentChanging += MusicList_ContainerContentChanging;
                    MusicGrid.ContainerContentChanging -= MusicGrid_ContainerContentChanging;
                    MusicGrid.ContainerContentChanging += MusicGrid_ContainerContentChanging;
                    if (MusicWaterfallGrid != null)
                    {
                        MusicWaterfallGrid.ContainerContentChanging -= MusicGrid_ContainerContentChanging;
                        MusicWaterfallGrid.ContainerContentChanging += MusicGrid_ContainerContentChanging;
                    }
                    break;
                case 1: // 歌单
                    PlaylistList.ContainerContentChanging -= PlaylistList_ContainerContentChanging;
                    PlaylistList.ContainerContentChanging += PlaylistList_ContainerContentChanging;
                    PlaylistGrid.ContainerContentChanging -= PlaylistGrid_ContainerContentChanging;
                    PlaylistGrid.ContainerContentChanging += PlaylistGrid_ContainerContentChanging;
                    if (PlaylistWaterfallGrid != null)
                    {
                        PlaylistWaterfallGrid.ContainerContentChanging -= PlaylistGrid_ContainerContentChanging;
                        PlaylistWaterfallGrid.ContainerContentChanging += PlaylistGrid_ContainerContentChanging;
                    }
                    break;
                case 2: // 歌手
                    ArtistList.ContainerContentChanging -= ArtistList_ContainerContentChanging;
                    ArtistList.ContainerContentChanging += ArtistList_ContainerContentChanging;
                    ArtistGrid.ContainerContentChanging -= ArtistGrid_ContainerContentChanging;
                    ArtistGrid.ContainerContentChanging += ArtistGrid_ContainerContentChanging;
                    if (ArtistWaterfallGrid != null)
                    {
                        ArtistWaterfallGrid.ContainerContentChanging -= ArtistGrid_ContainerContentChanging;
                        ArtistWaterfallGrid.ContainerContentChanging += ArtistGrid_ContainerContentChanging;
                    }
                    break;
                case 3: // 专辑
                    AlbumList.ContainerContentChanging -= AlbumList_ContainerContentChanging;
                    AlbumList.ContainerContentChanging += AlbumList_ContainerContentChanging;
                    AlbumGrid.ContainerContentChanging -= AlbumGrid_ContainerContentChanging;
                    AlbumGrid.ContainerContentChanging += AlbumGrid_ContainerContentChanging;
                    if (AlbumWaterfallGrid != null)
                    {
                        AlbumWaterfallGrid.ContainerContentChanging -= AlbumGrid_ContainerContentChanging;
                        AlbumWaterfallGrid.ContainerContentChanging += AlbumGrid_ContainerContentChanging;
                    }
                    break;
                case 4: // 文件夹
                    FolderList.ContainerContentChanging -= FolderList_ContainerContentChanging;
                    FolderList.ContainerContentChanging += FolderList_ContainerContentChanging;
                    FolderGrid.ContainerContentChanging -= FolderGrid_ContainerContentChanging;
                    FolderGrid.ContainerContentChanging += FolderGrid_ContainerContentChanging;
                    if (FolderWaterfallGrid != null)
                    {
                        FolderWaterfallGrid.ContainerContentChanging -= FolderGrid_ContainerContentChanging;
                        FolderWaterfallGrid.ContainerContentChanging += FolderGrid_ContainerContentChanging;
                    }
                    break;
            }
        }

        private void UnsubscribeAllContainerEvents()
        {
            MusicList.ContainerContentChanging -= MusicList_ContainerContentChanging;
            MusicGrid.ContainerContentChanging -= MusicGrid_ContainerContentChanging;
            if (MusicWaterfallGrid != null)
                MusicWaterfallGrid.ContainerContentChanging -= MusicGrid_ContainerContentChanging;
            PlaylistList.ContainerContentChanging -= PlaylistList_ContainerContentChanging;
            PlaylistGrid.ContainerContentChanging -= PlaylistGrid_ContainerContentChanging;
            if (PlaylistWaterfallGrid != null)
                PlaylistWaterfallGrid.ContainerContentChanging -= PlaylistGrid_ContainerContentChanging;
            ArtistList.ContainerContentChanging -= ArtistList_ContainerContentChanging;
            ArtistGrid.ContainerContentChanging -= ArtistGrid_ContainerContentChanging;
            if (ArtistWaterfallGrid != null)
                ArtistWaterfallGrid.ContainerContentChanging -= ArtistGrid_ContainerContentChanging;
            AlbumList.ContainerContentChanging -= AlbumList_ContainerContentChanging;
            AlbumGrid.ContainerContentChanging -= AlbumGrid_ContainerContentChanging;
            if (AlbumWaterfallGrid != null)
                AlbumWaterfallGrid.ContainerContentChanging -= AlbumGrid_ContainerContentChanging;
            FolderList.ContainerContentChanging -= FolderList_ContainerContentChanging;
            FolderGrid.ContainerContentChanging -= FolderGrid_ContainerContentChanging;
            if (FolderWaterfallGrid != null)
                FolderWaterfallGrid.ContainerContentChanging -= FolderGrid_ContainerContentChanging;
        }

        /// <summary>通过主导航 Frame 打开一个详情页。</summary>
        private void NavigateToDetailPage(Type pageType, object parameter)
        {
            // 保存当前数据到缓存（确保数据一致性）
            MusicDataCache.AllMusic = _allMusic;
            MusicDataCache.AllPlaylists = _allPlaylists;
            MusicDataCache.ArtistGroups = _artistGroups;
            MusicDataCache.AlbumGroups = _albumGroups;
            MusicDataCache.FolderGroups = _folderGroups;

            if (App.MainWindow is MainWindow mainWin)
                mainWin.NavigateMainFrame(pageType, parameter);
        }

        private void MediaScanner_CacheUpdated(object? sender, string mediaType)
        {
            if (mediaType != "Music")
                return;

            // ★ 如果页面已有数据，跳过 CacheUpdated 触发的重载，避免各视图完全重新虚拟化
            if (_allMusic.Count > 0)
            {
                AppLogger.Debug($"[MusicPage] CacheUpdated Music 被跳过（_allMusic.Count={_allMusic.Count} > 0）");
                return;
            }

            AppLogger.Info($"[MusicPage] CacheUpdated Music 触发 | _allMusic.Count=0 | ContainerGen={_containerGeneration} | CurrentGen={PageLifetimeService.CurrentGeneration} | IsActive={PageLifetimeService.IsActive(_containerGeneration)}");
            DispatcherQueue.TryEnqueue(async () =>
            {
                // ★ 回调是 async void，内部必须完整 try-catch，防止异常逃逸导致进程崩溃
                try
                {
                    if (!PageLifetimeService.IsActive(_containerGeneration))
                    {
                        AppLogger.Debug($"[MusicPage] CacheUpdated DispatcherQueue 执行时 generation 已过期，跳过");
                        return;
                    }
                    AppLogger.Debug($"[MusicPage] CacheUpdated DispatcherQueue 开始 LoadFromCache");
                    _allMusic = await Task.Run(() =>
                        MediaLibraryFolderManager.FilterByEnabledFolders(MediaScanner.LoadFromCache("Music"), "Music"));
                    if (!PageLifetimeService.IsActive(_containerGeneration))
                    {
                        AppLogger.Debug($"[MusicPage] CacheUpdated LoadFromCache 完成后 generation 已过期，跳过");
                        return;
                    }
                    AppLogger.Debug($"[MusicPage] CacheUpdated LoadFromCache 完成: {_allMusic.Count}项");
                    MusicDataCache.AllMusic = _allMusic;
                    MusicDataCache.RebuildDerivedGroups();
                    _artistGroups = MusicDataCache.ArtistGroups;
                    _albumGroups = MusicDataCache.AlbumGroups;
                    _folderGroups = MusicDataCache.FolderGroups;
                    ApplyMusicSortAndFilter();
                    // ★ 重新启动预加载器
                    _thumbnailPreloader?.Start(_allMusic);
                    AppLogger.Info($"[MusicPage] CacheUpdated 触发重载完成");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "[MusicPage] CacheUpdated 触发重载失败");
                }
            });
        }

        private async void ApplyMusicSortAndFilter()
        {
            if (MusicSearchBox == null || MusicSortComboBox == null || MusicViewModeComboBox == null)
                return;

            int generation = ++_musicFilterGeneration;
            string searchText = MusicSearchBox.Text?.Trim() ?? string.Empty;
            int sortIndex = MusicSortComboBox.SelectedIndex;

            // ★ 修复：把 _allMusic 的列表复制也移入后台线程。
            //   大列表时 ToList() 复制本身就可能耗时数十毫秒，
            //   若留在 UI 线程会阻塞渲染（60fps 帧预算仅 16ms）。
            //   _allMusic 的引用替换是原子的，后台读取旧 List 安全；
            //   仅"多选删除"的 RemoveAll 可能并发修改同一 List 实例，
            //   罕见冲突时捕获后重试一次。
            var filtered = await Task.Run(() =>
            {
                List<MediaItem> source;
                try
                {
                    source = _allMusic.ToList();
                }
                catch (InvalidOperationException)
                {
                    // 与 UI 线程的 RemoveAll 并发，重试一次（极少发生）
                    source = _allMusic.ToList();
                }

                IEnumerable<MediaItem> query = source;
                if (!string.IsNullOrWhiteSpace(searchText))
                {
                    query = query.Where(item =>
                        item.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                        item.Artist.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                        item.Album.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                        item.FileName.Contains(searchText, StringComparison.OrdinalIgnoreCase));
                }

                query = sortIndex switch
                {
                    1 => query.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
                    2 => query.OrderBy(item => item.ArtistDisplay, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
                    3 => query.OrderBy(item => item.AlbumDisplay, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.TrackNumber)
                        .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
                    4 => query.OrderByDescending(item => item.DateModified),
                    _ => query.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                };

                return query.ToList();
            });

            if (generation != _musicFilterGeneration)
            {
                AppLogger.Debug($"[MusicPage] ApplyMusicSortAndFilter 已过期（generation {generation} ≠ {_musicFilterGeneration}），丢弃结果");
                return;
            }

            // ★ 修复：仅对 UI 线程部分计时（赋值 + 刷新视图），
            //   避免把后台排序耗时误记为"UI 阻塞耗时"（旧日志曾误导诊断为 65ms UI 阻塞）。
            var uiSw = Stopwatch.StartNew();
            _filteredMusic = filtered;
            RefreshMusicView();
            uiSw.Stop();
            AppLogger.Debug($"[MusicPage] ApplyMusicSortAndFilter UI耗时: {uiSw.ElapsedMilliseconds}ms | 过滤后: {_filteredMusic.Count} | searchText=\"{searchText}\" | sortIndex={sortIndex}");
        }

        private async void ApplyPlaylistSortAndFilter()
        {
            if (PlaylistSearchBox == null || PlaylistSortComboBox == null || PlaylistViewModeComboBox == null)
                return;

            int generation = ++_playlistFilterGeneration;
            string searchText = PlaylistSearchBox.Text?.Trim() ?? string.Empty;
            int sortIndex = PlaylistSortComboBox.SelectedIndex;

            var filtered = await Task.Run(() =>
            {
                // ★ 修复：先快照再筛选，避免后台延迟枚举期间 UI 线程
                //   修改 _allPlaylists 引发"集合已修改"异常。
                List<Playlist> source;
                try
                {
                    source = _allPlaylists.ToList();
                }
                catch (InvalidOperationException)
                {
                    source = _allPlaylists.ToList();
                }

                IEnumerable<Playlist> query = source;

                if (!string.IsNullOrWhiteSpace(searchText))
                {
                    query = query.Where(p =>
                        p.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
                }

                query = sortIndex switch
                {
                    1 => query.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
                    _ => query.OrderByDescending(p => p.DateCreated)
                };

                return query.ToList();
            });

            if (generation != _playlistFilterGeneration)
            {
                AppLogger.Debug($"[MusicPage] ApplyPlaylistSortAndFilter 已过期（generation {generation} ≠ {_playlistFilterGeneration}），丢弃结果");
                return;
            }

            _filteredPlaylists = filtered;
            RefreshPlaylistView();
        }

        private void RefreshMusicView()
        {
            if (EmptyStateText == null || ListHeader == null || MusicList == null || MusicGrid == null)
                return;

            var sw = Stopwatch.StartNew();
            bool isEmpty = _filteredMusic.Count == 0;
            int mode = MusicViewModeComboBox.SelectedIndex;

            EmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            ListHeader.Visibility = mode == 0 && !isEmpty ? Visibility.Visible : Visibility.Collapsed;
            MusicList.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
            MusicGrid.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (MusicWaterfallGrid != null)
                MusicWaterfallGrid.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;

            MusicList.ItemsSource = mode == 0 && !isEmpty ? _filteredMusic : null;
            MusicGrid.ItemsSource = mode == 1 && !isEmpty ? _filteredMusic : null;
            if (MusicWaterfallGrid != null)
            {
                MusicWaterfallGrid.ItemsSource = mode == 2 && !isEmpty ? _filteredMusic : null;
                if (mode == 2) UpdateMusicWaterfallItemWidth();
            }

            TryLocatePending();
            AppLogger.Debug($"[MusicPage] RefreshMusicView 耗时: {sw.ElapsedMilliseconds}ms | 模式: {mode} | 项数: {_filteredMusic.Count}");
        }

        private void RefreshPlaylistView()
        {
            if (PlaylistEmptyStateText == null || PlaylistList == null || PlaylistGrid == null)
                return;

            bool isEmpty = _filteredPlaylists.Count == 0;
            int mode = PlaylistViewModeComboBox.SelectedIndex;

            PlaylistEmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

            if (isEmpty)
            {
                PlaylistList.Visibility = Visibility.Collapsed;
                PlaylistGrid.Visibility = Visibility.Collapsed;
                if (PlaylistWaterfallGrid != null) PlaylistWaterfallGrid.Visibility = Visibility.Collapsed;
                PlaylistList.ItemsSource = null;
                PlaylistGrid.ItemsSource = null;
                if (PlaylistWaterfallGrid != null) PlaylistWaterfallGrid.ItemsSource = null;
                return;
            }

            PlaylistList.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
            PlaylistGrid.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (PlaylistWaterfallGrid != null)
                PlaylistWaterfallGrid.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;

            PlaylistList.ItemsSource = mode == 0 ? _filteredPlaylists : null;
            PlaylistGrid.ItemsSource = mode == 1 ? _filteredPlaylists : null;
            if (PlaylistWaterfallGrid != null)
            {
                PlaylistWaterfallGrid.ItemsSource = mode == 2 ? _filteredPlaylists : null;
                if (mode == 2) UpdatePlaylistWaterfallItemWidth();
            }
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is string path && !string.IsNullOrWhiteSpace(path))
                _pendingLocatePath = path;
            TryLocatePending();
        }

        private void TryLocatePending()
        {
            if (string.IsNullOrEmpty(_pendingLocatePath) || _selectedTabIndex == 1)
                return;

            var item = _filteredMusic.FirstOrDefault(
                m => string.Equals(m.FilePath, _pendingLocatePath, StringComparison.OrdinalIgnoreCase));
            if (item == null)
                return;

            _pendingLocatePath = null;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    ListViewBase target = MusicViewModeComboBox.SelectedIndex switch
                    {
                        0 => MusicList,
                        1 => MusicGrid,
                        _ => MusicWaterfallGrid
                    };
                    target.ScrollIntoView(item);
                    target.SelectedItem = item;
                }
                catch { }
            });
        }

        private void MusicSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                DebounceApplySortAndFilter();
        }

        private void PlaylistSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyPlaylistSortAndFilter();
        }

        private void MusicViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || MusicViewModeComboBox == null || MusicViewModeComboBox.SelectedIndex < 0)
                return;

            if (MusicList != null)
                RefreshMusicView();

            if (App.SettingsHelper.MusicRememberView)
            {
                App.SettingsHelper.MusicDefaultView = MusicViewModeComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void PlaylistViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || PlaylistViewModeComboBox == null || PlaylistViewModeComboBox.SelectedIndex < 0)
                return;

            if (PlaylistGrid != null)
                RefreshPlaylistView();

            if (App.SettingsHelper.PlaylistRememberView)
            {
                App.SettingsHelper.PlaylistDefaultView = PlaylistViewModeComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void MusicSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || MusicSortComboBox == null || MusicSortComboBox.SelectedIndex < 0)
                return;

            if (MusicList != null)
                DebounceApplySortAndFilter();

            if (App.SettingsHelper.MusicRememberSort)
            {
                App.SettingsHelper.MusicDefaultSort = MusicSortComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void PlaylistSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || PlaylistSortComboBox == null || PlaylistSortComboBox.SelectedIndex < 0)
                return;

            if (PlaylistGrid != null)
                ApplyPlaylistSortAndFilter();

            if (App.SettingsHelper.PlaylistRememberSort)
            {
                App.SettingsHelper.PlaylistDefaultSort = PlaylistSortComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void DebounceApplySortAndFilter()
        {
            _debounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounceTimer.Tick -= DebounceTimer_Tick;
            _debounceTimer.Tick += DebounceTimer_Tick;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void DebounceTimer_Tick(object? sender, object e)
        {
            _debounceTimer?.Stop();
            ApplyMusicSortAndFilter();
        }

        private async void MusicView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                if (e.ClickedItem is MediaItem clickedItem)
                {
                    if (IsRecentMusicCheckboxInput(clickedItem.FilePath, out double elapsedMs))
                    {
                        AppLogger.Debug($"[MusicPage][#{++_musicCheckboxEventSeq}] 音乐项 ItemClick 已抑制（来自复选框输入）| Path={clickedItem.FilePath} | Elapsed={elapsedMs:F1}ms | Selected={_multiSelectedPaths.Contains(clickedItem.FilePath)}");
                        return;
                    }

                    AppLogger.Debug($"[MusicPage][#{++_musicCheckboxEventSeq}] 音乐项 ItemClick 正常切换 | Path={clickedItem.FilePath} | Selected={_multiSelectedPaths.Contains(clickedItem.FilePath)}");
                    ToggleMusicItemSelection(clickedItem);
                }
                return;
            }

            if (App.SettingsHelper.MusicFileOpenMode != 0)
                return;

            if (e.ClickedItem is MediaItem item)
                await PlayMusicAsync(item);
        }

        private async void MusicItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindVisualAncestor<CheckBox>(source) != null)
            {
                AppLogger.Debug("[MusicPage] 忽略音乐项 DoubleTapped：事件源来自复选框");
                e.Handled = true;
                return;
            }

            if (_isMultiSelectMode)
            {
                // 双击卡片时 ListViewBase 会触发两次 ItemClick（第二次延迟到双击窗口结束后），
                // 事件序列为：ItemClick#1(立即) → DoubleTapped → ItemClick#2(延迟)，
                // 若此处再执行 ToggleMusicItemSelection，一次双击将产生 3 次切换
                // （勾选→取消→勾选），第二次点击的取消操作被延迟的 ItemClick#2 反转——这正是日志中
                // "#7 Unchecked → #8 ItemClick(Add)" 序列对应的 bug。
                // 修复：双击只保留两次 ItemClick 的切换（勾选→取消），此处仅拦截手势冒泡，不再切换。
                e.Handled = true;
                return;
            }

            if (App.SettingsHelper.MusicFileOpenMode != 1 ||
                sender is not FrameworkElement { Tag: MediaItem item })
            {
                return;
            }

            e.Handled = true;
            await PlayMusicAsync(item);
        }

        private void MusicItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: MediaItem item } element)
                return;

            e.Handled = true;
            var menu = new MenuFlyout();

            // 播放（页面独有）
            var playItem = new MenuFlyoutItem
            {
                Text = "播放",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE768" }
            };
            playItem.Click += async (_, _) => await PlayMusicAsync(item);
            menu.Items.Add(playItem);

            // 从播放器右键菜单复用：查看封面
            menu.Items.Add(MusicItemMenuHelper.BuildViewCoverMenuItem(item, XamlRoot));

            menu.Items.Add(new MenuFlyoutSeparator());

            // 复用公共菜单项：使用其他应用打开 / 打开文件所在位置 / 添加到歌单 / 复制
            menu.Items.Add(MusicItemMenuHelper.BuildOpenWithMenuItem(item));
            menu.Items.Add(MusicItemMenuHelper.BuildOpenLocationMenuItem(item));
            menu.Items.Add(MusicItemMenuHelper.BuildAddToPlaylistMenuItem(item));
            menu.Items.Add(MusicItemMenuHelper.BuildCopySubMenu(item));

            menu.Items.Add(new MenuFlyoutSeparator());

            // 复用公共删除流程（删除后通过 MusicLibraryChanged 事件刷新当前视图）
            menu.Items.Add(MusicItemMenuHelper.BuildDeleteMenuItem(item, XamlRoot));

            // 选择 / 属性（页面独有）
            var selectItem = new MenuFlyoutItem
            {
                Text = "选择",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE73E" }
            };
            selectItem.Click += (_, _) => EnterMultiSelectMode(0, item);
            menu.Items.Add(selectItem);

            var propertiesItem = new MenuFlyoutItem
            {
                Text = "属性",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE946" }
            };
            propertiesItem.Click += async (_, _) => await ShowPropertiesAsync(item);
            menu.Items.Add(propertiesItem);

            menu.ShowAt(element, e.GetPosition(element));
        }

        private async void MusicRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            MusicRefreshButton.IsEnabled = false;
            MusicScanStatusText.Text = "正在扫描...";

            try
            {
                _allMusic = MediaLibraryFolderManager.FilterByEnabledFolders(
                    await MediaScanner.RefreshLibraryAsync("Music"), "Music");
                MusicDataCache.AllMusic = _allMusic;
                MusicDataCache.RebuildDerivedGroups();
                _artistGroups = MusicDataCache.ArtistGroups;
                _albumGroups = MusicDataCache.AlbumGroups;
                _folderGroups = MusicDataCache.FolderGroups;
                ApplyMusicSortAndFilter();
                MusicScanStatusText.Text = _allMusic.Count > 0
                    ? $"已扫描 {_allMusic.Count} 个音乐文件"
                    : "没有扫描到音乐";
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "刷新音乐库");
                MusicScanStatusText.Text = "扫描失败";
            }
            finally
            {
                MusicRefreshButton.IsEnabled = true;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    MusicScanStatusText.Text = string.Empty;
                };
                timer.Start();
            }
        }

        private void PlaylistRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyPlaylistSortAndFilter();
        }

        private async void CreatePlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            string selectedCoverPath = string.Empty;

            var dialog = new ContentDialog
            {
                Title = "创建歌单",
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            // Horizontal layout: cover on left, name + description on right
            var rootGrid = new Grid { ColumnSpacing = 6, Width = 520 };
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left: Cover image picker (1:1 square)
            var coverImage = new Image
            {
                Width = 200,
                Height = 200,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var coverPlaceholder = new FontIcon
            {
                FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"),
                Glyph = "\uE710",
                FontSize = 48,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var coverOverlay = new Border
            {
                IsHitTestVisible = false,
                Background = new SolidColorBrush(Colors.Transparent),
                CornerRadius = new CornerRadius(8)
            };
            var coverBorder = new Border
            {
                Width = 200,
                Height = 200,
                Margin = new Thickness(25, 0, 3, 0),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x33, 0, 0, 0)),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                Child = new Grid { Children = { coverPlaceholder, coverImage, coverOverlay } }
            };

            // Hover/Press visual feedback (universal, works on Win10 and Win11)
            coverBorder.PointerEntered += (_, _) => coverOverlay.Background = new SolidColorBrush(ColorHelper.FromArgb(0x14, 0, 0, 0));
            coverBorder.PointerExited += (_, _) => coverOverlay.Background = new SolidColorBrush(Colors.Transparent);
            coverBorder.PointerPressed += (_, _) => coverOverlay.Background = new SolidColorBrush(ColorHelper.FromArgb(0x28, 0, 0, 0));
            coverBorder.PointerReleased += (_, _) => coverOverlay.Background = new SolidColorBrush(ColorHelper.FromArgb(0x14, 0, 0, 0));

            coverBorder.Tapped += async (_, _) =>
            {
                try
                {
                    var picker = new FileOpenPicker();
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                    picker.FileTypeFilter.Add(".jpg");
                    picker.FileTypeFilter.Add(".jpeg");
                    picker.FileTypeFilter.Add(".png");
                    picker.FileTypeFilter.Add(".bmp");
                    picker.FileTypeFilter.Add(".webp");
                    picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

                    var file = await picker.PickSingleFileAsync();
                    if (file != null)
                    {
                        var croppedPath = await ImageCropDialog.ShowAsync(XamlRoot, file);
                        Debug.WriteLine($"[MusicPage] 裁剪结果: {(croppedPath ?? "null")}");
                        if (!string.IsNullOrEmpty(croppedPath) && File.Exists(croppedPath))
                        {
                            selectedCoverPath = croppedPath;
                            try
                            {
                                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                                byte[] imgBytes = await File.ReadAllBytesAsync(croppedPath);
                                using var mem = new MemoryStream(imgBytes, writable: false);
                                await bitmap.SetSourceAsync(mem.AsRandomAccessStream());
                                coverImage.Source = bitmap;
                                coverPlaceholder.Visibility = Visibility.Collapsed;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[MusicPage] 封面预览加载失败: {ex.GetType().Name}: {ex.Message}");
                                AppLogger.Error(ex, "封面裁剪结果图片加载失败");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "创建歌单封面选择失败");
                }
            };

            Grid.SetColumn(coverBorder, 0);
            rootGrid.Children.Add(coverBorder);

            // Right: Name + Description
            var rightPanel = new StackPanel { Spacing = 12 };
            var textBox = new TextBox { PlaceholderText = "歌单名称", Width = 250 };
            rightPanel.Children.Add(textBox);

            var descBox = new TextBox
            {
                PlaceholderText = "歌单描述（可选）",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 150,
                MaxHeight = 200,
                Width = 250
            };
            rightPanel.Children.Add(descBox);

            Grid.SetColumn(rightPanel, 1);
            rootGrid.Children.Add(rightPanel);

            dialog.Content = rootGrid;

            var result = await DialogService.ShowAsync(dialog, XamlRoot);
            if (result != ContentDialogResult.Primary)
                return;

            string name = textBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
                name = "无标题";

            var playlist = new Playlist
            {
                Name = name,
                Description = descBox.Text?.Trim() ?? string.Empty,
                CoverPath = selectedCoverPath
            };
            _allPlaylists.Add(playlist);
            SavePlaylists();
            ApplyPlaylistSortAndFilter();
        }

        private void PlaylistGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_playlistMultiSelectMode && e.ClickedItem is Playlist msItem)
            {
                TogglePlaylistItemSelection(msItem);
                return;
            }

            if (e.ClickedItem is Playlist playlist)
                OpenPlaylistDetail(playlist);
        }

        private void PlaylistItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_playlistMultiSelectMode)
            {
                // 双击卡片时 ItemClick 会触发两次（第二次延迟），若此处再 Toggle 将产生 3 次切换，
                // 导致第二次点击的取消操作被吞掉。此处仅拦截手势，切换交给两次 ItemClick 完成。
                e.Handled = true;
                return;
            }
            e.Handled = true;
        }

        private void PlaylistItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: Playlist playlist })
                return;

            e.Handled = true;
            var menu = new MenuFlyout();

            var openItem = new MenuFlyoutItem
            {
                Text = "打开",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            openItem.Click += (_, _) => OpenPlaylistDetail(playlist);
            menu.Items.Add(openItem);

            // 固定到侧边栏（歌单）
            var pinItem = new MenuFlyoutItem
            {
                Text = SidebarShortcutService.IsPinned(playlist.Id) ? "从侧边栏取消固定" : "固定到侧边栏",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE71B" }
            };
            pinItem.Click += (_, _) =>
            {
                if (SidebarShortcutService.IsPinned(playlist.Id))
                    SidebarShortcutService.Remove(playlist.Id);
                else
                    SidebarShortcutService.Add(new SidebarShortcut
                    {
                        Type = SidebarShortcutType.MusicPlaylist,
                        Title = $"音乐歌单：{playlist.Name}",
                        Name = playlist.Name,
                        Key = playlist.Id
                    });
            };
            menu.Items.Add(pinItem);

            var playItem = new MenuFlyoutItem
            {
                Text = "播放全部",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            playItem.Click += async (_, _) =>
            {
                if (playlist.Items.Count > 0)
                    await PlayMusicAsync(playlist.Items[0], playlist.Items);
            };
            menu.Items.Add(playItem);

            var editItem = new MenuFlyoutItem
            {
                Text = "编辑歌单",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            editItem.Click += async (_, _) =>
            {
                await PlaylistDetailPage.ShowEditDialogAsync(
                    playlist,
                    () =>
                    {
                        SavePlaylists();
                        // 歌单重命名后同步侧边栏固定项名称/标题
                        MainWindow.NotifyDetailSaved(SidebarShortcutType.MusicPlaylist, playlist.Id, playlist.Name);
                    },
                    XamlRoot);
                ApplyPlaylistSortAndFilter();
            };
            menu.Items.Add(editItem);

            var deleteItem = new MenuFlyoutItem
            {
                Text = "删除",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            deleteItem.Click += (_, _) =>
            {
                _allPlaylists.Remove(playlist);
                SavePlaylists();
                ApplyPlaylistSortAndFilter();
            };
            menu.Items.Add(deleteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var selectItem = new MenuFlyoutItem
            {
                Text = "选择",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE73E" }
            };
            selectItem.Click += (_, _) => EnterMultiSelectMode(1, playlist);
            menu.Items.Add(selectItem);

            var element = sender as FrameworkElement;
            if (element != null)
            {
                menu.ShowAt(element, e.GetPosition(element));
            }
        }

        private void OpenPlaylistDetail(Playlist playlist)
        {
            NavigateToDetailPage(typeof(PlaylistDetailPage), new PlaylistDetailArgs
            {
                Playlist = playlist,
                SaveChanges = MusicDataCache.SavePlaylists
            });
        }

        private async Task PlayMusicAsync(MediaItem item)
        {
            var queue = (_filteredMusic.Count > 0 ? _filteredMusic : _allMusic).ToList();
            await App.MusicPlayback.PlayAsync(item, queue);
        }

        private async Task PlayMusicAsync(MediaItem item, List<MediaItem> queue)
        {
            await App.MusicPlayback.PlayAsync(item, queue);
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

        private void SetupTabBar()
        {
            TabSelectorGrid.PointerPressed += TabSelector_PointerPressed;
            TabSelectorGrid.PointerMoved += TabSelector_PointerMoved;
            TabSelectorGrid.PointerReleased += TabSelector_PointerReleased;
            TabSelectorGrid.PointerExited += TabSelector_PointerExited;
            TabSelectorGrid.PointerCanceled += TabSelector_PointerCanceled;

            ((TabCursorGrid)TabSelectorGrid).SetHandCursor();

            SelectTab(0);
        }

        private void SelectTab(int index)
        {
            if (index == _selectedTabIndex)
                return;

            if (_multiSelectActiveTab >= 0)
                ExitMultiSelectMode();

            _selectedTabIndex = index;
            ClearHoverStates();
            AnimateIndicator(index);
            UpdateContentVisibility();
            SubscribeActiveContainerEvents();

            if (index == 1)
            {
                MusicToolbarGrid.Visibility = Visibility.Collapsed;
                MultiSelectToolbarGrid.Visibility = Visibility.Collapsed;
                PlaylistToolbarGrid.Visibility = Visibility.Visible;
                ArtistToolbarGrid.Visibility = Visibility.Collapsed;
                AlbumToolbarGrid.Visibility = Visibility.Collapsed;
                FolderToolbarGrid.Visibility = Visibility.Collapsed;

                PlaylistSearchBox.PlaceholderText = "搜索歌单";
                PlaylistViewModeComboBox.SelectedIndex = 1;

                if (!_playlistSortInitialized)
                {
                    PlaylistSortComboBox.SelectedIndex = 1;
                    _playlistSortInitialized = true;
                }

                ApplyPlaylistSortAndFilter();
            }
            else if (index == 2)
            {
                MusicToolbarGrid.Visibility = Visibility.Collapsed;
                MultiSelectToolbarGrid.Visibility = Visibility.Collapsed;
                PlaylistToolbarGrid.Visibility = Visibility.Collapsed;
                ArtistToolbarGrid.Visibility = Visibility.Visible;
                AlbumToolbarGrid.Visibility = Visibility.Collapsed;
                FolderToolbarGrid.Visibility = Visibility.Collapsed;

                ArtistSearchBox.PlaceholderText = "搜索歌手";
                ArtistSortComboBox.SelectedIndex = ArtistSortComboBox.SelectedIndex < 0 ? 0 : ArtistSortComboBox.SelectedIndex;
                ApplyArtistSortAndFilter();
            }
            else if (index == 3)
            {
                MusicToolbarGrid.Visibility = Visibility.Collapsed;
                MultiSelectToolbarGrid.Visibility = Visibility.Collapsed;
                PlaylistToolbarGrid.Visibility = Visibility.Collapsed;
                ArtistToolbarGrid.Visibility = Visibility.Collapsed;
                AlbumToolbarGrid.Visibility = Visibility.Visible;
                FolderToolbarGrid.Visibility = Visibility.Collapsed;

                AlbumSearchBox.PlaceholderText = "搜索专辑";
                AlbumSortComboBox.SelectedIndex = AlbumSortComboBox.SelectedIndex < 0 ? 0 : AlbumSortComboBox.SelectedIndex;
                ApplyAlbumSortAndFilter();
            }
            else if (index == 4)
            {
                MusicToolbarGrid.Visibility = Visibility.Collapsed;
                MultiSelectToolbarGrid.Visibility = Visibility.Collapsed;
                PlaylistToolbarGrid.Visibility = Visibility.Collapsed;
                ArtistToolbarGrid.Visibility = Visibility.Collapsed;
                AlbumToolbarGrid.Visibility = Visibility.Collapsed;
                FolderToolbarGrid.Visibility = Visibility.Visible;

                FolderSearchBox.PlaceholderText = "搜索文件夹";
                FolderSortComboBox.SelectedIndex = FolderSortComboBox.SelectedIndex < 0 ? 0 : FolderSortComboBox.SelectedIndex;
                ApplyFolderSortAndFilter();
            }
            else
            {
                MusicToolbarGrid.Visibility = Visibility.Visible;
                MultiSelectToolbarGrid.Visibility = Visibility.Collapsed;
                PlaylistToolbarGrid.Visibility = Visibility.Collapsed;
                ArtistToolbarGrid.Visibility = Visibility.Collapsed;
                AlbumToolbarGrid.Visibility = Visibility.Collapsed;
                FolderToolbarGrid.Visibility = Visibility.Collapsed;

                MusicSearchBox.PlaceholderText = "搜索音乐";
                MusicViewModeComboBox.SelectedIndex = App.SettingsHelper.MusicRememberView
                    ? Math.Clamp(App.SettingsHelper.MusicDefaultView, 0, 1)
                    : 0;
                MusicSortComboBox.SelectedIndex = App.SettingsHelper.MusicRememberSort
                    ? Math.Clamp(App.SettingsHelper.MusicDefaultSort, 0, 3)
                    : -1;

                ApplyMusicSortAndFilter();
            }
        }

        private void UpdateContentVisibility()
        {
            int index = _selectedTabIndex;
            AllContentPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            PlaylistContentHost.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            ArtistContentPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            AlbumContentPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
            FolderContentPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

            MusicToolbarGrid.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            MultiSelectToolbarGrid.Visibility = Visibility.Collapsed;
            PlaylistToolbarGrid.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            ArtistToolbarGrid.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            AlbumToolbarGrid.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
            FolderToolbarGrid.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

            if (index != 1)
            {
                PlaylistEmptyStateText.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearHoverStates()
        {
            SetHoverState(0, false);
            SetHoverState(1, false);
            SetHoverState(2, false);
            SetHoverState(3, false);
            SetHoverState(4, false);
        }

        private void SetHoverState(int index, bool hovered)
        {
            Border overlay = index switch
            {
                0 => HoverOverlay0,
                1 => HoverOverlay1,
                2 => HoverOverlay2,
                3 => HoverOverlay3,
                4 => HoverOverlay4,
                _ => null!
            };

            if (overlay == null)
                return;

            byte alpha = 0x0A;
            byte rgb = ActualTheme == ElementTheme.Dark ? (byte)0xFF : (byte)0x00;
            overlay.Background = hovered
                ? new SolidColorBrush(ColorHelper.FromArgb(alpha, rgb, rgb, rgb))
                : new SolidColorBrush(Colors.Transparent);
        }

        /// <summary>
        /// 将标签指示器移动到指定位置。
        /// </summary>
        private void AnimateIndicator(int index)
        {
            double targetX = index * TabFixedWidth;
            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, TabIndicator);
            Storyboard.SetTargetProperty(
                animation,
                "(UIElement.RenderTransform).(TranslateTransform.X)");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void TabSelector_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(TabSelectorGrid).Position;
            var translate = (TranslateTransform)TabIndicator.RenderTransform;
            var indicatorRect = new Rect(translate.X, 0, TabFixedWidth, TabSelectorGrid.ActualHeight);

            if (indicatorRect.Contains(pt))
            {
                _tabIsDragging = true;
                TabSelectorGrid.CapturePointer(e.Pointer);
                ((TabCursorGrid)TabSelectorGrid).SetGrabCursor();
            }
        }

        private void TabSelector_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(TabSelectorGrid).Position;

            if (_tabIsDragging)
            {
                int dragIndex = (int)(pt.X / TabFixedWidth);
                if (dragIndex >= 0 && dragIndex < 5 && dragIndex != _selectedTabIndex)
                {
                    SelectTab(dragIndex);
                }
                return;
            }

            int hoveredIndex = (int)(pt.X / TabFixedWidth);
            if (hoveredIndex >= 0 && hoveredIndex < 5)
            {
                _hoveredTabIndex = hoveredIndex;
                for (int i = 0; i < 5; i++)
                    SetHoverState(i, i == hoveredIndex && i != _selectedTabIndex);
            }
        }

        private void TabSelector_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_tabIsDragging)
            {
                _tabIsDragging = false;
                TabSelectorGrid.ReleasePointerCapture(e.Pointer);
                ((TabCursorGrid)TabSelectorGrid).SetHandCursor();
                return;
            }

            var pt = e.GetCurrentPoint(TabSelectorGrid).Position;
            int clickedIndex = (int)(pt.X / TabFixedWidth);
            if (clickedIndex >= 0 && clickedIndex < 5 && clickedIndex != _selectedTabIndex)
                SelectTab(clickedIndex);
        }

        private void TabSelector_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_tabIsDragging)
            {
                _hoveredTabIndex = -1;
                ClearHoverStates();
            }
        }

        private void TabSelector_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (_tabIsDragging)
            {
                _tabIsDragging = false;
                TabSelectorGrid.ReleasePointerCapture(null);
                ((TabCursorGrid)TabSelectorGrid).SetHandCursor();
            }
        }

        private void SavePlaylists()
        {
            MusicDataCache.SavePlaylists();
        }

        private void LoadPlaylists()
        {
            MusicDataCache.LoadPlaylists();
            _allPlaylists = MusicDataCache.AllPlaylists;
        }

        private void BuildArtistGroups()
        {
            MusicDataCache.RebuildDerivedGroups();
            _artistGroups = MusicDataCache.ArtistGroups;
        }

        private void BuildAlbumGroups()
        {
            MusicDataCache.RebuildDerivedGroups();
            _albumGroups = MusicDataCache.AlbumGroups;
        }

        private void BuildFolderGroups()
        {
            MusicDataCache.RebuildDerivedGroups();
            _folderGroups = MusicDataCache.FolderGroups;
        }

        private void ApplyArtistSortAndFilter()
        {
            if (ArtistSearchBox == null || ArtistSortComboBox == null || ArtistViewModeComboBox == null)
                return;

            string searchText = ArtistSearchBox.Text?.Trim() ?? string.Empty;
            int sortIndex = ArtistSortComboBox.SelectedIndex;

            IEnumerable<ArtistGroup> query = _artistGroups;

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                query = query.Where(a =>
                    a.ArtistName.Contains(searchText, StringComparison.OrdinalIgnoreCase));
            }

            query = sortIndex switch
            {
                0 => query.OrderBy(a => a.ArtistName, StringComparer.OrdinalIgnoreCase),
                1 => query.OrderByDescending(a => a.SongCount),
                _ => query.OrderBy(a => a.ArtistName, StringComparer.OrdinalIgnoreCase)
            };

            _filteredArtistGroups = query.ToList();
            RefreshArtistView();
        }

        private void ApplyAlbumSortAndFilter()
        {
            if (AlbumSearchBox == null || AlbumSortComboBox == null || AlbumViewModeComboBox == null)
                return;

            string searchText = AlbumSearchBox.Text?.Trim() ?? string.Empty;
            int sortIndex = AlbumSortComboBox.SelectedIndex;

            IEnumerable<AlbumGroup> query = _albumGroups;

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                query = query.Where(a =>
                    a.AlbumName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    a.Artist.Contains(searchText, StringComparison.OrdinalIgnoreCase));
            }

            query = sortIndex switch
            {
                0 => query.OrderBy(a => a.AlbumName, StringComparer.OrdinalIgnoreCase),
                1 => query.OrderByDescending(a => a.Year),
                2 => query.OrderByDescending(a => a.SongCount),
                _ => query.OrderBy(a => a.AlbumName, StringComparer.OrdinalIgnoreCase)
            };

            _filteredAlbumGroups = query.ToList();
            RefreshAlbumView();
        }

        private void ApplyFolderSortAndFilter()
        {
            if (FolderSearchBox == null || FolderSortComboBox == null)
                return;

            string searchText = FolderSearchBox.Text?.Trim() ?? string.Empty;
            int sortIndex = FolderSortComboBox.SelectedIndex;

            IEnumerable<FolderGroup> query = _folderGroups;

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                query = query.Where(f =>
                    f.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    f.FolderPath.Contains(searchText, StringComparison.OrdinalIgnoreCase));
            }

            query = sortIndex switch
            {
                0 => query.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase),
                1 => query.OrderByDescending(f => f.SongCount),
                _ => query.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
            };

            _filteredFolderGroups = query.ToList();
            RefreshFolderView();
        }

        private void RefreshArtistView()
        {
            if (ArtistEmptyStateText == null || ArtistList == null || ArtistGrid == null || ArtistWaterfallGrid == null)
                return;

            bool isEmpty = _filteredArtistGroups.Count == 0;
            int mode = ArtistViewModeComboBox.SelectedIndex;

            ArtistEmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

            bool showList = mode == 0;
            bool showGrid = mode == 1;
            bool showWaterfall = mode == 2;

            ArtistListHeader.Visibility = showList && !isEmpty ? Visibility.Visible : Visibility.Collapsed;
            ArtistList.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
            ArtistGrid.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
            ArtistWaterfallGrid.Visibility = showWaterfall ? Visibility.Visible : Visibility.Collapsed;

            ArtistList.ItemsSource = showList && !isEmpty ? _filteredArtistGroups : null;
            ArtistGrid.ItemsSource = showGrid && !isEmpty ? _filteredArtistGroups : null;
            ArtistWaterfallGrid.ItemsSource = showWaterfall && !isEmpty ? _filteredArtistGroups : null;

            if (showWaterfall)
                UpdateWaterfallItemWidth();
        }

        /// <summary>
        /// 根据可用内容宽度动态计算瀑布流列数（2-4列）和项宽度。
        /// </summary>
        private static void UpdateWaterfallLayout(GridView grid, ItemsWrapGrid panel)
        {
            // 减去左右 Padding 得到实际内容宽度
            double contentWidth = grid.ActualWidth - grid.Padding.Left - grid.Padding.Right;
            if (contentWidth <= 0) return;

            // 动态列数：根据最小列宽 250px 计算，限制在 2-4 列
            const double minColumnWidth = 250;
            const double itemGap = 4; // 项右间距 (Margin="0,0,4,4")
            int columns = Math.Clamp((int)(contentWidth / minColumnWidth), 2, 4);

            // 项宽度 = (内容宽度 - 列间间距) / 列数
            panel.ItemWidth = (contentWidth - (columns - 1) * itemGap) / columns;
        }

        private void UpdateWaterfallItemWidth()
        {
            if (ArtistWaterfallGrid?.ItemsPanelRoot is not ItemsWrapGrid panel || ArtistWaterfallGrid.ActualWidth <= 0)
                return;
            UpdateWaterfallLayout(ArtistWaterfallGrid, panel);
        }

        private void ArtistWaterfallGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ArtistViewModeComboBox.SelectedIndex == 2)
                UpdateWaterfallItemWidth();
        }

        private void UpdateMusicWaterfallItemWidth()
        {
            if (MusicWaterfallGrid?.ItemsPanelRoot is not ItemsWrapGrid panel || MusicWaterfallGrid.ActualWidth <= 0)
                return;
            UpdateWaterfallLayout(MusicWaterfallGrid, panel);
        }

        private void MusicWaterfallGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (MusicViewModeComboBox.SelectedIndex == 2)
                UpdateMusicWaterfallItemWidth();
        }

        private void UpdatePlaylistWaterfallItemWidth()
        {
            if (PlaylistWaterfallGrid?.ItemsPanelRoot is not ItemsWrapGrid panel || PlaylistWaterfallGrid.ActualWidth <= 0)
                return;
            UpdateWaterfallLayout(PlaylistWaterfallGrid, panel);
        }

        private void PlaylistWaterfallGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PlaylistViewModeComboBox.SelectedIndex == 2)
                UpdatePlaylistWaterfallItemWidth();
        }

        private void UpdateAlbumWaterfallItemWidth()
        {
            if (AlbumWaterfallGrid?.ItemsPanelRoot is not ItemsWrapGrid panel || AlbumWaterfallGrid.ActualWidth <= 0)
                return;
            UpdateWaterfallLayout(AlbumWaterfallGrid, panel);
        }

        private void AlbumWaterfallGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (AlbumViewModeComboBox.SelectedIndex == 2)
                UpdateAlbumWaterfallItemWidth();
        }

        private void UpdateFolderWaterfallItemWidth()
        {
            if (FolderWaterfallGrid?.ItemsPanelRoot is not ItemsWrapGrid panel || FolderWaterfallGrid.ActualWidth <= 0)
                return;
            UpdateWaterfallLayout(FolderWaterfallGrid, panel);
        }

        private void FolderWaterfallGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (FolderViewModeComboBox.SelectedIndex == 2)
                UpdateFolderWaterfallItemWidth();
        }

        private void RefreshAlbumView()
        {
            if (AlbumEmptyStateText == null || AlbumList == null || AlbumGrid == null)
                return;

            bool isEmpty = _filteredAlbumGroups.Count == 0;
            int mode = AlbumViewModeComboBox.SelectedIndex;

            AlbumEmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            AlbumListHeader.Visibility = mode == 0 && !isEmpty ? Visibility.Visible : Visibility.Collapsed;
            AlbumList.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
            AlbumGrid.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (AlbumWaterfallGrid != null)
                AlbumWaterfallGrid.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;

            AlbumList.ItemsSource = mode == 0 && !isEmpty ? _filteredAlbumGroups : null;
            AlbumGrid.ItemsSource = mode == 1 && !isEmpty ? _filteredAlbumGroups : null;
            if (AlbumWaterfallGrid != null)
            {
                AlbumWaterfallGrid.ItemsSource = mode == 2 && !isEmpty ? _filteredAlbumGroups : null;
                if (mode == 2) UpdateAlbumWaterfallItemWidth();
            }
        }

        private void RefreshFolderView()
        {
            if (FolderEmptyStateText == null || FolderList == null)
                return;

            bool isEmpty = _filteredFolderGroups.Count == 0;
            bool showList = FolderViewModeComboBox != null && FolderViewModeComboBox.SelectedIndex == 0;
            bool showGrid = FolderViewModeComboBox != null && FolderViewModeComboBox.SelectedIndex == 1;
            bool showWaterfall = FolderViewModeComboBox != null && FolderViewModeComboBox.SelectedIndex == 2;
            bool hasModeSelector = FolderViewModeComboBox != null;

            FolderEmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            FolderListHeader.Visibility = showList && !isEmpty ? Visibility.Visible : Visibility.Collapsed;

            if (!hasModeSelector)
            {
                FolderList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
                FolderList.ItemsSource = isEmpty ? null : _filteredFolderGroups;
                return;
            }

            if (isEmpty)
            {
                FolderList.Visibility = Visibility.Collapsed;
                if (FolderGrid != null) FolderGrid.Visibility = Visibility.Collapsed;
                if (FolderWaterfallGrid != null) FolderWaterfallGrid.Visibility = Visibility.Collapsed;
                FolderList.ItemsSource = null;
                if (FolderGrid != null) FolderGrid.ItemsSource = null;
                if (FolderWaterfallGrid != null) FolderWaterfallGrid.ItemsSource = null;
                return;
            }

            FolderList.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
            if (FolderGrid != null) FolderGrid.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
            if (FolderWaterfallGrid != null) FolderWaterfallGrid.Visibility = showWaterfall ? Visibility.Visible : Visibility.Collapsed;

            FolderList.ItemsSource = showList ? _filteredFolderGroups : null;
            if (FolderGrid != null) FolderGrid.ItemsSource = showGrid ? _filteredFolderGroups : null;
            if (FolderWaterfallGrid != null)
            {
                FolderWaterfallGrid.ItemsSource = showWaterfall ? _filteredFolderGroups : null;
                if (showWaterfall) UpdateFolderWaterfallItemWidth();
            }
        }

        private void ArtistSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyArtistSortAndFilter();
        }

        private void AlbumSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyAlbumSortAndFilter();
        }

        private void FolderSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyFolderSortAndFilter();
        }

        private void ArtistViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || ArtistViewModeComboBox == null || ArtistViewModeComboBox.SelectedIndex < 0)
                return;

            if (ArtistList != null)
                ApplyArtistSortAndFilter();

            if (App.SettingsHelper.ArtistRememberView)
            {
                App.SettingsHelper.ArtistDefaultView = ArtistViewModeComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void AlbumViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || AlbumViewModeComboBox == null || AlbumViewModeComboBox.SelectedIndex < 0)
                return;

            if (AlbumList != null)
                ApplyAlbumSortAndFilter();

            if (App.SettingsHelper.AlbumRememberView)
            {
                App.SettingsHelper.AlbumDefaultView = AlbumViewModeComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void FolderViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || FolderViewModeComboBox == null || FolderViewModeComboBox.SelectedIndex < 0)
                return;

            if (FolderList != null)
                ApplyFolderSortAndFilter();

            if (App.SettingsHelper.FolderRememberView)
            {
                App.SettingsHelper.FolderDefaultView = FolderViewModeComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void ArtistSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || ArtistSortComboBox == null || ArtistSortComboBox.SelectedIndex < 0)
                return;

            if (ArtistList != null)
                ApplyArtistSortAndFilter();

            if (App.SettingsHelper.ArtistRememberSort)
            {
                App.SettingsHelper.ArtistDefaultSort = ArtistSortComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void AlbumSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || AlbumSortComboBox == null || AlbumSortComboBox.SelectedIndex < 0)
                return;

            if (AlbumList != null)
                ApplyAlbumSortAndFilter();

            if (App.SettingsHelper.AlbumRememberSort)
            {
                App.SettingsHelper.AlbumDefaultSort = AlbumSortComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void FolderSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || FolderSortComboBox == null || FolderSortComboBox.SelectedIndex < 0)
                return;

            if (FolderList != null)
                ApplyFolderSortAndFilter();

            if (App.SettingsHelper.FolderRememberSort)
            {
                App.SettingsHelper.FolderDefaultSort = FolderSortComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void ArtistList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_artistMultiSelectMode && e.ClickedItem is ArtistGroup msItem)
            {
                ToggleArtistItemSelection(msItem);
                return;
            }
            if (e.ClickedItem is ArtistGroup artist)
                OpenArtistDetail(artist);
        }

        private void AlbumList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_albumMultiSelectMode && e.ClickedItem is AlbumGroup msItem)
            {
                ToggleAlbumItemSelection(msItem);
                return;
            }
            if (e.ClickedItem is AlbumGroup album)
                OpenAlbumDetail(album);
        }

        private void FolderList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_folderMultiSelectMode && e.ClickedItem is FolderGroup msItem)
            {
                ToggleFolderItemSelection(msItem);
                return;
            }
            if (e.ClickedItem is FolderGroup folder)
                OpenFolderDetail(folder);
        }

        private void ArtistItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_artistMultiSelectMode)
            {
                // 双击卡片时 ItemClick 会触发两次（第二次延迟），若此处再 Toggle 将产生 3 次切换，
                // 导致第二次点击的取消操作被吞掉。此处仅拦截手势，切换交给两次 ItemClick 完成。
                e.Handled = true;
                return;
            }
            e.Handled = true;
            if (sender is FrameworkElement { Tag: ArtistGroup artist })
                OpenArtistDetail(artist);
        }

        private void AlbumItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_albumMultiSelectMode)
            {
                // 双击卡片时 ItemClick 会触发两次（第二次延迟），若此处再 Toggle 将产生 3 次切换，
                // 导致第二次点击的取消操作被吞掉。此处仅拦截手势，切换交给两次 ItemClick 完成。
                e.Handled = true;
                return;
            }
            e.Handled = true;
            if (sender is FrameworkElement { Tag: AlbumGroup album })
                OpenAlbumDetail(album);
        }

        private void FolderItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_folderMultiSelectMode)
            {
                // 双击卡片时 ItemClick 会触发两次（第二次延迟），若此处再 Toggle 将产生 3 次切换，
                // 导致第二次点击的取消操作被吞掉。此处仅拦截手势，切换交给两次 ItemClick 完成。
                e.Handled = true;
                return;
            }
            e.Handled = true;
            if (sender is FrameworkElement { Tag: FolderGroup folder })
                OpenFolderDetail(folder);
        }

        private void ArtistItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: ArtistGroup artist } element)
                return;

            e.Handled = true;
            var menu = new MenuFlyout();

            var openItem = new MenuFlyoutItem
            {
                Text = "查看歌手",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            openItem.Click += (_, _) => OpenArtistDetail(artist);
            menu.Items.Add(openItem);

            // 固定到侧边栏（歌手）
            var pinItem = new MenuFlyoutItem
            {
                Text = SidebarShortcutService.IsPinned(artist.ArtistName) ? "从侧边栏取消固定" : "固定到侧边栏",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE71B" }
            };
            pinItem.Click += (_, _) =>
            {
                if (SidebarShortcutService.IsPinned(artist.ArtistName))
                    SidebarShortcutService.Remove(artist.ArtistName);
                else
                    SidebarShortcutService.Add(new SidebarShortcut
                    {
                        Type = SidebarShortcutType.MusicArtist,
                        Title = $"音乐歌手：{artist.ArtistName}",
                        Name = artist.ArtistName,
                        Key = artist.ArtistName
                    });
            };
            menu.Items.Add(pinItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var selectItem = new MenuFlyoutItem
            {
                Text = "选择",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE73E" }
            };
            selectItem.Click += (_, _) => EnterMultiSelectMode(2, artist);
            menu.Items.Add(selectItem);

            menu.ShowAt(element, e.GetPosition(element));
        }

        private void AlbumItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: AlbumGroup album } element)
                return;

            e.Handled = true;
            var menu = new MenuFlyout();

            var openItem = new MenuFlyoutItem
            {
                Text = "查看专辑",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            openItem.Click += (_, _) => OpenAlbumDetail(album);
            menu.Items.Add(openItem);

            // 固定到侧边栏（专辑）
            var albumKey = $"{album.AlbumName}|{album.Artist}";
            var pinItem = new MenuFlyoutItem
            {
                Text = SidebarShortcutService.IsPinned(albumKey) ? "从侧边栏取消固定" : "固定到侧边栏",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE71B" }
            };
            pinItem.Click += (_, _) =>
            {
                if (SidebarShortcutService.IsPinned(albumKey))
                    SidebarShortcutService.Remove(albumKey);
                else
                    SidebarShortcutService.Add(new SidebarShortcut
                    {
                        Type = SidebarShortcutType.MusicAlbum,
                        Title = $"音乐专辑：{album.AlbumName}",
                        Name = album.AlbumName,
                        SubName = album.Artist,
                        Key = albumKey
                    });
            };
            menu.Items.Add(pinItem);

            var playItem = new MenuFlyoutItem
            {
                Text = "播放全部",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            playItem.Click += async (_, _) =>
            {
                var songs = _allMusic
                    .Where(m => string.Equals(m.Album, album.AlbumName))
                    .ToList();
                if (songs.Count > 0)
                    await PlayMusicAsync(songs[0], songs);
            };
            menu.Items.Add(playItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var selectItem = new MenuFlyoutItem
            {
                Text = "选择",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE73E" }
            };
            selectItem.Click += (_, _) => EnterMultiSelectMode(3, album);
            menu.Items.Add(selectItem);

            menu.ShowAt(element, e.GetPosition(element));
        }

        private void FolderItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: FolderGroup folder } element)
                return;

            e.Handled = true;
            var menu = new MenuFlyout();

            var openItem = new MenuFlyoutItem
            {
                Text = "查看文件夹",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            openItem.Click += (_, _) => OpenFolderDetail(folder);
            menu.Items.Add(openItem);

            // 固定到侧边栏（音乐文件夹）
            var pinItem = new MenuFlyoutItem
            {
                Text = SidebarShortcutService.IsPinned(folder.FolderPath) ? "从侧边栏取消固定" : "固定到侧边栏",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE71B" }
            };
            pinItem.Click += (_, _) =>
            {
                if (SidebarShortcutService.IsPinned(folder.FolderPath))
                    SidebarShortcutService.Remove(folder.FolderPath);
                else
                    SidebarShortcutService.Add(new SidebarShortcut
                    {
                        Type = SidebarShortcutType.MusicFolder,
                        Title = $"音乐文件夹：{folder.DisplayName}",
                        Name = folder.DisplayName,
                        Key = folder.FolderPath
                    });
            };
            menu.Items.Add(pinItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var selectItem = new MenuFlyoutItem
            {
                Text = "选择",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE73E" }
            };
            selectItem.Click += (_, _) => EnterMultiSelectMode(4, folder);
            menu.Items.Add(selectItem);

            menu.ShowAt(element, e.GetPosition(element));
        }

        private void OpenArtistDetail(ArtistGroup artist)
        {
            var songs = _allMusic
                .Where(m => string.Equals(m.ArtistDisplay, artist.ArtistName, StringComparison.OrdinalIgnoreCase) ||
                            (string.IsNullOrWhiteSpace(m.Artist) && artist.ArtistName == "未知艺术家"))
                .ToList();

            NavigateToDetailPage(typeof(ArtistDetailPage), new ArtistDetailArgs
            {
                ArtistName = artist.ArtistName,
                Songs = songs
            });
        }

        private void OpenAlbumDetail(AlbumGroup album)
        {
            var songs = _allMusic
                .Where(m => string.Equals(m.AlbumDisplay, album.AlbumName, StringComparison.OrdinalIgnoreCase) ||
                            (string.IsNullOrWhiteSpace(m.Album) && album.AlbumName == "未知专辑"))
                .OrderBy(m => m.TrackNumber)
                .ToList();

            NavigateToDetailPage(typeof(AlbumDetailPage), new AlbumDetailArgs
            {
                AlbumName = album.AlbumName,
                Artist = album.Artist,
                Songs = songs
            });
        }

        private void OpenFolderDetail(FolderGroup folder)
        {
            var songs = _allMusic
                .Where(m => string.Equals(Path.GetDirectoryName(m.FilePath), folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            NavigateToDetailPage(typeof(FolderDetailPage), new FolderDetailArgs
            {
                FolderPath = folder.FolderPath,
                Songs = songs
            });
        }

        private static (List<string> Paths, bool Recursive) LoadLibrarySettings()
        {
            var paths = new List<string>();
            bool recursive = true;
            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SightoHear",
                "settings.json");

            bool hasConfiguredPaths = false;
            if (File.Exists(settingsPath))
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(settingsPath));
                    if (node?["MusicLibraryPaths"] is JsonArray pathsArray)
                    {
                        hasConfiguredPaths = true;
                        foreach (var value in pathsArray)
                        {
                            string? path = value?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                                paths.Add(path);
                        }
                    }

                    recursive = node?["MusicRecursiveScan"]?.GetValue<bool>() ?? true;
                }
                catch
                {
                }
            }

            if (!hasConfiguredPaths)
            {
                string defaultPath = MediaScanner.GetDefaultMusicPath();
                if (!string.IsNullOrWhiteSpace(defaultPath) && Directory.Exists(defaultPath))
                    paths.Add(defaultPath);
            }

            return (paths, recursive);
        }

        #region 多选功能
        private void EnterMultiSelectMode(int tabIndex, object? starter = null)
        {
            _multiSelectActiveTab = tabIndex;
            MusicToolbarGrid.Visibility = Visibility.Collapsed;
            PlaylistToolbarGrid.Visibility = Visibility.Collapsed;
            ArtistToolbarGrid.Visibility = Visibility.Collapsed;
            AlbumToolbarGrid.Visibility = Visibility.Collapsed;
            FolderToolbarGrid.Visibility = Visibility.Collapsed;
            MultiSelectToolbarGrid.Visibility = Visibility.Visible;

            switch (tabIndex)
            {
                case 0:
                    EnterMusicMultiSelectMode(starter as MediaItem);
                    break;
                case 1:
                    EnterPlaylistMultiSelectMode(starter as Playlist);
                    break;
                case 2:
                    EnterArtistMultiSelectMode(starter as ArtistGroup);
                    break;
                case 3:
                    EnterAlbumMultiSelectMode(starter as AlbumGroup);
                    break;
                case 4:
                    EnterFolderMultiSelectMode(starter as FolderGroup);
                    break;
            }
        }

        private void ExitMultiSelectMode()
        {
            switch (_multiSelectActiveTab)
            {
                case 0:
                    ExitMusicMultiSelectMode();
                    break;
                case 1:
                    ExitPlaylistMultiSelectMode();
                    break;
                case 2:
                    ExitArtistMultiSelectMode();
                    break;
                case 3:
                    ExitAlbumMultiSelectMode();
                    break;
                case 4:
                    ExitFolderMultiSelectMode();
                    break;
            }

            MultiSelectToolbarGrid.Visibility = Visibility.Collapsed;
            _multiSelectActiveTab = -1;

            if (_selectedTabIndex == 0)
                MusicToolbarGrid.Visibility = Visibility.Visible;
            else if (_selectedTabIndex == 1)
                PlaylistToolbarGrid.Visibility = Visibility.Visible;
            else if (_selectedTabIndex == 2)
                ArtistToolbarGrid.Visibility = Visibility.Visible;
            else if (_selectedTabIndex == 3)
                AlbumToolbarGrid.Visibility = Visibility.Visible;
            else if (_selectedTabIndex == 4)
                FolderToolbarGrid.Visibility = Visibility.Visible;
        }

        #region 音乐多选
        private void EnterMusicMultiSelectMode(MediaItem? starter = null)
        {
            _isMultiSelectMode = true;
            _multiSelectedPaths.Clear();

            if (starter != null)
                _multiSelectedPaths.Add(starter.FilePath);

            MultiSelectAddToPlaylistButton.Visibility = Visibility.Visible;
            MultiSelectPlayAllButton.Visibility = Visibility.Collapsed;
            MultiSelectSearchBox.PlaceholderText = "搜索音乐";
            MultiSelectToggleButton.IsChecked = true;

            UpdateAllItemCheckBoxes();
            UpdateMusicMultiSelectCount();
        }

        private void ExitMusicMultiSelectMode()
        {
            _isMultiSelectMode = false;
            _multiSelectedPaths.Clear();

            MultiSelectToggleButton.IsChecked = false;

            UpdateAllItemCheckBoxes();
            UpdateMusicMultiSelectCount();
        }

        private void ToggleMusicItemSelection(MediaItem item)
        {
            if (_multiSelectedPaths.Contains(item.FilePath))
                _multiSelectedPaths.Remove(item.FilePath);
            else
                _multiSelectedPaths.Add(item.FilePath);

            UpdateMusicMultiSelectCount();

            if (MusicList.ContainerFromItem(item) is ListViewItem container)
            {
                var checkbox = FindVisualChild<CheckBox>(container);
                if (checkbox != null)
                    checkbox.IsChecked = _multiSelectedPaths.Contains(item.FilePath);
            }
            if (MusicGrid.ContainerFromItem(item) is GridViewItem gridContainer)
            {
                var checkbox = FindVisualChild<CheckBox>(gridContainer);
                if (checkbox != null)
                    checkbox.IsChecked = _multiSelectedPaths.Contains(item.FilePath);
            }
            if (MusicWaterfallGrid.ContainerFromItem(item) is GridViewItem waterfallContainer)
            {
                var checkbox = FindVisualChild<CheckBox>(waterfallContainer);
                if (checkbox != null)
                    checkbox.IsChecked = _multiSelectedPaths.Contains(item.FilePath);
            }
        }

        private void UpdateMusicMultiSelectCount()
        {
            int count = _multiSelectedPaths.Count;
            int total = _filteredMusic.Count;

            MultiSelectCountText.Text = total > 0
                ? $"已选择 {count} / {total} 首歌曲"
                : "已选择 0 首歌曲";

            if (SelectAllCheckBox != null)
            {
                SelectAllCheckBox.IsChecked = count > 0 && count == total
                    ? true
                    : count == 0 ? false : null;
            }
        }

        private void UpdateAllItemCheckBoxes()
        {
            var visibility = _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;

            foreach (var item in _filteredMusic)
            {
                if (MusicList.ContainerFromItem(item) is ListViewItem container)
                {
                    var checkbox = FindVisualChild<CheckBox>(container);
                    if (checkbox != null)
                    {
                        checkbox.Visibility = visibility;
                        if (_isMultiSelectMode)
                            checkbox.IsChecked = _multiSelectedPaths.Contains(item.FilePath);
                    }
                }
                if (MusicGrid.ContainerFromItem(item) is GridViewItem gridContainer)
                {
                    var checkbox = FindVisualChild<CheckBox>(gridContainer);
                    if (checkbox != null)
                    {
                        checkbox.Visibility = visibility;
                        if (_isMultiSelectMode)
                            checkbox.IsChecked = _multiSelectedPaths.Contains(item.FilePath);
                    }
                }
                if (MusicWaterfallGrid.ContainerFromItem(item) is GridViewItem waterfallContainer)
                {
                    var checkbox = FindVisualChild<CheckBox>(waterfallContainer);
                    if (checkbox != null)
                    {
                        checkbox.Visibility = visibility;
                        if (_isMultiSelectMode)
                            checkbox.IsChecked = _multiSelectedPaths.Contains(item.FilePath);
                    }
                }
            }
        }

        private void MusicList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue)
                return;

            if (args.ItemContainer is ListViewItem container && args.Item is MediaItem item)
            {
                // ★ 修复：回收复用的容器可能残留旧的 Opacity=0（来自上一个未加载完封面的项），
                //   导致"透明卡片"bug——卡片不可见但可交互。每次绑定新项时强制重置为不透明。
                container.Opacity = 1.0;

                var checkbox = FindVisualChild<CheckBox>(container);
                if (checkbox != null)
                {
                    checkbox.Visibility = _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
                    checkbox.IsChecked = _multiSelectedPaths.Contains(item.FilePath);
                }
            }
        }

        private void MusicGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue)
                return;

            if (args.ItemContainer is GridViewItem container && args.Item is MediaItem item)
            {
                if (args.Phase == 0)
                {
                    // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置为不透明。
                    //   GridCoverLoadPhase 会在 Phase 1 中为未缓存的封面设置 Opacity=0 以实现阶梯淡入，
                    //   但已缓存的封面会直接设回 Opacity=1。此处重置确保不会出现"透明卡片"。
                    container.Opacity = 1.0;

                    var checkbox = FindVisualChild<CheckBox>(container);
                    if (checkbox != null)
                    {
                        checkbox.Visibility = _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
                        checkbox.IsChecked = _multiSelectedPaths.Contains(item.FilePath);
                    }
                    // 注册封面加载阶段
                    args.RegisterUpdateCallback(1, GridCoverLoadPhase);
                    args.Handled = true;
                }
            }
        }

        /// <summary>
        /// 通用封面加载阶段：在 Phase 1 中为网格项阶梯加载封面。
        /// </summary>
        private void GridCoverLoadPhase(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration)) return;

            // ★ 修复：再次确保容器 Opacity 为1（防御性重置，防止极端时序下残留透明状态）
            if (args.ItemContainer is FrameworkElement container)
                container.Opacity = 1.0;

            if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root)
                EnqueueCoverLoad(root, args.Item);
        }

        private void MusicItemCheckBox_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.DataContext is not MediaItem item)
                return;

            bool targetBefore = cb.IsChecked != true;
            _lastMusicCheckboxPointerPath = item.FilePath;
            _lastMusicCheckboxPointerTarget = targetBefore;
            _lastMusicCheckboxPointerTimestamp = Stopwatch.GetTimestamp();

            AppLogger.Debug($"[MusicPage][#{++_musicCheckboxEventSeq}] 音乐复选框 PointerPressed | Path={item.FilePath} | CheckboxIsChecked={cb.IsChecked} | RecordedTarget={_lastMusicCheckboxPointerTarget} | Selected={_multiSelectedPaths.Contains(item.FilePath)} | IsMultiSelect={_isMultiSelectMode}");
        }

        private void MusicItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            ApplyMusicItemCheckBoxState(sender, true, "Checked");
        }

        private void MusicItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            ApplyMusicItemCheckBoxState(sender, false, "Unchecked");
        }

        private void ApplyMusicItemCheckBoxState(object sender, bool eventState, string eventName)
        {
            if (sender is not CheckBox cb || cb.DataContext is not MediaItem item)
                return;

            bool targetState = eventState;
            bool? pointerTarget = _lastMusicCheckboxPointerTarget;
            bool hasPointerTarget = IsRecentMusicCheckboxInput(item.FilePath, out double elapsedMs) && pointerTarget.HasValue;
            if (hasPointerTarget)
                targetState = pointerTarget.GetValueOrDefault();

            bool wasSelected = _multiSelectedPaths.Contains(item.FilePath);
            bool willSetCheckbox = cb.IsChecked != targetState;

            if (targetState)
                _multiSelectedPaths.Add(item.FilePath);
            else
                _multiSelectedPaths.Remove(item.FilePath);

            AppLogger.Debug($"[MusicPage][#{++_musicCheckboxEventSeq}] 音乐复选框 {eventName} | Path={item.FilePath} | EventState={eventState} | PointerTarget={pointerTarget?.ToString() ?? "null"} | HasPointerTarget={hasPointerTarget} | Elapsed={elapsedMs:F1}ms | ComputedTarget={targetState} | WasSelected={wasSelected} | CheckboxIsChecked={cb.IsChecked} | WillSetCheckbox={willSetCheckbox} | SelectedNow={_multiSelectedPaths.Contains(item.FilePath)}");

            if (willSetCheckbox)
            {
                // 注意：此处强制设置 IsChecked 会再次触发 Checked/Unchecked 事件（重入），日志会继续出现后续事件序号
                cb.IsChecked = targetState;
            }

            UpdateMusicMultiSelectCount();
        }

        private bool IsRecentMusicCheckboxInput(string filePath, out double elapsedMs)
        {
            elapsedMs = _lastMusicCheckboxPointerTimestamp == 0
                ? double.PositiveInfinity
                : (Stopwatch.GetTimestamp() - _lastMusicCheckboxPointerTimestamp) * 1000.0 / Stopwatch.Frequency;

            return elapsedMs <= MusicCheckboxInputSuppressMs &&
                   string.Equals(_lastMusicCheckboxPointerPath, filePath, StringComparison.OrdinalIgnoreCase);
        }

        private void MusicItemCheckBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is CheckBox { DataContext: MediaItem item })
            {
                AppLogger.Debug($"[MusicPage][#{++_musicCheckboxEventSeq}] 音乐复选框 DoubleTapped 已拦截 | Path={item.FilePath} | Selected={_multiSelectedPaths.Contains(item.FilePath)} | CheckboxIsChecked={((CheckBox)sender).IsChecked}");
            }

            e.Handled = true;
        }
        #endregion

        #region 歌单多选
        private void EnterPlaylistMultiSelectMode(Playlist? starter = null)
        {
            _playlistMultiSelectMode = true;
            _playlistMultiSelectedIds.Clear();

            if (starter != null)
                _playlistMultiSelectedIds.Add(starter.Id);

            MultiSelectAddToPlaylistButton.Visibility = Visibility.Collapsed;
            MultiSelectPlayAllButton.Visibility = Visibility.Visible;
            MultiSelectSearchBox.PlaceholderText = "搜索歌单";
            PlaylistMultiSelectToggleButton.IsChecked = true;

            UpdateAllPlaylistCheckBoxes();
            UpdatePlaylistMultiSelectCount();
        }

        private void ExitPlaylistMultiSelectMode()
        {
            _playlistMultiSelectMode = false;
            _playlistMultiSelectedIds.Clear();

            PlaylistMultiSelectToggleButton.IsChecked = false;

            UpdateAllPlaylistCheckBoxes();
            UpdatePlaylistMultiSelectCount();
        }

        private void TogglePlaylistItemSelection(Playlist item)
        {
            if (_playlistMultiSelectedIds.Contains(item.Id))
                _playlistMultiSelectedIds.Remove(item.Id);
            else
                _playlistMultiSelectedIds.Add(item.Id);

            UpdatePlaylistMultiSelectCount();

            if (PlaylistList.ContainerFromItem(item) is ListViewItem container)
            {
                var checkbox = FindVisualChild<CheckBox>(container);
                if (checkbox != null)
                    checkbox.IsChecked = _playlistMultiSelectedIds.Contains(item.Id);
            }
            if (PlaylistGrid.ContainerFromItem(item) is GridViewItem gridContainer)
            {
                var checkbox = FindVisualChild<CheckBox>(gridContainer);
                if (checkbox != null)
                    checkbox.IsChecked = _playlistMultiSelectedIds.Contains(item.Id);
            }
            if (PlaylistWaterfallGrid.ContainerFromItem(item) is GridViewItem waterfallContainer)
            {
                var checkbox = FindVisualChild<CheckBox>(waterfallContainer);
                if (checkbox != null)
                    checkbox.IsChecked = _playlistMultiSelectedIds.Contains(item.Id);
            }
        }

        private void UpdatePlaylistMultiSelectCount()
        {
            int count = _playlistMultiSelectedIds.Count;
            int total = _filteredPlaylists.Count;

            MultiSelectCountText.Text = total > 0
                ? $"已选择 {count} / {total} 个歌单"
                : "已选择 0 个歌单";

            if (SelectAllCheckBox != null)
            {
                SelectAllCheckBox.IsChecked = count > 0 && count == total
                    ? true
                    : count == 0 ? false : null;
            }
        }

        private void UpdateAllPlaylistCheckBoxes()
        {
            var visibility = _playlistMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;

            void SyncItem(Playlist p)
            {
                if (PlaylistList.ContainerFromItem(p) is ListViewItem container)
                {
                    var cb = FindVisualChild<CheckBox>(container);
                    if (cb != null)
                    {
                        cb.Visibility = visibility;
                        if (_playlistMultiSelectMode)
                            cb.IsChecked = _playlistMultiSelectedIds.Contains(p.Id);
                    }
                }
                if (PlaylistGrid.ContainerFromItem(p) is GridViewItem gridContainer)
                {
                    var cb = FindVisualChild<CheckBox>(gridContainer);
                    if (cb != null)
                    {
                        cb.Visibility = visibility;
                        if (_playlistMultiSelectMode)
                            cb.IsChecked = _playlistMultiSelectedIds.Contains(p.Id);
                    }
                }
                if (PlaylistWaterfallGrid.ContainerFromItem(p) is GridViewItem waterfallContainer)
                {
                    var cb = FindVisualChild<CheckBox>(waterfallContainer);
                    if (cb != null)
                    {
                        cb.Visibility = visibility;
                        if (_playlistMultiSelectMode)
                            cb.IsChecked = _playlistMultiSelectedIds.Contains(p.Id);
                    }
                }
            }

            foreach (var p in _filteredPlaylists)
                SyncItem(p);
        }

        private void PlaylistList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is ListViewItem container && args.Item is Playlist item)
            {
                // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置
                container.Opacity = 1.0;

                var cb = FindVisualChild<CheckBox>(container);
                if (cb != null)
                {
                    cb.Visibility = _playlistMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
                    cb.IsChecked = _playlistMultiSelectedIds.Contains(item.Id);
                }
            }
        }

        private void PlaylistGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is GridViewItem container && args.Item is Playlist item)
            {
                if (args.Phase == 0)
                {
                    // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置
                    container.Opacity = 1.0;

                    var cb = FindVisualChild<CheckBox>(container);
                    if (cb != null)
                    {
                        cb.Visibility = _playlistMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
                        cb.IsChecked = _playlistMultiSelectedIds.Contains(item.Id);
                    }
                    args.RegisterUpdateCallback(1, GridCoverLoadPhase);
                    args.Handled = true;
                }
            }
        }

        private void PlaylistItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is Playlist item)
            {
                _playlistMultiSelectedIds.Add(item.Id);
                UpdatePlaylistMultiSelectCount();
            }
        }

        private void PlaylistItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is Playlist item)
            {
                _playlistMultiSelectedIds.Remove(item.Id);
                UpdatePlaylistMultiSelectCount();
            }
        }
        #endregion

        #region 歌手多选
        private void EnterArtistMultiSelectMode(ArtistGroup? starter = null)
        {
            _artistMultiSelectMode = true;
            _artistMultiSelectedNames.Clear();

            if (starter != null)
                _artistMultiSelectedNames.Add(starter.ArtistName);

            MultiSelectAddToPlaylistButton.Visibility = Visibility.Collapsed;
            MultiSelectPlayAllButton.Visibility = Visibility.Visible;
            MultiSelectSearchBox.PlaceholderText = "搜索歌手";
            ArtistMultiSelectToggleButton.IsChecked = true;

            UpdateAllArtistCheckBoxes();
            UpdateArtistMultiSelectCount();
        }

        private void ExitArtistMultiSelectMode()
        {
            _artistMultiSelectMode = false;
            _artistMultiSelectedNames.Clear();
            ArtistMultiSelectToggleButton.IsChecked = false;
            UpdateAllArtistCheckBoxes();
            UpdateArtistMultiSelectCount();
        }

        private void ToggleArtistItemSelection(ArtistGroup item)
        {
            if (_artistMultiSelectedNames.Contains(item.ArtistName))
                _artistMultiSelectedNames.Remove(item.ArtistName);
            else
                _artistMultiSelectedNames.Add(item.ArtistName);
            UpdateArtistMultiSelectCount();

            void SyncContainer(DependencyObject c)
            {
                var cb = FindVisualChild<CheckBox>(c);
                if (cb != null) cb.IsChecked = _artistMultiSelectedNames.Contains(item.ArtistName);
            }
            if (ArtistList.ContainerFromItem(item) is ListViewItem lvc) SyncContainer(lvc);
            if (ArtistGrid.ContainerFromItem(item) is GridViewItem gvc) SyncContainer(gvc);
            if (ArtistWaterfallGrid.ContainerFromItem(item) is GridViewItem wfc) SyncContainer(wfc);
        }

        private void UpdateArtistMultiSelectCount()
        {
            int count = _artistMultiSelectedNames.Count;
            int total = _filteredArtistGroups.Count;
            MultiSelectCountText.Text = total > 0 ? $"已选择 {count} / {total} 位歌手" : "已选择 0 位歌手";
            if (SelectAllCheckBox != null)
                SelectAllCheckBox.IsChecked = count > 0 && count == total ? true : count == 0 ? false : null;
        }

        private void UpdateAllArtistCheckBoxes()
        {
            var vis = _artistMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in _filteredArtistGroups)
            {
                if (ArtistList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) { cb.Visibility = vis; if (_artistMultiSelectMode) cb.IsChecked = _artistMultiSelectedNames.Contains(item.ArtistName); } }
                if (ArtistGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) { cb.Visibility = vis; if (_artistMultiSelectMode) cb.IsChecked = _artistMultiSelectedNames.Contains(item.ArtistName); } }
                if (ArtistWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) { cb.Visibility = vis; if (_artistMultiSelectMode) cb.IsChecked = _artistMultiSelectedNames.Contains(item.ArtistName); } }
            }
        }

        private void ArtistList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is ListViewItem container && args.Item is ArtistGroup item)
            {
                // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置
                container.Opacity = 1.0;

                var cb = FindVisualChild<CheckBox>(container);
                if (cb != null) { cb.Visibility = _artistMultiSelectMode ? Visibility.Visible : Visibility.Collapsed; cb.IsChecked = _artistMultiSelectedNames.Contains(item.ArtistName); }
            }
        }

        private void ArtistGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is GridViewItem container && args.Item is ArtistGroup item)
            {
                if (args.Phase == 0)
                {
                    // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置
                    container.Opacity = 1.0;

                    var cb = FindVisualChild<CheckBox>(container);
                    if (cb != null) { cb.Visibility = _artistMultiSelectMode ? Visibility.Visible : Visibility.Collapsed; cb.IsChecked = _artistMultiSelectedNames.Contains(item.ArtistName); }
                    args.RegisterUpdateCallback(1, GridCoverLoadPhase);
                    args.Handled = true;
                }
            }
        }

        private void ArtistWaterfallGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is GridViewItem container && args.Item is ArtistGroup item)
            {
                if (args.Phase == 0)
                {
                    // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置
                    container.Opacity = 1.0;

                    var cb = FindVisualChild<CheckBox>(container);
                    if (cb != null) { cb.Visibility = _artistMultiSelectMode ? Visibility.Visible : Visibility.Collapsed; cb.IsChecked = _artistMultiSelectedNames.Contains(item.ArtistName); }
                    args.RegisterUpdateCallback(1, GridCoverLoadPhase);
                    args.Handled = true;
                }
            }
        }

        private void ArtistItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is ArtistGroup item) { _artistMultiSelectedNames.Add(item.ArtistName); UpdateArtistMultiSelectCount(); }
        }

        private void ArtistItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is ArtistGroup item) { _artistMultiSelectedNames.Remove(item.ArtistName); UpdateArtistMultiSelectCount(); }
        }
        #endregion

        #region 专辑多选
        private void EnterAlbumMultiSelectMode(AlbumGroup? starter = null)
        {
            _albumMultiSelectMode = true;
            _albumMultiSelectedKeys.Clear();

            if (starter != null)
                _albumMultiSelectedKeys.Add(AlbumKey(starter));

            MultiSelectAddToPlaylistButton.Visibility = Visibility.Collapsed;
            MultiSelectPlayAllButton.Visibility = Visibility.Visible;
            MultiSelectSearchBox.PlaceholderText = "搜索专辑";
            AlbumMultiSelectToggleButton.IsChecked = true;

            UpdateAllAlbumCheckBoxes();
            UpdateAlbumMultiSelectCount();
        }

        private void ExitAlbumMultiSelectMode()
        {
            _albumMultiSelectMode = false;
            _albumMultiSelectedKeys.Clear();
            AlbumMultiSelectToggleButton.IsChecked = false;
            UpdateAllAlbumCheckBoxes();
            UpdateAlbumMultiSelectCount();
        }

        private static string AlbumKey(AlbumGroup a) => $"{a.AlbumName}|{a.Artist}";

        private void ToggleAlbumItemSelection(AlbumGroup item)
        {
            var key = AlbumKey(item);
            if (_albumMultiSelectedKeys.Contains(key)) _albumMultiSelectedKeys.Remove(key);
            else _albumMultiSelectedKeys.Add(key);
            UpdateAlbumMultiSelectCount();

            if (AlbumList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = _albumMultiSelectedKeys.Contains(key); }
            if (AlbumGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = _albumMultiSelectedKeys.Contains(key); }
            if (AlbumWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = _albumMultiSelectedKeys.Contains(key); }
        }

        private void UpdateAlbumMultiSelectCount()
        {
            int count = _albumMultiSelectedKeys.Count;
            int total = _filteredAlbumGroups.Count;
            MultiSelectCountText.Text = total > 0 ? $"已选择 {count} / {total} 张专辑" : "已选择 0 张专辑";
            if (SelectAllCheckBox != null)
                SelectAllCheckBox.IsChecked = count > 0 && count == total ? true : count == 0 ? false : null;
        }

        private void UpdateAllAlbumCheckBoxes()
        {
            var vis = _albumMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in _filteredAlbumGroups)
            {
                if (AlbumList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) { cb.Visibility = vis; if (_albumMultiSelectMode) cb.IsChecked = _albumMultiSelectedKeys.Contains(AlbumKey(item)); } }
                if (AlbumGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) { cb.Visibility = vis; if (_albumMultiSelectMode) cb.IsChecked = _albumMultiSelectedKeys.Contains(AlbumKey(item)); } }
                if (AlbumWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) { cb.Visibility = vis; if (_albumMultiSelectMode) cb.IsChecked = _albumMultiSelectedKeys.Contains(AlbumKey(item)); } }
            }
        }

        private void AlbumList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is ListViewItem container && args.Item is AlbumGroup item)
            {
                // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置
                container.Opacity = 1.0;

                var cb = FindVisualChild<CheckBox>(container);
                if (cb != null) { cb.Visibility = _albumMultiSelectMode ? Visibility.Visible : Visibility.Collapsed; cb.IsChecked = _albumMultiSelectedKeys.Contains(AlbumKey(item)); }
            }
        }

        private void AlbumGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is GridViewItem container && args.Item is AlbumGroup item)
            {
                if (args.Phase == 0)
                {
                    // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置
                    container.Opacity = 1.0;

                    var cb = FindVisualChild<CheckBox>(container);
                    if (cb != null) { cb.Visibility = _albumMultiSelectMode ? Visibility.Visible : Visibility.Collapsed; cb.IsChecked = _albumMultiSelectedKeys.Contains(AlbumKey(item)); }
                    args.RegisterUpdateCallback(1, GridCoverLoadPhase);
                    args.Handled = true;
                }
            }
        }

        private void AlbumItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is AlbumGroup item) { _albumMultiSelectedKeys.Add(AlbumKey(item)); UpdateAlbumMultiSelectCount(); }
        }

        private void AlbumItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is AlbumGroup item) { _albumMultiSelectedKeys.Remove(AlbumKey(item)); UpdateAlbumMultiSelectCount(); }
        }
        #endregion

        #region 文件夹多选
        private void EnterFolderMultiSelectMode(FolderGroup? starter = null)
        {
            _folderMultiSelectMode = true;
            _folderMultiSelectedPaths.Clear();

            if (starter != null)
                _folderMultiSelectedPaths.Add(starter.FolderPath);

            MultiSelectAddToPlaylistButton.Visibility = Visibility.Collapsed;
            MultiSelectPlayAllButton.Visibility = Visibility.Visible;
            MultiSelectSearchBox.PlaceholderText = "搜索文件夹";
            FolderMultiSelectToggleButton.IsChecked = true;

            UpdateAllFolderCheckBoxes();
            UpdateFolderMultiSelectCount();
        }

        private void ExitFolderMultiSelectMode()
        {
            _folderMultiSelectMode = false;
            _folderMultiSelectedPaths.Clear();
            FolderMultiSelectToggleButton.IsChecked = false;
            UpdateAllFolderCheckBoxes();
            UpdateFolderMultiSelectCount();
        }

        private void ToggleFolderItemSelection(FolderGroup item)
        {
            if (_folderMultiSelectedPaths.Contains(item.FolderPath)) _folderMultiSelectedPaths.Remove(item.FolderPath);
            else _folderMultiSelectedPaths.Add(item.FolderPath);
            UpdateFolderMultiSelectCount();

            if (FolderList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath); }
            if (FolderGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath); }
            if (FolderWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath); }
        }

        private void UpdateFolderMultiSelectCount()
        {
            int count = _folderMultiSelectedPaths.Count;
            int total = _filteredFolderGroups.Count;
            MultiSelectCountText.Text = total > 0 ? $"已选择 {count} / {total} 个文件夹" : "已选择 0 个文件夹";
            if (SelectAllCheckBox != null)
                SelectAllCheckBox.IsChecked = count > 0 && count == total ? true : count == 0 ? false : null;
        }

        private void UpdateAllFolderCheckBoxes()
        {
            var vis = _folderMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in _filteredFolderGroups)
            {
                if (FolderList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) { cb.Visibility = vis; if (_folderMultiSelectMode) cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath); } }
                if (FolderGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) { cb.Visibility = vis; if (_folderMultiSelectMode) cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath); } }
                if (FolderWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) { cb.Visibility = vis; if (_folderMultiSelectMode) cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath); } }
            }
        }

        private void FolderList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is ListViewItem container && args.Item is FolderGroup item)
            {
                // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置
                container.Opacity = 1.0;

                var cb = FindVisualChild<CheckBox>(container);
                if (cb != null) { cb.Visibility = _folderMultiSelectMode ? Visibility.Visible : Visibility.Collapsed; cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath); }
            }
        }

        private void FolderGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is GridViewItem container && args.Item is FolderGroup item)
            {
                if (args.Phase == 0)
                {
                    // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置
                    container.Opacity = 1.0;

                    var cb = FindVisualChild<CheckBox>(container);
                    if (cb != null) { cb.Visibility = _folderMultiSelectMode ? Visibility.Visible : Visibility.Collapsed; cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath); }
                    args.RegisterUpdateCallback(1, GridCoverLoadPhase);
                    args.Handled = true;
                }
            }
        }

        private void FolderWaterfallGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!PageLifetimeService.IsActive(_containerGeneration))
            {
                args.Handled = true;
                return;
            }
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is GridViewItem container && args.Item is FolderGroup item)
            {
                if (args.Phase == 0)
                {
                    // ★ 修复：回收复用的容器可能残留旧的 Opacity=0，强制重置
                    container.Opacity = 1.0;

                    var cb = FindVisualChild<CheckBox>(container);
                    if (cb != null) { cb.Visibility = _folderMultiSelectMode ? Visibility.Visible : Visibility.Collapsed; cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath); }
                    args.RegisterUpdateCallback(1, GridCoverLoadPhase);
                    args.Handled = true;
                }
            }
        }

        private void FolderItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is FolderGroup item) { _folderMultiSelectedPaths.Add(item.FolderPath); UpdateFolderMultiSelectCount(); }
        }

        private void FolderItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is FolderGroup item) { _folderMultiSelectedPaths.Remove(item.FolderPath); UpdateFolderMultiSelectCount(); }
        }
        #endregion

        #region 共享多选工具栏事件
        private void MultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isMultiSelectMode)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(0);
        }

        private void PlaylistMultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playlistMultiSelectMode)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(1);
        }

        private void ArtistMultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_artistMultiSelectMode)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(2);
        }

        private void AlbumMultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_albumMultiSelectMode)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(3);
        }

        private void FolderMultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_folderMultiSelectMode)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(4);
        }

        private void MultiSelectSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;

            if (_multiSelectActiveTab == 0)
            {
                MusicSearchBox.Text = MultiSelectSearchBox.Text;
                DebounceApplySortAndFilter();
            }
            else if (_multiSelectActiveTab == 1)
            {
                PlaylistSearchBox.Text = MultiSelectSearchBox.Text;
                ApplyPlaylistSortAndFilter();
            }
            else if (_multiSelectActiveTab == 2)
            {
                ArtistSearchBox.Text = MultiSelectSearchBox.Text;
                ApplyArtistSortAndFilter();
            }
            else if (_multiSelectActiveTab == 3)
            {
                AlbumSearchBox.Text = MultiSelectSearchBox.Text;
                ApplyAlbumSortAndFilter();
            }
            else if (_multiSelectActiveTab == 4)
            {
                FolderSearchBox.Text = MultiSelectSearchBox.Text;
                ApplyFolderSortAndFilter();
            }
        }

        private async void MultiSelectDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectActiveTab == 0)
                await DeleteMultiSelectMusic();
            else if (_multiSelectActiveTab == 1)
                await DeleteMultiSelectPlaylists();
            else if (_multiSelectActiveTab == 2)
                await DeleteMultiSelectArtists();
            else if (_multiSelectActiveTab == 3)
                await DeleteMultiSelectAlbums();
            else if (_multiSelectActiveTab == 4)
                await DeleteMultiSelectFolders();
        }

        private async Task DeleteMultiSelectMusic()
        {
            if (_multiSelectedPaths.Count == 0) return;

            int count = _multiSelectedPaths.Count;
            var dialog = new ContentDialog
            {
                Title = "删除确认",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将选中的 {count} 首音乐移入到回收站吗？可随时还原。"
                    : $"确定要删除选中的 {count} 个本地磁盘文件吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };

            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            // ★ Bug 修复：删除本地文件（移入回收站或永久删除，取决于设置）
            var itemsToDelete = _allMusic
                .Where(item => _multiSelectedPaths.Contains(item.FilePath))
                .ToList();
            MusicItemMenuHelper.DeleteMusicFilesFromDisk(itemsToDelete);

            _allMusic.RemoveAll(item => _multiSelectedPaths.Contains(item.FilePath));
            MusicDataCache.AllMusic = _allMusic;
            await Task.Run(() => MediaScanner.SaveToCache(_allMusic, "Music"));
            _multiSelectedPaths.Clear();
            ApplyMusicSortAndFilter();
            UpdateMusicMultiSelectCount();

            if (_allMusic.Count == 0)
                ExitMultiSelectMode();
        }

        private async Task DeleteMultiSelectPlaylists()
        {
            if (_playlistMultiSelectedIds.Count == 0) return;

            int count = _playlistMultiSelectedIds.Count;
            var dialog = new ContentDialog
            {
                Title = "删除确认",
                Content = $"确定要删除选中的 {count} 个歌单吗？此操作不可撤销。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };

            var result = await DialogService.ShowAsync(dialog, XamlRoot);
            if (result != ContentDialogResult.Primary) return;

            _allPlaylists.RemoveAll(p => _playlistMultiSelectedIds.Contains(p.Id));
            _playlistMultiSelectedIds.Clear();
            SavePlaylists();
            ApplyPlaylistSortAndFilter();
            UpdatePlaylistMultiSelectCount();

            if (_allPlaylists.Count == 0)
                ExitMultiSelectMode();
        }

        private async void MultiSelectAddToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectedPaths.Count == 0 || _allPlaylists.Count == 0)
                return;

            var selectedItems = _allMusic.Where(item => _multiSelectedPaths.Contains(item.FilePath)).ToList();
            if (selectedItems.Count == 0)
                return;

            var dialog = new ContentDialog
            {
                Title = "添加到歌单",
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            var panel = new StackPanel { Spacing = 8 };
            var comboBox = new ComboBox
            {
                ItemsSource = _allPlaylists,
                DisplayMemberPath = "Name",
                PlaceholderText = "选择歌单",
                Width = 300
            };
            panel.Children.Add(comboBox);
            dialog.Content = panel;

            var result = await DialogService.ShowAsync(dialog, XamlRoot);
            if (result != ContentDialogResult.Primary || comboBox.SelectedItem is not Playlist playlist)
                return;

            foreach (var item in selectedItems)
            {
                if (!playlist.Items.Any(s => string.Equals(s.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    playlist.Items.Add(item);
                }
            }
            SavePlaylists();
            ExitMultiSelectMode();
        }

        private async void MultiSelectPlayAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectActiveTab == 1)
                await PlayAllSelectedPlaylists();
            else if (_multiSelectActiveTab == 2)
                await PlayAllSelectedArtists();
            else if (_multiSelectActiveTab == 3)
                await PlayAllSelectedAlbums();
            else if (_multiSelectActiveTab == 4)
                await PlayAllSelectedFolders();
        }

        private async Task PlayAllSelectedPlaylists()
        {
            var songs = _allPlaylists
                .Where(p => _playlistMultiSelectedIds.Contains(p.Id))
                .SelectMany(p => p.Items)
                .DistinctBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (songs.Count == 0) return;

            await PlayMusicAsync(songs[0], songs);
            ExitMultiSelectMode();
        }

        private async Task PlayAllSelectedArtists()
        {
            var songs = _allMusic
                .Where(m => _artistMultiSelectedNames.Contains(m.Artist ?? ""))
                .DistinctBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (songs.Count == 0) return;
            await PlayMusicAsync(songs[0], songs);
            ExitMultiSelectMode();
        }

        private async Task PlayAllSelectedAlbums()
        {
            var songs = _allMusic
                .Where(m => _albumMultiSelectedKeys.Contains(AlbumKeyFromMedia(m)))
                .DistinctBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (songs.Count == 0) return;
            await PlayMusicAsync(songs[0], songs);
            ExitMultiSelectMode();
        }

        private async Task PlayAllSelectedFolders()
        {
            var songs = _allMusic
                .Where(m => _folderMultiSelectedPaths.Contains(Path.GetDirectoryName(m.FilePath) ?? ""))
                .DistinctBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (songs.Count == 0) return;
            await PlayMusicAsync(songs[0], songs);
            ExitMultiSelectMode();
        }

        private static string AlbumKeyFromMedia(MediaItem m) => $"{m.Album}|{m.Artist}";

        private async Task DeleteMultiSelectArtists()
        {
            if (_artistMultiSelectedNames.Count == 0) return;
            int count = _artistMultiSelectedNames.Count;
            var dialog = new ContentDialog
            {
                Title = "删除确认",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将选中的 {count} 位歌手的全部歌曲移入到回收站吗？可随时还原。"
                    : $"确定要删除选中的 {count} 位歌手的全部本地磁盘文件吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            // ★ Bug 修复：删除本地文件（移入回收站或永久删除，取决于设置）
            var itemsToDelete = _allMusic
                .Where(m => _artistMultiSelectedNames.Contains(m.Artist ?? ""))
                .ToList();
            MusicItemMenuHelper.DeleteMusicFilesFromDisk(itemsToDelete);

            _allMusic.RemoveAll(m => _artistMultiSelectedNames.Contains(m.Artist ?? ""));
            MusicDataCache.AllMusic = _allMusic;
            await Task.Run(() => MediaScanner.SaveToCache(_allMusic, "Music"));
            _artistMultiSelectedNames.Clear();
            BuildArtistGroups();
            ApplyArtistSortAndFilter();
            UpdateArtistMultiSelectCount();
            if (_allMusic.Count == 0) ExitMultiSelectMode();
        }

        private async Task DeleteMultiSelectAlbums()
        {
            if (_albumMultiSelectedKeys.Count == 0) return;
            int count = _albumMultiSelectedKeys.Count;
            var dialog = new ContentDialog
            {
                Title = "删除确认",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将选中的 {count} 张专辑的全部歌曲移入到回收站吗？可随时还原。"
                    : $"确定要删除选中的 {count} 张专辑的全部本地磁盘文件吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            // ★ Bug 修复：删除本地文件（移入回收站或永久删除，取决于设置）
            var itemsToDelete = _allMusic
                .Where(m => _albumMultiSelectedKeys.Contains(AlbumKeyFromMedia(m)))
                .ToList();
            MusicItemMenuHelper.DeleteMusicFilesFromDisk(itemsToDelete);

            _allMusic.RemoveAll(m => _albumMultiSelectedKeys.Contains(AlbumKeyFromMedia(m)));
            MusicDataCache.AllMusic = _allMusic;
            await Task.Run(() => MediaScanner.SaveToCache(_allMusic, "Music"));
            _albumMultiSelectedKeys.Clear();
            BuildAlbumGroups();
            ApplyAlbumSortAndFilter();
            UpdateAlbumMultiSelectCount();
            if (_allMusic.Count == 0) ExitMultiSelectMode();
        }

        private async Task DeleteMultiSelectFolders()
        {
            if (_folderMultiSelectedPaths.Count == 0) return;
            int count = _folderMultiSelectedPaths.Count;
            var dialog = new ContentDialog
            {
                Title = "删除确认",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将选中的 {count} 个文件夹的全部歌曲移入到回收站吗？可随时还原。"
                    : $"确定要删除选中的 {count} 个文件夹的全部本地磁盘文件吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            // ★ Bug 修复：删除本地文件（移入回收站或永久删除，取决于设置）
            var itemsToDelete = _allMusic
                .Where(m => _folderMultiSelectedPaths.Contains(Path.GetDirectoryName(m.FilePath) ?? ""))
                .ToList();
            MusicItemMenuHelper.DeleteMusicFilesFromDisk(itemsToDelete);

            _allMusic.RemoveAll(m => _folderMultiSelectedPaths.Contains(Path.GetDirectoryName(m.FilePath) ?? ""));
            MusicDataCache.AllMusic = _allMusic;
            await Task.Run(() => MediaScanner.SaveToCache(_allMusic, "Music"));
            _folderMultiSelectedPaths.Clear();
            BuildFolderGroups();
            ApplyFolderSortAndFilter();
            UpdateFolderMultiSelectCount();
            if (_allMusic.Count == 0) ExitMultiSelectMode();
        }

        private void MultiSelectCancelButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
        }

        private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_multiSelectActiveTab == 0)
                SelectAllMusic();
            else if (_multiSelectActiveTab == 1)
                SelectAllPlaylists();
            else if (_multiSelectActiveTab == 2)
                SelectAllArtists();
            else if (_multiSelectActiveTab == 3)
                SelectAllAlbums();
            else if (_multiSelectActiveTab == 4)
                SelectAllFolders();
        }

        private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_multiSelectActiveTab == 0)
                DeselectAllMusic();
            else if (_multiSelectActiveTab == 1)
                DeselectAllPlaylists();
            else if (_multiSelectActiveTab == 2)
                DeselectAllArtists();
            else if (_multiSelectActiveTab == 3)
                DeselectAllAlbums();
            else if (_multiSelectActiveTab == 4)
                DeselectAllFolders();
        }

        private void SelectAllMusic()
        {
            if (_selectAllChanging) return;

            foreach (var item in _filteredMusic)
            {
                _multiSelectedPaths.Add(item.FilePath);
                if (MusicList.ContainerFromItem(item) is ListViewItem container)
                {
                    var checkbox = FindVisualChild<CheckBox>(container);
                    if (checkbox != null) checkbox.IsChecked = true;
                }
                if (MusicGrid.ContainerFromItem(item) is GridViewItem gridContainer)
                {
                    var checkbox = FindVisualChild<CheckBox>(gridContainer);
                    if (checkbox != null) checkbox.IsChecked = true;
                }
                if (MusicWaterfallGrid.ContainerFromItem(item) is GridViewItem waterfallContainer)
                {
                    var checkbox = FindVisualChild<CheckBox>(waterfallContainer);
                    if (checkbox != null) checkbox.IsChecked = true;
                }
            }
            UpdateMusicMultiSelectCount();
        }

        private void DeselectAllMusic()
        {
            if (_selectAllChanging) return;

            int count = _multiSelectedPaths.Count;
            int total = _filteredMusic.Count;

            if (count > 0 && count < total)
            {
                _selectAllChanging = true;
                SelectAllCheckBox.IsChecked = true;
                _selectAllChanging = false;

                foreach (var item in _filteredMusic)
                {
                    _multiSelectedPaths.Add(item.FilePath);
                    if (MusicList.ContainerFromItem(item) is ListViewItem container)
                    {
                        var checkbox = FindVisualChild<CheckBox>(container);
                        if (checkbox != null) checkbox.IsChecked = true;
                    }
                    if (MusicGrid.ContainerFromItem(item) is GridViewItem gridContainer)
                    {
                        var checkbox = FindVisualChild<CheckBox>(gridContainer);
                        if (checkbox != null) checkbox.IsChecked = true;
                    }
                    if (MusicWaterfallGrid.ContainerFromItem(item) is GridViewItem waterfallContainer)
                    {
                        var checkbox = FindVisualChild<CheckBox>(waterfallContainer);
                        if (checkbox != null) checkbox.IsChecked = true;
                    }
                }
                UpdateMusicMultiSelectCount();
                return;
            }

            foreach (var item in _filteredMusic)
            {
                _multiSelectedPaths.Remove(item.FilePath);
                if (MusicList.ContainerFromItem(item) is ListViewItem container)
                {
                    var checkbox = FindVisualChild<CheckBox>(container);
                    if (checkbox != null) checkbox.IsChecked = false;
                }
                if (MusicGrid.ContainerFromItem(item) is GridViewItem gridContainer)
                {
                    var checkbox = FindVisualChild<CheckBox>(gridContainer);
                    if (checkbox != null) checkbox.IsChecked = false;
                }
                if (MusicWaterfallGrid.ContainerFromItem(item) is GridViewItem waterfallContainer)
                {
                    var checkbox = FindVisualChild<CheckBox>(waterfallContainer);
                    if (checkbox != null) checkbox.IsChecked = false;
                }
            }
            UpdateMusicMultiSelectCount();
        }

        private void SelectAllPlaylists()
        {
            if (_playlistSelectAllChanging) return;

            foreach (var p in _filteredPlaylists)
            {
                _playlistMultiSelectedIds.Add(p.Id);
                SyncPlaylistCheckbox(p, true);
            }
            UpdatePlaylistMultiSelectCount();
        }

        private void DeselectAllPlaylists()
        {
            if (_playlistSelectAllChanging) return;

            int count = _playlistMultiSelectedIds.Count;
            int total = _filteredPlaylists.Count;

            if (count > 0 && count < total)
            {
                _playlistSelectAllChanging = true;
                SelectAllCheckBox.IsChecked = true;
                _playlistSelectAllChanging = false;

                foreach (var p in _filteredPlaylists)
                {
                    _playlistMultiSelectedIds.Add(p.Id);
                    SyncPlaylistCheckbox(p, true);
                }
                UpdatePlaylistMultiSelectCount();
                return;
            }

            foreach (var p in _filteredPlaylists)
            {
                _playlistMultiSelectedIds.Remove(p.Id);
                SyncPlaylistCheckbox(p, false);
            }
            UpdatePlaylistMultiSelectCount();
        }

        private void SyncPlaylistCheckbox(Playlist item, bool isChecked)
        {
            if (PlaylistList.ContainerFromItem(item) is ListViewItem container)
            {
                var cb = FindVisualChild<CheckBox>(container);
                if (cb != null) cb.IsChecked = isChecked;
            }
            if (PlaylistGrid.ContainerFromItem(item) is GridViewItem gridContainer)
            {
                var cb = FindVisualChild<CheckBox>(gridContainer);
                if (cb != null) cb.IsChecked = isChecked;
            }
            if (PlaylistWaterfallGrid.ContainerFromItem(item) is GridViewItem waterfallContainer)
            {
                var cb = FindVisualChild<CheckBox>(waterfallContainer);
                if (cb != null) cb.IsChecked = isChecked;
            }
        }

        private void SelectAllArtists()
        {
            if (_artistSelectAllChanging) return;
            foreach (var item in _filteredArtistGroups)
            {
                _artistMultiSelectedNames.Add(item.ArtistName);
                if (ArtistList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = true; }
                if (ArtistGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = true; }
                if (ArtistWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = true; }
            }
            UpdateArtistMultiSelectCount();
        }

        private void DeselectAllArtists()
        {
            if (_artistSelectAllChanging) return;
            int count = _artistMultiSelectedNames.Count;
            int total = _filteredArtistGroups.Count;
            if (count > 0 && count < total)
            {
                _artistSelectAllChanging = true;
                SelectAllCheckBox.IsChecked = true;
                _artistSelectAllChanging = false;
                foreach (var item in _filteredArtistGroups)
                {
                    _artistMultiSelectedNames.Add(item.ArtistName);
                    if (ArtistList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = true; }
                    if (ArtistGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = true; }
                    if (ArtistWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = true; }
                }
                UpdateArtistMultiSelectCount();
                return;
            }
            foreach (var item in _filteredArtistGroups)
            {
                _artistMultiSelectedNames.Remove(item.ArtistName);
                if (ArtistList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = false; }
                if (ArtistGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = false; }
                if (ArtistWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = false; }
            }
            UpdateArtistMultiSelectCount();
        }

        private void SelectAllAlbums()
        {
            if (_albumSelectAllChanging) return;
            foreach (var item in _filteredAlbumGroups)
            {
                var key = AlbumKey(item);
                _albumMultiSelectedKeys.Add(key);
                if (AlbumList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = true; }
                if (AlbumGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = true; }
                if (AlbumWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = true; }
            }
            UpdateAlbumMultiSelectCount();
        }

        private void DeselectAllAlbums()
        {
            if (_albumSelectAllChanging) return;
            int count = _albumMultiSelectedKeys.Count;
            int total = _filteredAlbumGroups.Count;
            if (count > 0 && count < total)
            {
                _albumSelectAllChanging = true;
                SelectAllCheckBox.IsChecked = true;
                _albumSelectAllChanging = false;
                foreach (var item in _filteredAlbumGroups)
                {
                    var key = AlbumKey(item);
                    _albumMultiSelectedKeys.Add(key);
                    if (AlbumList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = true; }
                    if (AlbumGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = true; }
                    if (AlbumWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = true; }
                }
                UpdateAlbumMultiSelectCount();
                return;
            }
            foreach (var item in _filteredAlbumGroups)
            {
                var key = AlbumKey(item);
                _albumMultiSelectedKeys.Remove(key);
                if (AlbumList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = false; }
                if (AlbumGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = false; }
                if (AlbumWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = false; }
            }
            UpdateAlbumMultiSelectCount();
        }

        private void SelectAllFolders()
        {
            if (_folderSelectAllChanging) return;
            foreach (var item in _filteredFolderGroups)
            {
                _folderMultiSelectedPaths.Add(item.FolderPath);
                if (FolderList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = true; }
                if (FolderGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = true; }
                if (FolderWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = true; }
            }
            UpdateFolderMultiSelectCount();
        }

        private void DeselectAllFolders()
        {
            if (_folderSelectAllChanging) return;
            int count = _folderMultiSelectedPaths.Count;
            int total = _filteredFolderGroups.Count;
            if (count > 0 && count < total)
            {
                _folderSelectAllChanging = true;
                SelectAllCheckBox.IsChecked = true;
                _folderSelectAllChanging = false;
                foreach (var item in _filteredFolderGroups)
                {
                    _folderMultiSelectedPaths.Add(item.FolderPath);
                    if (FolderList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = true; }
                    if (FolderGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = true; }
                    if (FolderWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = true; }
                }
                UpdateFolderMultiSelectCount();
                return;
            }
            foreach (var item in _filteredFolderGroups)
            {
                _folderMultiSelectedPaths.Remove(item.FolderPath);
                if (FolderList.ContainerFromItem(item) is ListViewItem c) { var cb = FindVisualChild<CheckBox>(c); if (cb != null) cb.IsChecked = false; }
                if (FolderGrid.ContainerFromItem(item) is GridViewItem gc) { var cb = FindVisualChild<CheckBox>(gc); if (cb != null) cb.IsChecked = false; }
                if (FolderWaterfallGrid.ContainerFromItem(item) is GridViewItem wc) { var cb = FindVisualChild<CheckBox>(wc); if (cb != null) cb.IsChecked = false; }
            }
            UpdateFolderMultiSelectCount();
        }
        #endregion

        /// <summary>
        /// 阶梯封面加载：检查缓存并加入队列，从上到下逐个显示。
        /// </summary>
        private void EnqueueCoverLoad(FrameworkElement root, object item)
        {
            if (_thumbnailQueue == null) return;
            var image = FindVisualChild<Image>(root);
            if (image == null) return;

            // 如果绑定已设置 Source（来自转换器缓存命中），直接显示
            if (image.Source != null)
            {
                image.Opacity = 1.0;
                return;
            }

            // 获取封面文件路径
            string? filePath = item switch
            {
                MediaItem mi => mi.FilePath,
                Playlist p => p.CoverDisplayPath,
                ArtistGroup ag => ag.CoverFilePath,
                AlbumGroup ag => ag.CoverFilePath,
                _ => null
            };

            if (string.IsNullOrEmpty(filePath)) return;

            // 检查内存缓存
            if (item is MediaItem mi2)
            {
                string? cachedPath = MusicCoverService.TryGetCachedPath(mi2.FilePath);
                if (!string.IsNullOrEmpty(cachedPath) && ImageThumbnailService.IsInMemoryCache(cachedPath))
                {
                    var cached = ImageThumbnailService.GetOrCreate(cachedPath);
                    if (cached != null) { image.Source = cached; image.Opacity = 1.0; return; }
                }
            }
            else if (ImageThumbnailService.IsInMemoryCache(filePath))
            {
                var cached = ImageThumbnailService.GetOrCreate(filePath);
                if (cached != null) { image.Source = cached; image.Opacity = 1.0; return; }
            }

            // 未缓存：显示占位符，加入阶梯队列
            image.Opacity = 0;
            _thumbnailQueue.Enqueue(image, filePath, 256);
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t)
                    return t;
                var found = FindVisualChild<T>(child);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static T? FindVisualAncestor<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject? current = child;
            while (current != null)
            {
                if (current is T target)
                    return target;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        /// <summary>
        /// 内存预加载：将所有封面瞬间加载到 ImageThumbnailService 内存缓存，
        /// 使得任何视图的容器准备/滚动时，转换器和 ContainerContentChanging 都能直接命中
        /// 缓存的 BitmapImage，零延迟显示。
        ///
        /// BitmapImage 的 UriSource 由 XAML 框架后台异步解码，创建操作本身非常轻量，
        /// 因此在 UI 线程上密集创建大量 BitmapImage 也不会造成明显卡顿。
        /// yield 一次即可让页面完成初始渲染。
        /// </summary>
        private async Task PreloadMemoryCacheAsync(
            IReadOnlyList<MediaItem> items,
            CancellationToken ct)
        {
            if (items == null || items.Count == 0) return;

            var sw = Stopwatch.StartNew();
            int loadedCount = 0;
            int skipCount = 0;

            // 让 UI 完成初始渲染后再开始批量加载
            await Task.Yield();
            if (ct.IsCancellationRequested) return;

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) return;

                try
                {
                    // 解析封面缓存路径（仅内存字典查找，无 I/O）
                    string? coverPath = MusicCoverService.TryGetCachedPath(item.FilePath);
                    if (string.IsNullOrEmpty(coverPath) || !System.IO.File.Exists(coverPath))
                    {
                        skipCount++;
                        continue;
                    }

                    // 已在内存缓存中 → 跳过
                    if (ImageThumbnailService.IsInMemoryCache(coverPath))
                    {
                        skipCount++;
                        continue;
                    }

                    // 加载到内存缓存（BitmapImage 创建 + UriSource 设置，XAML 后台解码）
                    ImageThumbnailService.GetOrCreate(coverPath);
                    loadedCount++;

                    // ★ 每 500 项检查一次取消，避免循环中完全不检查
                    if (loadedCount % 500 == 0 && ct.IsCancellationRequested)
                        return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    skipCount++;
                }
            }

            AppLogger.Debug($"[MusicPage] 内存预加载完成: {sw.ElapsedMilliseconds}ms | " +
                $"加载: {loadedCount}, 跳过: {skipCount}, 总计: {items.Count}");
        }

        #endregion

        /// <summary>
        /// 点击「媒体库管理」按钮，打开媒体库文件夹管理弹窗（勾选展示/添加/移除文件夹）。
        /// </summary>
        private async void LibraryManageButton_Click(object sender, RoutedEventArgs e)
        {
            await MediaLibraryManageDialog.ShowAsync(this.XamlRoot, "Music");
            AppLogger.Info("[MusicPage] 媒体库管理弹窗已关闭");
        }

        /// <summary>
        /// 媒体库文件夹勾选状态变更（媒体库管理弹窗内操作）：
        /// 从磁盘重新加载完整媒体库数据，并按勾选的文件夹过滤后重建视图。
        /// </summary>
        private void MediaLibraryFolderManager_EnabledFoldersChanged(object? sender, string mediaType)
        {
            if (mediaType != "Music")
                return;

            DispatcherQueue.TryEnqueue(async () =>
            {
                // ★ 回调是 async void，内部必须完整 try-catch，防止异常逃逸导致进程崩溃
                try
                {
                    if (!PageLifetimeService.IsActive(_containerGeneration))
                        return;

                    int generation = ++_reloadGeneration;
                    AppLogger.Info("[MusicPage] 媒体库文件夹勾选变更，重新加载并过滤");
                    _allMusic = await Task.Run(() =>
                        MediaLibraryFolderManager.FilterByEnabledFolders(
                            MediaScanner.LoadFromCache("Music"), "Music"));
                    if (generation != _reloadGeneration)
                        return;

                    MusicDataCache.AllMusic = _allMusic;
                    MusicDataCache.RebuildDerivedGroups();
                    _artistGroups = MusicDataCache.ArtistGroups;
                    _albumGroups = MusicDataCache.AlbumGroups;
                    _folderGroups = MusicDataCache.FolderGroups;
                    ApplyMusicSortAndFilter();
                    _thumbnailPreloader?.Start(_allMusic);
                    AppLogger.Info($"[MusicPage] 文件夹过滤刷新完成: {_allMusic.Count} 首歌");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "[MusicPage] 文件夹过滤刷新失败");
                }
            });
        }

        /// <summary>
        /// 点击「进入播放器」按钮，打开音乐播放器覆盖层。
        /// </summary>
        private void EnterPlayerButton_Click(object sender, RoutedEventArgs e)
        {
            (App.MainWindow as MainWindow)?.ShowPlayerOverlay(typeof(MusicPlayerPage), new MusicPlayerArgs());
        }
    }
}
