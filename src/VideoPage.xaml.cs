using SightoHear.Models;
using SightoHear.Services;
using SightoHear.Helpers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using WinRT.Interop;

namespace SightoHear
{
    public sealed partial class VideoPage : Page
    {
        // ---- 卡片尺寸（固定值：缩略图 220x124（16:9），文本区 28px，卡片总高 152） ----
        public static double VideoCardWidth => 220;
        public static double VideoCardHeight => 152;
        public static double VideoThumbHeight => 124;

        // 全部标签数据
        private List<MediaItem> _allVideos = new();
        private List<MediaItem> _filteredVideos = new();
        private List<VideoGroup> _groupedVideos = new();

        // 文件夹标签数据
        private List<VideoFolderGroup> _folderGroups = new();
        private List<VideoFolderGroup> _filteredFolderGroups = new();

        // 文件夹浏览器状态（支持深度导航）
        private string _currentFolderPath = string.Empty;
        private readonly Stack<string> _folderNavStack = new();
        private List<string> _videoLibraryPaths = new();

        // 收藏标签数据
        private List<Playlist> _allFavorites = new();
        private List<Playlist> _filteredFavorites = new();

        private DispatcherTimer? _debounceTimer;
        private bool _initializing = true;
        private string? _pendingLocatePath;
        private int _containerGeneration;
        private const double TabFixedWidth = 60;
        private int _selectedTabIndex;
        private bool _tabIsDragging;
        private double _tabDragStartX;
        private int _hoveredTabIndex = -1;
        private readonly Microsoft.UI.Input.InputCursor _handCursor;
        private readonly Microsoft.UI.Input.InputCursor _grabCursor;

        #region 多选功能 - 字段

        private bool _isVideoMultiSelect;
        private readonly HashSet<string> _videoMultiSelectedPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _isFolderMultiSelect;
        private readonly HashSet<string> _folderMultiSelectedPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _isFavoritesMultiSelect;
        private readonly HashSet<string> _favoritesMultiSelectedIds = new(StringComparer.OrdinalIgnoreCase);
        private int _multiSelectActiveTab = -1;
        private bool _selectAllChanging;

        #endregion

        private static readonly string _favoritesFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "video_favorites.json");

        public VideoPage()
        {
            _handCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Hand);
            _grabCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.SizeAll);

            InitializeComponent();
            SetupTabBar();

            ViewModeComboBox.SelectedIndex = App.SettingsHelper.VideoRememberView
                ? Math.Clamp(App.SettingsHelper.VideoDefaultView, 0, 1)
                : 0;
            SortComboBox.SelectedIndex = App.SettingsHelper.VideoRememberSort
                ? Math.Clamp(App.SettingsHelper.VideoDefaultSort, 0, 1)
                : 1;
            _initializing = false;
            AppLogger.Debug($"视频页面实例化, CurrentGen={PageLifetimeService.CurrentGeneration}");
            this.Loaded += VideoPage_Loaded;
            this.Unloaded += VideoPage_Unloaded;
        }

        // 从主页"跳转到对应位置"导航而来时，滚动到目标文件所在位置
        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is string path && !string.IsNullOrWhiteSpace(path))
                _pendingLocatePath = path;
            TryLocatePending();
        }

        private void TryLocatePending()
        {
            if (string.IsNullOrEmpty(_pendingLocatePath) || _selectedTabIndex != 0)
                return;

            var item = _filteredVideos.FirstOrDefault(
                v => string.Equals(v.FilePath, _pendingLocatePath, StringComparison.OrdinalIgnoreCase));
            if (item == null)
                return;

            _pendingLocatePath = null;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try { VideoGrid.ScrollIntoView(item); }
                catch { }
            });
        }

        private void VideoPage_Loaded(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("视频页面加载完成");
            _containerGeneration = PageLifetimeService.CurrentGeneration;
            PageLifetimeService.OnNavigatedTo("VideoPage");
            MediaScanner.CacheUpdated -= MediaScanner_CacheUpdated;
            MediaScanner.CacheUpdated += MediaScanner_CacheUpdated;
            // 订阅媒体库文件夹勾选变更（媒体库管理弹窗）
            MediaLibraryFolderManager.EnabledFoldersChanged -= MediaLibraryFolderManager_EnabledFoldersChanged;
            MediaLibraryFolderManager.EnabledFoldersChanged += MediaLibraryFolderManager_EnabledFoldersChanged;
            LoadVideosFromCache();
        }

        private void VideoPage_Unloaded(object sender, RoutedEventArgs e)
        {
            MediaScanner.CacheUpdated -= MediaScanner_CacheUpdated;
            MediaLibraryFolderManager.EnabledFoldersChanged -= MediaLibraryFolderManager_EnabledFoldersChanged;
            _debounceTimer?.Stop();
            _debounceTimer = null;

            // ★ 性能修复：释放 UI 树持有的 BitmapImage 引用。
            //   本页面为 NavigationCacheMode="Required"，离开后实例被 Frame 永久持有；
            //   清空所有数据视图的 ItemsSource 让缩略图位图可被 GC 回收，
            //   避免浏览页面累积内存导致 Win2D 掉帧（下次进入 Loaded 会全量重建）。
            try
            {
                VideoGrid.ItemsSource = null;
                FolderList.ItemsSource = null;
                FolderGrid.ItemsSource = null;
                FavoritesList.ItemsSource = null;
                FavoritesGrid.ItemsSource = null;
            }
            catch { /* 个别控件已卸载时忽略 */ }

            // ★ 修复：页面离开后其缩略图不再"热"，裁剪 ImageThumbnailService
            //   强引用 LRU 缓存（保留最近 192 条热数据）。
            //   否则离开页面后缩略图 BitmapImage 仍被缓存强引用，
            //   其 GPU 解码显存（每个 256px ≈ 256KB+）滞留不释放，
            //   浏览多页面后累积可达数百 MB（显存碎片化 → Win2D 卡顿）。
            ImageThumbnailService.TrimMemoryCache(192);

            PageLifetimeService.OnNavigatingAway();
        }

        private void MediaScanner_CacheUpdated(object? sender, string mediaType)
        {
            if (mediaType != "Video") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!PageLifetimeService.IsActive(_containerGeneration))
                    return;
                // 缓存数据变化时重置文件夹导航状态
                LoadVideosFromCache();
            });
        }

        private void LoadVideosFromCache()
        {
            var videos = MediaLibraryFolderManager.FilterByEnabledFolders(MediaScanner.LoadFromCache("Video"), "Video");
            _allVideos = videos;
            ApplySortAndFilter();
            BuildFolderGroups();
            LoadFavorites();

            if (_selectedTabIndex == 0)
            {
                EmptyStateText.Visibility = _allVideos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            // ★ 异步补全缺失的视频时长、分辨率与帧率（首次扫描或旧缓存中数据缺失的情况）
            // 使用 fire-and-forget，EnrichVideoMetadataIfNeeded 内部有防重入保护
            _ = EnrichVideoMetadataIfNeeded(videos);
        }

        // 防重入标志：防止 SaveToCache → CacheUpdated → LoadVideosFromCache → EnrichVideoMetadataIfNeeded 死循环
        private bool _isEnrichingMetadata;

        /// <summary>
        /// 异步补全视频时长、分辨率与帧率数据。仅在确实有补全成功时才保存到缓存，避免触发死循环。
        /// </summary>
        private async System.Threading.Tasks.Task EnrichVideoMetadataIfNeeded(List<MediaItem> videos)
        {
            // 防止重入：如果正在补全中，直接跳过
            if (_isEnrichingMetadata) return;

            var itemsNeedingDuration = videos.Where(v => !v.Duration.HasValue).ToList();
            var itemsNeedingDimensions = videos.Where(v =>
                v.PixelWidth <= 0 || v.PixelHeight <= 0 || !v.FrameRate.HasValue).ToList();

            if (itemsNeedingDuration.Count == 0 && itemsNeedingDimensions.Count == 0) return;

            _isEnrichingMetadata = true;
            try
            {
                bool anyEnriched = false;

                if (itemsNeedingDuration.Count > 0)
                {
                    AppLogger.Info($"开始异步补全 {itemsNeedingDuration.Count} 个视频的时长数据");
                    await MediaScanner.EnrichVideoDurationsAsync(itemsNeedingDuration);
                    anyEnriched |= itemsNeedingDuration.Any(v => v.Duration.HasValue);
                }

                if (itemsNeedingDimensions.Count > 0)
                {
                    AppLogger.Info($"开始异步补全 {itemsNeedingDimensions.Count} 个视频的分辨率/帧率数据");
                    await MediaScanner.EnrichVideoDimensionsAsync(itemsNeedingDimensions);
                    anyEnriched |= itemsNeedingDimensions.Any(v =>
                        v.PixelWidth > 0 && v.PixelHeight > 0);
                }

                // 仅在确实有至少一个视频成功获取到数据时才保存缓存
                if (anyEnriched)
                {
                    // 临时取消订阅 CacheUpdated 事件，防止 SaveToCache 再次触发 LoadVideosFromCache
                    MediaScanner.CacheUpdated -= MediaScanner_CacheUpdated;
                    try
                    {
                        MediaLibraryFolderManager.SaveMergedCache(videos, "Video");
                    }
                    finally
                    {
                        MediaScanner.CacheUpdated += MediaScanner_CacheUpdated;
                    }
                    AppLogger.Info("视频时长数据补全完成，缓存已更新");
                }
                else
                {
                    AppLogger.Info("视频时长数据补全完成（部分文件无法访问，跳过缓存更新）");
                }
            }
            finally
            {
                _isEnrichingMetadata = false;
            }
        }

        #region 全部标签逻辑

        private void ApplySortAndFilter()
        {
            if (SearchBox == null || SortComboBox == null || ViewModeComboBox == null)
                return;

            var query = _allVideos.AsEnumerable();
            var searchText = SearchBox?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!string.IsNullOrEmpty(searchText))
                query = query.Where(v => v.FileName.ToLowerInvariant().Contains(searchText));

            if (SortComboBox.SelectedIndex == 0)
                query = query.OrderBy(v => v.FileName);
            else
                query = query.OrderByDescending(v => v.DateModified);

            _filteredVideos = query.ToList();

            if (ViewModeComboBox?.SelectedIndex == 0)
                _groupedVideos = GroupVideosByDate(_filteredVideos, SortComboBox?.SelectedIndex == 0);
            else
                _groupedVideos = new();

            RefreshView();
        }

        private void RefreshView()
        {
            if (VideoGrid == null) return;

            bool isEmpty = _filteredVideos.Count == 0;
            EmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

            if (isEmpty)
            {
                VideoGrid.ItemsSource = null;
                return;
            }

            if (ViewModeComboBox?.SelectedIndex == 0)
            {
                GroupedCVS.Source = _groupedVideos;
                VideoGrid.ItemsSource = GroupedCVS.View;
            }
            else
            {
                VideoGrid.ItemsSource = _filteredVideos;
            }

            TryLocatePending();
        }

        private List<VideoGroup> GroupVideosByDate(List<MediaItem> videos, bool sortByName)
        {
            if (sortByName)
            {
                return videos
                    .GroupBy(v => new DateTime(v.DateModified.Year, v.DateModified.Month, 1))
                    .OrderBy(g => g.Key)
                    .Select(g => new VideoGroup
                    {
                        Header = g.Key.ToString("yyyy年M月"),
                        Items = g.OrderBy(v => v.FileName).ToList()
                    })
                    .ToList();
            }
            else
            {
                return videos
                    .GroupBy(v => new DateTime(v.DateModified.Year, v.DateModified.Month, 1))
                    .OrderByDescending(g => g.Key)
                    .Select(g => new VideoGroup
                    {
                        Header = g.Key.ToString("yyyy年M月"),
                        Items = g.OrderByDescending(v => v.DateModified).ToList()
                    })
                    .ToList();
            }
        }

        private void VideoGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_isVideoMultiSelect)
            {
                if (e.ClickedItem is MediaItem item)
                    ToggleVideoItemSelection(item);
                return;
            }
            if (e.ClickedItem is MediaItem item2) PlayVideo(item2);
        }

        private void VideoGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                args.ItemContainer.ContextFlyout = null;
                if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root &&
                    root.FindName("ThumbnailImage") as Image is { } image)
                {
                    image.Opacity = 0;
                }
                return;
            }

            if (args.Phase == 0 && args.Item is MediaItem item)
            {
                args.ItemContainer.ContextFlyout = CreateItemContextMenu(item);

                if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root)
                {
                    if (root.FindName("ItemCheckBox") is CheckBox cb)
                    {
                        cb.Visibility = _isVideoMultiSelect ? Visibility.Visible : Visibility.Collapsed;
                        cb.IsChecked = _videoMultiSelectedPaths.Contains(item.FilePath);
                    }
                }

                if (!string.IsNullOrEmpty(item.ThumbnailPath))
                {
                    if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root2 &&
                        root2.FindName("ThumbnailImage") as Image is { } image)
                    {
                        var bitmap = ImageThumbnailService.GetOrCreate(item.ThumbnailPath);
                        if (bitmap != null)
                        {
                            image.Source = bitmap;
                            image.Opacity = 1;
                        }
                    }
                }
                args.Handled = true;
            }
        }

        private MenuFlyout CreateItemContextMenu(MediaItem item)
        {
            MenuFlyout menu = new MenuFlyout();

            var playItem = new MenuFlyoutItem { Text = "播放", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE768" } };
            playItem.Click += (s, args) => PlayVideo(item);
            menu.Items.Add(playItem);

            var openWithItem = new MenuFlyoutItem { Text = "使用其他应用打开", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8A7" } };
            openWithItem.Click += (s, args) => _ = OpenWithExternalAsync(item);
            menu.Items.Add(openWithItem);

            var openLocationItem = new MenuFlyoutItem { Text = "打开文件所在位置", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uED25" } };
            openLocationItem.Click += (s, args) => OpenFileLocation(item);
            menu.Items.Add(openLocationItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            // 添加到收藏夹子菜单
            var addToFavoriteItem = new MenuFlyoutSubItem
            {
                Text = "添加到收藏夹",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE734" }
            };
            PopulateAddToFavoriteMenu(addToFavoriteItem, item);
            menu.Items.Add(addToFavoriteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE74D" } };
            deleteItem.Click += (s, args) => _ = DeleteVideoAsync(item);
            menu.Items.Add(deleteItem);

            var renameItem = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8AC" } };
            renameItem.Click += (s, args) => _ = RenameVideoAsync(item);
            menu.Items.Add(renameItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var propertiesItem = new MenuFlyoutItem { Text = "属性", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE946" } };
            propertiesItem.Click += (s, args) => _ = ShowPropertiesAsync(item);
            menu.Items.Add(propertiesItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var selectItem = new MenuFlyoutItem { Text = "选择", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE73E" } };
            selectItem.Click += (s, args) => EnterMultiSelectMode(0, item);
            menu.Items.Add(selectItem);

            return menu;
        }

        private void PopulateAddToFavoriteMenu(MenuFlyoutSubItem parent, MediaItem item)
        {
            if (_allFavorites.Count == 0)
            {
                parent.Items.Add(new MenuFlyoutItem { Text = "暂无收藏夹", IsEnabled = false });
                return;
            }

            foreach (var fav in _allFavorites)
            {
                bool alreadyAdded = fav.Items.Any(v =>
                    string.Equals(v.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));

                var favItem = new MenuFlyoutItem
                {
                    Text = alreadyAdded ? $"{fav.Name}（已添加）" : fav.Name,
                    IsEnabled = !alreadyAdded
                };
                favItem.Click += (_, _) =>
                {
                    fav.Items.Add(item);
                    SaveFavorites();
                    if (_selectedTabIndex == 2) RefreshFavoritesView();
                };
                parent.Items.Add(favItem);
            }
        }

        private void PlayVideo(MediaItem item)
        {
            var args = new VideoPlayerArgs
            {
                Playlist = _filteredVideos,
                StartIndex = _filteredVideos.IndexOf(item)
            };
            (App.MainWindow as MainWindow)?.ShowPlayerOverlay(typeof(VideoPlayerPage), args);
        }

        private async Task OpenWithExternalAsync(MediaItem item)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                await Launcher.LaunchFileAsync(file, new LauncherOptions { DisplayApplicationPicker = true });
            }
            catch (Exception ex) { AppLogger.Error(ex, "打开方式"); }
        }

        private void OpenFileLocation(MediaItem item)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer",
                    Arguments = "/select,\"" + item.FilePath + "\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private async Task DeleteVideoAsync(MediaItem item)
        {
            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将 \"{item.FileName}\" 移入到回收站吗？可随时还原。"
                    : $"确定要删除本地磁盘文件 \"{item.FileName}\" 吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    // 根据「删除文件时移入回收站」设置决定删除方式
                    if (App.SettingsHelper.DeleteToRecycleBin)
                        RecycleBinHelper.DeleteToRecycleBin(item.FilePath);
                    else if (File.Exists(item.FilePath))
                        File.Delete(item.FilePath);
                    if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                        File.Delete(item.ThumbnailPath);
                    _allVideos.Remove(item);
                    _filteredVideos.Remove(item);
                    MediaLibraryFolderManager.SaveMergedCache(_allVideos, "Video");
                    BuildFolderGroups();
                    ApplySortAndFilter();
                }
                catch { }
            }
        }

        private async Task RenameVideoAsync(MediaItem item)
        {
            var textBox = new TextBox { Text = item.FileName, Width = 280 };
            textBox.SelectAll();
            var dialog = new ContentDialog
            {
                Title = "重命名",
                Content = textBox,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot);
            if (result == ContentDialogResult.Primary)
            {
                var newName = textBox.Text.Trim();
                if (string.IsNullOrEmpty(newName) || newName == item.FileName) return;
                try
                {
                    var dir = Path.GetDirectoryName(item.FilePath);
                    if (string.IsNullOrEmpty(dir)) return;
                    var ext = Path.GetExtension(item.FilePath);
                    var newPath = Path.Combine(dir, newName + ext);
                    File.Move(item.FilePath, newPath);
                    item.FilePath = newPath;
                    item.FileName = newName;
                    item.Title = newName;
                    MediaLibraryFolderManager.SaveMergedCache(_allVideos, "Video");
                    BuildFolderGroups();
                    ApplySortAndFilter();
                }
                catch { }
            }
        }

        private async Task ShowPropertiesAsync(MediaItem item)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "名称: " + item.FileName, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "类型: " + item.MediaType, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "路径: " + item.FilePath, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "大小: " + FormatFileSize(item.FileSize), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "修改日期: " + item.DateModified.ToString("yyyy-MM-dd HH:mm:ss"), TextWrapping = TextWrapping.Wrap });
            if (item.Duration.HasValue)
                panel.Children.Add(new TextBlock { Text = "时长: " + item.Duration.Value.ToString("hh\\:mm\\:ss"), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(item.VideoResolutionText))
                panel.Children.Add(new TextBlock { Text = "分辨率: " + item.VideoResolutionText, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(item.FrameRateText))
                panel.Children.Add(new TextBlock { Text = "帧率: " + item.FrameRateText, TextWrapping = TextWrapping.Wrap });

            var dialog = new ContentDialog
            {
                Title = "属性",
                Content = panel,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await DialogService.ShowAsync(dialog, XamlRoot);
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
            return string.Format("{0:0.##} {1}", size, sizes[order]);
        }

        #endregion

        #region 搜索与排序事件

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                DebounceApplySortAndFilter();
        }

        private void ViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || ViewModeComboBox == null || ViewModeComboBox.SelectedIndex < 0) return;
            DebounceApplySortAndFilter();
            if (App.SettingsHelper.VideoRememberView)
            {
                App.SettingsHelper.VideoDefaultView = ViewModeComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || SortComboBox == null || SortComboBox.SelectedIndex < 0) return;
            DebounceApplySortAndFilter();
            if (App.SettingsHelper.VideoRememberSort)
            {
                App.SettingsHelper.VideoDefaultSort = SortComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void DebounceApplySortAndFilter()
        {
            if (_debounceTimer == null)
            {
                _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _debounceTimer.Tick += DebounceTimer_Tick;
            }
            else { _debounceTimer.Stop(); }
            _debounceTimer.Start();
        }

        private void DebounceTimer_Tick(object? sender, object e)
        {
            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
                ApplySortAndFilter();
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (RefreshButton == null || ScanStatusText == null) return;
            _allVideos.Clear();
            ApplySortAndFilter();
            RefreshButton.IsEnabled = false;
            ScanStatusText.Text = "正在扫描...";

            try
            {
                var paths = new List<string>();
                var settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SightoHear", "settings.json");
                if (File.Exists(settingsPath))
                {
                    try
                    {
                        var json = File.ReadAllText(settingsPath);
                        var node = JsonNode.Parse(json);
                        var pathsArray = node?["VideoLibraryPaths"]?.AsArray();
                        if (pathsArray != null)
                            foreach (var item in pathsArray)
                            {
                                var path = item?.GetValue<string>();
                                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                    paths.Add(path);
                            }
                    }
                    catch { }
                }

                var allItems = new List<MediaItem>();
                if (paths.Count > 0)
                    foreach (var path in paths)
                    {
                        var items = await MediaScanner.ScanFolderAsync(path, "Video", SearchOption.AllDirectories);
                        allItems.AddRange(items);
                    }

                var uniqueItems = allItems.GroupBy(x => x.FilePath).Select(g => g.First()).ToList();
                MediaScanner.SaveToCache(uniqueItems, "Video");
                _allVideos = MediaLibraryFolderManager.FilterByEnabledFolders(uniqueItems, "Video");
                BuildFolderGroups();
                ApplySortAndFilter();

                EmptyStateText.Visibility = _allVideos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ScanStatusText.Text = uniqueItems.Count > 0
                    ? "已扫描 " + uniqueItems.Count + " 个视频文件"
                    : "没有扫描到视频";
            }
            catch
            {
                if (ScanStatusText != null) ScanStatusText.Text = "扫描失败";
            }
            finally
            {
                if (RefreshButton != null) RefreshButton.IsEnabled = true;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    if (ScanStatusText != null && ScanStatusText.Text != "正在扫描...")
                        ScanStatusText.Text = string.Empty;
                };
                timer.Start();
            }
        }

        #endregion

        #region 文件夹标签逻辑

        private void BuildFolderGroups()
        {
            _folderGroups = VideoFolderGroup.BuildFrom(_allVideos);
            LoadVideoLibraryPaths();
            _currentFolderPath = string.Empty;
            _folderNavStack.Clear();
            UpdateFolderBreadcrumb();
            if (_selectedTabIndex == 1)
                ApplyFolderSortAndFilter();
        }

        private async void FolderRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _allVideos.Clear();
            ApplySortAndFilter();
            FolderRefreshButton.IsEnabled = false;

            try
            {
                var paths = new List<string>();
                var settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SightoHear", "settings.json");
                if (File.Exists(settingsPath))
                {
                    try
                    {
                        var json = File.ReadAllText(settingsPath);
                        var node = JsonNode.Parse(json);
                        var pathsArray = node?["VideoLibraryPaths"]?.AsArray();
                        if (pathsArray != null)
                            foreach (var item in pathsArray)
                            {
                                var path = item?.GetValue<string>();
                                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                    paths.Add(path);
                            }
                    }
                    catch { }
                }

                var allItems = new List<MediaItem>();
                if (paths.Count > 0)
                    foreach (var path in paths)
                    {
                        var items = await MediaScanner.ScanFolderAsync(path, "Video", SearchOption.AllDirectories);
                        allItems.AddRange(items);
                    }

                var uniqueItems = allItems.GroupBy(x => x.FilePath).Select(g => g.First()).ToList();
                MediaScanner.SaveToCache(uniqueItems, "Video");
                _allVideos = MediaLibraryFolderManager.FilterByEnabledFolders(uniqueItems, "Video");
                BuildFolderGroups();
                ApplySortAndFilter();
            }
            catch { }
            finally
            {
                FolderRefreshButton.IsEnabled = true;
            }
        }

        private void ApplyFolderSortAndFilter()
        {
            if (FolderSearchBox == null || FolderSortComboBox == null) return;

            // 计算当前层级的文件夹列表
            var currentLevelFolders = ComputeFoldersAtCurrentLevel();

            string searchText = FolderSearchBox.Text?.Trim() ?? string.Empty;
            int sortIndex = FolderSortComboBox.SelectedIndex;

            IEnumerable<VideoFolderGroup> query = currentLevelFolders;

            if (!string.IsNullOrWhiteSpace(searchText))
                query = query.Where(f =>
                    f.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    f.FolderPath.Contains(searchText, StringComparison.OrdinalIgnoreCase));

            query = sortIndex switch
            {
                0 => query.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase),
                1 => query.OrderByDescending(f => f.VideoCount),
                _ => query.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
            };

            _filteredFolderGroups = query.ToList();
            RefreshFolderView();
        }

        private void RefreshFolderView()
        {
            if (FolderList == null) return;

            bool isEmpty = _filteredFolderGroups.Count == 0;
            int mode = FolderViewModeComboBox?.SelectedIndex ?? 0;

            FolderEmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            FolderListHeader.Visibility = mode == 0 && !isEmpty ? Visibility.Visible : Visibility.Collapsed;
            FolderList.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
            FolderGrid.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;

            // 默认网格视图
            if (FolderViewModeComboBox != null && FolderViewModeComboBox.SelectedIndex < 0)
                FolderViewModeComboBox.SelectedIndex = 1;

            FolderList.ItemsSource = mode == 0 && !isEmpty ? _filteredFolderGroups : null;
            FolderGrid.ItemsSource = mode == 1 && !isEmpty ? _filteredFolderGroups : null;
        }

        private void FolderSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyFolderSortAndFilter();
        }

        private void FolderViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || FolderViewModeComboBox == null || FolderViewModeComboBox.SelectedIndex < 0) return;
            ApplyFolderSortAndFilter();
        }

        private void FolderSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || FolderSortComboBox == null || FolderSortComboBox.SelectedIndex < 0) return;
            ApplyFolderSortAndFilter();
        }

        private void FolderList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_isFolderMultiSelect && e.ClickedItem is VideoFolderGroup msFolder)
            {
                ToggleFolderItemSelection(msFolder);
                return;
            }
            if (e.ClickedItem is VideoFolderGroup folder)
                HandleFolderClick(folder);
        }

        private void FolderItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isFolderMultiSelect)
            {
                e.Handled = true;
                return;
            }
            if (sender is FrameworkElement { Tag: VideoFolderGroup folder })
            {
                e.Handled = true;
                HandleFolderClick(folder);
            }
        }

        private void FolderItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: VideoFolderGroup folder } element) return;
            e.Handled = true;

            var menu = new MenuFlyout();

            var openItem = new MenuFlyoutItem { Text = "打开", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8A7" } };
            openItem.Click += (_, _) => HandleFolderClick(folder);
            menu.Items.Add(openItem);

            var selectItem = new MenuFlyoutItem { Text = "选择", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE73E" } };
            selectItem.Click += (_, _) => EnterMultiSelectMode(1, folder);
            menu.Items.Add(selectItem);

            // 固定到侧边栏（视频文件夹）
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
                        Type = SidebarShortcutType.VideoFolder,
                        Title = $"视频文件夹：{folder.DisplayName}",
                        Name = folder.DisplayName,
                        Key = folder.FolderPath
                    });
            };
            menu.Items.Add(pinItem);

            var openLocationItem = new MenuFlyoutItem { Text = "打开文件夹所在位置", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uED25" } };
            openLocationItem.Click += (_, _) => OpenFolderLocation(folder);
            menu.Items.Add(openLocationItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE74D" } };
            deleteItem.Click += async (_, _) => await DeleteFolderAsync(folder);
            menu.Items.Add(deleteItem);

            var renameItem = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8AC" } };
            renameItem.Click += async (_, _) => await RenameFolderAsync(folder);
            menu.Items.Add(renameItem);

            var propertiesItem = new MenuFlyoutItem { Text = "属性", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE946" } };
            propertiesItem.Click += async (_, _) => await ShowFolderPropertiesAsync(folder);
            menu.Items.Add(propertiesItem);

            menu.ShowAt(element, e.GetPosition(element));
        }

        private void OpenFolderLocation(VideoFolderGroup folder)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer",
                    Arguments = $"\"{folder.FolderPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"打开文件夹位置失败：{folder.FolderPath}");
            }
        }

        private void OpenFolderDetail(VideoFolderGroup folder)
        {
            // 传入该文件夹下所有视频（含子文件夹），由详情页自行区分直接视频和子文件夹
            var allUnderFolder = _allVideos
                .Where(v => v.FilePath.StartsWith(folder.FolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToList();

            NavigateToDetailPage(typeof(VideoFolderDetailPage), new VideoFolderDetailArgs
            {
                FolderPath = folder.FolderPath,
                Videos = allUnderFolder
            });
        }

        private async System.Threading.Tasks.Task DeleteFolderAsync(VideoFolderGroup folder)
        {
            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将文件夹 \"{folder.DisplayName}\" 及其中的所有视频移入到回收站吗？可随时还原。"
                    : $"确定要删除文件夹 \"{folder.DisplayName}\" 及其中的所有本地磁盘文件吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    var videosToDelete = _allVideos
                        .Where(v => string.Equals(Path.GetDirectoryName(v.FilePath), folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var video in videosToDelete)
                    {
                        // 根据「删除文件时移入回收站」设置决定删除方式
                        if (File.Exists(video.FilePath))
                        {
                            if (App.SettingsHelper.DeleteToRecycleBin)
                                RecycleBinHelper.DeleteToRecycleBin(video.FilePath);
                            else
                                File.Delete(video.FilePath);
                        }
                        if (!string.IsNullOrEmpty(video.ThumbnailPath) && File.Exists(video.ThumbnailPath))
                            File.Delete(video.ThumbnailPath);
                    }

                    _allVideos.RemoveAll(v => videosToDelete.Contains(v));
                    MediaLibraryFolderManager.SaveMergedCache(_allVideos, "Video");
                    BuildFolderGroups();
                    ApplySortAndFilter();
                }
                catch { }
            }
        }

        private async System.Threading.Tasks.Task RenameFolderAsync(VideoFolderGroup folder)
        {
            var textBox = new TextBox { Text = folder.DisplayName, Width = 280 };
            textBox.SelectAll();
            var dialog = new ContentDialog
            {
                Title = "重命名文件夹",
                Content = textBox,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot);
            if (result == ContentDialogResult.Primary)
            {
                var newName = textBox.Text.Trim();
                if (string.IsNullOrEmpty(newName) || newName == folder.DisplayName) return;
                try
                {
                    var parentDir = Path.GetDirectoryName(folder.FolderPath);
                    if (string.IsNullOrEmpty(parentDir))
                    {
                        await ShowRenameErrorAsync("无法获取父目录路径");
                        return;
                    }

                    var newPath = Path.Combine(parentDir, newName);

                    // 检查目标路径是否已存在
                    if (Directory.Exists(newPath))
                    {
                        await ShowRenameErrorAsync($"名为 \"{newName}\" 的文件夹已存在，请使用其他名称。");
                        return;
                    }

                    // 检查源路径是否存在
                    if (!Directory.Exists(folder.FolderPath))
                    {
                        await ShowRenameErrorAsync($"源文件夹 \"{folder.DisplayName}\" 不存在，可能已被移动或删除。");
                        return;
                    }

                    Directory.Move(folder.FolderPath, newPath);
                    AppLogger.Info($"文件夹重命名成功：{folder.FolderPath} -> {newPath}");

                    // 递归更新所有子文件夹中的视频路径（包括深层子文件夹）
                    var oldPathPrefix = folder.FolderPath + Path.DirectorySeparatorChar;
                    var oldPathPrefixAlt = folder.FolderPath + Path.AltDirectorySeparatorChar;

                    var videosToUpdate = _allVideos
                        .Where(v => v.FilePath.StartsWith(oldPathPrefix, StringComparison.OrdinalIgnoreCase) ||
                                    v.FilePath.StartsWith(oldPathPrefixAlt, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var video in videosToUpdate)
                    {
                        var relativePath = video.FilePath.Substring(folder.FolderPath.Length);
                        if (relativePath.StartsWith(Path.DirectorySeparatorChar.ToString()) || relativePath.StartsWith(Path.AltDirectorySeparatorChar.ToString()))
                            relativePath = relativePath.Substring(1);
                        video.FilePath = Path.Combine(newPath, relativePath);
                    }

                    MediaLibraryFolderManager.SaveMergedCache(_allVideos, "Video");
                    BuildFolderGroups();
                    _currentFolderPath = string.Empty;
                    ApplyFolderSortAndFilter();
                    ApplySortAndFilter();
                }
                catch (UnauthorizedAccessException ex)
                {
                    AppLogger.Error(ex, $"重命名文件夹失败：权限不足 - {folder.FolderPath}");
                    await ShowRenameErrorAsync("权限不足，无法重命名该文件夹。请确保没有其他程序正在使用该文件夹中的文件。");
                }
                catch (IOException ex)
                {
                    AppLogger.Error(ex, $"重命名文件夹失败（IO异常）：{folder.FolderPath}");
                    await ShowRenameErrorAsync($"重命名失败，文件可能正在被占用：{ex.Message}");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"重命名文件夹失败：{folder.FolderPath}");
                    await ShowRenameErrorAsync($"重命名失败：{ex.Message}");
                }
            }
        }

        private async System.Threading.Tasks.Task ShowRenameErrorAsync(string message)
        {
            var errorDialog = new ContentDialog
            {
                Title = "重命名失败",
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await DialogService.ShowAsync(errorDialog, XamlRoot);
        }

        private async System.Threading.Tasks.Task ShowFolderPropertiesAsync(VideoFolderGroup folder)
        {
            var videos = _allVideos
                .Where(v => string.Equals(Path.GetDirectoryName(v.FilePath), folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            long totalSize = videos.Sum(v => v.FileSize);
            string sizeText = FormatFileSize(totalSize);

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = $"文件夹名：{folder.DisplayName}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"路径：{folder.FolderPath}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"视频数量：{videos.Count}" });
            content.Children.Add(new TextBlock { Text = $"总大小：{sizeText}" });

            var dialog = new ContentDialog
            {
                Title = "文件夹属性",
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await DialogService.ShowAsync(dialog, XamlRoot);
        }

        /// <summary>
        /// 处理文件夹点击：如果有子文件夹则原位导航进入，否则打开详情页
        /// </summary>
        private void HandleFolderClick(VideoFolderGroup folder)
        {
            if (FolderHasSubFolders(folder.FolderPath))
                NavigateIntoFolder(folder.FolderPath);
            else
                OpenFolderDetail(folder);
        }

        /// <summary>
        /// 加载配置的视频库根路径（仅包含媒体库管理弹窗中勾选展示的文件夹）
        /// </summary>
        private void LoadVideoLibraryPaths()
        {
            _videoLibraryPaths.Clear();
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SightoHear", "settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    // 仅加载勾选展示的文件夹（未勾选的文件夹不在文件夹 Tab 导航中显示）
                    List<string> enabled = MediaLibraryFolderManager.GetEnabledFolders("Video");
                    var json = File.ReadAllText(settingsPath);
                    var node = JsonNode.Parse(json);
                    var pathsArray = node?["VideoLibraryPaths"]?.AsArray();
                    if (pathsArray != null)
                        foreach (var item in pathsArray)
                        {
                            var path = item?.GetValue<string>();
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) &&
                                enabled.Contains(path, StringComparer.OrdinalIgnoreCase))
                                _videoLibraryPaths.Add(path);
                        }
                }
                catch { }
            }
        }

        /// <summary>
        /// 判断文件夹是否有子文件夹（含视频）
        /// </summary>
        private bool FolderHasSubFolders(string folderPath)
        {
            string prefix = folderPath + Path.DirectorySeparatorChar;
            return _allVideos.Any(v =>
                v.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetDirectoryName(v.FilePath), folderPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 原位导航进入子文件夹（从右到左滑入）
        /// </summary>
        private void NavigateIntoFolder(string folderPath)
        {
            _folderNavStack.Push(_currentFolderPath);
            _currentFolderPath = folderPath;
            UpdateFolderBreadcrumb();
            ApplyFolderSortAndFilter();
            AnimateFolderContentSlideIn(isBack: false);
        }

        /// <summary>
        /// 返回到上一级文件夹（从左到右滑入）
        /// </summary>
        public void NavigateBack()
        {
            if (_folderNavStack.Count == 0) return;
            _currentFolderPath = _folderNavStack.Pop();
            UpdateFolderBreadcrumb();
            ApplyFolderSortAndFilter();
            AnimateFolderContentSlideIn(isBack: true);
        }

        /// <summary>
        /// 当前是否可返回上一级文件夹
        /// </summary>
        public bool CanNavigateBack => _folderNavStack.Count > 0;

        /// <summary>
        /// 更新面包屑导航：只在文件夹 Tab 显示完整路径，根显示库路径
        /// </summary>
        private void UpdateFolderBreadcrumb()
        {
            if (FolderNavigationGrid == null || FolderBreadcrumbPanel == null)
                return;

            // 只在文件夹 Tab（index=1）显示面包屑
            bool isFolderTab = _selectedTabIndex == 1;
            FolderNavigationGrid.Visibility = isFolderTab ? Visibility.Visible : Visibility.Collapsed;
            if (!isFolderTab) return;

            // 确定根路径显示名：取第一个库路径，无则用"视频库"
            string rootPath = _videoLibraryPaths.Count > 0
                ? _videoLibraryPaths[0]
                : "视频库";

            // 重建面包屑路径（根 → 父级 → 当前）
            var pathParts = new List<(string DisplayName, string Path)>();

            // 1. 根路径
            pathParts.Add((rootPath, ""));

            // 2. 栈中非空路径（从根到当前父级）
            var stackList = _folderNavStack.ToList();
            stackList.Reverse();
            foreach (var p in stackList)
            {
                if (!string.IsNullOrEmpty(p))
                    pathParts.Add((Path.GetFileName(p), p));
            }

            // 3. 当前路径（非根时）
            if (!string.IsNullOrEmpty(_currentFolderPath))
                pathParts.Add((Path.GetFileName(_currentFolderPath), _currentFolderPath));
            FolderBreadcrumbPanel.Children.Clear();

            for (int i = 0; i < pathParts.Count; i++)
            {
                var (displayName, path) = pathParts[i];
                bool isLast = i == pathParts.Count - 1;

                if (i > 0)
                {
                    FolderBreadcrumbPanel.Children.Add(new TextBlock
                    {
                        Text = "›",
                        FontSize = 16,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 6, 0)
                    });
                }

                if (isLast)
                {
                    FolderBreadcrumbPanel.Children.Add(new TextBlock
                    {
                        Text = displayName,
                        FontSize = 16,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                else
                {
                    var link = new HyperlinkButton
                    {
                        Content = displayName,
                        FontSize = 16,
                        Padding = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Tag = path
                    };
                    link.Click += (s, _) =>
                    {
                        if (s is HyperlinkButton btn && btn.Tag is string targetPath)
                            NavigateBackTo(targetPath);
                    };
                    FolderBreadcrumbPanel.Children.Add(link);
                }
            }

            if (App.MainWindow is MainWindow mw)
                mw.UpdateVideoPageBackButtonState(CanNavigateBack && _folderNavStack.Count > 0);
        }

        /// <summary>
        /// 点击面包屑链接跳转到指定层级
        /// </summary>
        private void NavigateBackTo(string targetPath)
        {
            // 从栈中弹出直到 currentFolderPath 等于目标路径
            while (_folderNavStack.Count > 0)
            {
                string prev = _folderNavStack.Pop();
                _currentFolderPath = prev;
                if (string.Equals(_currentFolderPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    break;
            }
            UpdateFolderBreadcrumb();
            ApplyFolderSortAndFilter();
            AnimateFolderContentSlideIn(isBack: true);
        }

        /// <summary>
        /// 文件夹内容区滑入动画
        /// </summary>
        /// <param name="isBack">是否为返回操作（从左到右）</param>
        private void AnimateFolderContentSlideIn(bool isBack = false)
        {
            if (FolderContentPanel == null) return;
            FolderContentPanel.RenderTransform = new CompositeTransform();
            var storyboard = new Storyboard();
            var anim = new DoubleAnimation
            {
                From = isBack ? -60 : 60,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, FolderContentPanel);
            Storyboard.SetTargetProperty(anim, "(UIElement.RenderTransform).(CompositeTransform.TranslateX)");
            storyboard.Children.Add(anim);
            storyboard.Begin();
        }

        /// <summary>
        /// 计算当前层级应显示的文件夹列表（只包含含视频的子文件夹）
        /// </summary>
        private List<VideoFolderGroup> ComputeFoldersAtCurrentLevel()
        {
            var result = new List<VideoFolderGroup>();

            IEnumerable<MediaItem> relevantVideos;
            if (string.IsNullOrEmpty(_currentFolderPath))
            {
                // 根级别：显示各个视频库路径下的子文件夹
                relevantVideos = _allVideos.Where(v =>
                    _videoLibraryPaths.Any(p =>
                        v.FilePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                // 子级别：显示当前文件夹下的子文件夹
                relevantVideos = _allVideos.Where(v =>
                    v.FilePath.StartsWith(_currentFolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    v.FilePath.StartsWith(_currentFolderPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            }

            // 按立即子文件夹分组统计视频数
            var subfolderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var video in relevantVideos)
            {
                string parentDir;
                if (string.IsNullOrEmpty(_currentFolderPath))
                {
                    // 根级别：找到视频所属的库路径
                    parentDir = _videoLibraryPaths.FirstOrDefault(p =>
                        video.FilePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                }
                else
                {
                    parentDir = _currentFolderPath;
                }

                if (string.IsNullOrEmpty(parentDir)) continue;

                // 使用 Path.GetRelativePath 正确计算相对路径，避免前缀长度计算错误
                var relativePath = Path.GetRelativePath(parentDir, video.FilePath);
                if (string.IsNullOrEmpty(relativePath)) continue;

                var sepIndex = relativePath.IndexOf(Path.DirectorySeparatorChar);
                if (sepIndex < 0) sepIndex = relativePath.IndexOf(Path.AltDirectorySeparatorChar);

                if (sepIndex >= 0)
                {
                    var subfolderName = relativePath[..sepIndex];
                    var subfolderPath = Path.Combine(parentDir, subfolderName);
                    subfolderCounts.TryGetValue(subfolderPath, out int count);
                    subfolderCounts[subfolderPath] = count + 1;
                }
            }

            return subfolderCounts
                .Select(kvp => new VideoFolderGroup { FolderPath = kvp.Key, VideoCount = kvp.Value })
                .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        #endregion

        #region 收藏标签逻辑

        private void LoadFavorites()
        {
            try
            {
                if (File.Exists(_favoritesFilePath))
                {
                    var json = File.ReadAllText(_favoritesFilePath);
                    _allFavorites = System.Text.Json.JsonSerializer.Deserialize<List<Playlist>>(json) ?? new();
                }
                else
                {
                    _allFavorites = new();
                }
            }
            catch
            {
                _allFavorites = new();
            }

            if (_selectedTabIndex == 2) RefreshFavoritesView();
        }

        private void FavoritesRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadFavorites();
            ApplyFavoritesSortAndFilter();
        }

        private void SaveFavorites()
        {
            try
            {
                var dir = Path.GetDirectoryName(_favoritesFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var json = System.Text.Json.JsonSerializer.Serialize(_allFavorites);
                File.WriteAllText(_favoritesFilePath, json);
            }
            catch { }
        }

        private void ApplyFavoritesSortAndFilter()
        {
            if (FavoritesSearchBox == null) return;

            string searchText = FavoritesSearchBox.Text?.Trim() ?? string.Empty;
            int sortIndex = FavoritesSortComboBox?.SelectedIndex ?? -1;

            IEnumerable<Playlist> query = _allFavorites;

            if (!string.IsNullOrWhiteSpace(searchText))
                query = query.Where(f => f.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));

            query = sortIndex switch
            {
                0 => query.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
                1 => query.OrderByDescending(f => f.DateCreated),
                _ => query.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            };

            _filteredFavorites = query.ToList();
            RefreshFavoritesView();
        }

        private void RefreshFavoritesView()
        {
            if (FavoritesList == null) return;

            bool isEmpty = _filteredFavorites.Count == 0;
            int mode = FavoritesViewModeComboBox?.SelectedIndex ?? 0;

            FavoritesEmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            FavoritesListHeader.Visibility = mode == 0 && !isEmpty ? Visibility.Visible : Visibility.Collapsed;
            FavoritesList.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
            FavoritesGrid.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;

            // 默认网格视图
            if (FavoritesViewModeComboBox != null && FavoritesViewModeComboBox.SelectedIndex < 0)
                FavoritesViewModeComboBox.SelectedIndex = 1;

            FavoritesList.ItemsSource = mode == 0 && !isEmpty ? _filteredFavorites : null;
            FavoritesGrid.ItemsSource = mode == 1 && !isEmpty ? _filteredFavorites : null;
        }

        private void FavoritesSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyFavoritesSortAndFilter();
        }

        private void FavoritesViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || FavoritesViewModeComboBox == null || FavoritesViewModeComboBox.SelectedIndex < 0) return;
            ApplyFavoritesSortAndFilter();
        }

        private void FavoritesSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || FavoritesSortComboBox == null || FavoritesSortComboBox.SelectedIndex < 0) return;
            ApplyFavoritesSortAndFilter();
        }

        private async void CreateFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            string selectedCoverPath = string.Empty;

            var dialog = new ContentDialog
            {
                Title = "新建收藏夹",
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            // 水平布局：左侧封面，右侧名称 + 描述
            var rootGrid = new Grid { ColumnSpacing = 6, Width = 520 };
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左侧：封面图片选择器（1:1 正方形）
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
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
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

            // 悬停/按下视觉反馈
            coverBorder.PointerEntered += (_, _) => coverOverlay.Background = new SolidColorBrush(ColorHelper.FromArgb(0x14, 0, 0, 0));
            coverBorder.PointerExited += (_, _) => coverOverlay.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
                                AppLogger.Error(ex, "视频收藏夹封面预览加载失败");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "视频收藏夹封面选择失败");
                }
            };

            Grid.SetColumn(coverBorder, 0);
            rootGrid.Children.Add(coverBorder);

            // 右侧：名称 + 描述
            var rightPanel = new StackPanel { Spacing = 12 };
            var textBox = new TextBox { PlaceholderText = "收藏夹名称", Width = 250 };
            rightPanel.Children.Add(textBox);

            var descBox = new TextBox
            {
                PlaceholderText = "收藏夹描述（可选）",
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
                name = "新建收藏夹";

            var favorite = new Playlist
            {
                Name = name,
                Description = descBox.Text?.Trim() ?? string.Empty,
                CoverPath = selectedCoverPath,
                DateCreated = DateTime.Now
            };
            _allFavorites.Add(favorite);
            SaveFavorites();
            ApplyFavoritesSortAndFilter();
        }

        private void FavoritesList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_isFavoritesMultiSelect && e.ClickedItem is Playlist msFav)
            {
                ToggleFavoritesItemSelection(msFav);
                return;
            }
            if (e.ClickedItem is Playlist favorite)
                OpenFavoriteDetail(favorite);
        }

        private void FavoriteItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isFavoritesMultiSelect)
            {
                e.Handled = true;
                return;
            }
            if (sender is FrameworkElement { Tag: Playlist favorite })
            {
                e.Handled = true;
                OpenFavoriteDetail(favorite);
            }
        }

        private void FavoriteItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: Playlist favorite } element) return;
            e.Handled = true;

            var menu = new MenuFlyout();

            var openItem = new MenuFlyoutItem { Text = "打开", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8A7" } };
            openItem.Click += (_, _) => OpenFavoriteDetail(favorite);
            menu.Items.Add(openItem);

            var selectItem = new MenuFlyoutItem { Text = "选择", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE73E" } };
            selectItem.Click += (_, _) => EnterMultiSelectMode(2, favorite);
            menu.Items.Add(selectItem);

            // 固定到侧边栏（视频收藏夹）
            var pinItem = new MenuFlyoutItem
            {
                Text = SidebarShortcutService.IsPinned(favorite.Id) ? "从侧边栏取消固定" : "固定到侧边栏",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE71B" }
            };
            pinItem.Click += (_, _) =>
            {
                if (SidebarShortcutService.IsPinned(favorite.Id))
                    SidebarShortcutService.Remove(favorite.Id);
                else
                    SidebarShortcutService.Add(new SidebarShortcut
                    {
                        Type = SidebarShortcutType.VideoFavorite,
                        Title = $"视频收藏夹：{favorite.Name}",
                        Name = favorite.Name,
                        Key = favorite.Id
                    });
            };
            menu.Items.Add(pinItem);

            var playItem = new MenuFlyoutItem { Text = "播放全部", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE768" } };
            playItem.Click += (_, _) =>
            {
                if (favorite.Items.Count > 0)
                {
                    var args = new VideoPlayerArgs
                    {
                        Playlist = favorite.Items,
                        StartIndex = 0
                    };
                    (App.MainWindow as MainWindow)?.ShowPlayerOverlay(typeof(VideoPlayerPage), args);
                }
            };
            playItem.IsEnabled = favorite.Items.Count > 0;
            menu.Items.Add(playItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var editItem = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8AC" } };
            editItem.Click += async (_, _) =>
            {
                var tb = new TextBox { Text = favorite.Name, Width = 280 };
                tb.SelectAll();
                var dlg = new ContentDialog
                {
                    Title = "重命名收藏夹",
                    Content = tb,
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    XamlRoot = XamlRoot
                };
                var r = await DialogService.ShowAsync(dlg, XamlRoot);
                if (r == ContentDialogResult.Primary)
                {
                    var newName = tb.Text.Trim();
                    if (!string.IsNullOrEmpty(newName))
                    {
                        favorite.Name = newName;
                        SaveFavorites();
                        // 收藏夹重命名后同步侧边栏固定项名称/标题
                        MainWindow.NotifyDetailSaved(SidebarShortcutType.VideoFavorite, favorite.Id, favorite.Name);
                        ApplyFavoritesSortAndFilter();
                    }
                }
            };
            menu.Items.Add(editItem);

            var deleteItem = new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE74D" } };
            deleteItem.Click += (_, _) =>
            {
                _allFavorites.Remove(favorite);
                SaveFavorites();
                ApplyFavoritesSortAndFilter();
            };
            menu.Items.Add(deleteItem);

            menu.ShowAt(element, e.GetPosition(element));
        }

        private void OpenFavoriteDetail(Playlist favorite)
        {
            NavigateToDetailPage(typeof(VideoFavoriteDetailPage), new VideoFavoriteDetailArgs
            {
                Favorite = favorite,
                SaveChanges = SaveFavorites
            });
        }

        #endregion

        #region 多选功能

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void EnterMultiSelectMode(int tabIndex, object? starter = null)
        {
            if (_multiSelectActiveTab >= 0) return;
            _multiSelectActiveTab = tabIndex;
            UpdateToolbarVisibility();

            switch (tabIndex)
            {
                case 0: EnterVideoMultiSelectMode(starter as MediaItem); break;
                case 1: EnterFolderMultiSelectMode(starter as VideoFolderGroup); break;
                case 2: EnterFavoritesMultiSelectMode(starter as Playlist); break;
            }
        }

        private void ExitMultiSelectMode()
        {
            switch (_multiSelectActiveTab)
            {
                case 0: ExitVideoMultiSelectMode(); break;
                case 1: ExitFolderMultiSelectMode(); break;
                case 2: ExitFavoritesMultiSelectMode(); break;
            }
            _multiSelectActiveTab = -1;
            UpdateToolbarVisibility();
        }

        private void EnterVideoMultiSelectMode(MediaItem? starter)
        {
            _isVideoMultiSelect = true;
            _videoMultiSelectedPaths.Clear();
            if (starter != null)
                _videoMultiSelectedPaths.Add(starter.FilePath);
            MultiSelectToggleButton.IsChecked = true;
            UpdateAllVideoCheckBoxes();
            UpdateVideoMultiSelectCount();
        }

        private void ExitVideoMultiSelectMode()
        {
            _isVideoMultiSelect = false;
            _videoMultiSelectedPaths.Clear();
            _selectAllChanging = true;
            MultiSelectToggleButton.IsChecked = false;
            SelectAllCheckBox.IsChecked = false;
            _selectAllChanging = false;
            UpdateAllVideoCheckBoxes();
            MultiSelectCountText.Text = string.Empty;
        }

        private void EnterFolderMultiSelectMode(VideoFolderGroup? starter)
        {
            _isFolderMultiSelect = true;
            _folderMultiSelectedPaths.Clear();
            if (starter != null)
                _folderMultiSelectedPaths.Add(starter.FolderPath);
            FolderMultiSelectToggleButton.IsChecked = true;
            UpdateAllFolderCheckBoxes();
            UpdateFolderMultiSelectCount();
        }

        private void ExitFolderMultiSelectMode()
        {
            _isFolderMultiSelect = false;
            _folderMultiSelectedPaths.Clear();
            _selectAllChanging = true;
            FolderMultiSelectToggleButton.IsChecked = false;
            SelectAllCheckBox.IsChecked = false;
            _selectAllChanging = false;
            UpdateAllFolderCheckBoxes();
            MultiSelectCountText.Text = string.Empty;
        }

        private void EnterFavoritesMultiSelectMode(Playlist? starter)
        {
            _isFavoritesMultiSelect = true;
            _favoritesMultiSelectedIds.Clear();
            if (starter != null)
                _favoritesMultiSelectedIds.Add(starter.Id);
            FavoritesMultiSelectToggleButton.IsChecked = true;
            UpdateAllFavoritesCheckBoxes();
            UpdateFavoritesMultiSelectCount();
        }

        private void ExitFavoritesMultiSelectMode()
        {
            _isFavoritesMultiSelect = false;
            _favoritesMultiSelectedIds.Clear();
            _selectAllChanging = true;
            FavoritesMultiSelectToggleButton.IsChecked = false;
            SelectAllCheckBox.IsChecked = false;
            _selectAllChanging = false;
            UpdateAllFavoritesCheckBoxes();
            MultiSelectCountText.Text = string.Empty;
        }

        private void ToggleVideoItemSelection(MediaItem item)
        {
            if (!_videoMultiSelectedPaths.Remove(item.FilePath))
                _videoMultiSelectedPaths.Add(item.FilePath);
            UpdateVideoMultiSelectCount();

            // 同步当前项卡片的复选框勾选状态（与 ContainerContentChanging 保持一致）
            if (VideoGrid.ContainerFromItem(item) is GridViewItem container &&
                container.ContentTemplateRoot is FrameworkElement root &&
                root.FindName("ItemCheckBox") is CheckBox cb)
            {
                cb.IsChecked = _videoMultiSelectedPaths.Contains(item.FilePath);
            }
        }

        private void ToggleFolderItemSelection(VideoFolderGroup item)
        {
            if (!_folderMultiSelectedPaths.Remove(item.FolderPath))
                _folderMultiSelectedPaths.Add(item.FolderPath);
            UpdateFolderMultiSelectCount();

            // 按当前视图模式同步对应列表/网格中的复选框
            int mode = FolderViewModeComboBox?.SelectedIndex ?? 0;
            if (mode == 0)
            {
                if (FolderList.ContainerFromItem(item) is ListViewItem lc &&
                    lc.ContentTemplateRoot is FrameworkElement lRoot &&
                    lRoot.FindName("ItemCheckBox") is CheckBox lcb)
                {
                    lcb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath);
                }
            }
            else
            {
                if (FolderGrid.ContainerFromItem(item) is GridViewItem gc &&
                    gc.ContentTemplateRoot is FrameworkElement gRoot &&
                    gRoot.FindName("ItemCheckBox") is CheckBox gcb)
                {
                    gcb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath);
                }
            }
        }

        private void ToggleFavoritesItemSelection(Playlist item)
        {
            if (!_favoritesMultiSelectedIds.Remove(item.Id))
                _favoritesMultiSelectedIds.Add(item.Id);
            UpdateFavoritesMultiSelectCount();

            // 按当前视图模式同步对应列表/网格中的复选框
            int mode = FavoritesViewModeComboBox?.SelectedIndex ?? 0;
            if (mode == 0)
            {
                if (FavoritesList.ContainerFromItem(item) is ListViewItem lc &&
                    lc.ContentTemplateRoot is FrameworkElement lRoot &&
                    lRoot.FindName("ItemCheckBox") is CheckBox lcb)
                {
                    lcb.IsChecked = _favoritesMultiSelectedIds.Contains(item.Id);
                }
            }
            else
            {
                if (FavoritesGrid.ContainerFromItem(item) is GridViewItem gc &&
                    gc.ContentTemplateRoot is FrameworkElement gRoot &&
                    gRoot.FindName("ItemCheckBox") is CheckBox gcb)
                {
                    gcb.IsChecked = _favoritesMultiSelectedIds.Contains(item.Id);
                }
            }
        }

        private void UpdateVideoMultiSelectCount()
        {
            MultiSelectCountText.Text = $"已选择 {_videoMultiSelectedPaths.Count} 项";
            if (!_selectAllChanging)
            {
                _selectAllChanging = true;
                SelectAllCheckBox.IsChecked = _videoMultiSelectedPaths.Count > 0
                    && _videoMultiSelectedPaths.Count == _filteredVideos.Count
                    ? true
                    : _videoMultiSelectedPaths.Count == 0 ? false : null;
                _selectAllChanging = false;
            }
        }

        private void UpdateFolderMultiSelectCount()
        {
            MultiSelectCountText.Text = $"已选择 {_folderMultiSelectedPaths.Count} 项";
            if (!_selectAllChanging)
            {
                _selectAllChanging = true;
                SelectAllCheckBox.IsChecked = _folderMultiSelectedPaths.Count > 0
                    && _folderMultiSelectedPaths.Count == _filteredFolderGroups.Count
                    ? true
                    : _folderMultiSelectedPaths.Count == 0 ? false : null;
                _selectAllChanging = false;
            }
        }

        private void UpdateFavoritesMultiSelectCount()
        {
            MultiSelectCountText.Text = $"已选择 {_favoritesMultiSelectedIds.Count} 项";
            if (!_selectAllChanging)
            {
                _selectAllChanging = true;
                SelectAllCheckBox.IsChecked = _favoritesMultiSelectedIds.Count > 0
                    && _favoritesMultiSelectedIds.Count == _filteredFavorites.Count
                    ? true
                    : _favoritesMultiSelectedIds.Count == 0 ? false : null;
                _selectAllChanging = false;
            }
        }

        private void UpdateAllVideoCheckBoxes()
        {
            foreach (var item in _filteredVideos)
            {
                var container = VideoGrid.ContainerFromItem(item) as GridViewItem;
                if (container?.ContentTemplateRoot is FrameworkElement root)
                {
                    if (root.FindName("ItemCheckBox") is CheckBox cb)
                    {
                        cb.Visibility = _isVideoMultiSelect ? Visibility.Visible : Visibility.Collapsed;
                        cb.IsChecked = _videoMultiSelectedPaths.Contains(item.FilePath);
                    }
                }
            }
        }

        private void UpdateAllFolderCheckBoxes()
        {
            var source = _filteredFolderGroups;
            int mode = FolderViewModeComboBox?.SelectedIndex ?? 0;
            if (mode == 0)
            {
                foreach (var item in source)
                {
                    var container = FolderList.ContainerFromItem(item) as ListViewItem;
                    if (container?.ContentTemplateRoot is FrameworkElement root)
                    {
                        if (root.FindName("ItemCheckBox") is CheckBox cb)
                        {
                            cb.Visibility = _isFolderMultiSelect ? Visibility.Visible : Visibility.Collapsed;
                            cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath);
                        }
                    }
                }
            }
            else
            {
                foreach (var item in source)
                {
                    var container = FolderGrid.ContainerFromItem(item) as GridViewItem;
                    if (container?.ContentTemplateRoot is FrameworkElement root)
                    {
                        if (root.FindName("ItemCheckBox") is CheckBox cb)
                        {
                            cb.Visibility = _isFolderMultiSelect ? Visibility.Visible : Visibility.Collapsed;
                            cb.IsChecked = _folderMultiSelectedPaths.Contains(item.FolderPath);
                        }
                    }
                }
            }
        }

        private void UpdateAllFavoritesCheckBoxes()
        {
            var source = _filteredFavorites;
            int mode = FavoritesViewModeComboBox?.SelectedIndex ?? 0;
            if (mode == 0)
            {
                foreach (var item in source)
                {
                    var container = FavoritesList.ContainerFromItem(item) as ListViewItem;
                    if (container?.ContentTemplateRoot is FrameworkElement root)
                    {
                        if (root.FindName("ItemCheckBox") is CheckBox cb)
                        {
                            cb.Visibility = _isFavoritesMultiSelect ? Visibility.Visible : Visibility.Collapsed;
                            cb.IsChecked = _favoritesMultiSelectedIds.Contains(item.Id);
                        }
                    }
                }
            }
            else
            {
                foreach (var item in source)
                {
                    var container = FavoritesGrid.ContainerFromItem(item) as GridViewItem;
                    if (container?.ContentTemplateRoot is FrameworkElement root)
                    {
                        if (root.FindName("ItemCheckBox") is CheckBox cb)
                        {
                            cb.Visibility = _isFavoritesMultiSelect ? Visibility.Visible : Visibility.Collapsed;
                            cb.IsChecked = _favoritesMultiSelectedIds.Contains(item.Id);
                        }
                    }
                }
            }
        }

        private void SelectAllVideo()
        {
            _selectAllChanging = true;
            foreach (var item in _filteredVideos)
                _videoMultiSelectedPaths.Add(item.FilePath);
            SelectAllCheckBox.IsChecked = _filteredVideos.Count > 0 ? true : false;
            _selectAllChanging = false;
            UpdateAllVideoCheckBoxes();
            UpdateVideoMultiSelectCount();
        }

        private void DeselectAllVideo()
        {
            _selectAllChanging = true;
            _videoMultiSelectedPaths.Clear();
            SelectAllCheckBox.IsChecked = false;
            _selectAllChanging = false;
            UpdateAllVideoCheckBoxes();
            UpdateVideoMultiSelectCount();
        }

        private void SelectAllFolder()
        {
            _selectAllChanging = true;
            foreach (var item in _filteredFolderGroups)
                _folderMultiSelectedPaths.Add(item.FolderPath);
            SelectAllCheckBox.IsChecked = _filteredFolderGroups.Count > 0 ? true : false;
            _selectAllChanging = false;
            UpdateAllFolderCheckBoxes();
            UpdateFolderMultiSelectCount();
        }

        private void DeselectAllFolder()
        {
            _selectAllChanging = true;
            _folderMultiSelectedPaths.Clear();
            SelectAllCheckBox.IsChecked = false;
            _selectAllChanging = false;
            UpdateAllFolderCheckBoxes();
            UpdateFolderMultiSelectCount();
        }

        private void SelectAllFavorites()
        {
            _selectAllChanging = true;
            foreach (var item in _filteredFavorites)
                _favoritesMultiSelectedIds.Add(item.Id);
            SelectAllCheckBox.IsChecked = _filteredFavorites.Count > 0 ? true : false;
            _selectAllChanging = false;
            UpdateAllFavoritesCheckBoxes();
            UpdateFavoritesMultiSelectCount();
        }

        private void DeselectAllFavorites()
        {
            _selectAllChanging = true;
            _favoritesMultiSelectedIds.Clear();
            SelectAllCheckBox.IsChecked = false;
            _selectAllChanging = false;
            UpdateAllFavoritesCheckBoxes();
            UpdateFavoritesMultiSelectCount();
        }

        private void MultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isVideoMultiSelect)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(0);
        }

        private void FolderMultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFolderMultiSelect)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(1);
        }

        private void FavoritesMultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFavoritesMultiSelect)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(2);
        }

        private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_selectAllChanging) return;
            switch (_multiSelectActiveTab)
            {
                case 0: SelectAllVideo(); break;
                case 1: SelectAllFolder(); break;
                case 2: SelectAllFavorites(); break;
            }
        }

        private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_selectAllChanging) return;
            switch (_multiSelectActiveTab)
            {
                case 0: DeselectAllVideo(); break;
                case 1: DeselectAllFolder(); break;
                case 2: DeselectAllFavorites(); break;
            }
        }

        private void VideoItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                if (!_videoMultiSelectedPaths.Contains(item.FilePath))
                {
                    _videoMultiSelectedPaths.Add(item.FilePath);
                    UpdateVideoMultiSelectCount();
                }
            }
        }

        private void VideoItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                if (_videoMultiSelectedPaths.Remove(item.FilePath))
                    UpdateVideoMultiSelectCount();
            }
        }

        private void VideoFolderItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is VideoFolderGroup item)
            {
                if (!_folderMultiSelectedPaths.Contains(item.FolderPath))
                {
                    _folderMultiSelectedPaths.Add(item.FolderPath);
                    UpdateFolderMultiSelectCount();
                }
            }
        }

        private void VideoFolderItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is VideoFolderGroup item)
            {
                if (_folderMultiSelectedPaths.Remove(item.FolderPath))
                    UpdateFolderMultiSelectCount();
            }
        }

        private void VideoFavoritesItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is Playlist item)
            {
                if (!_favoritesMultiSelectedIds.Contains(item.Id))
                {
                    _favoritesMultiSelectedIds.Add(item.Id);
                    UpdateFavoritesMultiSelectCount();
                }
            }
        }

        private void VideoFavoritesItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is Playlist item)
            {
                if (_favoritesMultiSelectedIds.Remove(item.Id))
                    UpdateFavoritesMultiSelectCount();
            }
        }

        private void MultiSelectCancelButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
        }

        private async void MultiSelectDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            switch (_multiSelectActiveTab)
            {
                case 0: await DeleteSelectedVideosAsync(); break;
                case 1: await DeleteSelectedFoldersAsync(); break;
                case 2: await DeleteSelectedFavoritesAsync(); break;
            }
        }

        private async Task DeleteSelectedVideosAsync()
        {
            var itemsToDelete = _filteredVideos
                .Where(v => _videoMultiSelectedPaths.Contains(v.FilePath))
                .ToList();
            if (itemsToDelete.Count == 0) return;

            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将选中的 {itemsToDelete.Count} 个视频移入到回收站吗？可随时还原。"
                    : $"确定要删除选中的 {itemsToDelete.Count} 个本地磁盘文件吗？此操作不可撤销。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            try
            {
                foreach (var item in itemsToDelete)
                {
                    if (App.SettingsHelper.DeleteToRecycleBin)
                        RecycleBinHelper.DeleteToRecycleBin(item.FilePath);
                    else if (File.Exists(item.FilePath))
                        File.Delete(item.FilePath);
                    if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                        File.Delete(item.ThumbnailPath);
                }
                _allVideos.RemoveAll(v => itemsToDelete.Contains(v));
                _filteredVideos.RemoveAll(v => itemsToDelete.Contains(v));
                MediaScanner.SaveToCache(_allVideos, "Video");
                BuildFolderGroups();
                ApplySortAndFilter();
                ExitMultiSelectMode();
            }
            catch { }
        }

        private async Task DeleteSelectedFoldersAsync()
        {
            var foldersToDelete = _filteredFolderGroups
                .Where(f => _folderMultiSelectedPaths.Contains(f.FolderPath))
                .ToList();
            if (foldersToDelete.Count == 0) return;

            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将选中的 {foldersToDelete.Count} 个文件夹及其中的视频移入回收站吗？"
                    : $"确定要删除选中的 {foldersToDelete.Count} 个文件夹及其中的所有本地文件吗？此操作不可撤销。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            try
            {
                HashSet<string> folderPaths = new(foldersToDelete.Select(f => f.FolderPath), StringComparer.OrdinalIgnoreCase);
                var videosToDelete = _allVideos
                    .Where(v => folderPaths.Any(fp =>
                        string.Equals(Path.GetDirectoryName(v.FilePath), fp, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                foreach (var video in videosToDelete)
                {
                    if (File.Exists(video.FilePath))
                    {
                        if (App.SettingsHelper.DeleteToRecycleBin)
                            RecycleBinHelper.DeleteToRecycleBin(video.FilePath);
                        else
                            File.Delete(video.FilePath);
                    }
                    if (!string.IsNullOrEmpty(video.ThumbnailPath) && File.Exists(video.ThumbnailPath))
                        File.Delete(video.ThumbnailPath);
                }

                _allVideos.RemoveAll(v => videosToDelete.Contains(v));
                MediaScanner.SaveToCache(_allVideos, "Video");
                BuildFolderGroups();
                ApplySortAndFilter();
                ExitMultiSelectMode();
            }
            catch { }
        }

        private async Task DeleteSelectedFavoritesAsync()
        {
            var favsToDelete = _filteredFavorites
                .Where(f => _favoritesMultiSelectedIds.Contains(f.Id))
                .ToList();
            if (favsToDelete.Count == 0) return;

            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = $"确定要删除选中的 {favsToDelete.Count} 个收藏夹吗？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            _allFavorites.RemoveAll(f => favsToDelete.Contains(f));
            SaveFavorites();
            ApplyFavoritesSortAndFilter();
            ExitMultiSelectMode();
        }

        private void MultiSelectPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectActiveTab != 0) return;

            var selected = _filteredVideos
                .Where(v => _videoMultiSelectedPaths.Contains(v.FilePath))
                .ToList();
            if (selected.Count == 0) return;

            var args = new VideoPlayerArgs
            {
                Playlist = selected,
                StartIndex = 0
            };
            (App.MainWindow as MainWindow)?.ShowPlayerOverlay(typeof(VideoPlayerPage), args);
        }

        #endregion

        #region 导航辅助

        private void NavigateToDetailPage(Type pageType, object parameter)
        {
            if (App.MainWindow is MainWindow mainWin)
                mainWin.NavigateMainFrame(pageType, parameter);
        }

        #endregion

        #region Tab Bar 逻辑

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
            if (index == _selectedTabIndex) return;

            if (_multiSelectActiveTab >= 0)
                ExitMultiSelectMode();

            _selectedTabIndex = index;
            ClearHoverStates();
            AnimateIndicator(index);
            UpdateContentVisibility();
            UpdateToolbarVisibility();

            // 按需触发各标签的数据加载
            if (index == 0)
            {
                ApplySortAndFilter();
            }
            else if (index == 1)
            {
                ApplyFolderSortAndFilter();
            }
            else if (index == 2)
            {
                ApplyFavoritesSortAndFilter();
            }
            // 切换 Tab 时同步更新面包屑（非文件夹 Tab 隐藏）
            UpdateFolderBreadcrumb();
        }

        private void UpdateContentVisibility()
        {
            int index = _selectedTabIndex;
            AllContentPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            FolderContentPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            FavoritesContentPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateToolbarVisibility()
        {
            int index = _selectedTabIndex;
            bool inMulti = _multiSelectActiveTab >= 0;
            ToolbarGrid.Visibility = !inMulti && index == 0 ? Visibility.Visible : Visibility.Collapsed;
            FolderToolbarGrid.Visibility = !inMulti && index == 1 ? Visibility.Visible : Visibility.Collapsed;
            FavoritesToolbarGrid.Visibility = !inMulti && index == 2 ? Visibility.Visible : Visibility.Collapsed;
            MultiSelectToolbarGrid.Visibility = inMulti ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearHoverStates()
        {
            SetHoverState(0, false);
            SetHoverState(1, false);
            SetHoverState(2, false);
        }

        private void SetHoverState(int index, bool hovered)
        {
            Border overlay = index switch
            {
                0 => HoverOverlay0,
                1 => HoverOverlay1,
                2 => HoverOverlay2,
                _ => null!
            };
            if (overlay != null)
            {
                byte alpha = 0x0A;
                byte rgb = ActualTheme == ElementTheme.Dark ? (byte)0xFF : (byte)0x00;
                overlay.Background = hovered
                    ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(alpha, rgb, rgb, rgb))
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }

        private void AnimateIndicator(int index)
        {
            double targetX = index * TabFixedWidth;
            var storyboard = new Storyboard();
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var anim = new DoubleAnimation
            {
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = easing
            };
            Storyboard.SetTarget(anim, TabIndicator);
            Storyboard.SetTargetProperty(anim, "(UIElement.RenderTransform).(TranslateTransform.X)");
            storyboard.Children.Add(anim);
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
                _tabDragStartX = pt.X;
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
                if (dragIndex >= 0 && dragIndex < 3 && dragIndex != _selectedTabIndex)
                    SelectTab(dragIndex);
                return;
            }
            int hoveredIndex = (int)(pt.X / TabFixedWidth);
            if (hoveredIndex >= 0 && hoveredIndex < 3)
            {
                _hoveredTabIndex = hoveredIndex;
                for (int i = 0; i < 3; i++)
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
            if (clickedIndex >= 0 && clickedIndex < 3 && clickedIndex != _selectedTabIndex)
                SelectTab(clickedIndex);
        }

        private void TabSelector_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_tabIsDragging) { _hoveredTabIndex = -1; ClearHoverStates(); }
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

        #endregion

        /// <summary>
        /// 点击「媒体库管理」按钮，打开媒体库文件夹管理弹窗（勾选展示/添加/移除文件夹）。
        /// </summary>
        private async void LibraryManageButton_Click(object sender, RoutedEventArgs e)
        {
            await MediaLibraryManageDialog.ShowAsync(this.XamlRoot, "Video");
            AppLogger.Info("[VideoPage] 媒体库管理弹窗已关闭");
        }

        /// <summary>
        /// 媒体库文件夹勾选状态变更（媒体库管理弹窗内操作）：重新加载并按勾选过滤。
        /// </summary>
        private void MediaLibraryFolderManager_EnabledFoldersChanged(object? sender, string mediaType)
        {
            if (mediaType != "Video")
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!PageLifetimeService.IsActive(_containerGeneration))
                    return;
                LoadVideosFromCache();
            });
        }

        /// <summary>
        /// 点击「进入播放器」按钮，打开视频播放器覆盖层。
        /// </summary>
        private void EnterPlayerButton_Click(object sender, RoutedEventArgs e)
        {
            (App.MainWindow as MainWindow)?.ShowPlayerOverlay(typeof(VideoPlayerPage), new VideoPlayerArgs());
        }
    }
}
