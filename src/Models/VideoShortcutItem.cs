namespace SightoHear.Models
{
    /// <summary>
    /// 单个视频快捷键绑定（JSON 序列化持久化到 video_shortcuts.json）。
    /// 页面卡片列表与绑定一一对应，同一行为允许重复添加多个绑定（多个卡片），
    /// 但同一组合键在全部绑定中只能出现一次。
    /// 仅记录行为 ID 与组合键（主键 VirtualKey 值 + Ctrl/Alt/Shift 修饰键）及触发时机/启用状态，
    /// 行为名称/描述在 VideoShortcutService 内置定义。
    /// </summary>
    public class VideoShortcutItem
    {
        /// <summary>绑定的行为 ID（对应 VideoShortcutService.Actions 中的 Id）。</summary>
        public string ActionId { get; set; } = string.Empty;

        /// <summary>该绑定是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>主按键的 VirtualKey 值；null = 未设置快捷键。</summary>
        public int? KeyCode { get; set; }

        /// <summary>是否包含 Ctrl 修饰键。</summary>
        public bool Ctrl { get; set; }

        /// <summary>是否包含 Alt 修饰键。</summary>
        public bool Alt { get; set; }

        /// <summary>是否包含 Shift 修饰键。</summary>
        public bool Shift { get; set; }

        /// <summary>是否在松开按键时执行（false = 按下时执行）。</summary>
        public bool ExecuteOnKeyUp { get; set; }

        /// <summary>是否已设置快捷键。</summary>
        public bool HasKey => KeyCode.HasValue;
    }
}
