using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SightoHear.Models;
using SightoHear.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 音乐卡片右键菜单与播放器信息右键菜单的公共构建辅助类：
    /// 音乐库页面与音乐播放器页面共享同一套菜单项（查看封面 / 复制 / 使用其他应用打开 /
    /// 打开文件所在位置 / 添加到歌单 / 删除），保证两处行为一致，避免重复实现。
    /// </summary>
    public static class MusicItemMenuHelper
    {
        private const string SegoeFluentIconsFont =
            "ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons";

        // ==================== 菜单项构建（页面共用） ====================

        /// <summary>"查看封面"菜单项：在软件内置图片查看器中查看封面大图。</summary>
        public static MenuFlyoutItem BuildViewCoverMenuItem(MediaItem item, XamlRoot xamlRoot)
        {
            var menuItem = new MenuFlyoutItem
            {
                Text = "查看封面",
                Icon = new FontIcon { FontFamily = new FontFamily(SegoeFluentIconsFont), Glyph = "\uE8B9" }
            };
            menuItem.Click += async (_, _) => await ViewCoverAsync(item, xamlRoot);
            return menuItem;
        }

        /// <summary>"复制"二级菜单：复制标题 / 复制歌手 / 复制专辑 / 全部复制。</summary>
        public static MenuFlyoutSubItem BuildCopySubMenu(MediaItem item)
        {
            var copyMenu = new MenuFlyoutSubItem
            {
                Text = "复制",
                Icon = new FontIcon { FontFamily = new FontFamily(SegoeFluentIconsFont), Glyph = "\uE8C8" }
            };
            // 与播放器界面显示一致：标题为空时回退到文件名
            string title = string.IsNullOrWhiteSpace(item.Title) ? item.FileName : item.Title;

            var copyTitle = new MenuFlyoutItem { Text = "复制标题" };
            copyTitle.Click += (_, _) => CopyTextToClipboard(title);
            copyMenu.Items.Add(copyTitle);

            var copyArtist = new MenuFlyoutItem { Text = "复制歌手" };
            copyArtist.Click += (_, _) => CopyTextToClipboard(item.ArtistDisplay);
            copyMenu.Items.Add(copyArtist);

            var copyAlbum = new MenuFlyoutItem { Text = "复制专辑" };
            copyAlbum.Click += (_, _) => CopyTextToClipboard(item.AlbumDisplay);
            copyMenu.Items.Add(copyAlbum);

            var copyAll = new MenuFlyoutItem { Text = "全部复制" };
            copyAll.Click += (_, _) =>
                CopyTextToClipboard($"标题：{title}\n歌手：{item.ArtistDisplay}\n专辑：{item.AlbumDisplay}");
            copyMenu.Items.Add(copyAll);

            return copyMenu;
        }

        /// <summary>"使用其他应用打开"菜单项。</summary>
        public static MenuFlyoutItem BuildOpenWithMenuItem(MediaItem item)
        {
            var menuItem = new MenuFlyoutItem
            {
                Text = "使用其他应用打开",
                Icon = new FontIcon { FontFamily = new FontFamily(SegoeFluentIconsFont), Glyph = "\uE8A7" }
            };
            menuItem.Click += async (_, _) => await OpenWithExternalAsync(item);
            return menuItem;
        }

        /// <summary>"打开文件所在位置"菜单项。</summary>
        public static MenuFlyoutItem BuildOpenLocationMenuItem(MediaItem item)
        {
            var menuItem = new MenuFlyoutItem
            {
                Text = "打开文件所在位置",
                Icon = new FontIcon { FontFamily = new FontFamily(SegoeFluentIconsFont), Glyph = "\uED25" }
            };
            menuItem.Click += (_, _) => OpenFileLocation(item);
            return menuItem;
        }

        /// <summary>"添加到歌单"二级菜单：从全局歌单缓存填充，已添加的歌单禁用并标注。</summary>
        /// <param name="onAdded">添加成功后的页面级回调（如刷新歌单列表 UI）。</param>
        public static MenuFlyoutSubItem BuildAddToPlaylistMenuItem(MediaItem item, Action? onAdded = null)
        {
            var addMenu = new MenuFlyoutSubItem
            {
                Text = "添加到歌单",
                Icon = new FontIcon { FontFamily = new FontFamily(SegoeFluentIconsFont), Glyph = "\uE109" }
            };

            var playlists = MusicDataCache.AllPlaylists;
            if (playlists.Count == 0)
            {
                addMenu.Items.Add(new MenuFlyoutItem { Text = "暂无歌单", IsEnabled = false });
                return addMenu;
            }

            foreach (var playlist in playlists)
            {
                bool alreadyAdded = playlist.Items.Any(song =>
                    string.Equals(song.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));

                var playlistItem = new MenuFlyoutItem
                {
                    Text = alreadyAdded ? $"{playlist.Name}（已添加）" : playlist.Name,
                    IsEnabled = !alreadyAdded
                };
                playlistItem.Click += (_, _) =>
                {
                    playlist.Items.Add(item);
                    MusicDataCache.SavePlaylists();
                    // 通知音乐库数据变更（音乐页面据此刷新歌单列表）
                    MusicDataCache.NotifyMusicLibraryChanged();
                    onAdded?.Invoke();
                };
                addMenu.Items.Add(playlistItem);
            }
            return addMenu;
        }

        /// <summary>"删除"菜单项：确认弹窗 + 磁盘删除 + 音乐库数据同步。</summary>
        /// <param name="afterDeleted">删除成功后的页面级回调（如刷新列表 UI）。</param>
        public static MenuFlyoutItem BuildDeleteMenuItem(
            MediaItem item, XamlRoot xamlRoot, Action<MediaItem>? afterDeleted = null)
        {
            var menuItem = new MenuFlyoutItem
            {
                Text = "删除",
                Icon = new FontIcon { FontFamily = new FontFamily(SegoeFluentIconsFont), Glyph = "\uE107" }
            };
            menuItem.Click += async (_, _) => await DeleteMusicItemAsync(item, xamlRoot, afterDeleted);
            return menuItem;
        }

        // ==================== 封面路径解析（播放器页面复用） ====================

        /// <summary>解析歌曲展示封面路径（优先背景封面 → 列表封面 → 缩略图）。</summary>
        public static string ResolveCoverPath(MediaItem? item)
        {
            if (item == null)
                return string.Empty;

            string backgroundCover = MusicCoverService.GetOrCreateBackground(item.FilePath);
            if (!string.IsNullOrWhiteSpace(backgroundCover) && File.Exists(backgroundCover))
                return backgroundCover;

            string cover = MusicCoverService.GetOrCreate(item.FilePath);
            if (!string.IsNullOrWhiteSpace(cover) && File.Exists(cover))
                return cover;

            return !string.IsNullOrWhiteSpace(item.ThumbnailPath) && File.Exists(item.ThumbnailPath)
                ? item.ThumbnailPath
                : string.Empty;
        }

        /// <summary>解析封面原图路径（用于查看封面大图），回退到展示封面路径。</summary>
        public static string ResolveDisplayCoverPath(MediaItem? item)
        {
            if (item == null)
                return string.Empty;

            string original = MusicCoverService.GetOrCreateOriginal(item.FilePath);
            if (!string.IsNullOrWhiteSpace(original) && File.Exists(original))
                return original;

            return ResolveCoverPath(item);
        }

        // ==================== 文件操作（页面共用） ====================

        /// <summary>在资源管理器中定位并选中文件。</summary>
        public static void OpenFileLocation(MediaItem item)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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

        /// <summary>
        /// 从磁盘删除音乐文件：根据「删除文件时移入回收站」设置决定移入回收站或永久删除。
        /// 若删除的是当前正在播放的歌曲，先停止播放再删除（避免文件占用导致删除失败）。
        /// </summary>
        public static void DeleteMusicFilesFromDisk(IEnumerable<MediaItem> items)
        {
            foreach (var item in items)
            {
                try
                {
                    if (item == null || string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
                        continue;

                    // 删除正在播放的歌曲前先停止播放，释放文件占用
                    var activeItem = App.MusicPlayback.ActiveItem;
                    if (activeItem != null &&
                        string.Equals(activeItem.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        App.MusicPlayback.StopPlayback();
                    }

                    if (App.SettingsHelper.DeleteToRecycleBin)
                        RecycleBinHelper.DeleteToRecycleBin(item.FilePath);
                    else
                        File.Delete(item.FilePath);

                    // 清理缩略图内存缓存，避免残留位图引用
                    ImageThumbnailService.Remove(item.FilePath);
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"删除音乐文件失败: {item.FilePath}");
                }
            }
        }

        // ==================== 内部实现 ====================

        /// <summary>在软件内置图片查看器中查看封面大图。</summary>
        private static async Task ViewCoverAsync(MediaItem item, XamlRoot xamlRoot)
        {
            string coverPath = ResolveDisplayCoverPath(item);
            if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath))
            {
                var dialog = new ContentDialog
                {
                    Title = "查看封面",
                    Content = "当前曲目没有可查看的封面。",
                    CloseButtonText = "确定",
                    XamlRoot = xamlRoot
                };
                await DialogService.ShowAsync(dialog, xamlRoot);
                return;
            }

            // 把封面图片包装为媒体项交给内置图片查看器（封面文件为缓存提取的原图）
            var coverItem = new MediaItem
            {
                FilePath = coverPath,
                Title = string.IsNullOrWhiteSpace(item.Title) ? "封面" : $"{item.Title} - 封面",
                Artist = item.Artist,
                Album = item.Album
            };
            (App.MainWindow as MainWindow)?.OpenImageViewer(new ImageViewerArgs
            {
                Playlist = new List<MediaItem> { coverItem },
                StartIndex = 0,
                // 查看封面属于临时预览，不记录到主页"上次打开"，避免覆盖正在播放的音乐记录
                SkipLastOpenedRecording = true
            });
        }

        /// <summary>将文本复制到系统剪贴板。</summary>
        private static void CopyTextToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            try
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(text);
                Clipboard.SetContent(dataPackage);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "复制文本到剪贴板失败");
            }
        }

        /// <summary>使用系统"打开方式"选择器以其他应用打开文件。</summary>
        private static async Task OpenWithExternalAsync(MediaItem item)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                await Launcher.LaunchFileAsync(file, new LauncherOptions
                {
                    DisplayApplicationPicker = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "选择音乐打开方式");
            }
        }

        /// <summary>删除确认流程：确认弹窗 → 磁盘删除 → 库数据同步 → 页面回调。</summary>
        private static async Task DeleteMusicItemAsync(
            MediaItem item, XamlRoot xamlRoot, Action<MediaItem>? afterDeleted)
        {
            var dialog = new ContentDialog
            {
                Title = "删除确认",
                Content = App.SettingsHelper.DeleteToRecycleBin
                    ? $"确定要将「{item.Title}」移入到回收站吗？可随时还原。"
                    : $"确定要删除本地磁盘文件「{item.Title}」吗？此操作不可撤销，无法反悔。",
                PrimaryButtonText = App.SettingsHelper.DeleteToRecycleBin ? "移入回收站" : "删除",
                CloseButtonText = "取消",
                XamlRoot = xamlRoot
            };

            var result = await DialogService.ShowAsync(dialog, xamlRoot, isFileDelete: true);
            if (result != ContentDialogResult.Primary)
                return;

            // 从磁盘删除（移入回收站或永久删除，取决于设置）
            DeleteMusicFilesFromDisk(new[] { item });

            // 同步音乐库缓存数据并通知页面刷新
            MusicDataCache.AllMusic.Remove(item);
            await Task.Run(() => MediaScanner.SaveToCache(MusicDataCache.AllMusic, "Music"));
            MusicDataCache.NotifyMusicLibraryChanged();

            afterDeleted?.Invoke(item);
        }
    }
}
