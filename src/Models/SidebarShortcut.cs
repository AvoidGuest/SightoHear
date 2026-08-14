using System;
using System.Text.Json.Serialization;

namespace SightoHear.Models
{
    /// <summary>
    /// 侧边栏固定快捷方式的类型（对应不同模块中可打开详情页的内容）。
    /// </summary>
    public enum SidebarShortcutType
    {
        /// <summary>音乐歌单</summary>
        MusicPlaylist,
        /// <summary>音乐歌手</summary>
        MusicArtist,
        /// <summary>音乐专辑</summary>
        MusicAlbum,
        /// <summary>音乐文件夹</summary>
        MusicFolder,
        /// <summary>视频文件夹</summary>
        VideoFolder,
        /// <summary>视频收藏夹</summary>
        VideoFavorite,
        /// <summary>图库文件夹</summary>
        GalleryFolder,
        /// <summary>图库相册</summary>
        GalleryAlbum
    }

    /// <summary>
    /// 侧边栏固定快捷方式（纯数据载体，支持 JSON 序列化持久化）。
    /// 用户在各页面右键“固定到侧边栏”时创建，点击快捷方式可直达对应详情页。
    /// </summary>
    public class SidebarShortcut
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>快捷方式类型（决定图标与打开逻辑）</summary>
        public SidebarShortcutType Type { get; set; }

        /// <summary>显示标题（如“音乐歌单：我的最爱”）</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>内容名称（歌单名 / 歌手名 / 专辑名 / 收藏夹名等）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>辅助标识（专辑的艺术家、歌单/收藏夹的唯一 Id 等）</summary>
        public string SubName { get; set; } = string.Empty;

        /// <summary>
        /// 去重与查找标识：歌单/收藏夹用 Playlist.Id，文件夹用完整路径，
        /// 歌手用歌手名，专辑用“专辑名|艺术家”。
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>固定时间（用于排序展示）</summary>
        public DateTime DateCreated { get; set; } = DateTime.Now;

        /// <summary>类型排序序号（仅 UI 使用，不参与序列化）</summary>
        [JsonIgnore]
        public int TypeOrder => (int)Type;

        /// <summary>
        /// 根据类型与内容名称生成的统一显示标题（如“音乐歌单：我的最爱”）。
        /// 名称变化时标题自动跟随，供侧边栏 UI 与同步逻辑复用。
        /// </summary>
        [JsonIgnore]
        public string DisplayTitle
        {
            get
            {
                string module = Type switch
                {
                    SidebarShortcutType.MusicPlaylist or SidebarShortcutType.MusicArtist or
                    SidebarShortcutType.MusicAlbum or SidebarShortcutType.MusicFolder => "音乐",
                    SidebarShortcutType.VideoFolder or SidebarShortcutType.VideoFavorite => "视频",
                    _ => "图库"
                };
                string kind = Type switch
                {
                    SidebarShortcutType.MusicPlaylist => "歌单",
                    SidebarShortcutType.MusicArtist => "歌手",
                    SidebarShortcutType.MusicAlbum => "专辑",
                    SidebarShortcutType.MusicFolder => "文件夹",
                    SidebarShortcutType.VideoFolder => "文件夹",
                    SidebarShortcutType.VideoFavorite => "收藏夹",
                    SidebarShortcutType.GalleryFolder => "文件夹",
                    SidebarShortcutType.GalleryAlbum => "相册",
                    _ => "快捷方式"
                };
                return $"{module}{kind}：{Name}";
            }
        }
    }
}
