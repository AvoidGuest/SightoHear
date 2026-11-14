using System;

namespace SightoHear.Mpv.Common;

public abstract partial class FrameBufferBase : IDisposable
{
    public abstract int BufferWidth { get; protected set; }

    public abstract int BufferHeight { get; protected set; }

    public abstract IntPtr SwapChainHandle { get; protected set; }

    public abstract void Dispose();
}
