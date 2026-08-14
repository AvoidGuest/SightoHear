using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;

namespace SightoHear.Services.Lyrics
{
    public sealed partial class LyricsContentParser
    {
        private readonly XNamespace _ttml = "http://www.w3.org/ns/ttml#metadata";
        private readonly XNamespace _tts = "http://www.w3.org/ns/ttml#styling";
        private readonly XNamespace _itunes = "http://itunes.apple.com/lyric-ttml-extensions";
        private List<LyricsData> _lyricsDataArr = [];

        [GeneratedRegex(@"\[(\d*):(\d*)(\.|\:)(\d*)\]")]
        private static partial Regex LrcRegex();

        [GeneratedRegex(@"(\[|\<)(\d*):(\d*)\.(\d*)(\]|\>)([^\[\]\<\>]*)")]
        private static partial Regex SyllableRegex();

        [GeneratedRegex(@"\<(\d*):(\d*)\.(\d*)\>([^\<\[]*)")]
        private static partial Regex EnhancedLrcSyllableRegex();

        [GeneratedRegex(@"\<(\d+):(\d+)[\.:](\d+)\>")]
        private static partial Regex EnhancedLrcTimeTagRegex();

        [GeneratedRegex(@"\[(\d+),(\d+)\](.*)")]
        private static partial Regex QrcKrcLineRegex();

        [GeneratedRegex(@"(?:\(|<)(\d+),(\d+)(?:,\d+)?(?:\)|>)([^()<>\[]*)")]
        private static partial Regex QrcKrcSyllableRegex();

        [GeneratedRegex(@"([^()<>\[]+?)(?:\((\d+),(\d+)(?:,\d+)?\))")]
        private static partial Regex QrcPostfixSyllableRegex();

        // 匹配网易云 YRC 逐字歌词的 [language:] 特征头
        [GeneratedRegex(@"^\s*\[language\s*[:：]", RegexOptions.Multiline)]
        private static partial Regex YrcLanguageRegex();

        // 匹配被截断的 KRC 时间标签残留：<数字 且数字后不是逗号/数字（即标签不完整，如 "<735"）
        [GeneratedRegex(@"<(\d{2,})(?![,\d])")]
        private static partial Regex ResidualKrcTimingTagRegex();

        public List<LyricsData> Parse(string? raw, double? durationSeconds = null)
        {
            _lyricsDataArr = [];
            if (string.IsNullOrWhiteSpace(raw))
                return _lyricsDataArr;

            LyricsFormat format = DetectFormat(raw);
            if (format == LyricsFormat.Ttml)
                ParseTtml(raw);
            else if (format == LyricsFormat.QrcKrc)
                ParseQrcKrc(raw);
            else
                ParseLrc(raw);

            EnsureSyllables(durationSeconds);
            EnsureEndMs(durationSeconds);

            return _lyricsDataArr;
        }

        private static LyricsFormat DetectFormat(string raw)
        {
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("<tt", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("<tt ", StringComparison.OrdinalIgnoreCase))
            {
                return LyricsFormat.Ttml;
            }

            if (QrcKrcLineRegex().IsMatch(raw) &&
                (QrcKrcSyllableRegex().IsMatch(raw) || QrcPostfixSyllableRegex().IsMatch(raw)))
            {
                return LyricsFormat.QrcKrc;
            }

            return LyricsFormat.Lrc;
        }

        private void ParseQrcKrc(string raw)
        {
            if (TryParseQrcKrcWithLyricify(raw))
                return;

            var lyricsLines = new List<LyricsLine>();
            var lineRegex = QrcKrcLineRegex();
            var syllableRegex = QrcKrcSyllableRegex();

            foreach (string rawLine in raw.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                Match lineMatch = lineRegex.Match(rawLine);
                if (!lineMatch.Success)
                    continue;

                int lineStartMs = int.Parse(lineMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int lineDurationMs = int.Parse(lineMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                string content = lineMatch.Groups[3].Value;
                var syllables = ParseQrcKrcSyllables(content, lineStartMs, lineDurationMs);
                string fullText = syllables.Count > 0
                    ? string.Concat(syllables.Select(s => s.Text))
                    : RemoveQrcKrcTimingTags(content).Trim();

                if (string.IsNullOrWhiteSpace(fullText))
                    continue;

                lyricsLines.Add(new LyricsLine
                {
                    StartMs = lineStartMs,
                    EndMs = lineStartMs + Math.Max(1, lineDurationMs),
                    PrimaryText = fullText,
                    PrimarySyllables = syllables,
                    IsPrimaryHasRealSyllableInfo = syllables.Count > 0
                });
            }

            _lyricsDataArr.Add(new LyricsData(lyricsLines));
        }

        private bool TryParseQrcKrcWithLyricify(string raw)
        {
            // 酷狗部分 KRC 歌词首行缺少 '['（形如 "id:$00000000]" 而非 "[id:$00000000]"），
            // 会导致 Lyricify KrcParser 直接抛 FormatException，使整个 Lyricify 解析链路失效，
            // 而后续 YrcParser 又会把 KRC 内容误解析出残缺行。先规范化首行再解析。
            string normalizedRaw = NormalizeKrcHeader(raw);

            // 网易云 YRC 逐字歌词才带 [language:] 头；KRC/QRC 内容交给 YrcParser 只会产出残缺乱码，
            // 因此仅当内容明显是 YRC 时才尝试 YrcParser。
            bool looksLikeYrc = YrcLanguageRegex().IsMatch(normalizedRaw);

            var parsers = new List<Func<string, List<ILineInfo>?>>
            {
                ParseQrcWithLyricify,
                ParseKrcWithLyricify,
            };
            if (looksLikeYrc)
                parsers.Add(ParseYrcWithLyricify);

            foreach (Func<string, List<ILineInfo>?> parser in parsers)
            {
                try
                {
                    List<ILineInfo>? parsedLines = parser(normalizedRaw);
                    if (parsedLines == null)
                        continue;

                    List<LyricsLine> lyricsLines = ConvertLyricifyLines(parsedLines);
                    // 只有行数足够且不含残留时间标签的结果才可信；
                    // 残缺结果（如 YrcParser 误解析 KRC 得到的 1-2 行乱码）应视为失败，
                    // 回退到自实现的 ParseQrcKrc 兜底。
                    if (lyricsLines.Any(line => line.IsPrimaryHasRealSyllableInfo) &&
                        IsCredibleLyricifyResult(lyricsLines))
                    {
                        _lyricsDataArr.Add(new LyricsData(lyricsLines));
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        /// <summary>
        /// 规范化酷狗 KRC 首行：部分 KRC 歌词首行缺失 '['（形如 "id:$00000000]"），
        /// 补全为 "[id:$00000000]"，避免 Lyricify KrcParser 抛 FormatException。
        /// 非 KRC 内容（首行以 '[' 开头）原样返回。
        /// </summary>
        private static string NormalizeKrcHeader(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return raw;

            int skip = 0;
            while (skip < raw.Length && (raw[skip] == '\uFEFF' || raw[skip] == '\r' || raw[skip] == '\n'))
                skip++;

            if (skip >= raw.Length || raw[skip] == '[')
                return raw;

            if (raw.AsSpan(skip).StartsWith("id:$", StringComparison.OrdinalIgnoreCase))
                return raw[..skip] + "[" + raw[skip..];

            return raw;
        }

        /// <summary>
        /// 校验 Lyricify 解析结果是否可信：
        /// 完整歌词至少 3 行；行文本中残留未闭合的尖括号时间标签（如 " - &lt;735"）说明内容被截断。
        /// 不可信时视为解析失败，回退到自实现解析。
        /// </summary>
        private static bool IsCredibleLyricifyResult(List<LyricsLine> lines)
        {
            if (lines.Count < 3)
                return false;

            foreach (LyricsLine line in lines)
            {
                if (!string.IsNullOrEmpty(line.PrimaryText) &&
                    ResidualKrcTimingTagRegex().IsMatch(line.PrimaryText))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<ILineInfo>? ParseQrcWithLyricify(string raw) =>
            QrcParser.Parse(raw).Lines?.ToList();

        private static List<ILineInfo>? ParseKrcWithLyricify(string raw) =>
            KrcParser.Parse(raw).Lines?.ToList();

        private static List<ILineInfo>? ParseYrcWithLyricify(string raw) =>
            YrcParser.Parse(raw).Lines?.ToList();

        private static List<LyricsLine> ConvertLyricifyLines(IEnumerable<ILineInfo> lines)
        {
            var lyricsLines = new List<LyricsLine>();
            foreach (ILineInfo lineRead in lines.Where(line => !string.IsNullOrEmpty(line.Text)))
            {
                var lineWrite = new LyricsLine
                {
                    StartMs = lineRead.StartTime ?? 0,
                    EndMs = lineRead.EndTime,
                    PrimaryText = lineRead.Text,
                    IsPrimaryHasRealSyllableInfo = false
                };

                if (lineRead is SyllableLineInfo syllableLine && syllableLine.Syllables.Count > 0)
                {
                    int startIndex = 0;
                    foreach (var syllable in syllableLine.Syllables)
                    {
                        if (string.IsNullOrEmpty(syllable.Text))
                            continue;

                        lineWrite.PrimarySyllables.Add(new BaseLyrics
                        {
                            StartMs = syllable.StartTime,
                            EndMs = syllable.EndTime,
                            Text = syllable.Text,
                            StartIndex = startIndex
                        });
                        startIndex += syllable.Text.Length;
                    }

                    lineWrite.IsPrimaryHasRealSyllableInfo = lineWrite.PrimarySyllables.Count > 0;
                    if (lineWrite.EndMs == null && lineWrite.PrimarySyllables.Count > 0)
                        lineWrite.EndMs = lineWrite.PrimarySyllables.Last().EndMs;
                }

                lyricsLines.Add(lineWrite);
            }

            return lyricsLines;
        }

        private static List<BaseLyrics> ParseQrcKrcSyllables(string content, int lineStartMs, int lineDurationMs)
        {
            List<BaseLyrics> syllables = ParsePrefixQrcKrcSyllables(content, lineStartMs, lineDurationMs);
            return syllables.Count > 0
                ? syllables
                : ParsePostfixQrcSyllables(content, lineStartMs, lineDurationMs);
        }

        private static List<BaseLyrics> ParsePrefixQrcKrcSyllables(string content, int lineStartMs, int lineDurationMs)
        {
            var syllables = new List<BaseLyrics>();
            int startIndex = 0;

            foreach (Match syllableMatch in QrcKrcSyllableRegex().Matches(content))
            {
                int rawStartMs = int.Parse(syllableMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int durationMs = int.Parse(syllableMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                string text = syllableMatch.Groups[3].Value;
                AddQrcKrcSyllable(syllables, lineStartMs, lineDurationMs, rawStartMs, durationMs, text, ref startIndex);
            }

            return syllables;
        }

        private static List<BaseLyrics> ParsePostfixQrcSyllables(string content, int lineStartMs, int lineDurationMs)
        {
            var syllables = new List<BaseLyrics>();
            int startIndex = 0;

            foreach (Match syllableMatch in QrcPostfixSyllableRegex().Matches(content))
            {
                string text = syllableMatch.Groups[1].Value;
                int rawStartMs = int.Parse(syllableMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                int durationMs = int.Parse(syllableMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                AddQrcKrcSyllable(syllables, lineStartMs, lineDurationMs, rawStartMs, durationMs, text, ref startIndex);
            }

            return syllables;
        }

        private static void AddQrcKrcSyllable(
            List<BaseLyrics> syllables,
            int lineStartMs,
            int lineDurationMs,
            int rawStartMs,
            int durationMs,
            string text,
            ref int startIndex)
        {
            if (string.IsNullOrEmpty(text))
                return;

            int syllableStartMs = NormalizeQrcKrcSyllableStart(lineStartMs, lineDurationMs, rawStartMs);
            syllables.Add(new BaseLyrics
            {
                StartMs = syllableStartMs,
                EndMs = syllableStartMs + Math.Max(1, durationMs),
                StartIndex = startIndex,
                Text = text
            });

            startIndex += text.Length;
        }

        private static int NormalizeQrcKrcSyllableStart(int lineStartMs, int lineDurationMs, int rawStartMs)
        {
            int lineEndMs = lineStartMs + Math.Max(1, lineDurationMs);
            if (rawStartMs >= lineStartMs - 50 && rawStartMs <= lineEndMs + 50)
                return rawStartMs;

            return lineStartMs + Math.Max(0, rawStartMs);
        }

        private static string RemoveQrcKrcTimingTags(string content)
        {
            string withoutPrefix = QrcKrcSyllableRegex().Replace(content, "$3");
            return QrcPostfixSyllableRegex().Replace(withoutPrefix, "$1");
        }

        private void ParseLrc(string raw)
        {
            var lines = raw.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            var lrcLines = new List<LyricsLine>();
            var syllableRegex = SyllableRegex();

            foreach (var line in lines)
            {
                if (TryAddEnhancedLrcLine(line, lrcLines))
                    continue;

                var matches = syllableRegex.Matches(line);
                var syllables = new List<BaseLyrics>();

                if (TryAddTimedInlineTranslationLine(matches, lrcLines))
                    continue;

                int startIndex = 0;
                for (int i = 0; i < matches.Count; i++)
                {
                    var match = matches[i];
                    int min = int.Parse(match.Groups[2].Value);
                    int sec = int.Parse(match.Groups[3].Value);
                    int ms = int.Parse(match.Groups[4].Value.PadRight(3, '0'));
                    int totalMs = min * 60_000 + sec * 1000 + ms;
                    string text = match.Groups[6].Value;

                    syllables.Add(new BaseLyrics { StartMs = totalMs, Text = text, StartIndex = startIndex });
                    startIndex += text.Length;
                }

                if (IsEnhancedLrcLine(matches, syllables))
                {
                    lrcLines.Add(new LyricsLine
                    {
                        StartMs = syllables[0].StartMs,
                        PrimaryText = string.Concat(syllables.Select(s => s.Text)),
                        PrimarySyllables = syllables,
                        IsPrimaryHasRealSyllableInfo = true
                    });
                }
                else
                {
                    Regex bracketRegex = LrcRegex();
                    var bracketMatches = bracketRegex.Matches(line);

                    if (bracketMatches.Count > 0)
                    {
                        AddPlainLrcSegments(line, bracketMatches, lrcLines);
                    }
                }
            }

            List<LyricsLine> mergedLines = MergeGroupedLrcLines(lrcLines);
            if (mergedLines.Count > 0)
                _lyricsDataArr.Add(new LyricsData(mergedLines));
        }

        private static List<LyricsLine> MergeGroupedLrcLines(List<LyricsLine> lrcLines)
        {
            const int timestampToleranceMs = 50;
            var result = new List<LyricsLine>();
            List<LyricsLine> orderedLines = lrcLines
                .Where(line => !string.IsNullOrWhiteSpace(line.PrimaryText))
                .OrderBy(line => line.StartMs)
                .ToList();

            for (int i = 0; i < orderedLines.Count;)
            {
                int groupStartMs = orderedLines[i].StartMs;
                var linesInGroup = new List<LyricsLine>();

                while (i < orderedLines.Count && Math.Abs(orderedLines[i].StartMs - groupStartMs) <= timestampToleranceMs)
                {
                    linesInGroup.Add(orderedLines[i]);
                    i++;
                }

                LyricsLine primary = linesInGroup.FirstOrDefault(line => line.IsPrimaryHasRealSyllableInfo) ?? linesInGroup[0];
                LyricsLine? secondary = linesInGroup.FirstOrDefault(line =>
                    !ReferenceEquals(line, primary) &&
                    !string.Equals(line.PrimaryText, primary.PrimaryText, StringComparison.Ordinal));

                if (secondary != null && string.IsNullOrWhiteSpace(primary.SecondaryText))
                {
                    // Detect translation vs word-by-word using the raw original line text.
                    bool primaryIsEnhanced = IsLineEnhancedByRawText(primary);
                    bool secondaryIsEnhanced = IsLineEnhancedByRawText(secondary);

                    if (!primaryIsEnhanced && secondaryIsEnhanced)
                    {
                        var temp = primary;
                        primary = secondary;
                        secondary = temp;
                        primary.IsPrimaryHasRealSyllableInfo = false;
                    }

                    primary.SecondaryText = secondary.PrimaryText;
                    // Never copy EndMs from secondary — the translation line's EndMs is derived from
                    // its own [time] tag and the next line's StartMs, which can be far beyond primary's
                    // actual last syllable time and causes the sweep to hang.
                }

                // 支持第三行翻译（例如：主歌词 + 罗马音 + 中文翻译）
                if (linesInGroup.Count > 2 && string.IsNullOrWhiteSpace(primary.TertiaryText))
                {
                    LyricsLine? tertiary = linesInGroup.FirstOrDefault(line =>
                        !ReferenceEquals(line, primary) &&
                        !ReferenceEquals(line, secondary) &&
                        !string.Equals(line.PrimaryText, primary.PrimaryText, StringComparison.Ordinal) &&
                        (secondary == null || !string.Equals(line.PrimaryText, secondary.PrimaryText, StringComparison.Ordinal)));

                    if (tertiary != null)
                    {
                        primary.TertiaryText = tertiary.PrimaryText;
                    }
                }

                result.Add(primary);
            }

            return result;
        }

        private static bool TryAddEnhancedLrcLine(string line, List<LyricsLine> lrcLines)
        {
            MatchCollection timeMatches = EnhancedLrcTimeTagRegex().Matches(line);
            if (timeMatches.Count == 0)
                return false;

            MatchCollection lineTimeMatches = LrcRegex().Matches(line);
            int lineStartMs = lineTimeMatches.Count > 0
                ? ParseLrcLineTime(lineTimeMatches[0])
                : ParseEnhancedLrcTimeTag(timeMatches[0]);

            var syllables = new List<BaseLyrics>();
            int startIndex = 0;
            for (int i = 0; i < timeMatches.Count; i++)
            {
                Match match = timeMatches[i];
                int contentStart = match.Index + match.Length;
                int contentEnd = i + 1 < timeMatches.Count ? timeMatches[i + 1].Index : line.Length;
                string text = RemoveInlineTimeTags(line[contentStart..contentEnd]);
                int tagTimeMs = ParseEnhancedLrcTimeTag(match);

                if (string.IsNullOrWhiteSpace(text))
                {
                    if (syllables.Count > 0)
                        syllables[^1].EndMs = tagTimeMs;
                    continue;
                }

                int? endMs = i + 1 < timeMatches.Count
                    ? ParseEnhancedLrcTimeTag(timeMatches[i + 1])
                    : null;

                syllables.Add(new BaseLyrics
                {
                    StartMs = tagTimeMs,
                    EndMs = endMs,
                    Text = text,
                    StartIndex = startIndex
                });
                startIndex += text.Length;
            }

            if (syllables.Count == 0)
                return false;

            lrcLines.Add(new LyricsLine
            {
                StartMs = lineStartMs,
                EndMs = syllables.Last().EndMs,
                PrimaryText = string.Concat(syllables.Select(s => s.Text)),
                PrimarySyllables = syllables,
                IsPrimaryHasRealSyllableInfo = true,
                RawLine = line
            });
            return true;
        }

        private static string RemoveInlineTimeTags(string text) =>
            string.IsNullOrEmpty(text)
                ? string.Empty
                : EnhancedLrcTimeTagRegex().Replace(text, string.Empty);

        private static string StripInlineTimeTags(string text) =>
            RemoveInlineTimeTags(text).Trim();

        private static int ParseEnhancedLrcTimeTag(Match match)
        {
            int min = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int sec = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int ms = int.Parse(match.Groups[3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
            return min * 60_000 + sec * 1000 + ms;
        }

        private static int ParseEnhancedLrcSyllableTime(Match match)
        {
            int min = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int sec = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int ms = int.Parse(match.Groups[3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
            return min * 60_000 + sec * 1000 + ms;
        }

        private static int ParseLrcLineTime(Match match)
        {
            int min = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int sec = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int ms = int.Parse(match.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
            return min * 60_000 + sec * 1000 + ms;
        }

        private static bool IsEnhancedLrcLine(MatchCollection matches, IReadOnlyList<BaseLyrics> syllables)
        {
            if (matches.Count <= 1)
                return false;

            int nonEmptySyllableCount = syllables.Count(syllable => !string.IsNullOrEmpty(syllable.Text));

            // Only treat as enhanced (word-by-word) if there are <ms> embedded tags
            bool hasAngleSyllableTags = matches.Cast<Match>().Any(match =>
                match.Groups[1].Value == "<" || match.Groups[5].Value == ">");
            if (hasAngleSyllableTags)
                return nonEmptySyllableCount > 0;

            // No <ms> tags — this is a plain LRC line or translation, not word-by-word
            return false;
        }

        // Checks whether a LyricsLine represents a word-by-word (enhanced) lyric by scanning
        // the preserved raw original line text for embedded <ms> tags.
        private static bool IsLineEnhancedByRawText(LyricsLine line)
        {
            if (string.IsNullOrEmpty(line.RawLine))
                return false;
            return EnhancedLrcTimeTagRegex().IsMatch(line.RawLine);
        }

        private static bool IsMultipleLeadingTimestampLine(MatchCollection matches)
        {
            if (matches.Count <= 1)
                return false;

            for (int i = 0; i < matches.Count - 1; i++)
            {
                if (!string.IsNullOrWhiteSpace(matches[i].Groups[6].Value))
                    return false;
            }

            return !string.IsNullOrWhiteSpace(matches[matches.Count - 1].Groups[6].Value);
        }

        private static bool TryAddTimedInlineTranslationLine(MatchCollection matches, List<LyricsLine> lrcLines)
        {
            if (matches.Count != 2)
                return false;

            if (matches.Cast<Match>().Any(match => match.Groups[1].Value == "<" || match.Groups[5].Value == ">"))
                return false;

            string primary = matches[0].Groups[6].Value.Trim();
            string secondary = matches[1].Groups[6].Value.Trim();
            if (primary.Length < 2 || secondary.Length < 2)
                return false;

            bool primaryHasHan = ContainsHan(primary);
            bool secondaryHasHan = ContainsHan(secondary);
            bool primaryHasLatin = ContainsLatin(primary);
            bool secondaryHasLatin = ContainsLatin(secondary);
            bool looksBilingual = primaryHasHan != secondaryHasHan ||
                (primaryHasLatin && secondaryHasHan) ||
                (primaryHasHan && secondaryHasLatin);
            if (!looksBilingual)
                return false;

            int startMs = ParseLrcMatchTime(matches[0]);
            int secondStartMs = ParseLrcMatchTime(matches[1]);
            lrcLines.Add(new LyricsLine
            {
                StartMs = startMs,
                EndMs = secondStartMs > startMs ? secondStartMs : null,
                PrimaryText = primary,
                SecondaryText = secondary,
                IsPrimaryHasRealSyllableInfo = false
            });

            return true;
        }

        private static int ParseLrcMatchTime(Match match)
        {
            int min = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int sec = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            int ms = int.Parse(match.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
            return min * 60_000 + sec * 1000 + ms;
        }

        private static bool ContainsHan(string text) =>
            text.Any(ch => ch is >= '\u3400' and <= '\u9FFF');

        private static bool ContainsLatin(string text) =>
            text.Any(ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'));

        private static void AddPlainLrcSegments(string line, MatchCollection bracketMatches, List<LyricsLine> lrcLines)
        {
            var matches = bracketMatches.Cast<Match>().ToList();
            string trailingContent = line[(matches.Last().Index + matches.Last().Length)..].Trim();
            bool hasOnlyLeadingTimeTags = matches.Count > 1 &&
                matches.Take(matches.Count - 1).All(match =>
                {
                    int contentStart = match.Index + match.Length;
                    int contentEnd = matches[matches.IndexOf(match) + 1].Index;
                    return string.IsNullOrWhiteSpace(line[contentStart..contentEnd]);
                });

            if (hasOnlyLeadingTimeTags)
            {
                if (trailingContent == "//")
                    trailingContent = "";

                foreach (Match match in matches)
                {
                    lrcLines.Add(CreatePlainLrcLine(match, trailingContent, line));
                }

                return;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                int contentStart = match.Index + match.Length;
                int contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
                string content = line[contentStart..contentEnd].Trim();
                if (content == "//")
                    content = "";

                if (string.IsNullOrWhiteSpace(content) && i + 1 < matches.Count)
                    continue;

                lrcLines.Add(CreatePlainLrcLine(match, content, line));
            }
        }

        private static LyricsLine CreatePlainLrcLine(Match match, string content, string? rawLine = null)
        {
            int min = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int sec = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int ms = int.Parse(match.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
            int lineStartMs = min * 60_000 + sec * 1000 + ms;
            content = StripInlineTimeTags(content);

            return new LyricsLine
            {
                StartMs = lineStartMs,
                PrimaryText = content,
                IsPrimaryHasRealSyllableInfo = false,
                RawLine = rawLine
            };
        }

        private void ParseTtml(string raw)
        {
            try
            {
                List<LyricsLine> originalLines = [];
                List<LyricsLine> translationLines = [];
                List<LyricsLine> romanLines = [];

                var xdoc = XDocument.Parse(raw, LoadOptions.PreserveWhitespace);
                Dictionary<string, List<XElement>> headTransDict = [];
                Dictionary<string, List<XElement>> headRomanDict = [];

                var head = xdoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "head");
                if (head != null)
                {
                    var texts = head.Descendants().Where(e => e.Name.LocalName == "text");
                    foreach (var text in texts)
                    {
                        var forKey = text.Attribute("for")?.Value;
                        if (string.IsNullOrEmpty(forKey)) continue;

                        var grandParent = text.Parent?.Parent?.Name.LocalName;
                        if (grandParent == "translations")
                        {
                            if (!headTransDict.ContainsKey(forKey)) headTransDict[forKey] = [];
                            headTransDict[forKey].Add(text);
                        }
                        else if (grandParent == "transliterations")
                        {
                            if (!headRomanDict.ContainsKey(forKey)) headRomanDict[forKey] = [];
                            headRomanDict[forKey].Add(text);
                        }
                    }
                }

                var body = xdoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
                if (body == null) return;

                var ps = body.Descendants().Where(e => e.Name.LocalName == "p");
                foreach (var p in ps)
                {
                    string pKey = p.Attribute(_itunes + "key")?.Value ?? "";
                    string agentId = p.Attribute(_ttml + "agent")?.Value ?? "";

                    ParseTtmlSegment(p, originalLines, translationLines, romanLines, agentId);

                    var currentOriginalLine = originalLines.LastOrDefault();
                    int pStart = currentOriginalLine?.StartMs ?? 0;
                    int pEnd = currentOriginalLine?.EndMs ?? 0;

                    if (!string.IsNullOrEmpty(pKey))
                    {
                        if (headTransDict.TryGetValue(pKey, out var transTexts))
                        {
                            foreach (var tText in transTexts)
                            {
                                ParseTtmlSegment(tText, translationLines, null, null, agentId, pStart, pEnd);
                                var textBgSpans = tText.Elements().Where(s => s.Attribute(_ttml + "role")?.Value == "x-bg");
                                foreach (var bg in textBgSpans)
                                    ParseTtmlSegment(bg, translationLines, null, null, agentId, pStart, pEnd);
                            }
                        }

                        if (headRomanDict.TryGetValue(pKey, out var romanTexts))
                        {
                            foreach (var rText in romanTexts)
                            {
                                ParseTtmlSegment(rText, romanLines, null, null, agentId, pStart, pEnd);
                                var textBgSpans = rText.Elements().Where(s => s.Attribute(_ttml + "role")?.Value == "x-bg");
                                foreach (var bg in textBgSpans)
                                    ParseTtmlSegment(bg, romanLines, null, null, agentId, pStart, pEnd);
                            }
                        }
                    }

                    var bgSpans = p.Elements().Where(s => s.Attribute(_ttml + "role")?.Value == "x-bg");
                    foreach (var bgSpan in bgSpans)
                    {
                        ParseTtmlSegment(
                            container: bgSpan,
                            primaryDest: originalLines,
                            transDest: translationLines,
                            romanDest: romanLines,
                            fallbackStartMs: pStart,
                            fallbackEndMs: pEnd,
                            agentId: agentId);
                    }
                }

                _lyricsDataArr.Add(new LyricsData(originalLines));
                if (translationLines.Count > 0)
                    _lyricsDataArr.Add(new LyricsData(translationLines));
                if (romanLines.Count > 0)
                    _lyricsDataArr.Add(new LyricsData(romanLines) { LanguageCode = "romaji" });
            }
            catch
            {
            }
        }

        private void ParseTtmlSegment(
            XElement container,
            List<LyricsLine>? primaryDest,
            List<LyricsLine>? transDest,
            List<LyricsLine>? romanDest,
            string agentId,
            int fallbackStartMs = 0,
            int fallbackEndMs = 0)
        {
            int startMs = fallbackStartMs;
            var beginAttr = container.Attribute("begin");
            if (beginAttr != null) startMs = ParseTtmlTime(beginAttr.Value);

            int? endMs = fallbackEndMs;
            var endAttr = container.Attribute("end");
            if (endAttr != null) endMs = ParseTtmlTime(endAttr.Value);

            var syllables = new List<BaseLyrics>();
            var sbText = new StringBuilder();
            int startIndex = 0;
            BaseLyrics? lastSyllable = null;

            foreach (var node in container.Nodes())
            {
                if (node is XText xText)
                {
                    string textVal = xText.Value;
                    if (textVal.Contains('\n'))
                        textVal = textVal.Trim(' ', '\t', '\r', '\n');
                    if (string.IsNullOrEmpty(textVal))
                        continue;

                    if (lastSyllable != null)
                        lastSyllable.Text += textVal;

                    sbText.Append(textVal);
                    startIndex += textVal.Length;
                }
                else if (node is XElement xElement && xElement.Name.LocalName == "span")
                {
                    string? role = xElement.Attribute(_ttml + "role")?.Value;
                    if (role == "x-bg" || role == "x-translation" || role == "x-roman")
                        continue;

                    string? rubyAttr = xElement.Attribute(_tts + "ruby")?.Value;
                    string textVal;
                    int sStartMs = startMs;
                    int? sEndMs = endMs;

                    if (rubyAttr == "container")
                    {
                        var baseSpan = xElement.Elements().FirstOrDefault(e => e.Attribute(_tts + "ruby")?.Value == "base");
                        var textSpans = xElement.Descendants().Where(e => e.Attribute(_tts + "ruby")?.Value == "text").ToList();

                        textVal = baseSpan?.Value ?? "";
                        int firstTime = ParseTtmlTime(textSpans.FirstOrDefault()?.Attribute("begin")?.Value ?? xElement.Attribute("begin")?.Value);
                        int lastTime = ParseTtmlTime(textSpans.LastOrDefault()?.Attribute("end")?.Value ?? xElement.Attribute("end")?.Value);

                        sStartMs = firstTime != 0 ? firstTime : startMs;
                        sEndMs = lastTime != 0 ? lastTime : endMs;
                    }
                    else
                    {
                        textVal = xElement.Value;
                        int bTime = ParseTtmlTime(xElement.Attribute("begin")?.Value);
                        int eTime = ParseTtmlTime(xElement.Attribute("end")?.Value);

                        sStartMs = bTime != 0 ? bTime : startMs;
                        sEndMs = eTime != 0 ? eTime : endMs;
                    }

                    if (!string.IsNullOrEmpty(textVal))
                    {
                        var syl = new BaseLyrics
                        {
                            StartMs = sStartMs,
                            EndMs = sEndMs,
                            StartIndex = startIndex,
                            Text = textVal
                        };
                        syllables.Add(syl);
                        lastSyllable = syl;

                        sbText.Append(textVal);
                        startIndex += textVal.Length;
                    }
                }
            }

            string fullPrimaryText = sbText.ToString().Trim();
            if (beginAttr == null && syllables.Count > 0) startMs = syllables.First().StartMs;
            if (endAttr == null && syllables.Count > 0) endMs = syllables.Last().EndMs;

            if (!string.IsNullOrWhiteSpace(fullPrimaryText) && primaryDest != null)
            {
                primaryDest.Add(new LyricsLine
                {
                    StartMs = startMs,
                    EndMs = endMs,
                    PrimaryText = fullPrimaryText,
                    PrimarySyllables = syllables,
                    IsPrimaryHasRealSyllableInfo = syllables.Count > 0,
                    AgentId = agentId
                });
            }

            if (transDest != null)
            {
                var transSpan = container.Elements().FirstOrDefault(s => s.Attribute(_ttml + "role")?.Value == "x-translation");
                AddAuxiliaryLine(transDest, transSpan, startMs, endMs);
            }

            if (romanDest != null)
            {
                var romanSpan = container.Elements().FirstOrDefault(s => s.Attribute(_ttml + "role")?.Value == "x-roman");
                AddAuxiliaryLine(romanDest, romanSpan, startMs, endMs);
            }
        }

        private void AddAuxiliaryLine(List<LyricsLine> destList, XElement? span, int startMs, int? endMs)
        {
            if (span != null && !string.IsNullOrWhiteSpace(span.Value))
            {
                destList.Add(new LyricsLine
                {
                    StartMs = startMs,
                    EndMs = endMs,
                    PrimaryText = span.Value.Trim(),
                    IsPrimaryHasRealSyllableInfo = false,
                });
            }
        }

        private static int ParseTtmlTime(string? t)
        {
            if (string.IsNullOrWhiteSpace(t))
                return 0;

            t = t.Trim();
            var parts = t.Split(':');

            try
            {
                if (parts.Length == 3)
                {
                    int h = int.Parse(parts[0]);
                    int m = int.Parse(parts[1]);
                    double s = double.Parse(parts[2], CultureInfo.InvariantCulture);
                    return (int)((h * 3600 + m * 60 + s) * 1000);
                }
                else if (parts.Length == 2)
                {
                    int m = int.Parse(parts[0]);
                    double s = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    return (int)((m * 60 + s) * 1000);
                }
                else if (parts.Length == 1)
                {
                    double s = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    return (int)(s * 1000);
                }
            }
            catch
            {
            }

            return 0;
        }

        private void EnsureEndMs(double? duration)
        {
            foreach (var lyricsData in _lyricsDataArr)
            {
                var lines = lyricsData.LyricsLines;
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    bool isLastLine = i + 1 >= lines.Count;
                    var nextLineStartMs = isLastLine ? (int)((duration ?? 0) * 1000) : lines[i + 1].StartMs;

                    if (line.EndMs == null)
                    {
                        if (line.PrimarySyllables.Count > 0)
                            line.EndMs = line.PrimarySyllables.Last().EndMs;
                        else
                            line.EndMs = line.StartMs >= nextLineStartMs ? line.StartMs + 1000 : nextLineStartMs;
                    }
                }
            }
        }

        private void EnsureSyllables(double? duration)
        {
            foreach (var lyricsData in _lyricsDataArr)
            {
                var lines = lyricsData.LyricsLines;
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    var nextLineStartMs = (i + 1 < lines.Count) ? lines[i + 1].StartMs : (int)((duration ?? 0) * 1000);

                    if (line.PrimarySyllables.Count > 0)
                    {
                        NormalizePrimarySyllables(line, nextLineStartMs);
                        if (!line.IsPrimaryHasRealSyllableInfo && line.PrimarySyllables.Count == 0)
                            AddEstimatedSyllables(line, nextLineStartMs);
                    }
                    else if (!line.IsPrimaryHasRealSyllableInfo)
                    {
                        line.PrimarySyllables.Clear();
                        AddEstimatedSyllables(line, nextLineStartMs);
                    }
                }
            }
        }

        private static void NormalizePrimarySyllables(LyricsLine line, int nextLineStartMs)
        {
            bool hasRealSyllableInfo = line.IsPrimaryHasRealSyllableInfo;
            line.PrimarySyllables = line.PrimarySyllables
                .Where(syllable => !string.IsNullOrEmpty(syllable.Text))
                .ToList();

            if (line.PrimarySyllables.Count == 0)
            {
                line.IsPrimaryHasRealSyllableInfo = false;
                return;
            }

            AlignPrimarySyllableIndexes(line);

            int firstStartMs = line.PrimarySyllables[0].StartMs;
            if (line.StartMs <= 0 || line.StartMs > firstStartMs)
                line.StartMs = firstStartMs;

            for (int j = 0; j < line.PrimarySyllables.Count; j++)
            {
                BaseLyrics syllable = line.PrimarySyllables[j];
                int? nextSyllableStartMs = j < line.PrimarySyllables.Count - 1
                    ? line.PrimarySyllables[j + 1].StartMs
                    : null;

                int fallbackEndMs = nextSyllableStartMs ??
                    (nextLineStartMs > syllable.StartMs
                        ? nextLineStartMs
                        : syllable.StartMs + Math.Max(1, syllable.Length));

                int endMs = syllable.EndMs ?? fallbackEndMs;
                if (endMs <= syllable.StartMs)
                    endMs = fallbackEndMs;

                if (nextSyllableStartMs.HasValue &&
                    nextSyllableStartMs.Value > syllable.StartMs &&
                    endMs > nextSyllableStartMs.Value)
                {
                    endMs = nextSyllableStartMs.Value;
                }

                if (endMs <= syllable.StartMs)
                    endMs = syllable.StartMs + Math.Max(1, syllable.Length);

                syllable.EndMs = endMs;
            }

            int lastEndMs = line.PrimarySyllables.Last().EndMs ?? line.PrimarySyllables.Last().StartMs;
            if (line.EndMs == null || line.EndMs <= line.StartMs || line.EndMs < lastEndMs)
                line.EndMs = Math.Max(lastEndMs, line.StartMs + 1);

            line.IsPrimaryHasRealSyllableInfo = hasRealSyllableInfo && HasUsableRealSyllables(line);
            if (!line.IsPrimaryHasRealSyllableInfo)
                line.PrimarySyllables.Clear();
        }

        private static void AlignPrimarySyllableIndexes(LyricsLine line)
        {
            if (!TryAlignPrimarySyllablesToText(line))
            {
                line.PrimaryText = string.Concat(line.PrimarySyllables.Select(syllable => syllable.Text));
                int startIndex = 0;
                foreach (BaseLyrics syllable in line.PrimarySyllables)
                {
                    syllable.StartIndex = startIndex;
                    startIndex += syllable.Text.Length;
                }
            }
        }

        private static bool TryAlignPrimarySyllablesToText(LyricsLine line)
        {
            if (string.IsNullOrEmpty(line.PrimaryText))
                return false;

            int searchStartIndex = 0;
            foreach (BaseLyrics syllable in line.PrimarySyllables)
            {
                int index = line.PrimaryText.IndexOf(syllable.Text, searchStartIndex, StringComparison.Ordinal);
                if (index < 0)
                    return false;

                syllable.StartIndex = index;
                searchStartIndex = index + syllable.Text.Length;
            }

            return true;
        }

        private static bool HasUsableRealSyllables(LyricsLine line) =>
            line.PrimarySyllables.Count > 0 &&
            line.PrimarySyllables.Any(syllable => syllable.EndMs > syllable.StartMs) &&
            line.PrimarySyllables.Any(syllable =>
            {
                // Use stripped text length for boundary check (Text may contain inline time tags)
                string stripped = StripInlineTimeTags(syllable.Text);
                int effectiveEnd = syllable.StartIndex + stripped.Length - 1;
                return syllable.StartIndex >= 0 &&
                       syllable.StartIndex < line.PrimaryText.Length &&
                       effectiveEnd < line.PrimaryText.Length;
            });

        private static void AddEstimatedSyllables(LyricsLine line, int nextLineStartMs)
        {
            string content = line.PrimaryText;
            if (string.IsNullOrEmpty(content))
                return;

            int endMs = nextLineStartMs > line.StartMs
                ? nextLineStartMs
                : line.StartMs + 1000;
            int durationMs = endMs - line.StartMs;
            if (durationMs <= 0)
                return;

            int avgSyllableDuration = Math.Max(1, durationMs / content.Length);
            for (int i = 0; i < content.Length; i++)
            {
                int startMs = line.StartMs + avgSyllableDuration * i;
                line.PrimarySyllables.Add(new BaseLyrics
                {
                    StartMs = startMs,
                    EndMs = i == content.Length - 1 ? endMs : Math.Min(endMs, startMs + avgSyllableDuration),
                    StartIndex = i,
                    Text = content[i].ToString()
                });
            }
        }

        private enum LyricsFormat
        {
            Lrc,
            Ttml,
            QrcKrc
        }
    }
}
