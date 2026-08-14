using SightoHear.Models;
using SightoHear.Services;
using SightoHear.Helpers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
using System.Threading;
using System.Collections.Concurrent;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using WinRT.Interop;

namespace SightoHear
{
    public sealed partial class GalleryPage : Page
    {
        private List<MediaItem> _allImages = new();
        private List<MediaItem> _filteredImages = new();
        private List<GalleryRow> _rows = new();
        private DispatcherTimer? _debounceTimer;
        private int _filterGeneration;
        private int _reloadGeneration;
        private int _containerGeneration;
        private bool _cacheLoaded;
        private double _lastLayoutWidth;
        private bool _initializing = true;
        private string? _pendingLocatePath;
        private const double TabFixedWidth = 60;
        private int _selectedTabIndex;
        private bool _tabIsDragging;
        private double _tabDragStartX;
        private int _hoveredTabIndex = -1;
        private readonly Microsoft.UI.Input.InputCursor _handCursor;
        private readonly Microsoft.UI.Input.InputCursor _grabCursor;
        // 文件夹标签数据
        private List<GalleryFolderGroup> _folderGroups = new();
        private List<GalleryFolderGroup> _filteredFolderGroups = new();

        // 文件夹浏览器状态（支持深度导航）
        private string _currentFolderPath = string.Empty;
        private readonly Stack<string> _folderNavStack = new();
        private List<string> _imageLibraryPaths = new();

        // 收藏标签数据
        private List<Playlist> _allFavorites = new();
        private List<Playlist> _filteredFavorites = new();
        private static readonly string _favoritesFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "gallery_favorites.json");

        private const double CardSpacing = 8;
        private const double DefaultLayoutWidth = 760;
        // ★ 缩略图生成尺寸跟随图库设置（默认 192），与卡片高度联动
        private uint ThumbnailSize => App.SettingsHelper.GalleryThumbnailSize;
        private readonly SemaphoreSlim _genSemaphore = new(4, 4);
        private CancellationTokenSource? _warmupCts;

        // 多选状态
        private bool _isImageMultiSelect;
        private readonly HashSet<string> _imageMultiSelectedPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _isFolderMultiSelect;
        private readonly HashSet<string> _folderMultiSelectedPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _isFavoritesMultiSelect;
        private readonly HashSet<string> _favoritesMultiSelectedIds = new(StringComparer.OrdinalIgnoreCase);
        private int _multiSelectActiveTab = -1;
        private bool _selectAllChanging;

        // ★ 渐进分批加载：Loaded 事件只入队，后台 pump 按批次（每批 8 个、间隔 50ms）推进 UI 线程加载
        private readonly ConcurrentQueue<(WeakReference<Image> ImageRef, MediaItem MediaItem)> _pendingLoads = new();
        private CancellationTokenSource? _pumpCts;

        public GalleryPage()
        {
            _handCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Hand);
            _grabCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.SizeAll);

            InitializeComponent();
            SetupTabBar();

            ViewModeComboBox.SelectedIndex = App.SettingsHelper.GalleryRememberView
                ? Math.Clamp(App.SettingsHelper.GalleryDefaultView, 0, 1)
                : 0;
            SortComboBox.SelectedIndex = App.SettingsHelper.GalleryRememberSort
                ? Math.Clamp(App.SettingsHelper.GalleryDefaultSort, 0, 1)
                : 1;
            _initializing = false;
            AppLogger.Debug($"图库页面实例化, CurrentGen={PageLifetimeService.CurrentGeneration}");
            this.Loaded += GalleryPage_Loaded;
            this.Unloaded += GalleryPage_Unloaded;
        }

        // 从主页“跳转到对应位置”导航而来时，滚动到目标文件所在行
        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is string path && !string.IsNullOrWhiteSpace(path))
                _pendingLocatePath = path;
            TryLocatePending();
        }

        private void TryLocatePending()
        {
            if (string.IsNullOrEmpty(_pendingLocatePath))
                return;

            int rowIndex = _rows.FindIndex(
                s => s.Items.Any(m => string.Equals(
                    m.FilePath, _pendingLocatePath, StringComparison.OrdinalIgnoreCase)));
            if (rowIndex < 0)
                return;

            _pendingLocatePath = null;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    if (ImageRepeater.GetOrCreateElement(rowIndex) is UIElement element)
                    {
                        element.UpdateLayout();
                        element.StartBringIntoView(new BringIntoViewOptions
                        {
                            VerticalAlignmentRatio = 0.5
                        });
                    }
                }
                catch { }
            });
        }

        private void GalleryPage_Loaded(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("图库页面加载完成");
            _containerGeneration = PageLifetimeService.CurrentGeneration;
            AppLogger.Debug($"[Gallery] Loaded: 捕获 _containerGeneration={_containerGeneration}, CurrentGen={PageLifetimeService.CurrentGeneration}");
            PageLifetimeService.OnNavigatedTo("GalleryPage");
            MediaScanner.CacheUpdated -= MediaScanner_CacheUpdated;
            MediaScanner.CacheUpdated += MediaScanner_CacheUpdated;
            // 订阅媒体库文件夹勾选变更（媒体库管理弹窗）
            MediaLibraryFolderManager.EnabledFoldersChanged -= MediaLibraryFolderManager_EnabledFoldersChanged;
            MediaLibraryFolderManager.EnabledFoldersChanged += MediaLibraryFolderManager_EnabledFoldersChanged;

            if (!_cacheLoaded)
            {
                AppLogger.Info($"[Gallery] Loaded 触发 LoadImagesFromCache（首次加载）");
                LoadImagesFromCache();
            }
            else
            {
                // ★ 后台预热：为全部图片预生成磁盘缩略图，下次打开瞬间加载
                AppLogger.Debug($"[Gallery] Loaded 触发（缓存已加载，仅预热）");
                _ = PreWarmDiskThumbnailsAsync();
            }

            // ★ 启动渐进加载泵（首次或切回页面均启动）
            StartLoadPump();

            // 加载收藏数据（仅在收藏标签页时刷新视图）
            LoadFavorites();
        }

        private void MediaScanner_CacheUpdated(object? sender, string mediaType)
        {
            if (mediaType != "Image") return;
            // ★ 不做 Count > 0 跳过——缩略图磁盘缓存可能是扫描后期异步生成的，
            //    CacheUpdated 触发时必须重新加载以让 UI 感知最新的缓存状态。
            AppLogger.Debug($"[Gallery] CacheUpdated Image, Count={_allImages.Count}, IsActive={PageLifetimeService.IsActive(_containerGeneration)}, gen={_containerGeneration}/{PageLifetimeService.CurrentGeneration}");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!PageLifetimeService.IsActive(_containerGeneration))
                {
                    AppLogger.Debug($"[Gallery] CacheUpdated DispatcherQueue 执行时 generation 已过期，跳过");
                    return;
                }
                AppLogger.Info($"[Gallery] CacheUpdated 触发 LoadImagesFromCache | Count={_allImages.Count}");
                LoadImagesFromCache();
            });
        }

        private async void LoadImagesFromCache()
        {
            int generation = ++_reloadGeneration;
            AppLogger.Debug($"[Gallery] LoadImagesFromCache 开始 | generation={generation}");
            // 将耗时的磁盘文件读取和反序列化移至后台工作线程，并按勾选文件夹过滤
            var images = await Task.Run(() =>
                MediaLibraryFolderManager.FilterByEnabledFolders(MediaScanner.LoadFromCache("Image"), "Image"));
            if (generation != _reloadGeneration)
            {
                AppLogger.Debug($"[Gallery] LoadImagesFromCache 完成但 generation 已过期（{generation} ≠ {_reloadGeneration}），跳过");
                return;
            }

            _allImages = images;
            _cacheLoaded = true;
            AppLogger.Debug($"[Gallery] LoadImagesFromCache: {images.Count}项, 有ThumbnailPath={images.Count(v => !string.IsNullOrEmpty(v.ThumbnailPath))}");
            ApplySortAndFilter();
            BuildFolderGroups();

            // ★ 启动渐进加载泵：容器刷新后才开始按批次取队列中的待加载图片
            StartLoadPump();

            // ★ 后台预热：逐个生成磁盘缩略图，限流 2 个并发，不影响前台滚动
            _ = PreWarmDiskThumbnailsAsync();

            // 仅在当前为"全部"tab 时更新空状态（避免操作隐藏控件）
            if (_selectedTabIndex == 0)
            {
                if (_allImages.Count == 0)
                {
                    ImageRepeater.ItemsSource = null;
                    EmptyStateText.Visibility = Visibility.Visible;
                }
                else
                {
                    EmptyStateText.Visibility = Visibility.Collapsed;
                }
            }

            // 如果当前在收藏标签页，重新加载收藏数据（因为 _allImages 已更新）
            if (_selectedTabIndex == 1)
                LoadFavorites();

            AppLogger.Debug($"[Gallery] LoadImagesFromCache 完成");
        }

        private async void ApplySortAndFilter()
        {
            if (SearchBox == null || SortComboBox == null || ViewModeComboBox == null)
                return;

            int generation = ++_filterGeneration;

            // 1. 在 UI 线程提前捕获控件状态与集合快照，防止跨线程访问产生异常
            string searchText = SearchBox.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            int sortIndex = SortComboBox.SelectedIndex;
            int viewModeIndex = ViewModeComboBox.SelectedIndex;
            var allImagesSnapshot = _allImages.ToList();
            double layoutWidth = GetGalleryLayoutWidth();

            // 2. 将高能耗的 LINQ 过滤、多级排序、分组计算扔给后台工作线程处理，释放 CPU UI 线程
            await Task.Run(() =>
            {
                var query = allImagesSnapshot.AsEnumerable();

                // 搜索过滤
                if (!string.IsNullOrEmpty(searchText))
                    query = query.Where(v => v.FileName.ToLowerInvariant().Contains(searchText));

                // 排序
                if (sortIndex == 0)
                    query = query.OrderBy(v => v.FileName);
                else
                    query = query.OrderByDescending(v => v.DateModified);

                var filtered = query.ToList();
                List<GalleryRow> rows;

                if (viewModeIndex == 0)
                {
                    var grouped = GroupImagesByDate(filtered, sortIndex == 0);
                    rows = BuildGroupedRows(grouped, layoutWidth);
                }
                else
                {
                    rows = BuildContinuousRows(filtered, layoutWidth);
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (generation != _filterGeneration)
                    {
                        AppLogger.Debug($"[Gallery] ApplySortAndFilter 已过期（generation {generation} ≠ {_filterGeneration}），丢弃结果");
                        return;
                    }

                    _filteredImages = filtered;
                    _rows = rows;
                    RefreshView();
                    AppLogger.Debug($"[Gallery] ApplySortAndFilter: 过滤后={_filteredImages.Count} | 行数={_rows.Count} | searchText=\"{searchText}\" | sortIndex={sortIndex} | viewMode={viewModeIndex}");
                });
            });
        }

        private void RefreshView()
        {
            if (ImageRepeater == null)
                return;

            bool isEmpty = _filteredImages.Count == 0;
            EmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

            if (isEmpty)
            {
                ImageRepeater.ItemsSource = null;
                AppLogger.Debug($"[Gallery] RefreshView: 空列表，ItemsSource=null");
                if (_isImageMultiSelect)
                    UpdateImageMultiSelectCount();
                return;
            }

            ImageRepeater.ItemsSource = _rows;
            AppLogger.Debug($"[Gallery] RefreshView: ItemsSource=_rows | 项数={_filteredImages.Count} | 行数={_rows.Count}");

            if (_isImageMultiSelect)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    UpdateAllImageCheckBoxes();
                    UpdateImageMultiSelectCount();
                });
            }

            TryLocatePending();
        }

        private static List<GalleryRow> BuildGroupedRows(
            IEnumerable<VideoGroup> groups,
            double layoutWidth)
        {
            var result = new List<GalleryRow>();
            foreach (VideoGroup group in groups)
            {
                bool isFirstRow = true;
                foreach (IReadOnlyList<MediaItem> row in
                         BuildRows(group.Items, layoutWidth))
                {
                    result.Add(new GalleryRow
                    {
                        Header = isFirstRow ? group.Header : string.Empty,
                        Items = row
                    });
                    isFirstRow = false;
                }
            }

            return result;
        }

        private static List<GalleryRow> BuildContinuousRows(
            IReadOnlyList<MediaItem> items,
            double layoutWidth)
        {
            var result = new List<GalleryRow>();
            foreach (IReadOnlyList<MediaItem> row in
                     BuildRows(items, layoutWidth))
            {
                result.Add(new GalleryRow
                {
                    Items = row
                });
            }

            return result;
        }

        private static IEnumerable<IReadOnlyList<MediaItem>> BuildRows(
            IReadOnlyList<MediaItem> items,
            double layoutWidth)
        {
            double availableWidth = Math.Max(72, layoutWidth);
            var row = new List<MediaItem>();
            double usedWidth = 0;

            foreach (MediaItem item in items)
            {
                double cardWidth = Math.Max(72, item.GalleryCardWidth);
                double requiredWidth =
                    row.Count == 0
                        ? cardWidth
                        : CardSpacing + cardWidth;

                if (row.Count > 0 &&
                    usedWidth + requiredWidth > availableWidth)
                {
                    yield return row;
                    row = new List<MediaItem>();
                    usedWidth = 0;
                    requiredWidth = cardWidth;
                }

                row.Add(item);
                usedWidth += requiredWidth;
            }

            if (row.Count > 0)
                yield return row;
        }

        private double GetGalleryLayoutWidth()
        {
            if (GalleryScrollViewer == null)
                return DefaultLayoutWidth;

            double width =
                GalleryScrollViewer.ActualWidth -
                GalleryScrollViewer.Padding.Left -
                GalleryScrollViewer.Padding.Right;

            return width > 72 ? width : DefaultLayoutWidth;
        }

        private void GalleryScrollViewer_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            double layoutWidth = GetGalleryLayoutWidth();
            if (Math.Abs(layoutWidth - _lastLayoutWidth) < 16)
                return;

            _lastLayoutWidth = layoutWidth;
            if (_cacheLoaded && _allImages.Count > 0)
                DebounceApplySortAndFilter();
        }

        private void GalleryItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement
                {
                    Tag: MediaItem mediaItem
                })
            {
                if (_isImageMultiSelect)
                {
                    ToggleImageSelection(mediaItem);
                    e.Handled = true;
                    return;
                }
                OpenImageViewer(mediaItem);
                e.Handled = true;
            }
        }

        /// <summary>
        /// ★ 渐进分批加载策略：
        ///   Loaded 事件仅将 (Image, MediaItem) 配对入队，不做任何 I/O。
        ///   后台 LoadPumpAsync 按每批 8 个、间隔 50ms 的节奏在 UI 线程逐一加载，
        ///   避免一次性全量加载造成 CPU 99% 卡顿。
        /// </summary>
        private void ThumbnailImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Image image ||
                image.Tag is not MediaItem mediaItem)
                return;

            if (image.Source != null && image.Opacity > 0)
                return;

            _pendingLoads.Enqueue((new WeakReference<Image>(image), mediaItem));
        }

        /// <summary>
        /// 控件卸载：释放 Source + 隐藏，让 BitmapImage 可被 GC 回收。
        /// </summary>
        private void ThumbnailImage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Image image)
            {
                image.Source = null;
                image.Opacity = 0;
            }
        }

        /// <summary>
        /// 同步加载单张缩略图（在 UI 线程安全执行）：
        ///   内存缓存 → 磁盘缩略图（小文件，解码快）→ 原图 + 后台生成缩略图
        /// </summary>
        private void TryLoadImage(Image image, MediaItem mediaItem)
        {
            if (image.Source != null && image.Opacity > 0)
                return;

            string sourcePath = !string.IsNullOrEmpty(mediaItem.ThumbnailPath)
                ? mediaItem.ThumbnailPath
                : mediaItem.FilePath;

            // 1. 内存缓存 → 瞬间返回
            if (ImageThumbnailService.IsInMemoryCache(sourcePath))
            {
                var bmp = ImageThumbnailService.GetOrCreate(sourcePath);
                if (bmp != null) { image.Source = bmp; image.Opacity = 1; return; }
            }

            string diskPath = ImageThumbnailService.GetDiskCachePath(sourcePath, ThumbnailSize);

            // 内存缓存（diskPath 键）
            if (ImageThumbnailService.IsInMemoryCache(diskPath))
            {
                var bmp = ImageThumbnailService.GetOrCreate(diskPath);
                if (bmp != null) { image.Source = bmp; image.Opacity = 1; return; }
            }

            // 2. 磁盘缩略图存在 → 从小文件加载（解码极快）
            if (File.Exists(diskPath))
            {
                var bmp = ImageThumbnailService.GetOrCreate(diskPath);
                if (bmp != null) { image.Source = bmp; image.Opacity = 1; return; }
            }

            // 3. 降级：从原图加载（首次，大文件）+ 后台生成缩略图
            var bmp2 = ImageThumbnailService.GetOrCreate(sourcePath);
            if (bmp2 != null) { image.Source = bmp2; image.Opacity = 1; }

            // ★ 后台生成磁盘缩略图（限流 4 并发）
            _ = GenerateDiskThumbAsync(sourcePath);
        }

        /// <summary>
        /// 渐进加载泵：后台线程取 8 个 → dispatch 到 UI 线程 → 等待 8 个全处理完 → 50ms 间隔 → 下一批。
        /// </summary>
        private async Task LoadPumpAsync(CancellationToken ct)
        {
            const int batchSize = 8;

            while (!ct.IsCancellationRequested)
            {
                var batch = new List<(WeakReference<Image> ImageRef, MediaItem MediaItem)>(batchSize);
                for (int i = 0; i < batchSize; i++)
                {
                    if (!_pendingLoads.TryDequeue(out var item))
                        break;
                    batch.Add(item);
                }

                if (batch.Count == 0)
                {
                    await Task.Delay(200, ct);
                    continue;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        foreach (var (imageRef, mediaItem) in batch)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (!imageRef.TryGetTarget(out var image))
                                continue;
                            TryLoadImage(image, mediaItem);
                        }
                    }
                    finally
                    {
                        tcs.TrySetResult();
                    }
                });

                await tcs.Task;
                await Task.Delay(50, ct);
            }
        }

        /// <summary>取消旧泵（如果有），新建并启动渐进加载泵。</summary>
        private void StartLoadPump()
        {
            _pumpCts?.Cancel();
            _pumpCts?.Dispose();
            _pumpCts = new CancellationTokenSource();
            _ = LoadPumpAsync(_pumpCts.Token);
        }

        /// <summary>限流生成磁盘缩略图：最多 4 并发。</summary>
        private async Task GenerateDiskThumbAsync(string filePath)
        {
            await _genSemaphore.WaitAsync();
            try
            {
                if (!string.IsNullOrEmpty(filePath))
                    await ImageThumbnailService.GetOrCreateDiskThumbnailAsync(filePath, ThumbnailSize);
            }
            catch { }
            finally
            {
                _genSemaphore.Release();
            }
        }

        /// <summary>
        /// 后台预热：限流 4 并发生成全部缺失的磁盘缩略图。已有缩略图的直接跳过。
        /// 受图库设置「后台预热缩略图」开关控制。
        /// </summary>
        private async Task PreWarmDiskThumbnailsAsync()
        {
            if (!App.SettingsHelper.GalleryPreloadThumbnails)
                return;

            _warmupCts?.Cancel();
            _warmupCts = new CancellationTokenSource();
            var ct = _warmupCts.Token;

            foreach (var item in _allImages)
            {
                if (ct.IsCancellationRequested) break;

                string path = !string.IsNullOrEmpty(item.ThumbnailPath)
                    ? item.ThumbnailPath
                    : item.FilePath;

                string diskPath = ImageThumbnailService.GetDiskCachePath(path, ThumbnailSize);
                if (File.Exists(diskPath)) continue;

                try
                {
                    await GenerateDiskThumbAsync(path);
                }
                catch { }
            }
        }

        private void GalleryPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppLogger.Debug($"[Gallery] Unloaded, Count={_allImages.Count}, 即将 OnNavigatingAway");
            _warmupCts?.Cancel();
            _warmupCts?.Dispose();
            _warmupCts = null;
            _pumpCts?.Cancel();
            _pumpCts?.Dispose();
            _pumpCts = null;
            MediaScanner.CacheUpdated -= MediaScanner_CacheUpdated;
            MediaLibraryFolderManager.EnabledFoldersChanged -= MediaLibraryFolderManager_EnabledFoldersChanged;
            _debounceTimer?.Stop();
            _filterGeneration++;
            _reloadGeneration++;

            // ★ 性能修复：释放 UI 树持有的 BitmapImage 引用。
            //   本页面为 NavigationCacheMode="Required"，离开后实例被 Frame 永久持有；
            //   清空 ItemsSource 让缩略图位图可被 GC 回收，避免浏览页面累积内存导致 Win2D 掉帧。
            //   同时重置 _cacheLoaded，使下次进入时重新走 LoadImagesFromCache 完整重建视图。
            try
            {
                ImageRepeater.ItemsSource = null;
                _cacheLoaded = false;
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

        /// <summary>
        /// 右键时才创建菜单，避免为每个虚拟化容器常驻一套菜单对象。
        /// </summary>
        private void Item_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            var mediaItem = element.Tag as MediaItem;
            if (mediaItem == null) return;

            // 阻止事件继续冒泡
            e.Handled = true;

            // 动态创建 MenuFlyout
            var menu = new MenuFlyout();

            var viewItem = new MenuFlyoutItem { Text = "查看", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE890" } };
            viewItem.Click += (s, args) => OpenImageViewer(mediaItem);
            menu.Items.Add(viewItem);

            var selectItem = new MenuFlyoutItem { Text = "选择", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uEA98" } };
            selectItem.Click += (s, args) => EnterMultiSelectMode(0, mediaItem);
            menu.Items.Add(selectItem);

            var openItem = new MenuFlyoutItem { Text = "使用其他应用打开", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8A7" } };
            openItem.Click += (s, args) => _ = OpenWithExternalAsync(mediaItem);
            menu.Items.Add(openItem);

            var openLocationItem = new MenuFlyoutItem { Text = "打开文件所在位置", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uED25" } };
            openLocationItem.Click += (s, args) => OpenFileLocation(mediaItem);
            menu.Items.Add(openLocationItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            // 添加到收藏夹子菜单
            var addToFavoriteItem = new MenuFlyoutSubItem
            {
                Text = "添加到相册",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uEB9F" }
            };
            PopulateAddToFavoriteMenu(addToFavoriteItem, mediaItem);
            menu.Items.Add(addToFavoriteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE74D" } };
            deleteItem.Click += (s, args) => _ = DeleteImageAsync(mediaItem);
            menu.Items.Add(deleteItem);

            var renameItem = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8AC" } };
            renameItem.Click += (s, args) => _ = RenameImageAsync(mediaItem);
            menu.Items.Add(renameItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var propertiesItem = new MenuFlyoutItem { Text = "属性", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE946" } };
            propertiesItem.Click += (s, args) => _ = ShowPropertiesAsync(mediaItem);
            menu.Items.Add(propertiesItem);

            // 显示菜单
            menu.ShowAt(element, e.GetPosition(element));
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (SearchBox == null) return;
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                DebounceApplySortAndFilter();
        }

        private void ViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || ViewModeComboBox == null || ViewModeComboBox.SelectedIndex < 0)
                return;

            DebounceApplySortAndFilter();

            if (App.SettingsHelper.GalleryRememberView)
            {
                App.SettingsHelper.GalleryDefaultView = ViewModeComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || SortComboBox == null || SortComboBox.SelectedIndex < 0)
                return;

            DebounceApplySortAndFilter();

            if (App.SettingsHelper.GalleryRememberSort)
            {
                App.SettingsHelper.GalleryDefaultSort = SortComboBox.SelectedIndex;
                App.SettingsHelper.Save();
            }
        }

        /// <summary>
        /// 防抖：延迟 300ms 再执行 ApplySortAndFilter，避免快速输入或切换时频繁刷新
        /// </summary>
        private void DebounceApplySortAndFilter()
        {
            if (_debounceTimer == null)
            {
                _debounceTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                _debounceTimer.Tick += DebounceTimer_Tick;
            }
            else
            {
                _debounceTimer.Stop();
            }
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

        private async Task OpenWithExternalAsync(MediaItem item)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                var options = new LauncherOptions
                {
                    DisplayApplicationPicker = true
                };
                await Launcher.LaunchFileAsync(file, options);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "打开方式");
            }
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

        private async Task DeleteImageAsync(MediaItem item)
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
                    ImageThumbnailService.Remove(item.FilePath);
                    ImageThumbnailService.DeleteDiskCache(item.FilePath, ThumbnailSize);
                    // 根据「删除文件时移入回收站」设置决定删除方式
                    if (App.SettingsHelper.DeleteToRecycleBin)
                        RecycleBinHelper.DeleteToRecycleBin(item.FilePath);
                    else if (File.Exists(item.FilePath))
                        File.Delete(item.FilePath);
                    if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                        File.Delete(item.ThumbnailPath);
                    _allImages.Remove(item);
                    _filteredImages.Remove(item);
                    MediaLibraryFolderManager.SaveMergedCache(_allImages, "Image");
                    ApplySortAndFilter();
                }
                catch { }
            }
        }

        private async Task RenameImageAsync(MediaItem item)
        {
            var textBox = new TextBox
            {
                Text = item.FileName,
                Width = 280
            };
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
                    ImageThumbnailService.Remove(item.FilePath);
                    File.Move(item.FilePath, newPath);
                    item.FilePath = newPath;
                    item.FileName = newName;
                    item.Title = newName;
                    MediaLibraryFolderManager.SaveMergedCache(_allImages, "Image");
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

            var dialog = new ContentDialog
            {
                Title = "属性",
                Content = panel,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await DialogService.ShowAsync(dialog, XamlRoot);
        }

        private void OpenImageViewer(MediaItem? item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
                return;

            var playlist = _filteredImages.Count > 0 ? _filteredImages.ToList() : _allImages.ToList();
            int index = playlist.FindIndex(x => x.FilePath == item.FilePath);
            if (index < 0) index = 0;

            (App.MainWindow as MainWindow)?.OpenImageViewer(
                new ImageViewerArgs
                {
                    Playlist = playlist,
                    StartIndex = index
                });
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return string.Format("{0:0.##} {1}", size, sizes[order]);
        }

        private List<VideoGroup> GroupImagesByDate(List<MediaItem> images, bool sortByName)
        {
            if (sortByName)
            {
                return images
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
                return images
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

        private void ClearGalleryViewAndCache()
        {
            _allImages.Clear();
            _filteredImages.Clear();
            _rows.Clear();

            MediaScanner.SaveToCache(new List<MediaItem>(), "Image");

            if (ImageRepeater != null)
                ImageRepeater.ItemsSource = null;

            if (EmptyStateText != null)
                EmptyStateText.Visibility = Visibility.Visible;

        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (RefreshButton == null || ScanStatusText == null) return;

            RefreshButton.IsEnabled = false;
            ImageThumbnailService.Clear();
            ScanStatusText.Text = "正在扫描...";

            try
            {
                // 读取 settings.json 中的 ImageLibraryPaths（与图库设置页扫描逻辑一致）
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
                        var pathsArray = node?["ImageLibraryPaths"]?.AsArray();
                        if (pathsArray != null)
                        {
                            foreach (var item in pathsArray)
                            {
                                var path = item?.GetValue<string>();
                                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                    paths.Add(path);
                            }
                        }
                    }
                    catch { }
                }

                // 不再自动填充默认路径：如果用户已清空列表，此处保持空状态
                // 仅在首次安装时由 MediaScanner.InitializeDefaultLibrarySettings() 写入默认路径

                if (paths.Count == 0)
                {
                    ClearGalleryViewAndCache();
                    ScanStatusText.Text = "没有可扫描的文件夹";
                    return;
                }

                // 遍历所有路径扫描并合并结果
                var allItems = new List<MediaItem>();
                var existingItems = _allImages
                    .GroupBy(
                        item => item.FilePath,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First(),
                        StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                {
                    var items = await MediaScanner.ScanFolderAsync(
                        path,
                        "Image",
                        SearchOption.AllDirectories,
                        existingItems);
                    allItems.AddRange(items);
                }

                // 去重（按 FilePath）并保存到缓存
                var uniqueItems = allItems.GroupBy(x => x.FilePath).Select(g => g.First()).ToList();

                if (uniqueItems.Count == 0)
                {
                    ClearGalleryViewAndCache();
                    ScanStatusText.Text = "没有扫描到图片";
                    return;
                }

                MediaScanner.SaveToCache(uniqueItems, "Image");

                // 刷新 UI
                _allImages = MediaLibraryFolderManager.FilterByEnabledFolders(uniqueItems, "Image");
                ApplySortAndFilter();

                ScanStatusText.Text = "已扫描 " + uniqueItems.Count + " 个图片文件";
            }
            catch
            {
                if (ScanStatusText != null)
                    ScanStatusText.Text = "扫描失败";
            }
            finally
            {
                if (RefreshButton != null)
                    RefreshButton.IsEnabled = true;

                // 使用 DispatcherTimer 替代 Task.Delay+ContinueWith，避免页面卸载后访问 UI 崩溃
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
            if (index == _selectedTabIndex)
                return;

            ExitMultiSelectMode();

            _selectedTabIndex = index;
            ClearHoverStates();
            AnimateIndicator(index);
            UpdateContentVisibility();
            // 切换 Tab 时同步更新面包屑（非文件夹 Tab 隐藏）
            UpdateFolderBreadcrumb();
        }

        private void UpdateContentVisibility()
        {
            int index = _selectedTabIndex;
            AllContentPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            FavoritesContentPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            FolderContentPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

            // 切换标签时控制工具栏显隐
            if (ToolbarGrid != null)
                ToolbarGrid.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (FavoritesToolbarGrid != null)
                FavoritesToolbarGrid.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (FolderToolbarGrid != null)
                FolderToolbarGrid.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (index == 0)
            {
                RefreshView();
            }
            else if (index == 1)
            {
                ApplyFavoritesSortAndFilter();
            }
            else if (index == 2)
            {
                ApplyFolderSortAndFilter();
            }
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
                {
                    SelectTab(dragIndex);
                }
                return;
            }

            int hoveredIndex = (int)(pt.X / TabFixedWidth);
            if (hoveredIndex >= 0 && hoveredIndex < 3)
            {
                _hoveredTabIndex = hoveredIndex;
                for (int i = 0; i < 3; i++)
                {
                    SetHoverState(i, i == hoveredIndex && i != _selectedTabIndex);
                }
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

        #endregion

        #region 文件夹标签逻辑

        private void BuildFolderGroups()
        {
            _folderGroups = GalleryFolderGroup.BuildFrom(_allImages);
            LoadImageLibraryPaths();
            _currentFolderPath = string.Empty;
            _folderNavStack.Clear();
            UpdateFolderBreadcrumb();
            if (_selectedTabIndex == 2)
                ApplyFolderSortAndFilter();
        }

        private async void FolderRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _allImages.Clear();
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
                        var pathsArray = node?["ImageLibraryPaths"]?.AsArray();
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
                        var items = await MediaScanner.ScanFolderAsync(path, "Image", SearchOption.AllDirectories);
                        allItems.AddRange(items);
                    }

                var uniqueItems = allItems.GroupBy(x => x.FilePath).Select(g => g.First()).ToList();
                MediaScanner.SaveToCache(uniqueItems, "Image");
                _allImages = MediaLibraryFolderManager.FilterByEnabledFolders(uniqueItems, "Image");
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

            var currentLevelFolders = ComputeFoldersAtCurrentLevel();

            string searchText = FolderSearchBox.Text?.Trim() ?? string.Empty;
            int sortIndex = FolderSortComboBox.SelectedIndex;

            IEnumerable<GalleryFolderGroup> query = currentLevelFolders;

            if (!string.IsNullOrWhiteSpace(searchText))
                query = query.Where(f =>
                    f.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    f.FolderPath.Contains(searchText, StringComparison.OrdinalIgnoreCase));

            query = sortIndex switch
            {
                0 => query.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase),
                1 => query.OrderByDescending(f => f.ImageCount),
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

            if (_isFolderMultiSelect)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    UpdateAllFolderCheckBoxes();
                    UpdateFolderMultiSelectCount();
                });
            }
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
            if (e.ClickedItem is GalleryFolderGroup folder)
            {
                if (_isFolderMultiSelect)
                {
                    ToggleFolderSelection(folder);
                    return;
                }
                HandleFolderClick(folder);
            }
        }

        private void FolderItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: GalleryFolderGroup folder })
            {
                e.Handled = true;
                if (_isFolderMultiSelect) return;
                HandleFolderClick(folder);
            }
        }

        private void FolderItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: GalleryFolderGroup folder } element) return;
            e.Handled = true;

            var menu = new MenuFlyout();

            var openItem = new MenuFlyoutItem { Text = "打开", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8A7" } };
            openItem.Click += (_, _) => HandleFolderClick(folder);
            menu.Items.Add(openItem);

            var selectItem = new MenuFlyoutItem { Text = "选择", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uEA98" } };
            selectItem.Click += (_, _) => EnterMultiSelectMode(2, folder);
            menu.Items.Add(selectItem);

            // 固定到侧边栏（图库文件夹）
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
                        Type = SidebarShortcutType.GalleryFolder,
                        Title = $"图库文件夹：{folder.DisplayName}",
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

        private void OpenFolderLocation(GalleryFolderGroup folder)
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

        private void OpenFolderDetail(GalleryFolderGroup folder)
        {
            // 传入该文件夹下所有图片（含子文件夹），由详情页自行区分直接图片和子文件夹
            var allUnderFolder = _allImages
                .Where(v => v.FilePath.StartsWith(folder.FolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToList();

            NavigateToDetailPage(typeof(GalleryFolderDetailPage), new GalleryFolderDetailArgs
            {
                FolderPath = folder.FolderPath,
                Images = allUnderFolder
            });
        }

        private async System.Threading.Tasks.Task DeleteFolderAsync(GalleryFolderGroup folder)
        {
            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将文件夹 \"{folder.DisplayName}\" 及其中的所有图片移入到回收站吗？可随时还原。"
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
                    var imagesToDelete = _allImages
                        .Where(v => string.Equals(Path.GetDirectoryName(v.FilePath), folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var image in imagesToDelete)
                    {
                        // 根据「删除文件时移入回收站」设置决定删除方式
                        if (File.Exists(image.FilePath))
                        {
                            if (App.SettingsHelper.DeleteToRecycleBin)
                                RecycleBinHelper.DeleteToRecycleBin(image.FilePath);
                            else
                                File.Delete(image.FilePath);
                        }
                        if (!string.IsNullOrEmpty(image.ThumbnailPath) && File.Exists(image.ThumbnailPath))
                            File.Delete(image.ThumbnailPath);
                    }

                    _allImages.RemoveAll(v => imagesToDelete.Contains(v));
                    MediaLibraryFolderManager.SaveMergedCache(_allImages, "Image");
                    BuildFolderGroups();
                    ApplySortAndFilter();
                }
                catch { }
            }
        }

        private async System.Threading.Tasks.Task RenameFolderAsync(GalleryFolderGroup folder)
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

                    if (Directory.Exists(newPath))
                    {
                        await ShowRenameErrorAsync($"名为 \"{newName}\" 的文件夹已存在，请使用其他名称。");
                        return;
                    }

                    if (!Directory.Exists(folder.FolderPath))
                    {
                        await ShowRenameErrorAsync($"源文件夹 \"{folder.DisplayName}\" 不存在，可能已被移动或删除。");
                        return;
                    }

                    Directory.Move(folder.FolderPath, newPath);

                    var oldPathPrefix = folder.FolderPath + Path.DirectorySeparatorChar;
                    var oldPathPrefixAlt = folder.FolderPath + Path.AltDirectorySeparatorChar;

                    var imagesToUpdate = _allImages
                        .Where(v => v.FilePath.StartsWith(oldPathPrefix, StringComparison.OrdinalIgnoreCase) ||
                                    v.FilePath.StartsWith(oldPathPrefixAlt, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var image in imagesToUpdate)
                    {
                        var relativePath = image.FilePath.Substring(folder.FolderPath.Length);
                        if (relativePath.StartsWith(Path.DirectorySeparatorChar.ToString()) || relativePath.StartsWith(Path.AltDirectorySeparatorChar.ToString()))
                            relativePath = relativePath.Substring(1);
                        image.FilePath = Path.Combine(newPath, relativePath);
                    }

                    MediaLibraryFolderManager.SaveMergedCache(_allImages, "Image");
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

        private async System.Threading.Tasks.Task ShowFolderPropertiesAsync(GalleryFolderGroup folder)
        {
            var images = _allImages
                .Where(v => string.Equals(Path.GetDirectoryName(v.FilePath), folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            long totalSize = images.Sum(v => v.FileSize);
            string sizeText = FormatFileSize(totalSize);

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = $"文件夹名：{folder.DisplayName}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"路径：{folder.FolderPath}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"图片数量：{images.Count}" });
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

        private void HandleFolderClick(GalleryFolderGroup folder)
        {
            if (FolderHasSubFolders(folder.FolderPath))
                NavigateIntoFolder(folder.FolderPath);
            else
                OpenFolderDetail(folder);
        }

        private bool FolderHasSubFolders(string folderPath)
        {
            string prefix = folderPath + Path.DirectorySeparatorChar;
            return _allImages.Any(v =>
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

        public bool CanNavigateBack => _folderNavStack.Count > 0;

        /// <summary>
        /// 更新面包屑导航：只在文件夹 Tab 显示完整路径，根显示库路径
        /// </summary>
        private void UpdateFolderBreadcrumb()
        {
            if (FolderNavigationGrid == null || FolderBreadcrumbPanel == null)
                return;

            // 只在文件夹 Tab（index=2）显示面包屑
            bool isFolderTab = _selectedTabIndex == 2;
            FolderNavigationGrid.Visibility = isFolderTab ? Visibility.Visible : Visibility.Collapsed;
            if (!isFolderTab) return;

            // 确定根路径显示名：取第一个库路径，无则用"图库"
            string rootPath = _imageLibraryPaths.Count > 0
                ? _imageLibraryPaths[0]
                : "图库";

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
                mw.UpdateGalleryPageBackButtonState(CanNavigateBack);
        }

        /// <summary>
        /// 点击面包屑链接跳转到指定层级
        /// </summary>
        private void NavigateBackTo(string targetPath)
        {
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

        private void LoadImageLibraryPaths()
        {
            _imageLibraryPaths.Clear();
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SightoHear", "settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    // 仅加载勾选展示的文件夹（未勾选的文件夹不在文件夹 Tab 导航中显示）
                    List<string> enabled = MediaLibraryFolderManager.GetEnabledFolders("Image");
                    var json = File.ReadAllText(settingsPath);
                    var node = JsonNode.Parse(json);
                    var pathsArray = node?["ImageLibraryPaths"]?.AsArray();
                    if (pathsArray != null)
                        foreach (var item in pathsArray)
                        {
                            var path = item?.GetValue<string>();
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) &&
                                enabled.Contains(path, StringComparer.OrdinalIgnoreCase))
                                _imageLibraryPaths.Add(path);
                        }
                }
                catch { }
            }
        }

        private List<GalleryFolderGroup> ComputeFoldersAtCurrentLevel()
        {
            var result = new List<GalleryFolderGroup>();

            IEnumerable<MediaItem> relevantImages;
            if (string.IsNullOrEmpty(_currentFolderPath))
            {
                relevantImages = _allImages.Where(v =>
                    _imageLibraryPaths.Any(p =>
                        v.FilePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                relevantImages = _allImages.Where(v =>
                    v.FilePath.StartsWith(_currentFolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    v.FilePath.StartsWith(_currentFolderPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            }

            var subfolderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var image in relevantImages)
            {
                string parentDir;
                if (string.IsNullOrEmpty(_currentFolderPath))
                {
                    parentDir = _imageLibraryPaths.FirstOrDefault(p =>
                        image.FilePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                }
                else
                {
                    parentDir = _currentFolderPath;
                }

                if (string.IsNullOrEmpty(parentDir)) continue;

                var relativePath = Path.GetRelativePath(parentDir, image.FilePath);
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
                .Select(kvp => new GalleryFolderGroup { FolderPath = kvp.Key, ImageCount = kvp.Value })
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

            if (_selectedTabIndex == 1) ApplyFavoritesSortAndFilter();
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

            if (_isFavoritesMultiSelect)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    UpdateAllFavoritesCheckBoxes();
                    UpdateFavoritesMultiSelectCount();
                });
            }
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
                Title = "新建相册",
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            var rootGrid = new Grid { ColumnSpacing = 6, Width = 520 };
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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
                                AppLogger.Error(ex, "图库收藏夹封面预览加载失败");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "图库收藏夹封面选择失败");
                }
            };

            Grid.SetColumn(coverBorder, 0);
            rootGrid.Children.Add(coverBorder);

            var rightPanel = new StackPanel { Spacing = 12 };
            var textBox = new TextBox { PlaceholderText = "相册名称", Width = 250 };
            rightPanel.Children.Add(textBox);

            var descBox = new TextBox
            {
                PlaceholderText = "相册描述（可选）",
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
                name = "新建相册";

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
            if (e.ClickedItem is Playlist favorite)
            {
                if (_isFavoritesMultiSelect)
                {
                    ToggleFavoritesSelection(favorite);
                    return;
                }
                OpenFavoriteDetail(favorite);
            }
        }

        private void FavoriteItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Playlist favorite })
            {
                e.Handled = true;
                if (_isFavoritesMultiSelect) return;
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

            var selectItem = new MenuFlyoutItem { Text = "选择", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uEA98" } };
            selectItem.Click += (_, _) => EnterMultiSelectMode(1, favorite);
            menu.Items.Add(selectItem);

            // 固定到侧边栏（图库相册）
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
                        Type = SidebarShortcutType.GalleryAlbum,
                        Title = $"图库相册：{favorite.Name}",
                        Name = favorite.Name,
                        Key = favorite.Id
                    });
            };
            menu.Items.Add(pinItem);

            var viewAllItem = new MenuFlyoutItem { Text = "查看全部", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE890" } };
            viewAllItem.Click += (_, _) =>
            {
                if (favorite.Items.Count > 0)
                {
                    var args = new ImageViewerArgs
                    {
                        Playlist = favorite.Items.ToList(),
                        StartIndex = 0
                    };
                    (App.MainWindow as MainWindow)?.OpenImageViewer(args);
                }
            };
            viewAllItem.IsEnabled = favorite.Items.Count > 0;
            menu.Items.Add(viewAllItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var editItem = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8AC" } };
            editItem.Click += async (_, _) =>
            {
                var tb = new TextBox { Text = favorite.Name, Width = 280 };
                tb.SelectAll();
                var dlg = new ContentDialog
                {
                    Title = "重命名相册",
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
                        // 相册重命名后同步侧边栏固定项名称/标题
                        MainWindow.NotifyDetailSaved(SidebarShortcutType.GalleryAlbum, favorite.Id, favorite.Name);
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
            NavigateToDetailPage(typeof(GalleryAlbumDetailPage), new GalleryAlbumDetailArgs
            {
                Favorite = favorite,
                SaveChanges = SaveFavorites
            });
        }

        private void PopulateAddToFavoriteMenu(MenuFlyoutSubItem parent, MediaItem item)
        {
            if (_allFavorites.Count == 0)
            {
                parent.Items.Add(new MenuFlyoutItem { Text = "暂无相册", IsEnabled = false });
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
                    if (_selectedTabIndex == 1) ApplyFavoritesSortAndFilter();
                };
                parent.Items.Add(favItem);
            }
        }

        #endregion

        #region 导航辅助

        private void NavigateToDetailPage(Type pageType, object parameter)
        {
            if (App.MainWindow is MainWindow mainWin)
                mainWin.NavigateMainFrame(pageType, parameter);
        }

        /// <summary>
        /// 点击「媒体库管理」按钮，打开媒体库文件夹管理弹窗（勾选展示/添加/移除文件夹）。
        /// </summary>
        private async void LibraryManageButton_Click(object sender, RoutedEventArgs e)
        {
            await MediaLibraryManageDialog.ShowAsync(this.XamlRoot, "Image");
            AppLogger.Info("[GalleryPage] 媒体库管理弹窗已关闭");
        }

        /// <summary>
        /// 媒体库文件夹勾选状态变更（媒体库管理弹窗内操作）：重新加载并按勾选过滤。
        /// </summary>
        private void MediaLibraryFolderManager_EnabledFoldersChanged(object? sender, string mediaType)
        {
            if (mediaType != "Image")
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!PageLifetimeService.IsActive(_containerGeneration))
                    return;
                LoadImagesFromCache();
            });
        }

        /// <summary>
        /// 点击「进入播放器」按钮，打开图片查看器覆盖层。
        /// </summary>
        private void EnterPlayerButton_Click(object sender, RoutedEventArgs e)
        {
            (App.MainWindow as MainWindow)?.ShowPlayerOverlay(typeof(ImageViewerPage), new ImageViewerArgs());
        }

        #endregion

        #region 多选功能

        private void EnterMultiSelectMode(int tabIndex, object? starter = null)
        {
            _multiSelectActiveTab = tabIndex;
            ToolbarGrid.Visibility = Visibility.Collapsed;
            FavoritesToolbarGrid.Visibility = Visibility.Collapsed;
            FolderToolbarGrid.Visibility = Visibility.Collapsed;
            MultiSelectToolbarGrid.Visibility = Visibility.Visible;

            switch (tabIndex)
            {
                case 0:
                    EnterImageMultiSelectMode(starter as MediaItem);
                    break;
                case 1:
                    EnterFavoritesMultiSelectMode(starter as Playlist);
                    break;
                case 2:
                    EnterFolderMultiSelectMode(starter as GalleryFolderGroup);
                    break;
            }
        }

        private void ExitMultiSelectMode()
        {
            switch (_multiSelectActiveTab)
            {
                case 0:
                    ExitImageMultiSelectMode();
                    break;
                case 1:
                    ExitFavoritesMultiSelectMode();
                    break;
                case 2:
                    ExitFolderMultiSelectMode();
                    break;
            }

            MultiSelectToolbarGrid.Visibility = Visibility.Collapsed;
            _multiSelectActiveTab = -1;

            if (_selectedTabIndex == 0)
                ToolbarGrid.Visibility = Visibility.Visible;
            else if (_selectedTabIndex == 1)
                FavoritesToolbarGrid.Visibility = Visibility.Visible;
            else if (_selectedTabIndex == 2)
                FolderToolbarGrid.Visibility = Visibility.Visible;
        }

        #region 图片多选（ItemsRepeater）
        private void EnterImageMultiSelectMode(MediaItem? starter = null)
        {
            _isImageMultiSelect = true;
            _imageMultiSelectedPaths.Clear();

            if (starter != null)
                _imageMultiSelectedPaths.Add(starter.FilePath);

            MultiSelectToggleButton.IsChecked = true;

            UpdateAllImageCheckBoxes();
            UpdateImageMultiSelectCount();
        }

        private void ExitImageMultiSelectMode()
        {
            _isImageMultiSelect = false;
            _imageMultiSelectedPaths.Clear();

            MultiSelectToggleButton.IsChecked = false;

            UpdateAllImageCheckBoxes();
            UpdateImageMultiSelectCount();
        }

        private void ToggleImageSelection(MediaItem item)
        {
            if (_imageMultiSelectedPaths.Contains(item.FilePath))
                _imageMultiSelectedPaths.Remove(item.FilePath);
            else
                _imageMultiSelectedPaths.Add(item.FilePath);

            UpdateImageMultiSelectCount();

            UpdateSingleImageCheckBox(item);
        }

        private void UpdateSingleImageCheckBox(MediaItem item)
        {
            foreach (var checkbox in FindAllItemCheckBoxesInRepeater())
            {
                if (checkbox.DataContext is MediaItem mi &&
                    string.Equals(mi.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    checkbox.IsChecked = _imageMultiSelectedPaths.Contains(item.FilePath);
                }
            }
        }

        private void UpdateAllImageCheckBoxes()
        {
            var visibility = _isImageMultiSelect ? Visibility.Visible : Visibility.Collapsed;

            foreach (var checkbox in FindAllItemCheckBoxesInRepeater())
            {
                checkbox.Visibility = visibility;
                if (_isImageMultiSelect && checkbox.DataContext is MediaItem item)
                    checkbox.IsChecked = _imageMultiSelectedPaths.Contains(item.FilePath);
            }
        }

        private List<CheckBox> FindAllItemCheckBoxesInRepeater()
        {
            var results = new List<CheckBox>();
            FindItemCheckBoxesInElement(ImageRepeater, results);
            return results;
        }

        private static void FindItemCheckBoxesInElement(DependencyObject parent, List<CheckBox> results)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is CheckBox cb && cb.Name == "ItemCheckBox")
                {
                    results.Add(cb);
                }
                FindItemCheckBoxesInElement(child, results);
            }
        }

        private void ImageRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Element is UIElement element)
            {
                var checkboxes = new List<CheckBox>();
                FindItemCheckBoxesInElement(element, checkboxes);
                foreach (var cb in checkboxes)
                {
                    cb.Visibility = _isImageMultiSelect ? Visibility.Visible : Visibility.Collapsed;
                    if (_isImageMultiSelect && cb.DataContext is MediaItem item)
                        cb.IsChecked = _imageMultiSelectedPaths.Contains(item.FilePath);
                }
            }
        }

        private void ImageRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            if (args.Element is UIElement element)
            {
                var checkboxes = new List<CheckBox>();
                FindItemCheckBoxesInElement(element, checkboxes);
                foreach (var cb in checkboxes)
                {
                    cb.Visibility = Visibility.Collapsed;
                    cb.IsChecked = false;
                }
            }
        }

        private void ImageItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                _imageMultiSelectedPaths.Add(item.FilePath);
                UpdateImageMultiSelectCount();
            }
        }

        private void ImageItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                _imageMultiSelectedPaths.Remove(item.FilePath);
                UpdateImageMultiSelectCount();
            }
        }

        private void UpdateImageMultiSelectCount()
        {
            int count = _imageMultiSelectedPaths.Count;
            int total = _filteredImages.Count;

            MultiSelectCountText.Text = total > 0
                ? $"已选择 {count} / {total} 张图片"
                : "已选择 0 张图片";

            if (SelectAllCheckBox != null)
            {
                _selectAllChanging = true;
                SelectAllCheckBox.IsChecked = count > 0 && count == total
                    ? true
                    : count == 0 ? false : null;
                _selectAllChanging = false;
            }
        }
        #endregion

        #region 文件夹多选
        private void EnterFolderMultiSelectMode(GalleryFolderGroup? starter = null)
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

            FolderMultiSelectToggleButton.IsChecked = false;

            UpdateAllFolderCheckBoxes();
            UpdateFolderMultiSelectCount();
        }

        private void ToggleFolderSelection(GalleryFolderGroup folder)
        {
            if (_folderMultiSelectedPaths.Contains(folder.FolderPath))
                _folderMultiSelectedPaths.Remove(folder.FolderPath);
            else
                _folderMultiSelectedPaths.Add(folder.FolderPath);

            UpdateFolderMultiSelectCount();

            if (FolderList.ContainerFromItem(folder) is ListViewItem container)
            {
                var cb = FindVisualChild<CheckBox>(container);
                if (cb != null)
                    cb.IsChecked = _folderMultiSelectedPaths.Contains(folder.FolderPath);
            }
            if (FolderGrid.ContainerFromItem(folder) is GridViewItem gridContainer)
            {
                var cb = FindVisualChild<CheckBox>(gridContainer);
                if (cb != null)
                    cb.IsChecked = _folderMultiSelectedPaths.Contains(folder.FolderPath);
            }
        }

        private void UpdateAllFolderCheckBoxes()
        {
            var visibility = _isFolderMultiSelect ? Visibility.Visible : Visibility.Collapsed;

            foreach (var folder in _filteredFolderGroups)
            {
                if (FolderList.ContainerFromItem(folder) is ListViewItem container)
                {
                    var cb = FindVisualChild<CheckBox>(container);
                    if (cb != null)
                    {
                        cb.Visibility = visibility;
                        if (_isFolderMultiSelect)
                            cb.IsChecked = _folderMultiSelectedPaths.Contains(folder.FolderPath);
                    }
                }
                if (FolderGrid.ContainerFromItem(folder) is GridViewItem gridContainer)
                {
                    var cb = FindVisualChild<CheckBox>(gridContainer);
                    if (cb != null)
                    {
                        cb.Visibility = visibility;
                        if (_isFolderMultiSelect)
                            cb.IsChecked = _folderMultiSelectedPaths.Contains(folder.FolderPath);
                    }
                }
            }
        }

        private void FolderItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is GalleryFolderGroup folder)
            {
                _folderMultiSelectedPaths.Add(folder.FolderPath);
                UpdateFolderMultiSelectCount();
            }
        }

        private void FolderItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is GalleryFolderGroup folder)
            {
                _folderMultiSelectedPaths.Remove(folder.FolderPath);
                UpdateFolderMultiSelectCount();
            }
        }

        private void UpdateFolderMultiSelectCount()
        {
            int count = _folderMultiSelectedPaths.Count;
            int total = _filteredFolderGroups.Count;

            MultiSelectCountText.Text = total > 0
                ? $"已选择 {count} / {total} 个文件夹"
                : "已选择 0 个文件夹";

            if (SelectAllCheckBox != null)
            {
                _selectAllChanging = true;
                SelectAllCheckBox.IsChecked = count > 0 && count == total
                    ? true
                    : count == 0 ? false : null;
                _selectAllChanging = false;
            }
        }
        #endregion

        #region 收藏多选
        private void EnterFavoritesMultiSelectMode(Playlist? starter = null)
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

            FavoritesMultiSelectToggleButton.IsChecked = false;

            UpdateAllFavoritesCheckBoxes();
            UpdateFavoritesMultiSelectCount();
        }

        private void ToggleFavoritesSelection(Playlist fav)
        {
            if (_favoritesMultiSelectedIds.Contains(fav.Id))
                _favoritesMultiSelectedIds.Remove(fav.Id);
            else
                _favoritesMultiSelectedIds.Add(fav.Id);

            UpdateFavoritesMultiSelectCount();

            if (FavoritesList.ContainerFromItem(fav) is ListViewItem container)
            {
                var cb = FindVisualChild<CheckBox>(container);
                if (cb != null)
                    cb.IsChecked = _favoritesMultiSelectedIds.Contains(fav.Id);
            }
            if (FavoritesGrid.ContainerFromItem(fav) is GridViewItem gridContainer)
            {
                var cb = FindVisualChild<CheckBox>(gridContainer);
                if (cb != null)
                    cb.IsChecked = _favoritesMultiSelectedIds.Contains(fav.Id);
            }
        }

        private void UpdateAllFavoritesCheckBoxes()
        {
            var visibility = _isFavoritesMultiSelect ? Visibility.Visible : Visibility.Collapsed;

            foreach (var fav in _filteredFavorites)
            {
                if (FavoritesList.ContainerFromItem(fav) is ListViewItem container)
                {
                    var cb = FindVisualChild<CheckBox>(container);
                    if (cb != null)
                    {
                        cb.Visibility = visibility;
                        if (_isFavoritesMultiSelect)
                            cb.IsChecked = _favoritesMultiSelectedIds.Contains(fav.Id);
                    }
                }
                if (FavoritesGrid.ContainerFromItem(fav) is GridViewItem gridContainer)
                {
                    var cb = FindVisualChild<CheckBox>(gridContainer);
                    if (cb != null)
                    {
                        cb.Visibility = visibility;
                        if (_isFavoritesMultiSelect)
                            cb.IsChecked = _favoritesMultiSelectedIds.Contains(fav.Id);
                    }
                }
            }
        }

        private void FavoriteItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is Playlist fav)
            {
                _favoritesMultiSelectedIds.Add(fav.Id);
                UpdateFavoritesMultiSelectCount();
            }
        }

        private void FavoriteItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is Playlist fav)
            {
                _favoritesMultiSelectedIds.Remove(fav.Id);
                UpdateFavoritesMultiSelectCount();
            }
        }

        private void UpdateFavoritesMultiSelectCount()
        {
            int count = _favoritesMultiSelectedIds.Count;
            int total = _filteredFavorites.Count;

            MultiSelectCountText.Text = total > 0
                ? $"已选择 {count} / {total} 个相册"
                : "已选择 0 个相册";

            if (SelectAllCheckBox != null)
            {
                _selectAllChanging = true;
                SelectAllCheckBox.IsChecked = count > 0 && count == total
                    ? true
                    : count == 0 ? false : null;
                _selectAllChanging = false;
            }
        }
        #endregion

        #region 工具栏按钮事件
        private void MultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isImageMultiSelect)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(0);
        }

        private void FavoritesMultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFavoritesMultiSelect)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(1);
        }

        private void FolderMultiSelectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFolderMultiSelect)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode(2);
        }

        private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_selectAllChanging) return;

            if (_multiSelectActiveTab == 0)
                SelectAllImages();
            else if (_multiSelectActiveTab == 1)
                SelectAllFavorites();
            else if (_multiSelectActiveTab == 2)
                SelectAllFolders();
        }

        private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_selectAllChanging) return;

            if (_multiSelectActiveTab == 0)
                DeselectAllImages();
            else if (_multiSelectActiveTab == 1)
                DeselectAllFavorites();
            else if (_multiSelectActiveTab == 2)
                DeselectAllFolders();
        }

        private void SelectAllImages()
        {
            foreach (var item in _filteredImages)
                _imageMultiSelectedPaths.Add(item.FilePath);

            UpdateAllImageCheckBoxes();
            UpdateImageMultiSelectCount();
        }

        private void DeselectAllImages()
        {
            int count = _imageMultiSelectedPaths.Count;
            int total = _filteredImages.Count;

            if (count > 0 && count < total)
            {
                _selectAllChanging = true;
                SelectAllCheckBox.IsChecked = true;
                _selectAllChanging = false;

                foreach (var item in _filteredImages)
                    _imageMultiSelectedPaths.Add(item.FilePath);

                UpdateAllImageCheckBoxes();
                UpdateImageMultiSelectCount();
                return;
            }

            _imageMultiSelectedPaths.Clear();
            UpdateAllImageCheckBoxes();
            UpdateImageMultiSelectCount();
        }

        private void SelectAllFolders()
        {
            foreach (var folder in _filteredFolderGroups)
                _folderMultiSelectedPaths.Add(folder.FolderPath);

            UpdateAllFolderCheckBoxes();
            UpdateFolderMultiSelectCount();
        }

        private void DeselectAllFolders()
        {
            int count = _folderMultiSelectedPaths.Count;
            int total = _filteredFolderGroups.Count;

            if (count > 0 && count < total)
            {
                _selectAllChanging = true;
                SelectAllCheckBox.IsChecked = true;
                _selectAllChanging = false;

                foreach (var folder in _filteredFolderGroups)
                    _folderMultiSelectedPaths.Add(folder.FolderPath);

                UpdateAllFolderCheckBoxes();
                UpdateFolderMultiSelectCount();
                return;
            }

            _folderMultiSelectedPaths.Clear();
            UpdateAllFolderCheckBoxes();
            UpdateFolderMultiSelectCount();
        }

        private void SelectAllFavorites()
        {
            foreach (var fav in _filteredFavorites)
                _favoritesMultiSelectedIds.Add(fav.Id);

            UpdateAllFavoritesCheckBoxes();
            UpdateFavoritesMultiSelectCount();
        }

        private void DeselectAllFavorites()
        {
            int count = _favoritesMultiSelectedIds.Count;
            int total = _filteredFavorites.Count;

            if (count > 0 && count < total)
            {
                _selectAllChanging = true;
                SelectAllCheckBox.IsChecked = true;
                _selectAllChanging = false;

                foreach (var fav in _filteredFavorites)
                    _favoritesMultiSelectedIds.Add(fav.Id);

                UpdateAllFavoritesCheckBoxes();
                UpdateFavoritesMultiSelectCount();
                return;
            }

            _favoritesMultiSelectedIds.Clear();
            UpdateAllFavoritesCheckBoxes();
            UpdateFavoritesMultiSelectCount();
        }

        private async void MultiSelectDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectActiveTab == 0)
                await DeleteMultiSelectImagesAsync();
            else if (_multiSelectActiveTab == 1)
                await DeleteMultiSelectFavoritesAsync();
            else if (_multiSelectActiveTab == 2)
                await DeleteMultiSelectFoldersAsync();
        }

        private async Task DeleteMultiSelectImagesAsync()
        {
            if (_imageMultiSelectedPaths.Count == 0) return;

            int count = _imageMultiSelectedPaths.Count;
            var dialog = new ContentDialog
            {
                Title = "删除确认",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将选中的 {count} 张图片移入到回收站吗？可随时还原。"
                    : $"确定要删除选中的 {count} 张图片吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };

            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            try
            {
                var itemsToDelete = _allImages
                    .Where(item => _imageMultiSelectedPaths.Contains(item.FilePath))
                    .ToList();

                foreach (var item in itemsToDelete)
                {
                    ImageThumbnailService.Remove(item.FilePath);
                    ImageThumbnailService.DeleteDiskCache(item.FilePath, ThumbnailSize);

                    if (File.Exists(item.FilePath))
                    {
                        if (App.SettingsHelper.DeleteToRecycleBin)
                            RecycleBinHelper.DeleteToRecycleBin(item.FilePath);
                        else
                            File.Delete(item.FilePath);
                    }
                    if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                        File.Delete(item.ThumbnailPath);
                }

                _allImages.RemoveAll(item => _imageMultiSelectedPaths.Contains(item.FilePath));
                MediaScanner.SaveToCache(_allImages, "Image");
                _imageMultiSelectedPaths.Clear();
                ApplySortAndFilter();
                UpdateImageMultiSelectCount();

                if (_allImages.Count == 0)
                    ExitMultiSelectMode();
            }
            catch { }
        }

        private async Task DeleteMultiSelectFoldersAsync()
        {
            if (_folderMultiSelectedPaths.Count == 0) return;

            int folderCount = _folderMultiSelectedPaths.Count;
            var imagesToDelete = _allImages
                .Where(v => _folderMultiSelectedPaths.Contains(Path.GetDirectoryName(v.FilePath) ?? ""))
                .ToList();
            int imageCount = imagesToDelete.Count;

            var dialog = new ContentDialog
            {
                Title = "删除确认",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将选中的 {folderCount} 个文件夹（含 {imageCount} 张图片）移入到回收站吗？可随时还原。"
                    : $"确定要删除选中的 {folderCount} 个文件夹（含 {imageCount} 张图片）吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };

            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            try
            {
                foreach (var image in imagesToDelete)
                {
                    ImageThumbnailService.Remove(image.FilePath);
                    ImageThumbnailService.DeleteDiskCache(image.FilePath, ThumbnailSize);

                    if (File.Exists(image.FilePath))
                    {
                        if (App.SettingsHelper.DeleteToRecycleBin)
                            RecycleBinHelper.DeleteToRecycleBin(image.FilePath);
                        else
                            File.Delete(image.FilePath);
                    }
                    if (!string.IsNullOrEmpty(image.ThumbnailPath) && File.Exists(image.ThumbnailPath))
                        File.Delete(image.ThumbnailPath);
                }

                _allImages.RemoveAll(v => imagesToDelete.Contains(v));
                MediaScanner.SaveToCache(_allImages, "Image");
                BuildFolderGroups();
                _folderMultiSelectedPaths.Clear();
                ApplySortAndFilter();
                ApplyFolderSortAndFilter();
                UpdateFolderMultiSelectCount();

                if (_allImages.Count == 0)
                    ExitMultiSelectMode();
            }
            catch { }
        }

        private async Task DeleteMultiSelectFavoritesAsync()
        {
            if (_favoritesMultiSelectedIds.Count == 0) return;

            int count = _favoritesMultiSelectedIds.Count;
            var dialog = new ContentDialog
            {
                Title = "删除确认",
                Content = $"确定要删除选中的 {count} 个相册吗？此操作不可撤销。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };

            var result = await DialogService.ShowAsync(dialog, XamlRoot);
            if (result != ContentDialogResult.Primary) return;

            _allFavorites.RemoveAll(f => _favoritesMultiSelectedIds.Contains(f.Id));
            SaveFavorites();
            _favoritesMultiSelectedIds.Clear();
            ApplyFavoritesSortAndFilter();
            UpdateFavoritesMultiSelectCount();

            if (_allFavorites.Count == 0)
                ExitMultiSelectMode();
        }

        private void MultiSelectCancelButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
        }
        #endregion

        #endregion

        #region 视觉树辅助
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
        #endregion
    }
}
