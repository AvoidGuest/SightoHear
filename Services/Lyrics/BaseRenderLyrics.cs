using System;

namespace SightoHear.Services.Lyrics
{
    public class BaseRenderLyrics : BaseLyrics
    {
        public bool IsPlayingLastFrame { get; set; } = false;

        public BaseRenderLyrics(BaseLyrics baseLyrics)
        {
            Text = baseLyrics.Text;
            StartMs = baseLyrics.StartMs;
            EndMs = baseLyrics.EndMs;
            StartIndex = baseLyrics.StartIndex;
        }

        public bool GetIsPlaying(double currentMs) => StartMs <= currentMs && currentMs < EndMs;
        public double GetPlayProgress(double currentMs)
        {
            int duration = DurationMs;
            if (duration <= 0)
                return currentMs >= StartMs ? 1 : 0;

            return Math.Clamp((currentMs - StartMs) / duration, 0, 1);
        }
    }
}
