using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SightoHear.Helpers;
using System;
using System.Diagnostics;
using System.Threading;
using Windows.Foundation;
using Windows.UI;

namespace SightoHear.Controls
{
    /// <summary>
    /// 无垂直同步（VSync）限制的 Win2D 渲染控件，实现 <see cref="IRenderSurface"/>。
    ///
    /// 内部结构：CanvasSwapChainPanel + CanvasSwapChain + 自建紧密渲染线程循环。
    /// 每次循环：触发 Update（真实时钟计时）→ CreateDrawingSession 触发 Draw → Present(0)
    /// （syncInterval=0 不等待 vsync）→ 绝不调用 WaitForVerticalBlank()。
    ///
    /// 因此"跟随系统 / 默认 GPU"（连接显示器、有垂直消隐可等的实例）也能跑满帧率，
    /// 与 GPU 实例数量无关，适用于所有电脑。代价是 GPU 满载（用户已知情）。
    ///
    /// 生命周期：
    /// - Loaded 启动渲染线程；Unloaded / RemoveFromVisualTree 停止线程并释放交换链。
    /// - 尺寸 / DPI 变化自动 ResizeBuffers；设备丢失自动重建并重新触发 CreateResources。
    /// - Paused=true 时渲染线程休眠等待（Monitor.Wait），不空转 CPU。
    /// </summary>
    public sealed partial class FreeRunCanvas : UserControl, IRenderSurface
    {
        private readonly CanvasSwapChainPanel _panel = new();
        private CanvasSwapChain? _swapChain;
        private CanvasDevice? _device;
        private Thread? _renderThread;
        private volatile bool _running;
        private volatile bool _paused;
        private volatile bool _swapChainReady;
        private readonly object _pauseLock = new();
        // 交换链创建/挂接在 UI 线程执行，渲染线程用此门等待完成（避免在后台线程操作 XAML 面板）
        private readonly ManualResetEventSlim _swapChainGate = new(false);

        // 以下字段由 UI 线程写入（布局/合成缩放事件），渲染线程读取。
        // _dpiScale 在 OnLoaded 与 OnPanelSizeChanged 中同步从 CompositionScaleX 刷新，
        // 确保渲染线程读取时始终为最新值，消除高 DPI 居中偏移竞态。
        private Size _dipSize;
        private float _dpiScale = 1f;

        // 渲染线程私有
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _lastElapsed = TimeSpan.Zero;
        private int _renderErrorLogged;   // 非设备丢失异常日志节流（Interlocked）

        private Color _clearColor = Color.FromArgb(255, 13, 11, 16);
        private volatile int _maxFps; // 最大帧率限制（0 表示不限制）

        /// <summary>CreateResources 事件（渲染线程触发：首次交换链就绪 + 设备丢失重建后）。</summary>
        public event FreeRunCreateResourcesHandler? CreateResources;
        /// <summary>Update 事件（渲染线程触发，ElapsedTime 为真实时钟）。</summary>
        public event FreeRunUpdateHandler? Update;
        /// <summary>Draw 事件（渲染线程触发，提供本帧绘制会话）。</summary>
        public event FreeRunDrawHandler? Draw;

        public FreeRunCanvas()
        {
            Content = _panel;
            _panel.SizeChanged += OnPanelSizeChanged;
            _panel.CompositionScaleChanged += OnCompositionScaleChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ── 公开配置 ──

        /// <summary>
        /// 自定义渲染设备（对应 CanvasAnimatedControl.CustomDevice）。须在控件加载前设置；
        /// 为 null 时使用进程共享默认设备（跟随系统 / 默认 GPU）。
        /// </summary>
        public CanvasDevice? CustomDevice { get; set; }

        /// <summary>每帧清屏颜色（与背景一致，Win2D 交换链不支持真正透明合成）。</summary>
        public Color ClearColor
        {
            get => _clearColor;
            set => _clearColor = value;
        }

        /// <summary>
        /// 最大帧率限制（帧/秒）。设置为 0 表示不限制（默认值）。
        /// 当设置为正值时，渲染循环会控制帧间隔不超过 1/MaxFps 秒。
        /// 例如：设置为 1000 表示最大帧率为 1000 帧/秒。
        /// </summary>
        public int MaxFps
        {
            get => _maxFps;
            set => _maxFps = Math.Max(0, value); // 确保值非负
        }

        // ── IRenderSurface / ICanvasResourceCreatorWithDpi 实现 ──

        /// <summary>当前尺寸（DIP）。</summary>
        public Size Size => _dipSize;

        /// <summary>DPI 缩放（物理像素 / DIP）。</summary>
        public float DpiScale => _dpiScale;

        /// <summary>渲染管线就绪（交换链已创建且尺寸有效）。</summary>
        public bool ReadyToDraw => _swapChainReady;

        /// <summary>底层交换链。</summary>
        public CanvasSwapChain? SwapChain => _swapChain;

        /// <summary>DPI（每英寸像素 = 96 × DpiScale）。</summary>
        public float Dpi => 96f * _dpiScale;

        /// <summary>渲染设备（首次访问时惰性创建）。</summary>
        public CanvasDevice Device => _device ??= GetEffectiveDevice();

        /// <summary>是否暂停渲染循环（true 时渲染线程休眠，不空转 CPU）。</summary>
        public bool Paused
        {
            get => _paused;
            set
            {
                if (_paused == value) return;
                _paused = value;
                if (!value)
                {
                    // 唤醒渲染线程（循环内会重置计时，避免 elapsed 大跳变）
                    lock (_pauseLock)
                        Monitor.PulseAll(_pauseLock);
                }
            }
        }

        /// <summary>停止渲染线程并释放交换链（页面卸载 / 移除控件时调用）。</summary>
        public void RemoveFromVisualTree() => StopRenderLoop();

        /// <summary>DIP → 像素（按 DpiRounding 取整）。</summary>
        public int ConvertDipsToPixels(float dips, CanvasDpiRounding dpiRounding) =>
            ConvertDipToPixel(dips, dpiRounding);

        /// <summary>像素 → DIP。</summary>
        public float ConvertPixelsToDips(int pixels) => pixels / _dpiScale;

        private int ConvertDipToPixel(float dips, CanvasDpiRounding rounding)
        {
            float px = dips * _dpiScale;
            return rounding switch
            {
                CanvasDpiRounding.Floor => (int)MathF.Floor(px),
                CanvasDpiRounding.Round => (int)MathF.Round(px),
                CanvasDpiRounding.Ceiling => (int)MathF.Ceiling(px),
                _ => (int)px
            };
        }

        // ── 布局 / 合成缩放事件（UI 线程） ──

        private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _dipSize = new Size(e.NewSize.Width, e.NewSize.Height);
            // 同步刷新 DPI 缩放：CompositionScaleChanged 事件可能尚未触发，
            // 但 CompositionScaleX 属性已是最新值。此处同步可消除高 DPI 下
            // SizeChanged 先于 CompositionScaleChanged 导致的居中偏移竞态。
            _dpiScale = (float)_panel.CompositionScaleX;
        }

        private void OnCompositionScaleChanged(Microsoft.UI.Xaml.Controls.SwapChainPanel sender, object args)
        {
            _dpiScale = (float)_panel.CompositionScaleX;
        }

        // ── 生命周期 ──

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 预初始化 DPI 缩放：CompositionScaleChanged 可能在 Loaded 之后才触发，
            // 此处确保渲染线程启动前 _dpiScale 已反映真实 DPI，避免首帧居中偏移。
            _dpiScale = (float)_panel.CompositionScaleX;
            if (_renderThread != null) return;
            _running = true;
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "FreeRunCanvas.RenderLoop"
            };
            _renderThread.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => StopRenderLoop();

        private void StopRenderLoop()
        {
            if (!_running) return;
            _running = false;
            _swapChainGate.Set(); // 放行可能阻塞在交换链挂接等待中的渲染线程，使其尽快退出
            lock (_pauseLock)
                Monitor.PulseAll(_pauseLock); // 唤醒可能休眠中的渲染线程，使其退出
            _renderThread?.Join(2000);
            _renderThread = null;

            // 释放 GPU 资源
            try
            {
                _swapChain?.Dispose();
            }
            catch { }
            _swapChain = null;
            _panel.SwapChain = null;
            _swapChainReady = false;

            // ★ 修复：释放同步原语（内核句柄），避免每次创建/销毁 FreeRunCanvas 时累积句柄泄漏
            try { _swapChainGate?.Dispose(); } catch { }
        }

        // ── 渲染循环（专用线程，紧密循环） ──

        private void RenderLoop()
        {
            _clock.Restart();
            _lastElapsed = TimeSpan.Zero;

            while (_running)
            {
                // 暂停：休眠等待唤醒（避免 busy-wait 空转 CPU）
                if (_paused)
                {
                    lock (_pauseLock)
                    {
                        while (_running && _paused)
                            Monitor.Wait(_pauseLock);
                    }
                    if (!_running) break;
                    // 唤醒后重置计时，避免恢复瞬间 elapsed 大跳变导致动画/流体时间骤进
                    _clock.Restart();
                    _lastElapsed = TimeSpan.Zero;
                }

                try
                {
                    if (!_swapChainReady)
                    {
                        EnsureSwapChain();
                        if (!_swapChainReady)
                        {
                            Thread.Sleep(5); // 尚未布局/设备未就绪：稍后重试
                            continue;
                        }
                    }

                    HandleResizeIfNeeded();

                    // 真实时钟计时：Present 间隔（帧率统计以此为准，绝不伪造）
                    TimeSpan now = _clock.Elapsed;
                    TimeSpan elapsed = now - _lastElapsed;
                    _lastElapsed = now;

                    Update?.Invoke(this, new FreeRunUpdateEventArgs { ElapsedTime = elapsed, TotalTime = now });

                    using (CanvasDrawingSession ds = _swapChain!.CreateDrawingSession(_clearColor))
                    {
                        Draw?.Invoke(this, new FreeRunDrawEventArgs { DrawingSession = ds });
                    }
                    _swapChain.Present(0); // syncInterval=0：不等待垂直同步；绝不调用 WaitForVerticalBlank()

                    // 帧率限制：如果设置了最大帧率，确保帧间隔不超过目标时间
                    if (_maxFps > 0)
                    {
                        double targetFrameTimeMs = 1000.0 / _maxFps; // 目标帧时间（毫秒）
                        double frameElapsedMs = _clock.Elapsed.TotalMilliseconds - now.TotalMilliseconds;
                        if (frameElapsedMs < targetFrameTimeMs)
                        {
                            double sleepTimeMs = targetFrameTimeMs - frameElapsedMs;
                            // 两级等待策略：
                            // 1. Thread.Sleep 释放 CPU 时间片（但精度约 15ms，仅用于较长时间）
                            // 2. Stopwatch 忙等待补齐剩余时间（高精度，不依赖系统计时器分辨率）
                            if (sleepTimeMs > 4.0)
                            {
                                Thread.Sleep((int)(sleepTimeMs - 2)); // 提前 2ms 醒来，用忙等待补齐
                            }
                            // 忙等待：直接轮询 Stopwatch，不使用 SpinWait（避免提前 Yield）
                            while (_clock.Elapsed.TotalMilliseconds - now.TotalMilliseconds < targetFrameTimeMs)
                            {
                                // 纯忙等待，保持 CPU 自旋以获取最高计时精度
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleRenderError(ex);
                }
            }
        }

        private void EnsureSwapChain()
        {
            if (_dipSize.Width <= 0 || _dipSize.Height <= 0)
                return;
            if (_swapChainReady)
                return;

            // 关键线程规则：CanvasSwapChainPanel 是 XAML 元素，其 SwapChain 赋值必须在 UI 线程；
            // 交换链创建也一并放 UI 线程，与官方 CanvasAnimatedControl.CreateResources 的语义一致
            //（订阅方原本就在 UI 线程收到 CreateResources）。渲染线程仅做 Update/Draw/Present。
            _swapChainGate.Reset();
            bool posted = DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_swapChainReady)
                        return;
                    // 此刻布局可能已更新，以最新尺寸创建
                    if (_dipSize.Width <= 0 || _dipSize.Height <= 0)
                        return;

                    _swapChain?.Dispose();
                    // DPI 取自 this（ICanvasResourceCreatorWithDpi），保证交换链 DPI 与控件一致
                    var sc = new CanvasSwapChain(this, _dipSize);
                    _panel.SwapChain = sc;
                    _swapChain = sc;
                    _swapChainReady = true;

                    // 设备（首次/重建）就绪：通知订阅方重新创建 GPU 资源
                    CreateResources?.Invoke(this, new FreeRunCreateResourcesEventArgs());
                }
                catch (Exception ex)
                {
                    // 不置 ready，下一轮循环重试
                    AppLogger.Error(ex, "FreeRunCanvas 在 UI 线程创建/挂接交换链失败");
                }
                finally
                {
                    _swapChainGate.Set();
                }
            });

            if (posted)
                _swapChainGate.Wait(3000); // 等待 UI 线程完成挂接；超时则下一轮循环重试
        }

        private void HandleResizeIfNeeded()
        {
            var sc = _swapChain;
            if (sc == null || _dipSize.Width <= 0 || _dipSize.Height <= 0)
                return;

            // 尺寸（DIP）或 DPI 任一变化时重设缓冲；ResizeBuffers 尺寸以 DIP 为单位，
            // 3 参数重载可同时更新 DPI，避免仅靠 SizeInPixels 比较时漏掉 DPI 变化
            if (Math.Abs(sc.Size.Width - _dipSize.Width) > 0.5f ||
                Math.Abs(sc.Size.Height - _dipSize.Height) > 0.5f ||
                Math.Abs(sc.Dpi - Dpi) > 0.5f)
            {
                sc.ResizeBuffers((float)_dipSize.Width, (float)_dipSize.Height, Dpi);
            }
        }

        private void HandleRenderError(Exception ex)
        {
            var device = _device;
            bool deviceLost = device != null && device.IsDeviceLost();

            if (deviceLost)
            {
                // 设备丢失：释放失效交换链，下一轮循环自动重建并重新触发 CreateResources。
                // 解除面板对失效交换链的引用也须在 UI 线程（XAML 元素访问），且先置空再 Dispose。
                var old = _swapChain;
                _swapChain = null;
                _device = null;
                _swapChainReady = false;
                try
                {
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        _panel.SwapChain = null;
                        old?.Dispose();
                    });
                }
                catch { }
            }
            else if (ex is not ObjectDisposedException && Interlocked.CompareExchange(ref _renderErrorLogged, 1, 0) == 0)
            {
                // 仅记录首个非设备丢失异常，避免高频异常刷爆日志
                AppLogger.Error(ex, "FreeRunCanvas 渲染循环异常");
            }
            Thread.Sleep(5);
        }

        private CanvasDevice GetEffectiveDevice() => CustomDevice ?? CanvasDevice.GetSharedDevice();
    }
}
