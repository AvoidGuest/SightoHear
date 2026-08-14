using SightoHear.Helpers;
using SightoHear.Models;
using SightoHear.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
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
    public sealed partial class AlbumDetailPage : Page
    {
        private AlbumDetailArgs? _args;
        private List<MediaItem> _songs = new();
        private List<MediaItem> _filteredSongs = new();
        private bool _isMultiSelectMode;
        private readonly HashSet<string> _multiSelectedFiles = new(StringComparer.OrdinalIgnoreCase);

        // 响应式布局阈值
        private const double NarrowThreshold = 600.0;
        private const double MediumThreshold = 900.0;

        public AlbumDetailPage()
        {
            InitializeComponent();
            Loaded += AlbumDetailPage_Loaded;
            SizeChanged += AlbumDetailPage_SizeChanged;
        }

        private void AlbumDetailPage_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateLayoutForCurrentWidth();
        }

        private void AlbumDetailPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLayoutForCurrentWidth();
        }

        /// <summary>
        /// 根据当前窗口宽度切换三套布局。
        /// </summary>
        private void UpdateLayoutForCurrentWidth()
        {
            double width = ActualWidth;
            if (width <= 0) return;

            bool isNarrow = width < NarrowThreshold;
            bool isMedium = width >= NarrowThreshold && width < MediumThreshold;
            bool isWide = width >= MediumThreshold;

            // 切换三套顶部布局
            WideHeaderLayout.Visibility = isWide ? Visibility.Visible : Visibility.Collapsed;
            MediumHeaderLayout.Visibility = isMedium ? Visibility.Visible : Visibility.Collapsed;
            NarrowHeaderLayout.Visibility = isNarrow ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is AlbumDetailArgs args)
                _args = args;

            _songs = _args?.Songs ?? new List<MediaItem>();
            RefreshHeader();
            ApplyFilter();
            AppLogger.Info($"专辑详情页打开: {_args?.AlbumName ?? "未知专辑"} (歌曲 {_songs.Count} 首)");
        }

        private void RefreshHeader()
        {
            string name = _args?.AlbumName ?? "未知专辑";
            string artist = _args?.Artist ?? "未知艺术家";
            int songCount = _songs.Count;
            int year = _songs.FirstOrDefault()?.DateCreated.Year ?? 0;
            string statText = year > 0 ? $"{year} · {songCount} 首" : $"{songCount} 首";

            // 更新宽窗口布局
            WideAlbumNameText.Text = name;
            WideAlbumArtistText.Text = artist;
            WideAlbumStatText.Text = statText;
            WidePlayButton.IsEnabled = songCount > 0;

            // 更新中等窗口布局
            MediumAlbumNameText.Text = name;
            MediumAlbumArtistText.Text = artist;
            MediumAlbumStatText.Text = statText;
            MediumPlayButton.IsEnabled = songCount > 0;

            // 更新窄窗口布局
            NarrowAlbumNameText.Text = name;
            NarrowAlbumArtistText.Text = artist;
            NarrowAlbumStatText.Text = statText;
            NarrowPlayButton.IsEnabled = songCount > 0;

            // 更新封面图片
            string coverPath = _songs.FirstOrDefault()?.FilePath ?? string.Empty;
            UpdateCoverImage(coverPath);
        }

        /// <summary>
        /// 更新所有布局中的封面图片
        /// </summary>
        private void UpdateCoverImage(string coverPath)
        {
            Microsoft.UI.Xaml.Media.Imaging.BitmapImage? bitmap = null;

            if (!string.IsNullOrWhiteSpace(coverPath))
            {
                bool isMusicFile = Path.GetExtension(coverPath).ToLowerInvariant() is ".mp3" or ".flac" or ".wav" or ".aac" or ".m4a" or ".ogg" or ".wma" or ".opus";
                string imagePath = isMusicFile
                    ? MusicCoverService.GetOrCreate(coverPath)
                    : coverPath;

                if (isMusicFile && string.IsNullOrWhiteSpace(imagePath))
                    imagePath = MusicCoverService.GetOrCreateOriginal(coverPath);

                if (!string.IsNullOrWhiteSpace(imagePath))
                    bitmap = ImageThumbnailService.GetOrCreate(imagePath);
            }

            WideAlbumCoverImage.Source = bitmap;
            MediumAlbumCoverImage.Source = bitmap;
            NarrowAlbumCoverImage.Source = bitmap;
        }

        private void ApplyFilter()
        {
            string searchText = GetActiveSearchText();
            IEnumerable<MediaItem> query = _songs;

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                query = query.Where(item =>
                    item.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    item.Artist.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    item.FileName.Contains(searchText, StringComparison.OrdinalIgnoreCase));
            }

            _filteredSongs = query.OrderBy(m => m.TrackNumber).ToList();
            RefreshSongList();
        }

        /// <summary>
        /// 获取当前活动的搜索框文本
        /// </summary>
        private string GetActiveSearchText()
        {
            double width = ActualWidth;
            if (width < NarrowThreshold)
                return NarrowSongSearchBox.Text?.Trim() ?? string.Empty;
            if (width < MediumThreshold)
                return MediumSongSearchBox.Text?.Trim() ?? string.Empty;
            return WideSongSearchBox.Text?.Trim() ?? string.Empty;
        }

        private void RefreshSongList()
        {
            bool resultEmpty = _filteredSongs.Count == 0;
            bool srcEmpty = _songs.Count == 0;

            ListHeader.Visibility = resultEmpty ? Visibility.Collapsed : Visibility.Visible;
            SongList.Visibility = resultEmpty ? Visibility.Collapsed : Visibility.Visible;
            EmptyStateText.Visibility = resultEmpty ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = srcEmpty
                ? "该专辑没有歌曲"
                : "没有找到匹配的歌曲";
            SongList.ItemsSource = resultEmpty ? null : _filteredSongs;
        }

        private void SongSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyFilter();
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_filteredSongs.Count == 0)
                return;
            await PlaySongAsync(_filteredSongs[0]);
        }

        private async void SongList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MediaItem clickedItem)
                return;

            if (_isMultiSelectMode)
            {
                ToggleItemSelection(clickedItem);
                return;
            }

            if (App.SettingsHelper.MusicFileOpenMode != 0)
                return;
            await PlaySongAsync(clickedItem);
        }

        private async void SongItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: MediaItem tappedItem })
                return;

            if (_isMultiSelectMode)
            {
                // 双击卡片时 ListViewBase 会触发两次 ItemClick（第二次延迟到双击窗口结束后），
                // 事件序列为 ItemClick#1(立即) → DoubleTapped → ItemClick#2(延迟)，
                // 若此处再 Toggle 将产生 3 次切换（勾选→取消→勾选），第二次点击的取消操作会被吞掉。
                // 修复：双击只保留两次 ItemClick 的切换，此处仅拦截手势，不再切换。
                e.Handled = true;
                return;
            }

            if (App.SettingsHelper.MusicFileOpenMode != 1)
                return;

            e.Handled = true;
            await PlaySongAsync(tappedItem);
        }

        private void SongItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: MediaItem item } element)
                return;

            e.Handled = true;
            var menu = new MenuFlyout();

            var playItem = new MenuFlyoutItem
            {
                Text = "播放",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            playItem.Click += async (_, _) => await PlaySongAsync(item);
            menu.Items.Add(playItem);

            var locationItem = new MenuFlyoutItem
            {
                Text = "打开文件所在位置",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            locationItem.Click += (_, _) => OpenFileLocation(item);
            menu.Items.Add(locationItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var selectItem = new MenuFlyoutItem
            {
                Text = "选择",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE73E" }
            };
            selectItem.Click += (_, _) =>
            {
                _isMultiSelectMode = true;
                SyncMultiSelectToggles(true);
                MultiSelectToolbar.Visibility = Visibility.Visible;
                ToggleItemSelection(item);
            };
            menu.Items.Add(selectItem);

            var propertiesItem = new MenuFlyoutItem
            {
                Text = "属性",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            propertiesItem.Click += async (_, _) => await ShowPropertiesAsync(item);
            menu.Items.Add(propertiesItem);

            menu.ShowAt(element, e.GetPosition(element));
        }

        #region 多选
        private void ToggleItemSelection(MediaItem item)
        {
            if (!_multiSelectedFiles.Remove(item.FilePath))
                _multiSelectedFiles.Add(item.FilePath);
            UpdateSongListCheckBoxes();
            UpdateMultiSelectUI();
        }

        private void UpdateSongListCheckBoxes()
        {
            if (SongList?.Items == null) return;
            foreach (var container in SongList.Items)
            {
                if (SongList.ContainerFromItem(container) is ListViewItem lvi &&
                    FindDescendant<CheckBox>(lvi) is CheckBox cb &&
                    container is MediaItem item)
                {
                    cb.Visibility = _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
                    cb.IsChecked = _multiSelectedFiles.Contains(item.FilePath);
                }
            }
        }

        private void UpdateMultiSelectUI()
        {
            int count = _multiSelectedFiles.Count;
            SelectedCountText.Text = $"已选择 {count} 项";
            SelectAllCheckBox.IsChecked = count == _filteredSongs.Count && count > 0;
            MultiSelectPlayAllButton.IsEnabled = count > 0;
        }

        private void EnterMultiSelectMode()
        {
            _isMultiSelectMode = true;
            _multiSelectedFiles.Clear();
            SyncMultiSelectToggles(true);
            MultiSelectToolbar.Visibility = Visibility.Visible;
            UpdateMultiSelectUI();
            UpdateSongListCheckBoxes();
        }

        private void ExitMultiSelectMode()
        {
            _isMultiSelectMode = false;
            _multiSelectedFiles.Clear();
            SyncMultiSelectToggles(false);
            MultiSelectToolbar.Visibility = Visibility.Collapsed;
            UpdateSongListCheckBoxes();
        }

        private void MultiSelectToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isMultiSelectMode)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode();
        }

        private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool selectAll = SelectAllCheckBox.IsChecked == true;
            _multiSelectedFiles.Clear();
            if (selectAll)
            {
                foreach (var item in _filteredSongs)
                    _multiSelectedFiles.Add(item.FilePath);
            }
            UpdateSongListCheckBoxes();
            UpdateMultiSelectUI();
        }

        private async void MultiSelectPlayAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectedFiles.Count == 0) return;

            // 播放选中：仅将选中的歌曲加入播放队列，而非整个列表
            var songs = _filteredSongs
                .Where(s => _multiSelectedFiles.Contains(s.FilePath))
                .ToList();

            if (songs.Count > 0)
                await App.MusicPlayback.PlayAsync(songs[0], songs);
        }

        private void SongList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is not ListViewItem lvi || args.Item is not MediaItem item)
                return;

            var cb = FindDescendant<CheckBox>(lvi);
            if (cb != null)
            {
                cb.Visibility = _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
                cb.IsChecked = _multiSelectedFiles.Contains(item.FilePath);
            }
        }

        private void AlbumSongCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                _multiSelectedFiles.Add(item.FilePath);
                UpdateMultiSelectUI();
            }
        }

        private void AlbumSongCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                _multiSelectedFiles.Remove(item.FilePath);
                UpdateMultiSelectUI();
            }
        }
        #endregion

        private async Task PlaySongAsync(MediaItem item)
        {
            var queue = _filteredSongs.Count > 0
                ? _filteredSongs.ToList()
                : _songs.ToList();
            AppLogger.Info($"专辑详情页播放: {item.Title} (队列 {queue.Count} 首)");
            await App.MusicPlayback.PlayAsync(item, queue);
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
            catch (Exception ex) { AppLogger.Error(ex, $"打开文件位置失败: {item.FileName}"); }
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

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;

                var descendant = FindDescendant<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }

        /// <summary>
        /// 按名称查找子元素
        /// </summary>
        private static T? FindDescendantByName<T>(DependencyObject root, string name) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match && child is FrameworkElement fe && fe.Name == name)
                    return match;

                var descendant = FindDescendantByName<T>(child, name);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }

        /// <summary>
        /// 同步三套布局中的多选按钮状态
        /// </summary>
        private void SyncMultiSelectToggles(bool isChecked)
        {
            WideMultiSelectToggle.IsChecked = isChecked;
            MediumMultiSelectToggle.IsChecked = isChecked;
            NarrowMultiSelectToggle.IsChecked = isChecked;
        }
    }

    public sealed class AlbumDetailArgs
    {
        public string AlbumName { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public List<MediaItem> Songs { get; set; } = new();
    }
}
