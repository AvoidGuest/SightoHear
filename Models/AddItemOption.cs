namespace SightoHear.Models
{
    /// <summary>
    /// 添加项目弹窗中使用的选择项模型，统一音乐和视频场景。
    /// </summary>
    public class AddItemOption
    {
        public AddItemOption(MediaItem item)
        {
            Item = item;
        }

        public MediaItem Item { get; }
        public bool IsSelected { get; set; }

        /// <summary>
        /// 代理到 MediaItem 的封面显示路径，供添加弹窗列表展示缩略图。
        /// </summary>
        public string CoverDisplayPath => Item.CoverDisplayPath;
    }
}
