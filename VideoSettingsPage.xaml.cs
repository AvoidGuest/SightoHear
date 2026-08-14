using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using SightoHear.Helpers;
using System;

namespace SightoHear
{
    public sealed partial class VideoSettingsPage : Page
    {
        private bool _isLoading = true;

        public VideoSettingsPage()
        {
            InitializeComponent();
            RememberPositionToggle.IsOn = App.SettingsHelper.RememberVideoPosition;
            AutoPlayNextToggle.IsOn = App.SettingsHelper.AutoPlayNextVideo;
            AutoPlayToggle.IsOn = App.SettingsHelper.AutoPlayVideo;
            BackgroundPlayToggle.IsOn = App.SettingsHelper.BackgroundPlayVideo;
            DecoderBackendComboBox.SelectedIndex =
                App.SettingsHelper.VideoDecoderBackend == "System" ? 1 : 0;
            DecodeModeComboBox.SelectedIndex =
                App.SettingsHelper.VideoDecodeMode == "Software" ? 1 : 0;

            // x86（32 位）平台不支持 libmpv（Endpne.LibMPV.Windows 仅提供 x64/ARM64 dll）：
            // 禁用超分模式选项并提示，已设置超分模式则自动回退为普通模式
            bool isX86 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X86;
            if (isX86)
            {
                MpvX86WarningText.Visibility = Visibility.Visible;
                PlayerModeComboBox.SelectedIndex = 0;
                PlayerModeComboBox.IsEnabled = false;
                if (App.SettingsHelper.VideoPlayerMode == "Mpv")
                {
                    App.SettingsHelper.VideoPlayerMode = "Normal";
                    App.SettingsHelper.Save();
                    AppLogger.Warning("x86（32 位）平台不支持 libmpv libmpv，设置已自动回退为 media player");
                }
            }
            else
            {
                PlayerModeComboBox.SelectedIndex =
                    App.SettingsHelper.VideoPlayerMode == "Mpv" ? 1 : 0;
            }

            UpdateDecodeModeAvailability();
            UpdateDecoderBackendAvailability();

            _isLoading = false;
            AppLogger.Info($"视频设置页加载完成, 解码后端={App.SettingsHelper.VideoDecoderBackend}, 解码模式={App.SettingsHelper.VideoDecodeMode}, 播放模式={App.SettingsHelper.VideoPlayerMode}, 记忆播放位置={App.SettingsHelper.RememberVideoPosition}, 自动播放下一个={App.SettingsHelper.AutoPlayNextVideo}, 自动播放={App.SettingsHelper.AutoPlayVideo}, 后台播放={App.SettingsHelper.BackgroundPlayVideo}, 架构x86={isX86}");
        }

        private void PlayerModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || PlayerModeComboBox.SelectedIndex < 0) return;
            App.SettingsHelper.VideoPlayerMode =
                PlayerModeComboBox.SelectedIndex == 1 ? "Mpv" : "Normal";
            App.SettingsHelper.Save();
            AppLogger.Info($"视频播放模式变更: {App.SettingsHelper.VideoPlayerMode}（{(PlayerModeComboBox.SelectedIndex == 1 ? "libmpv（实验性）" : "MediaPlayer（推荐）")}）");
            UpdateDecoderBackendAvailability();
        }

        private void RememberPositionToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            App.SettingsHelper.RememberVideoPosition = RememberPositionToggle.IsOn;
            App.SettingsHelper.Save();
            AppLogger.Info($"记忆全部视频播放进度变更: {App.SettingsHelper.RememberVideoPosition}");
        }

        private void AutoPlayNextToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            App.SettingsHelper.AutoPlayNextVideo = AutoPlayNextToggle.IsOn;
            App.SettingsHelper.Save();
            AppLogger.Info($"自动播放下一个变更: {App.SettingsHelper.AutoPlayNextVideo}");
        }

        private void AutoPlayToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            App.SettingsHelper.AutoPlayVideo = AutoPlayToggle.IsOn;
            App.SettingsHelper.Save();
            AppLogger.Info($"自动播放变更: {App.SettingsHelper.AutoPlayVideo}");
        }

        private void BackgroundPlayToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            App.SettingsHelper.BackgroundPlayVideo = BackgroundPlayToggle.IsOn;
            App.SettingsHelper.Save();
            AppLogger.Info($"后台播放变更: {App.SettingsHelper.BackgroundPlayVideo}");
        }

        /// <summary>「快捷键」入口：导航到快捷键设置子页面。</summary>
        private void ShortcutButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("视频设置页面: 导航到快捷键设置");
            Frame.Navigate(typeof(VideoShortcutSettingsPage), null,
                new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }

        private void DecoderBackendComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || DecoderBackendComboBox.SelectedIndex < 0) return;
            App.SettingsHelper.VideoDecoderBackend =
                DecoderBackendComboBox.SelectedIndex == 1 ? "System" : "FFmpeg";
            UpdateDecodeModeAvailability();
            App.SettingsHelper.Save();
            AppLogger.Info($"视频解码后端变更: {App.SettingsHelper.VideoDecoderBackend}");
        }

        private void DecodeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || DecodeModeComboBox.SelectedIndex < 0) return;
            App.SettingsHelper.VideoDecodeMode =
                DecodeModeComboBox.SelectedIndex == 1 ? "Software" : "Auto";
            App.SettingsHelper.Save();
            AppLogger.Info($"视频解码模式变更: {App.SettingsHelper.VideoDecodeMode}");
        }

        private void UpdateDecodeModeAvailability()
        {
            // 解码方式可用性：
            //   FFmpeg 后端 → 可用（VideoDecoderMode.Automatic = 尝试 D3D11 硬件解码，显卡/驱动支持时生效，
            //     否则自动回退软解；ForceFFmpegSoftwareDecoder = 明确强制软解）；
            //   System 后端 → 禁用（Media Foundation 由系统自动管理解码器，硬件优先、不支持自动回退软解，
            //     MediaPlayer 无强制软解 API）。
            DecodeModeComboBox.IsEnabled = DecoderBackendComboBox.SelectedIndex == 0;
            DecodeModeInfoButton.Visibility = Visibility.Visible;
        }

        private void UpdateDecoderBackendAvailability()
        {
            bool isMpvMode = PlayerModeComboBox.SelectedIndex == 1;
            DecoderBackendComboBox.IsEnabled = !isMpvMode;
            DecodeModeComboBox.IsEnabled = !isMpvMode && DecoderBackendComboBox.SelectedIndex == 0;
            DecodeModeInfoButton.Visibility = isMpvMode ? Visibility.Collapsed : Visibility.Visible;
            DecoderInfoButton.Visibility = isMpvMode ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DecoderInfoButton_Click(object sender, RoutedEventArgs e)
        {
            DecoderTeachingTip.Subtitle = "当前为 libmpv 播放模式，视频解码器设置已禁用。libmpv 使用其内置解码器，不受此处设置影响。";
            DecoderTeachingTip.IsOpen = true;
        }

        private void DecodeModeInfoButton_Click(object sender, RoutedEventArgs e)
        {
            // 按当前解码后端动态设置提示文案
            DecodeModeTeachingTip.Subtitle = DecoderBackendComboBox.SelectedIndex == 1
                ? "Windows 系统解码器由系统自动管理（优先硬件加速，不支持时自动回退软件解码），无需手动设置。"
                : "内置 FFmpeg：自动 = 优先尝试用显卡硬件解码（显卡与驱动支持时生效，否则自动回退软件解码）；强制软件解码 = 只用 CPU 解码。可在播放器「更多 → 显示调试信息」中查看实际解码方式（硬解 / 软解）。";
            DecodeModeTeachingTip.IsOpen = true;
        }
    }
}
