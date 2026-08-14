using System;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 弹簧动画的物理参数与工具常量（移植自 FlyPhotos.Core.Constants 中的弹簧部分）。
    /// 阻尼谐振子：accel = -Stiffness·位移 - Damping·速度。
    /// 手感由派生量决定：
    ///   固有频率   ω0 = sqrt(Stiffness)   ≈ 22.4 rad/s（多快想动）
    ///   阻尼比     ζ  = Damping / (2·sqrt(Stiffness)) ≈ 1.12（运动的形状，略过阻尼、无超调）
    /// 调整速度时应同时缩放 Stiffness 和 Damping，保持 Damping ≈ 2·sqrt(Stiffness)。
    /// </summary>
    internal static class SpringConstants
    {
        /// <summary>恢复力强度——"干脆度"。越大缩放越快越干脆。</summary>
        public const float Stiffness = 500f;

        /// <summary>摩擦——控制超调 vs 迟钝。略大于临界阻尼（44.7），缩放永不回弹。</summary>
        public const float Damping = 50f;

        /// <summary>单帧 dt 上限（秒）。动画卡顿恢复后不会一下子跳一大段。</summary>
        public const float MaxDtSeconds = 0.05f;

        /// <summary>积分最大子步长（约 240Hz）。帧率无关，且大帧（如启动首帧）不会积分超调。</summary>
        public const float MaxSubStepSeconds = 1f / 240f;

        /// <summary>缩放（log 空间）静止判定：位移阈值。</summary>
        public const float ScaleSettleEpsilon = 0.0008f;

        /// <summary>缩放（log 空间）静止判定：速度阈值（log 单位/秒）。</summary>
        public const float ScaleVelocitySettle = 0.05f;

        /// <summary>平移静止判定：位移阈值（像素）。</summary>
        public const double PanSettleEpsilon = 0.1;

        /// <summary>平移静止判定：速度阈值（像素/秒）。</summary>
        public const float PanVelocitySettle = 2f;

        /// <summary>
        /// 锚定缩放（滚轮/键盘）落定前，把 ≤0.5px 的网格对齐偏移混入平移的 log 缩放窗口。
        /// 窗口外锚点被精确钉住（零漂移），窗口内偏移平滑滑入，落定帧干净无跳变。
        /// </summary>
        public const double ZoomGridAlignBlendRangeLog = 0.08;

        /// <summary>旋转（角度空间，线性度）静止判定：位移阈值（度）。</summary>
        public const float RotationSettleEpsilon = 0.05f;

        /// <summary>旋转（角度空间，线性度）静止判定：速度阈值（度/秒）。</summary>
        public const float RotationVelocitySettle = 5f;
    }
}
