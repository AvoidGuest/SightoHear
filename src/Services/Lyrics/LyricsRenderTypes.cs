using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace SightoHear.Services.Lyrics
{
    public enum TextAlignmentType
    {
        Left,
        Center,
        Right
    }

    public enum LyricsLineContentOrientation
    {
        Horizontal,
        Vertical
    }

    public enum LyricsFontWeight
    {
        Thin,
        ExtraLight,
        Light,
        SemiLight,
        Normal,
        Medium,
        SemiBold,
        Bold,
        ExtraBold,
        Black,
        ExtraBlack
    }

    public enum WordByWordEffectMode
    {
        Auto,
        Never,
        Always
    }

    public enum LyricsEffectScope
    {
        LongDurationSyllable,
        LineStartToCurrentChar
    }

    public enum EasingType
    {
        Linear,
        SmoothStep,
        Sine,
        Quad,
        Cubic,
        Quart,
        Quint,
        Expo,
        Circle,
        Back,
        Elastic,
        Bounce
    }

    public enum EaseMode
    {
        In,
        Out,
        InOut
    }

    public readonly struct Keyframe<T>
    {
        public T Value { get; }
        public double Duration { get; }

        public Keyframe(T value, double durationSeconds)
        {
            Value = value;
            Duration = durationSeconds;
        }
    }

    public struct NowPlayingPalette
    {
        public Color NonCurrentLineFillColor;
        public Color PlayedCurrentLineFillColor;
        public Color UnplayedCurrentLineFillColor;
        public Color PlayedTextStrokeColor;
        public Color UnplayedTextStrokeColor;
    }

    public sealed class LyricsStyleSettings
    {
        public bool IsDynamicLyricsFontSize { get; set; } = true;
        public int PhoneticLyricsFontSize { get; set; } = 12;
        public int OriginalLyricsFontSize { get; set; } = 32;
        public int TranslatedLyricsFontSize { get; set; } = 18;
        public int PhoneticLyricsOpacity { get; set; } = 60;
        public int PlayedOriginalLyricsOpacity { get; set; } = 100;
        public int UnplayedOriginalLyricsOpacity { get; set; } = 30;
        public int TranslatedLyricsOpacity { get; set; } = 60;
        public TextAlignmentType LyricsAlignmentType { get; set; } = TextAlignmentType.Left;
        public bool UseInternalLyricsAlignment { get; set; } = true;
        public LyricsLineContentOrientation LyricsLineContentOrientation { get; set; } = LyricsLineContentOrientation.Vertical;
        public bool AutoWrap { get; set; } = true;
        public int LyricsFontStrokeWidth { get; set; } = 0;
        public LyricsFontWeight LyricsFontWeight { get; set; } = LyricsFontWeight.Bold;
        public double LyricsLineOverallSpacingFactor { get; set; } = 0.5;
        public double LyricsLineInnerSpacingFactor { get; set; } = 0.1;
        public string LyricsCJKFontFamily { get; set; } = "Arial";
        public string LyricsWesternFontFamily { get; set; } = "Arial";
        public int PlayingLineTopOffset { get; set; } = 50;
    }

    public sealed class LyricsEffectSettings
    {
        public WordByWordEffectMode WordByWordEffectMode { get; set; } = WordByWordEffectMode.Auto;
        public bool IsLyricsBlurEffectEnabled { get; set; } = true;
        public bool IsLyricsFadeOutEffectEnabled { get; set; } = true;
        public bool IsLyricsOutOfSightEffectEnabled { get; set; } = true;
        public bool IsLyricsGlowEffectEnabled { get; set; } = true;
        public LyricsEffectScope LyricsGlowEffectScope { get; set; } = LyricsEffectScope.LongDurationSyllable;
        public int LyricsGlowEffectLongSyllableDuration { get; set; } = 700;
        public bool IsLyricsGlowEffectAmountAutoAdjust { get; set; } = true;
        public int LyricsGlowEffectAmount { get; set; } = 8;
        public bool IsLyricsScaleEffectEnabled { get; set; } = true;
        public int LyricsScaleEffectLongSyllableDuration { get; set; } = 700;
        public bool IsLyricsScaleEffectAmountAutoAdjust { get; set; } = true;
        public int LyricsScaleEffectAmount { get; set; } = 115;
        public bool IsLyricsFloatAnimationEnabled { get; set; } = true;
        public bool IsLyricsFloatAnimationAmountAutoAdjust { get; set; } = true;
        public int LyricsFloatAnimationAmount { get; set; } = 8;
        public int LyricsFloatAnimationDuration { get; set; } = 450;
        public EasingType LyricsScrollEasingType { get; set; } = EasingType.Quad;
        public EaseMode LyricsScrollEasingMode { get; set; } = EaseMode.Out;
        public int LyricsScrollDuration { get; set; } = 500;
        public int LyricsScrollTopDuration { get; set; } = 500;
        public int LyricsScrollBottomDuration { get; set; } = 500;
        public int LyricsScrollTopDelay { get; set; } = 0;
        public int LyricsScrollBottomDelay { get; set; } = 0;
        public bool IsFanLyricsEnabled { get; set; } = false;
        public int FanLyricsAngle { get; set; } = 30;
        public bool IsLyricsBrethingEffectEnabled { get; set; } = false;
        public int LyricsBreathingIntensity { get; set; } = 80;
    }

    public sealed class LyricsWindowStatus
    {
        public LyricsStyleSettings LyricsStyleSettings { get; } = new();
        public LyricsEffectSettings LyricsEffectSettings { get; } = new();
        public NowPlayingPalette WindowPalette { get; set; } = new()
        {
            NonCurrentLineFillColor = Color.FromArgb(255, 255, 255, 255),
            PlayedCurrentLineFillColor = Color.FromArgb(255, 255, 255, 255),
            UnplayedCurrentLineFillColor = Color.FromArgb(255, 255, 255, 255),
            PlayedTextStrokeColor = Color.FromArgb(255, 255, 255, 255),
            UnplayedTextStrokeColor = Color.FromArgb(255, 255, 255, 255)
        };
    }

    public sealed class ValueTransition<T> where T : struct
    {
        private T _currentValue;
        private T _startValue;
        private T _targetValue;
        private readonly Queue<Keyframe<T>> _keyframeQueue = new();
        private double _stepDuration;
        private double _totalDurationForAutoSplit;
        private double _configuredDelaySeconds;
        private Func<T, T, double, T> _interpolator;
        private bool _isTransitioning;
        private double _progress;

        public T Value => _currentValue;
        public bool IsTransitioning => _isTransitioning;
        public T TargetValue => _targetValue;
        public double DurationSeconds => _totalDurationForAutoSplit;
        public double Progress => _progress;
        public Func<T, T, double, T> Interpolator => _interpolator;

        public ValueTransition(T initialValue, Func<T, T, double, T>? interpolator, double defaultTotalDuration = 0.3)
        {
            _currentValue = initialValue;
            _startValue = initialValue;
            _targetValue = initialValue;
            _totalDurationForAutoSplit = defaultTotalDuration;
            _interpolator = interpolator ?? throw new ArgumentNullException(nameof(interpolator));
        }

        public void SetDuration(double seconds) => _totalDurationForAutoSplit = Math.Max(0, seconds);
        public void SetDurationMs(double milliseconds) => SetDuration(milliseconds / 1000.0);
        public void SetDelay(double seconds) => _configuredDelaySeconds = seconds;
        public void SetInterpolator(Func<T, T, double, T> interpolator) => _interpolator = interpolator;

        public void JumpTo(T value)
        {
            _keyframeQueue.Clear();
            _currentValue = value;
            _startValue = value;
            _targetValue = value;
            _isTransitioning = false;
            _progress = 0;
        }

        public void Start(params Keyframe<T>[] keyframes)
        {
            if (keyframes.Length == 0)
                return;

            PrepareStart();
            if (_configuredDelaySeconds > 0)
                _keyframeQueue.Enqueue(new Keyframe<T>(_currentValue, _configuredDelaySeconds));
            foreach (Keyframe<T> keyframe in keyframes)
                _keyframeQueue.Enqueue(keyframe);
            MoveToNextSegment(firstStart: true);
        }

        public void Start(params T[] values)
        {
            if (values.Length == 0)
                return;
            if (values.Length == 1 && values[0].Equals(_currentValue) && _configuredDelaySeconds <= 0)
                return;

            PrepareStart();
            if (_configuredDelaySeconds > 0)
                _keyframeQueue.Enqueue(new Keyframe<T>(_currentValue, _configuredDelaySeconds));

            double autoStepDuration = _totalDurationForAutoSplit / values.Length;
            foreach (T value in values)
                _keyframeQueue.Enqueue(new Keyframe<T>(value, autoStepDuration));
            MoveToNextSegment(firstStart: true);
        }

        private void PrepareStart()
        {
            _keyframeQueue.Clear();
            _isTransitioning = true;
        }

        private void MoveToNextSegment(bool firstStart = false)
        {
            if (_keyframeQueue.Count > 0)
            {
                Keyframe<T> keyframe = _keyframeQueue.Dequeue();
                _startValue = firstStart ? _currentValue : _targetValue;
                _targetValue = keyframe.Value;
                _stepDuration = keyframe.Duration;
                if (firstStart)
                    _progress = 0;
            }
            else
            {
                _currentValue = _targetValue;
                _isTransitioning = false;
                _progress = 1;
            }
        }

        public void Update(TimeSpan elapsedTime)
        {
            if (!_isTransitioning)
                return;

            double timeStep = elapsedTime.TotalSeconds;
            while (timeStep > 0 && _isTransitioning)
            {
                double progressDelta = _stepDuration > 0.000001 ? timeStep / _stepDuration : 1.0;
                if (_progress + progressDelta >= 1.0)
                {
                    double timeConsumed = (1.0 - _progress) * _stepDuration;
                    timeStep -= timeConsumed;
                    _progress = 1.0;
                    _currentValue = _targetValue;
                    MoveToNextSegment();
                    if (_isTransitioning)
                        _progress = 0;
                }
                else
                {
                    _progress += progressDelta;
                    timeStep = 0;
                    _currentValue = _interpolator(_startValue, _targetValue, _progress);
                }
            }
        }
    }

    public static class LyricsMath
    {
        public static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(350);
        public static readonly TimeSpan LongAnimationDuration = TimeSpan.FromMilliseconds(650);

        public static Func<double, double, double, double> GetDoubleInterpolator(EasingType type, EaseMode easingMode = EaseMode.Out)
        {
            return (start, end, progress) =>
            {
                double t = Ease(progress, easingMode, type);
                return start + (end - start) * t;
            };
        }

        public static Color InterpolateColor(Color from, Color to, double progress)
        {
            progress = Math.Clamp(progress, 0, 1);
            return Color.FromArgb(
                (byte)(from.A + (to.A - from.A) * progress),
                (byte)(from.R + (to.R - from.R) * progress),
                (byte)(from.G + (to.G - from.G) * progress),
                (byte)(from.B + (to.B - from.B) * progress));
        }

        public static double Ease(double t, EaseMode mode, EasingType type)
        {
            t = Math.Clamp(t, 0, 1);
            double EaseIn(double x) => type switch
            {
                EasingType.Linear => x,
                EasingType.SmoothStep => x * x * (3 - 2 * x),
                EasingType.Sine => 1 - Math.Cos(x * Math.PI / 2),
                EasingType.Cubic => x * x * x,
                EasingType.Quart => x * x * x * x,
                EasingType.Quint => x * x * x * x * x,
                EasingType.Expo => x == 0 ? 0 : Math.Pow(2, 10 * x - 10),
                EasingType.Circle => 1 - Math.Sqrt(1 - x * x),
                EasingType.Back => (1.70158 + 1) * x * x * x - 1.70158 * x * x,
                _ => x * x
            };

            return mode switch
            {
                EaseMode.In => EaseIn(t),
                EaseMode.InOut => t < 0.5 ? EaseIn(t * 2) / 2 : 1 - EaseIn((1 - t) * 2) / 2,
                _ => 1 - EaseIn(1 - t)
            };
        }

        public static CanvasHorizontalAlignment ToCanvasHorizontalAlignment(this TextAlignmentType alignmentType) =>
            alignmentType switch
            {
                TextAlignmentType.Center => CanvasHorizontalAlignment.Center,
                TextAlignmentType.Right => CanvasHorizontalAlignment.Right,
                _ => CanvasHorizontalAlignment.Left
            };

        public static Windows.UI.Text.FontWeight ToFontWeight(this LyricsFontWeight weight) =>
            weight switch
            {
                LyricsFontWeight.Thin => Microsoft.UI.Text.FontWeights.Thin,
                LyricsFontWeight.ExtraLight => Microsoft.UI.Text.FontWeights.ExtraLight,
                LyricsFontWeight.Light => Microsoft.UI.Text.FontWeights.Light,
                LyricsFontWeight.SemiLight => Microsoft.UI.Text.FontWeights.SemiLight,
                LyricsFontWeight.Normal => Microsoft.UI.Text.FontWeights.Normal,
                LyricsFontWeight.Medium => Microsoft.UI.Text.FontWeights.Medium,
                LyricsFontWeight.SemiBold => Microsoft.UI.Text.FontWeights.SemiBold,
                LyricsFontWeight.ExtraBold => Microsoft.UI.Text.FontWeights.ExtraBold,
                LyricsFontWeight.Black => Microsoft.UI.Text.FontWeights.Black,
                LyricsFontWeight.ExtraBlack => Microsoft.UI.Text.FontWeights.ExtraBlack,
                _ => Microsoft.UI.Text.FontWeights.Bold
            };

        public static Color WithAlpha(this Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
        public static Rect AddX(this Rect rect, double x) => new(rect.X + x, rect.Y, rect.Width, rect.Height);
        public static Rect AddY(this Rect rect, double y) => new(rect.X, rect.Y + y, rect.Width, rect.Height);
        public static Rect Extend(this Rect rect, double padding) => rect.Extend(padding, padding, padding, padding);
        public static Rect Extend(this Rect rect, double horizontalPadding, double verticalPadding) => rect.Extend(horizontalPadding, verticalPadding, horizontalPadding, verticalPadding);
        public static Rect Extend(this Rect rect, double left, double top, double right, double bottom) =>
            new(rect.X - left, rect.Y - top, rect.Width + left + right, rect.Height + top + bottom);
        public static Rect Scale(this Rect rect, double scale)
        {
            double scaledWidth = rect.Width * scale;
            double scaledHeight = rect.Height * scale;
            return new Rect(
                rect.X - (scaledWidth - rect.Width) / 2,
                rect.Y - (scaledHeight - rect.Height) / 2,
                scaledWidth,
                scaledHeight);
        }
        public static Windows.Foundation.Point ToPoint(this Vector2 vector) => new(vector.X, vector.Y);
        public static Vector2 AddX(this Vector2 vector, float x) => new(vector.X + x, vector.Y);
        public static Vector2 AddY(this Vector2 vector, float y) => new(vector.X, vector.Y + y);
    }

    public sealed class RenderLyricsSyllable : BaseRenderLyrics
    {
        public List<RenderLyricsChar> ChildrenRenderLyricsChars { get; } = [];
        public RenderLyricsSyllable(BaseLyrics lyricsSyllable) : base(lyricsSyllable) { }
    }

    public sealed class RenderLyricsChar : BaseRenderLyrics
    {
        public Rect LayoutRect { get; }
        public ValueTransition<double> ScaleTransition { get; }
        public ValueTransition<double> GlowTransition { get; }
        public ValueTransition<double> FloatTransition { get; }
        public CropEffect Crop { get; }
        public GaussianBlurEffect Glow { get; }
        public double ProgressPlayed { get; set; }

        public RenderLyricsChar(BaseLyrics lyricsChar, Rect layoutRect) : base(lyricsChar)
        {
            ScaleTransition = new(1.0, LyricsMath.GetDoubleInterpolator(EasingType.Sine), LyricsMath.AnimationDuration.TotalSeconds);
            GlowTransition = new(0, LyricsMath.GetDoubleInterpolator(EasingType.Sine), LyricsMath.AnimationDuration.TotalSeconds);
            FloatTransition = new(0, LyricsMath.GetDoubleInterpolator(EasingType.Sine), LyricsMath.LongAnimationDuration.TotalSeconds);
            LayoutRect = layoutRect;
            Crop = new CropEffect { BorderMode = EffectBorderMode.Hard };
            Glow = new GaussianBlurEffect { Source = Crop, BorderMode = EffectBorderMode.Soft };
        }

        public void Update(TimeSpan elapsedTime)
        {
            ScaleTransition.Update(elapsedTime);
            GlowTransition.Update(elapsedTime);
            FloatTransition.Update(elapsedTime);
        }

        public void DisposeEffects()
        {
            Crop.Dispose();
            Glow.Dispose();
        }
    }

    public sealed class RenderLyricsRegion : IDisposable
    {
        public CanvasGradientStop[] FillStops { get; } = new CanvasGradientStop[4];
        public CanvasGradientStop[] StrokeStops { get; } = new CanvasGradientStop[4];
        public AlphaMaskEffect FinalFillEffect { get; }
        public AlphaMaskEffect? FinalStrokeEffect { get; }
        public CompositeEffect? CombinedEffect { get; }

        public RenderLyricsRegion(ICanvasImage cachedFill, ICanvasImage? cachedStroke)
        {
            FinalFillEffect = new AlphaMaskEffect { AlphaMask = cachedFill };
            if (cachedStroke != null)
            {
                FinalStrokeEffect = new AlphaMaskEffect { AlphaMask = cachedStroke };
                CombinedEffect = new CompositeEffect
                {
                    Sources = { FinalStrokeEffect, FinalFillEffect },
                    Mode = CanvasComposite.SourceOver
                };
            }
        }

        public void Dispose()
        {
            FinalFillEffect.Dispose();
            FinalStrokeEffect?.Dispose();
            CombinedEffect?.Dispose();
        }
    }

    public sealed class RenderLyricsLine : BaseRenderLyrics
    {
        public List<RenderLyricsChar> PrimaryRenderChars { get; } = [];
        private readonly Dictionary<int, RenderLyricsChar> _primaryRenderCharsByIndex = [];
        public List<RenderLyricsSyllable> PrimaryRenderSyllables { get; }
        public double AnimationDuration { get; set; } = 0.3;
        public ValueTransition<double> AngleTransition { get; }
        public ValueTransition<double> BlurAmountTransition { get; }
        public ValueTransition<double> ScaleTransition { get; }
        public ValueTransition<double> PlayedPrimaryOpacityTransition { get; }
        public ValueTransition<double> UnplayedPrimaryOpacityTransition { get; }
        public ValueTransition<double> SecondaryOpacityTransition { get; }
        public ValueTransition<double> TertiaryOpacityTransition { get; }
        public ValueTransition<double> PrimaryXOffsetTransition { get; }
        public ValueTransition<double> SecondaryXOffsetTransition { get; }
        public ValueTransition<double> TertiaryXOffsetTransition { get; }
        public ValueTransition<double> YOffsetTransition { get; }
        public ValueTransition<Color> PlayedFillColorTransition { get; }
        public ValueTransition<Color> UnplayedFillColorTransition { get; }
        public ValueTransition<Color> PlayedStrokeColorTransition { get; }
        public ValueTransition<Color> UnplayedStrokeColorTransition { get; }
        public CanvasTextLayout? PrimaryTextLayout { get; private set; }
        public CanvasTextLayout? SecondaryTextLayout { get; private set; }
        public CanvasTextLayout? TertiaryTextLayout { get; private set; }
        public Vector2 PrimaryPosition { get; set; }
        public Vector2 SecondaryPosition { get; set; }
        public Vector2 TertiaryPosition { get; set; }
        public Vector2 TopLeftPosition { get; set; }
        public Vector2 CenterPosition { get; set; }
        public Vector2 BottomRightPosition { get; set; }
        public CanvasGeometry? PrimaryCanvasGeometry { get; private set; }
        public CanvasGeometry? SecondaryCanvasGeometry { get; private set; }
        public CanvasGeometry? TertiaryCanvasGeometry { get; private set; }
        public string PrimaryText { get; set; } = "";
        public string SecondaryText { get; set; } = "";
        public string TertiaryText { get; set; } = "";
        public CanvasCommandList? CachedStroke { get; private set; }
        public CanvasCommandList? CachedFill { get; private set; }
        public TintEffect? UnplayedFillTint { get; private set; }
        public TintEffect? UnplayedStrokeTint { get; private set; }
        public CompositeEffect? UnplayedComposite { get; private set; }
        public CanvasTextLayoutRegion[]? PrimaryTextRegions { get; private set; }
        public RenderLyricsRegion[]? RenderLyricsRegions { get; private set; }
        public int LaneIndex { get; set; }
        public double? PrimaryLineHeight => PrimaryRenderChars.FirstOrDefault()?.LayoutRect.Height ?? PrimaryTextLayout?.LayoutBounds.Height;
        public bool IsPrimaryHasRealSyllableInfo { get; set; }
        public string AgentId { get; set; }
        public TextAlignmentType? HorizontalAlignmentType { get; set; }

        public RenderLyricsLine(LyricsLine lyricsLine) : base(lyricsLine)
        {
            Func<double, double, double, double> sine = LyricsMath.GetDoubleInterpolator(EasingType.Sine);
            AngleTransition = new(0, sine, AnimationDuration);
            BlurAmountTransition = new(0, sine, AnimationDuration);
            TertiaryOpacityTransition = new(0, sine, AnimationDuration);
            PlayedPrimaryOpacityTransition = new(0, sine, AnimationDuration);
            UnplayedPrimaryOpacityTransition = new(0, sine, AnimationDuration);
            SecondaryOpacityTransition = new(0, sine, AnimationDuration);
            ScaleTransition = new(1.0, sine, AnimationDuration);
            PrimaryXOffsetTransition = new(0, sine, AnimationDuration);
            SecondaryXOffsetTransition = new(0, sine, AnimationDuration);
            TertiaryXOffsetTransition = new(0, sine, AnimationDuration);
            YOffsetTransition = new(0, sine, AnimationDuration);
            PlayedFillColorTransition = new(Colors.Transparent, LyricsMath.InterpolateColor, AnimationDuration);
            UnplayedFillColorTransition = new(Colors.Transparent, LyricsMath.InterpolateColor, AnimationDuration);
            PlayedStrokeColorTransition = new(Colors.Transparent, LyricsMath.InterpolateColor, AnimationDuration);
            UnplayedStrokeColorTransition = new(Colors.Transparent, LyricsMath.InterpolateColor, AnimationDuration);

            StartMs = lyricsLine.StartMs;
            EndMs = lyricsLine.EndMs;
            TertiaryText = lyricsLine.TertiaryText;
            PrimaryText = lyricsLine.PrimaryText;
            SecondaryText = lyricsLine.SecondaryText;
            PrimaryRenderSyllables = lyricsLine.PrimarySyllables.Select(x => new RenderLyricsSyllable(x)).ToList();
            IsPrimaryHasRealSyllableInfo = lyricsLine.IsPrimaryHasRealSyllableInfo;
            AgentId = lyricsLine.AgentId;
        }

        public void DisposeTextLayout()
        {
            TertiaryTextLayout?.Dispose();
            PrimaryTextLayout?.Dispose();
            SecondaryTextLayout?.Dispose();
            TertiaryTextLayout = null;
            PrimaryTextLayout = null;
            SecondaryTextLayout = null;
        }

        public void RecreateTextLayout(
            ICanvasResourceCreator resourceCreator,
            bool createPhonetic,
            bool createTranslated,
            int phoneticTextFontSize,
            int originalTextFontSize,
            int translatedTextFontSize,
            LyricsFontWeight fontWeight,
            string fontFamilyCJK,
            string fontFamilyWestern,
            double maxWidth,
            double maxHeight,
            TextAlignmentType type,
            bool autoWrap,
            LyricsLineContentOrientation orientation)
        {
            DisposeTextLayout();

            CanvasWordWrapping wordWrapping = autoWrap ? CanvasWordWrapping.Wrap : CanvasWordWrapping.NoWrap;
            CanvasHorizontalAlignment horizontalAlignment = type.ToCanvasHorizontalAlignment();
            bool phoneticVisible = createPhonetic && !string.IsNullOrWhiteSpace(TertiaryText);
            bool translatedVisible = createTranslated && !string.IsNullOrWhiteSpace(SecondaryText);
            double requestedWidth = orientation == LyricsLineContentOrientation.Horizontal
                ? maxWidth / (1 + (translatedVisible ? 1 : 0))
                : maxWidth;

            if (phoneticVisible)
            {
                TertiaryTextLayout = CreateLayout(resourceCreator, TertiaryText, phoneticTextFontSize, fontWeight, wordWrapping, horizontalAlignment, requestedWidth, maxHeight);
            }

            PrimaryTextLayout = CreateLayout(resourceCreator, PrimaryText, originalTextFontSize, fontWeight, wordWrapping, horizontalAlignment, requestedWidth, maxHeight);
            PrimaryTextRegions = PrimaryText.Length > 0 ? PrimaryTextLayout.GetCharacterRegions(0, PrimaryText.Length) : [];

            if (translatedVisible)
            {
                SecondaryTextLayout = CreateLayout(resourceCreator, SecondaryText, translatedTextFontSize, fontWeight, wordWrapping, horizontalAlignment, requestedWidth, maxHeight);
            }
        }

        private static CanvasTextLayout CreateLayout(
            ICanvasResourceCreator resourceCreator,
            string text,
            int fontSize,
            LyricsFontWeight fontWeight,
            CanvasWordWrapping wordWrapping,
            CanvasHorizontalAlignment horizontalAlignment,
            double width,
            double height)
        {
            return new CanvasTextLayout(resourceCreator, text, new CanvasTextFormat
            {
                VerticalAlignment = CanvasVerticalAlignment.Top,
                FontSize = fontSize,
                FontWeight = fontWeight.ToFontWeight(),
                WordWrapping = wordWrapping
            }, (float)width, (float)height)
            {
                HorizontalAlignment = horizontalAlignment,
                Options = CanvasDrawTextOptions.NoPixelSnap
            };
        }

        public void DisposeTextGeometry()
        {
            TertiaryCanvasGeometry?.Dispose();
            PrimaryCanvasGeometry?.Dispose();
            SecondaryCanvasGeometry?.Dispose();
            TertiaryCanvasGeometry = null;
            PrimaryCanvasGeometry = null;
            SecondaryCanvasGeometry = null;
        }

        public void RecreateTextGeometry()
        {
            DisposeTextGeometry();
            if (TertiaryTextLayout != null)
                TertiaryCanvasGeometry = CanvasGeometry.CreateText(TertiaryTextLayout);
            if (PrimaryTextLayout != null)
                PrimaryCanvasGeometry = CanvasGeometry.CreateText(PrimaryTextLayout);
            if (SecondaryTextLayout != null)
                SecondaryCanvasGeometry = CanvasGeometry.CreateText(SecondaryTextLayout);
        }

        public void RecreateRenderChars(int strokeWidth)
        {
            PrimaryRenderChars.Clear();
            _primaryRenderCharsByIndex.Clear();
            if (PrimaryTextLayout == null)
                return;

            foreach (RenderLyricsSyllable syllable in PrimaryRenderSyllables)
                syllable.ChildrenRenderLyricsChars.Clear();

            for (int startCharIndex = 0; startCharIndex < PrimaryText.Length; startCharIndex++)
            {
                CanvasTextLayoutRegion region = PrimaryTextLayout.GetCharacterRegions(startCharIndex, 1).FirstOrDefault();
                Rect bounds = region.LayoutBounds.Extend(
                    startCharIndex == 0 ? strokeWidth : strokeWidth / 4f,
                    strokeWidth / 2f,
                    startCharIndex == PrimaryText.Length - 1 ? strokeWidth : strokeWidth / 4f,
                    strokeWidth / 2f);

                RenderLyricsSyllable? syllable = PrimaryRenderSyllables.FirstOrDefault(x => x.StartIndex <= startCharIndex && startCharIndex <= x.EndIndex);
                if (syllable == null || syllable.Length == 0)
                    continue;

                int syllableDuration = Math.Max(syllable.DurationMs, syllable.Length);
                double avgCharDuration = syllableDuration / (double)syllable.Length;
                int charOffset = startCharIndex - syllable.StartIndex;
                int charStartMs = syllable.StartMs + (int)Math.Floor(charOffset * avgCharDuration);
                int charEndMs = syllable.StartMs + (int)Math.Ceiling((charOffset + 1) * avgCharDuration);
                if (charEndMs <= charStartMs)
                    charEndMs = charStartMs + 1;

                var renderChar = new RenderLyricsChar(new BaseLyrics
                {
                    StartIndex = startCharIndex,
                    Text = PrimaryText[startCharIndex].ToString(),
                    StartMs = charStartMs,
                    EndMs = charEndMs
                }, bounds);

                syllable.ChildrenRenderLyricsChars.Add(renderChar);
                PrimaryRenderChars.Add(renderChar);
                _primaryRenderCharsByIndex[startCharIndex] = renderChar;
            }
        }

        public bool TryGetPrimaryRenderChar(int charIndex, out RenderLyricsChar renderChar) =>
            _primaryRenderCharsByIndex.TryGetValue(charIndex, out renderChar!);

        public void EnsureCaches(ICanvasResourceCreator resourceCreator, double strokeWidth)
        {
            if (CachedStroke != null && CachedFill != null)
                return;

            CachedFill = new CanvasCommandList(resourceCreator);
            using (CanvasDrawingSession ds = CachedFill.CreateDrawingSession())
            {
                if (TertiaryTextLayout != null) ds.DrawTextLayout(TertiaryTextLayout, TertiaryPosition, Colors.White);
                if (PrimaryTextLayout != null) ds.DrawTextLayout(PrimaryTextLayout, PrimaryPosition, Colors.White);
                if (SecondaryTextLayout != null) ds.DrawTextLayout(SecondaryTextLayout, SecondaryPosition, Colors.White);
            }

            CachedStroke = new CanvasCommandList(resourceCreator);
            if (strokeWidth > 0)
            {
                using CanvasStrokeStyle strokeStyle = new()
                {
                    LineJoin = CanvasLineJoin.Round,
                    StartCap = CanvasCapStyle.Round,
                    EndCap = CanvasCapStyle.Round
                };
                using CanvasDrawingSession ds = CachedStroke.CreateDrawingSession();
                if (TertiaryCanvasGeometry != null) ds.DrawGeometry(TertiaryCanvasGeometry, TertiaryPosition, Colors.White, (float)strokeWidth, strokeStyle);
                if (PrimaryCanvasGeometry != null) ds.DrawGeometry(PrimaryCanvasGeometry, PrimaryPosition, Colors.White, (float)strokeWidth, strokeStyle);
                if (SecondaryCanvasGeometry != null) ds.DrawGeometry(SecondaryCanvasGeometry, SecondaryPosition, Colors.White, (float)strokeWidth, strokeStyle);
            }

            UnplayedFillTint = new TintEffect { Source = CachedFill, Color = Colors.White };
            UnplayedStrokeTint = new TintEffect { Source = CachedStroke, Color = Colors.White };
            UnplayedComposite = new CompositeEffect { Sources = { UnplayedStrokeTint, UnplayedFillTint }, Mode = CanvasComposite.SourceOver };

            if (PrimaryTextRegions != null && (RenderLyricsRegions == null || RenderLyricsRegions.Length != PrimaryTextRegions.Length))
            {
                DisposeRenderLyricsRegions();
                RenderLyricsRegions = new RenderLyricsRegion[PrimaryTextRegions.Length];
                for (int i = 0; i < PrimaryTextRegions.Length; i++)
                    RenderLyricsRegions[i] = new RenderLyricsRegion(CachedFill, CachedStroke);
            }
        }

        private void DisposeRenderLyricsRegions()
        {
            if (RenderLyricsRegions == null)
                return;
            foreach (RenderLyricsRegion region in RenderLyricsRegions)
                region.Dispose();
            RenderLyricsRegions = null;
        }

        public void DisposeCaches()
        {
            UnplayedComposite?.Dispose();
            UnplayedStrokeTint?.Dispose();
            UnplayedFillTint?.Dispose();
            CachedStroke?.Dispose();
            CachedFill?.Dispose();
            UnplayedComposite = null;
            UnplayedStrokeTint = null;
            UnplayedFillTint = null;
            CachedStroke = null;
            CachedFill = null;
            DisposeRenderLyricsRegions();
            foreach (RenderLyricsChar renderChar in PrimaryRenderChars)
                renderChar.DisposeEffects();
        }

        public void Update(TimeSpan elapsedTime)
        {
            AngleTransition.Update(elapsedTime);
            ScaleTransition.Update(elapsedTime);
            BlurAmountTransition.Update(elapsedTime);
            PlayedPrimaryOpacityTransition.Update(elapsedTime);
            UnplayedPrimaryOpacityTransition.Update(elapsedTime);
            SecondaryOpacityTransition.Update(elapsedTime);
            TertiaryOpacityTransition.Update(elapsedTime);
            PrimaryXOffsetTransition.Update(elapsedTime);
            SecondaryXOffsetTransition.Update(elapsedTime);
            TertiaryXOffsetTransition.Update(elapsedTime);
            YOffsetTransition.Update(elapsedTime);
            PlayedFillColorTransition.Update(elapsedTime);
            UnplayedFillColorTransition.Update(elapsedTime);
            PlayedStrokeColorTransition.Update(elapsedTime);
            UnplayedStrokeColorTransition.Update(elapsedTime);
        }
    }
}
