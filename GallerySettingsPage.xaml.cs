using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SightoHear.Helpers;
using SightoHear.Services;
using System;

namespace SightoHear
{
    /// <summary>
    /// 图库设置页：只放图库/查看器特有的设置项。
    /// 通用设置（如「删除移入回收站」）放在基础设置页 BasicSettingsPage。
    /// </summary>
    public sealed partial class GallerySettingsPage : Page
    {
        private bool _isLoaded;

        public GallerySettingsPage()
        {
            InitializeComponent();
            Loaded += GallerySettingsPage_Loaded;
        }

        private void GallerySettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded) return;
            _isLoaded = true;

            // 卡片大小滑条
            ThumbnailSizeSlider.Value = App.SettingsHelper.GalleryThumbnailHeight;
            UpdateThumbnailSizeText();

            // 缩略图质量滑条
            ThumbnailQualitySlider.Value = App.SettingsHelper.GalleryThumbnailSize;
            UpdateThumbnailQualityText();

            // 图库页隐藏迷你播放器
            HideMiniPlayerToggle.IsOn = App.SettingsHelper.GalleryHideMiniPlayerOnEnter;

            // 查看器设置
            PreloadThumbnailsToggle.IsOn = App.SettingsHelper.GalleryPreloadThumbnails;
            ShowImageInfoToggle.IsOn = App.SettingsHelper.GalleryShowImageInfo;
            DoubleClickActionComboBox.SelectedIndex = App.SettingsHelper.GalleryViewerDoubleClickAction;
            AutoFullScreenToggle.IsOn = App.SettingsHelper.GalleryAutoFullScreen;
            ViewerBackgroundComboBox.SelectedIndex = App.SettingsHelper.GalleryViewerBackground;
            SlideAnimationToggle.IsOn = App.SettingsHelper.GallerySlideAnimation;

            UpdateThumbnailCacheStatus();
            AppLogger.Info("图库设置页加载完成");
        }

        // ---------- 卡片大小 ----------

        private void ThumbnailSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isLoaded) return;
            int value = (int)e.NewValue;
            App.SettingsHelper.GalleryThumbnailHeight = value;
            App.SettingsHelper.Save();
            UpdateThumbnailSizeText();
            AppLogger.Debug($"图库卡片大小变更: {value}px");
        }

        private void ResetThumbnailSizeButton_Click(object sender, RoutedEventArgs e)
        {
            App.SettingsHelper.GalleryThumbnailHeight = 146;
            ThumbnailSizeSlider.Value = 146;
            App.SettingsHelper.Save();
            UpdateThumbnailSizeText();
            AppLogger.Info("图库卡片大小已重置为默认 146px");
        }

        private void UpdateThumbnailSizeText()
        {
            if (ThumbnailSizeValueText != null)
                ThumbnailSizeValueText.Text = $"{App.SettingsHelper.GalleryThumbnailHeight} px";
        }

        // ---------- 缩略图质量 ----------

        private void ThumbnailQualitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isLoaded) return;
            App.SettingsHelper.GalleryThumbnailSize = (uint)e.NewValue;
            App.SettingsHelper.Save();
            UpdateThumbnailQualityText();
            AppLogger.Debug($"图库缩略图质量变更: {e.NewValue}px");
        }

        private void ResetThumbnailQualityButton_Click(object sender, RoutedEventArgs e)
        {
            App.SettingsHelper.GalleryThumbnailSize = 192;
            ThumbnailQualitySlider.Value = 192;
            App.SettingsHelper.Save();
            UpdateThumbnailQualityText();
            AppLogger.Info("图库缩略图质量已重置为默认 192px");
        }

        private void UpdateThumbnailQualityText()
        {
            if (ThumbnailQualityValueText != null)
                ThumbnailQualityValueText.Text = $"{App.SettingsHelper.GalleryThumbnailSize} px";
        }

        // ---------- 预热 / 显示信息 ----------

        private void PreloadThumbnailsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            App.SettingsHelper.GalleryPreloadThumbnails = PreloadThumbnailsToggle.IsOn;
            App.SettingsHelper.Save();
            AppLogger.Info($"图库缩略图预热: {(PreloadThumbnailsToggle.IsOn ? "开启" : "关闭")}");
        }

        // ---------- 图库页隐藏迷你播放器 ----------

        private void HideMiniPlayerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            App.SettingsHelper.GalleryHideMiniPlayerOnEnter = HideMiniPlayerToggle.IsOn;
            App.SettingsHelper.Save();
            AppLogger.Info($"图库页隐藏迷你播放器: {(HideMiniPlayerToggle.IsOn ? "开启" : "关闭")}");
        }

        private void ShowImageInfoToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            App.SettingsHelper.GalleryShowImageInfo = ShowImageInfoToggle.IsOn;
            App.SettingsHelper.Save();
            AppLogger.Info($"图库显示图片信息: {(ShowImageInfoToggle.IsOn ? "开启" : "关闭")}");
        }

        // ---------- 清理缩略图缓存 ----------

        private void UpdateThumbnailCacheStatus()
        {
            if (ThumbnailCacheStatusText == null) return;
            int count = ImageThumbnailService.GetDiskCacheCount();
            ThumbnailCacheStatusText.Text = count > 0
                ? $"{count} 个缓存文件"
                : "暂无缓存";
        }

        private void ClearThumbnailCacheButton_Click(object sender, RoutedEventArgs e)
        {
            int count = ImageThumbnailService.GetDiskCacheCount();
            ImageThumbnailService.ClearDiskCache();
            UpdateThumbnailCacheStatus();
            AppLogger.Info($"图库缩略图缓存已清理: {count} 个文件");
        }

        // ---------- 查看器 ----------

        private void DoubleClickActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || DoubleClickActionComboBox.SelectedIndex < 0) return;
            App.SettingsHelper.GalleryViewerDoubleClickAction = DoubleClickActionComboBox.SelectedIndex;
            App.SettingsHelper.Save();
            AppLogger.Info($"图库查看器双击动作变更: {DoubleClickActionComboBox.SelectedIndex}");
        }

        private void AutoFullScreenToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            App.SettingsHelper.GalleryAutoFullScreen = AutoFullScreenToggle.IsOn;
            App.SettingsHelper.Save();
            AppLogger.Info($"图库自动全屏: {(AutoFullScreenToggle.IsOn ? "开启" : "关闭")}");
        }

        private void ViewerBackgroundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || ViewerBackgroundComboBox.SelectedIndex < 0) return;
            App.SettingsHelper.GalleryViewerBackground = ViewerBackgroundComboBox.SelectedIndex;
            App.SettingsHelper.Save();
            AppLogger.Info($"图库查看器背景变更: {ViewerBackgroundComboBox.SelectedIndex}");
        }

        private void SlideAnimationToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            App.SettingsHelper.GallerySlideAnimation = SlideAnimationToggle.IsOn;
            App.SettingsHelper.Save();
            AppLogger.Info($"图库滑动动画: {(SlideAnimationToggle.IsOn ? "开启" : "关闭")}");
        }
    }
}
