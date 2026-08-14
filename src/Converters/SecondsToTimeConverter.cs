using Microsoft.UI.Xaml.Data;
using System;

namespace SightoHear
{
    public sealed partial class SecondsToTimeConverter : IValueConverter
    {
        public object Convert(
            object value,
            Type targetType,
            object parameter,
            string language)
        {
            double seconds = value switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                _ => 0
            };

            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss")
                : time.ToString(@"mm\:ss");
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            string language)
        {
            throw new NotSupportedException();
        }
    }
}
