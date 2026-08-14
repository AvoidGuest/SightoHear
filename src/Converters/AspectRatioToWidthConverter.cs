using Microsoft.UI.Xaml.Data;
using System;

namespace SightoHear
{
    /// <summary>
    /// 将 MediaItem.AspectRatio 转换为固定高度下的自适应宽度。
    /// ItemHeight 在 XAML 实例化时通过属性设置（默认 130）。
    /// 公式：Width = AspectRatio × ItemHeight
    /// </summary>
    public partial class AspectRatioToWidthConverter : IValueConverter
    {
        /// <summary>
        /// 缩略图的固定高度（像素）。Width = AspectRatio × ItemHeight。
        /// </summary>
        public double ItemHeight { get; set; } = 130.0;

        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double aspectRatio && aspectRatio > 0)
            {
                double width = aspectRatio * ItemHeight;
                // 保证最小宽度 40px，最大宽度 300px，防止极端比例
                return Math.Max(40.0, Math.Min(300.0, width));
            }
            return ItemHeight; // 默认 1:1
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}