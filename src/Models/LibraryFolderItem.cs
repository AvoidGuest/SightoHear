namespace SightoHear.Models
{
    /// <summary>
    /// 媒体库管理弹窗的文件夹数据项。
    /// 用于在视频/音乐/图库页面中展示可勾选的媒体库文件夹。
    /// </summary>
    public class LibraryFolderItem
    {
        /// <summary>文件夹完整路径。</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>是否勾选（勾选后该文件夹内容在当前页面展示）。</summary>
        public bool IsEnabled { get; set; }
    }
}
