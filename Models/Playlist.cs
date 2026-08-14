using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SightoHear.Models
{
    public class Playlist
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string CoverPath { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<MediaItem> Items { get; set; } = new();
        public DateTime DateCreated { get; set; } = DateTime.Now;

        [JsonIgnore]
        public string SongCountText => $"{Items.Count} 首";

        [JsonIgnore]
        public string CoverDisplayPath
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(CoverPath))
                    return CoverPath;

                // 回退逻辑：优先使用最后一个视频的缩略图，再回退到文件路径
                var lastItem = Items.LastOrDefault();
                if (lastItem != null)
                {
                    if (!string.IsNullOrWhiteSpace(lastItem.ThumbnailPath))
                        return lastItem.ThumbnailPath;
                    return lastItem.FilePath;
                }

                return string.Empty;
            }
        }
    }
}
