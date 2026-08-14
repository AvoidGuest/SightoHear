using SightoHear.Helpers;
using SightoHear.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SightoHear
{
    public sealed partial class BasicSettingsPage : Page
    {
        public ObservableCollection<string> VideoPaths { get; } = new();
        public ObservableCollection<string> MusicPaths { get; } = new();
        public ObservableCollection<string> ImagePaths { get; } = new();

        private bool _isLoading = true;

        public BasicSettingsPage()
        {
            InitializeComponent();
            LoadSettings();
            Loaded += BasicSettingsPage_Loaded;
            _isLoading = false;
            AppLogger.Info($"基础设置页加载完成, 视频库路径={VideoPaths.Count}, 音乐库路径={MusicPaths.Count}, 图库路径={ImagePaths.Count}");
        }

        private void BasicSettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 页面加载后（字体就绪），让 GPU ComboBox 按钮宽度跟随最长选项
            AdjustGpuComboBoxWidth();
        }

        private void LoadSettings()
        {
            LoadPaths("VideoLibraryPaths", VideoPaths);
            LoadPaths("MusicLibraryPaths", MusicPaths);
            LoadPaths("ImageLibraryPaths", ImagePaths);
            try
            {
                JsonNode? node = JsonNode.Parse(
                    File.ReadAllText(MediaScanner.SettingsPath));
                VideoRecursiveToggle.IsOn =
                    node?["RecursiveScan"]?.GetValue<bool>() ?? true;
                MusicRecursiveToggle.IsOn =
                    node?["MusicRecursiveScan"]?.GetValue<bool>() ?? true;
                ImageRecursiveToggle.IsOn =
                    node?["ImageRecursiveScan"]?.GetValue<bool>() ?? true;
            }
            catch
            {
            }

            LoadRememberSettings();
            LoadWin2DGpuSetting();
        }

        /// <summary>加载「默认记忆」分区的 9 个开关（音乐/视频/图库 × 刷新/视图/排序）与文件操作开关。</summary>
        private void LoadRememberSettings()
        {
            DeleteToRecycleBinToggle.IsOn = App.SettingsHelper.DeleteToRecycleBin;

            MusicRefreshOnStartupToggle.IsOn = App.SettingsHelper.MusicRefreshOnStartup;
            VideoRefreshOnStartupToggle.IsOn = App.SettingsHelper.VideoRefreshOnStartup;
            GalleryRefreshOnStartupToggle.IsOn = App.SettingsHelper.GalleryRefreshOnStartup;

            MusicRememberViewToggle.IsOn = App.SettingsHelper.MusicRememberView;
            VideoRememberViewToggle.IsOn = App.SettingsHelper.VideoRememberView;
            GalleryRememberViewToggle.IsOn = App.SettingsHelper.GalleryRememberView;

            MusicRememberSortToggle.IsOn = App.SettingsHelper.MusicRememberSort;
            VideoRememberSortToggle.IsOn = App.SettingsHelper.VideoRememberSort;
            GalleryRememberSortToggle.IsOn = App.SettingsHelper.GalleryRememberSort;
        }

        /// <summary>
        /// 「启动与记忆 / 文件操作」分区开关统一事件：
        /// 通过 ToggleSwitch.Tag 定位对应设置项并保存。
        /// </summary>
        private void RememberSetting_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;
            if (sender is not ToggleSwitch { Tag: string key } toggle)
                return;

            bool value = toggle.IsOn;
            switch (key)
            {
                case "DeleteToRecycleBin":
                    App.SettingsHelper.DeleteToRecycleBin = value;
                    break;
                case "MusicRefreshOnStartup":
                    App.SettingsHelper.MusicRefreshOnStartup = value;
                    break;
                case "VideoRefreshOnStartup":
                    App.SettingsHelper.VideoRefreshOnStartup = value;
                    break;
                case "GalleryRefreshOnStartup":
                    App.SettingsHelper.GalleryRefreshOnStartup = value;
                    break;
                case "MusicRememberView":
                    App.SettingsHelper.MusicRememberView = value;
                    break;
                case "VideoRememberView":
                    App.SettingsHelper.VideoRememberView = value;
                    break;
                case "GalleryRememberView":
                    App.SettingsHelper.GalleryRememberView = value;
                    break;
                case "MusicRememberSort":
                    App.SettingsHelper.MusicRememberSort = value;
                    break;
                case "VideoRememberSort":
                    App.SettingsHelper.VideoRememberSort = value;
                    break;
                case "GalleryRememberSort":
                    App.SettingsHelper.GalleryRememberSort = value;
                    break;
                default:
                    return;
            }
            App.SettingsHelper.Save();
        }

        /// <summary>加载 Win2D GPU 选择设置：选项卡第一项为「跟随系统」，其后为可用 GPU 列表。</summary>
        private void LoadWin2DGpuSetting()
        {
            var items = new List<GpuSelectionItem> { new() { IsSystem = true } };
            foreach (var gpu in Win2DDeviceManager.EnumerateGpus())
                items.Add(new GpuSelectionItem { Gpu = gpu });
            GpuComboBox.ItemsSource = items;

            // 恢复上次选择：手动指定且 LUID 有效时选中对应 GPU，否则回退「跟随系统」
            GpuSelectionItem selected = items[0];
            if (App.SettingsHelper.Win2DGpuPreference == Win2DDeviceManager.PreferenceManual &&
                ulong.TryParse(App.SettingsHelper.Win2DGpuAdapterLuid,
                    NumberStyles.HexNumber, null, out ulong luid))
            {
                selected = items.FirstOrDefault(i => !i.IsSystem && i.Gpu?.AdapterLuid == luid) ?? items[0];
            }
            GpuComboBox.SelectedItem = selected;
        }

        /// <summary>GPU 选项卡的数据项：跟随系统 或 指定 GPU。</summary>
        private sealed class GpuSelectionItem
        {
            public bool IsSystem { get; init; }
            public Win2DDeviceManager.GpuAdapterInfo? Gpu { get; init; }
            public string DisplayName => IsSystem
                ? "跟随系统"
                : Gpu?.DisplayName ?? "未知 GPU";
        }

        /// <summary>
        /// 测量所有选项文本的最大宽度，并将 ComboBox 的 MinWidth 设为该宽度 + 下拉菜单额外空间，
        /// 使按钮宽度与弹出菜单宽度一致，避免菜单超出窗口。
        /// </summary>
        private void AdjustGpuComboBoxWidth()
        {
            if (GpuComboBox.Items.Count == 0) return;

            double maxTextWidth = 0;
            var fontFamily = GpuComboBox.FontFamily;
            var fontSize = GpuComboBox.FontSize;
            var fontWeight = GpuComboBox.FontWeight;
            var fontStyle = GpuComboBox.FontStyle;
            var fontStretch = GpuComboBox.FontStretch;

            foreach (var item in GpuComboBox.Items)
            {
                string text = item is GpuSelectionItem gsi ? gsi.DisplayName : item?.ToString() ?? "";
                if (string.IsNullOrEmpty(text)) continue;

                var tb = new TextBlock
                {
                    Text = text,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    FontWeight = fontWeight,
                    FontStyle = fontStyle,
                    FontStretch = fontStretch
                };
                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                maxTextWidth = Math.Max(maxTextWidth, tb.DesiredSize.Width);
            }

            // ComboBox 下拉弹出菜单默认内边距（左右约 12px）+ 箭头区域约 20px + 安全余量
            const double dropdownExtra = 56;
            GpuComboBox.MinWidth = maxTextWidth + dropdownExtra;
        }

        private static void LoadPaths(
            string key,
            ObservableCollection<string> destination)
        {
            try
            {
                JsonNode? node = JsonNode.Parse(
                    File.ReadAllText(MediaScanner.SettingsPath));
                if (node?[key] is not JsonArray paths)
                    return;

                foreach (JsonNode? value in paths)
                {
                    string? path = value?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(path))
                        destination.Add(path);
                }
            }
            catch
            {
            }
        }

        private void SaveSettings()
        {
            try
            {
                JsonNode? node = null;
                if (File.Exists(MediaScanner.SettingsPath))
                    node = JsonNode.Parse(File.ReadAllText(MediaScanner.SettingsPath));
                node ??= new JsonObject();

                node["VideoLibraryPaths"] = ToJsonArray(VideoPaths);
                node["MusicLibraryPaths"] = ToJsonArray(MusicPaths);
                node["ImageLibraryPaths"] = ToJsonArray(ImagePaths);
                node["RecursiveScan"] = VideoRecursiveToggle.IsOn;
                node["MusicRecursiveScan"] = MusicRecursiveToggle.IsOn;
                node["ImageRecursiveScan"] = ImageRecursiveToggle.IsOn;

                Directory.CreateDirectory(
                    Path.GetDirectoryName(MediaScanner.SettingsPath)!);
                File.WriteAllText(
                    MediaScanner.SettingsPath,
                    node.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                    }));
            }
            catch
            {
            }
        }

        private static JsonArray ToJsonArray(
            ObservableCollection<string> paths)
        {
            var result = new JsonArray();
            foreach (string path in paths)
                result.Add(path);
            return result;
        }

        private async Task AddFolderAsync(
            ObservableCollection<string> paths,
            PickerLocationId location)
        {
            var picker = new FolderPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = location
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(
                picker,
                WindowNative.GetWindowHandle(App.MainWindow));

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && !paths.Contains(folder.Path))
            {
                paths.Add(folder.Path);
                SaveSettings();
                AppLogger.Info($"添加媒体库文件夹: {folder.Path}");
            }
            else if (folder != null)
            {
                AppLogger.Warning($"媒体库文件夹已存在, 忽略重复添加: {folder.Path}");
            }
        }

        private async Task ScanAsync(string mediaType, TextBlock status)
        {
            status.Text = "正在扫描...";
            AppLogger.Info($"开始扫描媒体库: {mediaType}");
            try
            {
                var items = await MediaScanner.RefreshLibraryAsync(mediaType);
                status.Text = $"已扫描 {items.Count} 个文件";
                AppLogger.Info($"扫描完成: {mediaType} 共 {items.Count} 个文件");
            }
            catch (Exception ex)
            {
                status.Text = "扫描失败";
                AppLogger.Error(ex, $"扫描媒体库失败: {mediaType}");
            }
        }

        private async void AddVideoFolderButton_Click(object sender, RoutedEventArgs e) =>
            await AddFolderAsync(VideoPaths, PickerLocationId.VideosLibrary);

        private async void AddMusicFolderButton_Click(object sender, RoutedEventArgs e) =>
            await AddFolderAsync(MusicPaths, PickerLocationId.MusicLibrary);

        private async void AddImageFolderButton_Click(object sender, RoutedEventArgs e) =>
            await AddFolderAsync(ImagePaths, PickerLocationId.PicturesLibrary);

        private void RemoveVideoPathButton_Click(object sender, RoutedEventArgs e) =>
            RemovePath(sender, VideoPaths);

        private void RemoveMusicPathButton_Click(object sender, RoutedEventArgs e) =>
            RemovePath(sender, MusicPaths);

        private void RemoveImagePathButton_Click(object sender, RoutedEventArgs e) =>
            RemovePath(sender, ImagePaths);

        private void RemovePath(
            object sender,
            ObservableCollection<string> paths)
        {
            if (sender is Button { Tag: string path })
            {
                paths.Remove(path);
                SaveSettings();
                AppLogger.Info($"移除媒体库文件夹: {path}");
            }
        }

        private void LibrarySetting_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoading)
                SaveSettings();
        }

        /// <summary>GPU 选项卡变更：选中「跟随系统」→ 自动模式；选中 GPU → 手动模式并保存 LUID。</summary>
        private void GpuComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (GpuComboBox.SelectedItem is not GpuSelectionItem item) return;

            if (item.IsSystem)
            {
                App.SettingsHelper.Win2DGpuPreference = Win2DDeviceManager.PreferenceAuto;
                App.SettingsHelper.Win2DGpuAdapterLuid = "";
                AppLogger.Info("Win2D GPU 选择: 跟随系统");
            }
            else if (item.Gpu != null)
            {
                App.SettingsHelper.Win2DGpuPreference = Win2DDeviceManager.PreferenceManual;
                App.SettingsHelper.Win2DGpuAdapterLuid = item.Gpu.AdapterLuid.ToString("X16");
                AppLogger.Info($"Win2D GPU 选择: {item.Gpu.DisplayName} (LUID={item.Gpu.AdapterLuid:X16})");
            }
            App.SettingsHelper.Save();
        }

        private void GpuInfoButton_Click(object sender, RoutedEventArgs e)
        {
            GpuTeachingTip.IsOpen = true;
        }

        private async void ScanVideoButton_Click(object sender, RoutedEventArgs e) =>
            await ScanAsync("Video", VideoStatusText);

        private async void ScanMusicButton_Click(object sender, RoutedEventArgs e) =>
            await ScanAsync("Music", MusicStatusText);

        private async void ScanImageButton_Click(object sender, RoutedEventArgs e) =>
            await ScanAsync("Image", ImageStatusText);
    }
}
