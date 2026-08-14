using SightoHear.Helpers;
using SightoHear.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SightoHear.Services
{
    /// <summary>
    /// 视频快捷键行为的元数据定义（名称/描述，内置固定，直接保存显示文本）。
    /// </summary>
    public sealed class VideoShortcutAction
    {
        public string Id { get; }
        public string Name { get; }
        public string Description { get; }

        public VideoShortcutAction(string id, string name, string description)
        {
            Id = id;
            Name = name;
            Description = description;
        }
    }

    /// <summary>
    /// 视频播放器快捷键服务：
    /// 维护内置快捷键行为定义（播放-暂停/音量加/音量减/下一个/上一个/全屏/快进10秒/后退10秒）
    /// 与绑定列表（每个绑定 = 一个设置卡片，允许同一行为重复绑定多个卡片）。
    /// 持久化到 %LocalApplicationData%\SightoHear\video_shortcuts.json。
    /// 规则：同一组合键在全部绑定中只能出现一次；默认每个行为各一个绑定（无按键）。
    /// </summary>
    public static class VideoShortcutService
    {
        /// <summary>内置快捷键行为定义列表（「添加行为」弹窗的选项来源）。</summary>
        public static readonly IReadOnlyList<VideoShortcutAction> Actions = new List<VideoShortcutAction>
        {
            new("TogglePlayPause", "播放-暂停", "播放或暂停当前视频"),
            new("VolumeUp", "音量加", "增大播放音量"),
            new("VolumeDown", "音量减", "减小播放音量"),
            new("NextVideo", "下一个视频", "切换到播放列表中的下一个视频"),
            new("PreviousVideo", "上一个视频", "切换到播放列表中的上一个视频"),
            new("ToggleFullScreen", "全屏", "切换全屏 / 退出全屏"),
            new("Forward10", "快进 10 秒", "当前视频快进 10 秒"),
            new("Backward10", "后退 10 秒", "当前视频后退 10 秒"),
        };

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "video_shortcuts.json");

        // 绑定列表（页面卡片与之一一对应）
        private static readonly List<VideoShortcutItem> Bindings = new();

        /// <summary>快捷键绑定变化时触发（添加/删除/恢复默认等结构性变化后，页面据此刷新 UI）。</summary>
        public static event Action? Changed;

        static VideoShortcutService()
        {
            Load();
            EnsureDefaultBindings();
        }

        /// <summary>全部绑定列表（只读副本，页面按序渲染卡片）。</summary>
        public static IReadOnlyList<VideoShortcutItem> GetAllBindings() => Bindings.ToList();

        /// <summary>无任何绑定时创建默认绑定：每个行为一个绑定（启用、无按键、按下执行）。</summary>
        public static void EnsureDefaultBindings()
        {
            if (Bindings.Count > 0)
                return;
            foreach (var action in Actions)
            {
                Bindings.Add(new VideoShortcutItem { ActionId = action.Id });
            }
            AppLogger.Info($"视频快捷键无配置，已创建默认绑定 {Bindings.Count} 个");
        }

        /// <summary>追加一个新绑定（行为可重复添加，形成新的设置卡片），保存并通知页面刷新。</summary>
        public static VideoShortcutItem AddBinding(string actionId, int? keyCode = null,
            bool ctrl = false, bool alt = false, bool shift = false, bool executeOnKeyUp = false)
        {
            var binding = new VideoShortcutItem
            {
                ActionId = actionId,
                KeyCode = keyCode,
                Ctrl = ctrl,
                Alt = alt,
                Shift = shift,
                ExecuteOnKeyUp = executeOnKeyUp
            };
            Bindings.Add(binding);
            Save();
            AppLogger.Info($"添加行为: {GetActionName(actionId)}（当前共 {Bindings.Count} 个绑定）");
            return binding;
        }

        /// <summary>移除指定绑定（删除对应的设置卡片）。</summary>
        public static void RemoveBinding(VideoShortcutItem binding)
        {
            if (Bindings.Remove(binding))
            {
                Save();
                AppLogger.Info($"删除快捷键绑定: {GetActionName(binding.ActionId)}（剩余 {Bindings.Count} 个）");
            }
        }

        /// <summary>获取行为的显示名称。</summary>
        public static string GetActionName(string actionId)
        {
            foreach (var action in Actions)
            {
                if (string.Equals(action.Id, actionId, StringComparison.OrdinalIgnoreCase))
                    return action.Name;
            }
            return actionId;
        }

        /// <summary>获取行为的描述文本。</summary>
        public static string GetActionDescription(string actionId)
        {
            foreach (var action in Actions)
            {
                if (string.Equals(action.Id, actionId, StringComparison.OrdinalIgnoreCase))
                    return action.Description;
            }
            return string.Empty;
        }

        /// <summary>
        /// 查找占用同一组合键的其它绑定（排除指定绑定实例；exclude 为 null 时检查全部绑定）。
        /// 返回占用绑定的显示文本（行为名 + 快捷键）；无冲突返回 null。
        /// </summary>
        public static string? FindConflict(VideoShortcutItem? exclude, int keyCode, bool ctrl, bool alt, bool shift)
        {
            foreach (var binding in Bindings)
            {
                if (exclude != null && ReferenceEquals(binding, exclude))
                    continue;
                if (!binding.Enabled || !binding.HasKey)
                    continue;
                if (binding.KeyCode == keyCode && binding.Ctrl == ctrl && binding.Alt == alt && binding.Shift == shift)
                {
                    string combo = ShortcutKeyHelper.Format(keyCode, ctrl, alt, shift);
                    return $"{GetActionName(binding.ActionId)}（{combo}）";
                }
            }
            return null;
        }

        /// <summary>
        /// 保存全部绑定配置到磁盘。
        /// </summary>
        /// <param name="notifyChanged">是否触发 <see cref="Changed"/>（页面据此重建卡片列表）。
        /// 卡片上的就地操作（开关/按键捕获）传 false 避免整页重建导致 UI 闪烁/状态丢失；
        /// 添加行为/删除/恢复默认等结构性变化传 true。</param>
        public static void Save(bool notifyChanged = true)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(Bindings);
                File.WriteAllText(FilePath, json, new UTF8Encoding(false));
                AppLogger.Debug($"保存视频快捷键绑定 {Bindings.Count} 条");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "保存视频快捷键绑定失败");
            }
            if (notifyChanged)
            {
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// 恢复默认：重建为每个行为一个绑定（启用、无按键、按下执行），移除所有自定义绑定。
        /// </summary>
        public static void ResetAll()
        {
            Bindings.Clear();
            foreach (var action in Actions)
            {
                Bindings.Add(new VideoShortcutItem { ActionId = action.Id });
            }
            Save();
            AppLogger.Info("视频快捷键已恢复默认（每个行为一个绑定，全部无按键）");
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<List<VideoShortcutItem>>(json);
                if (loaded == null || loaded.Count == 0)
                {
                    // 兼容旧版字典格式：{"ActionId": {...}}，键即行为 ID
                    var legacy = JsonSerializer.Deserialize<Dictionary<string, VideoShortcutItem>>(json);
                    if (legacy != null && legacy.Count > 0)
                    {
                        Bindings.Clear();
                        foreach (var kv in legacy)
                        {
                            kv.Value.ActionId = kv.Key;
                            Bindings.Add(kv.Value);
                        }
                        AppLogger.Info($"加载视频快捷键绑定（旧版格式转换）{Bindings.Count} 条");
                        return;
                    }
                    return;
                }
                Bindings.Clear();
                Bindings.AddRange(loaded);
                AppLogger.Info($"加载视频快捷键绑定 {Bindings.Count} 条");
            }
            catch (Exception ex)
            {
                Bindings.Clear();
                AppLogger.Error(ex, "加载视频快捷键绑定失败");
            }
        }
    }
}
