using System;
using System.Collections.Generic;

namespace SightoHear.Services.Lyrics
{
    public class LyricsSynchronizer
    {
        private int _lastFoundIndex = 0;

        public void Reset()
        {
            _lastFoundIndex = 0;
        }

        public int GetCurrentLineIndex(double currentTimeMs, IList<RenderLyricsLine>? lines)
        {
            if (lines == null || lines.Count == 0) return 0;

            int left = 0;
            int right = lines.Count - 1;
            int candidate = 0;
            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                if (lines[mid].StartMs <= currentTimeMs)
                {
                    candidate = mid;
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            int scanStart = Math.Max(0, candidate - 8);
            int scanEnd = Math.Min(lines.Count - 1, candidate + 8);
            for (int i = scanStart; i <= scanEnd; i++)
            {
                if (IsTimeInLine(currentTimeMs, lines, i))
                {
                    _lastFoundIndex = i;
                    return i;
                }
            }

            _lastFoundIndex = Math.Clamp(candidate, 0, lines.Count - 1);
            return _lastFoundIndex;
        }

        private static bool IsTimeInLine(double time, IList<RenderLyricsLine> lines, int index)
        {
            if (index < 0 || index >= lines.Count) return false;
            var line = lines[index];
            var nextLine = (index + 1 < lines.Count) ? lines[index + 1] : null;
            if (time < line.StartMs) return false;
            if (nextLine != null && time >= nextLine.StartMs) return false;
            return true;
        }
    }
}
