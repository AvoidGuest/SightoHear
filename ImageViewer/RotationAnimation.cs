using System;
using Windows.Foundation;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 旋转动画：把图片从当前角度平滑旋转到目标角度（±90°，落定必为 90° 倍数）。
    /// 角度用独立弹簧驱动（线性空间，单位度），从当前 <see cref="CanvasViewState.RotationAngle"/>
    /// 连续过渡到目标角。目标角由 <see cref="CanvasViewManager.RotateBy"/> 从逻辑方向累计推导
    /// （跟踪当前周数），因此快速连点旋转时弹簧持续向最新目标收敛、方向不跳变，且静止角度
    /// 永远落在 90° 倍数上。
    /// 旋转前若处于"适应窗口"状态，落定后由宿主启动一次平滑重新适应（宽高互换）。
    /// 旋转不驱动平移/缩放；用户拖动通过 <see cref="NudgePan"/> 直接改平移，与本动画共存。
    /// </summary>
    internal sealed class RotationAnimation : IViewAnimation
    {
        private readonly IAnimationHost _host;
        private readonly SpringAxis _angleSpring = new();

        private float _targetAngle;
        private bool _refitAfter;
        private Size _targetCanvasSize;

        public RotationAnimation(IAnimationHost host) => _host = host;

        /// <summary>
        /// 把旋转弹簧从当前视图角度重新瞄准到 <paramref name="targetAngle"/>。
        /// 弹簧硬重置到当前角度（速度清零）：连点旋转时从当前位置重新起步向最新目标收敛。
        /// </summary>
        public void Aim(float targetAngle, bool refitAfter, Size canvasSize)
        {
            _angleSpring.Reset((float)_host.View.RotationAngle);
            _targetAngle = targetAngle;
            _refitAfter = refitAfter;
            _targetCanvasSize = canvasSize;
        }

        public void Tick()
        {
            if (!_host.TryGetDt(out var dt)) return;

            _angleSpring.Step(_targetAngle, dt);
            var settled = _angleSpring.IsSettled(_targetAngle, SpringConstants.RotationSettleEpsilon, SpringConstants.RotationVelocitySettle);

            var view = _host.View;
            view.RotationAngle = settled ? _targetAngle : _angleSpring.Position;
            view.UpdateTransform();
            _host.RaiseViewChanged();

            if (settled)
            {
                _angleSpring.SettleTo(_targetAngle);
                view.RotationAngle = _targetAngle;
                view.UpdateTransform();
                _host.FinishRotation(_refitAfter, _targetCanvasSize);
            }
        }

        public void NudgePan(double dx, double dy)
        {
            // 旋转动画不拥有平移轨迹；拖动直接改平移并立即反馈（旋转角度照常收敛）
            var view = _host.View;
            view.ImagePos.X += dx;
            view.ImagePos.Y += dy;
            view.UpdateTransform();
        }

        public void CompleteImmediately()
        {
            // 吸附到目标角：逻辑方向（Rotation）已由 RotateBy 更新，此处只收拢视觉角度
            var view = _host.View;
            view.RotationAngle = _targetAngle;
            view.UpdateTransform();
        }
    }
}
