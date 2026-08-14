using SightoHear.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SightoHear.Services
{
    /// <summary>
    /// 单个视频的记忆播放位置条目。
    /// </summary>
    public sealed class VideoResumeEntry
    {
        /// <summary>记忆的播放位置（秒）。</summary>
        public double PositionSeconds { get; set; }

        /// <summary>记忆时的视频总时长（秒），用于恢复时校验有效性。</summary>
        public double DurationSeconds { get; set; }

        /// <summary>最近一次更新的时间。</summary>
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// 视频记忆播放位置（续播）存储服务：
    /// 以视频文件路径为键记录每个视频上次观看到的播放位置（秒），
    /// 数据持久化到 %LocalApplicationData%\SightoHear\video_resume_positions.json，应用重启后仍保留。
    /// 仅记录播放位置，不涉及播放速度等其它播放参数。
    /// 容量上限 <see cref="MaxEntries"/>，超出后按最近更新时间淘汰最旧的记录，防止文件无限增长。
    /// </summary>
    public static class VideoResumePositionService
    {
        /// <summary>记录容量上限（超出后淘汰最旧）。</summary>
        private const int MaxEntries = 500;

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "video_resume_positions.json");

        // 键不区分大小写（Windows 文件路径大小写不敏感，避免同一文件因大小写变化产生重复记录）
        private static readonly ConcurrentDictionary<string, VideoResumeEntry> Entries =
            new(StringComparer.OrdinalIgnoreCase);

        static VideoResumePositionService()
        {
            Load();
        }

        /// <summary>获取指定视频的记忆播放位置（无记录或位置无效返回 null）。</summary>
        public static double? GetPosition(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return null;
            if (!Entries.TryGetValue(filePath, out var entry))
                return null;
            // 记忆位置无效（<= 0）或已超过时长（视为已看完）时不恢复
            if (entry.PositionSeconds <= 0 || entry.DurationSeconds <= 0 ||
                entry.PositionSeconds >= entry.DurationSeconds - 10)
                return null;
            return entry.PositionSeconds;
        }

        /// <summary>
        /// 保存指定视频的播放位置并持久化。
        /// 位置与上次保存值相差不足 1 秒时跳过写盘（高频位置回调去重，避免频繁 IO）。
        /// </summary>
        public static void SavePosition(string? filePath, double positionSeconds, double durationSeconds)
        {
            if (string.IsNullOrEmpty(filePath) || positionSeconds <= 0 || durationSeconds <= 0)
                return;

            // 接近结尾（最后 10 秒）视为已看完 → 清除记录，下次从头播放
            if (positionSeconds >= durationSeconds - 10)
            {
                ClearPosition(filePath);
                return;
            }

            bool changed;
            lock (Entries)
            {
                if (Entries.TryGetValue(filePath, out var existing) &&
                    Math.Abs(existing.PositionSeconds - positionSeconds) < 1)
                {
                    return; // 与已保存值几乎相同，无需写盘
                }
                Entries[filePath] = new VideoResumeEntry
                {
                    PositionSeconds = positionSeconds,
                    DurationSeconds = durationSeconds,
                    LastUpdated = DateTime.Now
                };
                changed = true;
            }
            if (changed)
            {
                EnforceCapacity();
                Save();
            }
        }

        /// <summary>清除指定视频的记忆播放位置（播放完毕/用户从头播放时调用）。</summary>
        public static void ClearPosition(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return;
            if (Entries.TryRemove(filePath, out _))
                Save();
        }

        /// <summary>超出容量上限时按最近更新时间淘汰最旧的记录。</summary>
        private static void EnforceCapacity()
        {
            if (Entries.Count <= MaxEntries)
                return;
            // 按 LastUpdated 升序取最旧的条目（超出部分全部移除）
            var oldest = Entries
                .OrderBy(kv => kv.Value.LastUpdated)
                .Take(Entries.Count - MaxEntries)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in oldest)
            {
                Entries.TryRemove(key, out _);
            }
            AppLogger.Debug($"记忆播放位置超出上限，已淘汰 {oldest.Count} 条最旧记录");
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
                    var el = prop.Value;
                    if (el.TryGetProperty("PositionSeconds", out var pos) &&
                        el.TryGetProperty("DurationSeconds", out var dur))
                    {
                        Entries[prop.Name] = new VideoResumeEntry
                        {
                            PositionSeconds = pos.GetDouble(),
                            DurationSeconds = dur.GetDouble(),
                            LastUpdated = el.TryGetProperty("LastUpdated", out var updated)
                                ? updated.GetDateTime()
                                : DateTime.MinValue
                        };
                    }
                }
                AppLogger.Info($"加载记忆播放位置记录 {Entries.Count} 条");
            }
            catch (Exception ex)
            {
                Entries.Clear();
                AppLogger.Error(ex, "加载记忆播放位置记录失败");
            }
        }

        private static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(new Dictionary<string, VideoResumeEntry>(Entries));
                File.WriteAllText(FilePath, json, new UTF8Encoding(false));
                AppLogger.Debug($"保存记忆播放位置记录 {Entries.Count} 条");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "保存记忆播放位置记录失败");
            }
        }
    }
}
