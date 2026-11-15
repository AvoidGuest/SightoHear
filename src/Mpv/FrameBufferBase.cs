using System;

namespace SightoHear.Mpv.Common;

public abstract partial class FrameBufferBase : IDisposable
{
    public abstract int BufferWidth { get; protected set; }

    public abstract int BufferHeight { get; protected set; }

    public abstract IntPtr SwapChainHandle { get; protected set; }

    public abstract void Dispose();

    /// <summary>
    /// 把 DIP 尺寸换算成像素尺寸（交换链实际分辨率）。
    /// ★ 全项目唯一入口：RenderControl 的尺寸判定与 FrameBuffer 的构造必须都走这里。
    /// 曾经两边各写一套（一边 <c>(int)(dip * scale)</c>、一边
    /// <c>Convert.ToInt32((int)dip * scale)</c>），浮点截断时机不同，
    /// 在非整数缩放（如 125%/150%）下会稳定差 1 像素 → 判定永远不相等 →
    /// 每帧都重建交换链 → 播放画面持续黑闪、抽搐。
    /// 用 <see cref="Math.Round(double, MidpointRounding)"/> 而非直接截断，
    /// 避免多次重复计算时在 .5 边界来回抖动。
    /// </summary>
    public static int ComputePixels(double dip, double scale)
    {
        if (dip <= 0 || scale <= 0) return 0;
        // 先按浮点相乘，再四舍五入到最近整数（.5 向上），最后夹到 >= 1
        int px = (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);
        return px < 1 ? 1 : px;
    }
}
