using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SightoHear.Helpers;
using System;

namespace SightoHear
{
    public sealed partial class MusicSettingsPage : Page
    {
        private bool _isLoading = true;

        public MusicSettingsPage()
        {
            InitializeComponent();
            FileOpenModeComboBox.SelectedIndex = Math.Clamp(
                App.SettingsHelper.MusicFileOpenMode, 0, 1);

            _isLoading = false;
            AppLogger.Info($"音乐设置页加载完成, 文件打开方式={App.SettingsHelper.MusicFileOpenMode}");
        }

        private void FileOpenModeComboBox_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || FileOpenModeComboBox.SelectedIndex < 0)
                return;

            App.SettingsHelper.MusicFileOpenMode =
                FileOpenModeComboBox.SelectedIndex;
            App.SettingsHelper.Save();
            AppLogger.Info($"音乐文件打开方式变更: {FileOpenModeComboBox.SelectedIndex}");
        }
    }
}
