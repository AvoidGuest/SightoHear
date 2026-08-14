using System;
using System.Collections.Generic;
using System.Linq;

namespace SightoHear.Models
{
    public class AlbumGroup
    {
        public string AlbumName { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public int SongCount { get; set; }
        public int Year { get; set; }
        public string CoverFilePath { get; set; } = string.Empty;

        public string SongCountText => $"{SongCount} 首";

        public string YearText => Year > 0 ? Year.ToString() : "未知年份";

        public string DisplayName => string.IsNullOrWhiteSpace(AlbumName) ? "未知专辑" : AlbumName;
        public string ArtistDisplay => string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;

        public static List<AlbumGroup> BuildFrom(List<MediaItem> music)
        {
            return music
                .GroupBy(m => string.IsNullOrWhiteSpace(m.Album) ? "未知专辑" : m.Album)
                .Select(g =>
                {
                    var first = g.First();
                    return new AlbumGroup
                    {
                        AlbumName = g.Key,
                        Artist = first.ArtistDisplay,
                        SongCount = g.Count(),
                        Year = first.DateCreated.Year,
                        CoverFilePath = first.FilePath
                    };
                })
                .ToList();
        }
    }
}
