using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;

namespace SightoHear
{
    /// <summary>
    /// 自定义 Grid，用于 tab 栏的光标样式。
    /// </summary>
    public sealed partial class TabCursorGrid : Grid
    {
        private readonly InputCursor _hand;
        private readonly InputCursor _grab;

        public TabCursorGrid()
        {
            _hand = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            _grab = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            ProtectedCursor = _hand;
        }

        public void SetGrabCursor() => ProtectedCursor = _grab;
        public void SetHandCursor() => ProtectedCursor = _hand;
    }
}
