using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using SightoHear.Services;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 播放器设置弹窗公共构建逻辑：
    /// 音乐播放器与视频播放器共用同一套弹窗外框（居中标题 + 关闭按钮 + 固定底色 ContentDialog）
    /// 与"音频输出设备"设置卡片（设备下拉 + 展开区设备音量控制），避免两份重复实现。
    /// 各播放器通过参数注入"已保存设备 ID / 保存回调 / 应用回调"实现差异化行为。
    /// </summary>
    public static partial class PlayerSettingsDialogHelper
    {
        /// <summary>
        /// 显示播放器设置弹窗。外框（标题、关闭按钮、固定背景、宽度）在此统一构建，
        /// 弹窗内容由调用方通过 <paramref name="buildContent"/> 提供。
        /// 弹窗主题在调用前由页面依据 ActualTheme 决定，此处固定后跳过 DialogService 的
        /// 二次主题应用，避免打开时闪烁。
        /// </summary>
        /// <param name="xamlRoot">弹窗所属的 XamlRoot。</param>
        /// <param name="dark">深色主题时为 true（决定弹窗底色与按钮悬停色）。</param>
        /// <param name="buildContent">构建弹窗内容的回调（接收 ContentDialog 用于关闭按钮隐藏弹窗）。</param>
        public static async Task ShowPlayerSettingsDialogAsync(
            XamlRoot xamlRoot, bool dark, Func<ContentDialog, UIElement> buildContent)
        {
            AppLogger.Info("播放器设置弹窗打开");

            var dialog = new ContentDialog
            {
                // 使用自定义头部与关闭按钮，因此不设置 Title 与系统按钮。
                XamlRoot = xamlRoot,
                RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light
            };
            dialog.Content = buildContent(dialog);
            // ContentDialog 模板里除了 BackgroundElement 还有一层 ContentDialogTopOverlay，
            // 需要一起覆盖，否则会盖住 Background。深色/浅色各用一套固定底色。
            var fixedBackground = new SolidColorBrush(dark
                ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x2B, 0x2B, 0x2B)
                : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF9, 0xF9, 0xF9));
            dialog.Resources["ContentDialogMaxWidth"] = 580.0;
            dialog.Resources["ContentDialogMinWidth"] = 580.0;
            dialog.Resources["ContentDialogBackground"] = fixedBackground;
            dialog.Resources["ContentDialogTopOverlay"] = fixedBackground;
            dialog.Background = fixedBackground;
            // 弹窗主题已在此固定，跳过 DialogService 的二次主题应用，避免打开时闪烁。
            await DialogService.ShowAsync(dialog, xamlRoot, applyTheme: false);

            AppLogger.Info("播放器设置弹窗关闭");
        }

        /// <summary>
        /// 构建设置弹窗的头部：居中标题 + 右上角关闭按钮。
        /// </summary>
        /// <param name="dark">深色主题时为 true（决定关闭按钮悬停/按下反馈色）。</param>
        /// <param name="closeAction">点击关闭按钮时执行的动作（通常为隐藏所属 ContentDialog）。</param>
        public static Grid BuildDialogHeader(bool dark, Action closeAction)
        {
            // 顶部：居中标题 + 右上角关闭按钮
            var titleText = new TextBlock
            {
                Text = "播放器设置",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var closeButton = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new FontIcon
                {
                    FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"),
                    FontSize = 12,
                    Glyph = "\uE8BB" // 关闭
                }
            };
            // 覆盖 Button 三个状态的背景资源，让关闭按钮有 hover/pressed 反馈。
            byte overlayChannel = (byte)(dark ? 0xFF : 0x00);
            closeButton.Resources["ButtonBackground"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            closeButton.Resources["ButtonBackgroundPointerOver"] =
                new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x14, overlayChannel, overlayChannel, overlayChannel));
            closeButton.Resources["ButtonBackgroundPressed"] =
                new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x22, overlayChannel, overlayChannel, overlayChannel));
            closeButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ToolTipService.SetToolTip(closeButton, "关闭");
            closeButton.Click += (_, _) => closeAction();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            header.Children.Add(titleText);
            header.Children.Add(closeButton);
            return header;
        }

        /// <summary>
        /// "音频输出设备"可展开设置卡片：
        /// 允许软件单独输出到指定的音频设备（不影响系统默认输出），
        /// 展开后可控制该设备的"设备音量"（WASAPI IAudioEndpointVolume，与任务栏音量一致）。
        /// 选项首项为"跟随系统设备"（值 = 空字符串），其余为系统枚举到的输出设备。
        /// 遵循 Windows API 规范：MediaDevice.GetAudioRenderSelector() 获取 AQS 查询字符串，
        /// DeviceInformation.FindAllAsync 枚举渲染设备，DeviceInformation.CreateFromIdAsync
        /// 创建设备对象后赋给 MediaPlayer.AudioDevice；设备音量由 AudioEndpointVolumeService
        /// 按设备 ID 直接读写 IAudioEndpointVolume。
        /// </summary>
        /// <param name="savedDeviceId">当前播放器已保存的输出设备 ID（空字符串 = 跟随系统）。</param>
        /// <param name="saveDeviceId">保存设备 ID 的回调（调用方负责持久化与日志）。</param>
        /// <param name="applyDeviceAsync">将设备 ID 应用到播放器的回调（空字符串 = 跟随系统默认设备）。</param>
        public static SettingsExpander BuildAudioOutputExpander(
            string savedDeviceId,
            Action<string> saveDeviceId,
            Func<string, Task> applyDeviceAsync)
        {
            savedDeviceId ??= string.Empty;

            // 输出设备下拉框：固定宽度，防止长设备名撑开卡片布局
            var deviceCombo = new ComboBox
            {
                Width = 200,
                MinWidth = 200,
                MaxWidth = 200,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 首项：跟随系统默认输出设备（空字符串 ID）
            deviceCombo.Items.Add(new ComboBoxItem
            {
                Content = "跟随系统设备",
                Tag = string.Empty
            });
            deviceCombo.SelectedIndex = 0;

            // ---- 展开区：输出设备音量控制 ----
            var volumeText = new TextBlock
            {
                Width = 40,
                Text = "--",
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            var volumeSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                IsEnabled = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            var volumeRow = new Grid { ColumnSpacing = 12 };
            volumeRow.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(volumeSlider, 0);
            Grid.SetColumn(volumeText, 1);
            volumeRow.Children.Add(volumeSlider);
            volumeRow.Children.Add(volumeText);

            var volumeCard = new SettingsCard
            {
                Header = "输出设备音量",
                Content = volumeRow
            };
            volumeCard.Resources["SettingsCardWrapThreshold"] = 200.0;
            volumeCard.Resources["SettingsCardWrapNoIconThreshold"] = 160.0;

            // 当前音量控制的目标设备 ID（跟随系统时取系统默认设备）
            string volumeDeviceId = string.Empty;
            bool isInitializingVolume = false;

            // 刷新音量控件：读取当前目标设备的设备音量
            void RefreshVolumeControl()
            {
                float? volume = AudioEndpointVolumeService.GetVolume(volumeDeviceId);
                isInitializingVolume = true;
                if (volume.HasValue)
                {
                    int percent = (int)Math.Round(volume.Value * 100);
                    volumeSlider.IsEnabled = true;
                    volumeSlider.Value = percent;
                    volumeText.Text = percent.ToString();
                }
                else
                {
                    volumeSlider.IsEnabled = false;
                    volumeSlider.Value = 0;
                    volumeText.Text = "--";
                }
                isInitializingVolume = false;
            }

            // 跟随系统设备时，音量控制目标为系统默认输出设备
            void UpdateVolumeTarget(string deviceId)
            {
                volumeDeviceId = string.IsNullOrEmpty(deviceId)
                    ? MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default) ?? string.Empty
                    : deviceId;
                RefreshVolumeControl();
            }

            // 保存设备 ID：同步本地"已保存值"（供 SelectionChanged 幂等比较）并通知调用方持久化
            void SaveDevice(string deviceId)
            {
                savedDeviceId = deviceId;
                saveDeviceId(deviceId);
            }

            // 音量条变化 → 实时写入设备音量（初始化/未加载时跳过）
            volumeSlider.ValueChanged += (_, _) =>
            {
                if (isInitializingVolume || string.IsNullOrEmpty(volumeDeviceId))
                    return;
                int percent = (int)Math.Round(volumeSlider.Value);
                volumeText.Text = percent.ToString();
                AudioEndpointVolumeService.SetVolume(volumeDeviceId, percent / 100f);
            };

            // 异步枚举系统音频渲染设备并恢复已保存的选择。
            // 注意：初始化阶段（未加入视觉树）设置的 SelectedIndex 也会触发 SelectionChanged，
            // 通过 IsLoaded + 跳过初始化标志避免误保存。
            _ = LoadOutputDevicesAsync(deviceCombo, savedDeviceId, SaveDevice, UpdateVolumeTarget);

            deviceCombo.SelectionChanged += async (_, _) =>
            {
                if (!deviceCombo.IsLoaded ||
                    deviceCombo.SelectedItem is not ComboBoxItem item ||
                    item.Tag is not string deviceId)
                    return;

                // 异步加载设备后恢复的选中项与已保存值不同才保存并应用
                if (!string.Equals(deviceId, savedDeviceId, StringComparison.Ordinal))
                {
                    // 持久化并立即应用到当前播放器
                    SaveDevice(deviceId);
                    await applyDeviceAsync(deviceId);
                }

                // 同步音量控制目标并刷新
                UpdateVolumeTarget(deviceId);
            };

            // 可展开设置卡片主体：样式与"网络歌词源"卡片一致（窄弹窗不换行）。
            var expander = new SettingsExpander
            {
                Header = "音频输出设备",
                Description = "将音源输出到指定设备以互不干扰",
                HeaderIcon = new FontIcon
                {
                    FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"),
                    FontSize = 16,
                    Glyph = "\uE767" // 音量
                },
                Content = deviceCombo,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            expander.Resources["SettingsCardWrapThreshold"] = 200.0;
            expander.Resources["SettingsCardWrapNoIconThreshold"] = 160.0;
            expander.Resources["SettingsExpanderWrapThreshold"] = 200.0;
            expander.Resources["SettingsExpanderWrapNoIconThreshold"] = 160.0;
            expander.Items.Add(volumeCard);

            // 展开时刷新音量显示（设备音量可能在外部被修改）
            expander.Expanded += (_, _) => RefreshVolumeControl();

            return expander;
        }

        /// <summary>
        /// 枚举系统音频渲染设备（遵循 Windows API 规范）并填充输出设备下拉框，
        /// 恢复用户上次保存的设备选择。设备枚举完成后自动回退跟随系统设备。
        /// </summary>
        private static async Task LoadOutputDevicesAsync(
            ComboBox combo, string savedDeviceId, Action<string> saveDeviceId,
            Action<string>? onCompleted = null)
        {
            // 最终生效的设备 ID：设备不存在被重置时变为空字符串（跟随系统）
            string finalDeviceId = savedDeviceId;
            try
            {
                // Windows API 规范：音频渲染设备的 AQS 查询字符串 + 异步枚举
                string selector = MediaDevice.GetAudioRenderSelector();
                IReadOnlyList<DeviceInformation> devices =
                    await DeviceInformation.FindAllAsync(selector);
                if (devices == null)
                    return;

                int savedIndex = 0; // 默认跟随系统设备
                bool matched = string.IsNullOrEmpty(savedDeviceId);
                foreach (var device in devices)
                {
                    var item = new ComboBoxItem
                    {
                        Content = string.IsNullOrWhiteSpace(device.Name)
                            ? "未命名设备"
                            : device.Name,
                        Tag = device.Id
                    };
                    combo.Items.Add(item);

                    // 匹配已保存的设备 ID，记录其索引
                    if (!matched &&
                        string.Equals(device.Id, savedDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        savedIndex = combo.Items.Count - 1;
                        matched = true;
                    }
                }

                // 应用已保存的选择（可能触发 SelectionChanged，但 IsLoaded 检查会跳过初始化阶段）
                combo.SelectedIndex = savedIndex;

                // 已保存的设备在系统中已不存在（如已拔出）：重置为跟随系统设备
                if (!matched && !string.IsNullOrEmpty(savedDeviceId))
                {
                    finalDeviceId = string.Empty;
                    saveDeviceId(string.Empty);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "枚举音频输出设备失败");
            }
            finally
            {
                // 枚举完成（无论成败）后按最终生效的设备通知调用方刷新音量控件
                onCompleted?.Invoke(finalDeviceId);
            }
        }

        // ==================== 分段 Tab 栏（与音乐播放器弹窗一致）====================

        /// <summary>
        /// 构建播放器设置弹窗上方的胶囊分段 Tab 栏（目前仅"常规"一个分段）：
        /// 外层 Border 负责选中/悬停背景，内层按钮模板背景全透明，从根本上避免按下状态残留。
        /// 音乐播放器与视频播放器共用同一套外观。
        /// </summary>
        /// <param name="dark">深色主题时为 true（决定指示器与悬停底色）。</param>
        public static Border BuildSegmentBar(bool dark)
        {
            var segmentHost = BuildSegment("常规");

            const double TabFixedWidth = 80;
            segmentHost.Width = TabFixedWidth;

            var segmentedRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                IsHitTestVisible = false
            };
            segmentedRow.Children.Add(segmentHost);

            // 滑动指示器（中层）
            var indicator = new Border
            {
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                RenderTransform = new TranslateTransform(),
                IsHitTestVisible = false
            };
            indicator.Background = new SolidColorBrush(dark
                ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x50, 0x50, 0x50)
                : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            indicator.BorderBrush = ThemedBrush(dark, "CardStrokeColorDefaultBrush", 0xFF, 0xFF, 0xFF, 0x24);

            // 悬停背景（下层，固定 Brush 避免频繁创建）
            var transparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            const double HoverInset = 8;
            const double HoverWidth = TabFixedWidth - HoverInset * 2;

            var hoverOverlay = new Border
            {
                CornerRadius = new CornerRadius(7),
                Width = HoverWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                RenderTransform = new TranslateTransform { X = HoverInset },
                IsHitTestVisible = false,
                Background = transparentBrush,
                Margin = new Thickness(0, 4, 0, 4)
            };

            var selectorBarGrid = new HandCursorGrid
            {
                Background = transparentBrush
            };
            // Z 序：悬停背景(0) → 滑块(1) → 文字(2，纯显示)
            selectorBarGrid.Children.Add(hoverOverlay);
            selectorBarGrid.Children.Add(indicator);
            selectorBarGrid.Children.Add(segmentedRow);

            var selectorBarHost = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Background = ThemedBrush(dark, "CardBackgroundFillColorSecondaryBrush", 0x3A, 0x3A, 0x3A),
                BorderBrush = ThemedBrush(dark, "CardStrokeColorDefaultBrush", 0xFF, 0xFF, 0xFF, 0x18),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 16),
                Child = selectorBarGrid
            };

            // 滑动指示器定位：单分段固定在第 0 位
            void PositionIndicator()
            {
                indicator.Width = TabFixedWidth;
                ((TranslateTransform)indicator.RenderTransform).X = 0;
            }
            // 加入视觉树后再定位一次（确保布局时指示器宽度正确）
            selectorBarGrid.Loaded += (_, _) => PositionIndicator();
            PositionIndicator();

            return selectorBarHost;
        }

        /// <summary>
        /// 构建单个分段按钮宿主（内层按钮模板背景在所有状态都透明，视觉完全交给外层 Border，
        /// 避免 Pressed / PointerOver 视觉状态残留）。
        /// </summary>
        private static Border BuildSegment(string text)
        {
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var button = new SegmentButton
            {
                Content = new TextBlock
                {
                    Text = text,
                    FontSize = 13,
                    IsHitTestVisible = false,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                Padding = new Thickness(18, 6, 18, 6),
                MinWidth = 0,
                MinHeight = 0,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = transparent,
                UseSystemFocusVisuals = false
            };
            button.Resources["ButtonBackground"] = transparent;
            button.Resources["ButtonBackgroundPointerOver"] = transparent;
            button.Resources["ButtonBackgroundPressed"] = transparent;
            button.Resources["ButtonBackgroundDisabled"] = transparent;

            var hoverLayer = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(0),
                Background = transparent,
                Margin = new Thickness(3),
                Child = button
            };

            return new Border
            {
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = transparent,
                Child = hoverLayer
            };
        }

        /// <summary>
        /// 播放器设置弹窗专用主题刷子：深色模式用固定深色值，浅色模式解析指定的浅色资源键
        /// （弹窗主题被固定为深/浅，不能依赖 Application.Current.Resources 解析到系统主题值）。
        /// </summary>
        public static SolidColorBrush ThemedBrush(
            bool dark, string lightResourceKey, byte dr, byte dg, byte db, byte da = 0xFF)
        {
            if (dark)
                return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(da, dr, dg, db));

            // 浅色模式：使用固定颜色，避免 Application.Current.Resources 解析到系统主题值
            return lightResourceKey switch
            {
                "TextFillColorSecondaryBrush" => new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0x9E, 0x00, 0x00, 0x00)),
                "CardBackgroundFillColorDefaultBrush" => new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                "CardBackgroundFillColorSecondaryBrush" => new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xEB, 0xEB, 0xEB)),
                "CardStrokeColorDefaultBrush" => new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00)),
                _ => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(da, dr, dg, db))
            };
        }

        // 带手型光标的分段按钮：悬停时显示食指点击指针。
        public sealed partial class SegmentButton : Button
        {
            public SegmentButton()
            {
                ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                    Microsoft.UI.Input.InputSystemCursorShape.Hand);
            }
        }

        public sealed partial class HandCursorGrid : Grid
        {
            private readonly Microsoft.UI.Input.InputCursor _hand;
            private readonly Microsoft.UI.Input.InputCursor _grab;

            public HandCursorGrid()
            {
                _hand = Microsoft.UI.Input.InputSystemCursor.Create(
                    Microsoft.UI.Input.InputSystemCursorShape.Hand);
                _grab = Microsoft.UI.Input.InputSystemCursor.Create(
                    Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                ProtectedCursor = _hand;
            }

            public void SetGrabCursor() => ProtectedCursor = _grab;
            public void SetHandCursor() => ProtectedCursor = _hand;
        }
    }
}
