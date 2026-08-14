using System.Collections.Generic;
using System.Linq;

namespace SightoHear.Models
{
    public class ArtistGroup
    {
        public string ArtistName { get; set; } = string.Empty;
        public int SongCount { get; set; }
        public int AlbumCount { get; set; }
        public string CoverFilePath { get; set; } = string.Empty;

        public string SongCountText => $"{SongCount} 首歌";

        public string AlbumCountText => $"{AlbumCount} 张专辑";

        public string DisplayName => string.IsNullOrWhiteSpace(ArtistName) ? "未知艺术家" : ArtistName;

        public static List<ArtistGroup> BuildFrom(List<MediaItem> music)
        {
            return music
                .GroupBy(m => string.IsNullOrWhiteSpace(m.Artist) ? "未知艺术家" : m.Artist)
                .Select(g => new ArtistGroup
                {
                    ArtistName = g.Key,
                    SongCount = g.Count(),
                    AlbumCount = g.Select(m => m.Album).Distinct().Count(),
                    CoverFilePath = g.First().FilePath
                })
                .ToList();
        }
    }
}
