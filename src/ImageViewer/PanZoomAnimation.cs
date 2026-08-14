using System;
using Windows.Foundation;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 组合平移+缩放，朝向明确目标（适应窗口 / 100% / 居中 / 双击切换 / 启动展开、退出收起）。
    /// 缩放弹簧跑在 log 空间；平移 X/Y 各自独立弹簧（像素空间），朝预先对齐像素网格的目标收敛，
    /// 静止帧因此落在设备像素网格上（静止取整为空操作）。
    /// </summary>
    internal sealed class PanZoomAnimation : IViewAnimation
    {
        private readonly IAnimationHost _host;
        private Point _panTarget;
        private Size _targetCanvasSize;

        public PanZoomAnimation(IAnimationHost host) => _host = host;

        /// <summary>
        /// 把弹簧瞄准到 <paramref name="targetScale"/> / <paramref name="targetPosition"/>。
        /// 平移目标预先对齐像素网格，静止取整即为空操作。<paramref name="reseedPan"/> 表示是否
        /// 从当前实时位置重新播种平移弹簧（前一个动画不是平移弹簧、或强制重置时）；否则平移速度
        /// 保持延续，重瞄准无缝衔接。
        /// </summary>
        public void Aim(float targetScale, Point targetPosition, Size targetCanvasSize, bool reseedPan)
        {
            var view = _host.View;
            if (reseedPan)
            {
                _host.PanXSpring.Reset((float)view.ImagePos.X);
                _host.PanYSpring.Reset((float)view.ImagePos.Y);
            }
            _host.TargetScale = targetScale;
            _panTarget = view.SnapImagePosToPixelGrid(targetScale, targetPosition);
            _targetCanvasSize = targetCanvasSize;
        }

        public void Tick()
        {
            if (!_host.TryGetDt(out var dt)) return;

            var view = _host.View;
            var scale = _host.ScaleSpring;
            var panX = _host.PanXSpring;
            var panY = _host.PanYSpring;

            var targetLog = (float)Math.Log(_host.TargetScale);
            scale.Step(targetLog, dt);
            panX.Step((float)_panTarget.X, dt);
            panY.Step((float)_panTarget.Y, dt);

            var scaleSettled = scale.IsSettled(targetLog, SpringConstants.ScaleSettleEpsilon, SpringConstants.ScaleVelocitySettle);
            var panSettled = panX.IsSettled((float)_panTarget.X, SpringConstants.PanSettleEpsilon, SpringConstants.PanVelocitySettle)
                             && panY.IsSettled((float)_panTarget.Y, SpringConstants.PanSettleEpsilon, SpringConstants.PanVelocitySettle);
            var settled = scaleSettled && panSettled;

            if (settled)
            {
                scale.SettleTo(targetLog);
                panX.SettleTo((float)_panTarget.X);
                panY.SettleTo((float)_panTarget.Y);
                view.Scale = _host.TargetScale;
            }
            else
            {
                view.Scale = (float)Math.Exp(scale.Position);
            }
            view.ImagePos.X = panX.Position;
            view.ImagePos.Y = panY.Position;
            view.UpdateTransform();
            _host.RaiseViewChanged();
            _host.RaiseZoomChanged();

            if (settled)
            {
                _host.ReportSettledFit(_targetCanvasSize); // 用静止缩放校准适应 / 1:1 状态
                _host.FinishSpring();
            }
        }

        public void NudgePan(double dx, double dy)
        {
            // 平移目标与当前平移弹簧状态同时平移 δ：整条轨迹跟随光标，
            // 弹簧继续收敛（现在朝向移动后的目标），而不是下一帧把图片拖回原目标。
            _panTarget.X += dx;
            _panTarget.Y += dy;
            _host.PanXSpring.Position += (float)dx;
            _host.PanYSpring.Position += (float)dy;

            var view = _host.View;
            view.ImagePos.X += dx;
            view.ImagePos.Y += dy;
            view.UpdateTransform();
        }

        public void CompleteImmediately()
        {
            var view = _host.View;
            view.Scale = _host.TargetScale;
            view.ImagePos = _panTarget;
            view.UpdateTransform();
        }
    }
}
