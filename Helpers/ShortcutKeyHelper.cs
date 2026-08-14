using Microsoft.UI.Input;
using System;
using System.Collections.Generic;
using Windows.System;
using Windows.UI.Core;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 视频快捷键按键辅助类：
    /// 负责组合键的校验（限制规则）、显示格式化与当前修饰键状态读取。
    ///
    /// 限制规则（仅禁止不适合作为快捷键的系统/保留按键，单键或 Ctrl/Alt/Shift 组合均允许）：
    /// - 禁止的按键：Esc、Tab、CapsLock、Insert、Delete、PrintScreen、ScrollLock、Pause/Break、
    ///   NumLock、Windows 键、修饰键本身、应用程序键、音量/媒体键、浏览器键、游戏手柄键、F12（调试器保留）
    /// - 禁止的系统组合：Ctrl+Alt+Delete、Alt+F4（Alt+F4 由关闭窗口拦截，无法捕获）
    /// </summary>
    public static class ShortcutKeyHelper
    {
        /// <summary>校验组合键是否合法。返回 (是否合法, 错误提示)。</summary>
        public static (bool IsValid, string Error) Validate(int keyCode, bool ctrl, bool alt, bool shift)
        {
            if (!IsAllowedKey(keyCode))
                return (false, "该按键是系统保留按键，不支持设置为快捷键");
            if (ctrl && alt && keyCode == (int)VirtualKey.Delete)
                return (false, "Ctrl+Alt+Delete 为系统保留组合");
            return (true, string.Empty);
        }

        /// <summary>主按键是否允许作为快捷键（只校验按键本身，不含修饰键组合）。</summary>
        public static bool IsAllowedKey(int keyCode)
        {
            // 0-9（顶部数字行）
            if (keyCode is >= 0x30 and <= 0x39) return true;
            // A-Z
            if (keyCode is >= 0x41 and <= 0x5A) return true;
            // F1-F11（F12 保留给调试器）
            if (keyCode is >= 0x70 and <= 0x7A) return true;
            // 空格
            if (keyCode == 0x20) return true;
            // 方向键
            if (keyCode is >= 0x25 and <= 0x28) return true;
            // PageUp / PageDown / End / Home
            if (keyCode is 0x21 or 0x22 or 0x23 or 0x24) return true;
            // 标点键（Oem 系列）
            if (keyCode is 0xBA or 0xBB or 0xBC or 0xBD or 0xBE or 0xBF or 0xC0
                or 0xDB or 0xDC or 0xDD or 0xDE) return true;
            // 数字小键盘（Num0-Num9 + 运算键）
            if (keyCode is >= 0x60 and <= 0x6F) return true;
            return false;
        }

        /// <summary>读取当前线程的修饰键按下状态（Ctrl/Alt/Shift）。</summary>
        public static (bool Ctrl, bool Alt, bool Shift) GetModifierState()
        {
            bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(CoreVirtualKeyStates.Down);
            bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                .HasFlag(CoreVirtualKeyStates.Down);
            bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down);
            return (ctrl, alt, shift);
        }

        /// <summary>格式化组合键显示文本，如 "Ctrl+Alt+P"；未设置按键返回 "点击设置快捷键"。</summary>
        public static string Format(int? keyCode, bool ctrl, bool alt, bool shift)
        {
            if (keyCode is not int code)
                return "点击设置快捷键";
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            parts.Add(GetKeyName(code));
            return string.Join("+", parts);
        }

        /// <summary>主按键的显示名称（单字符/方向箭头/功能键等）。</summary>
        public static string GetKeyName(int keyCode)
        {
            var key = (VirtualKey)keyCode;
            switch (key)
            {
                case >= VirtualKey.Number0 and <= VirtualKey.Number9:
                    return ((char)('0' + (key - VirtualKey.Number0))).ToString();
                case >= VirtualKey.A and <= VirtualKey.Z:
                    return ((char)('A' + (key - VirtualKey.A))).ToString();
                case >= VirtualKey.F1 and <= VirtualKey.F12:
                    return "F" + (key - VirtualKey.F1 + 1);
                case >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9:
                    return "Num" + (key - VirtualKey.NumberPad0);
                // 数字小键盘运算键（UWP VirtualKey 无对应命名成员，使用键值）
                case (VirtualKey)0x6A: return "Num*";
                case (VirtualKey)0x6B: return "Num+";
                case (VirtualKey)0x6D: return "Num-";
                case (VirtualKey)0x6E: return "Num.";
                case (VirtualKey)0x6F: return "Num/";
                // 标点键（UWP VirtualKey 无对应命名成员，使用键值）
                case (VirtualKey)0xBA: return ";";
                case (VirtualKey)0xBB: return "+";
                case (VirtualKey)0xBC: return ",";
                case (VirtualKey)0xBD: return "-";
                case (VirtualKey)0xBE: return ".";
                case (VirtualKey)0xBF: return "/";
                case (VirtualKey)0xC0: return "`";
                case (VirtualKey)0xDB: return "[";
                case (VirtualKey)0xDC: return "\\";
                case (VirtualKey)0xDD: return "]";
                case (VirtualKey)0xDE: return "'";
            }
            return key switch
            {
                VirtualKey.Space => "Space",
                VirtualKey.Left => "←",
                VirtualKey.Right => "→",
                VirtualKey.Up => "↑",
                VirtualKey.Down => "↓",
                VirtualKey.Home => "Home",
                VirtualKey.End => "End",
                VirtualKey.PageUp => "PageUp",
                VirtualKey.PageDown => "PageDown",
                VirtualKey.Delete => "Delete",
                VirtualKey.Enter => "Enter",
                VirtualKey.Back => "Backspace",
                _ => key.ToString()
            };
        }
    }
}
