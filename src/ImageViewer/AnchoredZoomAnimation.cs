using System;
using Windows.Foundation;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 图片中心锚定缩放：缩放弹簧跑在 log 空间，平移每帧推导以保证图片中心不动
    /// （原地缩放，移植自 FlyPhotos.Display.Controllers.Animation.AnchoredZoomAnimation）。
    /// 内部维护"纯锚点轨迹"（平移严格跟随缩放，不掺入网格偏移，避免反馈累积漂移）
    /// 与一个 ≤0.5px 的常量网格对齐偏移，后者在落定尾部混入，使静止帧落在设备像素网格上。
    /// 锚点由 <see cref="CanvasViewManager.ZoomAtPoint"/> 统一指定为图片中心（ImagePos）。
    /// </summary>
    internal sealed class AnchoredZoomAnimation : IViewAnimation
    {
        private readonly IAnimationHost _host;
        private Point _zoomCenter;
        private double _anchorX;
        private double _anchorY;
        private double _gridOffsetX;
        private double _gridOffsetY;

        public AnchoredZoomAnimation(IAnimationHost host) => _host = host;

        /// <summary>
        /// 把缩放瞄准到 <paramref name="targetScale"/>，锚定在 <paramref name="anchor"/>。
        /// 全新开始时（<paramref name="resetAnchorTrack"/> = true）锚点轨迹从当前（已对齐网格的）位置播种；
        /// 重新瞄准时保留，使常量网格偏移不会反馈累积。无论哪种情况都会按新目标重算偏移 δ。
        /// </summary>
        public void Aim(float targetScale, Point anchor, bool resetAnchorTrack)
        {
            var view = _host.View;
            _zoomCenter = anchor;
            if (resetAnchorTrack)
            {
                _anchorX = view.ImagePos.X;
                _anchorY = view.ImagePos.Y;
            }
            _host.TargetScale = targetScale;

            // 预计算常量网格对齐偏移 δ（每轴 ≤0.5px）：精确锚定缩放的落点，微调到整像素，存下差值。
            // Tick 在落定尾部把 δ 混入，静止帧因此落在像素网格上。
            var startScale = view.Scale;
            if (startScale > 0f)
            {
                var anchorFinal = ZoomGeometry.AnchorPreservingPan(_zoomCenter, new Point(_anchorX, _anchorY), startScale, targetScale);
                var aligned = view.SnapImagePosToPixelGrid(targetScale, anchorFinal);
                _gridOffsetX = aligned.X - anchorFinal.X;
                _gridOffsetY = aligned.Y - anchorFinal.Y;
            }
        }

        public void Tick()
        {
            if (!_host.TryGetDt(out var dt)) return;

            var view = _host.View;
            var scale = _host.ScaleSpring;
            var targetLog = (float)Math.Log(_host.TargetScale);
            scale.Step(targetLog, dt);

            var settled = scale.IsSettled(targetLog, SpringConstants.ScaleSettleEpsilon, SpringConstants.ScaleVelocitySettle);

            float newScale;
            if (settled)
            {
                scale.SettleTo(targetLog);
                newScale = _host.TargetScale;
            }
            else
            {
                newScale = (float)Math.Exp(scale.Position);
            }

            // 推进纯锚点轨迹：平移严格绑定缩放，锚点像素每帧被精确钉住、零晃动。
            // 轨迹从不包含网格偏移，因此不会自我反馈漂移。
            var track = ZoomGeometry.AnchorPreservingPan(_zoomCenter, new Point(_anchorX, _anchorY), view.Scale, newScale);
            _anchorX = track.X;
            _anchorY = track.Y;
            view.Scale = newScale;

            // 在落定尾部混入常量 ≤0.5px 网格偏移：w 在缩放主体期间为 0（锚点精确钉住），
            // 落定时升到 1（静止帧落在像素网格，静止取整变为空操作）。无起始跳变、无结尾快切。
            var w = settled
                ? 1.0
                : Math.Clamp(1.0 - Math.Abs(scale.Position - targetLog) / SpringConstants.ZoomGridAlignBlendRangeLog, 0.0, 1.0);
            view.ImagePos.X = _anchorX + w * _gridOffsetX;
            view.ImagePos.Y = _anchorY + w * _gridOffsetY;
            view.UpdateTransform();
            _host.RaiseViewChanged();
            _host.RaiseZoomChanged();

            if (settled) _host.FinishSpring();
        }

        public void NudgePan(double dx, double dy)
        {
            // 屏幕锚点与纯锚点轨迹同时平移 δ。锚点保持公式是仿射的：
            // 平移 锚点+轨迹 会把任意缩放下重算出的平移也精确平移 δ —— 拖动得以持续而不是被下一帧弹回，
            // 同时缩放继续围绕（已移动的）锚点收敛。
            _zoomCenter.X += dx;
            _zoomCenter.Y += dy;
            _anchorX += dx;
            _anchorY += dy;

            // 本帧立即反馈（dt 为 0 时 Tick 可能不跑）
            var view = _host.View;
            view.ImagePos.X += dx;
            view.ImagePos.Y += dy;
            view.UpdateTransform();
        }

        public void CompleteImmediately()
        {
            var view = _host.View;
            // 纯锚点轨迹直接跳到目标缩放，然后加上完整网格偏移，强制停止的静止帧同样干净。
            var oldScale = view.Scale;
            if (oldScale > 0)
            {
                var track = ZoomGeometry.AnchorPreservingPan(_zoomCenter, new Point(_anchorX, _anchorY), oldScale, _host.TargetScale);
                _anchorX = track.X;
                _anchorY = track.Y;
            }
            view.Scale = _host.TargetScale;
            view.ImagePos.X = _anchorX + _gridOffsetX;
            view.ImagePos.Y = _anchorY + _gridOffsetY;
            view.UpdateTransform();
        }
    }
}
