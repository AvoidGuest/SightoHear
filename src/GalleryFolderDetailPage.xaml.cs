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
using System.Threading;
using Windows.Storage;
using Windows.System;

namespace SightoHear
{
    public sealed partial class GalleryFolderDetailPage : Page
    {
        private string _folderPath = string.Empty;
        private List<MediaItem> _directImages = new();          // 仅直接子图片
        private List<MediaItem> _filteredImages = new();
        private CancellationTokenSource? _pumpCts;

        public GalleryFolderDetailPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is GalleryFolderDetailArgs args)
            {
                _folderPath = args.FolderPath;
                // 直接图片：父目录 == 当前文件夹
                _directImages = args.Images
                    .Where(v => string.Equals(Path.GetDirectoryName(v.FilePath), _folderPath, StringComparison.OrdinalIgnoreCase))
                    .Where(v => !string.IsNullOrEmpty(v.FilePath) && File.Exists(v.FilePath))
                    .ToList();
            }

            _filteredImages = _directImages.ToList();
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
            string name = !string.IsNullOrEmpty(_folderPath) ? Path.GetFileName(_folderPath) : "文件夹";
            FolderNameText.Text = name;
            FolderPathText.Text = _folderPath;
            FolderStatText.Text = $"{_directImages.Count} 张图片";
            ViewAllButton.IsEnabled = _directImages.Count > 0;
        }

        private void ApplyFilter()
        {
            string searchText = ImageSearchBox.Text?.Trim() ?? string.Empty;
            IEnumerable<MediaItem> query = _directImages;

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
            bool isEmpty = _filteredImages.Count == 0;
            ImageGrid.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            EmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = "该文件夹没有图片";
            ImageGrid.ItemsSource = isEmpty ? null : _filteredImages;
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

        private void ImageGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MediaItem item)
                OpenImageViewer(item);
        }

        private void ImageItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: MediaItem item } element) return;
            e.Handled = true;

            var menu = new MenuFlyout();

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

            var propertiesItem = new MenuFlyoutItem { Text = "属性", Icon = new FontIcon { FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"), Glyph = "\uE946" } };
            propertiesItem.Click += (_, _) => _ = ShowPropertiesAsync(item);
            menu.Items.Add(propertiesItem);

            menu.ShowAt(element, e.GetPosition(element));
        }

        private void OpenImageViewer(MediaItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
                return;

            var playlist = _filteredImages.Count > 0 ? _filteredImages.ToList() : _directImages.ToList();
            int index = playlist.FindIndex(x => x.FilePath == item.FilePath);
            if (index < 0) index = 0;

            (App.MainWindow as MainWindow)?.OpenImageViewer(new ImageViewerArgs
            {
                Playlist = playlist,
                StartIndex = index
            });
        }

        private async System.Threading.Tasks.Task OpenWithExternalAsync(MediaItem item)
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

        private async System.Threading.Tasks.Task ShowPropertiesAsync(MediaItem item)
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

    public sealed class GalleryFolderDetailArgs
    {
        public string FolderPath { get; set; } = string.Empty;
        public List<MediaItem> Images { get; set; } = new();
    }
}
