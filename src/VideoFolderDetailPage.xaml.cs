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

namespace SightoHear
{
    public sealed partial class VideoFolderDetailPage : Page
    {
        private string _folderPath = string.Empty;
        private List<MediaItem> _directVideos = new();          // 仅直接子视频
        private List<MediaItem> _filteredVideos = new();
        private bool _isMultiSelectMode;
        private readonly HashSet<string> _multiSelectedFiles = new(StringComparer.OrdinalIgnoreCase);

        public VideoFolderDetailPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is VideoFolderDetailArgs args)
            {
                _folderPath = args.FolderPath;
                // 直接视频：父目录 == 当前文件夹
                _directVideos = args.Videos
                    .Where(v => string.Equals(Path.GetDirectoryName(v.FilePath), _folderPath, StringComparison.OrdinalIgnoreCase))
                    .Where(v => !string.IsNullOrEmpty(v.FilePath) && File.Exists(v.FilePath))
                    .ToList();
            }
            else if (e.Parameter is string path)
            {
                _folderPath = path;
                _directVideos = new();
            }

            _filteredVideos = _directVideos.ToList();
            RefreshHeader();
            ApplyFilter();
        }

        private void RefreshHeader()
        {
            string name = !string.IsNullOrEmpty(_folderPath)
                ? Path.GetFileName(_folderPath)
                : "文件夹";
            FolderNameText.Text = name;
            FolderPathText.Text = _folderPath;
            FolderStatText.Text = $"{_directVideos.Count} 个视频";
            PlayButton.IsEnabled = _directVideos.Count > 0;
        }

        private void ApplyFilter()
        {
            string searchText = VideoSearchBox?.Text?.Trim() ?? string.Empty;
            IEnumerable<MediaItem> query = _directVideos;

            if (!string.IsNullOrWhiteSpace(searchText))
                query = query.Where(v => v.FileName.Contains(searchText, StringComparison.OrdinalIgnoreCase));

            _filteredVideos = query.ToList();
            RefreshVideoList();
        }

        private void RefreshVideoList()
        {
            bool isEmpty = _filteredVideos.Count == 0;

            ListHeader.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            VideoList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            EmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = "该文件夹中没有视频";
            VideoList.ItemsSource = isEmpty ? null : _filteredVideos;
        }

        private void VideoSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyFilter();
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_filteredVideos.Count == 0) return;
            PlayVideo(_filteredVideos[0]);
        }

        private void VideoList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MediaItem clickedItem) return;

            if (_isMultiSelectMode)
            {
                ToggleItemSelection(clickedItem);
                return;
            }

            PlayVideo(clickedItem);
        }

        private void VideoItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
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

            e.Handled = true;
            PlayVideo(tappedItem);
        }

        private void VideoItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: MediaItem item } element)
                return;

            e.Handled = true;
            var menu = new MenuFlyout();

            var playItem = new MenuFlyoutItem
            {
                Text = "播放",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE768" }
            };
            playItem.Click += (_, _) => PlayVideo(item);
            menu.Items.Add(playItem);

            var locationItem = new MenuFlyoutItem
            {
                Text = "打开文件所在位置",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uED25" }
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
                MultiSelectToggle.IsChecked = true;
                MultiSelectToolbar.Visibility = Visibility.Visible;
                ToggleItemSelection(item);
            };
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

        #region 多选
        private void ToggleItemSelection(MediaItem item)
        {
            if (!_multiSelectedFiles.Remove(item.FilePath))
                _multiSelectedFiles.Add(item.FilePath);
            UpdateVideoListCheckBoxes();
            UpdateMultiSelectUI();
        }

        private void UpdateVideoListCheckBoxes()
        {
            if (VideoList?.Items == null) return;
            foreach (var container in VideoList.Items)
            {
                if (VideoList.ContainerFromItem(container) is ListViewItem lvi &&
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
            SelectAllCheckBox.IsChecked = count == _filteredVideos.Count && count > 0;
            MultiSelectDeleteButton.IsEnabled = count > 0;
            MultiSelectPlayAllButton.IsEnabled = count > 0;
        }

        private void EnterMultiSelectMode()
        {
            _isMultiSelectMode = true;
            _multiSelectedFiles.Clear();
            MultiSelectToggle.IsChecked = true;
            MultiSelectToolbar.Visibility = Visibility.Visible;
            UpdateMultiSelectUI();
            UpdateVideoListCheckBoxes();
        }

        private void ExitMultiSelectMode()
        {
            _isMultiSelectMode = false;
            _multiSelectedFiles.Clear();
            MultiSelectToggle.IsChecked = false;
            MultiSelectToolbar.Visibility = Visibility.Collapsed;
            UpdateVideoListCheckBoxes();
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
                foreach (var item in _filteredVideos)
                    _multiSelectedFiles.Add(item.FilePath);
            }
            UpdateVideoListCheckBoxes();
            UpdateMultiSelectUI();
        }

        private void MultiSelectPlayAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectedFiles.Count == 0) return;

            var selectedVideos = _filteredVideos
                .Where(v => _multiSelectedFiles.Contains(v.FilePath))
                .ToList();

            if (selectedVideos.Count > 0)
                PlayVideo(selectedVideos[0], selectedVideos);
        }

        private async void MultiSelectDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectedFiles.Count == 0) return;

            int count = _multiSelectedFiles.Count;
            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将选中的 {count} 个视频文件移入到回收站吗？可随时还原。"
                    : $"确定要删除选中的 {count} 个本地磁盘文件吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            var result = await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary) return;

            int deletedCount = 0;
            foreach (var filePath in _multiSelectedFiles.ToList())
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        // 根据「删除文件时移入回收站」设置决定删除方式
                        if (App.SettingsHelper.DeleteToRecycleBin)
                            RecycleBinHelper.DeleteToRecycleBin(filePath);
                        else
                            File.Delete(filePath);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"删除文件失败：{filePath}");
                }
            }

            // 从内存列表中移除已删除的视频
            _directVideos.RemoveAll(v => _multiSelectedFiles.Contains(v.FilePath));

            _multiSelectedFiles.Clear();
            RefreshHeader();
            ApplyFilter();
            ExitMultiSelectMode();
        }

        private void VideoList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is not ListViewItem lvi || args.Item is not MediaItem item)
                return;

            // 多选复选框状态
            var cb = FindDescendant<CheckBox>(lvi);
            if (cb != null)
            {
                cb.Visibility = _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
                cb.IsChecked = _multiSelectedFiles.Contains(item.FilePath);
            }

            // 异步加载缩略图
            if (args.Phase == 0 && !string.IsNullOrEmpty(item.ThumbnailPath))
            {
                if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root &&
                    root.FindName("ThumbnailImage") is Image image)
                {
                    var bitmap = ImageThumbnailService.GetOrCreate(item.ThumbnailPath);
                    if (bitmap != null)
                    {
                        image.Source = bitmap;
                        image.Opacity = 1;
                    }
                }
                args.Handled = true;
            }
        }

        private void FolderVideoCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                _multiSelectedFiles.Add(item.FilePath);
                UpdateMultiSelectUI();
            }
        }

        private void FolderVideoCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                _multiSelectedFiles.Remove(item.FilePath);
                UpdateMultiSelectUI();
            }
        }
        #endregion

        private void PlayVideo(MediaItem item, List<MediaItem>? playlist = null)
        {
            // 过滤掉文件不存在的项，避免播放器收到无效列表
            var sourceList = playlist ?? _filteredVideos;
            var validPlaylist = sourceList
                .Where(v => !string.IsNullOrEmpty(v.FilePath) && File.Exists(v.FilePath))
                .ToList();

            if (validPlaylist.Count == 0) return;

            // 重新定位起始索引（item 可能在过滤后的列表中不存在）
            int startIndex = validPlaylist.IndexOf(item);
            if (startIndex < 0)
                startIndex = 0;

            var args = new VideoPlayerArgs
            {
                Playlist = validPlaylist,
                StartIndex = startIndex
            };
            (App.MainWindow as MainWindow)?.ShowPlayerOverlay(typeof(VideoPlayerPage), args);
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

        private async System.Threading.Tasks.Task ShowPropertiesAsync(MediaItem item)
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = $"文件名：{item.FileName}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"时长：{item.DurationText}" });
            content.Children.Add(new TextBlock { Text = $"大小：{item.FileSizeText}" });
            content.Children.Add(new TextBlock { Text = $"修改日期：{item.DateModified:yyyy-MM-dd HH:mm:ss}" });
            content.Children.Add(new TextBlock { Text = $"路径：{item.FilePath}", TextWrapping = TextWrapping.Wrap });

            var dialog = new ContentDialog
            {
                Title = "视频属性",
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
    }

    public class VideoFolderDetailArgs
    {
        public string FolderPath { get; set; } = string.Empty;
        public List<MediaItem> Videos { get; set; } = new();
    }
}
