// Copyright (c) Bili Copilot. All rights reserved.

using SightoHear.Mpv.Enums.Client;
using System.Runtime.InteropServices;

namespace SightoHear.Mpv.Structs.Client;

[StructLayout(LayoutKind.Sequential)]
public struct MpvEventProperty
{
    public string Name;

    public MpvFormat Format;

    public IntPtr DataPtr; //Expand to all formats
}