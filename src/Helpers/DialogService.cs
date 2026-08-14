using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace SightoHear.Helpers
{
    public static class DialogService
    {
        public static async Task<ContentDialogResult> ShowAsync(
            ContentDialog dialog,
            XamlRoot xamlRoot,
            bool applyTheme = true,
            bool isFileDelete = false,
            bool useThemeColorButton = false)
        {
            dialog.XamlRoot = xamlRoot;
            // 调用方若已自行固定主题/背景（如强制浅色的设置弹窗），传 applyTheme:false
            // 跳过这里的主题重设，避免同一次打开做两轮主题切换而闪烁。
            if (applyTheme)
                ApplyTheme(dialog, xamlRoot);

            bool allowClose = false;
            bool isClosing = false;
            ContentDialogResult requestedResult = ContentDialogResult.None;

            // 记录原始主按钮文本："确认弹窗顺序"反转开启时会在显示前交换
            // Primary/Close 按钮文本，主题色按钮必须始终定位在确定按钮（原始主按钮）上，
            // 因此需在交换前提前捕获，不能读取已被交换的 dialog.PrimaryButtonText。
            string originalPrimaryButtonText = dialog.PrimaryButtonText;

            TypedEventHandler<ContentDialog, ContentDialogOpenedEventArgs>? opened = null;
            TypedEventHandler<ContentDialog, ContentDialogClosingEventArgs>? closing = null;

            opened = async (_, _) =>
            {
                // 为"删除"按钮应用危险样式（红色）或主题色（回收站模式）
                // 回收站模式开启时删除按钮使用普通软件主题色，否则使用红色危险样式；
                // useThemeColorButton 为 true 时（如还原弹窗），主按钮强制使用主题色。
                bool useThemeColor = useThemeColorButton || (isFileDelete && App.SettingsHelper.DeleteToRecycleBin);
                EnhanceDeleteButtonStyle(dialog, useThemeColor, useThemeColorButton, originalPrimaryButtonText);
                // 修复主题色按钮（确定/保存等）文字颜色为白色
                FixThemeButtonForeground(dialog);
                FrameworkElement target = GetAnimationTarget(dialog);
                await AnimateAsync(target, 1.04, 1, 0, 1, 170);
            };

            closing = async (_, args) =>
            {
                if (allowClose)
                    return;

                args.Cancel = true;
                requestedResult = args.Result;
                // 如果启用了确认弹窗顺序反转，交换返回结果
                if (App.SettingsHelper.ConfirmDialogReverse)
                {
                    requestedResult = requestedResult switch
                    {
                        ContentDialogResult.Primary => ContentDialogResult.None,
                        ContentDialogResult.None => ContentDialogResult.Primary,
                        _ => requestedResult
                    };
                }
                if (isClosing)
                    return;

                isClosing = true;
                FrameworkElement target = GetAnimationTarget(dialog);
                double closingScale =
                    requestedResult == ContentDialogResult.None ? 1.04 : 0.96;
                await AnimateAsync(target, 1, closingScale, 1, 0, 140);
                allowClose = true;
                dialog.Hide();
            };

            dialog.Opened += opened;
            dialog.Closing += closing;
            try
            {
                // 如果启用了确认弹窗顺序反转，在显示前交换 PrimaryButton 和 CloseButton 的文本
                if (App.SettingsHelper.ConfirmDialogReverse)
                {
                    var primaryText = dialog.PrimaryButtonText;
                    var closeText = dialog.CloseButtonText;
                    dialog.PrimaryButtonText = closeText;
                    dialog.CloseButtonText = primaryText;
                    
                    // 交换 DefaultButton
                    if (dialog.DefaultButton == ContentDialogButton.Primary)
                        dialog.DefaultButton = ContentDialogButton.Close;
                    else if (dialog.DefaultButton == ContentDialogButton.Close)
                        dialog.DefaultButton = ContentDialogButton.Primary;
                }

                ContentDialogResult result = await dialog.ShowAsync();
                
                // 如果启用了确认弹窗顺序反转，交换返回结果
                if (App.SettingsHelper.ConfirmDialogReverse)
                {
                    result = result switch
                    {
                        ContentDialogResult.Primary => ContentDialogResult.None,
                        ContentDialogResult.None => ContentDialogResult.Primary,
                        _ => result
                    };
                }
                
                return isClosing ? requestedResult : result;
            }
            finally
            {
                dialog.Opened -= opened;
                dialog.Closing -= closing;
            }
        }

        private static void ApplyTheme(ContentDialog dialog, XamlRoot xamlRoot)
        {
            ElementTheme theme = xamlRoot.Content is FrameworkElement root
                ? root.ActualTheme
                : ElementTheme.Default;
            dialog.RequestedTheme = theme;

            bool isDark = theme == ElementTheme.Dark;
            dialog.Background = new SolidColorBrush(
                isDark
                    ? ColorHelper.FromArgb(255, 43, 43, 43)
                    : ColorHelper.FromArgb(255, 249, 249, 249));
            dialog.Foreground = new SolidColorBrush(
                isDark ? Colors.White : Colors.Black);
            dialog.BorderBrush = new SolidColorBrush(
                isDark
                    ? ColorHelper.FromArgb(255, 68, 68, 68)
                    : ColorHelper.FromArgb(255, 218, 218, 218));
        }

        private static FrameworkElement GetAnimationTarget(ContentDialog dialog)
        {
            dialog.ApplyTemplate();
            return FindNamedElement(dialog, "BackgroundElement") ?? dialog;
        }

        private static FrameworkElement? FindNamedElement(
            DependencyObject parent,
            string name)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is FrameworkElement element && element.Name == name)
                    return element;

                FrameworkElement? result = FindNamedElement(child, name);
                if (result != null)
                    return result;
            }

            return null;
        }

        #region 删除按钮红色危险样式

        /// <summary>
        /// 在 Opened 事件中为"删除"按钮应用主题色（回收站模式）或红色危险样式（永久删除）。
        /// 通过覆盖 Button 视觉状态资源（ButtonBackgroundPointerOver 等）来修改颜色，
        /// 不替换按钮的 ControlTemplate 或 Style，因此保留了 ContentDialog 为按钮预设的
        /// 原生按压行为（不产生缩放效果），仅改变颜色和圆角。
        /// </summary>
        /// <param name="dialog">目标对话框</param>
        /// <param name="useThemeColor">
        /// true 表示使用普通软件主题色（删除文件时移入回收站，可随时还原；或还原类操作）；
        /// false 表示使用红色危险样式（永久删除本地磁盘文件，不可撤销）。
        /// </param>
        /// <param name="usePrimaryButton">
        /// true 表示优先对主按钮（PrimaryButtonText）应用样式（如"还原"按钮）；
        /// false 表示仅对"删除"/"移入回收站"/"清空"按钮应用样式。
        /// </param>
        /// <param name="primaryButtonText">
        /// 反转前的原始主按钮文本。"确认弹窗顺序"反转开启时 PrimaryButtonText
        /// 已被交换为关闭按钮文本，需用此参数定位真正的确定按钮。
        /// </param>
        private static void EnhanceDeleteButtonStyle(ContentDialog dialog, bool useThemeColor, bool usePrimaryButton = false, string? primaryButtonText = null)
        {
            // 确保模板已完全加载
            dialog.ApplyTemplate();

            // 查找目标按钮：显式指定时优先按原始主按钮文本定位确定按钮，
            // 否则查找"删除"、"移入回收站"或"清空"按钮
            Button? deleteButton = null;
            if (usePrimaryButton)
            {
                string? targetText = primaryButtonText ?? dialog.PrimaryButtonText;
                if (!string.IsNullOrEmpty(targetText))
                    deleteButton = FindButtonByText(dialog, targetText);
            }
            deleteButton ??= FindButtonByText(dialog, "删除") ?? FindButtonByText(dialog, "移入回收站") ?? FindButtonByText(dialog, "清空");
            if (deleteButton == null) return;

            // 读取取消按钮的圆角值，保持一致
            var cancelButton = FindButtonByText(dialog, "取消");
            if (cancelButton != null)
            {
                deleteButton.CornerRadius = cancelButton.CornerRadius;
            }

            // 三态颜色：正常 → 悬停（发黑变暗） → 按下（更黑更暗）
            SolidColorBrush normalBg, hoverBg, pressedBg;
            if (useThemeColor)
            {
                // 回收站模式：使用普通软件主题色（如强调色为蓝色则按钮为蓝色）
                Windows.UI.Color accent = GetCurrentAccentColor();
                normalBg = new SolidColorBrush(accent);
                hoverBg = new SolidColorBrush(Blend(accent, 0, 0.15));
                pressedBg = new SolidColorBrush(Blend(accent, 0, 0.3));
            }
            else
            {
                // 永久删除：红色危险样式
                normalBg = new SolidColorBrush(ColorHelper.FromArgb(255, 232, 17, 35));
                hoverBg = new SolidColorBrush(ColorHelper.FromArgb(255, 198, 40, 40));
                pressedBg = new SolidColorBrush(ColorHelper.FromArgb(255, 183, 28, 28));
            }
            var foreground = new SolidColorBrush(Colors.White);

            deleteButton.Background = normalBg;
            deleteButton.Foreground = foreground;

            // 覆盖 Button 视觉状态所需的资源，让 VisualStateManager 自动使用我们的颜色。
            // 这些资源仅影响颜色，不影响按钮的 ControlTemplate 和原生按压缩放行为。
            deleteButton.Resources["ButtonBackgroundPointerOver"] = hoverBg;
            deleteButton.Resources["ButtonBackgroundPressed"] = pressedBg;
            deleteButton.Resources["ButtonForegroundPointerOver"] = foreground;
            deleteButton.Resources["ButtonForegroundPressed"] = foreground;
        }

        /// <summary>
        /// 获取当前应用的强调色（主题色）。
        /// 优先读取全局主题色资源 SightoHearAccentBrush，
        /// 读取失败时回退到系统强调色，再失败则使用默认蓝色。
        /// </summary>
        private static Windows.UI.Color GetCurrentAccentColor()
        {
            if (Application.Current.Resources.TryGetValue("SightoHearAccentBrush", out var resource) &&
                resource is SolidColorBrush brush)
            {
                return brush.Color;
            }

            try
            {
                return App.GetSystemAccentColor();
            }
            catch
            {
                return Windows.UI.Color.FromArgb(255, 0, 120, 212);
            }
        }

        /// <summary>
        /// 将颜色向目标值混合，用于生成按钮悬停/按下的加深变体。
        /// </summary>
        /// <param name="color">源颜色</param>
        /// <param name="target">目标通道值（0 为黑色，255 为白色）</param>
        /// <param name="amount">混合比例（0~1）</param>
        private static Windows.UI.Color Blend(Windows.UI.Color color, byte target, double amount)
        {
            static byte Mix(byte source, byte targetValue, double ratio) =>
                (byte)Math.Clamp(Math.Round(source + (targetValue - source) * ratio), 0, 255);

            return Windows.UI.Color.FromArgb(
                255,
                Mix(color.R, target, amount),
                Mix(color.G, target, amount),
                Mix(color.B, target, amount));
        }

        /// <summary>
        /// 将使用主题色背景的按钮文字改为白色。
        /// 找到按钮内部的 ContentPresenter（VisualState 实际控制的目标），
        /// 通过 RegisterPropertyChangedCallback 同步拦截 Foreground 变化并改回白色，
        /// 无延迟、无闪烁，彻底解决深色模式悬停/按下文字变黑问题。
        /// </summary>
        private static void FixThemeButtonForeground(ContentDialog dialog)
        {
            string? targetText = dialog.DefaultButton switch
            {
                ContentDialogButton.Primary => dialog.PrimaryButtonText,
                ContentDialogButton.Close => dialog.CloseButtonText,
                _ => null
            };

            if (string.IsNullOrEmpty(targetText) || targetText == "删除" || targetText == "移入回收站" || targetText == "清空")
                return;

            var button = FindButtonByText(dialog, targetText);
            if (button == null) return;

            var whiteBrush = new SolidColorBrush(Colors.White);
            button.Foreground = whiteBrush;

            // 浅色模式无此问题
            if (dialog.ActualTheme != ElementTheme.Dark) return;

            button.Resources["ButtonForegroundPointerOver"] = whiteBrush;
            button.Resources["ButtonForegroundPressed"] = whiteBrush;

            button.ApplyTemplate();
            var cp = FindContentPresenter(button);
            if (cp == null) return;

            cp.Foreground = whiteBrush;

            // 监听 ContentPresenter.Foreground 属性变化，
            // VisualState 将其改为黑色时立即同步改回白色，无延迟无闪烁
            cp.RegisterPropertyChangedCallback(ContentPresenter.ForegroundProperty, (sender, dp) =>
            {
                var brush = (sender as ContentPresenter)?.Foreground as SolidColorBrush;
                if (brush != null && brush.Color != Colors.White)
                {
                    (sender as ContentPresenter)!.Foreground = whiteBrush;
                }
            });
        }

        /// <summary>
        /// 在可视化树中递归查找 ContentPresenter。
        /// </summary>
        private static ContentPresenter? FindContentPresenter(DependencyObject element)
        {
            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                if (child is ContentPresenter cp) return cp;
                var result = FindContentPresenter(child);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// 在可视化树中递归查找包含指定文本内容的 Button 控件。
        /// 支持 Content 为 string 或 TextBlock 两种形式。
        /// </summary>
        private static Button? FindButtonByText(DependencyObject parent, string text)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button button)
                {
                    string? contentText = button.Content switch
                    {
                        string s => s,
                        TextBlock tb => tb.Text,
                        _ => button.Content?.ToString()
                    };
                    if (contentText == text)
                        return button;
                }

                Button? result = FindButtonByText(child, text);
                if (result != null)
                    return result;
            }
            return null;
        }

        #endregion

        private static Task AnimateAsync(
            FrameworkElement target,
            double fromScale,
            double toScale,
            double fromOpacity,
            double toOpacity,
            int durationMilliseconds)
        {
            var transform = target.RenderTransform as CompositeTransform ??
                new CompositeTransform();
            target.RenderTransform = transform;
            target.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            transform.ScaleX = fromScale;
            transform.ScaleY = fromScale;
            target.Opacity = fromOpacity;

            var duration = new Duration(
                TimeSpan.FromMilliseconds(durationMilliseconds));
            var easing = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut
            };
            var scaleX = new DoubleAnimation
            {
                To = toScale,
                Duration = duration,
                EasingFunction = easing,
                EnableDependentAnimation = true
            };
            var scaleY = new DoubleAnimation
            {
                To = toScale,
                Duration = duration,
                EasingFunction = easing,
                EnableDependentAnimation = true
            };
            var opacity = new DoubleAnimation
            {
                To = toOpacity,
                Duration = duration,
                EasingFunction = easing
            };

            Storyboard.SetTarget(scaleX, transform);
            Storyboard.SetTargetProperty(scaleX, nameof(CompositeTransform.ScaleX));
            Storyboard.SetTarget(scaleY, transform);
            Storyboard.SetTargetProperty(scaleY, nameof(CompositeTransform.ScaleY));
            Storyboard.SetTarget(opacity, target);
            Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));

            var storyboard = new Storyboard();
            storyboard.Children.Add(scaleX);
            storyboard.Children.Add(scaleY);
            storyboard.Children.Add(opacity);

            var completion = new TaskCompletionSource();
            storyboard.Completed += (_, _) => completion.TrySetResult();
            storyboard.Begin();
            return completion.Task;
        }
    }
}
