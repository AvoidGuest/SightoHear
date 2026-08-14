using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.Foundation;

namespace SightoHear.Services.Lyrics
{
    public static class LyricsLayoutManager
    {
        private const float BaseMinFontSize = 14f;
        private const float BaseMaxFontSize = 80f;
        private const float TargetMinVisibleLines = 5f;
        private const float WidthPaddingRatio = 0.85f;

        public static void MeasureAndArrange(
            ICanvasResourceCreator resourceCreator,
            IList<RenderLyricsLine>? lines,
            LyricsWindowStatus status,
            double canvasWidth,
            double canvasHeight,
            double lyricsWidth,
            double lyricsHeight)
        {
            if (lines == null)
                return;

            LyricsStyleSettings style = status.LyricsStyleSettings;
            int originalFontSize;
            int phoneticFontSize;
            int translatedFontSize;

            if (style.IsDynamicLyricsFontSize)
            {
                float baseSize = CalculateBaseFontSize(canvasWidth, canvasHeight);
                phoneticFontSize = (int)Math.Max(baseSize * 0.55f, 10f);
                originalFontSize = (int)baseSize;
                translatedFontSize = (int)Math.Max(baseSize * 0.70f, 10f);
            }
            else
            {
                phoneticFontSize = style.PhoneticLyricsFontSize;
                originalFontSize = style.OriginalLyricsFontSize;
                translatedFontSize = style.TranslatedLyricsFontSize;
            }

            double currentX = 0;
            double currentY = 0;
            foreach (RenderLyricsLine line in lines)
            {
                double actualWidth = 0;
                TextAlignmentType alignment = style.UseInternalLyricsAlignment
                    ? line.HorizontalAlignmentType ?? style.LyricsAlignmentType
                    : style.LyricsAlignmentType;

                line.RecreateTextLayout(
                    resourceCreator,
                    createPhonetic: false,
                    createTranslated: true,
                    phoneticFontSize,
                    originalFontSize,
                    translatedFontSize,
                    style.LyricsFontWeight,
                    style.LyricsCJKFontFamily,
                    style.LyricsWesternFontFamily,
                    lyricsWidth,
                    lyricsHeight,
                    alignment,
                    style.AutoWrap,
                    style.LyricsLineContentOrientation);

                line.RecreateTextGeometry();
                line.DisposeCaches();

                line.TopLeftPosition = new Vector2((float)currentX, (float)currentY);
                line.TertiaryPosition = line.TopLeftPosition;
                if (line.TertiaryTextLayout != null)
                {
                    currentY += line.TertiaryTextLayout.LayoutBounds.Height;
                    currentY += (line.TertiaryTextLayout.LayoutBounds.Height / line.TertiaryTextLayout.LineCount) * style.LyricsLineInnerSpacingFactor;
                    actualWidth = Math.Max(actualWidth, line.TertiaryTextLayout.LayoutBounds.Width);
                }

                line.PrimaryPosition = new Vector2((float)currentX, (float)currentY);
                if (line.PrimaryTextLayout != null)
                {
                    currentY += line.PrimaryTextLayout.LayoutBounds.Height;
                    actualWidth = Math.Max(actualWidth, line.PrimaryTextLayout.LayoutBounds.Width);
                }

                if (style.LyricsLineContentOrientation == LyricsLineContentOrientation.Horizontal)
                {
                    line.SecondaryPosition = new Vector2(
                        (float)(currentX + lyricsWidth / 2),
                        (float)(line.TertiaryPosition.Y + (currentY - line.TertiaryPosition.Y) / 2 - (line.SecondaryTextLayout?.LayoutBounds.Height ?? 0) / 2));
                    if (line.SecondaryTextLayout != null)
                    {
                        currentY = Math.Max(line.SecondaryPosition.Y + line.SecondaryTextLayout.LayoutBounds.Height, currentY);
                        actualWidth += line.SecondaryTextLayout.LayoutBounds.Width;
                    }
                }
                else
                {
                    currentY += (line.SecondaryTextLayout?.LayoutBounds.Height ?? 0) / (line.SecondaryTextLayout?.LineCount ?? 1) * Math.Max(style.LyricsLineInnerSpacingFactor, 0.2);
                    line.SecondaryPosition = new Vector2((float)currentX, (float)currentY);
                    if (line.SecondaryTextLayout != null)
                    {
                        currentY += line.SecondaryTextLayout.LayoutBounds.Height;
                        actualWidth = Math.Max(actualWidth, line.SecondaryTextLayout.LayoutBounds.Width);
                    }
                }

                line.BottomRightPosition = new Vector2((float)currentX + (float)actualWidth, (float)currentY);
                if (line.PrimaryTextLayout != null)
                    currentY += (line.PrimaryTextLayout.LayoutBounds.Height / line.PrimaryTextLayout.LineCount) * style.LyricsLineOverallSpacingFactor;

                line.TopLeftPosition = line.PrimaryTextLayout?.HorizontalAlignment switch
                {
                    CanvasHorizontalAlignment.Center => line.TopLeftPosition.AddX((float)((lyricsWidth - actualWidth) / 2)),
                    CanvasHorizontalAlignment.Right => line.TopLeftPosition.AddX((float)(lyricsWidth - actualWidth)),
                    _ => line.TopLeftPosition
                };
                line.BottomRightPosition = line.PrimaryTextLayout?.HorizontalAlignment switch
                {
                    CanvasHorizontalAlignment.Center => line.BottomRightPosition.AddX((float)((lyricsWidth - actualWidth) / 2)),
                    CanvasHorizontalAlignment.Right => line.BottomRightPosition.AddX((float)(lyricsWidth - actualWidth)),
                    _ => line.BottomRightPosition
                };

                double centerY = (line.TopLeftPosition.Y + line.BottomRightPosition.Y) / 2;
                line.CenterPosition = line.PrimaryTextLayout?.HorizontalAlignment switch
                {
                    CanvasHorizontalAlignment.Center => new Vector2((float)(lyricsWidth / 2), (float)centerY),
                    CanvasHorizontalAlignment.Right => new Vector2((float)lyricsWidth, (float)centerY),
                    _ => new Vector2(0, (float)centerY)
                };

                line.RecreateRenderChars(style.LyricsFontStrokeWidth);
            }
        }

        public static double? CalculateTargetScrollOffset(IList<RenderLyricsLine>? lines, int playingLineIndex)
        {
            if (lines == null || lines.Count == 0)
                return null;
            RenderLyricsLine? currentLine = lines.ElementAtOrDefault(playingLineIndex);
            if (currentLine?.PrimaryTextLayout == null)
                return null;
            return -currentLine.CenterPosition.Y;
        }

        public static (int Start, int End) CalculateVisibleRange(
            IList<RenderLyricsLine>? lines,
            double currentScrollOffset,
            double lyricsY,
            double lyricsHeight,
            double canvasHeight,
            double playingLineTopOffsetFactor)
        {
            if (lines == null || lines.Count == 0)
                return (-1, -1);

            double offset = currentScrollOffset + lyricsY + lyricsHeight * playingLineTopOffsetFactor;
            int start = FindFirstVisibleLine(lines, offset);
            int end = FindLastVisibleLine(lines, offset, canvasHeight);
            if (start != -1 && end == -1)
                end = lines.Count - 1;
            return (start, end);
        }

        public static (int Start, int End) CalculateMaxRange(IList<RenderLyricsLine>? lines)
        {
            if (lines == null || lines.Count == 0)
                return (-1, -1);
            return (0, lines.Count - 1);
        }

        public static void CalculateLanes(IList<RenderLyricsLine>? lines, int toleranceMs = 50)
        {
            if (lines == null)
                return;

            var lanesEndMs = new List<int> { 0 };
            foreach (RenderLyricsLine line in lines)
            {
                int assignedLane = -1;
                for (int i = 0; i < lanesEndMs.Count; i++)
                {
                    if (lanesEndMs[i] <= line.StartMs + toleranceMs)
                    {
                        assignedLane = i;
                        break;
                    }
                }
                if (assignedLane == -1)
                {
                    assignedLane = lanesEndMs.Count;
                    lanesEndMs.Add(0);
                }
                lanesEndMs[assignedLane] = line.EndMs ?? 0;
                line.LaneIndex = assignedLane;
            }
        }

        public static void CalculateAlignments(IList<RenderLyricsLine>? lines)
        {
            if (lines == null || lines.Count == 0)
                return;

            var uniqueAgents = lines
                .Where(l => !string.IsNullOrEmpty(l.AgentId))
                .Select(l => l.AgentId)
                .Distinct()
                .ToList();
            var alignmentMap = new Dictionary<string, TextAlignmentType>();
            for (int i = 0; i < uniqueAgents.Count; i++)
            {
                string agent = uniqueAgents[i];
                if (agent == "v1000" || agent.Contains("group", StringComparison.OrdinalIgnoreCase))
                    alignmentMap[agent] = TextAlignmentType.Center;
                else if (i == 0)
                    alignmentMap[agent] = TextAlignmentType.Left;
                else if (i == 1)
                    alignmentMap[agent] = TextAlignmentType.Right;
                else
                    alignmentMap[agent] = i % 2 == 0 ? TextAlignmentType.Left : TextAlignmentType.Right;
            }

            foreach (RenderLyricsLine line in lines)
            {
                line.HorizontalAlignmentType = !string.IsNullOrEmpty(line.AgentId) && alignmentMap.TryGetValue(line.AgentId, out TextAlignmentType alignment)
                    ? alignment
                    : null;
            }
        }

        private static int FindFirstVisibleLine(IList<RenderLyricsLine> lines, double offset)
        {
            int left = 0;
            int right = lines.Count - 1;
            int result = -1;
            while (left <= right)
            {
                int mid = (left + right) / 2;
                RenderLyricsLine line = lines[mid];
                if (line.PrimaryTextLayout == null)
                    break;
                double value = offset + line.BottomRightPosition.Y;
                if (value >= 0)
                {
                    result = mid;
                    right = mid - 1;
                }
                else
                {
                    left = mid + 1;
                }
            }
            return result;
        }

        private static int FindLastVisibleLine(IList<RenderLyricsLine> lines, double offset, double canvasHeight)
        {
            int left = 0;
            int right = lines.Count - 1;
            int result = -1;
            while (left <= right)
            {
                int mid = (left + right) / 2;
                RenderLyricsLine line = lines[mid];
                if (line.PrimaryTextLayout == null)
                    break;
                double value = offset + line.BottomRightPosition.Y;
                if (value >= canvasHeight)
                {
                    result = mid;
                    right = mid - 1;
                }
                else
                {
                    left = mid + 1;
                }
            }
            return result;
        }

        private static float CalculateBaseFontSize(double width, double height)
        {
            float usableWidth = (float)width * WidthPaddingRatio;
            float targetCharsPerLine;
            if (width < 500)
                targetCharsPerLine = 14f;
            else if (width > 1000)
                targetCharsPerLine = 30f;
            else
                targetCharsPerLine = 14f + 16f * (float)((width - 500) / 500f);
            float sizeByWidth = usableWidth / targetCharsPerLine;
            float sizeByHeight = (float)height / TargetMinVisibleLines;
            float minLimit = width < 400 ? 16f : BaseMinFontSize;
            return Math.Clamp(Math.Min(sizeByWidth, sizeByHeight), minLimit, BaseMaxFontSize);
        }
    }

    public sealed class BetterLyricsAnimator
    {
        private const double DefaultScale = 0.75;
        private const double HighlightedScale = 1.0;

        public void UpdateLines(
            IList<RenderLyricsLine>? lines,
            int startIndex,
            int endIndex,
            int primaryPlayingLineIndex,
            double lyricsWidth,
            double lyricsHeight,
            double targetYScrollOffset,
            double playingLineTopOffsetFactor,
            LyricsStyleSettings lyricsStyle,
            LyricsEffectSettings lyricsEffect,
            ValueTransition<double> canvasYScrollTransition,
            NowPlayingPalette albumArtThemeColors,
            TimeSpan elapsedTime,
            bool isMouseScrolling,
            bool isLayoutChanged,
            bool isPrimaryPlayingLineChanged,
            bool isMouseScrollingChanged,
            bool isArtThemeColorsChanged,
            double currentPositionMs)
        {
            if (lines == null || lines.Count == 0 || primaryPlayingLineIndex < 0 || primaryPlayingLineIndex >= lines.Count)
                return;

            RenderLyricsLine primaryPlayingLine = lines[primaryPlayingLineIndex];
            double phoneticOpacity = lyricsStyle.PhoneticLyricsOpacity / 100.0;
            double originalOpacity = lyricsStyle.UnplayedOriginalLyricsOpacity / 100.0;
            double translatedOpacity = lyricsStyle.TranslatedLyricsOpacity / 100.0;
            double topHeightFactor = lyricsHeight * playingLineTopOffsetFactor;
            double bottomHeightFactor = lyricsHeight * (1 - playingLineTopOffsetFactor);
            double scrollTopDurationSec = lyricsEffect.LyricsScrollTopDuration / 1000.0;
            double scrollTopDelaySec = lyricsEffect.LyricsScrollTopDelay / 1000.0;
            double scrollBottomDurationSec = lyricsEffect.LyricsScrollBottomDuration / 1000.0;
            double scrollBottomDelaySec = lyricsEffect.LyricsScrollBottomDelay / 1000.0;
            double canvasTransDuration = canvasYScrollTransition.DurationSeconds;
            bool isBlurEnabled = lyricsEffect.IsLyricsBlurEffectEnabled;
            bool isOutOfSightEnabled = lyricsEffect.IsLyricsOutOfSightEffectEnabled;
            bool isFanEnabled = lyricsEffect.IsFanLyricsEnabled;
            double fanAngleRad = Math.PI * (lyricsEffect.FanLyricsAngle / 180.0);
            bool isGlowEnabled = lyricsEffect.IsLyricsGlowEffectEnabled;
            bool isFloatEnabled = lyricsEffect.IsLyricsFloatAnimationEnabled;
            bool isScaleEnabled = lyricsEffect.IsLyricsScaleEffectEnabled;

            int safeStart = Math.Max(0, startIndex);
            int safeEnd = Math.Min(lines.Count - 1, endIndex + 1);
            for (int i = safeStart; i <= safeEnd; i++)
            {
                RenderLyricsLine line = lines[i];
                double? lineHeight = line.PrimaryLineHeight;
                if (lineHeight == null || lineHeight <= 0)
                    continue;

                bool isWordAnimationEnabled = lyricsEffect.WordByWordEffectMode switch
                {
                    WordByWordEffectMode.Auto => line.IsPrimaryHasRealSyllableInfo && line.PrimaryRenderChars.Count > 0,
                    WordByWordEffectMode.Always => line.PrimaryRenderChars.Count > 0,
                    WordByWordEffectMode.Never => false,
                    _ => line.PrimaryRenderChars.Count > 0
                };

                bool isSecondaryLinePlaying = line.GetIsPlaying(currentPositionMs);
                bool isSecondaryLinePlayingChanged = line.IsPlayingLastFrame != isSecondaryLinePlaying;
                line.IsPlayingLastFrame = isSecondaryLinePlaying;
                double playProgress = line.GetPlayProgress(currentPositionMs);
                double targetCharFloat = lyricsEffect.IsLyricsFloatAnimationAmountAutoAdjust
                    ? lineHeight.Value * 0.1
                    : lyricsEffect.LyricsFloatAnimationAmount;
                double targetCharGlow = lyricsEffect.IsLyricsGlowEffectAmountAutoAdjust
                    ? lineHeight.Value * 0.2
                    : lyricsEffect.LyricsGlowEffectAmount;
                double targetCharScale = lyricsEffect.IsLyricsScaleEffectAmountAutoAdjust
                    ? 1.15
                    : lyricsEffect.LyricsScaleEffectAmount / 100.0;
                double maxAnimationDurationMs = Math.Max((line.EndMs ?? 0) - currentPositionMs, 0);

                if (isLayoutChanged || isPrimaryPlayingLineChanged || isMouseScrollingChanged || isSecondaryLinePlayingChanged || isArtThemeColorsChanged)
                {
                    int lineCountDelta = i - primaryPlayingLineIndex;
                    double distanceFromPlayingLine = Math.Abs(line.TopLeftPosition.Y - primaryPlayingLine.TopLeftPosition.Y);
                    double distanceFactor = lineCountDelta < 0
                        ? Math.Clamp(distanceFromPlayingLine / Math.Max(1, topHeightFactor), 0, 1)
                        : Math.Clamp(distanceFromPlayingLine / Math.Max(1, bottomHeightFactor), 0, 1);

                    double yScrollDuration;
                    double yScrollDelay;
                    if (lineCountDelta < 0)
                    {
                        yScrollDuration = canvasTransDuration + distanceFactor * (scrollTopDurationSec - canvasTransDuration);
                        yScrollDelay = distanceFactor * scrollTopDelaySec;
                    }
                    else if (lineCountDelta == 0)
                    {
                        yScrollDuration = canvasTransDuration;
                        yScrollDelay = 0;
                    }
                    else
                    {
                        yScrollDuration = canvasTransDuration + distanceFactor * (scrollBottomDurationSec - canvasTransDuration);
                        yScrollDelay = distanceFactor * scrollBottomDelaySec;
                    }

                    line.BlurAmountTransition.SetDuration(yScrollDuration);
                    line.BlurAmountTransition.SetDelay(yScrollDelay);
                    line.BlurAmountTransition.Start((isMouseScrolling || isSecondaryLinePlaying) ? 0 : isBlurEnabled ? 4.6 * distanceFactor : 0);

                    line.ScaleTransition.SetDuration(yScrollDuration);
                    line.ScaleTransition.SetDelay(yScrollDelay);
                    line.ScaleTransition.Start(isSecondaryLinePlaying ? HighlightedScale :
                        isOutOfSightEnabled ? HighlightedScale - distanceFactor * (HighlightedScale - DefaultScale) : HighlightedScale);

                    line.TertiaryOpacityTransition.SetDuration(yScrollDuration);
                    line.TertiaryOpacityTransition.SetDelay(yScrollDelay);
                    line.TertiaryOpacityTransition.Start(isSecondaryLinePlaying ? phoneticOpacity : CalculateTargetOpacity(phoneticOpacity, phoneticOpacity, distanceFactor, isMouseScrolling, lyricsEffect));

                    line.PlayedPrimaryOpacityTransition.SetDuration(yScrollDuration);
                    line.PlayedPrimaryOpacityTransition.SetDelay(yScrollDelay);
                    line.PlayedPrimaryOpacityTransition.Start(isSecondaryLinePlaying ? 1.0 : CalculateTargetOpacity(originalOpacity, 1.0, distanceFactor, isMouseScrolling, lyricsEffect));

                    line.UnplayedPrimaryOpacityTransition.SetDuration(yScrollDuration);
                    line.UnplayedPrimaryOpacityTransition.SetDelay(yScrollDelay);
                    line.UnplayedPrimaryOpacityTransition.Start(isSecondaryLinePlaying ? originalOpacity : CalculateTargetOpacity(originalOpacity, originalOpacity, distanceFactor, isMouseScrolling, lyricsEffect));

                    line.SecondaryOpacityTransition.SetDuration(yScrollDuration);
                    line.SecondaryOpacityTransition.SetDelay(yScrollDelay);
                    line.SecondaryOpacityTransition.Start(isSecondaryLinePlaying ? translatedOpacity : CalculateTargetOpacity(translatedOpacity, translatedOpacity, distanceFactor, isMouseScrolling, lyricsEffect));

                    line.PlayedFillColorTransition.SetDuration(yScrollDuration);
                    line.PlayedFillColorTransition.SetDelay(yScrollDelay);
                    line.PlayedFillColorTransition.Start(isSecondaryLinePlaying ? albumArtThemeColors.PlayedCurrentLineFillColor : albumArtThemeColors.NonCurrentLineFillColor);
                    line.UnplayedFillColorTransition.SetDuration(yScrollDuration);
                    line.UnplayedFillColorTransition.SetDelay(yScrollDelay);
                    line.UnplayedFillColorTransition.Start(isSecondaryLinePlaying ? albumArtThemeColors.UnplayedCurrentLineFillColor : albumArtThemeColors.NonCurrentLineFillColor);
                    line.PlayedStrokeColorTransition.SetDuration(yScrollDuration);
                    line.PlayedStrokeColorTransition.SetDelay(yScrollDelay);
                    line.PlayedStrokeColorTransition.Start(isSecondaryLinePlaying ? albumArtThemeColors.PlayedTextStrokeColor : albumArtThemeColors.UnplayedTextStrokeColor);
                    line.UnplayedStrokeColorTransition.SetDuration(yScrollDuration);
                    line.UnplayedStrokeColorTransition.SetDelay(yScrollDelay);
                    line.UnplayedStrokeColorTransition.Start(albumArtThemeColors.UnplayedTextStrokeColor);

                    line.AngleTransition.SetInterpolator(canvasYScrollTransition.Interpolator);
                    line.AngleTransition.SetDuration(yScrollDuration);
                    line.AngleTransition.SetDelay(yScrollDelay);
                    line.AngleTransition.Start(isFanEnabled && !isMouseScrolling ? fanAngleRad * distanceFactor * (i > primaryPlayingLineIndex ? 1 : -1) : 0);

                    if (isLayoutChanged || isPrimaryPlayingLineChanged || isMouseScrollingChanged)
                    {
                        line.YOffsetTransition.SetInterpolator(canvasYScrollTransition.Interpolator);
                        line.YOffsetTransition.SetDuration(yScrollDuration);
                        line.YOffsetTransition.SetDelay(yScrollDelay);
                        if (isLayoutChanged)
                            line.YOffsetTransition.JumpTo(targetYScrollOffset);
                        else
                            line.YOffsetTransition.Start(targetYScrollOffset);
                    }
                }

                if (isWordAnimationEnabled)
                {
                    if (isSecondaryLinePlayingChanged)
                    {
                        if (isGlowEnabled &&
                            lyricsEffect.LyricsGlowEffectScope == LyricsEffectScope.LineStartToCurrentChar &&
                            isSecondaryLinePlaying)
                        {
                            foreach (RenderLyricsChar renderChar in line.PrimaryRenderChars)
                            {
                                double stepInOutDuration = Math.Min(LyricsMath.AnimationDuration.TotalMilliseconds, maxAnimationDurationMs) / 2.0 / 1000.0;
                                double stepLastingDuration = Math.Max(maxAnimationDurationMs / 1000.0 - stepInOutDuration * 2, 0);
                                renderChar.GlowTransition.Start(
                                    new Keyframe<double>(targetCharGlow, stepInOutDuration),
                                    new Keyframe<double>(targetCharGlow, stepLastingDuration),
                                    new Keyframe<double>(0, stepInOutDuration));
                            }
                        }

                        if (isFloatEnabled)
                        {
                            foreach (RenderLyricsChar renderChar in line.PrimaryRenderChars)
                            {
                                if (isSecondaryLinePlaying)
                                {
                                    if (renderChar.EndMs < currentPositionMs)
                                        renderChar.FloatTransition.JumpTo(0);
                                    else
                                        renderChar.FloatTransition.Start(targetCharFloat);
                                }
                                else
                                {
                                    renderChar.FloatTransition.Start(0);
                                }
                            }
                        }
                    }

                    foreach (RenderLyricsChar renderChar in line.PrimaryRenderChars)
                    {
                        renderChar.ProgressPlayed = renderChar.GetPlayProgress(currentPositionMs);
                        bool isCharPlaying = renderChar.GetIsPlaying(currentPositionMs);
                        bool isCharPlayingChanged = renderChar.IsPlayingLastFrame != isCharPlaying;
                        if (isCharPlayingChanged)
                        {
                            if (isFloatEnabled)
                            {
                                renderChar.FloatTransition.SetDurationMs(Math.Min(lyricsEffect.LyricsFloatAnimationDuration, maxAnimationDurationMs));
                                renderChar.FloatTransition.Start(0);
                            }
                            renderChar.IsPlayingLastFrame = isCharPlaying;
                        }
                        else if (!isCharPlaying && currentPositionMs > renderChar.EndMs && renderChar.FloatTransition.Value != 0)
                        {
                            renderChar.FloatTransition.SetDurationMs(Math.Min(lyricsEffect.LyricsFloatAnimationDuration, maxAnimationDurationMs));
                            renderChar.FloatTransition.Start(0);
                        }
                    }

                    foreach (RenderLyricsSyllable syllable in line.PrimaryRenderSyllables)
                    {
                        bool isSyllablePlaying = syllable.GetIsPlaying(currentPositionMs);
                        bool isSyllablePlayingChanged = syllable.IsPlayingLastFrame != isSyllablePlaying;
                        double desiredAnimationDurationMs = Math.Max((syllable.EndMs ?? 0) - currentPositionMs, 0);

                        if (isSyllablePlayingChanged)
                        {
                            if (isScaleEnabled && isSyllablePlaying)
                            {
                                foreach (RenderLyricsChar renderChar in syllable.ChildrenRenderLyricsChars)
                                {
                                    if (syllable.DurationMs >= lyricsEffect.LyricsScaleEffectLongSyllableDuration)
                                    {
                                        (double inDuration, double outDuration) = CalculateSegmentDuration(
                                            desiredAnimationDurationMs / 1000.0,
                                            maxAnimationDurationMs / 1000.0);
                                        renderChar.ScaleTransition.Start(
                                            new Keyframe<double>(targetCharScale, inDuration),
                                            new Keyframe<double>(1.0, outDuration));
                                    }
                                }
                            }

                            if (isGlowEnabled &&
                                isSyllablePlaying &&
                                lyricsEffect.LyricsGlowEffectScope == LyricsEffectScope.LongDurationSyllable &&
                                syllable.DurationMs >= lyricsEffect.LyricsGlowEffectLongSyllableDuration)
                            {
                                foreach (RenderLyricsChar renderChar in syllable.ChildrenRenderLyricsChars)
                                {
                                    (double inDuration, double outDuration) = CalculateSegmentDuration(
                                        desiredAnimationDurationMs / 1000.0,
                                        maxAnimationDurationMs / 1000.0);
                                    renderChar.GlowTransition.Start(
                                        new Keyframe<double>(targetCharGlow, inDuration),
                                        new Keyframe<double>(0, outDuration));
                                }
                            }

                            syllable.IsPlayingLastFrame = isSyllablePlaying;
                        }
                    }

                    foreach (RenderLyricsChar renderChar in line.PrimaryRenderChars)
                        renderChar.Update(elapsedTime);
                }

                if (!lyricsStyle.AutoWrap && isSecondaryLinePlaying)
                {
                    line.PrimaryXOffsetTransition.JumpTo(CalculateTargetXOffset(lyricsStyle.LyricsAlignmentType, line.PrimaryTextLayout?.LayoutBounds.Width ?? 0, lyricsWidth, playProgress));
                    line.SecondaryXOffsetTransition.JumpTo(CalculateTargetXOffset(lyricsStyle.LyricsAlignmentType, line.SecondaryTextLayout?.LayoutBounds.Width ?? 0, lyricsWidth, playProgress));
                    line.TertiaryXOffsetTransition.JumpTo(CalculateTargetXOffset(lyricsStyle.LyricsAlignmentType, line.TertiaryTextLayout?.LayoutBounds.Width ?? 0, lyricsWidth, playProgress));
                }

                line.Update(elapsedTime);
            }
        }

        private static double CalculateTargetOpacity(double baseOpacity, double baseOpacityWhenZeroDistanceFactor, double distanceFactor, bool isMouseScrolling, LyricsEffectSettings lyricsEffect)
        {
            if (distanceFactor == 0)
                return baseOpacityWhenZeroDistanceFactor;
            if (isMouseScrolling || !lyricsEffect.IsLyricsFadeOutEffectEnabled)
                return baseOpacity;
            return (1 - distanceFactor) * baseOpacity;
        }

        private static double CalculateTargetXOffset(TextAlignmentType textAlignmentType, double actualWidth, double lyricsWidth, double progress)
        {
            double offset = textAlignmentType switch
            {
                TextAlignmentType.Center => (lyricsWidth - actualWidth) / 2,
                TextAlignmentType.Right => lyricsWidth - actualWidth,
                _ => 0
            };
            offset = -Math.Min(0, offset);
            double progressStartToScroll = lyricsWidth * 0.5 / actualWidth;
            double progressEndToScroll = 1 - progressStartToScroll;
            return -Math.Max((Math.Min(progress, progressEndToScroll) - progressStartToScroll), 0) * actualWidth + offset;
        }

        private static (double InDuration, double OutDuration) CalculateSegmentDuration(double desiredDuration, double maxDuration)
        {
            if (desiredDuration <= 0 || maxDuration <= 0)
                return (0, 0);

            double inDuration = Math.Min(
                desiredDuration / 2,
                Math.Min(LyricsMath.AnimationDuration.TotalSeconds, maxDuration / 2));
            double outDuration = Math.Min(
                desiredDuration - inDuration,
                Math.Max(maxDuration - inDuration, 0));
            return (Math.Max(0, inDuration), Math.Max(0, outDuration));
        }
    }
}
