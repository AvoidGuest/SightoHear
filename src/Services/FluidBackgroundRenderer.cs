using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using SightoHear.Controls;
using SightoHear.Shaders;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System;
using System.Numerics;
using Windows.UI;

namespace SightoHear.Services
{
    public sealed class FluidBackgroundRenderer : IDisposable
    {
        private PixelShaderEffect<FluidBackgroundEffect>? _fluidEffect;
        private readonly object _lock = new();
        private float _time;

        public bool IsEnabled { get; set; } = true;
        public double Opacity { get; set; } = 1.0;
        public bool EnableLightWave { get; set; }
        public bool EnableDithering { get; set; } = true;
        public bool UseHSVBlending { get; set; }

        public void LoadResources()
        {
            var oldEffect = _fluidEffect;
            _fluidEffect = new PixelShaderEffect<FluidBackgroundEffect>();
            oldEffect?.Dispose();
        }

        public void Update(
            IRenderSurface surface,
            TimeSpan elapsedTime,
            Color color1,
            Color color2,
            Color color3,
            Color color4)
        {
            if (_fluidEffect == null || !IsEnabled)
                return;

            _time += (float)elapsedTime.TotalSeconds;

            float width = surface.ConvertDipsToPixels((float)surface.Size.Width, CanvasDpiRounding.Round);
            float height = surface.ConvertDipsToPixels((float)surface.Size.Height, CanvasDpiRounding.Round);

            _fluidEffect.ConstantBuffer = new FluidBackgroundEffect(
                new float2(width, height),
                _time,
                ToFloat3(color1),
                ToFloat3(color2),
                ToFloat3(color3),
                ToFloat3(color4),
                0,
                0,
                0,
                UseHSVBlending,
                EnableLightWave,
                EnableDithering);
        }

        public void Draw(IRenderSurface surface, CanvasDrawingSession drawingSession)
        {
            if (!IsEnabled || Opacity <= 0)
                return;

            var effect = _fluidEffect;
            if (effect == null)
                return;

            if (Opacity >= 1.0)
            {
                drawingSession.DrawImage(effect);
                return;
            }

            using var opacityEffect = new OpacityEffect
            {
                Source = effect,
                Opacity = (float)Opacity
            };
            drawingSession.DrawImage(opacityEffect);
        }

        private static float3 ToFloat3(Color color) =>
            new(color.R / 255f, color.G / 255f, color.B / 255f);

        public void Dispose()
        {
            var effect = _fluidEffect;
            _fluidEffect = null;
            effect?.Dispose();
        }
    }
}
