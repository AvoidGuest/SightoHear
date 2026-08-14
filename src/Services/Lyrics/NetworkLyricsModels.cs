namespace SightoHear.Services.Lyrics
{
    /// <summary>
    /// 支持的网络歌词源。
    /// </summary>
    public enum NetworkLyricsProvider
    {
        Netease,
        QQ,
        Kugou,
        LrcLib,
    }

    /// <summary>
    /// 网络歌词源选择偏好。
    /// 决定检索时是并发使用全部源自动择优，还是固定使用某个源。
    /// </summary>
    public enum NetworkLyricsSourcePreference
    {
        /// <summary>自动：并发检索全部源，按匹配度择优（默认）。</summary>
        Auto,
        /// <summary>仅使用 QQ 音乐。</summary>
        QQ,
        /// <summary>仅使用网易云音乐。</summary>
        Netease,
        /// <summary>仅使用酷狗音乐。</summary>
        Kugou,
        /// <summary>仅使用 LrcLib（lrclib.net）。</summary>
        LrcLib,
    }

    /// <summary>
    /// 单个网络歌词源返回的候选结果。
    /// </summary>
    /// <param name="Provider">来源。</param>
    /// <param name="Title">命中曲目的标题。</param>
    /// <param name="Artist">命中曲目的艺术家。</param>
    /// <param name="Album">命中曲目的专辑。</param>
    /// <param name="Duration">命中曲目的时长（秒）。</param>
    /// <param name="Raw">主歌词原文（LRC/QRC/KRC/YRC/TTML 等）。</param>
    /// <param name="Translation">翻译轨道原文，可为空。</param>
    /// <param name="Transliteration">音译（罗马音）轨道原文，可为空。</param>
    /// <param name="Reference">来源链接，用于追溯。</param>
    /// <param name="MatchScore">与本地曲目的匹配度（0-100）。</param>
    public sealed record NetworkLyricsCandidate(
        NetworkLyricsProvider Provider,
        string? Title,
        string? Artist,
        string? Album,
        double? Duration,
        string? Raw,
        string? Translation = null,
        string? Transliteration = null,
        string? Reference = null,
        int MatchScore = 0)
    {
        public bool HasLyrics => !string.IsNullOrWhiteSpace(Raw);
    }
}
