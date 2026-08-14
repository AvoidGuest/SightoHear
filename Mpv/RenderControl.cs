using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading;
using WinRT;

namespace SightoHear.Mpv.Common;

public sealed unsafe partial class RenderControl : OpenGLRenderControlBase<FrameBuffer>
{
    private SwapChainPanel? _swapChainPanel;

    public ContextSettings Setting { get; set; } = new ContextSettings();
    public RenderContext? Context { get; private set; }

    public event EventHandler? Ready;
    public event Action<TimeSpan>? Render;
    public event Action? Present;

    public double ScaleX => _swapChainPanel?.CompositionScaleX ?? 1;
    public double ScaleY => _swapChainPanel?.CompositionScaleY ?? 1;

    public int RenderWidth => FrameBuffer?.BufferWidth ?? (int)Math.Max(0, ActualWidth * ScaleX);
    public int RenderHeight => FrameBuffer?.BufferHeight ?? (int)Math.Max(0, ActualHeight * ScaleY);

    // ── UI 线程 → 渲染线程 尺寸快照 ──
    private double _snapshotWidth;
    private double _snapshotHeight;
    private double _snapshotScaleX = 1;
    private double _snapshotScaleY = 1;

    // ── 重建队列：旧 FrameBuffer 转移到此字段，下一帧在渲染线程 dispose ──
    private FrameBuffer? _disposeQueue;
    // ── 延迟切换：新 FB 创建后暂存，待 UI 线程 SetSwapChain 完成后才原子切换 ──
    private FrameBuffer? _pendingNewFb;
    private volatile bool _pendingFbBound; // UI 线程写入，渲染线程读取
    // ── 重建防抖 ──
    private long _lastRebuildMs;
    private const long RebuildCooldownMs = 200;

    public RenderControl()
    {
        SizeChanged += OnSizeChanged;
        LayoutUpdated += OnLayoutUpdated;
        Unloaded += OnUnloaded;
    }

    protected override void OnRenderThreadStart()
    {
        _glContextAcquired = false;
        Helpers.AppLogger.Info("libmpv：渲染线程已启动，等待 GL 上下文释放");
    }

    protected override void OnRenderThreadStop()
    {
        if (Context != null && _glContextAcquired)
        {
            try { Context.GraphicsContext.MakeNoneCurrent(); } catch { }
            _glContextAcquired = false;
        }
        Helpers.AppLogger.Info("libmpv：渲染线程已解绑 OpenGL 上下文");
    }

    private volatile bool _glContextAcquired;

    private void EnsureGlContext()
    {
        if (_glContextAcquired || Context == null) return;
        try
        {
            Context.GraphicsContext.MakeCurrent();
            _glContextAcquired = true;
            Helpers.AppLogger.Info("libmpv：渲染线程已获取 OpenGL 上下文");
        }
        catch { }
    }

    public override void Initialize()
    {
        if (Context != null) return;

        base.Initialize();
        Helpers.AppLogger.Info("libmpv：开始创建 RenderContext（D3D11 + OpenGL）");
        Context = new RenderContext(Setting);
        Helpers.AppLogger.Info("libmpv：RenderContext 创建完成");
        _swapChainPanel = new SwapChainPanel();
        _swapChainPanel.CompositionScaleChanged += OnCompositionScaleChanged;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = _swapChainPanel;

        RefreshSizeSnapshot();

        if (!TryLoadFrameBuffer())
        {
            Helpers.AppLogger.Info($"libmpv：帧缓冲待创建（当前尺寸 {ActualWidth:0}x{ActualHeight:0}，等待布局）");
        }
        else
        {
            Helpers.AppLogger.Info($"libmpv：帧缓冲创建成功（{FrameBuffer?.BufferWidth ?? 0}x{FrameBuffer?.BufferHeight ?? 0}）");
        }

        StartRenderLoop();
        Helpers.AppLogger.Info("libmpv：独立渲染线程已启动");
        Ready?.Invoke(this, EventArgs.Empty);
    }

    public override void Release()
    {
        base.Release();

        var fb = FrameBuffer;
        if (fb != null)
        {
            try { fb.Dispose(); } catch (Exception ex) { Helpers.AppLogger.Warning($"libmpv释放帧缓冲失败: {ex.Message}"); }
            FrameBuffer = null;
        }

        // 清理 pending（含已废弃未 dispose 的）
        var pf = _pendingNewFb;
        if (pf != null) { try { pf.Dispose(); } catch { } _pendingNewFb = null; }

        var dq = _disposeQueue;
        if (dq != null) { try { dq.Dispose(); } catch { } _disposeQueue = null; }

        var ctx = Context;
        if (ctx != null) { try { ctx.Dispose(); } catch { } Context = null; }
    }

    public int GetBufferHandle()
        => FrameBuffer?.GLFrameBufferHandle ?? 0;

    // ★★★ 核心：延迟切换策略 — 旧 FB 持续渲染直到新 FB 就绪 ★★★
    protected override void Draw()
    {
        // ── 步骤0：清理上一轮遗留的旧 FrameBuffer ──
        if (_disposeQueue != null)
        {
            var dead = _disposeQueue;
            _disposeQueue = null;
            try { dead.Dispose(); } catch (Exception ex) { Helpers.AppLogger.Warning($"libmpv释放旧帧缓冲失败: {ex.Message}"); }
        }

        // ── 步骤1：检查是否有已就绪的 pending 新帧缓冲 ──
        if (_pendingNewFb != null && _pendingFbBound)
        {
            // ★ 切换：旧 FB → disposeQueue，新 FB 上线
            _disposeQueue = FrameBuffer;
            FrameBuffer = _pendingNewFb;
            _pendingNewFb = null;
            _pendingFbBound = false;
            Helpers.AppLogger.Debug($"libmpv帧缓冲切换完成 {FrameBuffer.BufferWidth}x{FrameBuffer.BufferHeight}");
            // 继续执行本次渲染（新 FB 首次 Begin 会初始化 interop + depth）
        }

        // ── 步骤2：读取 UI 线程尺寸快照 ──
        double snapW = Volatile.Read(ref _snapshotWidth);
        double snapH = Volatile.Read(ref _snapshotHeight);
        if (snapW <= 0 || snapH <= 0) return;

        EnsureGlContext();
        if (!_glContextAcquired) return;

        double scaleX = Volatile.Read(ref _snapshotScaleX);
        double scaleY = Volatile.Read(ref _snapshotScaleY);
        int newPxW = (int)(snapW * scaleX);
        int newPxH = (int)(snapH * scaleY);
        if (newPxW <= 0 || newPxH <= 0) return;

        // ── 步骤3：尺寸变化检测 → 触发重建 ──
        bool sizeMatches = FrameBuffer != null &&
                           newPxW == FrameBuffer.BufferWidth &&
                           newPxH == FrameBuffer.BufferHeight;

        if (!sizeMatches)
        {
            // 如果有上一个 pending 还没绑定的，先废弃它
            if (_pendingNewFb != null)
            {
                var abandoned = _pendingNewFb;
                _pendingNewFb = null;
                _pendingFbBound = false;
                _disposeQueue = abandoned; // 下一帧 dispose
            }

            long nowMs = Environment.TickCount64;
            if (FrameBuffer != null && nowMs - _lastRebuildMs < RebuildCooldownMs)
            {
                // 防抖中：继续用旧 FB 正常渲染，不重建
            }
            else
            {
                _lastRebuildMs = nowMs;

                // ★ 创建新帧缓冲（GL 操作，渲染线程安全）
                FrameBuffer newFb;
                try
                {
                    newFb = new FrameBuffer(Context!, (int)snapW, (int)snapH, scaleX, scaleY);
                }
                catch (Exception ex)
                {
                    Helpers.AppLogger.Error(ex, "libmpv创建新帧缓冲失败");
                    // 旧 FB 仍有效，继续用旧的渲染
                    goto renderFrame;
                }

                // ★ 预填充黑色：在新交换链中写入一帧黑色，避免绑定后闪白/垃圾
                try { newFb.PreFillBlack(); } catch { }

                // ★ 存入 pending，排队 UI 线程绑定
                _pendingNewFb = newFb;
                var scHandle = newFb.SwapChainHandle;
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        _swapChainPanel!.As<ISwapChainPanelNative>().SetSwapChain(scHandle);
                        _pendingFbBound = true; // volatile，渲染线程下帧可见
                    }
                    catch (Exception ex)
                    {
                        Helpers.AppLogger.Warning($"libmpv绑定新交换链失败: {ex.Message}");
                    }
                });

                Helpers.AppLogger.Info(
                    $"libmpv帧缓冲待切换 {newFb.BufferWidth}x{newFb.BufferHeight}" +
                    (FrameBuffer != null ? $"（当前 {FrameBuffer.BufferWidth}x{FrameBuffer.BufferHeight}）" : ""));

                // 旧 FB 继续本帧正常渲染，不 return
            }
        }

    renderFrame:
        // ── 步骤4：正常渲染帧 ──
        var fb = FrameBuffer;
        if (fb == null) return;

        bool begun = false;
        try
        {
            if (fb.Begin())
            {
                begun = true;
                Render?.Invoke(_stopwatch.Elapsed - _lastFrameStamp);
            }
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warning($"libmpv渲染帧异常: {ex.Message}");
        }
        finally
        {
            if (begun)
            {
                try
                {
                    fb.End();
                    try { Present?.Invoke(); } catch { }
                }
                catch (Exception ex)
                {
                    Helpers.AppLogger.Warning($"libmpv End/Present异常: {ex.Message}");
                }
            }
        }
    }

    // ── UI 线程事件 ──

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshSizeSnapshot();
        if (Context != null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            if (!TryLoadFrameBuffer())
            {
                // 初始创建未完成，记录尺寸以便稍后创建
                Helpers.AppLogger.Debug(
                    $"libmpv：SizeChanged {e.NewSize.Width:0}x{e.NewSize.Height:0}（帧缓冲尚未创建）");
            }
        }
    }

    private void OnLayoutUpdated(object? sender, object e)
    {
        RefreshSizeSnapshot();
    }

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        RefreshSizeSnapshot();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Release();
    }

    // ── 尺寸快照 ──

    private void RefreshSizeSnapshot()
    {
        Volatile.Write(ref _snapshotWidth, ActualWidth);
        Volatile.Write(ref _snapshotHeight, ActualHeight);
        Volatile.Write(ref _snapshotScaleX, ScaleX);
        Volatile.Write(ref _snapshotScaleY, ScaleY);
    }

    // ── 初始创建 ──

    private bool TryLoadFrameBuffer()
    {
        if (FrameBuffer != null || Context == null) return false;
        if (ActualWidth <= 0 || ActualHeight <= 0) return false;

        FrameBuffer = new FrameBuffer(Context, (int)ActualWidth, (int)ActualHeight, ScaleX, ScaleY);
        _swapChainPanel!.As<ISwapChainPanelNative>().SetSwapChain(FrameBuffer.SwapChainHandle);
        return true;
    }
}
