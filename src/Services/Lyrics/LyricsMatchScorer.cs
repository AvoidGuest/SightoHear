using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SightoHear.Services.Lyrics
{
    /// <summary>
    /// 依据标题 / 艺术家 / 专辑 / 时长计算本地曲目与网络候选之间的匹配度（0-100）。
    /// 权重与思路参考自 BetterLyrics 的 MetadataComparer，但改为无外部依赖的实现。
    /// </summary>
    public static partial class LyricsMatchScorer
    {
        private const double WeightTitle = 0.30;
        private const double WeightArtist = 0.30;
        private const double WeightAlbum = 0.10;
        private const double WeightDuration = 0.30;

        private const double DurationPerfectToleranceSeconds = 1.0;
        private const double DurationMaxToleranceSeconds = 10.0;

        public static int Score(
            string localTitle,
            string localArtist,
            string localAlbum,
            double? localDurationSeconds,
            string? remoteTitle,
            string? remoteArtist,
            string? remoteAlbum,
            double? remoteDurationSeconds)
        {
            bool localHasMetadata = !string.IsNullOrWhiteSpace(localTitle);
            bool remoteHasMetadata = !string.IsNullOrWhiteSpace(remoteTitle);

            double totalScore;
            if (localHasMetadata && remoteHasMetadata)
            {
                double titleScore = StringSimilarity(localTitle, remoteTitle);
                double artistScore = StringSimilarity(localArtist, remoteArtist);
                double albumScore = StringSimilarity(localAlbum, remoteAlbum);
                double durationScore = DurationSimilarity(localDurationSeconds, remoteDurationSeconds);

                totalScore = (titleScore * WeightTitle) +
                             (artistScore * WeightArtist) +
                             (albumScore * WeightAlbum) +
                             (durationScore * WeightDuration);
            }
            else
            {
                // 缺少结构化元数据时，退化为基于排序指纹的整体相似度比较。
                string localFingerprint = SortedFingerprint($"{localTitle} {localArtist}");
                string remoteFingerprint = SortedFingerprint($"{remoteTitle} {remoteArtist}");
                totalScore = string.IsNullOrWhiteSpace(localFingerprint) || string.IsNullOrWhiteSpace(remoteFingerprint)
                    ? 0
                    : NormalizedSimilarity(localFingerprint, remoteFingerprint);
            }

            return (int)Math.Round(Math.Clamp(totalScore, 0, 1) * 100);
        }

        private static double StringSimilarity(string? a, string? b)
        {
            a = a?.Trim().ToLowerInvariant() ?? "";
            b = b?.Trim().ToLowerInvariant() ?? "";

            if (a.Length == 0 && b.Length == 0)
                return 1.0;
            if (a.Length == 0 || b.Length == 0)
                return 0.0;

            return NormalizedSimilarity(a, b);
        }

        private static double DurationSimilarity(double? localSeconds, double? remoteSeconds)
        {
            if (localSeconds is null or <= 0 || remoteSeconds is null or <= 0)
                return 0.0;

            double diff = Math.Abs(localSeconds.Value - remoteSeconds.Value);
            if (diff <= DurationPerfectToleranceSeconds)
                return 1.0;
            if (diff >= DurationMaxToleranceSeconds)
                return 0.0;

            return 1.0 - ((diff - DurationPerfectToleranceSeconds) /
                          (DurationMaxToleranceSeconds - DurationPerfectToleranceSeconds));
        }

        /// <summary>
        /// 基于 Levenshtein 编辑距离的归一化相似度（1 表示完全相同）。
        /// </summary>
        private static double NormalizedSimilarity(string a, string b)
        {
            int distance = Levenshtein(a, b);
            int longest = Math.Max(a.Length, b.Length);
            return longest == 0 ? 1.0 : 1.0 - ((double)distance / longest);
        }

        private static int Levenshtein(string a, string b)
        {
            if (a.Length == 0)
                return b.Length;
            if (b.Length == 0)
                return a.Length;

            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++)
                previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }

        private static string SortedFingerprint(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            string cleaned = NonWordCharactersRegex().Replace(input.ToLowerInvariant(), " ");
            IEnumerable<string> tokens = cleaned
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(token => token, StringComparer.Ordinal);
            return string.Join(" ", tokens);
        }

        [GeneratedRegex(@"[\p{P}\p{S}]")]
        private static partial Regex NonWordCharactersRegex();
    }
}
