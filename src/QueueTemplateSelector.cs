using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SightoHear.Models;

namespace SightoHear;

public partial class QueueTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultTemplate { get; set; }
    public DataTemplate? NowPlayingTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        return item is MediaItem media && media == App.MusicPlayback.ActiveItem
            ? NowPlayingTemplate
            : DefaultTemplate;
    }
}
