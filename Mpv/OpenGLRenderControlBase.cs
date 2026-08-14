using System;
using System.Diagnostics;
using System.Threading;

namespace SightoHear.Mpv.Common;

/// <summary>
/// 渲染循环基类：使用独立渲染线程替代 CompositionTarget.Rendering。
/// 由 mpv_render_context_set_update_callback 通知新帧就绪（ManualResetEventSlim 唤醒）。
/// </summary>
public abstract class OpenGLRenderControlBase<TFrame> : Microsoft.UI.Xaml.Controls.ContentControl
    where TFrame : FrameBufferBase
{
    protected Stopwatch _stopwatch = Stopwatch.StartNew();
    protected TimeSpan _lastFrameStamp;

    protected TFrame? FrameBuffer { get; set; }

    /// <summary>渲染门控。mpv 初始化期间为 false，完成后置 true。</summary>
    public volatile bool IsRenderingEnabled = true;

    private Thread? _renderThread;
    private volatile bool _renderThreadRunning;
    private readonly ManualResetEventSlim _frameReadyEvent = new(false);
    private readonly CancellationTokenSource _renderLoopCts = new();

    protected void StartRenderLoop()
    {
        if (_renderThread != null) return;
        _renderThreadRunning = true;
        _frameReadyEvent.Reset();
        _renderThread = new Thread(RenderLoop)
        {
            Name = "MpvGL-RenderThread",
            IsBackground = true,
        };
        _renderThread.Start();
    }

    protected void StopRenderLoop()
    {
        _renderThreadRunning = false;
        try { _renderLoopCts.Cancel(); } catch { }
        try { _frameReadyEvent.Set(); } catch { }
        var thread = _renderThread;
        if (thread != null && thread.IsAlive) thread.Join(2000);
        _renderThread = null;
        try { _renderLoopCts.Dispose(); } catch { }
        try { _frameReadyEvent.Dispose(); } catch { }
    }

    /// <summary>mpv update callback 唤醒渲染线程。</summary>
    public void SignalFrameReady()
    {
        if (_renderThreadRunning) try { _frameReadyEvent.Set(); } catch { }
    }

    private void RenderLoop()
    {
        OnRenderThreadStart();
        while (_renderThreadRunning)
        {
            bool signaled;
            try
            {
                signaled = _frameReadyEvent.Wait(16, _renderLoopCts.Token);
                _frameReadyEvent.Reset();
            }
            catch (OperationCanceledException) { break; }

            if (!_renderThreadRunning) break;
            if (!IsRenderingEnabled) continue;
            if (FrameBuffer == null) continue;

            try { Draw(); }
            catch (Exception ex) { Helpers.AppLogger.Warning($"libmpv渲染线程异常: {ex.Message}"); }

            if (!signaled && _renderThreadRunning) Thread.Sleep(0);
        }
        OnRenderThreadStop();
    }

    protected virtual void OnRenderThreadStart() { }
    protected virtual void OnRenderThreadStop() { }

    public virtual void Release() { StopRenderLoop(); }

    /// <summary>绘制一帧（渲染线程调用）。</summary>
    protected abstract void Draw();

    public virtual void Initialize() { }
}
