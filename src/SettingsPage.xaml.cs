using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using SightoHear.Helpers;

namespace SightoHear
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
            SettingsContentFrame.Navigated += SettingsContentFrame_Navigated;
            SettingsContentFrame.Navigate(typeof(SettingsHomePage));
            AppLogger.Info("设置页打开");
        }

        /// <summary>
        /// ★ 支持导航参数直达子页面：外部（如播放器"视频设置"超链接）可通过
        /// <c>ContentFrame.Navigate(typeof(SettingsPage), typeof(VideoSettingsPage))</c>
        /// 跳转到设置页并直接打开指定的子设置页。
        /// </summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is Type pageType &&
                typeof(Microsoft.UI.Xaml.Controls.Page).IsAssignableFrom(pageType))
            {
                AppLogger.Info($"设置页直达子页面: {pageType.Name}");
                // 延迟到布局完成后导航，避免与构造函数中的 SettingsHomePage 导航交错
                DispatcherQueue.TryEnqueue(() => SettingsContentFrame.Navigate(pageType));
            }
        }

        private void SettingsContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            if (SettingsContentFrame.CanGoBack)
            {
                // 三级面包屑：
                //   一级子页面（视频/音乐/...）→ 设置 > 页面名
                //   二级子页面（视频快捷键等）→ 设置 > 父级 > 页面名
                BreadcrumbParentText.Text = "设置";
                BreadcrumbMiddle.Visibility = Visibility.Collapsed;
                BreadcrumbArrow2.Visibility = Visibility.Collapsed;

                if (e.SourcePageType == typeof(VideoShortcutSettingsPage))
                {
                    // 二级页：设置 > 视频 > 快捷键
                    BreadcrumbMiddleText.Text = "视频";
                    BreadcrumbMiddle.Visibility = Visibility.Visible;
                    BreadcrumbArrow2.Visibility = Visibility.Visible;
                    BreadcrumbCurrent.Text = "快捷键";
                }
                else
                {
                    // 一级页：设置 > 页面名
                    if (e.SourcePageType == typeof(BasicSettingsPage))
                        BreadcrumbCurrent.Text = "基础";
                    else if (e.SourcePageType == typeof(AppearancePage))
                        BreadcrumbCurrent.Text = "外观";
                    else if (e.SourcePageType == typeof(VideoSettingsPage))
                        BreadcrumbCurrent.Text = "视频";
                    else if (e.SourcePageType == typeof(MusicSettingsPage))
                        BreadcrumbCurrent.Text = "音乐";
                    else if (e.SourcePageType == typeof(GallerySettingsPage))
                        BreadcrumbCurrent.Text = "图库";
                    else if (e.SourcePageType == typeof(DebugPage))
                        BreadcrumbCurrent.Text = "调试";
                    else if (e.SourcePageType == typeof(AboutPage))
                        BreadcrumbCurrent.Text = "关于";
                    else
                        BreadcrumbCurrent.Text = string.Empty;
                }

                TitlePanel.Visibility = Visibility.Collapsed;
                BreadcrumbPanel.Visibility = Visibility.Visible;
                AppLogger.Info($"设置子页面导航: {BreadcrumbCurrent.Text}");
            }
            else
            {
                TitlePanel.Visibility = Visibility.Visible;
                BreadcrumbPanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>点击"设置"：一路返回到设置首页（二级页时先返回一级再返回首页）。</summary>
        private void BreadcrumbParent_Click(object sender, RoutedEventArgs e)
        {
            while (SettingsContentFrame.CurrentSourcePageType != typeof(SettingsHomePage)
                && SettingsContentFrame.CanGoBack)
            {
                SettingsContentFrame.GoBack();
            }
        }

        /// <summary>点击中间级（如"视频"）：返回到对应的上级设置页面（导航栈的上一页）。</summary>
        private void BreadcrumbMiddle_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsContentFrame.CanGoBack)
                SettingsContentFrame.GoBack();
        }

        public bool CanGoBack => SettingsContentFrame.CanGoBack;

        public void GoBack()
        {
            if (SettingsContentFrame.CanGoBack)
                SettingsContentFrame.GoBack();
        }
    }
}
