using SightoHear.Helpers;
using SightoHear.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SightoHear.Services
{
    /// <summary>
    /// 侧边栏固定快捷方式服务：
    /// 负责快捷方式的持久化（JSON 存储于 %LocalApplicationData%\SightoHear\sidebar_shortcuts.json）、
    /// 增删查操作以及变更通知（MainWindow 订阅后刷新侧边栏 UI）。
    /// </summary>
    public static class SidebarShortcutService
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "sidebar_shortcuts.json");

        private static readonly object _lock = new();
        private static List<SidebarShortcut> _shortcuts = new();

        /// <summary>快捷方式列表的只读副本。</summary>
        public static IReadOnlyList<SidebarShortcut> Shortcuts
        {
            get { lock (_lock) return _shortcuts.ToList(); }
        }

        /// <summary>快捷方式集合变化时触发（添加 / 移除 / 外部变更）。</summary>
        public static event Action? Changed;

        static SidebarShortcutService()
        {
            Load();
        }

        /// <summary>从磁盘加载快捷方式。</summary>
        public static void Load()
        {
            lock (_lock)
            {
                _shortcuts = new List<SidebarShortcut>();
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var json = File.ReadAllText(FilePath);
                        _shortcuts = JsonSerializer.Deserialize<List<SidebarShortcut>>(json)
                                     ?? new List<SidebarShortcut>();
                        AppLogger.Debug($"侧边栏快捷方式加载: {_shortcuts.Count} 个");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "侧边栏快捷方式加载失败");
                    _shortcuts = new List<SidebarShortcut>();
                }
            }
        }

        /// <summary>保存到磁盘。</summary>
        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(_shortcuts));
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "侧边栏快捷方式保存失败");
                }
            }
        }

        /// <summary>是否已固定（通过 Key 判断，大小写不敏感）。</summary>
        public static bool IsPinned(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;
            lock (_lock)
                return _shortcuts.Any(s =>
                    string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 添加快捷方式。
        /// 若 Key 已存在则返回 false（不重复固定）。
        /// </summary>
        public static bool Add(SidebarShortcut shortcut)
        {
            if (shortcut == null || string.IsNullOrWhiteSpace(shortcut.Key))
                return false;

            // 标题统一由“类型 + 内容名称”生成，保证格式一致
            shortcut.Title = shortcut.DisplayTitle;

            lock (_lock)
            {
                if (_shortcuts.Any(s =>
                    string.Equals(s.Key, shortcut.Key, StringComparison.OrdinalIgnoreCase)))
                    return false;
                _shortcuts.Add(shortcut);
                Save();
            }
            Changed?.Invoke();
            AppLogger.Info($"侧边栏快捷方式添加: {shortcut.Title} ({shortcut.Key})");
            return true;
        }

        /// <summary>
        /// 移除快捷方式（通过 Id 或 Key），返回是否移除成功。
        /// </summary>
        public static bool Remove(string idOrKey)
        {
            if (string.IsNullOrWhiteSpace(idOrKey))
                return false;

            bool removed;
            lock (_lock)
            {
                int count = _shortcuts.Count;
                _shortcuts.RemoveAll(s =>
                    string.Equals(s.Id, idOrKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Key, idOrKey, StringComparison.OrdinalIgnoreCase));
                removed = _shortcuts.Count != count;
                if (removed)
                    Save();
            }
            if (removed)
            {
                Changed?.Invoke();
                AppLogger.Info($"侧边栏快捷方式移除: {idOrKey}");
            }
            return removed;
        }

        /// <summary>通过 Key 查找快捷方式。</summary>
        public static SidebarShortcut? FindByKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;
            lock (_lock)
                return _shortcuts.FirstOrDefault(s =>
                    string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
