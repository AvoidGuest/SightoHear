using SightoHear.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SightoHear.Services.Lyrics
{
    /// <summary>
    /// 每首歌独立的歌词延迟存储：
    /// 以歌曲文件路径为键记录用户手动调整的歌词延迟（毫秒），无记录时返回 0（默认不偏移），
    /// 从而保证延迟"只对当前歌曲生效"——换歌自动恢复 0ms，切回原歌时恢复该歌设置的延迟。
    /// 数据持久化到 %LocalApplicationData%\SightoHear\lyrics_delays.json，应用重启后仍保留。
    /// </summary>
    public static class LyricsDelayStore
    {
        private const int MaxDelayMs = 10000; // 与播放器设置弹窗的范围一致

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "lyrics_delays.json");

        private static readonly ConcurrentDictionary<string, int> Delays = new();

        static LyricsDelayStore()
        {
            Load();
        }

        /// <summary>获取指定歌曲的歌词延迟（无记录返回 0）。</summary>
        public static int GetDelay(string? itemPath)
        {
            if (string.IsNullOrEmpty(itemPath))
                return 0;
            return Delays.TryGetValue(itemPath, out int ms) ? ms : 0;
        }

        /// <summary>设置指定歌曲的歌词延迟并持久化（归零时移除记录，保持文件精简）。</summary>
        public static void SetDelay(string? itemPath, int delayMs)
        {
            if (string.IsNullOrEmpty(itemPath))
                return;
            int clamped = Math.Clamp(delayMs, -MaxDelayMs, MaxDelayMs);
            if (clamped == 0)
            {
                if (Delays.TryRemove(itemPath, out _))
                    Save();
            }
            else
            {
                Delays[itemPath] = clamped;
                Save();
            }
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;
                var json = File.ReadAllText(FilePath);
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetInt32(out int ms))
                        Delays[prop.Name] = Math.Clamp(ms, -MaxDelayMs, MaxDelayMs);
                }
                AppLogger.Info($"加载歌词延迟记录 {Delays.Count} 条");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "加载歌词延迟记录失败");
            }
        }

        private static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(new Dictionary<string, int>(Delays));
                File.WriteAllText(FilePath, json, new UTF8Encoding(false));
                AppLogger.Debug($"保存歌词延迟记录 {Delays.Count} 条");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "保存歌词延迟记录失败");
            }
        }
    }
}
