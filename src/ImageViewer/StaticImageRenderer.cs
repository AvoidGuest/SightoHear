using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using SightoHear.Controls;
using SightoHear.Helpers;
using Windows.Foundation;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 静态图片渲染器（移植自 FlyPhotos.Display.ImageRendering.StaticImageRenderer，去掉设置项依赖）。
    /// 持有源 <see cref="CanvasBitmap"/> 并为其生成 mipmap 金字塔（0.5×/0.25×/0.125×…）。
    /// 缩放缩小时自动选用"尺寸仍不小于显示尺寸"的最小 mip 层，避免逐像素采样闪烁，动画平滑。
    /// </summary>
    internal sealed class StaticImageRenderer : IDisposable
    {
        private readonly CanvasBitmap _sourceBitmap;
        private readonly Action _invalidateCanvas;
        private readonly bool _generateMipChain;

        // Mipmap 金字塔。索引 0 = 0.5× 源，1 = 0.25×，2 = 0.125×，…
        // _sourceBitmap 是隐式第 0 层，不存于此数组。
        // _mipChain 与 _mipGenCts 在 _mipChainLock 下交换/取消；Draw 也在锁内选取层级。
        private CanvasRenderTarget[] _mipChain = [];
        private readonly object _mipChainLock = new();
        private CancellationTokenSource? _mipGenCts;
        private volatile bool _mipChainReady;

        public StaticImageRenderer(IRenderSurface surface, CanvasBitmap sourceBitmap, Action invalidateCanvas, bool generateMipChain = true)
        {
            _sourceBitmap = sourceBitmap;
            _invalidateCanvas = invalidateCanvas;
            _generateMipChain = generateMipChain;

            if (_generateMipChain)
            {
                // 设备在调用线程（W2D）读取后传入后台任务，避免后台线程触碰控件属性
                KickOffMipGeneration(surface.Device);
            }
        }

        private void KickOffMipGeneration(CanvasDevice device)
        {
            _mipGenCts = new CancellationTokenSource();
            var token = _mipGenCts.Token;
            _ = Task.Run(() => GenerateMipChain(device, token), token);
        }

        private void GenerateMipChain(CanvasDevice device, CancellationToken token)
        {
            const int MaxLevels = 5;
            const float MinDimension = 64f;

            try
            {
                // 逐层减半生成，直到尺寸低于最小阈值或达到最大层数
                var mips = new List<CanvasRenderTarget>(MaxLevels);
                CanvasBitmap prevLevel = _sourceBitmap;
                float prevWidth = (float)_sourceBitmap.Bounds.Width;
                float prevHeight = (float)_sourceBitmap.Bounds.Height;

                for (var i = 0; i < MaxLevels; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var halfW = prevWidth / 2f;
                    var halfH = prevHeight / 2f;
                    if (halfW < MinDimension || halfH < MinDimension) break;

                    var mip = new CanvasRenderTarget(device, halfW, halfH, 96);
                    using (var ds = mip.CreateDrawingSession())
                    {
                        ds.Clear(Microsoft.UI.Colors.Transparent);
                        ds.DrawImage(prevLevel, new Rect(0, 0, halfW, halfH),
                            new Rect(0, 0, prevWidth, prevHeight), 1f,
                            CanvasImageInterpolation.HighQualityCubic);
                    }

                    if (token.IsCancellationRequested)
                    {
                        mip.Dispose();
                        break;
                    }

                    mips.Add(mip);
                    prevLevel = mip;
                    prevWidth = halfW;
                    prevHeight = halfH;
                }

                if (token.IsCancellationRequested)
                {
                    foreach (var m in mips) m.Dispose();
                    return;
                }

                lock (_mipChainLock)
                {
                    if (token.IsCancellationRequested)
                    {
                        foreach (var m in mips) m.Dispose();
                        return;
                    }
                    _mipChain = [.. mips];
                    _mipChainReady = true;
                }
                _invalidateCanvas();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Warning($"Mip 链生成失败，回退到源位图: {ex.Message}");
            }
        }

        /// <summary>
        /// 绘制当前帧。<paramref name="session"/> 已由调用方设置像素单位与变换矩阵。
        /// </summary>
        public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality)
        {
            Draw(session, viewState.ImageRect, viewState.Scale, quality);
        }

        /// <summary>
        /// 绘制到指定目标矩形（像素）。<paramref name="scale"/> 用于 mip 层级选择；
        /// <paramref name="session"/> 的变换矩阵由调用方设置。供双图滑出过渡绘制旧图/新图使用。
        /// </summary>
        public void Draw(CanvasDrawingSession session, Rect destRect, float scale, CanvasImageInterpolation quality)
        {
            session.Units = CanvasUnits.Pixels;
            session.Antialiasing = CanvasAntialiasing.Antialiased;

            CanvasBitmap src;
            lock (_mipChainLock)
                src = SelectBitmapForScale(scale);
            session.DrawImage(src, destRect, src.Bounds, 1f, quality);
        }

        /// <summary>
        /// 选取像素尺寸仍 ≥ 显示尺寸的最小 mip 层——保证总是从所选层向下采样（绝不向上放大）。
        /// CanvasRenderTarget 继承 CanvasBitmap，因此源与 mip 共用返回类型。须在 _mipChainLock 内调用。
        /// </summary>
        private CanvasBitmap SelectBitmapForScale(float scale)
        {
            if (!_mipChainReady || _mipChain.Length == 0 || scale >= 1f)
                return _sourceBitmap;

            // k = floor(log2(1/scale))：scale≥1 时 0，scale<0.5 时 1，scale<0.25 时 2，…
            var k = (int)Math.Floor(Math.Log2(1.0 / scale));
            k = Math.Clamp(k, 0, _mipChain.Length);
            return k == 0 ? _sourceBitmap : _mipChain[k - 1];
        }

        public void Dispose()
        {
            lock (_mipChainLock)
            {
                _mipGenCts?.Cancel();
                _mipGenCts?.Dispose();
                _mipGenCts = null;
                foreach (var mip in _mipChain) mip?.Dispose();
                _mipChain = [];
                _mipChainReady = false;
            }
            // 源位图由本渲染器持有生命周期，随渲染器一并释放
            _sourceBitmap.Dispose();
        }
    }
}
