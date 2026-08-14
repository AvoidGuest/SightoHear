// 同步系统标题栏按钮（最小化/最大化/关闭）的颜色以匹配当前应用主题。
// 解决 WinUI 3 的一个已知问题：AppWindow.TitleBar 的按钮颜色在应用运行期间切换主题时不会自动更新。

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;

namespace SightoHear.Helpers;

internal static class TitleBarHelper
{
    public static void ApplySystemThemeToCaptionButtons(Window window, ElementTheme currentTheme)
    {
        if (window.AppWindow is not AppWindow appWindow)
            return;

        var foregroundColor = currentTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        appWindow.TitleBar.ButtonForegroundColor = foregroundColor;
        appWindow.TitleBar.ButtonHoverForegroundColor = foregroundColor;

        var backgroundHoverColor = currentTheme == ElementTheme.Dark
            ? Color.FromArgb(24, 255, 255, 255)
            : Color.FromArgb(24, 0, 0, 0);
        appWindow.TitleBar.ButtonHoverBackgroundColor = backgroundHoverColor;
    }
}
