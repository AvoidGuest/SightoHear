using Windows.Foundation;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 一种视图动画（锚定缩放 / 平移+缩放 / 抖动），由 <see cref="CanvasViewManager"/> 每帧驱动。
    /// 每种实现拥有自己特有的几何与运动；共享的物理底座（弹簧、时钟、目标缩放）与所有事件/
    /// 像素对齐/完成逻辑放在 <see cref="IAnimationHost"/>（即 CanvasViewManager）上。
    /// </summary>
    internal interface IViewAnimation
    {
        /// <summary>推进一帧，把结果写入宿主视图并触发宿主事件。</summary>
        void Tick();

        /// <summary>把视图立即强制到本动画的静止目标（不发事件）。用于切换新图片时继承"落定视图"而非飞行中的中间帧。</summary>
        void CompleteImmediately();

        /// <summary>
        /// 把一个用户平移增量（画布像素）折进动画自身的平移状态，避免动画中途到来的拖动被下一帧
        /// <see cref="Tick"/> 丢弃：图片跟随光标，而动画（如滚轮缩放弹簧）继续向缩放目标收敛。
        /// </summary>
        void NudgePan(double dx, double dy);
    }

    /// <summary>
    /// <see cref="CanvasViewManager"/> 提供给 <see cref="IViewAnimation"/> 的宿主切片：
    /// 实时视图、共享弹簧组 + 帧时钟 + 目标缩放（跨重瞄准、跨动画类型保持速度与时钟连续），
    /// 以及事件 / 适应状态 / 完成通知。
    /// </summary>
    internal interface IAnimationHost
    {
        CanvasViewState View { get; }

        SpringAxis ScaleSpring { get; } // 弹簧 log(scale)
        SpringAxis PanXSpring { get; }  // 弹簧 ImagePos.X（像素）
        SpringAxis PanYSpring { get; }  // 弹簧 ImagePos.Y（像素）

        /// <summary>当前弹簧正在收敛的目标缩放（在缩放动画间共享）。</summary>
        float TargetScale { get; set; }

        /// <summary>当前动画时钟经过的毫秒数（供抖动动画使用）。</summary>
        double ElapsedMs { get; }

        /// <summary>每帧 dt（秒，已钳制）；无时间流逝时返回 false。</summary>
        bool TryGetDt(out float dt);

        void RaiseViewChanged();

        /// <summary>触发缩放变化通知（除非当前动画被抑制，如启动/退出缩放）。</summary>
        void RaiseZoomChanged();

        /// <summary>根据静止缩放设置适应 / 1:1 状态（平移+缩放落定时）。</summary>
        void ReportSettledFit(Size canvasSize);

        /// <summary>完成一次弹簧：恢复像素对齐、重建静止帧、触发动画完成事件。</summary>
        void FinishSpring();

        /// <summary>完成一次抖动：恢复像素对齐、重建静止帧、触发视图变化。</summary>
        void FinishShrug();

        /// <summary>
        /// 完成一次旋转：恢复像素对齐、重建静止帧；若旋转前处于"适应窗口"，
        /// 则启动一次平滑重新适应（宽高互换后的 fit 动画）。
        /// </summary>
        void FinishRotation(bool refit, Size canvasSize);
    }
}
