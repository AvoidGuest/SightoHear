using System;
using System.Numerics;
using Windows.Foundation;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 图片查看器的视图状态：缩放、平移、旋转的唯一数据源（移植自 FlyPhotos.Display.State.CanvasViewState）。
    /// 所有用户操作（滚轮/拖动/旋转/适应/1:1）最终都写入这里，再由 <see cref="Mat"/> 矩阵应用到渲染。
    /// 本类只存状态，不含任何交互/动画逻辑。
    /// </summary>
    internal sealed class CanvasViewState
    {
        /// <summary>图片原始像素尺寸（绘制在像素单位下，见 CanvasDisplayController）。</summary>
        public Rect ImageRect;

        /// <summary>图片中心在画布上的位置（像素）。</summary>
        public Point ImagePos = new(0, 0);

        /// <summary>完整变换矩阵：中心原点 → 缩放 → 旋转 → 平移。</summary>
        public Matrix3x2 Mat;

        /// <summary>Mat 的逆矩阵，用于命中测试（判断指针是否点在图片上）。</summary>
        public Matrix3x2 MatInv;

        /// <summary>当前缩放值（1.0 = 原始像素 1:1）。</summary>
        public float Scale = 1.0f;

        /// <summary>最近一次"瞄准"的缩放值；连续缩放（滚轮快速滚动）以此为基准，保证手感连续。</summary>
        public float LastScaleTo = 1.0f;

        /// <summary>用户旋转方向（度，90 步进，0/90/180/270）。逻辑状态：适应/大小切换按它判断宽高互换。</summary>
        public int Rotation = 0;

        /// <summary>
        /// 连续旋转角度（度，可累计超过 360）。矩阵变换用它，<see cref="RotationAnimation"/>
        /// 每帧驱动它平滑过渡到目标；静止时与 <see cref="Rotation"/> 保持一致。
        /// </summary>
        public float RotationAngle = 0;

        /// <summary>
        /// 静止时是否将平移取整到设备像素。
        /// 静止开启：平移落在设备像素网格上，避免 NVIDIA 最近邻采样闪烁（FlyPhotos #55）；
        /// 动画期间关闭：取整会把连续的亚像素运动量化成 1px 阶梯，产生可见的"颤振"。
        /// </summary>
        public bool SnapTranslation = true;

        /// <summary>
        /// 由 Scale / ImagePos / Rotation 重建完整矩阵。
        /// 静止（SnapTranslation = true）时对平移分量做 ±0.5px 取整；动画期间跳过取整。
        /// 动画的目标点通过 <see cref="SnapImagePosToPixelGrid"/> 预先量化，静止帧自然落在像素网格上。
        /// </summary>
        public void UpdateTransform()
        {
            Mat = ComposeUnsnapped(Scale, ImagePos);

            // 取整必须在完整组合之后（取整前置项会被 Scale 放大，如 2000% 时误差可达 ±10px）
            if (SnapTranslation)
            {
                Mat.M31 = MathF.Round(Mat.M31);
                Mat.M32 = MathF.Round(Mat.M32);
            }

            Matrix3x2.Invert(Mat, out MatInv);
        }

        /// <summary>
        /// 构建完整的 图片→屏幕 变换（中心原点 → 缩放 → 旋转 → 平移），不做像素取整。
        /// 与 <see cref="UpdateTransform"/> 共用，保证取整前/后的组合方式永远一致。
        /// </summary>
        private Matrix3x2 ComposeUnsnapped(float scale, Point imagePos)
        {
            var m = Matrix3x2.Identity;
            m *= Matrix3x2.CreateTranslation((float)(-ImageRect.Width * 0.5f), (float)(-ImageRect.Height * 0.5f));
            m *= Matrix3x2.CreateScale(scale, scale);
            m *= Matrix3x2.CreateRotation((float)(Math.PI * RotationAngle / 180f));
            m *= Matrix3x2.CreateTranslation((float)imagePos.X, (float)imagePos.Y);
            return m;
        }

        /// <summary>
        /// 把 <paramref name="imagePos"/> 微调（≤0.5px）到使完整组合后的平移（Mat.M31/M32）
        /// 落在整像素上。喂给动画作为静止目标后，静止时的取整即为空操作，缩放结束不再有跳变。
        /// </summary>
        public Point SnapImagePosToPixelGrid(float scale, Point imagePos)
        {
            var m = ComposeUnsnapped(scale, imagePos);
            return new Point(imagePos.X - (m.M31 - MathF.Round(m.M31)),
                             imagePos.Y - (m.M32 - MathF.Round(m.M32)));
        }
    }
}
