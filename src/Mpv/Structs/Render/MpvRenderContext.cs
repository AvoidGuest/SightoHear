// Copyright (c) Bili Copilot. All rights reserved.

using System.Runtime.InteropServices;

namespace SightoHear.Mpv.Structs.Render;

[StructLayout(LayoutKind.Sequential)]
public struct MpvRenderContextHandle
{
    public IntPtr Handle;
}