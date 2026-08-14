using SightoHear.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SightoHear.Services
{
    /// <summary>
    /// 音乐库数据的全局静态缓存。
    /// MusicPage 不再永久持有数据，而是通过此类共享，
    /// 确保 MusicPage 重建时无需重新扫描磁盘，且释放时数据不丢失。
    /// </summary>
    public static class MusicDataCache
    {
        private static List<MediaItem>? _allMusic;
        private static List<Playlist>? _allPlaylists;
        private static List<ArtistGroup>? _artistGroups;
        private static List<AlbumGroup>? _albumGroups;
        private static List<FolderGroup>? _folderGroups;

        // ★ 显式初始化标记：区分"setter 显式赋值"和"getter 自动初始化"。
        //   getter 中 ??= 自动创建空列表时不会置此标记，
        //   只有通过 setter 或 MusicPage 等显式赋值后才为 true。
        //   防止 FileActivationService 等外部代码通过 getter 查询缓存时，
        //   意外触发自动初始化导致 IsInitialized 误判为 true。
        private static bool _initialized;

        /// <summary>所有音乐文件，仅由 MusicDataCache 内部延迟填充。</summary>
        public static List<MediaItem> AllMusic
        {
            get
            {
                if (_allMusic == null)
                {
                    // ★ 仅当通过 setter 显式赋值或首次加载后才标记已初始化
                    _allMusic = new List<MediaItem>();
                }
                return _allMusic;
            }
            set
            {
                _allMusic = value;
                _initialized = true; // ★ 显式赋值时标记为已初始化
            }
        }

        public static List<Playlist> AllPlaylists
        {
            get => _allPlaylists ??= new List<Playlist>();
            set
            {
                _allPlaylists = value;
                _initialized = true;
            }
        }

        public static List<ArtistGroup> ArtistGroups
        {
            get
            {
                if (_artistGroups == null && _allMusic != null)
                    _artistGroups = ArtistGroup.BuildFrom(_allMusic);
                return _artistGroups ??= new List<ArtistGroup>();
            }
            set
            {
                _artistGroups = value;
                _initialized = true;
            }
        }

        public static List<AlbumGroup> AlbumGroups
        {
            get
            {
                if (_albumGroups == null && _allMusic != null)
                    _albumGroups = AlbumGroup.BuildFrom(_allMusic);
                return _albumGroups ??= new List<AlbumGroup>();
            }
            set
            {
                _albumGroups = value;
                _initialized = true;
            }
        }

        public static List<FolderGroup> FolderGroups
        {
            get
            {
                if (_folderGroups == null && _allMusic != null)
                    _folderGroups = FolderGroup.BuildFrom(_allMusic);
                return _folderGroups ??= new List<FolderGroup>();
            }
            set
            {
                _folderGroups = value;
                _initialized = true;
            }
        }

        /// <summary>是否已通过显式赋值初始化过数据。</summary>
        /// <remarks>
        /// 注意与 <c>_allMusic != null</c> 的区别：
        /// getter 在 <c>_allMusic</c> 为 null 时会自动初始化为空列表（不会改变此标记），
        /// 但只有 MusicPage 等通过 setter 显式写入数据后，此标记才为 true。
        /// 防止外部代码（如 FileActivationService 查询缓存）意外触发自动初始化导致误判。
        /// </remarks>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// 音乐库数据变更事件（如删除歌曲）。任何页面/服务修改了 AllMusic 等缓存数据后
        /// 应调用 <see cref="NotifyMusicLibraryChanged"/> 通知订阅方（如 MusicPage）刷新 UI。
        /// </summary>
        public static event Action? MusicLibraryChanged;

        /// <summary>通知音乐库数据已变更（删除歌曲等），触发订阅方刷新 UI。</summary>
        public static void NotifyMusicLibraryChanged() => MusicLibraryChanged?.Invoke();

        /// <summary>
        /// 音乐库数据缓存统计（供资源诊断服务输出快照）：
        /// 各列表条目数与是否已初始化。
        /// </summary>
        public static (int Music, int Playlists, int Artists, int Albums, int Folders, bool Initialized) GetCacheStats()
        {
            return (
                _allMusic?.Count ?? 0,
                _allPlaylists?.Count ?? 0,
                _artistGroups?.Count ?? 0,
                _albumGroups?.Count ?? 0,
                _folderGroups?.Count ?? 0,
                _initialized);
        }

        /// <summary>重建派生分组（Artist/Album/Folder）。</summary>
        public static void RebuildDerivedGroups()
        {
            if (_allMusic == null) return;
            _artistGroups = ArtistGroup.BuildFrom(_allMusic);
            _albumGroups = AlbumGroup.BuildFrom(_allMusic);
            _folderGroups = FolderGroup.BuildFrom(_allMusic);
        }

        /// <summary>全部清空（释放引用）。</summary>
        public static void Clear()
        {
            _allMusic = null;
            _allPlaylists = null;
            _artistGroups = null;
            _albumGroups = null;
            _folderGroups = null;
            _initialized = false; // ★ 清空时重置初始化标记
        }

        #region 歌单持久化（原 MusicPage 中的 SavePlaylists / LoadPlaylists）

        private static readonly string PlaylistsFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "playlists.json");

        public static void SavePlaylists()
        {
            try
            {
                var dir = Path.GetDirectoryName(PlaylistsFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var dto = AllPlaylists.Select(p => new PlaylistDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    CoverPath = p.CoverPath,
                    Description = p.Description,
                    DateCreated = p.DateCreated,
                    Items = p.Items.Select(item => new MediaItemDto
                    {
                        FilePath = item.FilePath,
                        Title = item.Title,
                        Artist = item.Artist,
                        Album = item.Album,
                        FileSize = item.FileSize,
                        Duration = item.Duration,
                        DateCreated = item.DateCreated,
                        DateModified = item.DateModified,
                        ThumbnailPath = item.ThumbnailPath,
                        MediaType = item.MediaType,
                        TrackNumber = item.TrackNumber,
                        PixelWidth = item.PixelWidth,
                        PixelHeight = item.PixelHeight
                    }).ToList()
                }).ToList();

                var options = new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = null };
                File.WriteAllText(PlaylistsFilePath, JsonSerializer.Serialize(dto, options));
            }
            catch { }
        }

        public static void LoadPlaylists()
        {
            if (!File.Exists(PlaylistsFilePath))
            {
                _allPlaylists = new List<Playlist>();
                return;
            }

            try
            {
                var json = File.ReadAllText(PlaylistsFilePath);
                var dto = JsonSerializer.Deserialize<List<PlaylistDto>>(json);
                if (dto == null)
                {
                    _allPlaylists = new List<Playlist>();
                    return;
                }

                _allPlaylists = dto.Select(d => new Playlist
                {
                    Id = d.Id,
                    Name = d.Name,
                    CoverPath = d.CoverPath,
                    Description = d.Description,
                    DateCreated = d.DateCreated,
                    Items = d.Items.Select(i => new MediaItem
                    {
                        FilePath = i.FilePath,
                        Title = i.Title,
                        Artist = i.Artist,
                        Album = i.Album,
                        FileSize = i.FileSize,
                        Duration = i.Duration,
                        DateCreated = i.DateCreated,
                        DateModified = i.DateModified,
                        ThumbnailPath = i.ThumbnailPath,
                        MediaType = i.MediaType,
                        TrackNumber = i.TrackNumber,
                        PixelWidth = i.PixelWidth,
                        PixelHeight = i.PixelHeight
                    }).ToList()
                }).ToList();
            }
            catch
            {
                _allPlaylists = new List<Playlist>();
            }
        }

        // 注意：这些 DTO 类与原有 MusicPage 中的一致
        // 如果修改了 MusicPage 中的 DTO，这里也需要同步修改
        private class PlaylistDto
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string CoverPath { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public DateTime DateCreated { get; set; }
            public List<MediaItemDto> Items { get; set; } = new();
        }

        private class MediaItemDto
        {
            public string FilePath { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
            public string Album { get; set; } = string.Empty;
            public long FileSize { get; set; }
            public TimeSpan? Duration { get; set; }
            public DateTime DateCreated { get; set; }
            public DateTime DateModified { get; set; }
            public string ThumbnailPath { get; set; } = string.Empty;
            public string MediaType { get; set; } = string.Empty;
            public uint TrackNumber { get; set; }
            public int PixelWidth { get; set; }
            public int PixelHeight { get; set; }
        }
        #endregion
    }
}
