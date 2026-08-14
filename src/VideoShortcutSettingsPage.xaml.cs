using CommunityToolkit.WinUI.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SightoHear.Helpers;
using SightoHear.Services;
using System;
using System.Threading.Tasks;

namespace SightoHear
{
    /// <summary>
    /// 视频快捷键设置页（视频设置的子页面）：
    /// 顶部为"快捷键自定义"标题与「添加行为」「恢复默认」按钮，
    /// 下方以 SettingsExpander 卡片列出全部内置快捷键行为，可设置/修改按键、启用开关、
    /// 展开后调整"是否松开执行"或删除快捷键。
    /// </summary>
    public sealed partial class VideoShortcutSettingsPage : Page
    {
        public VideoShortcutSettingsPage()
        {
            InitializeComponent();
            VideoShortcutService.Changed += OnShortcutChanged;
            RebuildList();
            AppLogger.Info("视频快捷键设置页打开");
        }

        private void VideoShortcutSettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            VideoShortcutService.Changed -= OnShortcutChanged;
        }

        /// <summary>快捷键配置变化后刷新卡片列表（重建全部卡片）。</summary>
        private void OnShortcutChanged()
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                RebuildList();
            }
            else
            {
                DispatcherQueue.TryEnqueue(RebuildList);
            }
        }

        private void RebuildList()
        {
            ShortcutList.Children.Clear();
            foreach (var binding in VideoShortcutService.GetAllBindings())
            {
                ShortcutList.Children.Add(BuildCard(binding));
            }
        }

        /// <summary>构建单个快捷键绑定（设置卡片）的 UI（SettingsExpander）。</summary>
        private FrameworkElement BuildCard(Models.VideoShortcutItem item)
        {
            string actionName = VideoShortcutService.GetActionName(item.ActionId);
            string actionDescription = VideoShortcutService.GetActionDescription(item.ActionId);

            // ---- 右侧：快捷键捕获按钮 + 启用开关 ----
            var keyButton = new ShortcutKeyCaptureButton
            {
                KeyCode = item.KeyCode,
                Ctrl = item.Ctrl,
                Alt = item.Alt,
                Shift = item.Shift,
                MinWidth = 170,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            keyButton.UpdateDisplay();
            keyButton.ValidationFailed += msg => ShowErrorTip(keyButton, msg);
            keyButton.KeyCaptured += (code, ctrl, alt, shift) =>
            {
                // 组合键被其它绑定占用时回滚
                string? conflict = VideoShortcutService.FindConflict(item, code, ctrl, alt, shift);
                if (conflict != null)
                {
                    ShowErrorTip(keyButton, $"该快捷键已被「{conflict}」使用");
                    keyButton.Clear();
                    return;
                }
                item.KeyCode = code;
                item.Ctrl = ctrl;
                item.Alt = alt;
                item.Shift = shift;
                // 就地保存，不触发整页重建（按钮自身已显示新按键）
                VideoShortcutService.Save(notifyChanged: false);
                AppLogger.Info($"快捷键设置: {actionName} → {ShortcutKeyHelper.Format(code, ctrl, alt, shift)}");
            };

            var enableToggle = new ToggleSwitch
            {
                IsOn = item.Enabled,
                OnContent = "",
                OffContent = "",
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            enableToggle.Toggled += (_, _) =>
            {
                item.Enabled = enableToggle.IsOn;
                // 就地保存，不触发整页重建（避免开关切换时卡片重载闪烁）
                VideoShortcutService.Save(notifyChanged: false);
                AppLogger.Info($"快捷键启用状态变更: {actionName} = {item.Enabled}");
            };

            // ---- 展开项：是否松开执行 + 删除 ----
            var keyUpToggle = new ToggleSwitch
            {
                IsOn = item.ExecuteOnKeyUp,
                OnContent = "",
                OffContent = "",
                MinWidth = 0
            };
            keyUpToggle.Toggled += (_, _) =>
            {
                item.ExecuteOnKeyUp = keyUpToggle.IsOn;
                // 就地保存，不触发整页重建
                VideoShortcutService.Save(notifyChanged: false);
                AppLogger.Info($"快捷键松开执行变更: {actionName} = {item.ExecuteOnKeyUp}");
            };

            var deleteButton = new Button
            {
                Content = "确认删除",
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 232, 17, 35))
            };
            deleteButton.Click += async (_, _) => await ConfirmDeleteAsync(item);

            var expander = new SettingsExpander
            {
                Header = actionName,
                Description = actionDescription,
                IsExpanded = false
            };
            expander.HeaderIcon = new FontIcon
            {
                FontFamily = GetFluentIconsFont(),
                Glyph = "\uE765"
            };
            expander.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { keyButton, enableToggle }
            };
            expander.Items.Add(new SettingsCard
            {
                Header = "是否松开按键执行",
                Content = keyUpToggle
            });
            expander.Items.Add(new SettingsCard
            {
                Header = "删除",
                Content = deleteButton
            });
            return expander;
        }
        /// <summary>删除快捷键设置卡片确认弹窗（红色删除按钮，删除该卡片本身）。</summary>
        private async Task ConfirmDeleteAsync(Models.VideoShortcutItem item)
        {
            string actionName = VideoShortcutService.GetActionName(item.ActionId);
            var dialog = new ContentDialog
            {
                Title = "删除快捷键",
                Content = $"确定要删除「{actionName}」这张快捷键设置卡片吗？删除后该卡片的按键设置一并清除。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                // 与其它删除确认弹窗一致：不设置 DefaultButton（默认 None），
                // "删除"按钮由 DialogService 应用红色危险样式，"取消"为普通按钮
                XamlRoot = XamlRoot
            };
            ContentDialogResult result = await DialogService.ShowAsync(dialog, XamlRoot);
            if (result == ContentDialogResult.Primary)
            {
                VideoShortcutService.RemoveBinding(item);
            }
        }

        /// <summary>「添加行为」按钮：打开添加行为弹窗（允许重复添加同一行为，形成多张卡片）。</summary>
        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            await AddVideoShortcutDialog.ShowAsync(XamlRoot);
        }

        /// <summary>「恢复默认」按钮：确认后恢复为默认绑定列表（每个行为一张卡片、全部无按键）。</summary>
        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "恢复默认快捷键",
                Content = "确定要恢复默认快捷键吗？所有自定义快捷键设置卡片及其按键将被清除。",
                PrimaryButtonText = "恢复默认",
                CloseButtonText = "取消",
                // DefaultButton=None + useThemeColorButton：与回收站"还原"弹窗一致的规范样式——
                // "恢复默认"按钮由 DialogService 应用主题色（含悬停/按下反馈），"取消"为普通按钮
                DefaultButton = ContentDialogButton.None,
                XamlRoot = XamlRoot
            };
            ContentDialogResult result = await DialogService.ShowAsync(dialog, XamlRoot, useThemeColorButton: true);
            if (result == ContentDialogResult.Primary)
            {
                VideoShortcutService.ResetAll();
            }
        }

        /// <summary>在指定捕获按钮旁显示校验失败提示。</summary>
        private void ShowErrorTip(ShortcutKeyCaptureButton target, string message)
        {
            ErrorTip.Target = target;
            ErrorTip.Subtitle = message;
            ErrorTip.IsOpen = true;
        }

        private static FontFamily GetFluentIconsFont()
        {
            if (Application.Current.Resources.TryGetValue("SegoeFluentIconsFontFamily", out var resource)
                && resource is FontFamily font)
                return font;
            return new FontFamily("Segoe Fluent Icons");
        }
    }
}
