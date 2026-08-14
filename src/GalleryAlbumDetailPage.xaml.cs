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
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace SightoHear
{
    public sealed partial class GalleryAlbumDetailPage : Page
    {
        private Playlist? _favorite;
        private Action? _saveChanges;

        /// <summary>
        /// 调用保存委托，并在保存后通知 MainWindow 同步侧边栏固定项
        /// （相册重命名后侧边栏名称立即更新，无论从哪个入口进入）。
        /// </summary>
        private void InvokeSaveChanges()
        {
            _saveChanges?.Invoke();
            if (_favorite != null)
                MainWindow.NotifyDetailSaved(SidebarShortcutType.GalleryAlbum, _favorite.Id, _favorite.Name);
        }
        private List<MediaItem> _allImages = new();
        private List<MediaItem> _filteredImages = new();
        private bool _isMultiSelectMode;
        private readonly HashSet<string> _multiSelectedFiles = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _pumpCts;

        public GalleryAlbumDetailPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is GalleryAlbumDetailArgs args)
            {
                _favorite = args.Favorite;
                _saveChanges = args.SaveChanges;
            }

            _allImages = _favorite?.Items ?? new List<MediaItem>();
            RefreshHeader();
            ApplyFilter();
            StartLoadPump();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _pumpCts?.Cancel();
            _pumpCts?.Dispose();
            _pumpCts = null;
        }

        private void RefreshHeader()
        {
            if (_favorite == null) return;

            AlbumNameText.Text = _favorite.Name;
            AlbumDescriptionText.Text = _favorite.Description;
            CreatedTimeText.Text = _favorite.DateCreated.ToString("yyyy-MM-dd");
            ImageCountText.Text = $"{_allImages.Count} 张图片";
            ViewAllButton.IsEnabled = _allImages.Count > 0;

            // 加载封面
            if (!string.IsNullOrEmpty(_favorite.CoverPath) && File.Exists(_favorite.CoverPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    byte[] imgBytes = File.ReadAllBytes(_favorite.CoverPath);
                    using var mem = new MemoryStream(imgBytes, writable: false);
                    bitmap.SetSource(mem.AsRandomAccessStream());
                    CoverImage.Source = bitmap;
                    CoverImage.Visibility = Visibility.Visible;
                    CoverPlaceholderIcon.Visibility = Visibility.Collapsed;
                }
                catch { }
            }
            else
            {
                // 尝试用最后一张图片作为封面
                var lastItem = _allImages.LastOrDefault();
                if (lastItem != null)
                {
                    string sourcePath = !string.IsNullOrEmpty(lastItem.ThumbnailPath)
                        ? lastItem.ThumbnailPath
                        : lastItem.FilePath;
                    try
                    {
                        var bitmap = new BitmapImage();
                        byte[] imgBytes = File.ReadAllBytes(sourcePath);
                        using var mem = new MemoryStream(imgBytes, writable: false);
                        bitmap.SetSource(mem.AsRandomAccessStream());
                        CoverImage.Source = bitmap;
                        CoverImage.Visibility = Visibility.Visible;
                        CoverPlaceholderIcon.Visibility = Visibility.Collapsed;
                    }
                    catch { }
                }
            }
        }

        private void ApplyFilter()
        {
            string searchText = ImageSearchBox.Text?.Trim() ?? string.Empty;
            IEnumerable<MediaItem> query = _allImages;

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                query = query.Where(item =>
                    item.FileName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    item.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase));
            }

            _filteredImages = query.ToList();
            RefreshImageGrid();
        }

        private void RefreshImageGrid()
        {
            bool resultEmpty = _filteredImages.Count == 0;
            bool srcEmpty = _allImages.Count == 0;

            ImageGrid.Visibility = resultEmpty ? Visibility.Collapsed : Visibility.Visible;
            EmptyStateText.Visibility = resultEmpty ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = srcEmpty
                ? "该相册没有图片"
                : "没有找到匹配的图片";

            ImageGrid.ItemsSource = resultEmpty ? null : _filteredImages;
        }

        private void ImageSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyFilter();
        }

        private void ViewAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_filteredImages.Count > 0)
            {
                var viewArgs = new ImageViewerArgs
                {
                    Playlist = _filteredImages.ToList(),
                    StartIndex = 0
                };
                (App.MainWindow as MainWindow)?.OpenImageViewer(viewArgs);
            }
        }

        #region 编辑/添加

        private async void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (_favorite == null) return;

            var nameBox = new TextBox { Text = _favorite.Name, Width = 280 };
            nameBox.SelectAll();
            var descBox = new TextBox
            {
                Text = _favorite.Description,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 100,
                Width = 280,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "相册名称" });
            panel.Children.Add(nameBox);
            panel.Children.Add(new TextBlock { Text = "相册描述", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(descBox);

            var dialog = new ContentDialog
            {
                Title = "编辑相册",
                Content = panel,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            var result = await DialogService.ShowAsync(dialog, XamlRoot);
            if (result == ContentDialogResult.Primary)
            {
                var newName = nameBox.Text?.Trim();
                if (!string.IsNullOrEmpty(newName))
                    _favorite.Name = newName;
                _favorite.Description = descBox.Text?.Trim() ?? string.Empty;
                InvokeSaveChanges();
                RefreshHeader();
            }
        }

        private async void AddImagesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_favorite == null) return;

            var allImages = MediaScanner.LoadFromCache("Image");
            var existingPaths = _favorite.Items
                .Select(item => item.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var options = allImages
                .Where(item => !existingPaths.Contains(item.FilePath))
                .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(item => new AddItemOption(item))
                .ToList();

            var selectedItems = await ItemPickerDialog.ShowAsync(
                XamlRoot,
                "添加图片",
                options,
                (DataTemplate)Resources["AddImageItemTemplate"]);

            if (selectedItems == null || selectedItems.Count == 0) return;

            _favorite.Items.AddRange(selectedItems);
            InvokeSaveChanges();
            ApplyFilter();
            RefreshHeader();
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

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T tChild)
                    return tChild;
                var result = FindDescendant<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        #endregion

        #endregion

        #region 多选

        private void MultiSelectToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isMultiSelectMode)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode();
        }

        private void EnterMultiSelectMode()
        {
            _isMultiSelectMode = true;
            _multiSelectedFiles.Clear();
            MultiSelectToggle.IsChecked = true;
            MultiSelectToolbar.Visibility = Visibility.Visible;
            UpdateMultiSelectUI();
        }

        private void ExitMultiSelectMode()
        {
            _isMultiSelectMode = false;
            _multiSelectedFiles.Clear();
            MultiSelectToggle.IsChecked = false;
            MultiSelectToolbar.Visibility = Visibility.Collapsed;
            UpdateMultiSelectUI();
        }

        private void UpdateMultiSelectUI()
        {
            int count = _multiSelectedFiles.Count;
            SelectedCountText.Text = $"已选择 {count} 项";
            SelectAllCheckBox.IsChecked = count == _filteredImages.Count && count > 0;
            MultiSelectRemoveButton.IsEnabled = count > 0;
            MultiSelectViewButton.IsEnabled = count > 0;
        }

        private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool selectAll = SelectAllCheckBox.IsChecked == true;
            _multiSelectedFiles.Clear();
            if (selectAll)
            {
                foreach (var item in _filteredImages)
                    _multiSelectedFiles.Add(item.FilePath);
            }
            UpdateMultiSelectUI();
        }

        private async void MultiSelectRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectedFiles.Count == 0 || _favorite == null) return;

            var dialog = new ContentDialog
            {
                Title = "确认移除",
                Content = $"确定要从相册 \"{_favorite.Name}\" 中移除选中的 {_multiSelectedFiles.Count} 张图片吗？",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };

            var result = await DialogService.ShowAsync(dialog, XamlRoot);
            if (result == ContentDialogResult.Primary)
            {
                _allImages.RemoveAll(v => _multiSelectedFiles.Contains(v.FilePath));
                _favorite.Items = _allImages;
                InvokeSaveChanges();
                ExitMultiSelectMode();
                ApplyFilter();
                RefreshHeader();
            }
        }

        private void MultiSelectViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectedFiles.Count == 0) return;

            var selected = _filteredImages
                .Where(s => _multiSelectedFiles.Contains(s.FilePath))
                .ToList();

            if (selected.Count > 0)
            {
                (App.MainWindow as MainWindow)?.OpenImageViewer(new ImageViewerArgs
                {
                    Playlist = selected,
                    StartIndex = 0
                });
            }
        }

        #endregion

        #region 图片交互

        private void ImageGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MediaItem item)
            {
                if (_isMultiSelectMode)
                {
                    ToggleItemSelection(item);
                    return;
                }
                OpenImageViewer(item);
            }
        }

        private void ImageItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: MediaItem item } element) return;
            e.Handled = true;

            var menu = new MenuFlyout();

            if (!_isMultiSelectMode)
            {
                var viewItem = new MenuFlyoutItem { Text = "查看", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE890" } };
                viewItem.Click += (_, _) => OpenImageViewer(item);
                menu.Items.Add(viewItem);

                var openItem = new MenuFlyoutItem { Text = "使用其他应用打开", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE8A7" } };
                openItem.Click += (_, _) => _ = OpenWithExternalAsync(item);
                menu.Items.Add(openItem);

                var openLocationItem = new MenuFlyoutItem { Text = "打开文件所在位置", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uED25" } };
                openLocationItem.Click += (_, _) => OpenFileLocation(item);
                menu.Items.Add(openLocationItem);

                menu.Items.Add(new MenuFlyoutSeparator());
            }

            var removeItem = new MenuFlyoutItem { Text = "从相册移除", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE74D" } };
            removeItem.Click += (_, _) =>
            {
                _allImages.Remove(item);
                _favorite!.Items = _allImages;
                InvokeSaveChanges();
                ApplyFilter();
                RefreshHeader();
            };
            menu.Items.Add(removeItem);

            if (!_isMultiSelectMode)
            {
                var propertiesItem = new MenuFlyoutItem { Text = "属性", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE946" } };
                propertiesItem.Click += (_, _) => _ = ShowPropertiesAsync(item);
                menu.Items.Add(propertiesItem);
            }

            menu.ShowAt(element, e.GetPosition(element));
        }

        private void ToggleItemSelection(MediaItem item)
        {
            if (!_multiSelectedFiles.Remove(item.FilePath))
                _multiSelectedFiles.Add(item.FilePath);
            UpdateMultiSelectUI();
        }

        private void OpenImageViewer(MediaItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
                return;

            var playlist = _filteredImages.Count > 0 ? _filteredImages.ToList() : _allImages.ToList();
            int index = playlist.FindIndex(x => x.FilePath == item.FilePath);
            if (index < 0) index = 0;

            (App.MainWindow as MainWindow)?.OpenImageViewer(new ImageViewerArgs
            {
                Playlist = playlist,
                StartIndex = index
            });
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

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
            return string.Format("{0:0.##} {1}", size, sizes[order]);
        }

        #endregion

        #region 缩略图渐进加载

        private void ThumbnailImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Image image || image.Tag is not MediaItem mediaItem)
                return;
            if (image.Source != null && image.Opacity > 0)
                return;

            string sourcePath = !string.IsNullOrEmpty(mediaItem.ThumbnailPath)
                ? mediaItem.ThumbnailPath
                : mediaItem.FilePath;

            TryLoadImage(image, sourcePath);
        }

        private void ThumbnailImage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Image image)
            {
                image.Source = null;
                image.Opacity = 0;
            }
        }

        private void TryLoadImage(Image image, string sourcePath)
        {
            if (image.Source != null && image.Opacity > 0)
                return;

            string diskPath = ImageThumbnailService.GetDiskCachePath(sourcePath, 192);

            if (ImageThumbnailService.IsInMemoryCache(diskPath))
            {
                var bmp = ImageThumbnailService.GetOrCreate(diskPath);
                if (bmp != null) { image.Source = bmp; image.Opacity = 1; return; }
            }

            if (File.Exists(diskPath))
            {
                var bmp = ImageThumbnailService.GetOrCreate(diskPath);
                if (bmp != null) { image.Source = bmp; image.Opacity = 1; return; }
            }

            var bmp2 = ImageThumbnailService.GetOrCreate(sourcePath);
            if (bmp2 != null) { image.Source = bmp2; image.Opacity = 1; }
        }

        private void StartLoadPump() { }

        #endregion
    }

    public sealed class GalleryAlbumDetailArgs
    {
        public Playlist Favorite { get; set; } = new();
        public Action SaveChanges { get; set; } = () => { };
    }
}
