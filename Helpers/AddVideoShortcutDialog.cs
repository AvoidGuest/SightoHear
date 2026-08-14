using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SightoHear.Services;
using System.Threading.Tasks;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 「添加行为」弹窗（视频快捷键设置页右上角按钮触发）：
    /// 选择行为 → 设置按键（点击后等待输入，限制规则见 ShortcutKeyHelper）→
    /// 选择是否松开按键执行 → 确定后新增一张快捷键设置卡片。
    /// 允许重复添加同一行为（多张卡片），但同一组合键在全部绑定中只能出现一次；
    /// 取消/关闭弹窗不做任何校验。
    /// </summary>
    public static class AddVideoShortcutDialog
    {
        /// <summary>显示添加行为弹窗。</summary>
        public static async Task ShowAsync(XamlRoot xamlRoot)
        {
            // ---- 行为选择（允许重复添加，全部可用） ----
            var actionCombo = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var action in VideoShortcutService.Actions)
            {
                actionCombo.Items.Add(new ComboBoxItem
                {
                    Content = VideoShortcutService.GetActionName(action.Id),
                    Tag = action.Id
                });
            }
            if (actionCombo.Items.Count > 0)
                actionCombo.SelectedIndex = 0;

            // ---- 按键捕获按钮 ----
            var keyButton = new ShortcutKeyCaptureButton
            {
                MinWidth = 200,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // ---- 是否松开按键执行 ----
            var keyUpToggle = new ToggleSwitch
            {
                IsOn = false,
                OnContent = "",
                OffContent = "",
                MinWidth = 0
            };

            // ---- 校验错误提示（红字） ----
            var errorText = new TextBlock
            {
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 232, 17, 35)),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 0)
            };

            void ShowError(string message)
            {
                errorText.Text = message;
                errorText.Visibility = Visibility.Visible;
            }

            // 按键校验失败 → 显示错误
            keyButton.ValidationFailed += ShowError;

            // ---- 弹窗布局 ----
            var root = new StackPanel
            {
                Width = 460,
                Spacing = 8,
                Padding = new Thickness(4, 0, 4, 0)
            };
            root.Children.Add(CreateSettingRow("行为", actionCombo));
            root.Children.Add(CreateSettingRow("按键", keyButton));
            root.Children.Add(CreateSettingRow("是否松开按键执行", keyUpToggle));
            root.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = "添加行为",
                Content = root,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            // ★ 适配「确认弹窗顺序」：DialogService 在显示前会交换 Primary/Close 按钮文本。
            //   反转模式下 Primary 位置显示的是"取消"，点击它不应校验（直接关闭即可）；
            //   非反转模式下 Primary 是"确定"，点击时校验，失败则阻止关闭并提示。
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (App.SettingsHelper.ConfirmDialogReverse)
                    return;
                string? error = Validate(actionCombo, keyButton);
                if (error != null)
                {
                    ShowError(error);
                    args.Cancel = true;
                }
            };

            ContentDialogResult result = await DialogService.ShowAsync(dialog, xamlRoot);
            if (result != ContentDialogResult.Primary)
                return; // 取消（无论是否反转）直接关闭，不做任何校验

            // 反转模式下"确定"位于 Close 位置（未走 PrimaryButtonClick），这里兜底校验
            string? finalError = Validate(actionCombo, keyButton);
            if (finalError != null)
            {
                AppLogger.Warning($"添加行为校验未通过: {finalError}");
                return;
            }
            if (actionCombo.SelectedItem is not ComboBoxItem { Tag: string actionId })
                return;

            // 新增一张快捷键设置卡片（带按键，一次性保存触发页面重建）
            VideoShortcutService.AddBinding(
                actionId,
                keyButton.KeyCode,
                keyButton.Ctrl,
                keyButton.Alt,
                keyButton.Shift,
                keyUpToggle.IsOn);
            AppLogger.Info($"添加行为: {VideoShortcutService.GetActionName(actionId)} → " +
                $"{ShortcutKeyHelper.Format(keyButton.KeyCode, keyButton.Ctrl, keyButton.Alt, keyButton.Shift)}" +
                $"（{(keyUpToggle.IsOn ? "松开执行" : "按下执行")}）");
        }

        /// <summary>校验弹窗当前配置，返回错误提示；无错误返回 null。</summary>
        private static string? Validate(ComboBox actionCombo, ShortcutKeyCaptureButton keyButton)
        {
            if (actionCombo.SelectedItem is not ComboBoxItem { Tag: string actionId })
            {
                return "请选择行为";
            }
            if (!keyButton.KeyCode.HasValue)
            {
                return "请先点击「按键」设置快捷键";
            }
            // 组合键不能与任何现有绑定重复
            string? conflict = VideoShortcutService.FindConflict(
                null, keyButton.KeyCode.Value, keyButton.Ctrl, keyButton.Alt, keyButton.Shift);
            return conflict != null
                ? $"该快捷键已被「{conflict}」使用"
                : null;
        }

        /// <summary>构建"标签 + 右侧控件"的设置行（标题居左，控件撑满右侧）。</summary>
        private static Grid CreateSettingRow(string label, FrameworkElement control)
        {
            var grid = new Grid
            {
                ColumnSpacing = 16,
                Margin = new Thickness(0, 2, 0, 2)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center
            };
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(control, 1);

            grid.Children.Add(title);
            grid.Children.Add(control);
            return grid;
        }
    }
}
