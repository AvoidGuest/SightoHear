using SightoHear.Helpers;
using SightoHear.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;

namespace SightoHear.Services.Lyrics
{
    /// <summary>
    /// 网络歌词源获取模块。
    /// 在本地歌词缺失时，从多个在线歌词源并发检索、按元数据匹配度择优，
    /// 返回可直接渲染的 <see cref="LyricsData"/>。
    ///
    /// 参考自 BetterLyrics 的 LyricsSearchService，QQ / 网易 / 酷狗 通过项目已引用的
    /// Lyricify.Lyrics.Helper 库检索与解密，LrcLib 走公开 HTTP JSON 接口。
    /// </summary>
    public static class NetworkLyricsService
    {
        // 默认启用的歌词源，顺序同时用作同分时的优先级（越靠前越优先）。
        private static readonly NetworkLyricsProvider[] DefaultProviders =
        [
            NetworkLyricsProvider.QQ,
            NetworkLyricsProvider.Netease,
            NetworkLyricsProvider.Kugou,
            NetworkLyricsProvider.LrcLib,
        ];

        // 低于此匹配度的候选视为不可信，直接丢弃。
        private const int MinAcceptableScore = 45;

        // 达到此匹配度即视为“足够好”，立刻采用并停止等待其余歌词源。
        private const int HighConfidenceScore = 70;

        // 单个歌词源的检索超时。
        private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(8);

        // 整体检索截止时间：即使仍有慢速/不通的源未返回，也不再等待，
        // 直接采用当前已收集到的最佳结果。这是“秒匹配”的关键——避免被卡死的源拖住。
        private static readonly TimeSpan OverallDeadline = TimeSpan.FromSeconds(6);

        // 会话内缓存：同一首歌再次进入时直接命中，无需重新联网检索。
        // 缓存候选结果（含原始歌词文本），供 FetchAsync（解析）与 FetchRawAsync（原文）共用。
        private static readonly ConcurrentDictionary<string, NetworkLyricsCandidate?> Cache = new();

        /// <summary>
        /// 清空歌词缓存。进入全屏播放器时调用，释放累积的歌词数据内存。
        /// </summary>
        public static void ClearCache() => Cache.Clear();

        /// <summary>歌词缓存条目数（供资源诊断服务输出快照）。</summary>
        public static int GetCacheCount() => Cache.Count;

        private static readonly HttpClient LrcLibHttpClient = CreateLrcLibHttpClient();

        private static HttpClient CreateLrcLibHttpClient()
        {
            var client = new HttpClient { Timeout = ProviderTimeout };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "SightoHear (https://github.com/)");
            return client;
        }

        /// <summary>
        /// 为指定曲目从网络获取最佳匹配歌词并解析为可渲染的 <see cref="LyricsData"/>。
        /// 找不到或全部失败时返回 <c>null</c>。
        /// </summary>
        /// <param name="item">当前曲目。</param>
        /// <param name="durationSeconds">曲目时长（秒），可为空。</param>
        /// <param name="preference">歌词源偏好：Auto 表示并发检索全部源择优，否则仅检索固定源。</param>
        /// <param name="token">取消令牌。</param>
        public static async Task<LyricsData?> FetchAsync(
            MediaItem item,
            double? durationSeconds,
            NetworkLyricsSourcePreference preference = NetworkLyricsSourcePreference.Auto,
            CancellationToken token = default)
        {
            NetworkLyricsCandidate? candidate = await FetchRawAsync(item, durationSeconds, preference, token);
            if (candidate is not { HasLyrics: true })
                return null;

            LyricsData? lyrics = BuildLyricsFromCandidate(candidate, durationSeconds);
            if (lyrics == null || lyrics.LyricsLines.Count == 0)
            {
                AppLogger.Info($"网络歌词：{candidate.Provider} 命中但解析为空");
                return null;
            }

            return lyrics;
        }

        /// <summary>
        /// 为指定曲目从网络获取最佳匹配歌词的原始文本（未经解析的 LRC/QRC/KRC/YRC/TTML 原文），
        /// 供"保存当前歌词文件"等需要原文件的场景使用。找不到或全部失败时返回 <c>null</c>。
        /// 与 <see cref="FetchAsync"/> 共用会话缓存，已检索过的曲目直接命中，无需重复联网。
        /// </summary>
        public static async Task<NetworkLyricsCandidate?> FetchRawAsync(
            MediaItem item,
            double? durationSeconds,
            NetworkLyricsSourcePreference preference = NetworkLyricsSourcePreference.Auto,
            CancellationToken token = default)
        {
            string title = string.IsNullOrWhiteSpace(item.Title)
                ? System.IO.Path.GetFileNameWithoutExtension(item.FileName)
                : item.Title;
            string artist = item.Artist ?? string.Empty;
            string album = item.Album ?? string.Empty;
            double? duration = durationSeconds ?? item.Duration?.TotalSeconds;

            if (string.IsNullOrWhiteSpace(title))
            {
                AppLogger.Info("网络歌词：缺少标题，跳过检索");
                return null;
            }

            string cacheKey = BuildCacheKey(title, artist, album, duration, preference);
            if (Cache.TryGetValue(cacheKey, out NetworkLyricsCandidate? cached))
            {
                AppLogger.Info($"网络歌词：命中缓存 (title={title})");
                return cached;
            }

            NetworkLyricsCandidate? best = await SearchBestAsync(title, artist, album, duration, preference, token);
            Cache[cacheKey] = best;
            if (best is not { HasLyrics: true })
            {
                AppLogger.Info($"网络歌词：未找到匹配 (title={title}, artist={artist})");
                return null;
            }

            AppLogger.Info(
                $"网络歌词：选用 {best.Provider} (score={best.MatchScore}, ref={best.Reference})");
            return best;
        }

        private static LyricsData? BuildLyricsFromCandidate(NetworkLyricsCandidate candidate, double? durationSeconds) =>
            LocalLyricsService.BuildFromRaw(
                candidate.Raw, candidate.Translation, candidate.Transliteration, durationSeconds);

        private static string BuildCacheKey(
            string title,
            string artist,
            string album,
            double? duration,
            NetworkLyricsSourcePreference preference) =>
            $"{title.Trim().ToLowerInvariant()}|{artist.Trim().ToLowerInvariant()}|" +
            $"{album.Trim().ToLowerInvariant()}|{(duration is > 0 ? (int)Math.Round(duration.Value) : 0)}|{preference}";

        /// <summary>
        /// 解析设置中保存的歌词源偏好字符串，非法值回退为 <see cref="NetworkLyricsSourcePreference.Auto"/>。
        /// </summary>
        public static NetworkLyricsSourcePreference ParsePreference(string? value) =>
            Enum.TryParse<NetworkLyricsSourcePreference>(value, out NetworkLyricsSourcePreference preference)
                ? preference
                : NetworkLyricsSourcePreference.Auto;

        /// <summary>
        /// 根据用户偏好解析本次检索实际使用的歌词源列表：
        /// Auto 使用全部源（顺序即同分优先级），固定源仅保留单个。
        /// </summary>
        private static NetworkLyricsProvider[] ResolveProviders(NetworkLyricsSourcePreference preference) =>
            preference switch
            {
                NetworkLyricsSourcePreference.QQ => [NetworkLyricsProvider.QQ],
                NetworkLyricsSourcePreference.Netease => [NetworkLyricsProvider.Netease],
                NetworkLyricsSourcePreference.Kugou => [NetworkLyricsProvider.Kugou],
                NetworkLyricsSourcePreference.LrcLib => [NetworkLyricsProvider.LrcLib],
                _ => DefaultProviders,
            };

        /// <summary>
        /// 并发检索歌词源（Auto 为全部源，固定偏好时仅检索该源）。
        /// 谁先返回“足够好”（匹配度 &gt;= <see cref="HighConfidenceScore"/>）
        /// 的结果就立即采用并停止等待其余源；否则在 <see cref="OverallDeadline"/> 截止前收集，
        /// 返回其中匹配度最高者（低于 <see cref="MinAcceptableScore"/> 则为 <c>null</c>）。
        /// </summary>
        public static async Task<NetworkLyricsCandidate?> SearchBestAsync(
            string title,
            string artist,
            string album,
            double? durationSeconds,
            NetworkLyricsSourcePreference preference = NetworkLyricsSourcePreference.Auto,
            CancellationToken token = default)
        {
            NetworkLyricsProvider[] providers = ResolveProviders(preference);

            var pending = providers
                .Select(provider => SearchProviderSafeAsync(provider, title, artist, album, durationSeconds, token))
                .ToList();

            Task deadlineTask = Task.Delay(OverallDeadline, token);
            NetworkLyricsCandidate? best = null;

            while (pending.Count > 0)
            {
                var raceList = new List<Task>(pending) { deadlineTask };
                Task completed = await Task.WhenAny(raceList);

                if (completed == deadlineTask)
                {
                    AppLogger.Info("网络歌词：到达整体截止时间，采用当前最佳结果");
                    break;
                }

                var finishedSearch = (Task<NetworkLyricsCandidate?>)completed;
                pending.Remove(finishedSearch);

                NetworkLyricsCandidate? candidate = await finishedSearch;
                if (candidate is not { HasLyrics: true } || candidate.MatchScore < MinAcceptableScore)
                    continue;

                // 足够好：立即采用，剩余源任其在后台自然结束（不再等待）。
                if (candidate.MatchScore >= HighConfidenceScore)
                    return candidate;

                if (best == null ||
                    candidate.MatchScore > best.MatchScore ||
                    (candidate.MatchScore == best.MatchScore &&
                     Array.IndexOf(providers, candidate.Provider) < Array.IndexOf(providers, best.Provider)))
                {
                    best = candidate;
                }
            }

            return best;
        }

        private static async Task<NetworkLyricsCandidate?> SearchProviderSafeAsync(
            NetworkLyricsProvider provider,
            string title,
            string artist,
            string album,
            double? durationSeconds,
            CancellationToken token)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(ProviderTimeout);

            try
            {
                NetworkLyricsCandidate? candidate = provider switch
                {
                    NetworkLyricsProvider.QQ => await SearchLyricifyAsync(provider, Searchers.QQMusic, title, artist, album, durationSeconds, timeoutCts.Token),
                    NetworkLyricsProvider.Netease => await SearchLyricifyAsync(provider, Searchers.Netease, title, artist, album, durationSeconds, timeoutCts.Token),
                    NetworkLyricsProvider.Kugou => await SearchLyricifyAsync(provider, Searchers.Kugou, title, artist, album, durationSeconds, timeoutCts.Token),
                    NetworkLyricsProvider.LrcLib => await SearchLrcLibAsync(title, artist, album, durationSeconds, timeoutCts.Token),
                    _ => null
                };

                return candidate is { HasLyrics: true } ? candidate : null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Info($"网络歌词：{provider} 检索超时");
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"网络歌词：{provider} 检索失败");
                return null;
            }
        }

        private static async Task<NetworkLyricsCandidate?> SearchLyricifyAsync(
            NetworkLyricsProvider provider,
            Searchers searcher,
            string title,
            string artist,
            string album,
            double? durationSeconds,
            CancellationToken token)
        {
            var metadata = new TrackMultiArtistMetadata
            {
                Title = title,
                Artist = artist,
                Album = album,
                DurationMs = durationSeconds is > 0 ? (int)Math.Round(durationSeconds.Value * 1000) : null,
            };

            ISearchResult? result = await SearchHelper.Search(metadata, searcher, CompareHelper.MatchType.NoMatch);
            token.ThrowIfCancellationRequested();
            if (result == null)
                return null;

            string? raw = null;
            string? translation = null;
            string? transliteration = null;
            string? reference = null;

            switch (result)
            {
                case QQMusicSearchResult qq:
                {
                    var response = await ProviderHelper.QQMusicApi.GetLyricsAsync(qq.Id);
                    raw = response?.Lyrics;
                    translation = response?.Trans;
                    reference = string.IsNullOrEmpty(qq.Mid) ? null : $"https://y.qq.com/n/ryqq/songDetail/{qq.Mid}";
                    break;
                }
                case NeteaseSearchResult netease:
                {
                    var response = await ProviderHelper.NeteaseApi.GetLyric(netease.Id);
                    // 优先逐字歌词（Yrc），回退到逐行歌词（Lrc）。
                    string? yrc = response?.Yrc?.Lyric;
                    if (!string.IsNullOrWhiteSpace(yrc))
                    {
                        raw = yrc;
                        translation = response?.Ytlrc?.Lyric;
                        transliteration = response?.Yromalrc?.Lyric;
                    }
                    else
                    {
                        raw = response?.Lrc?.Lyric;
                        translation = response?.Tlyric?.Lyric;
                        transliteration = response?.Romalrc?.Lyric;
                    }
                    reference = $"https://music.163.com/song?id={netease.Id}";
                    break;
                }
                case KugouSearchResult kugou:
                {
                    var response = await ProviderHelper.KugouApi.GetSearchLyrics(hash: kugou.Hash);
                    var candidate = response?.Candidates?.FirstOrDefault();
                    if (candidate != null)
                    {
                        raw = await Lyricify.Lyrics.Decrypter.Krc.Helper.GetLyricsAsync(candidate.Id, candidate.AccessKey);
                        translation = ExtractKrcTranslation(raw);
                        reference = "https://www.kugou.com/";
                    }
                    break;
                }
            }

            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            int score = LyricsMatchScorer.Score(
                title, artist, album, durationSeconds,
                result.Title, result.Artist, result.Album,
                result.DurationMs is > 0 ? result.DurationMs.Value / 1000.0 : null);

            return new NetworkLyricsCandidate(
                provider,
                result.Title,
                result.Artist,
                result.Album,
                result.DurationMs is > 0 ? result.DurationMs.Value / 1000.0 : null,
                raw,
                translation,
                transliteration,
                reference,
                score);
        }

        /// <summary>
        /// 从酷狗 KRC 中抽取内嵌的中文翻译，转换为逐行 LRC。KRC 的翻译不在主歌词行内，
        /// 需通过 KrcParser 单独解析。
        /// </summary>
        private static string? ExtractKrcTranslation(string? krc)
        {
            if (string.IsNullOrWhiteSpace(krc))
                return null;

            List<ILineInfo>? parsed;
            try
            {
                parsed = Lyricify.Lyrics.Parsers.KrcParser.ParseLyrics(krc);
            }
            catch
            {
                return null;
            }

            if (parsed == null)
                return null;

            var builder = new StringBuilder();
            foreach (ILineInfo line in parsed)
            {
                if (line is not FullSyllableLineInfo fullLine)
                    continue;

                string translation = fullLine.Translations.GetValueOrDefault("zh") ?? "";
                if (string.IsNullOrWhiteSpace(translation))
                    continue;

                var start = TimeSpan.FromMilliseconds(fullLine.StartTime ?? 0);
                builder.Append('[')
                    .Append(start.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture))
                    .Append(']')
                    .Append(translation)
                    .Append('\n');
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        private static async Task<NetworkLyricsCandidate?> SearchLrcLibAsync(
            string title,
            string artist,
            string album,
            double? durationSeconds,
            CancellationToken token)
        {
            string url =
                "https://lrclib.net/api/search?" +
                $"track_name={Uri.EscapeDataString(title)}" +
                $"&artist_name={Uri.EscapeDataString(artist)}" +
                $"&album_name={Uri.EscapeDataString(album)}";

            using var response = await LrcLibHttpClient.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
                return null;

            string json = await response.Content.ReadAsStringAsync(token);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            NetworkLyricsCandidate? best = null;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                string? synced = GetJsonString(element, "syncedLyrics");
                string? plain = GetJsonString(element, "plainLyrics");
                string? raw = string.IsNullOrWhiteSpace(synced) ? plain : synced;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string? remoteTitle = GetJsonString(element, "trackName");
                string? remoteArtist = GetJsonString(element, "artistName");
                string? remoteAlbum = GetJsonString(element, "albumName");
                double? remoteDuration = element.TryGetProperty("duration", out JsonElement durationElement) &&
                                         durationElement.ValueKind == JsonValueKind.Number
                    ? durationElement.GetDouble()
                    : null;

                int score = LyricsMatchScorer.Score(
                    title, artist, album, durationSeconds,
                    remoteTitle, remoteArtist, remoteAlbum, remoteDuration);

                if (best == null || score > best.MatchScore)
                {
                    best = new NetworkLyricsCandidate(
                        NetworkLyricsProvider.LrcLib,
                        remoteTitle,
                        remoteArtist,
                        remoteAlbum,
                        remoteDuration,
                        raw,
                        Reference: url,
                        MatchScore: score);
                }
            }

            return best;
        }

        private static string? GetJsonString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
