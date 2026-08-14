using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using SightoHear.Helpers;

namespace SightoHear
{
    public sealed partial class SettingsHomePage : Page
    {
        public SettingsHomePage()
        {
            InitializeComponent();
        }

        private void AppearanceButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("设置页面: 导航到外观设置");
            Frame.Navigate(typeof(AppearancePage), null, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }

        private void BasicButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("设置页面: 导航到基础设置");
            Frame.Navigate(typeof(BasicSettingsPage), null, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }

        private void VideoButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("设置页面: 导航到视频设置");
            Frame.Navigate(typeof(VideoSettingsPage), null, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }

        private void GalleryButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("设置页面: 导航到图库设置");
            Frame.Navigate(typeof(GallerySettingsPage), null, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }

        private void MusicButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("设置页面: 导航到音乐设置");
            Frame.Navigate(typeof(MusicSettingsPage), null, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }

        private void DebugButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("设置页面: 导航到调试设置");
            Frame.Navigate(typeof(DebugPage), null, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("设置页面: 导航到关于页面");
            Frame.Navigate(typeof(AboutPage), null, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }
    }
}
