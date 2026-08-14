using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using SightoHear.Helpers;
using CommunityToolkit.WinUI.Controls;

namespace SightoHear
{
    /// <summary>
    /// 外观设置页：涵盖「主题 / 背景 / 窗口 / 交互」四类设置。
    /// 窗口行为（记住窗口大小/位置、允许多开）与交互习惯（确认弹窗顺序）位于本页。
    /// </summary>
    public sealed partial class AppearancePage : Page
    {
        public ObservableCollection<Brush> Colors { get; } = new();
        private bool _isLoadingSettings = false;

        public AppearancePage()
        {
            _isLoadingSettings = true;
            InitializeComponent();
            Colors.Add(new SolidColorBrush(ColorHelper.FromArgb(255, 232, 17, 35)));
            Colors.Add(new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 212)));
            Colors.Add(new SolidColorBrush(ColorHelper.FromArgb(255, 16, 124, 16)));
            Colors.Add(new SolidColorBrush(ColorHelper.FromArgb(255, 136, 23, 152)));
            Colors.Add(new SolidColorBrush(ColorHelper.FromArgb(255, 216, 59, 1)));
            Colors.Add(new SolidColorBrush(ColorHelper.FromArgb(255, 246, 55, 154)));
            this.Loaded += (s, e) => { 
                LoadSettings(); 
                LoadBackdropSettings(); 
                LoadThemeModeSettings(); 
                KeepContentMicaToggle.IsOn = App.SettingsHelper.KeepContentMica;
                LoadRememberSettings();
                _isLoadingSettings = false;
            };

            // 配置 ColorPickerButton 内部的 ColorPicker 属性
            ColorPickerButton.Loaded += ColorPickerButton_Loaded;

            // 监听 ColorPickerButton 的 SelectedColor 属性变化
            ColorPickerButton.RegisterPropertyChangedCallback(
                CommunityToolkit.WinUI.Controls.ColorPickerButton.SelectedColorProperty,
                ColorPickerButton_SelectedColorChanged);
        }

        private void ColorPickerButton_SelectedColorChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (_isLoadingSettings) return;
            if (sender is not CommunityToolkit.WinUI.Controls.ColorPickerButton button) return;

            var color = button.SelectedColor;

            // 应用自定义颜色
            ApplyAccentColor(color);

            // 取消选择预设颜色
            ColorGridView.SelectedIndex = -1;

            // 保存自定义颜色
            App.SettingsHelper.UseWindowsTheme = false;
            WindowsThemeCheckBox.IsChecked = false;
            App.SettingsHelper.CustomAccentColor = color;

            SaveSettings();

            AppLogger.Info($"自定义主题颜色已应用: ARGB={color.A},{color.R},{color.G},{color.B}");
        }

        private void ColorPickerButton_Loaded(object sender, RoutedEventArgs e)
        {
            // 控件加载后配置 ColorPicker 属性
            if (sender is CommunityToolkit.WinUI.Controls.ColorPickerButton button
                && button.ColorPicker is Microsoft.UI.Xaml.Controls.ColorPicker picker)
            {
                picker.IsAlphaSliderVisible = true;
                picker.IsColorSliderVisible = true;
                picker.ColorSpectrumShape = Microsoft.UI.Xaml.Controls.ColorSpectrumShape.Ring;
            }
        }

        private void ApplyAccentColor(Color color)
        {
            App.ApplyGlobalAccentColor(color);
        }

        private void UseWindowsThemeColor()
        {
            App.ApplyGlobalAccentColor(App.GetSystemAccentColor());
        }

        private void ColorGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingSettings) return;
            if (WindowsThemeCheckBox.IsChecked != true && ColorGridView.SelectedItem is SolidColorBrush brush)
            {
                ApplyAccentColor(brush.Color);
                SaveSettings();
            }
        }

        private void WindowsThemeCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings) return;
            ColorGridView.IsEnabled = false;
            ColorGridView.Opacity = 0.4;
            ColorPickerButton.IsEnabled = false;
            ColorPickerButton.Opacity = 0.4;
            UseWindowsThemeColor();
            SaveSettings();
        }

        private void WindowsThemeCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings) return;
            ColorGridView.IsEnabled = true;
            ColorGridView.Opacity = 1.0;
            ColorPickerButton.IsEnabled = true;
            ColorPickerButton.Opacity = 1.0;
            if (ColorGridView.SelectedIndex < 0) ColorGridView.SelectedIndex = 1;
            if (ColorGridView.SelectedItem is SolidColorBrush brush)
            {
                ApplyAccentColor(brush.Color);
            }
            SaveSettings();
        }

        private void SaveSettings()
        {
            App.SettingsHelper.UseWindowsTheme = WindowsThemeCheckBox.IsChecked == true;
            App.SettingsHelper.SelectedColorIndex = ColorGridView.SelectedIndex;
            AppLogger.Info($"外观设置保存: 系统主题={WindowsThemeCheckBox.IsChecked}, 颜色索引={ColorGridView.SelectedIndex}");
            App.SettingsHelper.Save();
        }

        private void LoadSettings()
        {
            App.SettingsHelper.Load();
            WindowsThemeCheckBox.IsChecked = App.SettingsHelper.UseWindowsTheme;
            if (!App.SettingsHelper.UseWindowsTheme)
            {
                int idx = App.SettingsHelper.SelectedColorIndex;
                if (idx >= 0 && idx < Colors.Count)
                    ColorGridView.SelectedIndex = idx;
                else
                {
                    // 自定义颜色（索引为 -1），恢复自定义颜色
                    ColorGridView.SelectedIndex = -1;
                    ApplyAccentColor(App.SettingsHelper.CustomAccentColor);
                }
            }
            if (App.SettingsHelper.UseWindowsTheme)
            {
                ColorGridView.IsEnabled = false;
                ColorGridView.Opacity = 0.4;
                ColorPickerButton.IsEnabled = false;
                ColorPickerButton.Opacity = 0.4;
            }
            else
            {
                ColorGridView.IsEnabled = true;
                ColorGridView.Opacity = 1.0;
                ColorPickerButton.IsEnabled = true;
                ColorPickerButton.Opacity = 1.0;
            }
        }

        private void LoadBackdropSettings()
        {
            var type = App.SettingsHelper.BackdropType;
            BackdropComboBox.SelectedIndex = type switch
            {
                "Acrylic" => 1,
                "None" => 2,
                _ => 0
            };
        }

        private void BackdropComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingSettings) return;
            var type = BackdropComboBox.SelectedIndex switch
            {
                1 => "Acrylic",
                2 => "None",
                _ => "Mica"
            };
            AppLogger.Info($"背景效果变更: {type}");
            App.SettingsHelper.BackdropType = type;
            App.SettingsHelper.Save();
            if (App.MainWindow is MainWindow mw)
            {
                mw.ApplyBackdrop(type);
            }
        }

        private void LoadThemeModeSettings()
        {
            var mode = App.SettingsHelper.ThemeMode;
            ThemeModeComboBox.SelectedIndex = mode switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };
        }

        private void ThemeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingSettings) return;
            var mode = ThemeModeComboBox.SelectedIndex switch
            {
                1 => "Light",
                2 => "Dark",
                _ => "System"
            };
            AppLogger.Info($"主题模式变更: {mode}");
            App.SettingsHelper.ThemeMode = mode;
            App.SettingsHelper.Save();
            if (App.MainWindow is MainWindow mw)
            {
                mw.ApplyTheme(mode);
            }

            App.TriggerThemeChanged(mode);
        }

        private void KeepContentMicaToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings)
                return;

            App.SettingsHelper.KeepContentMica = KeepContentMicaToggle.IsOn;
            App.SettingsHelper.Save();
            if (App.MainWindow is MainWindow mw)
            {
                mw.ApplyBackdrop(App.SettingsHelper.BackdropType);
            }
        }

        /// <summary>加载「窗口 / 交互」分区的 4 个开关。</summary>
        private void LoadRememberSettings()
        {
            RememberWindowSizeToggle.IsOn = App.SettingsHelper.RememberWindowSize;
            RememberWindowPositionToggle.IsOn = App.SettingsHelper.RememberWindowPosition;
            AllowMultiInstanceToggle.IsOn = App.SettingsHelper.AllowMultiInstance;
            ConfirmDialogReverseToggle.IsOn = App.SettingsHelper.ConfirmDialogReverse;
        }

        /// <summary>
        /// 「窗口 / 交互」分区开关统一事件：通过 ToggleSwitch.Tag 定位对应设置项并保存。
        /// </summary>
        private void RememberSetting_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings)
                return;
            if (sender is not ToggleSwitch { Tag: string key } toggle)
                return;

            bool value = toggle.IsOn;
            switch (key)
            {
                case "RememberWindowSize":
                    App.SettingsHelper.RememberWindowSize = value;
                    break;
                case "RememberWindowPosition":
                    App.SettingsHelper.RememberWindowPosition = value;
                    break;
                case "AllowMultiInstance":
                    App.SettingsHelper.AllowMultiInstance = value;
                    break;
                case "ConfirmDialogReverse":
                    App.SettingsHelper.ConfirmDialogReverse = value;
                    break;
                default:
                    return;
            }
            App.SettingsHelper.Save();
        }
    }
}
