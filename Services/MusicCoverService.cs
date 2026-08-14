using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SkiaSharp;

namespace SightoHear.Services
{
    public static class MusicCoverService
    {
        private const int MaxDimension = 256;
        private const int BackgroundMaxDimension = 1200;
        private const long JpegQuality = 82L;
        private const int MaxTagBytes = 32 * 1024 * 1024;
        private const int MaxCommentScanBytes = 8 * 1024 * 1024;

        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear",
            "Cache",
            "MusicCovers");

        private static readonly ConcurrentDictionary<string, Lazy<string>> InFlight =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<string>> BackgroundInFlight =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<string>> OriginalInFlight =
            new(StringComparer.OrdinalIgnoreCase);

        // 缓存已解析过的封面路径：以 "audioFilePath|Length|LastWriteTicks|kind" 为键，
        // 避免每次都读整段音频、解析 ID3 标签并计算封面 hash。空字符串表示无封面。
        private static readonly ConcurrentDictionary<string, string> ResolvedCoverPaths =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 清空封面路径解析缓存。进入全屏播放器时调用，释放累积的字符串引用，
        /// 降低 GC 压力，避免因缓存膨胀导致的掉帧。
        /// </summary>
        public static void ClearCache()
        {
            ResolvedCoverPaths.Clear();
        }

        /// <summary>
        /// 封面缓存统计（供资源诊断服务输出快照）：
        /// 已解析封面路径数 + 三种尺寸的并发提取中任务数。
        /// </summary>
        public static (int ResolvedPaths, int InFlight, int BackgroundInFlight, int OriginalInFlight) GetCacheStats()
        {
            return (
                ResolvedCoverPaths.Count,
                InFlight.Count,
                BackgroundInFlight.Count,
                OriginalInFlight.Count);
        }

        /// <summary>
        /// 快速检查：音频文件的封面是否已解析过，返回缓存的封面路径。
        /// 仅检查内存缓存，不触发任何 I/O。
        /// </summary>
        /// <param name="audioFilePath">音频文件路径。</param>
        /// <returns>
        ///   已缓存时有封面则返回封面路径，无封面则返回空字符串；
        ///   尚未解析过则返回 null。
        /// </returns>
        public static string? TryGetCachedPath(string audioFilePath)
        {
            string? key = BuildAudioCacheKey(audioFilePath, "cover");
            if (key == null) return null;
            if (TryGetResolvedPath(key, out string coverPath))
                return coverPath;
            return null;
        }

        /// <summary>
        /// 快速检查：音频文件的原版封面（未缩放）是否已解析过。
        /// </summary>
        public static string? TryGetCachedOriginalPath(string audioFilePath)
        {
            string? key = BuildAudioCacheKey(audioFilePath, "original");
            if (key == null) return null;
            if (TryGetResolvedPath(key, out string coverPath))
                return coverPath;
            return null;
        }

        private static string? BuildAudioCacheKey(string audioFilePath, string kind)
        {
            try
            {
                var info = new FileInfo(audioFilePath);
                if (!info.Exists)
                    return null;
                return $"{audioFilePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{kind}";
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetResolvedPath(string? key, out string coverPath)
        {
            coverPath = string.Empty;
            if (key is null)
                return false;

            if (!ResolvedCoverPaths.TryGetValue(key, out string? cached))
                return false;

            if (cached.Length == 0)
            {
                coverPath = string.Empty;
                return true;
            }

            if (File.Exists(cached))
            {
                coverPath = cached;
                return true;
            }

            // 缓存文件已被删除，剔除记录以便下次重新生成
            ResolvedCoverPaths.TryRemove(key, out _);
            return false;
        }

        private static string CacheResolvedPath(string? key, string coverPath)
        {
            if (key is not null)
                ResolvedCoverPaths[key] = coverPath ?? string.Empty;
            return coverPath ?? string.Empty;
        }

        public static string GetOrCreate(string audioFilePath)
        {
            string? cacheKey = BuildAudioCacheKey(audioFilePath, "cover");
            if (TryGetResolvedPath(cacheKey, out string cached))
                return cached;

            byte[]? imageData = TryExtractEmbeddedCover(audioFilePath);
            imageData ??= TryReadFolderCover(audioFilePath);
            if (imageData is not { Length: > 0 })
                return CacheResolvedPath(cacheKey, string.Empty);

            string hash = Convert.ToHexString(SHA256.HashData(imageData)).ToLowerInvariant();
            string outputPath = Path.Combine(CacheDirectory, $"{hash}.jpg");
            if (File.Exists(outputPath))
                return CacheResolvedPath(cacheKey, outputPath);

            var pending = InFlight.GetOrAdd(
                hash,
                _ => new Lazy<string>(() => SaveCover(imageData, outputPath), true));

            try
            {
                string coverPath = pending.Value;
                if (string.IsNullOrWhiteSpace(coverPath))
                    return CacheResolvedPath(cacheKey, GetOrCreateOriginal(audioFilePath));

                return CacheResolvedPath(cacheKey, coverPath);
            }
            finally
            {
                InFlight.TryRemove(hash, out _);
            }
        }

        public static string GetOrCreateBackground(string audioFilePath)
        {
            string? cacheKey = BuildAudioCacheKey(audioFilePath, "background");
            if (TryGetResolvedPath(cacheKey, out string cached))
                return cached;

            byte[]? imageData = TryExtractEmbeddedCover(audioFilePath);
            imageData ??= TryReadFolderCover(audioFilePath);
            if (imageData is not { Length: > 0 })
                return CacheResolvedPath(cacheKey, string.Empty);

            string hash = Convert.ToHexString(SHA256.HashData(imageData)).ToLowerInvariant();
            string outputPath = Path.Combine(CacheDirectory, $"{hash}.bg.jpg");
            if (File.Exists(outputPath))
                return CacheResolvedPath(cacheKey, outputPath);

            var pending = BackgroundInFlight.GetOrAdd(
                hash,
                _ => new Lazy<string>(() => SaveCover(imageData, outputPath, BackgroundMaxDimension, 92L), true));

            try
            {
                string coverPath = pending.Value;
                if (string.IsNullOrWhiteSpace(coverPath))
                    return CacheResolvedPath(cacheKey, GetOrCreateOriginal(audioFilePath));

                return CacheResolvedPath(cacheKey, coverPath);
            }
            finally
            {
                BackgroundInFlight.TryRemove(hash, out _);
            }
        }

        public static string GetOrCreateOriginal(string audioFilePath)
        {
            string? cacheKey = BuildAudioCacheKey(audioFilePath, "original");
            if (TryGetResolvedPath(cacheKey, out string cached))
                return cached;

            byte[]? imageData = TryExtractEmbeddedCover(audioFilePath);
            imageData ??= TryReadFolderCover(audioFilePath);
            if (imageData is not { Length: > 0 })
                return CacheResolvedPath(cacheKey, string.Empty);

            string hash = Convert.ToHexString(SHA256.HashData(imageData)).ToLowerInvariant();
            string outputPath = Path.Combine(CacheDirectory, $"{hash}.original{GetImageExtension(imageData)}");
            if (File.Exists(outputPath))
                return CacheResolvedPath(cacheKey, outputPath);

            var pending = OriginalInFlight.GetOrAdd(
                hash,
                _ => new Lazy<string>(() => SaveOriginalCover(imageData, outputPath), true));

            try
            {
                return CacheResolvedPath(cacheKey, pending.Value);
            }
            finally
            {
                OriginalInFlight.TryRemove(hash, out _);
            }
        }

        public static IReadOnlyList<Windows.UI.Color> GetBackgroundAccentColors(string imagePath, int count)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return CreateFallbackAccentColors(count);

            try
            {
                using SKBitmap? source = SKBitmap.Decode(imagePath);
                if (source is null)
                    return CreateFallbackAccentColors(count);

                var bitmap = new SKBitmap(72, 72, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(bitmap))
#pragma warning disable CS0618
                using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
#pragma warning restore CS0618
                {
                    canvas.Clear(SKColors.Black);
                    canvas.DrawBitmap(source, new SKRect(0, 0, 72, 72), paint);
                    canvas.Flush();
                }

                var buckets = new Dictionary<int, ColorBucket>();
                for (int y = 0; y < bitmap.Height; y += 2)
                {
                    for (int x = 0; x < bitmap.Width; x += 2)
                    {
                        SKColor color = bitmap.GetPixel(x, y);

                        int key = (color.Red / 32) << 16 | (color.Green / 32) << 8 | color.Blue / 32;
                        if (!buckets.TryGetValue(key, out ColorBucket? bucket))
                        {
                            bucket = new ColorBucket();
                            buckets[key] = bucket;
                        }

                        double luminance = GetLuminance(color);
                        double saturation = GetSaturation(color);
                        double mid = 1.0 - Math.Abs(luminance - 0.46) * 1.2;
                        bucket.Add(color, 0.5 + saturation * 1.4 + Math.Max(0, mid));
                    }
                }

                var selected = new List<Windows.UI.Color>();
                foreach (ColorBucket bucket in buckets.Values.OrderByDescending(bucket => bucket.Score))
                {
                    Windows.UI.Color color = TuneAccentColor(bucket.ToColor());
                    if (selected.All(existing => ColorDistance(existing, color) > 24))
                        selected.Add(color);

                    if (selected.Count >= count)
                        break;
                }

                // 如果只提取到 1 种颜色（如全白/全黑封面），
                // 流光背景至少需要 2 个颜色才能正常渲染，
                // 因此根据主色亮度自动补充一个对比暗色
                if (selected.Count == 1)
                {
                    Windows.UI.Color primary = selected[0];
                    double luminance = GetLuminance(ToSKColor(primary));
                    Windows.UI.Color contrast = luminance > 0.5
                        ? Windows.UI.Color.FromArgb(255, 30, 30, 30)   // 浅色封面 → 补深色
                        : Windows.UI.Color.FromArgb(255, 220, 220, 220); // 深色封面 → 补浅色
                    selected.Add(contrast);
                }

                // 如果一个颜色都没提取到，使用后备颜色
                if (selected.Count == 0)
                {
                    bitmap.Dispose();
                    return CreateFallbackAccentColors(count);
                }

                // 用最后一个颜色补齐到 count 个，确保调用方不会越界
                while (selected.Count < count)
                    selected.Add(selected[^1]);

                bitmap.Dispose();
                return selected.Take(count).ToList();
            }
            catch
            {
                return CreateFallbackAccentColors(count);
            }
        }

        private static byte[]? TryExtractEmbeddedCover(string audioFilePath)
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

                byte[]? id3Cover = TryExtractId3Cover(stream);
                if (id3Cover is not null)
                    return id3Cover;

                stream.Position = 0;
                string extension = Path.GetExtension(audioFilePath).ToLowerInvariant();
                return extension switch
                {
                    ".flac" => TryExtractFlacCover(stream),
                    ".m4a" or ".mp4" or ".aac" => TryExtractMp4Cover(stream),
                    ".ogg" or ".opus" => TryExtractVorbisCover(stream),
                    ".wma" => TryExtractAsfCover(stream),
                    ".wav" => TryExtractWaveId3Cover(stream),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static byte[]? TryExtractId3Cover(Stream stream)
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
            int offset = 0;

            if ((header[5] & 0x40) != 0 && tag.Length >= 4)
            {
                int extendedSize = version == 4
                    ? ReadSyncSafeInt(tag.AsSpan(0, 4))
                    : ReadInt32BigEndian(tag, 0) + 4;
                if (extendedSize > 0 && extendedSize < tag.Length)
                    offset = extendedSize;
            }

            bool globalUnsynchronization = (header[5] & 0x80) != 0;
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

                if (frameId is "APIC" or "PIC")
                {
                    byte[]? image = ParseAttachedPicture(
                        tag.AsSpan(dataOffset, frameSize),
                        version == 2);
                    if (image is not null)
                        return globalUnsynchronization
                            ? RemoveUnsynchronization(image)
                            : image;
                }

                offset = dataOffset + frameSize;
            }

            return null;
        }

        private static byte[]? ParseAttachedPicture(ReadOnlySpan<byte> frame, bool id3v22)
        {
            if (frame.Length < 5)
                return null;

            byte encoding = frame[0];
            int offset = 1;

            if (id3v22)
            {
                offset += 3;
            }
            else
            {
                int mimeEnd = frame[offset..].IndexOf((byte)0);
                if (mimeEnd < 0)
                    return null;
                offset += mimeEnd + 1;
            }

            if (offset >= frame.Length)
                return null;

            offset++;
            int descriptionEnd = FindEncodedTerminator(frame, offset, encoding);
            if (descriptionEnd < 0)
                return null;

            offset = descriptionEnd + (encoding is 1 or 2 ? 2 : 1);
            return offset < frame.Length ? frame[offset..].ToArray() : null;
        }

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

        private static byte[]? TryExtractFlacCover(Stream stream)
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

                if (blockLength < 0 || blockLength > MaxTagBytes ||
                    stream.Position + blockLength > stream.Length)
                    return null;

                if (blockType == 6)
                {
                    byte[] block = new byte[blockLength];
                    stream.ReadExactly(block);
                    return ParseFlacPictureBlock(block);
                }

                stream.Position += blockLength;
            }

            return null;
        }

        private static byte[]? ParseFlacPictureBlock(ReadOnlySpan<byte> block)
        {
            int offset = 0;
            if (!TrySkipBigEndianField(block, ref offset, 4) ||
                !TrySkipLengthPrefixedField(block, ref offset) ||
                !TrySkipLengthPrefixedField(block, ref offset) ||
                !TrySkipBigEndianField(block, ref offset, 16) ||
                offset + 4 > block.Length)
                return null;

            int imageLength = ReadInt32BigEndian(block, offset);
            offset += 4;
            return imageLength > 0 && offset + imageLength <= block.Length
                ? block.Slice(offset, imageLength).ToArray()
                : null;
        }

        private static bool TrySkipLengthPrefixedField(ReadOnlySpan<byte> data, ref int offset)
        {
            if (offset + 4 > data.Length)
                return false;

            int length = ReadInt32BigEndian(data, offset);
            offset += 4;
            if (length < 0 || offset + length > data.Length)
                return false;

            offset += length;
            return true;
        }

        private static bool TrySkipBigEndianField(
            ReadOnlySpan<byte> data,
            ref int offset,
            int length)
        {
            if (offset + length > data.Length)
                return false;
            offset += length;
            return true;
        }

        private static byte[]? TryExtractMp4Cover(Stream stream)
        {
            stream.Position = 0;
            return FindMp4CoverInRange(stream, 0, stream.Length, 0);
        }

        private static byte[]? FindMp4CoverInRange(
            Stream stream,
            long start,
            long end,
            int depth)
        {
            if (depth > 8)
                return null;

            long position = start;
            byte[] typeBytes = new byte[4];
            while (position + 8 <= end)
            {
                stream.Position = position;
                uint size32 = ReadUInt32BigEndian(stream);
                stream.ReadExactly(typeBytes);
                string type = Encoding.Latin1.GetString(typeBytes);

                long headerSize = 8;
                long atomSize = size32;
                if (size32 == 1)
                {
                    atomSize = checked((long)ReadUInt64BigEndian(stream));
                    headerSize = 16;
                }
                else if (size32 == 0)
                {
                    atomSize = end - position;
                }

                if (atomSize < headerSize || position + atomSize > end)
                    break;

                long contentStart = position + headerSize;
                long atomEnd = position + atomSize;

                if (type == "covr")
                {
                    byte[]? cover = FindMp4DataAtom(stream, contentStart, atomEnd);
                    if (cover is not null)
                        return cover;
                }
                else if (type is "moov" or "udta" or "meta" or "ilst")
                {
                    if (type == "meta")
                        contentStart += 4;

                    byte[]? nested = FindMp4CoverInRange(
                        stream,
                        contentStart,
                        atomEnd,
                        depth + 1);
                    if (nested is not null)
                        return nested;
                }

                position = atomEnd;
            }

            return null;
        }

        private static byte[]? FindMp4DataAtom(Stream stream, long start, long end)
        {
            long position = start;
            byte[] typeBytes = new byte[4];
            while (position + 8 <= end)
            {
                stream.Position = position;
                uint size = ReadUInt32BigEndian(stream);
                stream.ReadExactly(typeBytes);
                string type = Encoding.Latin1.GetString(typeBytes);
                if (size < 8 || position + size > end)
                    break;

                if (type == "data" && size > 16 && size - 16 <= MaxTagBytes)
                {
                    stream.Position = position + 16;
                    byte[] image = new byte[size - 16];
                    stream.ReadExactly(image);
                    return image;
                }

                position += size;
            }

            return null;
        }

        private static byte[]? TryExtractVorbisCover(Stream stream)
        {
            stream.Position = 0;
            int length = (int)Math.Min(stream.Length, MaxCommentScanBytes);
            byte[] data = new byte[length];
            stream.ReadExactly(data);

            byte[]? picture = TryExtractBase64Field(
                data,
                "METADATA_BLOCK_PICTURE=",
                parseFlacPicture: true);
            return picture ?? TryExtractBase64Field(
                data,
                "COVERART=",
                parseFlacPicture: false);
        }

        private static byte[]? TryExtractBase64Field(
            byte[] data,
            string marker,
            bool parseFlacPicture)
        {
            ReadOnlySpan<byte> markerBytes = Encoding.ASCII.GetBytes(marker);
            int markerIndex = data.AsSpan().IndexOf(markerBytes);
            if (markerIndex < 0)
                return null;

            int start = markerIndex + markerBytes.Length;
            int end = start;
            while (end < data.Length && IsBase64Byte(data[end]))
            {
                byte current = data[end++];
                if (current == '=')
                {
                    while (end < data.Length && data[end] == '=')
                        end++;
                    break;
                }
            }

            if (end <= start)
                return null;

            try
            {
                byte[] decoded = Convert.FromBase64String(
                    Encoding.ASCII.GetString(data, start, end - start));
                return parseFlacPicture
                    ? ParseFlacPictureBlock(decoded)
                    : decoded;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsBase64Byte(byte value) =>
            value is >= (byte)'A' and <= (byte)'Z' ||
            value is >= (byte)'a' and <= (byte)'z' ||
            value is >= (byte)'0' and <= (byte)'9' ||
            value is (byte)'+' or (byte)'/' or (byte)'=';

        private static byte[]? TryExtractAsfCover(Stream stream)
        {
            stream.Position = 0;
            int length = (int)Math.Min(stream.Length, MaxCommentScanBytes);
            byte[] data = new byte[length];
            stream.ReadExactly(data);

            byte[] marker = Encoding.Unicode.GetBytes("WM/Picture");
            int markerIndex = data.AsSpan().IndexOf(marker);
            if (markerIndex < 0)
                return null;

            int descriptorOffset = markerIndex + marker.Length;
            foreach ((int lengthSize, int valueOffset) in new[] { (2, 4), (4, 6) })
            {
                if (descriptorOffset + valueOffset > data.Length)
                    continue;

                int valueType = ReadUInt16LittleEndian(data, descriptorOffset);
                int valueLength = lengthSize == 2
                    ? ReadUInt16LittleEndian(data, descriptorOffset + 2)
                    : ReadInt32LittleEndian(data, descriptorOffset + 2);
                int start = descriptorOffset + valueOffset;

                if (valueType == 1 && valueLength > 0 &&
                    valueLength <= MaxTagBytes &&
                    start + valueLength <= data.Length)
                {
                    byte[]? picture = ParseWmPicture(
                        data.AsSpan(start, valueLength));
                    if (picture is not null)
                        return picture;
                }
            }

            return null;
        }

        private static byte[]? ParseWmPicture(ReadOnlySpan<byte> value)
        {
            if (value.Length < 7)
                return null;

            int imageLength = ReadInt32LittleEndian(value, 1);
            int offset = 5;
            offset = SkipUtf16String(value, offset);
            if (offset < 0)
                return null;
            offset = SkipUtf16String(value, offset);
            if (offset < 0)
                return null;

            return imageLength > 0 && offset + imageLength <= value.Length
                ? value.Slice(offset, imageLength).ToArray()
                : null;
        }

        private static int SkipUtf16String(ReadOnlySpan<byte> data, int offset)
        {
            for (int i = offset; i + 1 < data.Length; i += 2)
            {
                if (data[i] == 0 && data[i + 1] == 0)
                    return i + 2;
            }

            return -1;
        }

        private static byte[]? TryExtractWaveId3Cover(Stream stream)
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8) ||
                !header[8..12].SequenceEqual("WAVE"u8))
                return null;

            byte[] chunkHeader = new byte[8];
            while (stream.Position + 8 <= stream.Length)
            {
                stream.ReadExactly(chunkHeader);
                string chunkId = Encoding.ASCII.GetString(chunkHeader, 0, 4);
                int chunkSize = ReadInt32LittleEndian(chunkHeader, 4);
                if (chunkSize < 0 || stream.Position + chunkSize > stream.Length)
                    break;

                if (chunkId.Equals("id3 ", StringComparison.OrdinalIgnoreCase) &&
                    chunkSize <= MaxTagBytes)
                {
                    byte[] id3 = new byte[chunkSize];
                    stream.ReadExactly(id3);
                    using var memory = new MemoryStream(id3, writable: false);
                    return TryExtractId3Cover(memory);
                }

                stream.Position += chunkSize + (chunkSize & 1);
            }

            return null;
        }

        private static byte[]? TryReadFolderCover(string audioFilePath)
        {
            try
            {
                string? directory = Path.GetDirectoryName(audioFilePath);
                if (string.IsNullOrWhiteSpace(directory))
                    return null;

                string[] names = { "cover", "folder", "front", "album" };
                string[] extensions = { ".jpg", ".jpeg", ".png", ".bmp" };

                foreach (string name in names)
                {
                    foreach (string extension in extensions)
                    {
                        string path = Path.Combine(directory, name + extension);
                        if (File.Exists(path))
                            return File.ReadAllBytes(path);
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string SaveCover(byte[] imageData, string outputPath) =>
            SaveCover(imageData, outputPath, MaxDimension, JpegQuality);

        private static string SaveOriginalCover(byte[] imageData, string outputPath)
        {
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                // 直接写入目标路径——并发去重由 GetOrCreate 的 Lazy<string> 保证
                File.WriteAllBytes(outputPath, imageData);
                return outputPath;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetImageExtension(byte[] imageData)
        {
            if (imageData.Length >= 8 &&
                imageData[0] == 0x89 &&
                imageData[1] == 0x50 &&
                imageData[2] == 0x4E &&
                imageData[3] == 0x47)
            {
                return ".png";
            }

            if (imageData.Length >= 3 &&
                imageData[0] == 0xFF &&
                imageData[1] == 0xD8 &&
                imageData[2] == 0xFF)
            {
                return ".jpg";
            }

            if (imageData.Length >= 2 &&
                imageData[0] == 0x42 &&
                imageData[1] == 0x4D)
            {
                return ".bmp";
            }

            if (imageData.Length >= 12 &&
                imageData[0] == 0x52 &&
                imageData[1] == 0x49 &&
                imageData[2] == 0x46 &&
                imageData[3] == 0x46 &&
                imageData[8] == 0x57 &&
                imageData[9] == 0x45 &&
                imageData[10] == 0x42 &&
                imageData[11] == 0x50)
            {
                return ".webp";
            }

            return ".jpg";
        }

        private static string SaveCover(byte[] imageData, string outputPath, int maxDimension, long jpegQuality)
        {
            try
            {
                Directory.CreateDirectory(CacheDirectory);

                using SKBitmap? source = SKBitmap.Decode(imageData);
                if (source is null)
                    return string.Empty;

                double scale = Math.Min(
                    1.0,
                    (double)maxDimension / Math.Max(source.Width, source.Height));
                int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                int height = Math.Max(1, (int)Math.Round(source.Height * scale));

                // 使用 SKSurface 创建独立像素的图像，确保编码稳定性
                using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height));
                {
                    var canvas = surface.Canvas;
                    canvas.Clear(new SKColor(32, 32, 32));
                    using var paint = new SKPaint
                    {
                        IsAntialias = true
                    };
#pragma warning disable CS0618
                    paint.FilterQuality = SKFilterQuality.High;
#pragma warning restore CS0618
                    canvas.DrawBitmap(source, new SKRect(0, 0, width, height), paint);
                }

                using SKImage image = surface.Snapshot();
                if (image == null)
                    return string.Empty;

                // 尝试 JPEG 编码；失败时降级为 PNG
                SKData? encoded = image.Encode(SKEncodedImageFormat.Jpeg, (int)jpegQuality);
                if (encoded == null)
                {
                    encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                    if (encoded == null)
                        return string.Empty;
                    outputPath = Path.ChangeExtension(outputPath, ".png");
                }

                // 直接写入目标路径——并发去重由 Lazy<string> 保证，不需要原子移动
                byte[] encodedBytes = encoded.ToArray();
                File.WriteAllBytes(outputPath, encodedBytes);
                return outputPath;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static double GetLuminance(SKColor color) =>
            (0.2126 * color.Red + 0.7152 * color.Green + 0.0722 * color.Blue) / 255.0;

        private static double GetSaturation(SKColor color)
        {
            double r = color.Red / 255.0;
            double g = color.Green / 255.0;
            double b = color.Blue / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            return max <= 0 ? 0 : (max - min) / max;
        }

        private static Windows.UI.Color TuneAccentColor(SKColor color)
        {
            RgbToHsl(color, out double h, out double s, out double l);
            
            // 对于黑白颜色（饱和度很低），保持其灰度特性
            // 不提升饱和度，只调整亮度到合适范围
            if (s < 0.1)
            {
                l = Math.Clamp(l * 1.04, 0.30, 0.58);
                return HslToColor(h, s, l);
            }
            
            // 对于彩色颜色，保持原有的调整逻辑
            s = Math.Clamp(s * 1.18, 0.34, 0.86);
            l = Math.Clamp(l * 1.04, 0.30, 0.58);
            return HslToColor(h, s, l);
        }

        private static void RgbToHsl(SKColor color, out double h, out double s, out double l)
        {
            double r = color.Red / 255.0;
            double g = color.Green / 255.0;
            double b = color.Blue / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2.0;

            if (Math.Abs(max - min) < 0.0001)
            {
                h = 0;
                s = 0;
                return;
            }

            double delta = max - min;
            s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

            if (Math.Abs(max - r) < 0.0001)
                h = (g - b) / delta + (g < b ? 6 : 0);
            else if (Math.Abs(max - g) < 0.0001)
                h = (b - r) / delta + 2;
            else
                h = (r - g) / delta + 4;

            h /= 6;
        }

        private static Windows.UI.Color HslToColor(double h, double s, double l)
        {
            double r;
            double g;
            double b;

            if (s <= 0)
            {
                r = g = b = l;
            }
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = HueToRgb(p, q, h + 1.0 / 3.0);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3.0);
            }

            return Windows.UI.Color.FromArgb(
                255,
                (byte)Math.Round(r * 255),
                (byte)Math.Round(g * 255),
                (byte)Math.Round(b * 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }

        private static double ColorDistance(Windows.UI.Color first, Windows.UI.Color second)
        {
            int dr = first.R - second.R;
            int dg = first.G - second.G;
            int db = first.B - second.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        private static SKColor ToSKColor(Windows.UI.Color color) =>
            new(color.R, color.G, color.B, color.A);

        private static IReadOnlyList<Windows.UI.Color> CreateFallbackAccentColors(int count)
        {
            Windows.UI.Color[] colors =
            {
                Windows.UI.Color.FromArgb(255, 104, 56, 190),
                Windows.UI.Color.FromArgb(255, 176, 68, 132),
                Windows.UI.Color.FromArgb(255, 82, 44, 146),
                Windows.UI.Color.FromArgb(255, 198, 84, 52)
            };

            return Enumerable.Range(0, Math.Max(1, count))
                .Select(index => colors[index % colors.Length])
                .ToList();
        }

        private sealed class ColorBucket
        {
            private double _r;
            private double _g;
            private double _b;
            private double _weight;

            public double Score { get; private set; }

            public void Add(SKColor color, double score)
            {
                _r += color.Red * score;
                _g += color.Green * score;
                _b += color.Blue * score;
                _weight += score;
                Score += score;
            }

            public SKColor ToColor()
            {
                double weight = Math.Max(1, _weight);
                return new SKColor(
                    (byte)Math.Round(_r / weight),
                    (byte)Math.Round(_g / weight),
                    (byte)Math.Round(_b / weight));
            }
        }

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

        private static int ReadUInt16LittleEndian(ReadOnlySpan<byte> data, int offset) =>
            data[offset] | (data[offset + 1] << 8);

        private static uint ReadUInt32BigEndian(Stream stream)
        {
            Span<byte> data = stackalloc byte[4];
            stream.ReadExactly(data);
            return ((uint)data[0] << 24) |
                   ((uint)data[1] << 16) |
                   ((uint)data[2] << 8) |
                   data[3];
        }

        private static ulong ReadUInt64BigEndian(Stream stream)
        {
            Span<byte> data = stackalloc byte[8];
            stream.ReadExactly(data);
            ulong value = 0;
            foreach (byte current in data)
                value = (value << 8) | current;
            return value;
        }
    }
}
