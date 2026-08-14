using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using SightoHear.Controls;
using SightoHear.Helpers;
using SightoHear.Services;
using Windows.Foundation;

namespace SightoHear.ImageViewer
{
    /// <summary>切图滑动过渡方向：Next = 旧图向左滑出、新图从右滑入；Previous 相反；None = 无过渡。</summary>
    internal enum SlideDirection
    {
        None,
        Next,
        Previous
    }

    /// <summary>
    /// 图片查看器的 Win2D 渲染控制器（移植自 FlyPhotos.Display.Controllers.CanvasController，
    /// 精简为单级显示：直接安装已加载的 <see cref="CanvasBitmap"/>）。
    ///
    /// 线程模型——"一切触碰渲染状态的代码都跑在 W2D 线程；动作队列是唯一入口"。
    /// <see cref="IRenderSurface"/>（FreeRunCanvas）运行一条 Update/Draw 工作线程（"W2D 线程"）。
    /// <see cref="_renderer"/>、<see cref="_viewState"/>、<see cref="_viewManager"/> 均为 W2D 线程所有：
    /// 只会在 <see cref="Update"/> 顶部排空的队列动作里、或 Update/Draw 内部被触碰。
    /// UI 线程绝不直接触碰它们，而是通过 <see cref="_pump"/> 投递工作。渲染热路径因此无锁。
    ///
    /// 坐标单位：内部一律使用"像素"（CanvasUnits.Pixels + DPI 换算），
    /// 指针坐标从 UI 线程（DIP）换算到像素后再进入命中测试与平移增量。
    /// </summary>
    internal sealed class CanvasDisplayController : IDisposable
    {
        // --- 事件（已在 UI 线程派发，页面可直接订阅） ---
        /// <summary>缩放百分比变化（已取整）。</summary>
        public event Action<int>? OnZoomChanged;
        /// <summary>适应窗口状态变化。</summary>
        public event Action<bool>? OnFitToScreenStateChanged;
        /// <summary>100% 缩放状态变化。</summary>
        public event Action<bool>? OnOneToOneStateChanged;

        /// <summary>首次资源创建完成（W2D 线程）；页面在此后加载第一张图片。</summary>
        public event Action? DeviceReady;

        private readonly IRenderSurface _canvas;
        private readonly CanvasViewState _viewState = new();
        private readonly CanvasViewManager _viewManager;

        // 唯一的 UI → W2D 交接队列，在每次 Update 顶部排空
        private readonly ConcurrentQueue<Action> _pump = new();

        // W2D 线程所有
        private StaticImageRenderer? _renderer;

        // --- 切图双图滑动过渡状态（W2D 线程所有） ---
        // 切图时旧图不立即销毁，而是与新图同屏反向滑动（旧图滑出、新图滑入），过渡结束再释放旧图。
        private StaticImageRenderer? _slideOutRenderer;  // 正在滑出的旧图（延迟释放）
        private SlideDirection _slideDirection;
        private float _slideCanvasWidth;                // 过渡基准：画布像素宽（滑出/滑入的满幅距离）
        private float _slideOutOffsetX;                 // 旧图屏幕平移偏移（像素）
        private float _slideInOffsetX;                  // 新图屏幕平移偏移（像素）
        private float _slideOutScale;                   // 旧图切图前缩放（mip 层级选择用）
        private Rect _slideOutRect;                     // 旧图目标矩形（切图前 ImageRect）
        private Matrix3x2 _slideOutMat;                 // 旧图切图前完整矩阵（快照）
        private readonly System.Diagnostics.Stopwatch _slideStopwatch = new();

        /// <summary>滑动过渡时长（毫秒）。</summary>
        private const double SlideDurationMs = 350;

        // 已发布的 W2D → UI 命中测试快照：每帧 Update 写入，指针事件时 UI 线程读取。
        // 矩阵（6 个 float）与 Rect（4 个 double）无法原子写入，故用锁；指针事件远少于 60Hz Update，争用可忽略。
        private Matrix3x2 _hitTestMatInv = Matrix3x2.Identity;
        private Rect _hitTestImageRect;
        private readonly object _hitTestLock = new();

        private bool _isFirstPhoto = true;

        // 缩放百分比 UI 派发的合并状态（W2D 线程写，UI 线程读，用 Interlocked 协调）
        private int _zoomPercentPending;
        private int _zoomPercentDispatching;
        private int _lastDispatchedZoomPercent = -1;

        // ── Win2D HUD 性能采集（仅 W2D 线程访问）──
        private readonly Stopwatch _updateSw = new();
        private readonly Stopwatch _drawSw = new();
        private double _lastDrawMs;

        public CanvasDisplayController(IRenderSurface canvas)
        {
            _canvas = canvas;
            _viewManager = new CanvasViewManager(_viewState);

            _canvas.CreateResources += Canvas_CreateResources;
            _canvas.Update += Canvas_Update;
            _canvas.Draw += Canvas_Draw;
            _canvas.SizeChanged += Canvas_SizeChanged;

            // ViewManager 事件在 W2D 线程触发；此处是它们回到 UI 线程的唯一关口
            _viewManager.FitToScreenStateChanged += isFitted =>
                _canvas.DispatcherQueue.TryEnqueue(() => OnFitToScreenStateChanged?.Invoke(isFitted));
            _viewManager.OneToOneStateChanged += isOneToOne =>
                _canvas.DispatcherQueue.TryEnqueue(() => OnOneToOneStateChanged?.Invoke(isOneToOne));
            _viewManager.ZoomChanged += RequestZoomUpdate;
            _viewManager.ViewChanged += Wake;
        }

        // --- 图片安装 ---

        /// <summary>
        /// 安装一张新图片（UI 线程调用；bitmap 由调用方用共享设备加载完成）。
        /// 会在 W2D 线程上替换渲染器并重置视图为默认（适应窗口）。
        /// <paramref name="imageSize"/> 为图片像素尺寸，<paramref name="slide"/> 为切图滑动过渡方向
        /// （Next/Previous 时旧图保留并反向滑出、新图从侧外滑入；None 时旧图直接释放）。
        /// </summary>
        public void SetSource(CanvasBitmap bitmap, Size imageSize, int rotation, SlideDirection slide = SlideDirection.None)
        {
            _pump.Enqueue(() =>
            {
                var old = _renderer;
                _renderer = new StaticImageRenderer(_canvas, bitmap, Wake);

                if (slide != SlideDirection.None && old != null)
                {
                    // 双图滑动过渡：保留旧图用于滑出（延迟到过渡结束释放），快照旧图的显示状态
                    _slideOutRenderer?.Dispose();          // 丢弃上一次未完成的过渡旧图
                    _slideOutRenderer = old;
                    _slideDirection = slide;
                    _slideCanvasWidth = (float)GetCanvasSizePx().Width;
                    _slideOutScale = _viewState.Scale;
                    _slideOutRect = _viewState.ImageRect;
                    _slideOutMat = _viewState.Mat;
                    _slideOutOffsetX = 0;                  // 旧图从原位开始滑出
                    _slideStopwatch.Restart();
                    // 新图滑入偏移从侧外开始（SetNewPhoto 已把新图视图设为 fit 中心）
                    _slideInOffsetX = slide == SlideDirection.Next ? _slideCanvasWidth : -_slideCanvasWidth;
                }
                else
                {
                    old?.Dispose();
                    _slideOutRenderer = null;
                    _slideInOffsetX = 0;
                }

                _viewManager.SetNewPhoto(imageSize, rotation, GetCanvasSizePx(), _isFirstPhoto);
                _isFirstPhoto = false;
            });
            Wake(); // 渲染循环可能已暂停，需唤醒以排空安装动作
        }

        // --- 交互 API（均在 UI 线程调用，内部投递到 W2D 线程） ---

        // dipAnchor 参数保留仅为兼容页面调用；实际锚点由视图管理器统一为"图片中心"（原地缩放）
        public void ZoomAtPoint(ZoomDirection direction, Point dipAnchor) =>
            EnqueueViewAction(v => v.ZoomAtPoint(direction, ToPixels(dipAnchor)));

        public void ZoomAtPointPrecision(int delta, Point dipAnchor) =>
            EnqueueViewAction(v => v.ZoomAtPointPrecision(delta, ToPixels(dipAnchor)));

        public void ZoomByKeyboard(ZoomDirection direction, Point? dipAnchor = null)
        {
            var canvasSize = GetCanvasSizePx();
            EnqueueViewAction(v =>
            {
                if (dipAnchor.HasValue)
                    v.ZoomAtPoint(direction, ToPixels(dipAnchor.Value));
                else
                    v.ZoomAtCenter(direction, canvasSize);
            });
        }

        public void StepZoom(ZoomDirection direction, Point? dipAnchor = null)
        {
            var canvasSize = GetCanvasSizePx();
            EnqueueViewAction(v => v.StepZoom(direction, canvasSize, dipAnchor.HasValue ? ToPixels(dipAnchor.Value) : null));
        }

        public void ZoomToHundred() => EnqueueViewAction(v => v.ZoomToHundred(GetCanvasSizePx()));

        public void ZoomToHundred(Point dipAnchor) =>
            EnqueueViewAction(v => v.ZoomToHundred(GetCanvasSizePx(), ToPixels(dipAnchor)));

        public void FitToScreen(bool animateChange) =>
            EnqueueViewAction(v => v.ZoomPanToFit(animateChange,
                new Size(_viewState.ImageRect.Width, _viewState.ImageRect.Height), GetCanvasSizePx()));

        /// <summary>拖动平移（delta 为 DIP，内部换算像素后直接生效，跟手无动画）。</summary>
        public void Pan(double dxDip, double dyDip)
        {
            var scale = _canvas.DpiScale;
            EnqueueViewAction(v => v.Pan(dxDip * scale, dyDip * scale));
        }

        public void RotateBy90(bool clockwise) =>
            EnqueueViewAction(v => v.RotateBy(clockwise ? 90 : -90, GetCanvasSizePx()));

        public void Shrug() => EnqueueViewAction(v => v.Shrug());

        /// <summary>
        /// 判断 DIP 坐标是否落在图片上（命中测试）：逆矩阵变换后检查是否在图片矩形内。
        /// 在 UI 线程调用，读取每帧发布的快照。
        /// </summary>
        public bool IsPressedOnImage(Point dipPos)
        {
            Matrix3x2 matInv;
            Rect imageRect;
            lock (_hitTestLock)
            {
                matInv = _hitTestMatInv;
                imageRect = _hitTestImageRect;
            }
            var px = ToPixels(dipPos);
            var tp = Vector2.Transform(new Vector2((float)px.X, (float)px.Y), matInv);
            return tp.X >= imageRect.X && tp.Y >= imageRect.Y
                                   && tp.X <= imageRect.Right && tp.Y <= imageRect.Bottom;
        }

        // --- Win2D 事件循环 ---

        private void Canvas_CreateResources(IRenderSurface sender, FreeRunCreateResourcesEventArgs args)
        {
            try
            {
                // 设备（重新）创建：设备丢失重建时，旧 GPU 资源（位图/mip）已随旧设备失效，
                // 必须立即释放，避免后续 Draw 使用已失效资源触发 COMException（间歇性崩溃）。
                // 首次触发时 _renderer 尚为 null，此清理是空操作。
                _renderer?.Dispose();
                _renderer = null;

                // 首次触发通知页面加载第一张图；设备重建时由页面通过 DeviceReady 重新加载当前图片
                DeviceReady?.Invoke();
            }
            catch (Exception ex)
            {
                // W2D 线程异常不经过托管全局处理器，不捕获将直接 fail-fast（0xc000027b），必须就地记录
                AppLogger.Error(ex, "W2D CreateResources 异常");
            }
        }

        private void Canvas_Update(IRenderSurface sender, FreeRunUpdateEventArgs args)
        {
            _updateSw.Restart();
            try
            {
                // ① 排空所有 UI 线程投递的请求（一切视图/渲染器变更都发生在这里）
                while (_pump.TryDequeue(out var action))
                    action();

                // ② 推进平移/缩放动画
                _viewManager.OnUpdate();

                // ②½ 推进切图滑动过渡（旧图滑出 / 新图滑入）
                AdvanceSlideTransition();

                // ③ 发布命中测试快照
                lock (_hitTestLock)
                {
                    _hitTestMatInv = _viewState.MatInv;
                    _hitTestImageRect = _viewState.ImageRect;
                }

                // ④ 无事可做时暂停渲染循环；暂停后重新检查队列，封堵"入队/暂停"丢失唤醒的竞态
                if (_pump.IsEmpty && !_viewManager.PanZoomAnimationOnGoing && _slideOutRenderer == null)
                {
                    _canvas.Paused = true;
                    if (!_pump.IsEmpty) // 暂停决定期间有动作入队：立即恢复
                        _canvas.Paused = false;
                }
            }
            catch (Exception ex)
            {
                // W2D 线程异常直接 fail-fast（0xc000027b），必须就地记录
                AppLogger.Error(ex, "W2D Update 异常");
            }
            finally
            {
                // ⑤ 性能采集：上报画布尺寸与帧数据（帧时长 / Update 耗时 / 上帧 Draw 耗时）
                // 帧时长用 FreeRunCanvas 的真实时钟（Present 间隔），绝不伪造
                _updateSw.Stop();
                Win2DPerformanceHud.ReportSurface(
                    _canvas.Size.Width, _canvas.Size.Height, _canvas.DpiScale);
                Win2DPerformanceHud.ReportFrame(
                    args.ElapsedTime.TotalMilliseconds,
                    _updateSw.Elapsed.TotalMilliseconds,
                    _lastDrawMs);
            }
        }

        private void Canvas_Draw(IRenderSurface sender, FreeRunDrawEventArgs args)
        {
            _drawSw.Restart();
            try
            {
                // ★ 画布背景固定为不透明深色（#101010，与页面背景一致），永久深色、不随主题变化。
                // 不能清为 Transparent：Win2D 交换链不支持真正的透明合成，
                // 浅色主题下透明像素会透出/显示为白色背景，非常晃眼（参考 MusicPlayerPage 的固定做法）。
                args.DrawingSession.Clear(Windows.UI.Color.FromArgb(255, 16, 16, 16));

                var renderer = _renderer;
                if (renderer == null) return;

                // 动画中或刚结束用高质量插值，静止时线性（mip 已保证缩小时质量）
                var isAnimating = _viewManager.PanZoomAnimationOnGoing || _slideOutRenderer != null;
                var quality = isAnimating
                    ? CanvasImageInterpolation.HighQualityCubic
                    : CanvasImageInterpolation.Linear;

                if (_slideOutRenderer != null)
                {
                    // 切图滑动过渡：旧图（快照矩阵 + 滑出偏移）与新图（当前矩阵 + 滑入偏移）同屏绘制
                    var oldMat = _slideOutMat;
                    oldMat.M31 += _slideOutOffsetX;
                    args.DrawingSession.Transform = oldMat;
                    _slideOutRenderer.Draw(args.DrawingSession, _slideOutRect, _slideOutScale, quality);

                    var newMat = _viewState.Mat;
                    newMat.M31 += _slideInOffsetX;
                    args.DrawingSession.Transform = newMat;
                    renderer.Draw(args.DrawingSession, _viewState.ImageRect, _viewState.Scale, quality);
                }
                else
                {
                    args.DrawingSession.Transform = _viewState.Mat;
                    renderer.Draw(args.DrawingSession, _viewState, quality);
                }
            }
            catch (Exception ex)
            {
                // W2D 线程异常直接 fail-fast（0xc000027b），必须就地记录
                AppLogger.Error(ex, "W2D Draw 异常");
            }
            finally
            {
                // 性能采集：记录本帧 Draw 耗时，供下一帧 Update 一并上报
                _drawSw.Stop();
                _lastDrawMs = _drawSw.Elapsed.TotalMilliseconds;
            }
        }

        /// <summary>
        /// 推进切图滑动过渡：旧图反向滑出、新图从侧外滑入（同一缓动，视觉上两图同步平移）。
        /// 过渡结束释放旧图渲染器并清零偏移。
        /// </summary>
        private void AdvanceSlideTransition()
        {
            if (_slideOutRenderer == null) return;

            var t = Math.Min(_slideStopwatch.Elapsed.TotalMilliseconds / SlideDurationMs, 1.0);
            var eased = SmoothStep(t); // 缓入缓出：起步柔和、落定自然
            var dir = _slideDirection == SlideDirection.Next ? 1f : -1f;
            _slideOutOffsetX = -dir * _slideCanvasWidth * (float)eased;   // 旧图向滑入方向的相反侧退出
            _slideInOffsetX = dir * _slideCanvasWidth * (1f - (float)eased); // 新图从侧外进入中心

            if (t >= 1.0)
            {
                // 过渡完成：释放旧图，恢复正常单图绘制
                _slideOutRenderer.Dispose();
                _slideOutRenderer = null;
                _slideOutOffsetX = 0;
                _slideInOffsetX = 0;
            }
        }

        /// <summary>smoothstep 缓动：0→1 缓入缓出，两端无速度突变。</summary>
        private static double SmoothStep(double t) => t * t * (3.0 - 2.0 * t);

        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs args)
        {
            var newPx = new Size(args.NewSize.Width * _canvas.DpiScale, args.NewSize.Height * _canvas.DpiScale);
            var oldPx = new Size(args.PreviousSize.Width * _canvas.DpiScale, args.PreviousSize.Height * _canvas.DpiScale);
            EnqueueViewAction(v => v.HandleSizeChange(newPx, oldPx));
        }

        // --- 工具 ---

        /// <summary>唤醒渲染循环（任意线程可调用）。</summary>
        private void Wake() => _canvas.Paused = false;

        /// <summary>投递一个视图管理器变更，带"渲染器为空即无操作"的标准守卫。</summary>
        private void EnqueueViewAction(Action<CanvasViewManager> apply)
        {
            _pump.Enqueue(() =>
            {
                if (_renderer == null) return;
                apply(_viewManager);
            });
            Wake();
        }

        /// <summary>画布像素尺寸（像素单位下的一切数学基准）。</summary>
        private Size GetCanvasSizePx() => new(_canvas.Size.Width * _canvas.DpiScale, _canvas.Size.Height * _canvas.DpiScale);

        /// <summary>把 DIP 坐标换算为像素坐标。</summary>
        private Point ToPixels(Point dip) => new(dip.X * _canvas.DpiScale, dip.Y * _canvas.DpiScale);

        /// <summary>缩放百分比 UI 派发（合并：最新值胜出，整数不变不派发）。</summary>
        private void RequestZoomUpdate()
        {
            var newZoom = (int)Math.Round(_viewState.Scale * 100);
            if (newZoom == _lastDispatchedZoomPercent) return;
            _lastDispatchedZoomPercent = newZoom;
            Volatile.Write(ref _zoomPercentPending, newZoom);
            if (Interlocked.CompareExchange(ref _zoomPercentDispatching, 1, 0) == 0)
                _canvas.DispatcherQueue.TryEnqueue(() =>
                {
                    Volatile.Write(ref _zoomPercentDispatching, 0);
                    OnZoomChanged?.Invoke(Volatile.Read(ref _zoomPercentPending));
                });
        }

        public void Dispose()
        {
            try
            {
                // 先停并合入 Win2D 工作线程再释放 GPU 资源，避免飞行中的 Update/Draw 观察到已释放的渲染器
                _canvas.RemoveFromVisualTree();
                _canvas.CreateResources -= Canvas_CreateResources;
                _canvas.Update -= Canvas_Update;
                _canvas.Draw -= Canvas_Draw;
                _canvas.SizeChanged -= Canvas_SizeChanged;

                _viewManager.Dispose();
                _renderer?.Dispose();
                _renderer = null;
                _slideOutRenderer?.Dispose();
                _slideOutRenderer = null;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "CanvasDisplayController 释放失败");
            }
        }
    }
}
