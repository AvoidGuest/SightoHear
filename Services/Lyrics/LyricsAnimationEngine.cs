using System.Collections.Generic;

namespace SightoHear.Services.Lyrics
{
    public static class EasingHelper
    {
        public static double EaseInCubic(double t) => t * t * t;
        public static double EaseOutCubic(double t) => 1 - System.Math.Pow(1 - t, 3);
        public static double EaseInOutCubic(double t)
        {
            return t < 0.5
                ? 4 * t * t * t
                : 1 - System.Math.Pow(-2 * t + 2, 3) / 2;
        }
        public static double EaseOutSine(double t) => System.Math.Sin(t * System.Math.PI / 2);
    }

    public class SyllableAnimationState
    {
        public double CurrentOffset { get; set; } = 0;
    }

    public class LineAnimationManager
    {
        // ── 浮起动画配置 ─────────────────────────────────────────
        // 英文歌词：振幅 / 持续时间 → 浮得更高、更快
        // 中文歌词：振幅 / 持续时间 → 浮得更低、更慢（见 Configure 中的 isCJK 分支）
        private const double FloatDelayMs = 150.0;
        private const double DefaultFloatDurationMs = 500.0;
        private const double CjkFloatDurationMs = 1000.0;
        // ──────────────────────────────────────────────────────────

        private readonly Dictionary<int, (double StartMs, double EndMs, double Amplitude, double DurationMs)> _charConfigs = new();
        private readonly Dictionary<int, SyllableAnimationState> _states = new();

        public void Configure(int charIndex, double startMs, double endMs, double amplitude, bool isCJK = false)
        {
            _charConfigs[charIndex] = (startMs, endMs, isCJK ? amplitude * 0.75 : amplitude, isCJK ? CjkFloatDurationMs : DefaultFloatDurationMs);
            if (!_states.ContainsKey(charIndex))
                _states[charIndex] = new SyllableAnimationState();
        }

        public void Update(double currentTimeMs)
        {
            foreach (var kvp in _charConfigs)
            {
                int index = kvp.Key;
                var (startMs, _, amplitude, durationMs) = kvp.Value;

                double floatStartMs = startMs + FloatDelayMs;

                double offset = 0;
                if (currentTimeMs >= floatStartMs)
                {
                    double progress = System.Math.Clamp((currentTimeMs - floatStartMs) / durationMs, 0, 1);
                    double eased = EasingHelper.EaseOutSine(progress);
                    offset = -amplitude * eased;
                }

                _states[index].CurrentOffset = offset;
            }
        }

        public double GetFloatOffset(int charIndex)
        {
            if (charIndex < 0 || !_states.TryGetValue(charIndex, out var state))
                return 0;
            return state.CurrentOffset;
        }

        public void Reset()
        {
            foreach (var state in _states.Values)
                state.CurrentOffset = 0;
            _states.Clear();
            _charConfigs.Clear();
        }
    }
}
