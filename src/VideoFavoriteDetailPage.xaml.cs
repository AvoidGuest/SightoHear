using SightoHear.Helpers;
using SightoHear.Models;
using SightoHear.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SightoHear
{
    public sealed partial class VideoFavoriteDetailPage : Page
    {
        private Playlist? _favorite;
        private List<MediaItem> _filteredVideos = new();
        private bool _isMultiSelectMode;
        private readonly HashSet<string> _multiSelectedFiles = new(StringComparer.OrdinalIgnoreCase);
        private Action? _saveChanges;

        /// <summary>
        /// 调用保存委托，并在保存后通知 MainWindow 同步侧边栏固定项
        /// （收藏夹重命名后侧边栏名称立即更新，无论从哪个入口进入）。
        /// </summary>
        private void InvokeSaveChanges()
        {
            _saveChanges?.Invoke();
            if (_favorite != null)
                MainWindow.NotifyDetailSaved(SidebarShortcutType.VideoFavorite, _favorite.Id, _favorite.Name);
        }

        public VideoFavoriteDetailPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is VideoFavoriteDetailArgs args)
            {
                _favorite = args.Favorite;
                _saveChanges = args.SaveChanges;
            }

            _filteredVideos = _favorite?.Items.ToList() ?? new();
            RefreshHeader();
            ApplyFilter();
        }

        private async void RefreshHeader()
        {
            string name = _favorite?.Name ?? "收藏夹";
            int count = _favorite?.Items.Count ?? 0;

            FavoriteNameText.Text = name;
            CreatedTimeText.Text = _favorite == null
                ? string.Empty
                : $"创建于 {_favorite.DateCreated:yyyy/M/d}";
            VideoCountText.Text = $"{count} 个视频";
            PlayButton.IsEnabled = count > 0;

            if (!string.IsNullOrWhiteSpace(_favorite?.Description))
                FavoriteDescriptionText.Text = _favorite.Description;
            else
                FavoriteDescriptionText.Text = "暂无描述";

            // 加载封面图片（优先自定义封面，回退到最后一个视频的缩略图）
            string coverPath = _favorite?.CoverPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(coverPath) && _favorite?.Items.Count > 0)
            {
                var lastItem = _favorite.Items.LastOrDefault();
                if (lastItem != null && !string.IsNullOrWhiteSpace(lastItem.ThumbnailPath) && File.Exists(lastItem.ThumbnailPath))
                    coverPath = lastItem.ThumbnailPath;
            }

            if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    byte[] imgBytes = await File.ReadAllBytesAsync(coverPath);
                    using var mem = new MemoryStream(imgBytes, writable: false);
                    await bitmap.SetSourceAsync(mem.AsRandomAccessStream());
                    CoverImage.Source = bitmap;
                    CoverImage.Visibility = Visibility.Visible;
                    CoverPlaceholderIcon.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    CoverImage.Visibility = Visibility.Collapsed;
                    CoverPlaceholderIcon.Visibility = Visibility.Visible;
                }
            }
            else
            {
                CoverImage.Visibility = Visibility.Collapsed;
                CoverPlaceholderIcon.Visibility = Visibility.Visible;
            }
        }

        private void ApplyFilter()
        {
            string searchText = VideoSearchBox?.Text?.Trim() ?? string.Empty;
            IEnumerable<MediaItem> query = _favorite?.Items ?? new List<MediaItem>();

            if (!string.IsNullOrWhiteSpace(searchText))
                query = query.Where(v => v.FileName.Contains(searchText, StringComparison.OrdinalIgnoreCase));

            _filteredVideos = query.ToList();
            RefreshVideoList();
        }

        private void RefreshVideoList()
        {
            bool resultEmpty = _filteredVideos.Count == 0;
            bool srcEmpty = _favorite?.Items.Count == 0;

            ListHeader.Visibility = resultEmpty ? Visibility.Collapsed : Visibility.Visible;
            VideoList.Visibility = resultEmpty ? Visibility.Collapsed : Visibility.Visible;
            EmptyStateText.Visibility = resultEmpty ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = srcEmpty == true
                ? "收藏夹中没有视频"
                : "没有找到匹配的视频";
            VideoList.ItemsSource = resultEmpty ? null : _filteredVideos;
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

            var removeItem = new MenuFlyoutItem
            {
                Text = "从收藏夹移除",
                Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE74D" }
            };
            removeItem.Click += (_, _) => RemoveFromFavorite(item);
            menu.Items.Add(removeItem);

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

            var videos = _filteredVideos
                .Where(v => _multiSelectedFiles.Contains(v.FilePath))
                .ToList();

            if (videos.Count > 0)
                PlayVideo(videos[0], videos);
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

        private void FavoriteVideoCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is MediaItem item)
            {
                _multiSelectedFiles.Add(item.FilePath);
                UpdateMultiSelectUI();
            }
        }

        private void FavoriteVideoCheckBox_Unchecked(object sender, RoutedEventArgs e)
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
            var args = new VideoPlayerArgs
            {
                Playlist = playlist ?? _filteredVideos,
                StartIndex = (playlist ?? _filteredVideos).IndexOf(item)
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

        private void RemoveFromFavorite(MediaItem item)
        {
            if (_favorite == null) return;

            _favorite.Items.Remove(item);
            InvokeSaveChanges();
            _filteredVideos = _favorite.Items.ToList();
            RefreshHeader();
            ApplyFilter();
        }

        private async void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (_favorite == null) return;

            string selectedCoverPath = _favorite.CoverPath ?? string.Empty;

            var dialog = new ContentDialog
            {
                Title = "编辑收藏夹",
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            // 水平布局：左侧封面，右侧名称 + 描述
            var rootGrid = new Grid { ColumnSpacing = 6, Width = 520 };
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左侧：封面图片选择器（1:1 正方形）
            var coverPlaceholder = new FontIcon
            {
                FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"),
                Glyph = "\uE710",
                FontSize = 48,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var coverImage = new Image
            {
                Width = 200,
                Height = 200,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
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
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x33, 0, 0, 0)),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                Child = new Grid { Children = { coverPlaceholder, coverImage, coverOverlay } }
            };

            // 如果已有封面，直接显示
            if (!string.IsNullOrEmpty(selectedCoverPath) && File.Exists(selectedCoverPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    byte[] imgBytes = await File.ReadAllBytesAsync(selectedCoverPath);
                    using var mem = new MemoryStream(imgBytes, writable: false);
                    await bitmap.SetSourceAsync(mem.AsRandomAccessStream());
                    coverImage.Source = bitmap;
                    coverPlaceholder.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "编辑收藏夹时加载封面预览失败");
                }
            }

            // 清除自定义图片按钮
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
                                var bitmap = new BitmapImage();
                                byte[] imgBytes = await File.ReadAllBytesAsync(croppedPath);
                                using var mem = new MemoryStream(imgBytes, writable: false);
                                await bitmap.SetSourceAsync(mem.AsRandomAccessStream());
                                coverImage.Source = bitmap;
                                coverPlaceholder.Visibility = Visibility.Collapsed;
                                clearCoverBtn.IsEnabled = true;
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Error(ex, "编辑收藏夹封面预览加载失败");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "编辑收藏夹封面选择失败");
                }
            };

            var leftPanel = new StackPanel { Padding = new Thickness(25, 0, 3, 0), Children = { coverBorder, clearCoverBtn } };
            Grid.SetColumn(leftPanel, 0);
            rootGrid.Children.Add(leftPanel);

            // 右侧：名称 + 描述
            var rightPanel = new StackPanel { Spacing = 12 };
            var textBox = new TextBox { Text = _favorite.Name, PlaceholderText = "收藏夹名称", Width = 250 };
            textBox.SelectAll();
            rightPanel.Children.Add(textBox);

            var descBox = new TextBox
            {
                Text = _favorite.Description ?? string.Empty,
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
            if (result == ContentDialogResult.Primary)
            {
                var newName = textBox.Text?.Trim();
                if (!string.IsNullOrEmpty(newName))
                    _favorite.Name = newName;
                _favorite.Description = descBox.Text?.Trim() ?? string.Empty;
                _favorite.CoverPath = selectedCoverPath;
                InvokeSaveChanges();
                RefreshHeader();
            }
        }

        #region 添加视频到收藏夹

        private async void AddSongsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_favorite == null) return;

            var allVideos = MediaScanner.LoadFromCache("Video");
            var existingPaths = _favorite.Items
                .Select(item => item.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var options = allVideos
                .Where(item => !existingPaths.Contains(item.FilePath))
                .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(item => new AddItemOption(item))
                .ToList();

            var selectedItems = await ItemPickerDialog.ShowAsync(
                XamlRoot,
                "添加视频",
                options,
                (DataTemplate)Resources["AddVideoItemTemplate"]);

            if (selectedItems == null || selectedItems.Count == 0) return;

            _favorite.Items.AddRange(selectedItems);
            InvokeSaveChanges();
            RefreshHeader();
            ApplyFilter();
        }

        #region 添加弹窗项事件处理

        private void ItemCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: AddItemOption option } element)
                return;

            option.IsSelected = !option.IsSelected;
            if (FindDescendant<CheckBox>(element) is CheckBox checkBox)
                checkBox.IsChecked = option.IsSelected;
        }

        private void ItemCard_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
                border.Background = GetAddItemCardBrush("AddItemCardPointerOverBackgroundBrush");
        }

        private void ItemCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
                border.Background = GetAddItemCardBrush("AddItemCardNormalBackgroundBrush");
        }

        private void ItemCard_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
                border.Background = GetAddItemCardBrush("AddItemCardPressedBackgroundBrush");
        }

        private void ItemCard_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            // 按下释放后恢复为悬停状态
            if (sender is Border border)
                border.Background = GetAddItemCardBrush("AddItemCardPointerOverBackgroundBrush");
        }

        // ★ 从页面资源的 ThemeDictionaries 中按实际主题取刷子：
        //   弹窗中 ActualTheme 在 Win11 下可能返回 Default 导致手动计算颜色时走错分支（反色），
        //   这里通过 XamlRoot 回退可靠判断主题，且颜色统一由 XAML 主题字典管理。
        private Brush GetAddItemCardBrush(string key)
        {
            bool isDark = ActualTheme == ElementTheme.Dark
                || (ActualTheme == ElementTheme.Default
                    && XamlRoot?.Content is FrameworkElement root
                    && root.ActualTheme == ElementTheme.Dark);
            string dictKey = isDark ? "Dark" : "Light";
            if (Resources.ThemeDictionaries.TryGetValue(dictKey, out var dict)
                && dict is ResourceDictionary rd
                && rd.TryGetValue(key, out var value)
                && value is Brush brush)
                return brush;
            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        #endregion

        #endregion

        #region 多选删除

        private void MultiSelectDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_favorite == null || _multiSelectedFiles.Count == 0) return;

            _favorite.Items.RemoveAll(v => _multiSelectedFiles.Contains(v.FilePath));
            InvokeSaveChanges();
            _multiSelectedFiles.Clear();
            RefreshHeader();
            ApplyFilter();
            ExitMultiSelectMode();
        }

        #endregion

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

    public class VideoFavoriteDetailArgs
    {
        public Playlist Favorite { get; set; } = null!;
        public Action? SaveChanges { get; set; }
    }


}
