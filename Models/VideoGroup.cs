using System.Collections.Generic;

namespace SightoHear.Models
{
    public class VideoGroup
    {
        public string Header { get; set; } = string.Empty;
        public List<MediaItem> Items { get; set; } = new();
    }
}
