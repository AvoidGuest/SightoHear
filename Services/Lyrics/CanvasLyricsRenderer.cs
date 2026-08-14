using SightoHear.Helpers;
using SightoHear.Controls;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.UI;

namespace SightoHear.Services.Lyrics
{
    public sealed class CanvasLyricsRenderer : IDisposable
    {
        private static readonly Regex InlineTimeTagRegex = new(@"<\d+:\d+[\.:]\d+>", RegexOptions.Compiled);
        private const double CurrentLineAnchor = 0.50;
        private const double HorizontalPadding = 4;
        private const double LineInnerSpacingFactorCJK = 0.08;
        private const double LineOverallSpacingFactorCJK = 0.56;
        private const double LineInnerSpacingFactorLatin = 0.05;
        private const double LineOverallSpacingFactorLatin = 0.40;
        private const double CurrentScale = 1.0;
        private const double NormalScale = 0.75;
        private const float MinPrimarySize = 36f;
        private const float MaxPrimarySize = 50f;
        private const double CurrentPlayedOpacity = 1.0;
        private const double CurrentUnplayedOpacity = 0.30;
        private const double SecondaryOpacity = 0.60;
        private const double NonCurrentOpacity = 0.45;
        private const double ScrollDurationSeconds = 0.30;
        private const double VisualDurationSeconds = 0.18;
        private const double LyricsVisualLeadMs = 220;
        private const double FloatAmplitude = 4.0;
        private const double WheelScrollFactor = 0.72;
        private const double ManualScrollHoldMs = 1600;
        private const double AutoFollowResumeDistance = 10;
        private const double ManualBrowseOpacity = 0.56;

        private readonly object _lineHitsLock = new();
        // ★ 歌词渲染锁：保护 _renderLines 的遍历与重建。渲染线程（Win2D Update/Draw 回调）
        //   与主线程（SetLyrics/SetPlaceholder/Dispose 重建歌词）并发访问同一列表，
        //   若不加锁会导致 "Collection was modified" 使当帧渲染中断（卡顿）。
        private readonly object _renderLinesLock = new();
        private readonly List<LineLayoutHit> _lineHits = [];
        private volatile List<LineLayoutHit>? _completedLineHits;
        private readonly DoubleTransition _scrollTransition = new(0, ScrollDurationSeconds);
        private readonly DoubleTransition _manualScrollTransition = new(0, ScrollDurationSeconds);
        private List<LyricsLine>? _lines;
        private List<RenderLineState> _renderLines = [];
        private string _placeholder = "No lyrics";
        private double _lyricsX;
        private double _lyricsY;
        private double _lyricsWidth = 1;
        private double _lyricsHeight = 1;
        private double _lastArrangeWidth = -1;
        private double _totalFlowHeight;
        private float _primarySize = 40f;
        private float _secondarySize = 30f;
        private long _manualScrollUntilTick;
        private bool _isManualScrolling;
        private double _manualBrowseVisibility;
        private int _currentLineIndex;
        private int _lastCurrentLineIndex = -1;
        private volatile int _hoverLineIndex = -1;  // written on UI thread (PointerMoved), read on render thread (DrawLine)
        private bool _layoutDirty = true;
        private bool _hasAnyCJKText;
        private bool _loggedLoadedStats;

        // ★ 缓存的预分配数组（纯托管，跨帧复用安全）与 List
        private CanvasGradientStop[] _cachedGradientStops = new CanvasGradientStop[4];
        // ★ 修复：空歌词占位文本的文本格式缓存。
        //   DrawPlaceholder 在无歌词状态下每帧调用，若每帧 new CanvasTextFormat
        //   会持续产生托管/COM 对象分配（GC 压力 → 渲染线程抖动）。
        //   格式固定，缓存为成员后跨帧复用，Dispose 时统一释放。
        private CanvasTextFormat? _placeholderFormat;
        // ★ 缓存的 List，用于多行歌词路径（避免每帧分配临时 List）
        private readonly List<(int startChar, int length, double totalWidth, Rect bounds)> _cachedLineInfos = new();
        private readonly List<(double minY, double maxY, int minChar, int maxChar, double totalWidth, double minX, double maxX)> _cachedLineYGroups = new();
        private readonly List<KeyValuePair<int, CanvasTextLayoutRegion>> _cachedCharRegions = new();
        private readonly List<KeyValuePair<int, TimedChar>> _cachedTimedChars = new();

        public double CurrentProgressMs { get; set; }

        /// <summary>
        /// 用户手动调整的歌词延迟（毫秒，可正可负）：
        /// 正值 = 歌词相对声音延后显示（歌词偏快时使用，高亮回退到更早的行），
        /// 负值 = 歌词相对声音提前显示（歌词偏慢时使用，高亮前进到更晚的行）。
        /// 叠加在 <see cref="CurrentProgressMs"/> 之上参与行切换与逐字进度计算，
        /// 因此只需渲染器内部统一偏移，页面侧无需感知。
        /// 使用 volatile 字段保证 UI 线程写入后渲染线程立即可见（避免 Release 优化读到旧值）。
        /// 延迟值由 UI 以整数毫秒设置，int 存储无损（volatile 不支持 double）。
        /// </summary>
        private volatile int _userDelayMs;

        public double UserDelayMs
        {
            get => _userDelayMs;
            set => _userDelayMs = (int)value;
        }

        /// <summary>歌词渲染统一使用的时间轴（播放位置 + 视觉提前量 − 用户延迟）。
        /// 减号语义：加延迟（正值）→ 时间轴回退 → 高亮更早的行；减延迟（负值）→ 时间轴前进。</summary>
        private double LyricsTimeMs => Math.Max(0, CurrentProgressMs + LyricsVisualLeadMs - UserDelayMs);

        /// <summary>
        /// 跳转到指定歌词行（点击歌词 / 拖动进度条时调用）：
        /// 目标 <paramref name="lyricsStartMs"/> 是歌词原始时间戳，而渲染时间轴包含
        /// 视觉提前量与用户延迟，因此需要反向补偿，让跳转瞬间恰好显示目标行，
        /// 避免正延迟时跳转后立刻切到下一行。
        /// </summary>
        public void JumpToLyricsTime(double lyricsStartMs)
        {
            CurrentProgressMs = Math.Max(0, lyricsStartMs - LyricsVisualLeadMs + UserDelayMs);
        }

        public void SetPlaceholder(string text)
        {
            ClearRenderLines();
            _lines = null;
            _placeholder = string.IsNullOrWhiteSpace(text) ? "No lyrics" : text;
            _currentLineIndex = 0;
            _lastCurrentLineIndex = -1;
            _hoverLineIndex = -1;
            _scrollTransition.JumpTo(0);
            _manualScrollTransition.JumpTo(0);
            _manualScrollUntilTick = 0;
            _isManualScrolling = false;
            _manualBrowseVisibility = 0;
            lock (_lineHitsLock)
            {
                _lineHits.Clear();
                _completedLineHits = null;
            }
            _loggedLoadedStats = false;
        }

        public void SetLyrics(IEnumerable<LyricsLine> lines)
        {
            ClearRenderLines();

            List<LyricsLine> ordered = lines
                .Where(line => !string.IsNullOrWhiteSpace(line.PrimaryText))
                .OrderBy(line => line.StartMs)
                .ToList();

            // ★ 先构建完整列表再整体替换引用：渲染线程 EnsureLayout 会遍历 _lines，
            //   若就地 Add 会导致 "Collection was modified"（切歌/歌词切换时卡顿）。
            List<LyricsLine> newLines = [];
            for (int i = 0; i < ordered.Count; i++)
                newLines.Add(NormalizeLine(ordered[i]));
            FillMissingLineEndTimes(newLines);
            _lines = newLines;

            // Pre-detect if ANY line has CJK text (for optimization)
            _hasAnyCJKText = _lines.Any(line => IsCJKText(line.PrimaryText));

            _currentLineIndex = 0;
            _lastCurrentLineIndex = -1;
            _hoverLineIndex = -1;
            _scrollTransition.JumpTo(0);
            _manualScrollTransition.JumpTo(0);
            _manualScrollUntilTick = 0;
            _isManualScrolling = false;
            _manualBrowseVisibility = 0;
            _layoutDirty = true;
            lock (_lineHitsLock)
            {
                _lineHits.Clear();
                _completedLineHits = null;
            }
            _loggedLoadedStats = false;

            LogLoadedLyrics();
        }

        public void Update(IRenderSurface surface, TimeSpan elapsedTime, double x, double y, double width, double height)
        {
            UpdateBounds(x, y, width, height);

            if (_lines == null || _lines.Count == 0)
                return;

            EnsureLayout(surface);
            UpdateCurrentLine();
            UpdateScrollTarget(isLayoutPass: false);
            UpdateManualScroll(elapsedTime.TotalSeconds);
            UpdateManualBrowseVisibility(elapsedTime.TotalSeconds);
            UpdateLineVisualTargets();

            _scrollTransition.Update(elapsedTime);
            _manualScrollTransition.Update(elapsedTime);
            lock (_renderLinesLock)
            {
                foreach (RenderLineState line in _renderLines)
                    line.Update(elapsedTime);
            }

            UpdateCharFloatAnimations();
        }

        private void UpdateCharFloatAnimations()
        {
            double timeMs = LyricsTimeMs;

            // ★ 加锁：主线程 SetLyrics 可能在渲染线程遍历期间重建 _renderLines
            lock (_renderLinesLock)
            {
                foreach (RenderLineState line in _renderLines)
                {
                    line.AnimationManager.Update(timeMs);
                }
            }
        }

        public void Draw(IRenderSurface surface, CanvasDrawingSession ds, double x, double y, double width, double height, double currentProgressMs)
        {
            System.Numerics.Matrix3x2 rootTransform = ds.Transform;
            ds.Transform = System.Numerics.Matrix3x2.Identity;

            UpdateBounds(x, y, width, height);
            lock (_lineHitsLock)
                _lineHits.Clear();

            try
            {
                if (_lines == null || _lines.Count == 0)
                {
                    DrawPlaceholder(ds);
                    return;
                }

                // Update 中已执行 EnsureLayout/UpdateCurrentLine/UpdateScrollTarget，
                // 这里仅处理 Update 之后才触发的 _layoutDirty（极低概率）
                if (_layoutDirty)
                    EnsureLayout(surface);

                double timeMs = currentProgressMs - UserDelayMs;
                double yOffsetBase = _lyricsY + _lyricsHeight * CurrentLineAnchor + _scrollTransition.Value + _manualScrollTransition.Value;
                double clipTop = _lyricsY - 180;
                double clipBottom = _lyricsY + _lyricsHeight + 220;

                // ★ 加锁：主线程 SetLyrics 可能在渲染线程绘制期间重建 _renderLines
                lock (_renderLinesLock)
                {
                    for (int i = 0; i < _renderLines.Count; i++)
                    {
                        RenderLineState line = _renderLines[i];
                        double lineTop = yOffsetBase + line.FlowTop;
                        double lineBottom = yOffsetBase + line.FlowBottom;
                        if (lineBottom < clipTop)
                            continue;

                        if (lineTop > clipBottom)
                            break;

                        if (lineBottom < _lyricsY - 120 || lineTop > _lyricsY + _lyricsHeight + 160)
                            continue;

                        // Use row-index distance with cap — symmetric and ensures visible blur
                        int lineDist = Math.Abs(i - _currentLineIndex);
                        double cappedDist = Math.Min(lineDist, 5); // cap at 5 rows so blur stays visible
                        double distanceFactor = cappedDist / 5.0;
                        double blurFactor = Math.Pow(distanceFactor, 1.5);
                        // Boost immediate neighbor blur
                        if (lineDist == 1) blurFactor *= 1.6;

                        double edgeFade = 1.0;
                        double blurAmount = _isManualScrolling ? 0.0 : blurFactor * 8.5;

                        DrawLine(ds, line, i, lineTop, i == _currentLineIndex, timeMs, edgeFade, blurAmount);
                    }
                }
            }
            finally
            {
                lock (_lineHitsLock)
                {
                    _completedLineHits = _lineHits.Count > 0
                        ? new List<LineLayoutHit>(_lineHits)
                        : null;
                }
                ds.Transform = rootTransform;
            }
        }

        /// All lines are fully opaque (1.0) — blur provides the edge visual effect.
        private double GetEdgeFadeOpacity(double lineTop, double lineBottom, double centerOffset, double fadeRange)
        {
            return 1.0;
        }

        public void ScrollBy(double deltaY)
        {
            if (_lines == null || _lines.Count == 0)
                return;

            EnsureManualScrollStartsFromCurrentView();
            double target = Math.Clamp(
                _manualScrollTransition.TargetValue + deltaY * WheelScrollFactor,
                GetMinManualScrollOffset(),
                GetMaxManualScrollOffset());
            _manualScrollTransition.Start(target);
            _manualScrollUntilTick = Environment.TickCount64 + (long)ManualScrollHoldMs;
            _isManualScrolling = true;
            _manualBrowseVisibility = Math.Max(_manualBrowseVisibility, 0.72);
        }

        public void ResumeAutoFollow(bool animated = true)
        {
            double previousCombinedOffset = _scrollTransition.Value + _manualScrollTransition.Value;
            _manualScrollUntilTick = 0;
            _isManualScrolling = false;
            UpdateCurrentLine();

            if (animated)
            {
                double targetScroll;
                lock (_renderLinesLock)
                {
                    if (_renderLines.Count == 0 || _currentLineIndex < 0 || _currentLineIndex >= _renderLines.Count)
                        targetScroll = double.NaN;
                    else
                        targetScroll = -_renderLines[_currentLineIndex].PrimaryCenterY;
                }
                if (double.IsNaN(targetScroll))
                {
                    _manualScrollTransition.JumpTo(0);
                    _manualBrowseVisibility = 0;
                    _lastCurrentLineIndex = -1;
                    UpdateScrollTarget(isLayoutPass: true);
                    UpdateLineVisualTargets(force: true);
                    return;
                }
                double carryOffset = previousCombinedOffset - targetScroll;
                _scrollTransition.JumpTo(targetScroll);
                _manualScrollTransition.JumpTo(carryOffset);
                _manualScrollTransition.Start(0);
                _lastCurrentLineIndex = _currentLineIndex;
                UpdateLineVisualTargets(force: false);
            }
            else
            {
                _manualScrollTransition.JumpTo(0);
                _manualBrowseVisibility = 0;
                _lastCurrentLineIndex = -1;
                UpdateScrollTarget(isLayoutPass: true);
                UpdateLineVisualTargets(force: true);
            }
        }

        public bool TryGetLineStartAt(Point canvasPoint, out TimeSpan start)
        {
            start = TimeSpan.Zero;
            _hoverLineIndex = -1;

            // Read from the previous frame's stable hit data to avoid cursor jitter
            List<LineLayoutHit>? snapshot;
            lock (_lineHitsLock)
            {
                snapshot = _completedLineHits;
            }
            if (snapshot == null)
                return false;

            foreach (LineLayoutHit hit in snapshot)
            {
                if (!hit.Bounds.Contains(canvasPoint))
                    continue;

                _hoverLineIndex = hit.Index;
                start = TimeSpan.FromMilliseconds(Math.Max(0, hit.StartMs));
                return true;
            }

            return false;
        }

        public void ClearHoverLine() => _hoverLineIndex = -1;

        public void Dispose()
        {
            ClearRenderLines();
            lock (_lineHitsLock)
            {
                _lineHits.Clear();
                _completedLineHits = null;
            }
            // ★ 修复：释放缓存的占位文本格式（CanvasTextFormat 实现 IDisposable）
            _placeholderFormat?.Dispose();
            _placeholderFormat = null;
        }

        private void UpdateBounds(double x, double y, double width, double height)
        {
            _lyricsX = x;
            _lyricsY = y;
            _lyricsWidth = Math.Max(220, width);
            _lyricsHeight = Math.Max(1, height);
            float primarySize = CalculatePrimarySize(_lyricsWidth, _lyricsHeight);
            float secondarySize = MathF.Round(primarySize * 0.75f);

            if (Math.Abs(_lastArrangeWidth - _lyricsWidth) > 0.5 ||
                Math.Abs(_primarySize - primarySize) > 0.5 ||
                Math.Abs(_secondarySize - secondarySize) > 0.5)
            {
                _lastArrangeWidth = _lyricsWidth;
                _primarySize = primarySize;
                _secondarySize = secondarySize;
                _layoutDirty = true;
            }
        }

        private void EnsureLayout(IRenderSurface surface)
        {
            if (!_layoutDirty || _lines == null)
                return;

            ClearRenderLines();
            double flowY = 0;
            double textWidth = Math.Max(1, _lyricsWidth - HorizontalPadding * 2);

            foreach (LyricsLine line in _lines)
            {
                RenderLineState state = new(line);
                state.PrimaryLayout = CreateTextLayout(surface, line.PrimaryText, _primarySize, textWidth);
                state.PrimaryFontSize = _primarySize;
                state.PrimaryRegions = line.PrimaryText.Length > 0
                    ? state.PrimaryLayout.GetCharacterRegions(0, line.PrimaryText.Length)
                    : [];

                if (!string.IsNullOrWhiteSpace(line.SecondaryText))
                    state.SecondaryLayout = CreateTextLayout(surface, line.SecondaryText, _secondarySize, textWidth);

                if (!string.IsNullOrWhiteSpace(line.TertiaryText))
                    state.TertiaryLayout = CreateTextLayout(surface, line.TertiaryText, _secondarySize, textWidth);

                state.BuildTimedChars();

                // For multi-line text, use single line height for spacing calculation
                double primaryHeight = Math.Max(state.PrimaryLayout.LayoutBounds.Height, _primarySize + 12);
                int lineCount = Math.Max(1, state.PrimaryLayout.LineCount);
                double singleLineHeight = primaryHeight / lineCount;

                // Detect language for primary and secondary text independently
                bool primaryIsCJK = IsCJKText(line.PrimaryText);
                bool secondaryIsCJK = !string.IsNullOrWhiteSpace(line.SecondaryText) && IsCJKText(line.SecondaryText);

                // Use single line height for spacing factor calculation
                double innerSpacing = singleLineHeight * (primaryIsCJK || secondaryIsCJK ? LineInnerSpacingFactorCJK : LineInnerSpacingFactorLatin);
                double overallSpacing = singleLineHeight * (primaryIsCJK ? LineOverallSpacingFactorCJK : LineOverallSpacingFactorLatin);
                double secondaryHeight = state.SecondaryLayout?.LayoutBounds.Height ?? 0;
                double tertiaryHeight = state.TertiaryLayout?.LayoutBounds.Height ?? 0;

                state.FlowTop = flowY;
                state.PrimaryTop = flowY;
                state.PrimaryCenterY = state.PrimaryTop + state.PrimaryLayout.LayoutBounds.Y + primaryHeight * 0.5;
                state.SecondaryTop = state.PrimaryTop + primaryHeight + innerSpacing;
                state.TertiaryTop = state.SecondaryLayout is not null
                    ? state.SecondaryTop + secondaryHeight + innerSpacing * 0.6
                    : state.PrimaryTop + primaryHeight + innerSpacing;
                state.VisualBottom = state.TertiaryLayout is not null
                    ? state.TertiaryTop + tertiaryHeight
                    : state.SecondaryLayout is not null
                        ? state.SecondaryTop + secondaryHeight
                        : state.PrimaryTop + primaryHeight;
                state.FlowBottom = state.TertiaryLayout is not null
                    ? state.TertiaryTop + tertiaryHeight + overallSpacing
                    : state.SecondaryLayout is not null
                        ? state.SecondaryTop + secondaryHeight + overallSpacing
                        : state.PrimaryTop + primaryHeight + overallSpacing;

                // ★ 加锁：主线程 SetLyrics/SetPlaceholder 可能同时 ClearRenderLines
                lock (_renderLinesLock)
                    _renderLines.Add(state);
                flowY = state.FlowBottom;
            }

            _totalFlowHeight = flowY;
            _layoutDirty = false;
            UpdateCurrentLine();
            UpdateScrollTarget(isLayoutPass: true);
            UpdateLineVisualTargets(force: true);
        }

        private void UpdateCurrentLine()
        {
            _currentLineIndex = FindCurrentLineIndex(LyricsTimeMs);
        }

        private void UpdateScrollTarget(bool isLayoutPass)
        {
            double target;
            lock (_renderLinesLock)
            {
                if (_renderLines.Count == 0 || _currentLineIndex < 0 || _currentLineIndex >= _renderLines.Count)
                    return;
                target = -_renderLines[_currentLineIndex].PrimaryCenterY;
            }
            if (isLayoutPass || _lastCurrentLineIndex < 0)
            {
                _scrollTransition.JumpTo(target);
            }
            else if (_lastCurrentLineIndex != _currentLineIndex)
            {
                _scrollTransition.Start(target);
                if (_isManualScrolling &&
                    Environment.TickCount64 > _manualScrollUntilTick &&
                    Math.Abs(_manualScrollTransition.TargetValue) <= AutoFollowResumeDistance)
                {
                    _isManualScrolling = false;
                    _manualScrollTransition.Start(0);
                }
            }

            _lastCurrentLineIndex = _currentLineIndex;
        }

        private void UpdateLineVisualTargets(bool force = false)
        {
            lock (_renderLinesLock)
            {
                if (_renderLines.Count == 0 || _currentLineIndex < 0 || _currentLineIndex >= _renderLines.Count)
                    return;

                double currentCenter = _renderLines[_currentLineIndex].PrimaryCenterY;
                double topRange = Math.Max(1, _lyricsHeight * CurrentLineAnchor);
                double bottomRange = Math.Max(1, _lyricsHeight * (1 - CurrentLineAnchor));

                for (int i = 0; i < _renderLines.Count; i++)
                {
                    RenderLineState line = _renderLines[i];
                    double distance = Math.Abs(line.PrimaryCenterY - currentCenter);
                    double range = i < _currentLineIndex ? topRange : bottomRange;
                    double distanceFactor = Math.Clamp(distance / range, 0, 1);
                    bool isCurrent = i == _currentLineIndex;

                    double fade = 1 - distanceFactor;
                    double browse = _manualBrowseVisibility;
                    double browseOpacity = ManualBrowseOpacity * (0.82 + 0.18 * (1 - Math.Min(distanceFactor, 1)));
                    double playedOpacity = isCurrent
                        ? CurrentPlayedOpacity
                        : Lerp(NonCurrentOpacity * fade, browseOpacity, browse);
                    double unplayedOpacity = isCurrent
                        ? CurrentUnplayedOpacity
                    : Lerp(NonCurrentOpacity * fade, browseOpacity, browse);
                double secondaryOpacity = isCurrent
                    ? SecondaryOpacity
                    : Lerp(SecondaryOpacity * NonCurrentOpacity * fade, browseOpacity * 0.66, browse);
                double tertiaryOpacity = isCurrent
                    ? SecondaryOpacity * 0.85
                    : Lerp(SecondaryOpacity * NonCurrentOpacity * fade * 0.85, browseOpacity * 0.55, browse);
                double scale = isCurrent
                    ? CurrentScale
                    : Lerp(CurrentScale - distanceFactor * (CurrentScale - NormalScale), 0.88, browse);

                line.StartVisualTargets(scale, playedOpacity, unplayedOpacity, secondaryOpacity, tertiaryOpacity, force);
                }
            }
        }

        private void DrawLine(
            CanvasDrawingSession ds,
            RenderLineState renderLine,
            int index,
            double y,
            bool isCurrent,
            double timeMs,
            double edgeFade,
            double blurAmount)
        {
            // No blur → draw directly without CommandList overhead
            if (blurAmount <= 0.01)
            {
                DrawLineDirect(ds, renderLine, index, y, isCurrent, timeMs, edgeFade);
                return;
            }

            double x = _lyricsX + HorizontalPadding;
            double centerX = x;
            double centerY = y + renderLine.PrimaryCenterY - renderLine.FlowTop;
            System.Numerics.Matrix3x2 oldTransform = ds.Transform;
            ds.Transform *= System.Numerics.Matrix3x2.CreateScale((float)renderLine.Scale.Value, new System.Numerics.Vector2((float)centerX, (float)centerY));

            Rect hitBounds = new(_lyricsX, y - 8, _lyricsWidth, renderLine.VisualBottom - renderLine.FlowTop + 16);
            if (index == _hoverLineIndex)
                ds.FillRoundedRectangle(hitBounds.Extend(8, 4), 8, 8, Color.FromArgb(16, 255, 255, 255));

            bool lineHasEnded = renderLine.Source.EndMs.HasValue && timeMs >= renderLine.Source.EndMs.Value;
            bool shouldSweep = isCurrent || lineHasEnded;

            bool needsBlur = blurAmount > 0.01;

            if (needsBlur)
            {
                using CanvasCommandList primaryCL = new((ICanvasResourceCreator)ds);
                using (CanvasDrawingSession pds = primaryCL.CreateDrawingSession())
                {
                    if (shouldSweep)
                        DrawCurrentPrimary((ICanvasResourceCreator)pds, pds, renderLine, x, y + renderLine.PrimaryTop - renderLine.FlowTop, timeMs, edgeFade);
                    else
                        DrawNonCurrentPrimary(pds, renderLine, x, y + renderLine.PrimaryTop - renderLine.FlowTop, edgeFade);
                }
                using GaussianBlurEffect blur = new()
                {
                    Source = primaryCL,
                    BlurAmount = (float)blurAmount,
                    BorderMode = EffectBorderMode.Soft
                };
                ds.DrawImage(blur);
            }
            else
            {
                if (shouldSweep)
                    DrawCurrentPrimary((ICanvasResourceCreator)ds, ds, renderLine, x, y + renderLine.PrimaryTop - renderLine.FlowTop, timeMs, edgeFade);
                else
                    DrawNonCurrentPrimary(ds, renderLine, x, y + renderLine.PrimaryTop - renderLine.FlowTop, edgeFade);
            }

            // Secondary text with edgeFade opacity and optional blur
            if (renderLine.SecondaryLayout is not null)
            {
                double secondaryOpacity = Math.Min(1.0, renderLine.SecondaryOpacity.Value * edgeFade);
                if (secondaryOpacity > 0.005)
                {
                    if (needsBlur)
                    {
                        using CanvasCommandList secCL = new((ICanvasResourceCreator)ds);
                        using (CanvasDrawingSession sds = secCL.CreateDrawingSession())
                        {
                            Color secondaryColor = WhiteWithOpacity(secondaryOpacity);
                            sds.DrawTextLayout(renderLine.SecondaryLayout, (float)x, (float)(y + renderLine.SecondaryTop - renderLine.FlowTop), secondaryColor);
                        }
                        using GaussianBlurEffect secBlur = new()
                        {
                            Source = secCL,
                            BlurAmount = (float)blurAmount,
                            BorderMode = EffectBorderMode.Soft
                        };
                        ds.DrawImage(secBlur);
                    }
                    else
                    {
                        Color secondaryColor = WhiteWithOpacity(secondaryOpacity);
                        ds.DrawTextLayout(renderLine.SecondaryLayout, (float)x, (float)(y + renderLine.SecondaryTop - renderLine.FlowTop), secondaryColor);
                    }
                }
            }

            // Tertiary text（第三级文本，例如第二种翻译）with edgeFade opacity and optional blur
            if (renderLine.TertiaryLayout is not null)
            {
                double tertiaryOpacity = Math.Min(1.0, renderLine.TertiaryOpacity.Value * edgeFade);
                if (tertiaryOpacity > 0.005)
                {
                    if (needsBlur)
                    {
                        using CanvasCommandList terCL = new((ICanvasResourceCreator)ds);
                        using (CanvasDrawingSession tds = terCL.CreateDrawingSession())
                        {
                            Color tertiaryColor = WhiteWithOpacity(tertiaryOpacity);
                            tds.DrawTextLayout(renderLine.TertiaryLayout, (float)x, (float)(y + renderLine.TertiaryTop - renderLine.FlowTop), tertiaryColor);
                        }
                        using GaussianBlurEffect terBlur = new()
                        {
                            Source = terCL,
                            BlurAmount = (float)blurAmount,
                            BorderMode = EffectBorderMode.Soft
                        };
                        ds.DrawImage(terBlur);
                    }
                    else
                    {
                        Color tertiaryColor = WhiteWithOpacity(tertiaryOpacity);
                        ds.DrawTextLayout(renderLine.TertiaryLayout, (float)x, (float)(y + renderLine.TertiaryTop - renderLine.FlowTop), tertiaryColor);
                    }
                }
            }

            ds.Transform = oldTransform;

            lock (_lineHitsLock)
                _lineHits.Add(new LineLayoutHit(index, renderLine.Source.StartMs, hitBounds));
        }

        private void DrawLineDirect(
            CanvasDrawingSession ds,
            RenderLineState renderLine,
            int index,
            double y,
            bool isCurrent,
            double timeMs,
            double edgeFade)
        {
            // Direct draw without any blur or edge fade — used during manual scrolling
            double x = _lyricsX + HorizontalPadding;
            double centerX = x;
            double centerY = y + renderLine.PrimaryCenterY - renderLine.FlowTop;
            System.Numerics.Matrix3x2 oldTransform = ds.Transform;
            ds.Transform *= System.Numerics.Matrix3x2.CreateScale((float)renderLine.Scale.Value, new System.Numerics.Vector2((float)centerX, (float)centerY));

            Rect hitBounds = new(_lyricsX, y - 8, _lyricsWidth, renderLine.VisualBottom - renderLine.FlowTop + 16);
            if (index == _hoverLineIndex)
                ds.FillRoundedRectangle(hitBounds.Extend(8, 4), 8, 8, Color.FromArgb(16, 255, 255, 255));

            bool lineHasEnded = renderLine.Source.EndMs.HasValue && timeMs >= renderLine.Source.EndMs.Value;
            bool shouldSweep = isCurrent || lineHasEnded;

            if (shouldSweep)
                DrawCurrentPrimary((ICanvasResourceCreator)ds, ds, renderLine, x, y + renderLine.PrimaryTop - renderLine.FlowTop, timeMs, edgeFade);
            else
                DrawNonCurrentPrimary(ds, renderLine, x, y + renderLine.PrimaryTop - renderLine.FlowTop, edgeFade);

            if (renderLine.SecondaryLayout is not null)
            {
                Color secondaryColor = WhiteWithOpacity(renderLine.SecondaryOpacity.Value);
                ds.DrawTextLayout(renderLine.SecondaryLayout, (float)x, (float)(y + renderLine.SecondaryTop - renderLine.FlowTop), secondaryColor);
            }

            if (renderLine.TertiaryLayout is not null)
            {
                Color tertiaryColor = WhiteWithOpacity(renderLine.TertiaryOpacity.Value);
                ds.DrawTextLayout(renderLine.TertiaryLayout, (float)x, (float)(y + renderLine.TertiaryTop - renderLine.FlowTop), tertiaryColor);
            }

            ds.Transform = oldTransform;

            lock (_lineHitsLock)
                _lineHits.Add(new LineLayoutHit(index, renderLine.Source.StartMs, hitBounds));
        }

        private static double CalculateLineTotalSweptWidth(RenderLineState line, double timeMs)
        {
            double totalWidth = 0;
            for (int i = 0; i < line.Source.PrimaryText.Length; i++)
            {
                if (!line.TimedChars.TryGetValue(i, out TimedChar tc))
                    continue;

                double progress = tc.GetPlayProgress(timeMs);
                if (progress >= 1)
                {
                    totalWidth += tc.Width;
                }
                else if (progress > 0)
                {
                    totalWidth += tc.Width * progress;
                    break; // 当前字符 — 停止，之后的字符未播放
                }
                else
                {
                    break; // 未播放，停止
                }
            }
            return totalWidth;
        }

        private void DrawCurrentPrimary(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds, RenderLineState line, double x, double y, double timeMs, double edgeFade)
        {
            if (!HasRealTimedSyllables(line.Source))
            {
                ds.DrawTextLayout(line.PrimaryLayout, (float)x, (float)y, WhiteWithOpacity(line.PlayedOpacity.Value * edgeFade));
                return;
            }

            Color playedColor = WhiteWithOpacity(line.PlayedOpacity.Value * edgeFade);
            Color unplayedColor = WhiteWithOpacity(line.UnplayedOpacity.Value * edgeFade);

            var layout = line.PrimaryLayout;
            int lineCount = layout.LineCount;

            // 单行 - 沿用原有高效逻辑
            if (lineCount <= 1)
            {
                DrawSingleLineCurrentPrimary(resourceCreator, ds, line, x, y, timeMs, edgeFade, playedColor, unplayedColor);
                return;
            }

            // ---- 多行：使用 CharacterRegions 的 Y 坐标精确分割 ----
            var charRegions = line.CharacterRegions;
            if (charRegions == null || charRegions.Count == 0)
            {
                // 降级保护：直接绘制整段文本（无扫色）
                ds.DrawTextLayout(layout, (float)x, (float)y, playedColor);
                return;
            }

            // 1. 按 Y 坐标分组，构建每行的字符范围与物理边界
            _cachedLineInfos.Clear();
            _cachedLineYGroups.Clear();

            // ★ 用排序替代 OrderBy().ThenBy().ToList()，避免分配迭代器 + 新 List
            _cachedCharRegions.Clear();
            foreach (var kv in charRegions)
                _cachedCharRegions.Add(kv);
            _cachedCharRegions.Sort((a, b) =>
            {
                int yCmp = a.Value.LayoutBounds.Y.CompareTo(b.Value.LayoutBounds.Y);
                return yCmp != 0 ? yCmp : a.Key.CompareTo(b.Key);
            });

            const double yTolerance = 2.0;
            foreach (var (idx, region) in _cachedCharRegions)
            {
                double top = region.LayoutBounds.Y;
                bool placed = false;
                for (int gi = 0; gi < _cachedLineYGroups.Count; gi++)
                {
                    if (Math.Abs(top - _cachedLineYGroups[gi].minY) <= yTolerance)
                    {
                        var g = _cachedLineYGroups[gi];
                        double w = line.TimedChars.TryGetValue(idx, out var tc) ? tc.Width : region.LayoutBounds.Width;
                        _cachedLineYGroups[gi] = (
                            g.minY,
                            Math.Max(g.maxY, region.LayoutBounds.Bottom),
                            Math.Min(g.minChar, idx),
                            Math.Max(g.maxChar, idx),
                            g.totalWidth + w,
                            Math.Min(g.minX, region.LayoutBounds.X),
                            Math.Max(g.maxX, region.LayoutBounds.Right)
                        );
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    double w = line.TimedChars.TryGetValue(idx, out var tc) ? tc.Width : region.LayoutBounds.Width;
                    _cachedLineYGroups.Add((
                        top, region.LayoutBounds.Bottom,
                        idx, idx,
                        w,
                        region.LayoutBounds.X, region.LayoutBounds.Right
                    ));
                }
            }

            // Sort by Y (top to bottom)
            _cachedLineYGroups.Sort((a, b) => a.minY.CompareTo(b.minY));

            foreach (var (minY, maxY, minChar, maxChar, totalWidth, minX, maxX) in _cachedLineYGroups)
            {
                int length = maxChar - minChar + 1;
                if (length <= 0) continue;

                double yOffset = minY;
                double lineHeight = maxY - minY;
                if (lineHeight <= 0) lineHeight = layout.LayoutBounds.Height / _cachedLineYGroups.Count;

                _cachedLineInfos.Add((minChar, length, totalWidth, new Rect(minX, yOffset, maxX - minX, lineHeight)));
            }

            if (_cachedLineInfos.Count == 0)
            {
                ds.DrawTextLayout(layout, (float)x, (float)y, playedColor);
                return;
            }

            double globalTotalWidth = 0;
            foreach (var info in _cachedLineInfos)
                globalTotalWidth += info.totalWidth;

            if (globalTotalWidth <= 0) return;

            // 2. 计算全局已扫宽度（基于字符播放进度顺序累加）
            double globalSweptWidth = 0;
            _cachedTimedChars.Clear();
            foreach (var kv in line.TimedChars)
                _cachedTimedChars.Add(kv);
            _cachedTimedChars.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var (idx, tc) in _cachedTimedChars)
            {
                double prog = tc.GetPlayProgress(timeMs);
                if (prog >= 1)
                    globalSweptWidth += tc.Width;
                else if (prog > 0)
                {
                    globalSweptWidth += tc.Width * prog;
                    break;
                }
                else
                    break;
            }

            // 3. 使用缓存遮罩（文本不变时复用），避免每帧重建 CanvasCommandList
            CanvasCommandList maskCL = line.GetOrCreateMask(resourceCreator, (float)x, (float)y);

            const double fadeWidth = 0.06;
            double accumulatedWidth = 0;

            // 4. 逐行绘制
            int currentLineGroupIndex = 0;
            foreach (var (startChar, length, lineTotal, bounds) in _cachedLineInfos)
            {
                if (length <= 0 || lineTotal <= 0) continue;

                double logicalStart = accumulatedWidth;
                accumulatedWidth += lineTotal;

                double localSweepStart = (globalSweptWidth - logicalStart) / lineTotal;
                double localSweepEnd = localSweepStart + fadeWidth;

                localSweepStart = Math.Clamp(localSweepStart, 0, 1);
                localSweepEnd = Math.Clamp(localSweepEnd, 0, 1);

                if (localSweepStart >= localSweepEnd)
                {
                    localSweepStart = localSweepEnd = localSweepStart > 0.5 ? 1 : 0;
                }

                // ★ 复用预分配数组，消除每行 CanvasGradientStop[] 分配
                _cachedGradientStops[0] = new CanvasGradientStop { Position = 0f, Color = playedColor };
                _cachedGradientStops[1] = new CanvasGradientStop { Position = (float)localSweepStart, Color = playedColor };
                _cachedGradientStops[2] = new CanvasGradientStop { Position = (float)localSweepEnd, Color = unplayedColor };
                _cachedGradientStops[3] = new CanvasGradientStop { Position = 1f, Color = unplayedColor };

                double rectX = x + bounds.X;
                double rectY = y + bounds.Y;
                double rectWidth = bounds.Width;
                double rectHeight = bounds.Height;

                if (rectWidth <= 0 || rectHeight <= 0) continue;

                // GradinetBrush 必须 using（Stops 为只读）
                using CanvasLinearGradientBrush gradBrush = new(resourceCreator, _cachedGradientStops)
                {
                    StartPoint = new System.Numerics.Vector2((float)rectX, 0),
                    EndPoint = new System.Numerics.Vector2((float)(rectX + rectWidth), 0)
                };

                // 每行创建 fillCL（不同行不同位置/渐变）
                using CanvasCommandList fillCL = new(resourceCreator);
                using (CanvasDrawingSession fillDs = fillCL.CreateDrawingSession())
                {
                    fillDs.FillRectangle(new Rect(rectX, rectY, rectWidth, rectHeight), gradBrush);
                }

                using AlphaMaskEffect maskedText = new()
                {
                    Source = fillCL,
                    AlphaMask = maskCL
                };

                DrawChars(resourceCreator, ds, line, maskedText, startChar, length, x, y);
                currentLineGroupIndex++;
            }
        }

        // 逐字绘制辅助方法
        private static void DrawChars(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds, RenderLineState line, AlphaMaskEffect maskedText, int startChar, int length, double x, double y)
        {
            int endChar = Math.Min(startChar + length, line.Source.PrimaryText.Length);
            int i = startChar;
            while (i < endChar)
            {
                if (!line.CharacterRegions.TryGetValue(i, out _) ||
                    !line.TimedChars.ContainsKey(i))
                {
                    i++;
                    continue;
                }

                int segmentStart = i;
                bool isLatinSegment = IsLatinVisualGroupChar(line.Source.PrimaryText, i);
                i++;

                if (isLatinSegment)
                {
                    while (i < endChar &&
                           line.CharacterRegions.ContainsKey(i) &&
                           line.TimedChars.ContainsKey(i) &&
                           IsLatinVisualGroupChar(line.Source.PrimaryText, i))
                    {
                        i++;
                    }
                }

                DrawCharSegment(ds, line, maskedText, segmentStart, i, x, y);
            }
        }

        private static void DrawCharSegment(CanvasDrawingSession ds, RenderLineState line, AlphaMaskEffect maskedText, int startChar, int endChar, double x, double y)
        {
            Rect? segmentBounds = null;
            for (int i = startChar; i < endChar; i++)
            {
                if (!line.CharacterRegions.TryGetValue(i, out CanvasTextLayoutRegion region))
                    continue;

                Rect bounds = region.LayoutBounds;
                segmentBounds = segmentBounds.HasValue
                    ? Union(segmentBounds.Value, bounds)
                    : bounds;
            }

            if (!segmentBounds.HasValue)
                return;

            Rect srcRect = ToPixelAlignedRect(new Rect(
                x + segmentBounds.Value.X,
                y + segmentBounds.Value.Y,
                segmentBounds.Value.Width,
                segmentBounds.Value.Height));

            double floatOffset = CalculateSegmentFloatOffset(line, startChar, endChar);
            Rect destRect = new(srcRect.X, srcRect.Y + floatOffset, srcRect.Width, srcRect.Height);

            using CropEffect crop = new()
            {
                Source = maskedText,
                SourceRectangle = srcRect,
                BorderMode = EffectBorderMode.Soft
            };
            ds.DrawImage(crop, destRect, srcRect);
        }

        private static double CalculateSegmentFloatOffset(RenderLineState line, int startChar, int endChar)
        {
            double offset = 0;
            int count = 0;
            for (int i = startChar; i < endChar; i++)
            {
                if (!line.TimedChars.ContainsKey(i))
                    continue;

                offset += line.AnimationManager.GetFloatOffset(i);
                count++;
            }

            return count == 0 ? 0 : offset / count;
        }

        private static Rect Union(Rect a, Rect b)
        {
            double left = Math.Min(a.Left, b.Left);
            double top = Math.Min(a.Top, b.Top);
            double right = Math.Max(a.Right, b.Right);
            double bottom = Math.Max(a.Bottom, b.Bottom);
            return new Rect(left, top, right - left, bottom - top);
        }

        private static Rect ToPixelAlignedRect(Rect rect)
        {
            double left = Math.Floor(rect.Left);
            double top = Math.Floor(rect.Top);
            double right = Math.Ceiling(rect.Right);
            double bottom = Math.Ceiling(rect.Bottom);
            return new Rect(left, top, right - left, bottom - top);
        }

        private static bool IsLatinVisualGroupChar(string text, int index)
        {
            if (index < 0 || index >= text.Length)
                return false;

            char c = text[index];
            return c < 128 && (char.IsLetterOrDigit(c) || c == '\'' || c == '-' || c == '’');
        }

        private void DrawSingleLineCurrentPrimary(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds, RenderLineState line, double x, double y, double timeMs, double edgeFade, Color playedColor, Color unplayedColor)
        {
            double totalSweptWidth = CalculateLineTotalSweptWidth(line, timeMs);
            Rect layoutBounds = line.PrimaryLayout.LayoutBounds;
            double totalWidth = layoutBounds.Width;

            double sweepRatio = Math.Clamp(totalSweptWidth / Math.Max(1, totalWidth), 0, 1);
            double fadeWidth = 0.06;

            // ★ 复用预分配数组，消除每帧 CanvasGradientStop[] 分配
            _cachedGradientStops[0] = new CanvasGradientStop { Position = 0f, Color = playedColor };
            _cachedGradientStops[1] = new CanvasGradientStop { Position = (float)sweepRatio, Color = playedColor };
            _cachedGradientStops[2] = new CanvasGradientStop { Position = (float)Math.Min(sweepRatio + fadeWidth, 1.0), Color = unplayedColor };
            _cachedGradientStops[3] = new CanvasGradientStop { Position = 1f, Color = unplayedColor };

            // 注：CanvasLinearGradientBrush.Stops 在此 Win2D 版本中为只读，
            //     每帧创建 using。CanvasCommandList / AlphaMaskEffect 必须每帧
            //     using（跨帧复用会导致渲染内容丢失 — GPU 异步读取与 CreateDrawingSession 冲突）
            using CanvasLinearGradientBrush gradientBrush = new(resourceCreator, _cachedGradientStops)
            {
                StartPoint = new System.Numerics.Vector2((float)(x + layoutBounds.X), 0),
                EndPoint = new System.Numerics.Vector2((float)(x + layoutBounds.X + totalWidth), 0)
            };

            using CanvasCommandList fillCL = new(resourceCreator);
            using (CanvasDrawingSession fillDs = fillCL.CreateDrawingSession())
            {
                fillDs.FillRectangle(new Rect(x + layoutBounds.X, y + layoutBounds.Y, totalWidth, layoutBounds.Height), gradientBrush);
            }

            // ★ 使用缓存遮罩，避免每帧重建 CanvasCommandList
            CanvasCommandList maskCL = line.GetOrCreateMask(resourceCreator, (float)x, (float)y);

            using AlphaMaskEffect maskedText = new()
            {
                Source = fillCL,
                AlphaMask = maskCL
            };

            DrawChars(resourceCreator, ds, line, maskedText, 0, line.Source.PrimaryText.Length, x, y);
        }

        private void DrawNonCurrentPrimary(CanvasDrawingSession ds, RenderLineState line, double x, double y, double edgeFade)
        {
            double opacity = Math.Max(line.PlayedOpacity.Value, line.UnplayedOpacity.Value) * edgeFade;
            if (opacity <= 0.005)
                return;

            ds.DrawTextLayout(line.PrimaryLayout, (float)x, (float)y, WhiteWithOpacity(opacity));
        }

        private void DrawPlaceholder(CanvasDrawingSession ds)
        {
            // ★ 修复：复用缓存格式，避免空歌词状态下每帧创建 CanvasTextFormat
            _placeholderFormat ??= new CanvasTextFormat
            {
                FontFamily = "Arial, Microsoft YaHei UI, Segoe UI",
                FontSize = 42,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                WordWrapping = CanvasWordWrapping.Wrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
                VerticalAlignment = CanvasVerticalAlignment.Center,
                Options = CanvasDrawTextOptions.NoPixelSnap
            };
            ds.DrawText(_placeholder, new Rect(_lyricsX, _lyricsY, _lyricsWidth, _lyricsHeight), Color.FromArgb(225, 255, 255, 255), _placeholderFormat);
        }

        private static CanvasTextLayout CreateTextLayout(ICanvasResourceCreator resourceCreator, string text, float fontSize, double width)
        {
            return new CanvasTextLayout(resourceCreator, text, new CanvasTextFormat
            {
                FontFamily = "Arial, Microsoft YaHei UI, Segoe UI",
                FontSize = fontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                WordWrapping = CanvasWordWrapping.Wrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
                VerticalAlignment = CanvasVerticalAlignment.Top,
                Options = CanvasDrawTextOptions.NoPixelSnap
            }, (float)Math.Max(width, 1), 1600);
        }

        private static float CalculatePrimarySize(double lyricsWidth, double lyricsHeight)
        {
            double widthBased = lyricsWidth / 16.2;
            double heightBased = lyricsHeight / 12.2;
            return (float)Math.Clamp(Math.Min(widthBased, heightBased), MinPrimarySize, MaxPrimarySize);
        }

        private void UpdateManualScroll(double elapsedSeconds)
        {
            if (!_isManualScrolling)
                return;

            if (Environment.TickCount64 <= _manualScrollUntilTick)
                return;

            if (Math.Abs(_manualScrollTransition.TargetValue) < 0.1)
            {
                ResumeAutoFollow();
                return;
            }

            double decay = Math.Exp(-Math.Max(0.001, elapsedSeconds) * 6.4);
            double target = _manualScrollTransition.TargetValue * decay;
            if (Math.Abs(target) < 0.1)
            {
                target = 0;
                ResumeAutoFollow();
            }

            _manualScrollTransition.JumpTo(target);
        }

        private void UpdateManualBrowseVisibility(double elapsedSeconds)
        {
            double target = _isManualScrolling ? 1 : 0;
            double speed = _isManualScrolling ? 8.5 : 5.0;
            double step = 1 - Math.Exp(-Math.Max(0.001, elapsedSeconds) * speed);
            _manualBrowseVisibility += (target - _manualBrowseVisibility) * step;
            if (Math.Abs(_manualBrowseVisibility - target) < 0.002)
                _manualBrowseVisibility = target;
        }

        private void EnsureManualScrollStartsFromCurrentView()
        {
            lock (_renderLinesLock)
            {
                if (_renderLines.Count == 0 || _isManualScrolling)
                    return;
            }

            _manualScrollTransition.JumpTo(0);
        }

        private double GetMinManualScrollOffset()
        {
            lock (_renderLinesLock)
            {
                if (_renderLines.Count == 0 || _currentLineIndex < 0 || _currentLineIndex >= _renderLines.Count)
                    return 0;

                double currentCenter = _renderLines[_currentLineIndex].PrimaryCenterY;
                double currentToLast = currentCenter - _renderLines[^1].PrimaryCenterY - _lyricsHeight * 0.35;
                double keepLastReachable = _lyricsHeight * (1 - CurrentLineAnchor) - _totalFlowHeight - _scrollTransition.TargetValue;
                return Math.Min(0, Math.Min(currentToLast, keepLastReachable));
            }
        }

        private double GetMaxManualScrollOffset()
        {
            lock (_renderLinesLock)
            {
                if (_renderLines.Count == 0 || _currentLineIndex < 0 || _currentLineIndex >= _renderLines.Count)
                    return 0;

                double currentCenter = _renderLines[_currentLineIndex].PrimaryCenterY;
                double currentToFirst = currentCenter - _renderLines[0].PrimaryCenterY + _lyricsHeight * 0.35;
                double keepFirstReachable = _lyricsHeight * CurrentLineAnchor + _scrollTransition.TargetValue;
                return Math.Max(0, Math.Max(currentToFirst, keepFirstReachable));
            }
        }

        private int FindCurrentLineIndex(double timeMs)
        {
            if (_lines == null || _lines.Count == 0)
                return 0;

            int left = 0;
            int right = _lines.Count - 1;
            int candidate = 0;
            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                if (_lines[mid].StartMs <= timeMs)
                {
                    candidate = mid;
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            return Math.Clamp(candidate, 0, _lines.Count - 1);
        }

        private void ClearRenderLines()
        {
            // ★ 加锁：渲染线程可能正在遍历/绘制 _renderLines，主线程重建时必须互斥
            lock (_renderLinesLock)
            {
                foreach (RenderLineState line in _renderLines)
                    line.Dispose();
                _renderLines.Clear();
            }
            _totalFlowHeight = 0;
            _layoutDirty = true;
        }

        private void LogLoadedLyrics()
        {
            if (_loggedLoadedStats || _lines == null)
                return;

            _loggedLoadedStats = true;
            int realLineCount = _lines.Count(line => line.IsPrimaryHasRealSyllableInfo && line.PrimarySyllables.Count > 0);
            int realSyllableCount = _lines.Sum(line => line.IsPrimaryHasRealSyllableInfo ? line.PrimarySyllables.Count : 0);
            AppLogger.Info($"CanvasLyricsRenderer loaded: lines={_lines.Count}, realLines={realLineCount}, realSyllables={realSyllableCount}");
        }

        private static LyricsLine NormalizeLine(LyricsLine source)
        {
            string primaryText = StripInlineTimeTags(source.PrimaryText);
            var line = new LyricsLine
            {
                StartMs = source.StartMs,
                EndMs = source.EndMs,
                PrimaryText = primaryText,
                SecondaryText = StripInlineTimeTags(source.SecondaryText),
                TertiaryText = StripInlineTimeTags(source.TertiaryText),
                AgentId = source.AgentId,
                IsPrimaryHasRealSyllableInfo = source.IsPrimaryHasRealSyllableInfo,
                PrimarySyllables = source.IsPrimaryHasRealSyllableInfo
                    ? source.PrimarySyllables
                        .Where(s => !string.IsNullOrEmpty(s.Text))
                        .OrderBy(s => s.StartMs)
                        .Select(s => new BaseLyrics
                        {
                            StartMs = s.StartMs,
                            EndMs = s.EndMs,
                            StartIndex = s.StartIndex,
                            Text = s.Text
                        })
                        .Where(s => !string.IsNullOrEmpty(s.Text))
                        .ToList()
                    : []
            };

            if (line.PrimarySyllables.Count > 0)
                AlignAndValidateSyllables(line);

            return line;
        }

        /// <summary>
        /// Align syllable StartIndex to stripped text position in PrimaryText.
        /// Fix EndMs gaps. Preserves original whitespace in PrimaryText.
        /// </summary>
        private static void AlignAndValidateSyllables(LyricsLine line)
        {
            // Step 1: Align syllable.StartIndex — find stripped syllable text in PrimaryText
            int searchStart = 0;
            foreach (var syllable in line.PrimarySyllables)
            {
                string strippedText = StripInlineTimeTags(syllable.Text);
                if (string.IsNullOrEmpty(strippedText))
                    continue;

                int found = line.PrimaryText.IndexOf(strippedText, searchStart, StringComparison.Ordinal);
                if (found >= 0)
                {
                    syllable.StartIndex = found;
                    searchStart = found + strippedText.Length;
                }
                else
                {
                    // syllable text not found in PrimaryText — skip
                    searchStart = Math.Clamp(syllable.StartIndex + strippedText.Length, 0, line.PrimaryText.Length);
                }
            }

            // Step 2: Fix EndMs — each syllable's end time = next syllable's start time
            for (int i = 0; i < line.PrimarySyllables.Count; i++)
            {
                var syllable = line.PrimarySyllables[i];
                int? nextStartMs = i + 1 < line.PrimarySyllables.Count
                    ? line.PrimarySyllables[i + 1].StartMs
                    : null;

                int fallbackEndMs = nextStartMs ??
                    (line.EndMs.HasValue && line.EndMs.Value > syllable.StartMs
                        ? line.EndMs.Value
                        : syllable.StartMs + Math.Max(1, syllable.Length * 120));

                int endMs = syllable.EndMs ?? fallbackEndMs;
                if (endMs <= syllable.StartMs)
                    endMs = fallbackEndMs;

                if (nextStartMs.HasValue &&
                    nextStartMs.Value > syllable.StartMs &&
                    endMs > nextStartMs.Value)
                {
                    endMs = nextStartMs.Value;
                }

                if (endMs <= syllable.StartMs)
                    endMs = syllable.StartMs + Math.Max(1, syllable.Length);

                syllable.EndMs = endMs;
            }

            // Step 3: Ensure line.EndMs covers all syllables
            if (line.PrimarySyllables.Count > 0)
            {
                int lastEnd = line.PrimarySyllables.Last().EndMs ?? line.PrimarySyllables.Last().StartMs;
                if (line.EndMs == null || line.EndMs < lastEnd)
                    line.EndMs = Math.Max(lastEnd, line.StartMs + 1);
            }
        }

        private static string StripInlineTimeTags(string text) =>
            string.IsNullOrEmpty(text)
                ? string.Empty
                : InlineTimeTagRegex.Replace(text, string.Empty).Trim();

        private static bool HasRealTimedSyllables(LyricsLine line) =>
            line.IsPrimaryHasRealSyllableInfo &&
            line.PrimarySyllables.Count > 0 &&
            line.PrimarySyllables.Any(syllable => (syllable.EndMs ?? syllable.StartMs) > syllable.StartMs);

        private static double GetProgress(double timeMs, int startMs, int endMs)
        {
            if (endMs <= startMs)
                return timeMs >= startMs ? 1 : 0;

            return Math.Clamp((timeMs - startMs) / (endMs - startMs), 0, 1);
        }

        private static void FillMissingLineEndTimes(IList<LyricsLine> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                LyricsLine line = lines[i];
                if (line.EndMs.HasValue && line.EndMs.Value > line.StartMs)
                    continue;

                int fallbackEnd = i + 1 < lines.Count
                    ? lines[i + 1].StartMs
                    : line.StartMs + 2600;

                line.EndMs = Math.Max(line.StartMs + 1, fallbackEnd);
            }
        }

        private static double Lerp(double from, double to, double amount) =>
            from + (to - from) * Math.Clamp(amount, 0, 1);

        private static Color ColorLerp(Color from, Color to, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            byte a = (byte)(from.A + (to.A - from.A) * amount);
            byte r = (byte)(from.R + (to.R - from.R) * amount);
            byte g = (byte)(from.G + (to.G - from.G) * amount);
            byte b = (byte)(from.B + (to.B - from.B) * amount);
            return Color.FromArgb(a, r, g, b);
        }

        private static Color WhiteWithOpacity(double opacity)
        {
            byte alpha = (byte)Math.Clamp((int)Math.Round(255 * Math.Clamp(opacity, 0, 1)), 0, 255);
            return Color.FromArgb(alpha, 255, 255, 255);
        }

        private sealed class RenderLineState : IDisposable
        {
            // ★ 缓存的文字遮罩 CommandList，避免每帧重建（文本不变时复用）
            private CanvasCommandList? _cachedMask;
            private float _cachedMaskX;
            private float _cachedMaskY;

            public RenderLineState(LyricsLine source)
            {
                Source = source;
            }

            public LyricsLine Source { get; }
            public CanvasTextLayout PrimaryLayout { get; set; } = null!;
            public CanvasTextLayout? SecondaryLayout { get; set; }
            public CanvasTextLayout? TertiaryLayout { get; set; }
            public CanvasTextLayoutRegion[] PrimaryRegions { get; set; } = [];
            public Dictionary<int, CanvasTextLayoutRegion> CharacterRegions { get; } = [];
            public Dictionary<int, TimedChar> TimedChars { get; } = [];
            public float PrimaryFontSize { get; set; }
            public LineAnimationManager AnimationManager { get; } = new();
            public Dictionary<int, int> CharToSyllableIndex { get; } = [];
            public List<DoubleTransition> CharFloatTransitions { get; } = new();
            public HashSet<int> TriggeredFloatChars { get; } = new();
            public double FlowTop { get; set; }
            public double FlowBottom { get; set; }
            public double PrimaryTop { get; set; }
            public double PrimaryCenterY { get; set; }
            public double SecondaryTop { get; set; }
            public double TertiaryTop { get; set; }
            public double VisualBottom { get; set; }
            public DoubleTransition Scale { get; } = new(CurrentScale, VisualDurationSeconds);
            public DoubleTransition PlayedOpacity { get; } = new(0, VisualDurationSeconds);
            public DoubleTransition UnplayedOpacity { get; } = new(0, VisualDurationSeconds);
            public DoubleTransition SecondaryOpacity { get; } = new(0, VisualDurationSeconds);
            public DoubleTransition TertiaryOpacity { get; } = new(0, VisualDurationSeconds);

            public void BuildTimedChars()
            {
                TimedChars.Clear();
                CharacterRegions.Clear();
                CharToSyllableIndex.Clear();
                AnimationManager.Reset();
                // CharFloatTransitions / TriggeredFloatChars no longer used — see comment above.
                if (!HasRealTimedSyllables(Source) || PrimaryRegions.Length == 0)
                    return;

                for (int i = 0; i < Source.PrimaryText.Length; i++)
                {
                    CanvasTextLayoutRegion region = PrimaryLayout.GetCharacterRegions(i, 1).FirstOrDefault();
                    if (region.LayoutBounds.Width > 0)
                        CharacterRegions[i] = region;
                }

                // Build per-character timed info by iterating every character in PrimaryText,
                // finding its syllable, and distributing time evenly within each syllable.
                // This is exactly what BetterLyrics-dev RecreateRenderChars() does.
                for (int charIndex = 0; charIndex < Source.PrimaryText.Length; charIndex++)
                {
                    // Find syllable that owns this character
                    BaseLyrics? owningSyllable = null;
                    int syllableCharOffset = 0; // position of this char within the syllable's text
                    int owningSyllableIndex = -1;
                    for (int s = 0; s < Source.PrimarySyllables.Count; s++)
                    {
                        BaseLyrics syllable = Source.PrimarySyllables[s];
                        string sylText = StripInlineTimeTags(syllable.Text);
                        int sylStart = syllable.StartIndex;
                        int sylEnd = sylStart + sylText.Length - 1;

                        if (charIndex >= sylStart && charIndex <= sylEnd)
                        {
                            owningSyllable = syllable;
                            syllableCharOffset = charIndex - sylStart;
                            CharToSyllableIndex[charIndex] = s;
                            owningSyllableIndex = s;
                            break;
                        }
                    }

                    if (owningSyllable == null ||
                        !CharacterRegions.TryGetValue(charIndex, out CanvasTextLayoutRegion region))
                        continue;

                    string strippedSyllableText = StripInlineTimeTags(owningSyllable.Text);
                    int syllableCharCount = strippedSyllableText.Length;
                    int syllableEnd = owningSyllable.EndMs ?? owningSyllable.StartMs + Math.Max(1, syllableCharCount * 0);
                    double charDuration = Math.Max(1, (syllableEnd - owningSyllable.StartMs) / (double)Math.Max(1, syllableCharCount));
                    int charStartMs = owningSyllable.StartMs + (int)Math.Floor(syllableCharOffset * charDuration);
                    int charEndMs = owningSyllable.StartMs + (int)Math.Ceiling((syllableCharOffset + 1) * charDuration);
                    TimedChars[charIndex] = new TimedChar(charStartMs, Math.Max(charStartMs + 1, charEndMs), region.LayoutBounds.Width);

                    // Configure per-character float animation (idempotent per char index)
                    AnimationManager.Configure(charIndex, charStartMs, charEndMs, FloatAmplitude, IsCJKChar(Source.PrimaryText, charIndex));
                }

                // Fill gaps: characters not owned by any syllable (spaces, punctuation between words)
                // borrow time from the nearest syllable so they participate in the sweep.
                // Prefer the preceding syllable's last char; fall back to the next syllable's first char.
                for (int charIndex = 0; charIndex < Source.PrimaryText.Length; charIndex++)
                {
                    if (TimedChars.ContainsKey(charIndex) ||
                        !CharacterRegions.TryGetValue(charIndex, out CanvasTextLayoutRegion region))
                        continue;

                    // Find nearest timed character before this gap
                    int? nearestPrevStart = null;
                    for (int j = charIndex - 1; j >= 0; j--)
                    {
                        if (TimedChars.TryGetValue(j, out TimedChar prevTc))
                        {
                            nearestPrevStart = prevTc.StartMs;
                            break;
                        }
                    }

                    // Find nearest timed character after this gap
                    int? nearestNextStart = null;
                    for (int j = charIndex + 1; j < Source.PrimaryText.Length; j++)
                    {
                        if (TimedChars.TryGetValue(j, out TimedChar nextTc))
                        {
                            nearestNextStart = nextTc.StartMs;
                            break;
                        }
                    }

                    int charStartMs;
                    if (nearestPrevStart.HasValue)
                    {
                        charStartMs = nearestPrevStart.Value;
                    }
                    else if (nearestNextStart.HasValue)
                    {
                        charStartMs = nearestNextStart.Value;
                    }
                    else
                    {
                        // No timed chars at all in this line — shouldn't happen but guard
                        charStartMs = Source.StartMs;
                    }

                    int charEndMs = charStartMs + 50; // minimal placeholder duration
                    TimedChars[charIndex] = new TimedChar(charStartMs, charEndMs, region.LayoutBounds.Width);
                    AnimationManager.Configure(charIndex, charStartMs, charEndMs, FloatAmplitude, IsCJKChar(Source.PrimaryText, charIndex));
                }

                // CharFloatTransitions and TriggeredFloatChars are no longer used —
                // float offset is computed in real-time from play progress in DrawCurrentPrimary.
            }

            public void StartVisualTargets(double scale, double playedOpacity, double unplayedOpacity, double secondaryOpacity, double tertiaryOpacity, bool force)
            {
                StartOrJump(Scale, scale, force);
                StartOrJump(PlayedOpacity, playedOpacity, force);
                StartOrJump(UnplayedOpacity, unplayedOpacity, force);
                StartOrJump(SecondaryOpacity, secondaryOpacity, force);
                StartOrJump(TertiaryOpacity, tertiaryOpacity, force);
            }

            public void Update(TimeSpan elapsedTime)
            {
                Scale.Update(elapsedTime);
                PlayedOpacity.Update(elapsedTime);
                UnplayedOpacity.Update(elapsedTime);
                SecondaryOpacity.Update(elapsedTime);
                TertiaryOpacity.Update(elapsedTime);
            }

            public void Dispose()
            {
                PrimaryLayout?.Dispose();
                SecondaryLayout?.Dispose();
                TertiaryLayout?.Dispose();
                _cachedMask?.Dispose();
                _cachedMask = null;
            }

            /// <summary>
            /// 获取缓存的文字遮罩 CommandList。如果位置变化或遮罩无效则重建。
            /// 避免每帧创建 CanvasCommandList 的开销（Win2D 文档建议静态内容不要每帧重建）。
            /// </summary>
            public CanvasCommandList GetOrCreateMask(ICanvasResourceCreator resourceCreator, float x, float y)
            {
                // 位置变化或遮罩无效时重建
                if (_cachedMask == null || Math.Abs(x - _cachedMaskX) > 0.5f || Math.Abs(y - _cachedMaskY) > 0.5f)
                {
                    _cachedMask?.Dispose();
                    _cachedMask = new CanvasCommandList(resourceCreator);
                    using (CanvasDrawingSession maskDs = _cachedMask.CreateDrawingSession())
                    {
                        maskDs.DrawTextLayout(PrimaryLayout, x, y, Color.FromArgb(255, 255, 255, 255));
                    }
                    _cachedMaskX = x;
                    _cachedMaskY = y;
                }
                return _cachedMask;
            }

            public void InvalidateMask()
            {
                _cachedMask?.Dispose();
                _cachedMask = null;
            }

            private static void StartOrJump(DoubleTransition transition, double value, bool force)
            {
                if (force)
                    transition.JumpTo(value);
                else if (Math.Abs(transition.TargetValue - value) > 0.005)
                    transition.Start(value);
            }
        }

        private readonly struct TimedChar(int StartMs, int EndMs, double Width)
        {
            public int StartMs { get; } = StartMs;
            public int EndMs { get; } = EndMs;
            public double Width { get; } = Width;

            public double GetPlayProgress(double currentMs)
            {
                double duration = Math.Max(EndMs - StartMs, 1);
                return Math.Clamp((currentMs - StartMs) / duration, 0, 1);
            }
        }

        private class DoubleTransition
        {
            protected readonly double _durationSeconds;
            protected double _startValue;
            protected double _currentValue;
            protected double _targetValue;
            protected double _progress;
            protected bool _isRunning;

            public DoubleTransition(double initialValue, double durationSeconds)
            {
                _durationSeconds = Math.Max(0.001, durationSeconds);
                _startValue = initialValue;
                _currentValue = initialValue;
                _targetValue = initialValue;
            }

            public double Value => _currentValue;
            public double TargetValue => _targetValue;

            public virtual void JumpTo(double value)
            {
                _startValue = value;
                _currentValue = value;
                _targetValue = value;
                _progress = 1;
                _isRunning = false;
            }

            public virtual void Start(double value)
            {
                if (Math.Abs(_targetValue - value) < 0.001)
                    return;

                _startValue = _currentValue;
                _targetValue = value;
                _progress = 0;
                _isRunning = true;
            }

            public virtual void Update(TimeSpan elapsedTime)
            {
                if (!_isRunning)
                    return;

                _progress = Math.Clamp(_progress + elapsedTime.TotalSeconds / _durationSeconds, 0, 1);
                double eased = EaseOutSine(_progress);
                _currentValue = _startValue + (_targetValue - _startValue) * eased;
                if (_progress >= 1)
                {
                    _currentValue = _targetValue;
                    _isRunning = false;
                }
            }

            public virtual void StartKeyframes(List<(double Target, double DurationSec)> keyframes)
            {
                Start(keyframes.Last().Target);
            }

            private static double EaseOutSine(double progress) => Math.Sin(progress * Math.PI / 2);
        }

        private class KeyframeDoubleTransition : DoubleTransition
        {
            private readonly List<(double Target, double DurationSec)> _keyframes;
            private int _currentKeyframeIndex;
            private double _keyframeProgress;
            private double _totalDuration;

            public KeyframeDoubleTransition(List<(double Target, double DurationSec)> keyframes, double totalDuration)
                : base(0, totalDuration)
            {
                _keyframes = keyframes;
                _totalDuration = Math.Max(0.001, totalDuration);
                _currentKeyframeIndex = 0;
                _keyframeProgress = 0;
            }

            public override void StartKeyframes(List<(double Target, double DurationSec)> keyframes)
            {
                _keyframes.Clear();
                _keyframes.AddRange(keyframes);
                _totalDuration = _keyframes.Sum(kf => kf.DurationSec);
                _currentKeyframeIndex = 0;
                _keyframeProgress = 0;
                _isRunning = true;
                _currentValue = 0;
                _startValue = 0;
                _targetValue = 0;
                _progress = 1;
            }

            public override void JumpTo(double value)
            {
                _keyframes.Clear();
                _currentValue = value;
                _currentKeyframeIndex = 0;
                _keyframeProgress = 0;
                _isRunning = false;
                _startValue = value;
                _targetValue = value;
                _progress = 1;
            }

            public override void Update(TimeSpan elapsedTime)
            {
                if (!_isRunning || _keyframes.Count == 0)
                    return;

                double elapsedTotal = elapsedTime.TotalSeconds;
                while (elapsedTotal > 0 && _currentKeyframeIndex < _keyframes.Count)
                {
                    var kf = _keyframes[_currentKeyframeIndex];
                    double remainingInKf = kf.DurationSec - _keyframeProgress;
                    double step = Math.Min(elapsedTotal, remainingInKf);

                    _keyframeProgress += step;
                    elapsedTotal -= step;

                    double kfProgress = Math.Clamp(_keyframeProgress / Math.Max(0.001, kf.DurationSec), 0, 1);
                    double eased = EaseOutSine(kfProgress);

                    double prevTarget = _currentKeyframeIndex > 0 ? _keyframes[_currentKeyframeIndex - 1].Target : 0;
                    _currentValue = prevTarget + (kf.Target - prevTarget) * eased;

                    if (_keyframeProgress >= kf.DurationSec - 0.0001)
                    {
                        _currentValue = kf.Target;
                        _currentKeyframeIndex++;
                        _keyframeProgress = 0;
                    }
                }

                if (_currentKeyframeIndex >= _keyframes.Count)
                {
                    _currentValue = _keyframes.Last().Target;
                    _isRunning = false;
                }
            }

            private static double EaseOutSine(double progress) => Math.Sin(progress * Math.PI / 2);
        }

        private static bool IsCJKChar(string text, int index)
        {
            if (index < 0 || index >= text.Length)
                return false;
            return IsCJKText(text[index].ToString());
        }

        private static bool IsCJKText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (char c in text)
            {
                // CJK Unified Ideographs (中文)
                if (c >= 0x4E00 && c <= 0x9FFF) return true;
                // Hangul Syllables (韩文)
                if (c >= 0xAC00 && c <= 0xD7AF) return true;
                // Hiragana (日文平假名)
                if (c >= 0x3040 && c <= 0x309F) return true;
                // Katakana (日文片假名)
                if (c >= 0x30A0 && c <= 0x30FF) return true;
            }

            return false;
        }

        private readonly record struct LineLayoutHit(int Index, int StartMs, Rect Bounds);
    }
}
