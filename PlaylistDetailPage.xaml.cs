using SightoHear.Helpers;
using SightoHear.Models;
using SightoHear.Services;
using Microsoft.UI;
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
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace SightoHear
{
    public sealed partial class PlaylistDetailPage : Page
    {
        private Playlist? _playlist;
        private Action? _saveChanges;

        /// <summary>
        /// 调用保存委托，并在保存后通知 MainWindow 同步侧边栏固定项
        /// （歌单重命名后侧边栏名称立即更新，无论从哪个入口进入）。
        /// </summary>
        private void InvokeSaveChanges()
        {
            _saveChanges?.Invoke();
            if (_playlist != null)
                MainWindow.NotifyDetailSaved(SidebarShortcutType.MusicPlaylist, _playlist.Id, _playlist.Name);
        }
        private List<MediaItem> _filteredSongs = new();
        private bool _isMultiSelectMode;
        private bool _isNarrowLayout;
        private readonly HashSet<string> _multiSelectedFiles = new(StringComparer.OrdinalIgnoreCase);

        // 响应式布局阈值
        private const double NarrowThreshold = 600.0;
        private const double MediumThreshold = 900.0;

        public PlaylistDetailPage()
        {
            InitializeComponent();
            Loaded += PlaylistDetailPage_Loaded;
            SizeChanged += PlaylistDetailPage_SizeChanged;
        }

        private void PlaylistDetailPage_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateLayoutForCurrentWidth();
        }

        private void PlaylistDetailPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLayoutForCurrentWidth();
        }

        /// <summary>
        /// 根据当前窗口宽度切换三套布局，并更新歌曲列表列可见性。
        /// 宽窗口（≥900px）：操作按钮和搜索框在同一行靠右
        /// 中等窗口（600-900px）：搜索框+操作按钮在封面下方
        /// 窄窗口（<600px）：操作按钮在一行，搜索框在下方
        /// </summary>
        private void UpdateLayoutForCurrentWidth()
        {
            double width = ActualWidth;
            if (width <= 0) return;

            bool isNarrow = width < NarrowThreshold;
            _isNarrowLayout = isNarrow;
            bool isMedium = width >= NarrowThreshold && width < MediumThreshold;
            bool isWide = width >= MediumThreshold;

            // 切换三套顶部布局
            WideHeaderLayout.Visibility = isWide ? Visibility.Visible : Visibility.Collapsed;
            MediumHeaderLayout.Visibility = isMedium ? Visibility.Visible : Visibility.Collapsed;
            NarrowHeaderLayout.Visibility = isNarrow ? Visibility.Visible : Visibility.Collapsed;

            // 更新歌曲列表列可见性
            UpdateSongListColumns(isNarrow, isMedium);
        }

        /// <summary>
        /// 根据窗口宽度更新歌曲列表中的专辑列和时长列可见性。
        /// 窄窗口隐藏时长列，中等窗口专辑列变窄，宽窗口正常显示。
        /// </summary>
        private void UpdateSongListColumns(bool isNarrow, bool isMedium)
        {
            // 更新列表头
            if (isNarrow)
            {
                // 极窄窗口：隐藏专辑列，显示时长列
                AlbumColumn2.Width = new GridLength(0);
                DurationColumn.Width = new GridLength(72);
                AlbumColumnHeader.Visibility = Visibility.Collapsed;
                DurationColumnHeader.Visibility = Visibility.Visible;
            }
            else if (isMedium)
            {
                // 中等窗口：正常显示
                AlbumColumn2.Width = new GridLength(220);
                DurationColumn.Width = new GridLength(72);
                AlbumColumnHeader.Visibility = Visibility.Visible;
                DurationColumnHeader.Visibility = Visibility.Visible;
            }
            else
            {
                // 宽窗口：正常显示
                AlbumColumn2.Width = new GridLength(220);
                DurationColumn.Width = new GridLength(72);
                AlbumColumnHeader.Visibility = Visibility.Visible;
                DurationColumnHeader.Visibility = Visibility.Visible;
            }

            // 更新歌曲列表项模板中的列
            UpdateSongListItemsColumns(isNarrow);
        }

        /// <summary>
        /// 更新歌曲列表项模板中的列可见性
        /// </summary>
        private void UpdateSongListItemsColumns(bool isNarrow)
        {
            if (SongList?.Items == null) return;

            foreach (var container in SongList.Items)
            {
                if (SongList.ContainerFromItem(container) is ListViewItem lvi)
                {
                    var songItemGrid = FindDescendantByName<Grid>(lvi, "SongItemGrid");
                    if (songItemGrid != null)
                    {
                        // 专辑列
                        if (songItemGrid.ColumnDefinitions.Count > 2)
                        {
                            songItemGrid.ColumnDefinitions[2].Width = isNarrow
                                ? new GridLength(0)
                                : new GridLength(220);
                        }

                        // 时长文本
                        var durationText = songItemGrid.Children[3] as TextBlock;
                        if (durationText != null)
                        {
                            durationText.Visibility = Visibility.Visible;
                        }
                    }
                }
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is PlaylistDetailArgs args)
            {
                _playlist = args.Playlist;
                _saveChanges = args.SaveChanges;
            }
            else if (e.Parameter is Playlist playlist)
            {
                _playlist = playlist;
            }

            RefreshHeader();
            ApplyFilter();
        }

        private void RefreshHeader()
        {
            string name = _playlist?.Name ?? "歌单";
            int count = _playlist?.Items.Count ?? 0;
            string createdTime = _playlist == null
                ? string.Empty
                : $"创建于 {_playlist.DateCreated:yyyy/M/d}";
            string songCount = $"{count} 首歌曲";
            string description = string.IsNullOrWhiteSpace(_playlist?.Description)
                ? "暂无描述"
                : _playlist!.Description;

            // 更新宽窗口布局
            WidePlaylistNameText.Text = name;
            WideCreatedTimeText.Text = createdTime;
            WideSongCountText.Text = songCount;
            WidePlaylistDescriptionText.Text = description;
            WidePlayButton.IsEnabled = count > 0;

            // 更新中等窗口布局
            MediumPlaylistNameText.Text = name;
            MediumCreatedTimeText.Text = createdTime;
            MediumSongCountText.Text = songCount;
            MediumPlaylistDescriptionText.Text = description;
            MediumPlayButton.IsEnabled = count > 0;

            // 更新窄窗口布局
            NarrowPlaylistNameText.Text = name;
            NarrowPlaylistDescriptionText.Text = description;
            NarrowPlayButton.IsEnabled = count > 0;

            // 更新封面图片
            string coverPath = _playlist?.CoverDisplayPath ?? string.Empty;
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

            WidePlaylistCoverImage.Source = bitmap;
            MediumPlaylistCoverImage.Source = bitmap;
            NarrowPlaylistCoverImage.Source = bitmap;
        }

        private void ApplyFilter()
        {
            if (_playlist == null)
            {
                _filteredSongs = new List<MediaItem>();
                RefreshSongList();
                return;
            }

            // 获取当前活动的搜索框文本
            string searchText = GetActiveSearchText();
            IEnumerable<MediaItem> query = _playlist.Items;

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                query = query.Where(item =>
                    item.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    item.Artist.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    item.Album.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    item.FileName.Contains(searchText, StringComparison.OrdinalIgnoreCase));
            }

            _filteredSongs = query.ToList();
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
            bool playlistEmpty = _playlist == null || _playlist.Items.Count == 0;
            bool resultEmpty = _filteredSongs.Count == 0;

            ListHeader.Visibility = resultEmpty ? Visibility.Collapsed : Visibility.Visible;
            SongList.Visibility = resultEmpty ? Visibility.Collapsed : Visibility.Visible;
            EmptyStateText.Visibility = resultEmpty ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = playlistEmpty
                ? "这个歌单还没有歌曲"
                : "没有找到匹配的歌曲";
            SongList.ItemsSource = resultEmpty ? null : _filteredSongs;
        }

        private void SongSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyFilter();
        }

        private void SongSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            // 用户按回车或点击搜索按钮提交时，应用一次过滤
            ApplyFilter();
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_filteredSongs.Count == 0)
                return;

            await PlaySongAsync(_filteredSongs[0]);
        }

        private async void AddSongsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playlist == null)
                return;

            var allMusic = MusicDataCache.IsInitialized
                ? MusicDataCache.AllMusic
                : await Task.Run(() => MediaScanner.LoadFromCache("Music"));

            var existingPaths = _playlist.Items
                .Select(item => item.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var options = allMusic
                .Where(item => !existingPaths.Contains(item.FilePath))
                .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .Select(item => new AddSongOption(item))
                .ToList();

            var content = BuildAddSongsDialogContent(options);
            var dialog = new ContentDialog
            {
                Content = content,
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                Padding = new Thickness(24, 24, 4, 24)
            };

            var result = await DialogService.ShowAsync(dialog, XamlRoot);
            if (result != ContentDialogResult.Primary)
                return;

            var selectedItems = options
                .Where(option => option.IsSelected)
                .Select(option => option.Item)
                .ToList();
            if (selectedItems.Count == 0)
                return;

            _playlist.Items.AddRange(selectedItems);
            _playlist.CoverPath = string.Empty;
            InvokeSaveChanges();
            RefreshHeader();
            ApplyFilter();
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

            var removeItem = new MenuFlyoutItem
            {
                Text = "从歌单移除",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "" }
            };
            removeItem.Click += (_, _) => RemoveSong(item);
            menu.Items.Add(removeItem);

            menu.Items.Add(new MenuFlyoutSeparator());

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

        private Grid BuildAddSongsDialogContent(List<AddSongOption> options)
        {
            var root = new Grid
            {
                Width = 440,
                Height = 640,
                RowSpacing = 18
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var headerGrid = new Grid
            {
                ColumnSpacing = 12
            };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headerLeft = new StackPanel { Spacing = 4 };
            headerLeft.Children.Add(new TextBlock
            {
                Text = "添加歌曲",
                FontSize = 26,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            headerLeft.Children.Add(new TextBlock
            {
                Text = "添加音乐到歌单",
                Opacity = 0.72
            });
            headerGrid.Children.Add(headerLeft);

            var selectAllButton = new Button
            {
                Content = "全选",
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 0)
            };
            Grid.SetColumn(selectAllButton, 1);
            headerGrid.Children.Add(selectAllButton);

            root.Children.Add(headerGrid);

            if (options.Count == 0)
            {
                var emptyText = new TextBlock
                {
                    Text = "没有可添加的歌曲",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.72
                };
                Grid.SetRow(emptyText, 1);
                root.Children.Add(emptyText);
                return root;
            }

            var listView = new ListView
            {
                ItemsSource = options,
                ItemTemplate = (DataTemplate)Resources["AddSongItemTemplate"],
                SelectionMode = ListViewSelectionMode.None,
                Padding = new Thickness(0, 0, 12, 8)
            };
            Grid.SetRow(listView, 1);
            listView.ItemContainerStyle = new Style(typeof(ListViewItem))
            {
                Setters =
                {
                    new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                    new Setter(ListViewItem.PaddingProperty, new Thickness(0)),
                    new Setter(ListViewItem.MarginProperty, new Thickness(0, 0, 0, 8))
                }
            };

            selectAllButton.Click += (_, _) =>
            {
                bool anyUnselected = options.Any(opt => !opt.IsSelected);
                foreach (var opt in options)
                    opt.IsSelected = anyUnselected;
                selectAllButton.Content = anyUnselected ? "取消全选" : "全选";
                listView.ItemsSource = null;
                listView.ItemsSource = options;
            };
            root.Children.Add(listView);
            return root;
        }

        private void AddSongCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: AddSongOption option } element)
                return;

            option.IsSelected = !option.IsSelected;
            if (FindDescendant<CheckBox>(element) is CheckBox checkBox)
                checkBox.IsChecked = option.IsSelected;
        }

        private async Task PlaySongAsync(MediaItem item)
        {
            var queue = _filteredSongs.Count > 0
                ? _filteredSongs.ToList()
                : _playlist?.Items.ToList() ?? new List<MediaItem> { item };
            await App.MusicPlayback.PlayAsync(item, queue);
        }

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
            MultiSelectDeleteButton.IsEnabled = count > 0;
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

        /// <summary>
        /// 同步三套布局中的多选按钮状态
        /// </summary>
        private void SyncMultiSelectToggles(bool isChecked)
        {
            WideMultiSelectToggle.IsChecked = isChecked;
            MediumMultiSelectToggle.IsChecked = isChecked;
            NarrowMultiSelectToggle.IsChecked = isChecked;
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

        private void MultiSelectDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playlist == null || _multiSelectedFiles.Count == 0)
                return;

            _playlist.Items.RemoveAll(s => _multiSelectedFiles.Contains(s.FilePath));
            InvokeSaveChanges();
            _multiSelectedFiles.Clear();
            RefreshHeader();
            ApplyFilter();
            ExitMultiSelectMode();
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

            var grid = FindDescendantByName<Grid>(lvi, "SongItemGrid");
            if (grid != null && grid.ColumnDefinitions.Count > 2)
            {
                grid.ColumnDefinitions[2].Width = _isNarrowLayout
                    ? new GridLength(0)
                    : new GridLength(220);
            }
        }

        private void PlaylistSongCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                _multiSelectedFiles.Add(item.FilePath);
                UpdateMultiSelectUI();
            }
        }

        private void PlaylistSongCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                _multiSelectedFiles.Remove(item.FilePath);
                UpdateMultiSelectUI();
            }
        }

        private void RemoveSong(MediaItem item)
        {
            if (_playlist == null)
                return;

            var target = _playlist.Items.FirstOrDefault(song =>
                string.Equals(song.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return;

            _playlist.Items.Remove(target);
            InvokeSaveChanges();
            RefreshHeader();
            ApplyFilter();
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
            catch
            {
            }
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

        private async void EditPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playlist == null)
                return;

            if (await PlaylistDetailPage.ShowEditDialogAsync(_playlist, InvokeSaveChanges, XamlRoot))
                RefreshHeader();
        }

        /// <summary>
        /// 打开与创建歌单一致的完整编辑弹窗（封面裁剪 + 名称 + 描述 + 清除自定义图片）。
        /// </summary>
        public static async Task<bool> ShowEditDialogAsync(
            Playlist playlist,
            Action? saveChanges,
            XamlRoot xamlRoot)
        {
            if (playlist == null)
                return false;

            string selectedCoverPath = playlist.CoverPath;

            var dialog = new ContentDialog
            {
                Title = "编辑歌单",
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
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

            // Load existing cover
            string displayCover = playlist.CoverDisplayPath;
            if (!string.IsNullOrWhiteSpace(displayCover))
            {
                bool isMusicFile = Path.GetExtension(displayCover).ToLowerInvariant() is ".mp3" or ".flac" or ".wav" or ".aac" or ".m4a" or ".ogg" or ".wma" or ".opus";
                string imagePath = isMusicFile
                    ? MusicCoverService.GetOrCreate(displayCover)
                    : displayCover;

                if (isMusicFile && string.IsNullOrWhiteSpace(imagePath))
                    imagePath = MusicCoverService.GetOrCreateOriginal(displayCover);

                if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                {
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    using (var stream = System.IO.File.OpenRead(imagePath))
                    {
                        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                    }
                    coverImage.Source = bitmap;
                    coverPlaceholder.Visibility = Visibility.Collapsed;
                }
            }

            var coverBorder = new Border
            {
                Width = 200,
                Height = 200,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x33, 0, 0, 0)),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                Child = new Grid { Children = { coverPlaceholder, coverImage, coverOverlay } }
            };

            // Hover/Press visual feedback
            coverBorder.PointerEntered += (_, _) => coverOverlay.Background = new SolidColorBrush(ColorHelper.FromArgb(0x14, 0, 0, 0));
            coverBorder.PointerExited += (_, _) => coverOverlay.Background = new SolidColorBrush(Colors.Transparent);
            coverBorder.PointerPressed += (_, _) => coverOverlay.Background = new SolidColorBrush(ColorHelper.FromArgb(0x28, 0, 0, 0));
            coverBorder.PointerReleased += (_, _) => coverOverlay.Background = new SolidColorBrush(ColorHelper.FromArgb(0x14, 0, 0, 0));

            var clearCoverBtn = new Button
            {
                Content = "清除自定义图片",
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                IsEnabled = !string.IsNullOrEmpty(selectedCoverPath)
            };
            clearCoverBtn.Click += (_, _) =>
            {
                selectedCoverPath = string.Empty;
                coverImage.Source = null;
                coverPlaceholder.Visibility = Visibility.Visible;
                clearCoverBtn.IsEnabled = false;
            };

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
                        var croppedPath = await ImageCropDialog.ShowAsync(xamlRoot, file);
                        Debug.WriteLine($"[EditPlaylist] 裁剪结果: {(croppedPath ?? "null")}");
                        if (!string.IsNullOrEmpty(croppedPath) && File.Exists(croppedPath))
                        {
                            selectedCoverPath = croppedPath;
                            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                            byte[] imgBytes = await File.ReadAllBytesAsync(croppedPath);
                            using var mem = new MemoryStream(imgBytes, writable: false);
                            await bitmap.SetSourceAsync(mem.AsRandomAccessStream());
                            coverImage.Source = bitmap;
                            coverPlaceholder.Visibility = Visibility.Collapsed;
                            clearCoverBtn.IsEnabled = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[EditPlaylist] 封面选择失败: {ex.GetType().Name}: {ex.Message}");
                    AppLogger.Error(ex, "编辑歌单封面选择失败");
                }
            };

            var leftPanel = new StackPanel { Padding = new Thickness(25, 0, 3, 0), Children = { coverBorder, clearCoverBtn } };
            Grid.SetColumn(leftPanel, 0);
            rootGrid.Children.Add(leftPanel);

            // Right: Name + Description
            var rightPanel = new StackPanel { Spacing = 12 };
            var textBox = new TextBox
            {
                PlaceholderText = "歌单名称",
                Text = playlist.Name,
                Width = 250
            };
            rightPanel.Children.Add(textBox);

            var descBox = new TextBox
            {
                PlaceholderText = "歌单描述（可选）",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 150,
                MaxHeight = 200,
                Text = playlist.Description,
                Width = 250
            };
            rightPanel.Children.Add(descBox);

            Grid.SetColumn(rightPanel, 1);
            rootGrid.Children.Add(rightPanel);

            dialog.Content = rootGrid;

            var result = await DialogService.ShowAsync(dialog, xamlRoot);
            if (result != ContentDialogResult.Primary)
                return false;

            string name = textBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
                name = "无标题";

            playlist.Name = name;
            playlist.Description = descBox.Text?.Trim() ?? string.Empty;
            playlist.CoverPath = selectedCoverPath;
            saveChanges?.Invoke();

            return true;
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
    }

    public sealed class PlaylistDetailArgs
    {
        public Playlist? Playlist { get; set; }
        public Action? SaveChanges { get; set; }
    }

    public sealed class AddSongOption
    {
        public AddSongOption(MediaItem item)
        {
            Item = item;
        }

        public MediaItem Item { get; }
        public bool IsSelected { get; set; }
    }
}
