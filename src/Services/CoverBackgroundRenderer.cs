using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using SightoHear.Controls;
using System;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace SightoHear.Services
{
    public sealed class CoverBackgroundRenderer : IDisposable
    {
        private const float BaseRotationSpeed = 0.6f;
        private const double CrossfadeSeconds = 0.7;

        private CanvasBitmap? _currentBitmap;
        private CanvasBitmap? _previousBitmap;
        private CanvasRenderTarget? _fallbackBitmap;
        private CanvasRenderTarget? _currentCache;
        private CanvasRenderTarget? _previousCache;
        private Size _lastSize;
        private float _rotationAngle;
        private double _crossfadeProgress = 1;
        private bool _isCrossfading;
        private bool _needsCacheUpdate = true;
        // ★ 缓存的渐变刷，避免每帧分配
        private CanvasLinearGradientBrush? _cachedVerticalShade;
        private CanvasRadialGradientBrush? _cachedSideShade;

        public bool IsEnabled { get; set; }
        public int Opacity { get; set; } = 100;
        public int BlurAmount { get; set; } = 100;
        public int Speed { get; set; } = 50;

        public void SetCoverBitmap(CanvasBitmap? bitmap)
        {
            if (_currentBitmap == bitmap)
                return;

            // ★ 修复：若上一轮 crossfade 的 previous 位图仍在（切歌过快尚未释放），
            //   先释放它，避免 CanvasBitmap（GPU 纹理）被静默覆盖而泄漏——
            //   否则连续快速切歌会持续泄漏显存，导致浏览多页后 Win2D 渲染卡顿。
            if (_previousBitmap != null && _previousBitmap != _currentBitmap)
                ReleasePrevious();

            _previousBitmap = _currentBitmap;
            _previousCache = _currentCache;
            _currentBitmap = bitmap;
            _currentCache = null;
            _needsCacheUpdate = true;

            if (_previousBitmap != null && _currentBitmap != null)
            {
                _crossfadeProgress = 0;
                _isCrossfading = true;
            }
            else
            {
                _crossfadeProgress = 1;
                _isCrossfading = false;
                if (_previousBitmap != null && _currentBitmap == null)
                    ReleasePrevious();
            }
        }

        public void Update(IRenderSurface surface, TimeSpan elapsedTime)
        {
            if (_lastSize != surface.Size)
            {
                _lastSize = surface.Size;
                _needsCacheUpdate = true;
            }

            if (Speed > 0)
            {
                _rotationAngle += BaseRotationSpeed * (Speed / 100f) * (float)elapsedTime.TotalSeconds;
                _rotationAngle %= MathF.PI * 2;
            }

            if (_isCrossfading)
            {
                _crossfadeProgress = Math.Clamp(
                    _crossfadeProgress + elapsedTime.TotalSeconds / CrossfadeSeconds,
                    0,
                    1);

                if (_crossfadeProgress >= 1)
                {
                    _isCrossfading = false;
                    ReleasePrevious();
                }
            }
        }

        public void Draw(IRenderSurface surface, CanvasDrawingSession drawingSession)
        {
            if (!IsEnabled || Opacity <= 0)
                return;

            if (_lastSize != surface.Size)
            {
                _lastSize = surface.Size;
                _needsCacheUpdate = true;
            }

            float width = Math.Max(1, (float)surface.Size.Width);
            float height = Math.Max(1, (float)surface.Size.Height);

            CanvasBitmap? source = _currentBitmap ?? EnsureFallbackBitmap(surface);
            EnsureCachedLayer(surface, source, ref _currentCache);

            Vector2 center = new(width / 2f, height / 2f);
            float fade = SmoothStep((float)_crossfadeProgress);

            if (_isCrossfading && _previousCache != null)
                DrawCachedLayer(drawingSession, _previousCache, center, _rotationAngle, Opacity / 100f);

            if (_currentCache != null)
                DrawCachedLayer(drawingSession, _currentCache, center, _rotationAngle, fade * Opacity / 100f);

            DrawDepthOverlays(surface, drawingSession, width, height);
        }

        private void EnsureCachedLayer(
            ICanvasResourceCreator resourceCreator,
            CanvasBitmap? source,
            ref CanvasRenderTarget? cache)
        {
            if (source == null)
            {
                cache?.Dispose();
                cache = null;
                return;
            }

            bool deviceMismatch = cache != null && cache.Device != resourceCreator.Device;
            if (!_needsCacheUpdate && cache != null && !deviceMismatch)
                return;

            cache?.Dispose();

            float sourceWidth = Math.Max(1, (float)source.SizeInPixels.Width);
            float sourceHeight = Math.Max(1, (float)source.SizeInPixels.Height);
            float screenWidth = Math.Max(1, (float)_lastSize.Width);
            float screenHeight = Math.Max(1, (float)_lastSize.Height);
            float screenDiagonal = MathF.Sqrt(screenWidth * screenWidth + screenHeight * screenHeight);
            float scale = Math.Max(screenDiagonal / sourceWidth, screenDiagonal / sourceHeight);
            float targetWidth = Math.Max(1, sourceWidth * scale);
            float targetHeight = Math.Max(1, sourceHeight * scale);

            cache = new CanvasRenderTarget(resourceCreator, targetWidth, targetHeight, source.Dpi);
            using CanvasDrawingSession ds = cache.CreateDrawingSession();
            ds.Clear(Color.FromArgb(0, 0, 0, 0));

            using var transform = new Transform2DEffect
            {
                Source = source,
                TransformMatrix = Matrix3x2.CreateScale(scale),
                InterpolationMode = CanvasImageInterpolation.Linear
            };
            using var blur = new GaussianBlurEffect
            {
                Source = transform,
                BlurAmount = BlurAmount,
                BorderMode = EffectBorderMode.Hard
            };

            ds.DrawImage(blur);

            if (source == _currentBitmap || _currentBitmap == null)
                _needsCacheUpdate = false;
        }

        private CanvasRenderTarget EnsureFallbackBitmap(ICanvasResourceCreator resourceCreator)
        {
            bool deviceMismatch = _fallbackBitmap != null && _fallbackBitmap.Device != resourceCreator.Device;
            if (_fallbackBitmap != null && !deviceMismatch)
                return _fallbackBitmap;

            _fallbackBitmap?.Dispose();
            _fallbackBitmap = new CanvasRenderTarget(resourceCreator, 640, 640, 96);
            using CanvasDrawingSession ds = _fallbackBitmap.CreateDrawingSession();
            ds.Clear(Color.FromArgb(255, 24, 20, 28));

            DrawFallbackOrb(ds, resourceCreator, new Vector2(160, 140), 360, Color.FromArgb(235, 82, 52, 124));
            DrawFallbackOrb(ds, resourceCreator, new Vector2(520, 140), 330, Color.FromArgb(225, 42, 95, 146));
            DrawFallbackOrb(ds, resourceCreator, new Vector2(380, 520), 420, Color.FromArgb(215, 144, 78, 42));

            using var shade = new CanvasRadialGradientBrush(
                resourceCreator,
                new[]
                {
                    new CanvasGradientStop { Position = 0.00f, Color = Color.FromArgb(0, 0, 0, 0) },
                    new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(130, 0, 0, 0) }
                });
            shade.Center = new Vector2(320, 300);
            shade.RadiusX = 430;
            shade.RadiusY = 430;
            ds.FillRectangle(0, 0, 640, 640, shade);

            return _fallbackBitmap;
        }

        private static void DrawFallbackOrb(
            CanvasDrawingSession drawingSession,
            ICanvasResourceCreator resourceCreator,
            Vector2 center,
            float radius,
            Color color)
        {
            using var brush = new CanvasRadialGradientBrush(
                resourceCreator,
                new[]
                {
                    new CanvasGradientStop { Position = 0.00f, Color = color },
                    new CanvasGradientStop { Position = 0.58f, Color = Color.FromArgb((byte)(color.A / 3), color.R, color.G, color.B) },
                    new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(0, color.R, color.G, color.B) }
                });
            brush.Center = center;
            brush.RadiusX = radius;
            brush.RadiusY = radius;
            drawingSession.FillCircle(center, radius, brush);
        }

        private static void DrawCachedLayer(
            CanvasDrawingSession drawingSession,
            CanvasRenderTarget texture,
            Vector2 screenCenter,
            float rotationRadians,
            float opacity)
        {
            Vector2 textureCenter = new(
                (float)texture.Size.Width / 2f,
                (float)texture.Size.Height / 2f);
            Matrix3x2 oldTransform = drawingSession.Transform;
            drawingSession.Transform =
                Matrix3x2.CreateTranslation(-textureCenter) *
                Matrix3x2.CreateRotation(rotationRadians) *
                Matrix3x2.CreateTranslation(screenCenter) *
                oldTransform;

            drawingSession.DrawImage(
                texture,
                0,
                0,
                new Rect(0, 0, texture.Size.Width, texture.Size.Height),
                opacity);
            drawingSession.Transform = oldTransform;
        }

        private void DrawDepthOverlays(
            ICanvasResourceCreator resourceCreator,
            CanvasDrawingSession drawingSession,
            float width,
            float height)
        {
            // ★ 复用缓存的 LinearGradientBrush，仅更新起止点。
            // 设备丢失重建后旧 brush 绑定的是旧设备 —— 必须检测 deviceMismatch 并重建。
            if (_cachedVerticalShade == null || _cachedVerticalShade.Device != resourceCreator.Device)
            {
                _cachedVerticalShade?.Dispose();
                _cachedVerticalShade = new CanvasLinearGradientBrush(
                    resourceCreator,
                    new[]
                    {
                        new CanvasGradientStop { Position = 0.00f, Color = Color.FromArgb(82, 0, 0, 0) },
                        new CanvasGradientStop { Position = 0.42f, Color = Color.FromArgb(34, 0, 0, 0) },
                        new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(178, 0, 0, 0) }
                    });
            }
            _cachedVerticalShade.StartPoint = new Vector2(0, 0);
            _cachedVerticalShade.EndPoint = new Vector2(0, height);
            drawingSession.FillRectangle(0, 0, width, height, _cachedVerticalShade);

            // ★ 复用缓存的 RadialGradientBrush，仅更新位置/尺寸。
            // 同样检测 deviceMismatch 防止设备丢失后使用失效资源。
            if (_cachedSideShade == null || _cachedSideShade.Device != resourceCreator.Device)
            {
                _cachedSideShade?.Dispose();
                _cachedSideShade = new CanvasRadialGradientBrush(
                    resourceCreator,
                    new[]
                    {
                        new CanvasGradientStop { Position = 0.00f, Color = Color.FromArgb(0, 0, 0, 0) },
                        new CanvasGradientStop { Position = 0.72f, Color = Color.FromArgb(64, 0, 0, 0) },
                        new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(176, 0, 0, 0) }
                    });
            }
            _cachedSideShade.Center = new Vector2(width * 0.52f, height * 0.45f);
            _cachedSideShade.RadiusX = width * 0.72f;
            _cachedSideShade.RadiusY = height * 0.74f;
            drawingSession.FillRectangle(0, 0, width, height, _cachedSideShade);

            drawingSession.FillRectangle(0, 0, width, height, Color.FromArgb(42, 7, 6, 10));
        }

        private static float SmoothStep(float value)
        {
            value = Math.Clamp(value, 0, 1);
            return value * value * (3 - 2 * value);
        }

        private void ReleasePrevious()
        {
            _previousBitmap?.Dispose();
            _previousCache?.Dispose();
            _previousBitmap = null;
            _previousCache = null;
        }

        public void Dispose()
        {
            _currentBitmap?.Dispose();
            _previousBitmap?.Dispose();
            _fallbackBitmap?.Dispose();
            _currentCache?.Dispose();
            _previousCache?.Dispose();

            _currentBitmap = null;
            _previousBitmap = null;
            _fallbackBitmap = null;
            _currentCache = null;
            _previousCache = null;
            _cachedVerticalShade?.Dispose();
            _cachedSideShade?.Dispose();
            _cachedVerticalShade = null;
            _cachedSideShade = null;
        }
    }
}
