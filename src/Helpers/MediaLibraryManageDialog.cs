using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SightoHear.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 「媒体库管理」弹窗：在视频/音乐/图库页面右上角快速管理当前媒体库的文件夹。
    /// 功能与设置页「基础设置 → 数据库」共用同一份配置，额外支持勾选要展示的文件夹：
    /// - 勾选/取消勾选：控制该文件夹内容是否在当前页面展示（即时生效并持久化）
    /// - 添加文件夹：通过系统文件夹选择器加入媒体库，添加后自动后台扫描
    /// - 移除文件夹：从媒体库路径中移除
    /// - 全选/取消全选：一键切换展示范围
    /// </summary>
    public static class MediaLibraryManageDialog
    {
        /// <summary>
        /// 显示媒体库管理弹窗。
        /// </summary>
        /// <param name="xamlRoot">当前页面的 XamlRoot</param>
        /// <param name="mediaType">媒体类型（Video/Music/Image）</param>
        public static async Task ShowAsync(XamlRoot xamlRoot, string mediaType)
        {
            string typeName = mediaType switch
            {
                "Video" => "视频",
                "Music" => "音乐",
                "Image" => "图库",
                _ => mediaType
            };
            PickerLocationId location = mediaType switch
            {
                "Video" => PickerLocationId.VideosLibrary,
                "Music" => PickerLocationId.MusicLibrary,
                "Image" => PickerLocationId.PicturesLibrary,
                _ => PickerLocationId.ComputerFolder
            };

            // 当前库路径 + 启用（勾选）状态
            List<string> libraryPaths = MediaLibraryFolderManager.GetLibraryPaths(mediaType);
            List<string> enabled = MediaLibraryFolderManager.GetEnabledFolders(mediaType);

            // 状态说明文本
            var statusText = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.72,
                Margin = new Thickness(0, 0, 0, 4)
            };

            // 空状态文本
            var emptyText = new TextBlock
            {
                Text = "尚未添加任何文件夹，点击下方「添加文件夹」加入媒体库",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 24),
                Opacity = 0.72
            };

            void UpdateStatus()
            {
                statusText.Text =
                    $"勾选要展示的文件夹，未勾选的文件夹内容将不在本页显示。当前展示 {enabled.Count}/{libraryPaths.Count} 个文件夹。";
                emptyText.Visibility = libraryPaths.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            // 文件夹列表容器（手动构建行，文件夹数量通常较少，无需虚拟化）
            var listContainer = new StackPanel { Spacing = 2 };

            void RebuildList()
            {
                listContainer.Children.Clear();
                foreach (string path in libraryPaths)
                {
                    bool isChecked = enabled.Contains(path, StringComparer.OrdinalIgnoreCase);

                    var row = new Grid
                    {
                        ColumnSpacing = 8,
                        Margin = new Thickness(0, 4, 0, 4)
                    };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var checkBox = new CheckBox
                    {
                        IsChecked = isChecked,
                        MinWidth = 0,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(checkBox, 0);
                    row.Children.Add(checkBox);

                    var pathText = new TextBlock
                    {
                        Text = path,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(pathText, 1);
                    row.Children.Add(pathText);

                    Button removeButton;
                    removeButton = new Button
                    {
                        Content = new FontIcon
                        {
                            Glyph = "\uE711",
                            FontSize = 14,
                            FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons")
                        },
                        Padding = new Thickness(8, 4, 8, 4),
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    ToolTipService.SetToolTip(removeButton, "移除该文件夹");
                    Grid.SetColumn(removeButton, 2);
                    row.Children.Add(removeButton);

                    // 勾选 → 即时保存并通知页面刷新
                    checkBox.Checked += (_, _) => SetEnabled(path, true);
                    checkBox.Unchecked += (_, _) => SetEnabled(path, false);

                    removeButton.Click += (_, _) =>
                    {
                        MediaLibraryFolderManager.RemoveLibraryFolder(mediaType, path);
                        // 重新读取库路径与启用状态（移除时可能同步清理了启用列表）
                        libraryPaths = MediaLibraryFolderManager.GetLibraryPaths(mediaType);
                        enabled = MediaLibraryFolderManager.GetEnabledFolders(mediaType);
                        RebuildList();
                        UpdateStatus();
                    };

                    listContainer.Children.Add(row);
                }
                UpdateStatus();
            }

            void SetEnabled(string path, bool isOn)
            {
                if (isOn)
                {
                    if (!enabled.Contains(path, StringComparer.OrdinalIgnoreCase))
                        enabled.Add(path);
                }
                else
                {
                    enabled.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                }
                MediaLibraryFolderManager.SetEnabledFolders(mediaType, enabled);
                UpdateStatus();
            }

            // 添加文件夹按钮
            var addButton = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new FontIcon
                        {
                            Glyph = "\uE710",
                            FontSize = 12,
                            FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons")
                        },
                        new TextBlock { Text = "添加文件夹" }
                    }
                }
            };
            addButton.Click += async (_, _) =>
            {
                var picker = new FolderPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = location
                };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

                var folder = await picker.PickSingleFolderAsync();
                if (folder == null)
                    return;

                string newPath = folder.Path;
                if (libraryPaths.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                {
                    // 已存在：自动勾选，方便用户直接看到内容
                    if (!enabled.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                    {
                        enabled.Add(newPath);
                        MediaLibraryFolderManager.SetEnabledFolders(mediaType, enabled);
                        RebuildList();
                        UpdateStatus();
                    }
                    return;
                }

                // 添加到库路径（与设置页「数据库」共用）
                MediaLibraryFolderManager.AddLibraryFolder(mediaType, newPath);
                // 默认勾选新文件夹（添加后即可展示）
                enabled.Add(newPath);
                MediaLibraryFolderManager.SetEnabledFolders(mediaType, enabled);

                libraryPaths = MediaLibraryFolderManager.GetLibraryPaths(mediaType);
                RebuildList();
                UpdateStatus();

                // 后台扫描新文件夹内容（完成后页面通过 CacheUpdated 自动刷新）
                _ = ScanFolderAsync(mediaType, newPath);
            };

            // 全选/取消全选按钮
            var selectAllButton = new Button { Content = "全选" };
            selectAllButton.Click += (_, _) =>
            {
                bool anyUnselected = libraryPaths.Any(
                    p => !enabled.Contains(p, StringComparer.OrdinalIgnoreCase));
                enabled = anyUnselected
                    ? new List<string>(libraryPaths)
                    : new List<string>();
                MediaLibraryFolderManager.SetEnabledFolders(mediaType, enabled);
                selectAllButton.Content = anyUnselected ? "取消全选" : "全选";
                RebuildList();
                UpdateStatus();
            };

            // 底部操作行
            var actionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0)
            };
            actionRow.Children.Add(addButton);
            actionRow.Children.Add(selectAllButton);

            // 弹窗根布局
            var root = new StackPanel
            {
                Width = 440,
                Spacing = 4,
                Padding = new Thickness(4, 0, 4, 0)
            };
            root.Children.Add(statusText);

            // 列表区域：ScrollViewer（文件夹列表）+ 空状态文本（列表为空时显示）
            var listHost = new Grid();
            var scrollViewer = new ScrollViewer
            {
                Content = listContainer,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            listHost.Children.Add(scrollViewer);
            listHost.Children.Add(emptyText);
            root.Children.Add(listHost);
            root.Children.Add(actionRow);

            RebuildList();

            var dialog = new ContentDialog
            {
                Title = $"{typeName}媒体库管理",
                Content = root,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            await DialogService.ShowAsync(dialog, xamlRoot);
        }

        /// <summary>后台刷新指定媒体类型的媒体库（添加新文件夹后调用），完成后通知页面刷新。</summary>
        private static async Task ScanFolderAsync(string mediaType, string path)
        {
            try
            {
                AppLogger.Info($"媒体库管理弹窗: 开始扫描新增文件夹 {path}");
                var items = await MediaScanner.RefreshLibraryAsync(mediaType);
                AppLogger.Info($"媒体库管理弹窗: 扫描完成 {path}，共 {items.Count} 个文件");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"媒体库管理弹窗扫描失败: {mediaType} {path}");
            }

            // 扫描完成后显式通知页面重新加载（缓存已更新，页面即可展示新内容）
            MediaLibraryFolderManager.TriggerRefresh(mediaType);
        }
    }
}
