using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace SightoHear
{
    /// <summary>
    /// 自定义按钮，鼠标悬停时显示手型光标。
    /// </summary>
    public sealed partial class InfoButton : Button
    {
        private static readonly InputCursor HandCursor =
            InputSystemCursor.Create(InputSystemCursorShape.Hand);

        public InfoButton()
        {
            ProtectedCursor = HandCursor;
        }
    }
}
