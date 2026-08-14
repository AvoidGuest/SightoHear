using System;
using Windows.Foundation;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 衰减正弦"抖动"：操作被拒绝的反馈（如删除失败）。基于时间而非弹簧：
    /// 抖动 <see cref="ShrugAnimationDurationMs"/> 毫秒后把图片精确弹回起点。
    /// </summary>
    internal sealed class ShrugAnimation : IViewAnimation
    {
        private const double ShrugAnimationDurationMs = 350;
        private const double ShrugAmplitude = 20;  // 抖动幅度（像素）
        private const double ShrugFrequency = 4;   // 抖动次数

        private readonly IAnimationHost _host;
        private Point _startPosition;

        public ShrugAnimation(IAnimationHost host) => _host = host;

        /// <summary>记录抖动围绕并回归的起始位置。</summary>
        public void Start() => _startPosition = _host.View.ImagePos;

        public void Tick()
        {
            var view = _host.View;
            var t = Math.Clamp(_host.ElapsedMs / ShrugAnimationDurationMs, 0.0, 1.0);

            if (t >= 1.0)
            {
                // 结束：确保图片回到精确起点，然后恢复取整并重绘
                view.ImagePos = _startPosition;
                _host.FinishShrug();
                return;
            }

            // (1 - t) 衰减振幅；正弦提供往返运动。Y 轴不动。
            var damping = 1 - t;
            var wave = Math.Sin(t * ShrugFrequency * 2 * Math.PI);
            var xOffset = ShrugAmplitude * wave * damping;

            view.ImagePos.X = _startPosition.X + xOffset;
            view.UpdateTransform();
            _host.RaiseViewChanged();
        }

        /// <summary>平移抖动围绕的起点，使抖动期间的拖动不被丢弃。</summary>
        public void NudgePan(double dx, double dy)
        {
            _startPosition.X += dx;
            _startPosition.Y += dy;
        }

        /// <summary>抖动没有可跳转的静止目标，强制停止即直接结束摆动。</summary>
        public void CompleteImmediately() { }
    }
}
