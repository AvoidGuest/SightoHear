using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 播放进度平滑驱动器。
    ///
    /// <para>
    /// 问题：进度条只能在定时器 tick（250~500ms）时被赋一次值，于是每次都是一格一格地跳，
    /// 视觉上明显不连续。
    /// </para>
    /// <para>
    /// 思路：定时器只负责告诉本类"真实播放位置在 T 时刻是 P、当前速率是 R"，
    /// 真正的 UI 值由 <see cref="CompositionTarget.Rendering"/>（每帧回调，跟随显示器刷新率）
    /// 用 <c>P + (now - T) * R</c> 外推得到。播放中位置线性前进，外推与实际完全吻合，
    /// 因此每帧都是连续的新值，肉眼看到的就是顺滑滚动；暂停时 R=0，值恒定不动。
    /// </para>
    /// <para>
    /// 用户拖动进度条时调用 <see cref="SnapTo"/> 直接落位，不做外推，避免"拖了又被拉回去"。
    /// </para>
    /// </summary>
    public sealed class SmoothProgressDriver
    {
        private readonly Action<double> _apply;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private double _basePosition;
        private double _duration;
        private double _rate;
        private long _baseTimestamp;
        private double _lastApplied = double.NaN;
        private bool _attached;

        /// <param name="apply">把当前应显示的进度值写进控件（进度条 Value / 填充宽度等）。</param>
        public SmoothProgressDriver(Action<double> apply)
        {
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        }

        /// <summary>开始接收逐帧回调（页面 Loaded / 播放开始时调用）。</summary>
        public void Start()
        {
            if (_attached) return;
            _attached = true;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>停止逐帧回调（页面 Unloaded / 控件销毁时调用，避免回调泄漏）。</summary>
        public void Stop()
        {
            if (!_attached) return;
            _attached = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        /// <summary>
        /// 更新真实播放位置（由播放器的位置定时器/事件按秒级频率调用）。
        /// </summary>
        /// <param name="position">当前真实播放位置（秒）。</param>
        /// <param name="duration">媒体总时长（秒）；&lt;=0 表示未知。</param>
        /// <param name="rate">每秒前进的进度值（正常播放=1，暂停=0，1.5 倍速=1.5）。</param>
        public void SetPosition(double position, double duration, double rate)
        {
            _basePosition = position;
            _baseTimestamp = _clock.ElapsedTicks;
            _duration = duration;
            _rate = rate > 0 ? rate : 0;
        }

        /// <summary>
        /// 立即把显示值落到指定位置（拖动进度条 / 跳转 / 切换媒体时调用）。
        /// </summary>
        public void SnapTo(double position)
        {
            _basePosition = position;
            _baseTimestamp = _clock.ElapsedTicks;
            Apply(position, force: true);
        }

        /// <summary>媒体时长发生变化时同步（不做值刷新）。</summary>
        public void SetDuration(double duration) => _duration = duration;

        private void OnRendering(object? sender, object e)
        {
            if (_duration <= 0) return;

            double elapsed = (double)(_clock.ElapsedTicks - _baseTimestamp) / Stopwatch.Frequency;
            double projected = _basePosition + elapsed * _rate;
            Apply(projected, force: false);
        }

        private void Apply(double value, bool force)
        {
            double clamped = value;
            if (_duration > 0)
                clamped = Math.Clamp(value, 0, _duration);
            if (clamped < 0) clamped = 0;

            // 变化量小于半毫秒就不重设，避免每帧无意义地触发布局/绑定
            if (!force && !double.IsNaN(_lastApplied) && Math.Abs(clamped - _lastApplied) < 0.0005)
                return;

            _lastApplied = clamped;
            _apply(clamped);
        }
    }
}
