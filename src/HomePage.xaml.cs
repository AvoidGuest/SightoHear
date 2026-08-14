using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using SightoHear.Helpers;
using SightoHear.Models;
using SightoHear.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace SightoHear
{
    public sealed partial class HomePage : Page
    {
        private List<MediaItem> _allVideos = new();
        private List<MediaItem> _allImages = new();
        private List<MediaItem> _allMusic = new();
        private bool _sectionsLoaded;
        private int _reloadGeneration;
        private int _containerGeneration;

        // 窗口宽度阈值：低于此值时隐藏右侧音乐栏
        private const double NarrowWindowThreshold = 860.0;

        public HomePage()
        {
            InitializeComponent();
            Loaded += HomePage_Loaded;
            Unloaded += HomePage_Unloaded;
            // 监听窗口大小变化，自适应隐藏/显示右侧音乐栏
            RootGrid.SizeChanged += RootGrid_SizeChanged;
        }

        private void HomePage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _containerGeneration = PageLifetimeService.CurrentGeneration;
            PageLifetimeService.OnNavigatedTo("HomePage");
            MediaScanner.CacheUpdated -= MediaScanner_CacheUpdated;
            MediaScanner.CacheUpdated += MediaScanner_CacheUpdated;

            if (!_sectionsLoaded)
                _ = ReloadAllSectionsAsync();
        }

        private void HomePage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            AppLogger.Debug($"[Diag] 主页卸载: _containerGen={_containerGeneration} " +
                $"CurrentGen={PageLifetimeService.CurrentGeneration} _reloadGen={_reloadGeneration}");
            MediaScanner.CacheUpdated -= MediaScanner_CacheUpdated;
            _reloadGeneration++;

            // ★ 性能修复：释放 UI 树持有的 BitmapImage 引用。
            //   本页面为 NavigationCacheMode="Required"，离开后实例被 Frame 永久持有；
            //   若不释放 Image.Source / ItemsSource，缩略图位图将一直驻留内存，
            //   浏览多个页面后累积成百上千个位图，打开 Win2D 播放器时引发 GC 风暴导致掉帧。
            try
            {
                HeroImage.Source = null;
                RecentMusicList.ItemsSource = null;
                RecentVideoGrid.ItemsSource = null;
                RecentImageGrid.ItemsSource = null;
                RecentMusicInlineGrid.ItemsSource = null;
                _sectionsLoaded = false;
            }
            catch { /* 个别控件已卸载时忽略 */ }

            // ★ 修复：页面离开后其缩略图不再"热"，裁剪 ImageThumbnailService
            //   强引用 LRU 缓存（保留最近 192 条热数据），释放 GPU 解码显存驻留。
            ImageThumbnailService.TrimMemoryCache(192);

            PageLifetimeService.OnNavigatingAway();
        }

        /// <summary>
        /// 窗口大小变化时自适应隐藏/显示右侧音乐栏。
        /// 窗口过窄时隐藏右侧栏（主内容区已有"最近音乐"板块）。
        /// </summary>
        private void RootGrid_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            UpdateSidebarVisibility();
        }

        /// <summary>
        /// 根据窗口宽度控制"最近音乐"的显示位置：
        /// - 宽窗口（≥860px）：右侧栏显示，主内容区隐藏
        /// - 窄窗口（<860px）：主内容区显示，右侧栏隐藏
        /// </summary>
        private void UpdateSidebarVisibility()
        {
            double width = RootGrid.ActualWidth;
            if (width <= 0) return;

            bool narrow = width < NarrowWindowThreshold;

            // 右侧栏
            SidebarColumn.Width = narrow ? new Microsoft.UI.Xaml.GridLength(0) : new Microsoft.UI.Xaml.GridLength(320);
            MusicSidebarScrollViewer.Visibility = narrow
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;

            // 主内容区内的内联音乐板块
            RecentMusicInlineCard.Visibility = narrow
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        private void MediaScanner_CacheUpdated(object? sender, string mediaType)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                // ★ 回调是 async void，内部必须完整 try-catch，防止异常逃逸导致进程崩溃
                try
                {
                    // ★ 使用 dispatch 执行时的当前 generation 检查
                    if (!PageLifetimeService.IsActive(_containerGeneration)) return;
                    switch (mediaType)
                    {
                        case "Video":
                            // ★ 已有数据时跳过 CacheUpdated，避免重设 ItemsSource 导致 UI 闪烁
                            if (_allVideos.Count > 0) return;
                            _allVideos = await Task.Run(() => MediaScanner.LoadFromCache("Video"));
                            PopulateHero();
                            PopulateRecentVideos();
                            break;
                        case "Music":
                            if (_allMusic.Count > 0) return;
                            _allMusic = await Task.Run(() => MediaScanner.LoadFromCache("Music"));
                            PopulateHero();
                            PopulateRecentMusic();
                            PopulateRecentMusicGrid();
                            break;
                        case "Image":
                            if (_allImages.Count > 0) return;
                            _allImages = await Task.Run(() => MediaScanner.LoadFromCache("Image"));
                            PopulateHero();
                            PopulateRecentImages();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"主页缓存更新处理失败: {ex.Message}");
                }
            });
        }

        private async Task ReloadAllSectionsAsync()
        {
            int generation = ++_reloadGeneration;

            var (music, videos, images) = await Task.Run(() =>
                (MediaScanner.LoadFromCache("Music"),
                 MediaScanner.LoadFromCache("Video"),
                 MediaScanner.LoadFromCache("Image")));

            if (generation != _reloadGeneration)
                return;

            _allMusic = music;
            _allVideos = videos;
            _allImages = images;
            _sectionsLoaded = true;

            PopulateHero();
            PopulateRecentMusic();
            PopulateRecentMusicGrid();
            PopulateRecentVideos();
            PopulateRecentImages();
            UpdateSidebarVisibility();
        }

        // 按文件本身的时间从新到旧排序：优先修改时间，回退到创建时间/扫描时间
        private static List<MediaItem> SortByScannedDesc(List<MediaItem> items, int count = 6)
        {
            return items
                .OrderByDescending(FileTimeOf)
                .Take(count)
                .ToList();
        }

        private static DateTime FileTimeOf(MediaItem item)
        {
            if (item.DateModified != default) return item.DateModified;
            if (item.DateCreated != default) return item.DateCreated;
            return item.DateScanned;
        }

        private MediaItem? GetMostRecentOpened()
        {
            DateTime videoTime = App.SettingsHelper.LastVideoTime;
            DateTime imageTime = App.SettingsHelper.LastImageTime;
            DateTime musicTime = App.SettingsHelper.LastMusicTime;

            string videoPath = App.SettingsHelper.LastVideoPath;
            string imagePath = App.SettingsHelper.LastImagePath;
            string musicPath = App.SettingsHelper.LastMusicPath;

            var candidates = new List<(DateTime Time, string Path, string Type)>();
            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
                candidates.Add((videoTime, videoPath, "Video"));
            if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                candidates.Add((imageTime, imagePath, "Image"));
            if (!string.IsNullOrWhiteSpace(musicPath) && File.Exists(musicPath))
                candidates.Add((musicTime, musicPath, "Music"));

            if (candidates.Count == 0)
                return null;

            var newest = candidates.OrderByDescending(c => c.Time).First();

            List<MediaItem> source = newest.Type switch
            {
                "Video" => _allVideos,
                "Image" => _allImages,
                "Music" => _allMusic,
                _ => new()
            };

            return source.FirstOrDefault(
                i => i.FilePath.Equals(newest.Path, StringComparison.OrdinalIgnoreCase));
        }

        private void PopulateHero()
        {
            AppLogger.Debug($"[Diag] PopulateHero: 开始 _containerGen={_containerGeneration} " +
                $"ActualTheme={ActualTheme} _sectionsLoaded={_sectionsLoaded}");
            // ★ 重置卡片背景：此时页面主题已正确解析（Loaded 之后），确保深色模式初始为黑色
            SetHeroCardBackground(HomeCardBackgroundBrush);

            var item = GetMostRecentOpened();

            if (item == null)
            {
                HeroCard.Tag = null;
                HeroPlaceholderIcon.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                HeroImage.Source = null;
                HeroTitle.Text = "暂无最近打开记录";
                HeroSubtitle.Text = "打开任何视频、图片或音乐后这里将显示预览";
                HeroTypeLabel.Text = string.Empty;
                return;
            }

            HeroCard.Tag = item;
            HeroTitle.Text = item.FileName;

            string typeLabel = item.MediaType switch
            {
                "Video" => "上次打开的视频",
                "Image" => "上次打开的图片",
                "Music" => "上次打开的音乐",
                _ => "最近打开"
            };
            HeroTypeLabel.Text = typeLabel;

            string detail = item.MediaType switch
            {
                "Video" => $"大小: {item.FileSizeText}",
                "Music" => $"{item.ArtistDisplay} · {item.AlbumDisplay}",
                "Image" => $"{item.PixelWidth} × {item.PixelHeight}",
                _ => item.FileSizeText
            };
            HeroSubtitle.Text = detail;

            LoadHeroImage(item);
        }

        private void LoadHeroImage(MediaItem item)
        {
            HeroImage.Source = null;
            HeroPlaceholderIcon.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

            string glyph = item.MediaType switch
            {
                "Video" => "",
                "Image" => "",
                "Music" => "",
                _ => ""
            };
            HeroPlaceholderIcon.Glyph = glyph;

            try
            {
                // ★ 主页大卡片直接使用原图/原封面，不走缩略图缓存
                string? sourcePath = null;

                if (item.MediaType == "Image" && File.Exists(item.FilePath))
                    sourcePath = item.FilePath;           // 图片：直接用原文件
                else if (item.MediaType == "Music")
                    sourcePath = MusicCoverService.GetOrCreateOriginal(item.FilePath); // 音乐：直接用原封面（不缩放）
                else if (item.MediaType == "Video" && File.Exists(item.FilePath))
                    sourcePath = VideoCoverService.GetOrCreateOriginal(item.FilePath); // 视频：提取高清封面帧

                if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = 1920 };
                    bitmap.ImageOpened += (s, e) =>
                    {
                        HeroPlaceholderIcon.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    };
                    bitmap.UriSource = new Uri(sourcePath);
                    HeroImage.Source = bitmap;
                }
            }
            catch { }
        }

        private void PopulateRecentMusic()
        {
            var recent = SortByScannedDesc(_allMusic.ToList(), 10);
            if (recent.Count == 0)
            {
                RecentMusicList.ItemsSource = null;
                RecentMusicList.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                RecentMusicEmptyText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                return;
            }

            RecentMusicEmptyText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            RecentMusicList.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            RecentMusicList.ItemsSource = recent;
        }

        private void PopulateRecentVideos()
        {
            var recent = SortByScannedDesc(_allVideos.ToList(), 8);
            if (recent.Count == 0)
            {
                AppLogger.Debug($"[Diag] PopulateRecentVideos: 无视频, _containerGen={_containerGeneration} CurrentGen={PageLifetimeService.CurrentGeneration}");
                RecentVideoGrid.ItemsSource = null;
                RecentVideoGrid.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                RecentVideoEmptyText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                return;
            }

            RecentVideoEmptyText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            RecentVideoGrid.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            AppLogger.Debug($"[Diag] PopulateRecentVideos: {recent.Count}项");
            // ★ 先确保事件已订阅，再设置 ItemsSource，避免容器生成时漏掉 ContainerContentChanging
            RecentVideoGrid.ContainerContentChanging -= VideoGrid_ContainerContentChanging;
            RecentVideoGrid.ContainerContentChanging += VideoGrid_ContainerContentChanging;
            RecentVideoGrid.ItemsSource = recent;
        }

        private void PopulateRecentImages()
        {
            var recent = SortByScannedDesc(_allImages.ToList(), 8);
            if (recent.Count == 0)
            {
                AppLogger.Debug($"[Diag] PopulateRecentImages: 无图片, _containerGen={_containerGeneration} CurrentGen={PageLifetimeService.CurrentGeneration}");
                RecentImageGrid.ItemsSource = null;
                RecentImageGrid.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                RecentImageEmptyText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                return;
            }

            RecentImageEmptyText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            RecentImageGrid.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            AppLogger.Debug($"[Diag] PopulateRecentImages: {recent.Count}项");
            // ★ 先确保事件已订阅，再设置 ItemsSource，避免容器生成时漏掉 ContainerContentChanging
            RecentImageGrid.ContainerContentChanging -= ImageGrid_ContainerContentChanging;
            RecentImageGrid.ContainerContentChanging += ImageGrid_ContainerContentChanging;
            RecentImageGrid.ItemsSource = recent;
        }

        /// <summary>
        /// 填充"最近音乐"缩略图卡片板块（主内容区内内联版本，窄窗口时显示）。
        /// </summary>
        private void PopulateRecentMusicGrid()
        {
            var recent = SortByScannedDesc(_allMusic.ToList(), 8);
            if (recent.Count == 0)
            {
                RecentMusicInlineGrid.ItemsSource = null;
                RecentMusicInlineGrid.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                RecentMusicInlineEmptyText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                return;
            }

            RecentMusicInlineEmptyText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            RecentMusicInlineGrid.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            RecentMusicInlineGrid.ContainerContentChanging -= MusicGrid_ContainerContentChanging;
            RecentMusicInlineGrid.ContainerContentChanging += MusicGrid_ContainerContentChanging;
            RecentMusicInlineGrid.ItemsSource = recent;
        }

        private void VideoGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            // ★ 封面加载与页面活跃状态完全解耦：不检查 PageLifetimeService.IsActive，
            //   避免因其他页面切换导致的 generation 变化而跳过封面加载。
            //   Unloaded 时 ItemsSource 已被清空、容器被回收（InRecycleQueue 分支会清空图片），
            //   因此无需担心页面离开后此处误加载。
            var itemName = args.Item is MediaItem mi ? mi.FileName : "null";
            if (args.InRecycleQueue)
            {
                AppLogger.Debug($"[Diag] VideoGrid_CCC: 回收 item={itemName}");
                if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root &&
                    root.FindName("ThumbImage") is Image img)
                {
                    img.Source = null;
                }
                return;
            }

            AppLogger.Debug($"[Diag] VideoGrid_CCC: Phase={args.Phase} item={itemName} CurrentGen={PageLifetimeService.CurrentGeneration}");
            if (args.Phase == 0)
            {
                args.RegisterUpdateCallback(1, LoadVideoThumbnail);
                args.Handled = true;
            }
        }

        private void LoadVideoThumbnail(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not MediaItem item) return;
            if (args.ItemContainer.ContentTemplateRoot is not FrameworkElement root) return;
            if (root.FindName("ThumbImage") is not Image image) return;

            try
            {
                if (!string.IsNullOrEmpty(item.ThumbnailPath))
                {
                    // 使用 ImageThumbnailService 获取 LRU 内存缓存/磁盘缓存的缩略图
                    var bitmap = ImageThumbnailService.GetOrCreate(item.ThumbnailPath);
                    if (bitmap != null)
                    {
                        image.Source = bitmap;
                        AppLogger.Debug($"[Diag] LoadVideoThumbnail: 成功 item={item.FileName} thumb={item.ThumbnailPath}");
                    }
                    else
                    {
                        AppLogger.Debug($"[Diag] LoadVideoThumbnail: GetOrCreate返回null item={item.FileName} thumb={item.ThumbnailPath}");
                    }
                }
                else
                {
                    AppLogger.Debug($"[Diag] LoadVideoThumbnail: ThumbnailPath为空 item={item.FileName}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"[Diag] LoadVideoThumbnail: 异常 item={item.FileName} ex={ex.Message}");
            }
        }

        private void ImageGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            // ★ 封面加载与页面活跃状态完全解耦：不检查 PageLifetimeService.IsActive，
            //   避免因其他页面切换导致的 generation 变化而跳过封面加载。
            //   Unloaded 时 ItemsSource 已被清空、容器被回收（InRecycleQueue 分支会清空图片），
            //   因此无需担心页面离开后此处误加载。
            var itemName = args.Item is MediaItem mi ? mi.FileName : "null";
            if (args.InRecycleQueue)
            {
                AppLogger.Debug($"[Diag] ImageGrid_CCC: 回收 item={itemName}");
                if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root &&
                    root.FindName("ThumbImage") is Image img)
                {
                    img.Source = null;
                }
                return;
            }

            AppLogger.Debug($"[Diag] ImageGrid_CCC: Phase={args.Phase} item={itemName} CurrentGen={PageLifetimeService.CurrentGeneration}");
            if (args.Phase == 0)
            {
                args.RegisterUpdateCallback(1, LoadImageThumbnail);
                args.Handled = true;
            }
        }

        private void LoadImageThumbnail(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not MediaItem item) return;
            if (args.ItemContainer.ContentTemplateRoot is not FrameworkElement root) return;
            if (root.FindName("ThumbImage") is not Image image) return;

            try
            {
                // 优先使用预提取的缩略图路径，无则用原图
                string sourcePath = !string.IsNullOrEmpty(item.ThumbnailPath)
                    ? item.ThumbnailPath
                    : item.FilePath;

                if (File.Exists(sourcePath))
                {
                    // 使用 ImageThumbnailService 获取 LRU 内存缓存/磁盘缓存的缩略图
                    var bitmap = ImageThumbnailService.GetOrCreate(sourcePath);
                    if (bitmap != null)
                    {
                        image.Source = bitmap;
                        AppLogger.Debug($"[Diag] LoadImageThumbnail: 成功 item={item.FileName} source={sourcePath}");
                    }
                    else
                    {
                        AppLogger.Debug($"[Diag] LoadImageThumbnail: GetOrCreate返回null item={item.FileName} source={sourcePath}");
                    }
                }
                else
                {
                    AppLogger.Debug($"[Diag] LoadImageThumbnail: 文件不存在 item={item.FileName} source={sourcePath}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"[Diag] LoadImageThumbnail: 异常 item={item.FileName} ex={ex.Message}");
            }
        }

        /// <summary>
        /// 音乐缩略图卡片的容器内容变化回调：延迟加载音乐封面。
        /// </summary>
        private void MusicGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            var itemName = args.Item is MediaItem mi ? mi.FileName : "null";
            if (args.InRecycleQueue)
            {
                if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root &&
                    root.FindName("MusicThumbImage") is Image img)
                {
                    img.Source = null;
                }
                return;
            }

            if (args.Phase == 0)
            {
                args.RegisterUpdateCallback(1, LoadMusicThumbnail);
                args.Handled = true;
            }
        }

        /// <summary>
        /// 加载音乐封面缩略图。
        /// </summary>
        private void LoadMusicThumbnail(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not MediaItem item) return;
            if (args.ItemContainer.ContentTemplateRoot is not FrameworkElement root) return;
            if (root.FindName("MusicThumbImage") is not Image image) return;

            try
            {
                string? coverPath = MusicCoverService.GetOrCreate(item.FilePath);
                if (string.IsNullOrWhiteSpace(coverPath) && !string.IsNullOrEmpty(item.ThumbnailPath))
                    coverPath = item.ThumbnailPath;

                if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
                {
                    var bitmap = ImageThumbnailService.GetOrCreate(coverPath);
                    if (bitmap != null)
                        image.Source = bitmap;
                }
            }
            catch { }
        }

        private Brush GetThemeResourceBrush(string key)
        {
            // ★ 修复：ActualTheme 在页面首次加载前可能为 Default，
            //    此时 App.Current.RequestedTheme 始终为 Light（从未被设置），
            //    改用 XamlRoot.Content（即 MainWindow 根元素）的 ActualTheme 判断
            var isDark = ActualTheme == ElementTheme.Dark
                || (ActualTheme == ElementTheme.Default
                    && XamlRoot?.Content is FrameworkElement root
                    && root.ActualTheme == ElementTheme.Dark);
            var dictKey = isDark ? "Dark" : "Light";
            if (Resources.ThemeDictionaries.TryGetValue(dictKey, out var dict)
                && dict is ResourceDictionary rd
                && rd.TryGetValue(key, out var value)
                && value is Brush brush)
            {
                var colorStr = brush is SolidColorBrush scb ? scb.Color.ToString() : "non-solid";
                AppLogger.Debug($"[Diag] GetThemeResourceBrush: key={key} isDark={isDark} dict={dictKey} " +
                    $"ActualTheme={ActualTheme} color={colorStr}");
                return brush;
            }
            // fallback — 理论上不应走到这里
            AppLogger.Debug($"[Diag] GetThemeResourceBrush: 回退Transparent key={key} isDark={isDark} dict={dictKey}");
            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private Brush HomeCardBackgroundBrush =>
            GetThemeResourceBrush("HomeInteractiveCardBackgroundBrush");

        private Brush HomeCardPointerOverBackgroundBrush =>
            GetThemeResourceBrush("HomeInteractiveCardPointerOverBackgroundBrush");

        private Brush HomeCardPressedBackgroundBrush =>
            GetThemeResourceBrush("HomeInteractiveCardPressedBackgroundBrush");

        private static bool IsPointerOver(FrameworkElement element, PointerRoutedEventArgs e)
        {
            var position = e.GetCurrentPoint(element).Position;
            return position.X >= 0 && position.X <= element.ActualWidth &&
                   position.Y >= 0 && position.Y <= element.ActualHeight;
        }

        private void SetHeroCardBackground(Brush brush)
        {
            var colorStr = brush is SolidColorBrush scb ? scb.Color.ToString() : "non-solid";
            AppLogger.Debug($"[Diag] SetHeroCardBackground: color={colorStr}");
            HeroCard.Background = brush;
            HeroInfoPanel.Background = brush;
        }

        private void AnimateScale(ScaleTransform scale, double value, double seconds = 0.22)
        {
            var scaleX = new DoubleAnimation
            {
                To = value,
                Duration = TimeSpan.FromSeconds(seconds),
                EnableDependentAnimation = true
            };
            var scaleY = new DoubleAnimation
            {
                To = value,
                Duration = TimeSpan.FromSeconds(seconds),
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(scaleX, scale);
            Storyboard.SetTargetProperty(scaleX, "ScaleX");
            Storyboard.SetTarget(scaleY, scale);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");

            var storyboard = new Storyboard();
            storyboard.Children.Add(scaleX);
            storyboard.Children.Add(scaleY);
            storyboard.Begin();
        }

        private void HeroCard_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            SetHeroCardBackground(HomeCardPointerOverBackgroundBrush);
            AnimateScale(HeroImageScale, 1.04, 0.32);
        }

        private void HeroCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            SetHeroCardBackground(HomeCardBackgroundBrush);
            AnimateScale(HeroImageScale, 1, 0.24);
        }

        private void HeroCard_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // PointerExited 在 WinUI 中不可靠，用 PointerMoved 兜底检测是否已离开元素
            if (!IsPointerOver(HeroCard, e))
            {
                SetHeroCardBackground(HomeCardBackgroundBrush);
                AnimateScale(HeroImageScale, 1, 0.24);
            }
        }

        private void HeroCard_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            SetHeroCardBackground(HomeCardPressedBackgroundBrush);
        }

        private void HeroCard_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            SetHeroCardBackground(IsPointerOver(HeroCard, e) ? HomeCardPointerOverBackgroundBrush : HomeCardBackgroundBrush);
        }

        private void HeroCard_PointerCanceled(object sender, PointerRoutedEventArgs _)
        {
            SetHeroCardBackground(HomeCardBackgroundBrush);
        }

        private void RecentMusicItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
                card.Background = HomeCardPointerOverBackgroundBrush;
        }

        private void RecentMusicItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
                card.Background = HomeCardBackgroundBrush;
        }

        private void RecentMusicItem_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // PointerExited 在 WinUI 中不可靠，用 PointerMoved 兜底检测是否已离开元素
            if (sender is Border card && !IsPointerOver(card, e))
                card.Background = HomeCardBackgroundBrush;
        }

        private void RecentMusicItem_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
                card.Background = HomeCardPressedBackgroundBrush;
        }

        private void RecentMusicItem_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
                card.Background = IsPointerOver(card, e) ? HomeCardPointerOverBackgroundBrush : HomeCardBackgroundBrush;
        }

        private void RecentMusicItem_PointerCanceled(object sender, PointerRoutedEventArgs _)
        {
            if (sender is Border card)
                card.Background = HomeCardBackgroundBrush;
        }

        private void OpenVideo(MediaItem item)
        {
            int index = _allVideos.FindIndex(
                m => m.FilePath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase));
            (App.MainWindow as MainWindow)?.ShowPlayerOverlay(typeof(VideoPlayerPage), new VideoPlayerArgs
            {
                Playlist = _allVideos.ToList(),
                StartIndex = Math.Max(0, index)
            });
        }

        private void OpenImage(MediaItem item)
        {
            int index = _allImages.FindIndex(
                m => m.FilePath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase));
            (App.MainWindow as MainWindow)?.OpenImageViewer(new ImageViewerArgs
            {
                Playlist = _allImages.ToList(),
                StartIndex = Math.Max(0, index)
            });
        }

        private void OpenMusic(MediaItem item)
        {
            var queue = _allMusic.ToList();
            int index = queue.FindIndex(
                m => m.FilePath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase));

            bool isSameFile = string.Equals(
                App.MusicPlayback.CurrentItem?.FilePath,
                item.FilePath,
                StringComparison.OrdinalIgnoreCase);

            if (!isSameFile)
                _ = App.MusicPlayback.PlayAsync(item, queue);

            (App.MainWindow as MainWindow)?.ShowPlayerOverlay(typeof(MusicPlayerPage), new MusicPlayerArgs
            {
                CurrentItem = item,
                Playlist = queue,
                CurrentIndex = Math.Max(0, index)
            });
        }

        private void HeroCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (HeroCard.Tag is not MediaItem item || !File.Exists(item.FilePath))
                return;

            switch (item.MediaType)
            {
                case "Video":
                    OpenVideo(item);
                    break;
                case "Image":
                    OpenImage(item);
                    break;
                case "Music":
                    OpenMusic(item);
                    break;
            }
        }

        private void RecentMusicList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MediaItem item || !File.Exists(item.FilePath))
                return;

            OpenMusic(item);
        }

        private void RecentVideoGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MediaItem item || !File.Exists(item.FilePath))
                return;

            OpenVideo(item);
        }

        private void RecentImageGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MediaItem item || !File.Exists(item.FilePath))
                return;

            OpenImage(item);
        }

        /// <summary>
        /// "最近音乐"缩略图卡片点击事件 → 播放音乐并打开播放器。
        /// </summary>
        private void RecentMusicGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MediaItem item || !File.Exists(item.FilePath))
                return;

            OpenMusic(item);
        }

        /// <summary>
        /// "最近音乐"缩略图卡片右键菜单。
        /// </summary>
        private void RecentMusicThumbnail_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: MediaItem item } element) return;
            e.Handled = true;
            ShowMusicContextMenu(element, item, e.GetPosition(element));
        }

        private void ViewAllMusicButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(MusicPage));
        }

        private void ViewAllVideosButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VideoPage));
        }

        private void ViewAllImagesButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(GalleryPage));
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var rootTheme = XamlRoot?.Content is FrameworkElement rf ? rf.ActualTheme.ToString() : "none";
            var bgColor = "none";
            if (HeroCard.Background is SolidColorBrush scb) bgColor = scb.Color.ToString();
            AppLogger.Debug($"[Diag] OnNavigatedTo: ActualTheme={ActualTheme} RootActualTheme={rootTheme} " +
                $"AppTheme={App.Current.RequestedTheme} " +
                $"_containerGen={_containerGeneration} CurrentGen={PageLifetimeService.CurrentGeneration} " +
                $"_sectionsLoaded={_sectionsLoaded} HeroCard.Bg={bgColor}");

            // 导航返回后重置卡片到正常状态（离开时 PointerExited 不会触发）
            SetHeroCardBackground(HomeCardBackgroundBrush);

            // 首次进入时由 Loaded 触发；后续每次返回再从缓存刷新一次，命中内存快照几乎无开销。
            if (_sectionsLoaded)
                _ = ReloadAllSectionsAsync();
        }

        /// <summary>
        /// 播放器覆盖层关闭后调用：主页一直存活在 ContentFrame 中，覆盖层退出不会触发
        /// OnNavigatedTo，因此需要主动刷新一次，让"上次打开"大卡片及最近板块即时更新。
        /// </summary>
        public void RefreshAfterPlayerOverlayClosed()
        {
            if (_sectionsLoaded)
                _ = ReloadAllSectionsAsync();
        }

        protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);
            AppLogger.Info("离开主页");
        }

        // ================== 右键菜单 ==================

        private void RecentMusic_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: MediaItem item } element) return;
            e.Handled = true;
            ShowMusicContextMenu(element, item, e.GetPosition(element));
        }

        private void RecentThumbnail_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: MediaItem item } element) return;
            e.Handled = true;

            if (item.MediaType == "Video")
                ShowVideoContextMenu(element, item, e.GetPosition(element));
            else if (item.MediaType == "Image")
                ShowImageContextMenu(element, item, e.GetPosition(element));
        }

        // “上次打开”大卡片：根据文件类型弹出对应的右键菜单
        private void HeroCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (HeroCard.Tag is not MediaItem item) return;
            e.Handled = true;

            var position = e.GetPosition(HeroCard);
            switch (item.MediaType)
            {
                case "Video":
                    ShowVideoContextMenu(HeroCard, item, position);
                    break;
                case "Image":
                    ShowImageContextMenu(HeroCard, item, position);
                    break;
                case "Music":
                    ShowMusicContextMenu(HeroCard, item, position);
                    break;
            }
        }

        private void ShowMusicContextMenu(FrameworkElement element, MediaItem item, Windows.Foundation.Point position)
        {
            var menu = new MenuFlyout();

            var playItem = new MenuFlyoutItem { Text = "播放", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            playItem.Click += (_, _) => OpenMusic(item);
            menu.Items.Add(playItem);

            var openWith = new MenuFlyoutItem { Text = "使用其他应用打开", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            openWith.Click += (_, _) => _ = OpenWithExternalAsync(item);
            menu.Items.Add(openWith);

            var location = new MenuFlyoutItem { Text = "打开文件所在位置", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            location.Click += (_, _) => OpenFileLocation(item);
            menu.Items.Add(location);

            menu.Items.Add(new MenuFlyoutSeparator());

            var properties = new MenuFlyoutItem { Text = "属性", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            properties.Click += (_, _) => _ = ShowPropertiesAsync(item);
            menu.Items.Add(properties);

            menu.ShowAt(element, position);
        }

        private void ShowVideoContextMenu(FrameworkElement element, MediaItem item, Windows.Foundation.Point position)
        {
            var menu = new MenuFlyout();

            var playItem = new MenuFlyoutItem { Text = "播放", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            playItem.Click += (_, _) => OpenVideo(item);
            menu.Items.Add(playItem);

            var openWith = new MenuFlyoutItem { Text = "使用其他应用打开", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            openWith.Click += (_, _) => _ = OpenWithExternalAsync(item);
            menu.Items.Add(openWith);

            var location = new MenuFlyoutItem { Text = "打开文件所在位置", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            location.Click += (_, _) => OpenFileLocation(item);
            menu.Items.Add(location);

            menu.Items.Add(new MenuFlyoutSeparator());

            var delete = new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            delete.Click += (_, _) => _ = DeleteMediaAsync(item, "Video");
            menu.Items.Add(delete);

            var rename = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            rename.Click += (_, _) => _ = RenameMediaAsync(item, "Video");
            menu.Items.Add(rename);

            menu.Items.Add(new MenuFlyoutSeparator());

            var properties = new MenuFlyoutItem { Text = "属性", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            properties.Click += (_, _) => _ = ShowPropertiesAsync(item);
            menu.Items.Add(properties);

            menu.ShowAt(element, position);
        }

        private void ShowImageContextMenu(FrameworkElement element, MediaItem item, Windows.Foundation.Point position)
        {
            var menu = new MenuFlyout();

            var viewItem = new MenuFlyoutItem { Text = "查看", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            viewItem.Click += (_, _) => OpenImage(item);
            menu.Items.Add(viewItem);

            var openWith = new MenuFlyoutItem { Text = "使用其他应用打开", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            openWith.Click += (_, _) => _ = OpenWithExternalAsync(item);
            menu.Items.Add(openWith);

            var location = new MenuFlyoutItem { Text = "打开文件所在位置", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            location.Click += (_, _) => OpenFileLocation(item);
            menu.Items.Add(location);

            menu.Items.Add(new MenuFlyoutSeparator());

            var delete = new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            delete.Click += (_, _) => _ = DeleteMediaAsync(item, "Image");
            menu.Items.Add(delete);

            var rename = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            rename.Click += (_, _) => _ = RenameMediaAsync(item, "Image");
            menu.Items.Add(rename);

            menu.Items.Add(new MenuFlyoutSeparator());

            var properties = new MenuFlyoutItem { Text = "属性", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" } };
            properties.Click += (_, _) => _ = ShowPropertiesAsync(item);
            menu.Items.Add(properties);

            menu.ShowAt(element, position);
        }

        // ================== 菜单动作 ==================

        private static async Task OpenWithExternalAsync(MediaItem item)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                var options = new LauncherOptions { DisplayApplicationPicker = true };
                await Launcher.LaunchFileAsync(file, options);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "打开方式");
            }
        }

        private static void OpenFileLocation(MediaItem item)
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

        private async Task DeleteMediaAsync(MediaItem item, string mediaType)
        {
            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将 \"{item.FileName}\" 移入到回收站吗？可随时还原。"
                    : $"确定要删除本地磁盘文件 \"{item.FileName}\" 吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            try
            {
                if (mediaType == "Image")
                    ImageThumbnailService.Remove(item.FilePath);

                // 根据「删除文件时移入回收站」设置决定删除方式
                if (File.Exists(item.FilePath))
                {
                    if (App.SettingsHelper.DeleteToRecycleBin)
                        RecycleBinHelper.DeleteToRecycleBin(item.FilePath);
                    else
                        File.Delete(item.FilePath);
                }
                if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                    File.Delete(item.ThumbnailPath);

                RemoveFromCache(item, mediaType);
                RefreshSection(mediaType);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "删除文件");
            }
        }

        private async Task RenameMediaAsync(MediaItem item, string mediaType)
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
            if (result != ContentDialogResult.Primary) return;

            var newName = textBox.Text.Trim();
            if (string.IsNullOrEmpty(newName) || newName == item.FileName) return;

            try
            {
                var dir = Path.GetDirectoryName(item.FilePath);
                if (string.IsNullOrEmpty(dir)) return;
                var ext = Path.GetExtension(item.FilePath);
                var newPath = Path.Combine(dir, newName + ext);

                if (mediaType == "Image")
                    ImageThumbnailService.Remove(item.FilePath);

                File.Move(item.FilePath, newPath);
                item.FilePath = newPath;
                item.FileName = newName;
                item.Title = newName;

                MediaScanner.SaveToCache(GetListFor(mediaType), mediaType);
                RefreshSection(mediaType);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "重命名文件");
            }
        }

        private async Task ShowPropertiesAsync(MediaItem item)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "名称: " + item.FileName, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "类型: " + item.MediaType, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "路径: " + item.FilePath, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "大小: " + item.FileSizeText, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "修改日期: " + item.DateModified.ToString("yyyy-MM-dd HH:mm:ss"), TextWrapping = TextWrapping.Wrap });
            if (item.Duration.HasValue)
                panel.Children.Add(new TextBlock { Text = "时长: " + item.Duration.Value.ToString("hh\\:mm\\:ss"), TextWrapping = TextWrapping.Wrap });

            var dialog = new ContentDialog
            {
                Title = "属性",
                Content = panel,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await DialogService.ShowAsync(dialog, XamlRoot);
        }

        private List<MediaItem> GetListFor(string mediaType) => mediaType switch
        {
            "Video" => _allVideos,
            "Image" => _allImages,
            "Music" => _allMusic,
            _ => new List<MediaItem>()
        };

        private void RemoveFromCache(MediaItem item, string mediaType)
        {
            var list = GetListFor(mediaType);
            list.Remove(item);
            MediaScanner.SaveToCache(list, mediaType);
        }

        private void RefreshSection(string mediaType)
        {
            switch (mediaType)
            {
                case "Video": PopulateRecentVideos(); break;
                case "Image": PopulateRecentImages(); break;
                case "Music":
                    PopulateRecentMusic();
                    PopulateRecentMusicGrid();
                    break;
            }
            PopulateHero();
        }
    }
}
