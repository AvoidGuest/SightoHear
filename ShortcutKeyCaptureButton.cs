using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SightoHear.Helpers;
using System;
using Windows.System;

namespace SightoHear
{
    /// <summary>
    /// 快捷键捕获按钮控件：
    /// 未设置时显示"点击设置快捷键"，点击后进入捕获状态（显示"请按下快捷键..."），
    /// 等待用户按下组合键，经 <see cref="ShortcutKeyHelper"/> 校验通过后显示格式化按键文本。
    /// 支持按下 Esc 取消捕获；捕获中失焦自动取消。
    /// 触发事件：
    /// - <see cref="KeyCaptured"/>：捕获到合法组合键（参数：keyCode, ctrl, alt, shift）
    /// - <see cref="ValidationFailed"/>：按下的按键不合法（参数：错误提示文本）
    /// </summary>
    public sealed partial class ShortcutKeyCaptureButton : Button
    {
        private bool _capturing;
        // ★ 抑制"捕获成功后的误触 Click"：捕获空格键成功后，空格 KeyUp 会触发按钮 Click
        //   （WinUI 标准行为），导致刚显示完快捷键又立刻重新进入捕获态。捕获成功时置位，
        //   由紧接着的那次 Click 消费并复位。
        private bool _suppressClick;

        /// <summary>捕获到合法组合键时触发。</summary>
        public event Action<int, bool, bool, bool>? KeyCaptured;

        /// <summary>按键校验失败时触发（参数为错误提示）。</summary>
        public event Action<string>? ValidationFailed;

        /// <summary>主按键 VirtualKey 值；null = 未设置。</summary>
        public int? KeyCode { get; set; }

        /// <summary>是否包含 Ctrl 修饰键。</summary>
        public bool Ctrl { get; set; }

        /// <summary>是否包含 Alt 修饰键。</summary>
        public bool Alt { get; set; }

        /// <summary>是否包含 Shift 修饰键。</summary>
        public bool Shift { get; set; }

        public ShortcutKeyCaptureButton()
        {
            Click += (_, _) =>
            {
                // 吞掉捕获成功后由空格 KeyUp 触发的误触 Click，避免重新进入捕获态
                if (_suppressClick)
                {
                    _suppressClick = false;
                    return;
                }
                StartCapture();
            };
            // ★ 必须用 handledEventsToo 接收按键：Button 类处理器会把 Space/Enter 标记为
            //   已处理并触发 Click，普通 KeyDown 订阅收不到这些键，导致无法捕获空格等按键。
            AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnCaptureKeyDown), true);
            LostFocus += (_, _) =>
            {
                if (_capturing)
                    CancelCapture();
            };
            UpdateDisplay();
        }

        /// <summary>按当前按键配置刷新按钮文本。</summary>
        public void UpdateDisplay()
        {
            Content = _capturing
                ? "请按下快捷键..."
                : ShortcutKeyHelper.Format(KeyCode, Ctrl, Alt, Shift);
        }

        /// <summary>进入捕获状态（按钮获得焦点等待用户按键）。</summary>
        public void StartCapture()
        {
            if (_capturing)
                return;
            _capturing = true;
            UpdateDisplay();
            Focus(FocusState.Programmatic);
        }

        /// <summary>取消捕获状态（保留已有按键配置）。</summary>
        public void CancelCapture()
        {
            if (!_capturing)
                return;
            _capturing = false;
            UpdateDisplay();
        }

        /// <summary>恢复为无按键配置（同时退出捕获状态，显示"点击设置快捷键"）。</summary>
        public void Clear()
        {
            _capturing = false;
            KeyCode = null;
            Ctrl = Alt = Shift = false;
            UpdateDisplay();
        }

        private void OnCaptureKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!_capturing)
                return;

            // Esc / Delete：取消捕获并清除按键（返回"点击设置快捷键"状态）
            if (e.Key == VirtualKey.Escape || e.Key == VirtualKey.Delete)
            {
                Clear();
                e.Handled = true;
                return;
            }

            // 纯修饰键：仅等待主键，不结束捕获
            if (e.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
                or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            {
                e.Handled = true;
                return;
            }

            var (ctrl, alt, shift) = ShortcutKeyHelper.GetModifierState();
            var (valid, error) = ShortcutKeyHelper.Validate((int)e.Key, ctrl, alt, shift);
            if (!valid)
            {
                ValidationFailed?.Invoke(error);
                e.Handled = true;
                return;
            }

            KeyCode = (int)e.Key;
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
            _capturing = false;
            // ★ 空格键按下捕获成功后，其 KeyUp 会触发按钮 Click，置位抑制标志吞掉这次误触
            _suppressClick = true;
            // 兜底：若因焦点变化等原因未收到空格 KeyUp 的 Click，延迟后自动复位，
            // 避免抑制标志残留导致吞掉用户的下一次正常点击
            DispatcherQueue.TryEnqueue(async () =>
            {
                await System.Threading.Tasks.Task.Delay(300);
                _suppressClick = false;
            });
            UpdateDisplay();
            KeyCaptured?.Invoke(KeyCode.Value, ctrl, alt, shift);
            e.Handled = true;
        }
    }
}
