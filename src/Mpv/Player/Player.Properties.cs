// Copyright (c) Bili Copilot. All rights reserved.

using SightoHear.Mpv.Args;
using SightoHear.Mpv.Enums.Player;
using SightoHear.Mpv.Interop;
using SightoHear.Mpv.Structs.Player;
using System.Threading;

namespace SightoHear.Mpv;

public sealed partial class Player
{
    private bool _isDisposed;
    private bool _isLoaded;
    private long _currentDuration;
    private long _currentPosition;
    private Task? _eventLoopTask;
    private CancellationTokenSource? _eventLoopCancellationTokenSource;
    // 保持 mpv get_proc_address 委托存活，防止被 GC 回收导致原生回调崩溃
    private Delegate? _glGetProcAddressDelegate;

    public event EventHandler<LogMessageReceivedEventArgs>? LogMessageReceived;

    public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public event EventHandler<PlaybackStoppedEventArgs>? PlaybackStopped;

    public event EventHandler<PlaybackPositionChangedEventArgs>? PlaybackPositionChanged;

    public event EventHandler? Destroyed;

    public MpvClientNative Client { get; }

    public MpvRenderContextNative? RenderContext { get; private set; }

    public LibMpvDependencies Dependencies { get; private set; }

    public PlaybackState PlaybackState { get; private set; }

    public bool AutoPlay { get; set; }

    public bool IsLoggingEnabled { get; set; } = true;

    /// <summary>
    /// 该播放器是否已被释放.
    /// </summary>
    public bool IsDisposed => _isDisposed;

    public TimeSpan? Duration => _currentDuration > 0 ? TimeSpan.FromSeconds(_currentDuration) : default;

    public TimeSpan? Position => _currentPosition > 0 ? TimeSpan.FromSeconds(_currentPosition) : default;
}
