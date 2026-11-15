using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using SightoHear.Helpers;
using SightoHear.Services;

namespace SightoHear
{
    public sealed partial class SettingsHomePage : Page
    {
        public SettingsHomePage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            RefreshDefaultAppBanner();
        }

        // ==================== 默认应用横幅 ====================

        /// <summary>
        /// 叉号关闭后的冷却期：这段时间内不再打扰用户。
        /// 超过后若本软件仍不是默认应用，横幅会重新出现。
        /// </summary>
        private static readonly TimeSpan BannerDismissCooldown = TimeSpan.FromDays(7);

        /// <summary>
        /// 决定横幅是否显示。判定顺序：
        /// <list type="number">
        /// <item><b>已是默认应用</b>（音乐/照片/视频三类全归本应用）→ 隐藏，且顺带清掉关闭记录，
        ///       这样之后用户一旦把默认应用改回别家，横幅能立刻重新出现。</item>
        /// <item><b>刚点过叉号且在冷却期内</b> → 隐藏（尊重用户"别再烦我"的意愿）。</item>
        /// <item>其余情况 → 显示。</item>
        /// </list>
        /// ★ 关键：叉号只"暂时"收起，不是永久开关。曾经做成永久布尔值，
        ///   导致用户点过一次叉号后再也不是默认应用、横幅却永远不回来（实测踩坑）。
        /// </summary>
        private void RefreshDefaultAppBanner()
        {
            try
            {
                var status = FileActivationService.GetDefaultAppStatus();

                if (status.IsDefault)
                {
                    // ① 三类已全部归本应用：引导没有意义，隐藏；
                    //    并清掉关闭时间，保证"以后被改回别家"时能立即重新提示。
                    DefaultAppBanner.Visibility = Visibility.Collapsed;
                    if (App.SettingsHelper.DefaultAppBannerDismissedAt != DateTime.MinValue)
                    {
                        App.SettingsHelper.DefaultAppBannerDismissedAt = DateTime.MinValue;
                        App.SettingsHelper.Save();
                    }
                    AppLogger.Info("[设置页] 已是默认应用（音乐/照片/视频均归本应用），隐藏横幅");
                    return;
                }

                // ② 冷却期内尊重用户的关闭操作
                var dismissedAt = App.SettingsHelper.DefaultAppBannerDismissedAt;
                if (dismissedAt != DateTime.MinValue)
                {
                    var elapsed = DateTime.UtcNow - dismissedAt;
                    if (elapsed >= TimeSpan.Zero && elapsed < BannerDismissCooldown)
                    {
                        DefaultAppBanner.Visibility = Visibility.Collapsed;
                        AppLogger.Info(
                            $"[设置页] 横幅在冷却期内（已关闭 {elapsed.TotalHours:F1} 小时，冷却 {BannerDismissCooldown.TotalDays:F0} 天）");
                        return;
                    }

                    // 冷却期已过：清掉记录，让横幅自然回归
                    App.SettingsHelper.DefaultAppBannerDismissedAt = DateTime.MinValue;
                    App.SettingsHelper.Save();
                    AppLogger.Info("[设置页] 横幅关闭冷却期已过，重新显示");
                }

                // ③ 显示横幅
                DefaultAppBanner.Visibility = Visibility.Visible;
                DefaultAppTitleText.Text = "将 SightoHear 设为默认应用";
                DefaultAppDescriptionText.Text =
                    "设为默认应用，双击音乐、视频和图片就能直接打开，省去每次选应用";

                AppLogger.Info(
                    $"[设置页] 显示默认应用横幅（音乐={status.MusicDefault} 照片={status.PhotoDefault} 视频={status.VideoDefault}）");
            }
            catch (Exception ex)
            {
                // 状态读取失败时保守地显示横幅（引导用户去设置不会有副作用）
                DefaultAppBanner.Visibility = Visibility.Visible;
                DefaultAppTitleText.Text = "将 SightoHear 设为默认应用";
                DefaultAppDescriptionText.Text =
                    "设为默认应用，双击音乐、视频和图片就能直接打开，省去每次选应用";
                AppLogger.Error(ex, "[设置页] 读取默认应用状态失败");
            }
        }

        private async void SetDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetDefaultButton.IsEnabled = false;
                AppLogger.Info("[设置页] 点击「设为默认」，准备唤起系统默认应用设置");

                bool ok = await FileActivationService.SetAsDefaultAsync();
                if (!ok && XamlRoot != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "无法打开系统设置",
                        Content = "请在 Windows 设置中手动打开「应用 → 默认应用」，选择 SightoHear 进行设置。",
                        CloseButtonText = "知道了"
                    };
                    await DialogService.ShowAsync(dialog, XamlRoot);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "[设置页] 设置默认应用失败");
            }
            finally
            {
                SetDefaultButton.IsEnabled = true;
                // 用户可能已在系统设置里改好，回来按最新状态决定横幅去留
                RefreshDefaultAppBanner();
            }
        }

        /// <summary>
        /// 点叉号：记录关闭时间，本次隐藏横幅。
        /// 注意这只是"暂时收起"——冷却期（7 天）过后若本软件仍不是默认应用，横幅会重新出现。
        /// </summary>
        private void DismissBannerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                App.SettingsHelper.DefaultAppBannerDismissedAt = DateTime.UtcNow;
                App.SettingsHelper.Save();
                DefaultAppBanner.Visibility = Visibility.Collapsed;
                AppLogger.Info(
                    $"[设置页] 用户关闭了默认应用横幅，{BannerDismissCooldown.TotalDays:F0} 天内不再提示");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "[设置页] 关闭默认应用横幅失败");
            }
        }

        // ==================== 分类导航 ====================

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
