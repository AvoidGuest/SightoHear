using SightoHear.Models;
using SightoHear.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SightoHear.Services.Lyrics
{
    public static class LocalLyricsService
    {
        private const int MaxTagBytes = 32 * 1024 * 1024;
        private const int MaxCommentScanBytes = 8 * 1024 * 1024;
        private static readonly string[] Extensions = [".ttml", ".qrc", ".krc", ".yrc", ".eslrc", ".lrc", ".txt"];

        /// <summary>
        /// 本地歌词的原始内容载体：<see cref="RawText"/> 为未经解析的原始歌词文本，
        /// <see cref="SourcePath"/> 为来源歌词文件路径（嵌入标签歌词为 <c>null</c>），
        /// <see cref="IsEmbedded"/> 表示歌词来自音频文件嵌入标签。
        /// </summary>
        public sealed record LocalRawLyrics(string RawText, string? SourcePath, bool IsEmbedded);

        public static async Task<LyricsData?> LoadAsync(MediaItem item, double? durationSeconds)
        {
            LocalLyricsCandidate? localLyrics = await TryLoadBestLocalLyricsCandidateAsync(item, durationSeconds);
            if (localLyrics != null)
                return localLyrics.Lyrics;

            string? raw = TryExtractEmbeddedLyrics(item.FilePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                AppLogger.Info($"No lyrics found: {item.FilePath}");
                return null;
            }

            AppLogger.Info($"Embedded lyrics found: {item.FilePath}");
            LyricsData? embeddedLyrics = ParseLyricsPayload(raw, null, null, durationSeconds);
            if (embeddedLyrics != null)
                AppLogger.Info($"Embedded lyrics parsed: {DescribeLyrics(embeddedLyrics)}");
            return embeddedLyrics;
        }

        /// <summary>
        /// 获取本地歌词的原始文本（未经解析）。
        /// 优先返回文件系统中评分最高的歌词文件原文及其路径；
        /// 无文件歌词时返回音频嵌入标签中提取的原始歌词文本。
        /// 找不到任何本地歌词时返回 <c>null</c>。
        /// </summary>
        public static async Task<LocalRawLyrics?> GetRawLocalLyricsAsync(MediaItem item)
        {
            LocalLyricsCandidate? candidate = await TryLoadBestLocalLyricsCandidateAsync(item, null);
            if (candidate != null)
                return new LocalRawLyrics(candidate.RawText, candidate.SourcePath, IsEmbedded: false);

            string? embeddedRaw = TryExtractEmbeddedLyrics(item.FilePath);
            if (string.IsNullOrWhiteSpace(embeddedRaw))
            {
                AppLogger.Info($"No raw lyrics found: {item.FilePath}");
                return null;
            }

            return new LocalRawLyrics(embeddedRaw, null, IsEmbedded: true);
        }

        /// <summary>
        /// 将原始歌词文本（可附带翻译、音译轨道）解析并合并为可直接渲染的 <see cref="LyricsData"/>。
        /// 供网络歌词源复用本地歌词的解析与轨道合并逻辑。
        /// </summary>
        public static LyricsData? BuildFromRaw(string? raw, string? translation, string? transliteration, double? durationSeconds) =>
            ParseLyricsPayload(raw, translation, transliteration, durationSeconds);

        private static LyricsData? ParseLyricsPayload(string? raw, string? translation, string? transliteration, double? durationSeconds)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var tracks = new List<LyricsData>();
            tracks.AddRange(new LyricsContentParser().Parse(raw, durationSeconds));

            if (!string.IsNullOrWhiteSpace(translation))
                tracks.AddRange(new LyricsContentParser().Parse(translation, durationSeconds));

            if (!string.IsNullOrWhiteSpace(transliteration))
            {
                var transliterationTracks = new LyricsContentParser().Parse(transliteration, durationSeconds);
                foreach (LyricsData data in transliterationTracks)
                    data.LanguageCode = "romaji";
                tracks.AddRange(transliterationTracks);
            }

            return MergeLyricsTracks(tracks);
        }

        private static LyricsData? MergeLyricsTracks(List<LyricsData> tracks)
        {
            LyricsData? main = tracks
                .Where(data => data.LyricsLines.Count > 0)
                .OrderByDescending(data => data.IsWordByWord)
                .ThenByDescending(data => data.LyricsLines.Count)
                .FirstOrDefault();
            if (main == null)
                return null;

            LyricsData? phonetic = tracks.FirstOrDefault(data =>
                data != main &&
                string.Equals(data.LanguageCode, "romaji", StringComparison.OrdinalIgnoreCase));
            LyricsData? translation = tracks.FirstOrDefault(data =>
                data != main &&
                data != phonetic &&
                data.LyricsLines.Count > 0);

            if (translation != null)
                ApplySecondaryText(main, translation);

            phonetic ??= tracks.FirstOrDefault(data =>
                data != main &&
                data != translation &&
                data.LyricsLines.Count > 0);
            if (phonetic != null)
                ApplyTertiaryText(main, phonetic);

            SplitInlineTranslations(main);

            return main;
        }

        private static void ApplySecondaryText(LyricsData main, LyricsData secondary, int toleranceMs = 50)
        {
            foreach (LyricsLine line in main.LyricsLines)
            {
                LyricsLine? match = FindTimedMatch(secondary.LyricsLines, line.StartMs, toleranceMs);
                line.SecondaryText = match?.PrimaryText ?? "";
            }
        }

        private static void ApplyTertiaryText(LyricsData main, LyricsData tertiary, int toleranceMs = 50)
        {
            foreach (LyricsLine line in main.LyricsLines)
            {
                LyricsLine? match = FindTimedMatch(tertiary.LyricsLines, line.StartMs, toleranceMs);
                line.TertiaryText = match?.PrimaryText ?? "";
            }
        }

        private static LyricsLine? FindTimedMatch(IReadOnlyList<LyricsLine> lines, int startMs, int toleranceMs)
        {
            LyricsLine? best = null;
            int bestDelta = int.MaxValue;
            foreach (LyricsLine candidate in lines)
            {
                int delta = Math.Abs(candidate.StartMs - startMs);
                if (delta > toleranceMs || delta >= bestDelta)
                    continue;

                best = candidate;
                bestDelta = delta;
            }

            return best;
        }

        private static void SplitInlineTranslations(LyricsData main)
        {
            foreach (LyricsLine line in main.LyricsLines)
            {
                if (!string.IsNullOrWhiteSpace(line.SecondaryText))
                    continue;

                if (!TrySplitInlineTranslation(line.PrimaryText, out string primary, out string secondary))
                    continue;

                line.PrimaryText = primary;
                line.SecondaryText = secondary;
                if (line.IsPrimaryHasRealSyllableInfo)
                    TrimPrimarySyllables(line, primary);
                else
                    line.PrimarySyllables.Clear();
            }
        }

        private static void TrimPrimarySyllables(LyricsLine line, string primary)
        {
            int primaryLength = primary.Length;
            var trimmed = new List<BaseLyrics>();
            foreach (BaseLyrics syllable in line.PrimarySyllables)
            {
                if (syllable.StartIndex >= primaryLength)
                    continue;

                if (syllable.EndIndex >= primaryLength)
                {
                    int keepLength = primaryLength - syllable.StartIndex;
                    if (keepLength <= 0)
                        continue;

                    syllable.Text = syllable.Text[..Math.Min(keepLength, syllable.Text.Length)].TrimEnd();
                    if (syllable.Text.Length == 0)
                        continue;
                }

                trimmed.Add(syllable);
            }

            line.PrimarySyllables = trimmed;
            line.IsPrimaryHasRealSyllableInfo = trimmed.Count > 0;
        }

        private static bool TrySplitInlineTranslation(string text, out string primary, out string secondary)
        {
            primary = text;
            secondary = "";

            foreach (string separator in new[] { "\\n", "\n", "\t", " / ", " ／ ", " ｜ ", " | ", "//" })
            {
                int index = text.IndexOf(separator, StringComparison.Ordinal);
                if (index <= 0)
                    continue;

                string left = text[..index].Trim();
                string right = text[(index + separator.Length)..].Trim();
                if (left.Length < 2 || right.Length < 2)
                    continue;

                if (!ContainsHan(right))
                    continue;

                primary = left;
                secondary = right;
                return true;
            }

            return false;
        }

        private static bool ContainsHan(string text) =>
            text.Any(ch => ch is >= '\u3400' and <= '\u9FFF');

        private static async Task<string> ReadLyricsTextAsync(string path)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            foreach (Encoding encoding in GetCandidateEncodings(bytes))
            {
                string text = encoding.GetString(bytes).Trim('\0', '\uFEFF');
                if (!LooksCorrupt(text))
                    return text;
            }

            return Encoding.UTF8.GetString(bytes).Trim('\0', '\uFEFF');
        }

        private static IEnumerable<Encoding> GetCandidateEncodings(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                yield return new UTF8Encoding(false, true);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                yield return Encoding.Unicode;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                yield return Encoding.BigEndianUnicode;

            yield return new UTF8Encoding(false, true);

            foreach (int codePage in new[] { 54936, 936, 950 })
            {
                Encoding? encoding = TryGetEncoding(codePage);
                if (encoding != null)
                    yield return encoding;
            }
        }

        private static Encoding? TryGetEncoding(int codePage)
        {
            try
            {
                TryRegisterCodePagesProvider();
                return Encoding.GetEncoding(codePage);
            }
            catch
            {
                return null;
            }
        }

        private static void TryRegisterCodePagesProvider()
        {
            try
            {
                Type? providerType = Type.GetType(
                    "System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages",
                    throwOnError: false);
                object? provider = providerType
                    ?.GetProperty("Instance")
                    ?.GetValue(null);
                if (provider is EncodingProvider encodingProvider)
                    Encoding.RegisterProvider(encodingProvider);
            }
            catch
            {
            }
        }

        private static bool LooksCorrupt(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            int replacementCount = text.Count(ch => ch == '\uFFFD');
            if (replacementCount > 0)
                return true;

            int markerScore = 0;
            if (text.Contains('[') && text.Contains(']'))
                markerScore++;
            if (text.Contains("<tt", StringComparison.OrdinalIgnoreCase))
                markerScore++;
            if (text.Contains('\n'))
                markerScore++;

            return markerScore == 0 && text.Length < 16;
        }

        private static async Task<LocalLyricsCandidate?> TryLoadBestLocalLyricsCandidateAsync(MediaItem item, double? durationSeconds)
        {
            List<string> candidates = FindLyricsCandidates(item);
            LocalLyricsCandidate? best = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                string path = candidates[i];
                try
                {
                    string raw = await ReadLyricsTextAsync(path);
                    LyricsData? lyrics = ParseLyricsPayload(raw, null, null, durationSeconds);
                    if (lyrics == null || lyrics.LyricsLines.Count == 0)
                        continue;

                    int score = ScoreLocalLyricsCandidate(lyrics, path, i);
                    if (best == null || score > best.Score)
                        best = new LocalLyricsCandidate(path, lyrics, score, raw);
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"Load local lyrics failed: {path}");
                }
            }

            if (best == null)
                return null;

            AppLogger.Info($"Local lyrics selected: {best.SourcePath}, {DescribeLyrics(best.Lyrics)}");
            return best;
        }

        private static string DescribeLyrics(LyricsData lyrics)
        {
            int realLineCount = lyrics.LyricsLines.Count(line => line.IsPrimaryHasRealSyllableInfo);
            int realSyllableCount = lyrics.LyricsLines.Sum(line =>
                line.IsPrimaryHasRealSyllableInfo ? line.PrimarySyllables.Count : 0);
            return $"wordByWord={lyrics.IsWordByWord}, lines={lyrics.LyricsLines.Count}, realLines={realLineCount}, realSyllables={realSyllableCount}";
        }

        private static int ScoreLocalLyricsCandidate(LyricsData lyrics, string path, int candidateIndex)
        {
            int score = lyrics.IsWordByWord ? 10_000 : 0;
            score += Math.Min(lyrics.LyricsLines.Count, 500);
            score += Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".ttml" => 600,
                ".qrc" or ".krc" or ".yrc" => 500,
                ".eslrc" => 300,
                ".lrc" => 100,
                _ => 0
            };

            if (lyrics.LyricsLines.Any(line => !string.IsNullOrWhiteSpace(line.SecondaryText)))
                score += 100;

            return score - Math.Min(candidateIndex, 100);
        }

        private static List<string> FindLyricsCandidates(MediaItem item)
        {
            string? directory = Path.GetDirectoryName(item.FilePath);
            if (string.IsNullOrWhiteSpace(directory))
                return [];

            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string path)
            {
                if (seen.Add(path) && File.Exists(path))
                    candidates.Add(path);
            }

            string baseName = Path.GetFileNameWithoutExtension(item.FilePath);
            var candidateNames = new[]
            {
                baseName,
                item.Title,
                $"{item.Artist} - {item.Title}",
                $"{item.Title} - {item.Artist}",
                $"{item.Artist}-{item.Title}",
                $"{item.Title}-{item.Artist}",
            }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            foreach (string candidateName in candidateNames)
            {
                foreach (string extension in Extensions)
                    AddCandidate(Path.Combine(directory, candidateName + extension));
            }

            foreach (string lyricsDirectoryName in new[] { "Lyrics", "lyrics", "Lyric", "lyric", "LRC", "lrc" })
            {
                string lyricsDirectory = Path.Combine(directory, lyricsDirectoryName);
                if (!Directory.Exists(lyricsDirectory))
                    continue;

                foreach (string candidateName in candidateNames)
                {
                    foreach (string extension in Extensions)
                        AddCandidate(Path.Combine(lyricsDirectory, candidateName + extension));
                }
            }

            foreach (string extension in Extensions)
                AddCandidate(Path.Combine(directory, baseName + extension));

            string normalizedTitle = NormalizeName(item.Title);
            string normalizedFileName = NormalizeName(baseName);
            foreach (string file in Directory.EnumerateFiles(directory)
                         .Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
            {
                string name = NormalizeName(Path.GetFileNameWithoutExtension(file));
                if (!string.IsNullOrWhiteSpace(normalizedTitle) && name == normalizedTitle)
                    AddCandidate(file);
                if (name == normalizedFileName)
                    AddCandidate(file);
            }

            return candidates;
        }

        private static string? TryExtractEmbeddedLyrics(string audioFilePath)
        {
            try
            {
                using var stream = new FileStream(
                    audioFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    64 * 1024,
                    FileOptions.SequentialScan);

            string? id3Lyrics = TryExtractId3Lyrics(stream);
            if (!string.IsNullOrWhiteSpace(id3Lyrics))
                return id3Lyrics;

                stream.Position = 0;
                string extension = Path.GetExtension(audioFilePath).ToLowerInvariant();
                return extension switch
                {
                    ".flac" => TryExtractFlacLyrics(stream),
                    ".ogg" or ".opus" => TryExtractVorbisLyrics(stream),
                    ".wav" => TryExtractWaveId3Lyrics(stream),
                    ".m4a" or ".mp4" or ".aac" => TryExtractMp4Lyrics(stream),
                    _ => null
                };
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"读取嵌入歌词失败: {audioFilePath}");
                return null;
            }
        }

        private static string? TryExtractId3Lyrics(Stream stream)
        {
            if (stream.Length < 10)
                return null;

            stream.Position = 0;
            Span<byte> header = stackalloc byte[10];
            stream.ReadExactly(header);
            if (header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3')
                return null;

            int version = header[3];
            if (version is < 2 or > 4)
                return null;

            int tagSize = ReadSyncSafeInt(header[6..10]);
            if (tagSize <= 0 || tagSize > MaxTagBytes || tagSize > stream.Length - 10)
                return null;

            byte[] tag = new byte[tagSize];
            stream.ReadExactly(tag);
            if ((header[5] & 0x80) != 0)
                tag = RemoveUnsynchronization(tag);

            int offset = 0;
            if ((header[5] & 0x40) != 0 && tag.Length >= 4)
            {
                int extendedSize = version == 4
                    ? ReadSyncSafeInt(tag.AsSpan(0, 4))
                    : ReadInt32BigEndian(tag, 0) + 4;
                if (extendedSize > 0 && extendedSize < tag.Length)
                    offset = extendedSize;
            }

            var candidates = new List<string?>();
            double mpegFrameDurationMs = TryGetMpegFrameDurationMs(stream) ?? 26.122448979591837;
            while (offset < tag.Length)
            {
                int headerSize = version == 2 ? 6 : 10;
                if (offset + headerSize > tag.Length)
                    break;

                int idLength = version == 2 ? 3 : 4;
                string frameId = Encoding.ASCII.GetString(tag, offset, idLength);
                if (frameId.All(ch => ch == '\0'))
                    break;

                int frameSize = version switch
                {
                    2 => ReadInt24BigEndian(tag, offset + 3),
                    4 => ReadSyncSafeInt(tag.AsSpan(offset + 4, 4)),
                    _ => ReadInt32BigEndian(tag, offset + 4)
                };

                int dataOffset = offset + headerSize;
                if (frameSize <= 0 || dataOffset + frameSize > tag.Length)
                    break;

                ReadOnlySpan<byte> frame = tag.AsSpan(dataOffset, frameSize);
                if (frameId is "USLT" or "ULT")
                {
                    string? text = ParseUnsynchronizedLyricsFrame(frame, version == 2);
                    if (LooksLikeLyricsText(text))
                        candidates.Add(text);
                }
                else if (frameId is "SYLT" or "SLT")
                {
                    string? text = ParseSynchronizedLyricsFrame(frame, version == 2, mpegFrameDurationMs);
                    if (LooksLikeLyricsText(text))
                        candidates.Add(text);
                }
                else if (frameId is "TXXX" or "TXX")
                {
                    string? text = ParseUserTextFrame(frame);
                    if (LooksLikeLyricsText(text))
                        candidates.Add(text);
                }

                offset = dataOffset + frameSize;
            }

            return SelectBestLyricsCandidate(candidates);
        }

        private static string? ParseUnsynchronizedLyricsFrame(ReadOnlySpan<byte> frame, bool id3v22)
        {
            if (frame.Length < (id3v22 ? 5 : 5))
                return null;

            byte encoding = frame[0];
            int offset = id3v22 ? 4 : 4;
            int descriptionEnd = FindEncodedTerminator(frame, offset, encoding);
            if (descriptionEnd < 0)
                return null;

            offset = descriptionEnd + TerminatorLength(encoding);
            return offset < frame.Length ? DecodeEncodedString(frame[offset..], encoding) : null;
        }

        private static double? TryGetMpegFrameDurationMs(Stream stream)
        {
            long originalPosition = stream.Position;
            try
            {
                stream.Position = 10;
                Span<byte> header = stackalloc byte[4];
                while (stream.Position + 4 <= stream.Length)
                {
                    stream.ReadExactly(header);
                    if (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
                    {
                        int versionBits = (header[1] >> 3) & 0x03;
                        int layerBits = (header[1] >> 1) & 0x03;
                        int sampleRateBits = (header[2] >> 2) & 0x03;
                        if (versionBits == 1 || layerBits == 0 || sampleRateBits == 3)
                            return null;

                        int sampleRate = sampleRateBits switch
                        {
                            0 => 44100,
                            1 => 48000,
                            _ => 32000
                        };
                        if (versionBits == 2)
                            sampleRate /= 2;
                        else if (versionBits == 0)
                            sampleRate /= 4;

                        int samplesPerFrame = layerBits switch
                        {
                            3 => 384,
                            2 => 1152,
                            _ => versionBits == 3 ? 1152 : 576
                        };
                        return samplesPerFrame * 1000.0 / sampleRate;
                    }

                    stream.Position -= 3;
                }
            }
            catch
            {
            }
            finally
            {
                stream.Position = originalPosition;
            }

            return null;
        }

        private static string? ParseSynchronizedLyricsFrame(ReadOnlySpan<byte> frame, bool id3v22, double mpegFrameDurationMs)
        {
            if (frame.Length < (id3v22 ? 7 : 7))
                return null;

            byte encoding = frame[0];
            byte timestampFormat = frame[4];
            int offset = id3v22 ? 6 : 6;
            int descriptionEnd = FindEncodedTerminator(frame, offset, encoding);
            if (descriptionEnd < 0)
                return null;

            offset = descriptionEnd + TerminatorLength(encoding);
            var lines = new List<(int Time, string Text)>();
            while (offset < frame.Length)
            {
                int textEnd = FindEncodedTerminator(frame, offset, encoding);
                if (textEnd < 0)
                    break;

                string text = DecodeEncodedStringPreserveWhitespace(frame[offset..textEnd], encoding);
                offset = textEnd + TerminatorLength(encoding);
                if (offset + 4 > frame.Length)
                    break;

                int rawTime = ReadInt32BigEndian(frame, offset);
                int timeMs = timestampFormat == 1
                    ? (int)Math.Round(rawTime * mpegFrameDurationMs)
                    : rawTime;
                offset += 4;
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add((timeMs, text));
            }

            return ConvertSynchronizedLyricsToEnhancedLrc(lines);
        }

        private static string? ConvertSynchronizedLyricsToEnhancedLrc(List<(int Time, string Text)> timedTexts)
        {
            if (timedTexts.Count == 0)
                return null;

            var result = new List<string>();
            var currentLine = new List<(int Time, string Text)>();

            foreach ((int time, string text) in timedTexts.OrderBy(line => line.Time))
            {
                string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
                string[] parts = normalized.Split('\n');

                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        if (ShouldStartNewSyltLine(currentLine, time))
                            FlushSyltLine(currentLine, result);

                        currentLine.Add((time, part));
                    }

                    if (i < parts.Length - 1)
                        FlushSyltLine(currentLine, result);
                }
            }

            FlushSyltLine(currentLine, result);
            return result.Count == 0 ? null : string.Join(Environment.NewLine, result);
        }

        private static bool ShouldStartNewSyltLine(List<(int Time, string Text)> currentLine, int nextTimeMs)
        {
            if (currentLine.Count == 0)
                return false;

            int lastTimeMs = currentLine[^1].Time;
            int gapMs = nextTimeMs - lastTimeMs;
            int currentLength = currentLine.Sum(part => part.Text.Length);
            string lastText = currentLine[^1].Text.TrimEnd();
            char lastChar = lastText.Length > 0 ? lastText[^1] : '\0';

            if (gapMs >= 1800)
                return true;

            if (currentLength >= 42)
                return true;

            if (gapMs >= 700 && ".!?;".Contains(lastChar))
                return true;

            return false;
        }

        private static void FlushSyltLine(List<(int Time, string Text)> currentLine, List<string> result)
        {
            if (currentLine.Count == 0)
                return;

            int lineStartMs = currentLine[0].Time;
            int lineEndMs = EstimateSyltLineEndMs(currentLine);
            var builder = new StringBuilder();
            builder.Append('[').Append(FormatTimestamp(lineStartMs)).Append(']');

            foreach ((int time, string text) in currentLine)
            {
                builder.Append('<').Append(FormatTimestamp(time)).Append('>');
                builder.Append(text);
            }
            builder.Append('<').Append(FormatTimestamp(lineEndMs)).Append('>');

            result.Add(builder.ToString());
            currentLine.Clear();
        }

        private static int EstimateSyltLineEndMs(List<(int Time, string Text)> currentLine)
        {
            if (currentLine.Count == 0)
                return 0;

            int lastTime = currentLine[^1].Time;
            if (currentLine.Count >= 2)
            {
                int previousTime = currentLine[^2].Time;
                int duration = Math.Clamp(lastTime - previousTime, 120, 1200);
                return lastTime + duration;
            }

            return lastTime + Math.Clamp(currentLine[^1].Text.Length * 180, 200, 1200);
        }

        private static string? ParseUserTextFrame(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 2)
                return null;

            byte encoding = frame[0];
            int descriptionEnd = FindEncodedTerminator(frame, 1, encoding);
            if (descriptionEnd < 0)
                return null;

            string description = DecodeEncodedString(frame[1..descriptionEnd], encoding) ?? "";
            int valueOffset = descriptionEnd + TerminatorLength(encoding);
            if (valueOffset >= frame.Length || !LooksLikeLyricsKey(description))
                return null;

            return DecodeEncodedString(frame[valueOffset..], encoding);
        }

        private static string? TryExtractFlacLyrics(Stream stream)
        {
            Span<byte> signature = stackalloc byte[4];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual("fLaC"u8))
                return null;

            bool isLast = false;
            byte[] lengthBytes = new byte[3];
            while (!isLast && stream.Position + 4 <= stream.Length)
            {
                int blockHeader = stream.ReadByte();
                if (blockHeader < 0)
                    break;

                isLast = (blockHeader & 0x80) != 0;
                int blockType = blockHeader & 0x7F;
                stream.ReadExactly(lengthBytes);
                int blockLength = (lengthBytes[0] << 16) |
                                  (lengthBytes[1] << 8) |
                                  lengthBytes[2];

                if (blockLength < 0 || blockLength > MaxTagBytes || stream.Position + blockLength > stream.Length)
                    return null;

                if (blockType == 4)
                {
                    byte[] block = new byte[blockLength];
                    stream.ReadExactly(block);
                    return TryExtractVorbisCommentLyrics(block);
                }

                stream.Position += blockLength;
            }

            return null;
        }

        private static string? TryExtractVorbisLyrics(Stream stream)
        {
            stream.Position = 0;
            int length = (int)Math.Min(stream.Length, MaxCommentScanBytes);
            byte[] data = new byte[length];
            stream.ReadExactly(data);
            return SelectBestLyricsCandidate([
                TryExtractTextMarker(data, "SYNCEDLYRICS="),
                TryExtractTextMarker(data, "UNSYNCEDLYRICS="),
                TryExtractTextMarker(data, "LYRICS=")
            ]);
        }

        private static string? TryExtractVorbisCommentLyrics(ReadOnlySpan<byte> block)
        {
            int offset = 0;
            if (offset + 4 > block.Length)
                return null;

            int vendorLength = ReadInt32LittleEndian(block, offset);
            offset += 4 + vendorLength;
            if (vendorLength < 0 || offset + 4 > block.Length)
                return null;

            int count = ReadInt32LittleEndian(block, offset);
            offset += 4;
            var candidates = new List<string?>();
            for (int i = 0; i < count && offset + 4 <= block.Length; i++)
            {
                int length = ReadInt32LittleEndian(block, offset);
                offset += 4;
                if (length < 0 || offset + length > block.Length)
                    break;

                string comment = Encoding.UTF8.GetString(block.Slice(offset, length));
                offset += length;
                int separator = comment.IndexOf('=');
                if (separator > 0 && LooksLikeLyricsKey(comment[..separator]))
                    candidates.Add(comment[(separator + 1)..]);
            }

            return SelectBestLyricsCandidate(candidates);
        }

        private static string? TryExtractMp4Lyrics(Stream stream)
        {
            stream.Position = 0;
            int length = (int)Math.Min(stream.Length, MaxCommentScanBytes);
            byte[] data = new byte[length];
            stream.ReadExactly(data);
            return SelectBestLyricsCandidate([
                TryExtractTextMarker(data, "©lyr"),
                TryExtractTextMarker(data, "lyrics"),
                TryExtractTextMarker(data, "LYRICS=")
            ]);
        }

        private static string? TryExtractWaveId3Lyrics(Stream stream)
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
                return null;

            byte[] chunkHeader = new byte[8];
            while (stream.Position + 8 <= stream.Length)
            {
                stream.ReadExactly(chunkHeader);
                string chunkId = Encoding.ASCII.GetString(chunkHeader, 0, 4);
                int chunkSize = ReadInt32LittleEndian(chunkHeader, 4);
                if (chunkSize < 0 || stream.Position + chunkSize > stream.Length)
                    break;

                if (chunkId.Equals("id3 ", StringComparison.OrdinalIgnoreCase) && chunkSize <= MaxTagBytes)
                {
                    byte[] id3 = new byte[chunkSize];
                    stream.ReadExactly(id3);
                    using var memory = new MemoryStream(id3, writable: false);
                    return TryExtractId3Lyrics(memory);
                }

                stream.Position += chunkSize + (chunkSize & 1);
            }

            return null;
        }

        private static string? TryExtractTextMarker(byte[] data, string marker)
        {
            int index = IndexOf(data, Encoding.UTF8.GetBytes(marker));
            if (index < 0)
                index = IndexOf(data, Encoding.ASCII.GetBytes(marker));
            if (index < 0)
                return null;

            int start = index + Encoding.UTF8.GetByteCount(marker);
            int end = start;
            while (end < data.Length && data[end] != 0)
                end++;

            string text = Encoding.UTF8.GetString(data, start, end - start).Trim();
            return LooksLikeLyricsText(text) ? text : null;
        }

        private static string NormalizeName(string value) =>
            string.Join(
                "",
                (value ?? string.Empty)
                    .Where(ch => !char.IsWhiteSpace(ch) && ch is not '-' and not '_' and not '.'))
            .ToLowerInvariant();

        private static bool LooksLikeLyricsKey(string key) =>
            key.Contains("LYRIC", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("LRC", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeTimedLyrics(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            (Regex.IsMatch(text, @"\[\d{1,3}:\d{2}[\.:]\d{1,3}\]", RegexOptions.CultureInvariant) ||
             Regex.IsMatch(text, @"\[\d+,\d+\]", RegexOptions.CultureInvariant) ||
             text.Contains("<tt", StringComparison.OrdinalIgnoreCase));

        private static bool LooksLikeLyricsText(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            text.Length > 8 &&
            (LooksLikeTimedLyrics(text) || text.Contains('\n'));

        private static string? SelectBestLyricsCandidate(IEnumerable<string?> candidates) =>
            candidates
                .Where(LooksLikeLyricsText)
                .OrderByDescending(ScoreLyricsText)
                .FirstOrDefault();

        private static int ScoreLyricsText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            int score = 0;
            if (text.Contains("<tt", StringComparison.OrdinalIgnoreCase))
                score += 500;
            if (Regex.IsMatch(text, @"\[\d+,\d+\].*(?:\(|<)\d+,\d+", RegexOptions.CultureInvariant))
                score += 450;
            if (Regex.IsMatch(text, @"\[\d{1,3}:\d{2}[\.:]\d{1,3}\]\s*<\d{1,3}:\d{2}[\.:]\d{1,3}>", RegexOptions.CultureInvariant))
                score += 450;
            if (Regex.IsMatch(text, @"(?:\[|<)\d{1,3}:\d{2}[\.:]\d{1,3}(?:\]|>)[^\r\n\[\]<>]+(?:\[|<)\d{1,3}:\d{2}[\.:]\d{1,3}(?:\]|>)", RegexOptions.CultureInvariant))
                score += 400;

            int timedLineCount = Regex.Matches(text, @"\[\d{1,3}:\d{2}[\.:]\d{1,3}\]", RegexOptions.CultureInvariant).Count;
            score += Math.Min(timedLineCount, 100);
            score += Math.Min(text.Length / 200, 50);
            return score;
        }

        private static string FormatTimestamp(int milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
        }

        private static string? DecodeEncodedString(ReadOnlySpan<byte> data, byte encoding)
        {
            if (data.IsEmpty)
                return string.Empty;

            Encoding textEncoding = encoding switch
            {
                1 => Encoding.Unicode,
                2 => Encoding.BigEndianUnicode,
                3 => Encoding.UTF8,
                _ => Encoding.Latin1
            };

            return textEncoding.GetString(data).Trim('\0', '\uFEFF').Trim();
        }

        private static string DecodeEncodedStringPreserveWhitespace(ReadOnlySpan<byte> data, byte encoding)
        {
            if (data.IsEmpty)
                return string.Empty;

            Encoding textEncoding = encoding switch
            {
                1 => Encoding.Unicode,
                2 => Encoding.BigEndianUnicode,
                3 => Encoding.UTF8,
                _ => Encoding.Latin1
            };

            return textEncoding.GetString(data).Trim('\0', '\uFEFF');
        }

        private static int TerminatorLength(byte encoding) => encoding is 1 or 2 ? 2 : 1;

        private static int FindEncodedTerminator(ReadOnlySpan<byte> data, int start, byte encoding)
        {
            if (encoding is not (1 or 2))
            {
                int index = data[start..].IndexOf((byte)0);
                return index < 0 ? -1 : start + index;
            }

            for (int i = start; i + 1 < data.Length; i += 2)
            {
                if (data[i] == 0 && data[i + 1] == 0)
                    return i;
            }

            return -1;
        }

        private static byte[] RemoveUnsynchronization(byte[] data)
        {
            var result = new List<byte>(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                result.Add(data[i]);
                if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0)
                    i++;
            }

            return result.ToArray();
        }

        private static int IndexOf(byte[] data, byte[] marker) =>
            data.AsSpan().IndexOf(marker);

        private static int ReadSyncSafeInt(ReadOnlySpan<byte> data) =>
            (data[0] << 21) | (data[1] << 14) | (data[2] << 7) | data[3];

        private static int ReadInt24BigEndian(byte[] data, int offset) =>
            (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];

        private static int ReadInt32BigEndian(byte[] data, int offset) =>
            ReadInt32BigEndian(data.AsSpan(), offset);

        private static int ReadInt32BigEndian(ReadOnlySpan<byte> data, int offset) =>
            (data[offset] << 24) |
            (data[offset + 1] << 16) |
            (data[offset + 2] << 8) |
            data[offset + 3];

        private static int ReadInt32LittleEndian(ReadOnlySpan<byte> data, int offset) =>
            data[offset] |
            (data[offset + 1] << 8) |
            (data[offset + 2] << 16) |
            (data[offset + 3] << 24);

        private sealed class LocalLyricsCandidate(string sourcePath, LyricsData lyrics, int score, string rawText)
        {
            public string SourcePath { get; } = sourcePath;
            public LyricsData Lyrics { get; } = lyrics;
            public int Score { get; } = score;
            public string RawText { get; } = rawText;
        }
    }
}
