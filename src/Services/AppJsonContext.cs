using System.Collections.Generic;
using System.Text.Json.Serialization;
using SightoHear.Models;

namespace SightoHear.Services
{
    /// <summary>
    /// ★ Native AOT 关键配置：System.Text.Json 源生成上下文。
    ///
    /// Native AOT / 裁剪后**没有运行时反射**（JsonSerializer.IsReflectionEnabledByDefault = false），
    /// 任何 `JsonSerializer.Serialize&lt;T&gt;` / `Deserialize&lt;T&gt;` 若不配合源生成，
    /// 运行时都会抛：
    ///   InvalidOperationException: Reflection-based serialization has been disabled for this application.
    /// 反之，源生成在编译期为每个类型生成读写器，AOT 下零反射、启动更快、体积更小。
    ///
    /// 用法（替换所有泛型调用）：
    ///   JsonSerializer.Serialize(obj, AppJsonContext.Default.ListPlaylist)
    ///   JsonSerializer.Deserialize(json, AppJsonContext.Default.ListPlaylist)
    ///
    /// 新增可持久化类型时，务必在此处补一行 [JsonSerializable]，否则 AOT 版会静默失败
    /// （调用点通常有 try/catch，只会记录日志而不会崩溃，容易漏测）。
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        // 保持与原有序列化行为一致：属性名原样输出（PascalCase），不做 camelCase 转换，
        // 否则已有用户数据（settings.json / playlists.json 等）将无法反序列化。
        PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
    [JsonSerializable(typeof(List<Playlist>))]
    [JsonSerializable(typeof(Playlist))]
    [JsonSerializable(typeof(List<MediaItem>))]
    [JsonSerializable(typeof(MediaItem))]
    [JsonSerializable(typeof(List<VideoShortcutItem>))]
    [JsonSerializable(typeof(VideoShortcutItem))]
    [JsonSerializable(typeof(Dictionary<string, VideoShortcutItem>))]
    [JsonSerializable(typeof(Dictionary<string, VideoResumeEntry>))]
    [JsonSerializable(typeof(List<SidebarShortcut>))]
    [JsonSerializable(typeof(Dictionary<string, int>))]
    // JsonNode 相关：AOT 下 JsonArray.Add<T>(T) 会走 ConvertFromValue → JsonSerializerOptions.Default
    // （AOT 中是 EmptyJsonTypeInfoResolver）而抛 NotSupportedException，
    // 必须改用 JsonValue.Create(...) 并登记这些节点类型（见 JsonNodeHelper）。
    [JsonSerializable(typeof(System.Text.Json.Nodes.JsonArray))]
    [JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
    [JsonSerializable(typeof(System.Text.Json.Nodes.JsonValue))]
    [JsonSerializable(typeof(List<string>))]
    // MusicDataCache 的歌单缓存 DTO（结构不同于 Models.Playlist：使用 MediaItemDto 精简字段）
    [JsonSerializable(typeof(List<PlaylistCacheDto>))]
    [JsonSerializable(typeof(PlaylistCacheDto))]
    [JsonSerializable(typeof(List<MediaItemCacheDto>))]
    [JsonSerializable(typeof(MediaItemCacheDto))]
    internal sealed partial class AppJsonContext : JsonSerializerContext
    {
    }
}
