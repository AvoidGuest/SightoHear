using System.Collections.Generic;

namespace SightoHear.Models
{
    public class MusicPlayerArgs
    {
        public MediaItem? CurrentItem { get; set; }
        public List<MediaItem> Playlist { get; set; } = new();
        public int CurrentIndex { get; set; } = -1;
    }
}
