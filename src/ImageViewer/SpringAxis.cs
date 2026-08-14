using System;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 阻尼谐振子的一条轴（移植自 FlyPhotos.Display.Controllers.Animation.SpringAxis）。
    /// 持有 <see cref="Position"/> 与 <see cref="Velocity"/>，每帧向目标 <see cref="Step"/> 并判断是否静止。
    /// 缩放弹簧跑在 log 空间（Position 存 log(scale)），平移弹簧跑在像素空间。
    /// 长期驻留的字段，原地修改，不做分配。
    /// </summary>
    internal sealed class SpringAxis
    {
        /// <summary>当前值（如 log(scale)，或平移像素坐标）。</summary>
        public float Position;

        /// <summary>当前变化速率（Position 单位/秒）。</summary>
        public float Velocity;

        /// <summary>以 <paramref name="position"/> 重置并清零速度（有意的硬重置）。</summary>
        public void Reset(float position)
        {
            Position = position;
            Velocity = 0f;
        }

        /// <summary>精确吸附到 <paramref name="target"/> 并停止——静止判定通过时调用。</summary>
        public void SettleTo(float target)
        {
            Position = target;
            Velocity = 0f;
        }

        /// <summary>当剩余位移与速度都低于阈值时为 true。</summary>
        public bool IsSettled(float target, double positionEpsilon, double velocityEpsilon) =>
            Math.Abs(Position - target) < positionEpsilon && Math.Abs(Velocity) < velocityEpsilon;

        /// <summary>
        /// 用 Euler 积分前进一帧（固定子步长），向 <paramref name="target"/> 收敛。
        /// 子步进让大 dt（如启动首帧）不会单步超调；画布仍每帧只画一次，且运动与帧率无关。
        /// </summary>
        public void Step(float target, float dt)
        {
            var steps = Math.Max(1, (int)Math.Ceiling(dt / SpringConstants.MaxSubStepSeconds));
            var h = dt / steps;
            for (var i = 0; i < steps; i++)
            {
                var displacement = Position - target;
                var acceleration = -SpringConstants.Stiffness * displacement - SpringConstants.Damping * Velocity;
                Velocity += acceleration * h;
                Position += Velocity * h;
            }
        }
    }
}
