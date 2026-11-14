using System;
using System.Collections.Generic;

namespace SightoHear.Services
{
    /// <summary>
    /// 歌单磁盘缓存 DTO（playlists.json）。
    /// 与 <see cref="Models.Playlist"/> 分开定义的原因：缓存只保存精简字段，
    /// 不写入 Id/封面等可由扫描重建的信息。
    ///
    /// ★ 必须为 public/internal 顶层类型（不能是 MusicDataCache 的私有嵌套类），
    /// 否则 System.Text.Json 源生成器无法在编译期为其生成读写器，
    /// Native AOT 下反序列化会静默失败。新增字段后无需改 AppJsonContext（类型已登记）。
    /// </summary>
    internal sealed class PlaylistCacheDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string CoverPath { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime DateCreated { get; set; }
        public List<MediaItemCacheDto> Items { get; set; } = new();
    }

    /// <summary>
    /// 歌单缓存中的媒体项 DTO（精简版 <see cref="Models.MediaItem"/>）。
    /// </summary>
    internal sealed class MediaItemCacheDto
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
}
