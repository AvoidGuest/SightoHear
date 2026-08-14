using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;

namespace SightoHear.Models
{
    /// <summary>
    /// 图库的"一行"，是 ItemsRepeater 的最小虚拟化单元。
    /// 仅该组第一行才显示日期标题 Header。
    /// </summary>
    public sealed class GalleryRow
    {
        public string Header { get; init; } = string.Empty;
        public IReadOnlyList<MediaItem> Items { get; init; } =
            Array.Empty<MediaItem>();
        public Visibility HeaderVisibility =>
            string.IsNullOrEmpty(Header)
                ? Visibility.Collapsed
                : Visibility.Visible;
    }
}
