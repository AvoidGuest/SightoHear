using System;
using System.IO;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SightoHear.Models
{
    public class MediaItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime DateCreated { get; set; }
        public DateTime DateModified { get; set; }
        public DateTime DateScanned { get; set; }
        public TimeSpan? Duration { get; set; }
        public string ThumbnailPath { get; set; } = string.Empty;
        public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailPath);
        public string MediaType { get; set; } = string.Empty; // "Video", "Music", "Image"
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public uint TrackNumber { get; set; }
        public bool MusicMetadataScanned { get; set; }

        // 图片原始像素尺寸 / 视频分辨率（LinedFlowLayout 需要宽高比数据）
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }

        // 视频帧率（fps），仅视频类型有效
        public double? FrameRate { get; set; }

        // 视频编码格式，仅视频类型有效
        public string VideoCodec { get; set; } = string.Empty;

        // 宽高比：宽度 / 高度，LinedFlowLayout 通过此值自动计算卡片宽度
        [JsonIgnore]
        public double AspectRatio => PixelHeight > 0 ? (double)PixelWidth / PixelHeight : 1.0;

        [JsonIgnore]
        public double GalleryCardHeight => SightoHear.App.SettingsHelper.GalleryThumbnailHeight;

        [JsonIgnore]
        public double GalleryCardWidth =>
            GalleryCardHeight * Math.Clamp(AspectRatio, 0.5, 3.2);

        /// <summary>
        /// 卡片信息栏可见性（图库设置：卡片显示图片信息）。
        /// </summary>
        [JsonIgnore]
        public Microsoft.UI.Xaml.Visibility GalleryInfoVisibility =>
            SightoHear.App.SettingsHelper.GalleryShowImageInfo
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;

        [JsonIgnore]
        public BitmapImage? ThumbnailBitmap => null;

        [JsonIgnore]
        public string ArtistDisplay => string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;

        /// <summary>
        /// 列表缩略图使用的封面路径（统一视频/音乐/图片的显示逻辑）。
        /// 视频优先使用扫描时生成的缩略图（jpg），避免把视频文件本身交给图片解码器；
        /// 音乐/图片直接返回文件路径，由转换器分别走内嵌封面提取与图片解码。
        /// </summary>
        [JsonIgnore]
        public string CoverDisplayPath
        {
            get
            {
                if (string.Equals(MediaType, "Video", StringComparison.OrdinalIgnoreCase))
                    return !string.IsNullOrEmpty(ThumbnailPath) ? ThumbnailPath : FilePath;
                return FilePath;
            }
        }

        [JsonIgnore]
        public string AlbumDisplay => string.IsNullOrWhiteSpace(Album) ? "未知专辑" : Album;

        [JsonIgnore]
        public string DurationText => Duration.HasValue
            ? Duration.Value.TotalHours >= 1
                ? Duration.Value.ToString(@"h\:mm\:ss")
                : Duration.Value.ToString(@"m\:ss")
            : "--:--";

        [JsonIgnore]
        public string FileSizeText
        {
            get
            {
                string[] units = { "B", "KB", "MB", "GB", "TB" };
                double size = FileSize;
                int unit = 0;
                while (size >= 1024 && unit < units.Length - 1)
                {
                    size /= 1024;
                    unit++;
                }

                return $"{size:0.##} {units[unit]}";
            }
        }

        /// <summary>
        /// 带扩展名的完整文件名（仅图片/视频场景）
        /// </summary>
        [JsonIgnore]
        public string FileNameFull => System.IO.Path.GetFileName(FilePath);

        /// <summary>
        /// 图片信息文字，如 "2.5 MB · 1920×1080"
        /// </summary>
        [JsonIgnore]
        public string ImageInfoText
        {
            get
            {
                string size = FileSizeText;
                string resolution = PixelWidth > 0 && PixelHeight > 0
                    ? $"{PixelWidth}×{PixelHeight}"
                    : string.Empty;
                return string.IsNullOrEmpty(resolution)
                    ? size
                    : $"{size} · {resolution}";
            }
        }

        /// <summary>
        /// 视频分辨率文字，如 "1920×1080"
        /// </summary>
        [JsonIgnore]
        public string VideoResolutionText => PixelWidth > 0 && PixelHeight > 0
            ? $"{PixelWidth}×{PixelHeight}"
            : string.Empty;

        /// <summary>
        /// 视频帧率文字，如 "30 fps"、"23.976 fps"
        /// </summary>
        [JsonIgnore]
        public string FrameRateText => FrameRate.HasValue
            ? $"{FrameRate.Value:F3}".TrimEnd('0').TrimEnd('.') + " fps"
            : string.Empty;

        /// <summary>
        /// 视频详细信息文字，如 "1920×1080 · 30 fps"
        /// </summary>
        [JsonIgnore]
        public string VideoInfoText
        {
            get
            {
                string resolution = VideoResolutionText;
                string frameRate = FrameRateText;
                if (!string.IsNullOrEmpty(resolution) && !string.IsNullOrEmpty(frameRate))
                    return $"{resolution} · {frameRate}";
                if (!string.IsNullOrEmpty(resolution))
                    return resolution;
                if (!string.IsNullOrEmpty(frameRate))
                    return frameRate;
                return string.Empty;
            }
        }
    }
}
