using System;
using System.Diagnostics;
using Windows.Foundation;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 拥有画布视图变换——缩放、平移、旋转——并为其变化做动画
    /// （移植自 FlyPhotos.Display.Controllers.CanvasViewManager，已精简：去掉每图记忆视图、设置项依赖）。
    ///
    /// 线程模型：本类的所有公开方法都在 Win2D 渲染（"W2D"）线程上运行——UI 线程的调用经由
    /// <see cref="CanvasDisplayController"/> 的动作队列转投。因此无需加锁，全类视为单线程。
    /// <see cref="OnUpdate"/> 每帧由渲染线程驱动，推进当前激活的动画。
    ///
    /// 动画模型：同一时刻至多一个 <see cref="IViewAnimation"/> 激活：
    ///   <see cref="AnchoredZoomAnimation"/> —— 纯缩放弹簧，平移每帧重算保持锚点不动（滚轮/键盘）。
    ///   <see cref="PanZoomAnimation"/>      —— 缩放 + 平移三轴独立弹簧（适应/100%/居中/双击）。
    ///   Shrug 抖动 —— 拒绝操作的反馈（本项目未使用，保留实现）。
    /// 平移、旋转、触控板精度缩放直接生效（无动画）。
    /// </summary>
    internal sealed class CanvasViewManager : IAnimationHost
    {
        // --- 协作对象与当前图片 ---
        private readonly CanvasViewState _canvasViewState;
        private Size _imageSize;

        // --- 动画底座：至多一个激活动画；弹簧组与时钟跨重瞄准、跨动画类型持续 ---
        private IViewAnimation? _activeAnimation;
        private readonly Stopwatch _animationStopwatch = new();
        private bool _suppressZoomUpdateForNextAnimation;
        private float _zoomTargetScale;
        private double _lastSpringElapsedMs;
        private readonly SpringAxis _scaleSpring = new();   // 弹簧 log(scale)
        private readonly SpringAxis _panXSpring = new();    // 弹簧 ImagePos.X（像素）
        private readonly SpringAxis _panYSpring = new();    // 弹簧 ImagePos.Y（像素）

        /// <summary>缩放相等判定容差（0.1%），fit/1:1/默认状态检查用。float 除法与 log/exp 往返不可能精确相等。</summary>
        private const float ScaleTolerance = 0.001f;

        // --- 事件 ---
        /// <summary>视图"适应窗口"状态变化。</summary>
        public event Action<bool>? FitToScreenStateChanged;
        /// <summary>视图"100% 缩放"状态变化。</summary>
        public event Action<bool>? OneToOneStateChanged;
        /// <summary>缩放值变化。</summary>
        public event Action? ZoomChanged;
        /// <summary>需要重绘画布。</summary>
        public event Action? ViewChanged;
        /// <summary>任意动画完成。</summary>
        public event Action? AnimationCompleted;

        // --- 属性 ---
        /// <summary>是否有平移/缩放动画正在进行（<see cref="IViewAnimation"/> 激活）。</summary>
        public bool PanZoomAnimationOnGoing => _activeAnimation != null;

        /// <summary>图片是否完美适应画布（含留白）。</summary>
        private bool IsFittedToScreen
        {
            get;
            set
            {
                if (field == value) return;
                field = value;
                FitToScreenStateChanged?.Invoke(field);
            }
        }

        /// <summary>图片是否恰好在 100% 缩放。</summary>
        private bool IsAtOneToOne
        {
            get;
            set
            {
                if (field == value) return;
                field = value;
                OneToOneStateChanged?.Invoke(field);
            }
        }

        public CanvasViewManager(CanvasViewState canvasViewState) => _canvasViewState = canvasViewState;

        // --- 生命周期 ---

        /// <summary>
        /// 为一张新显示的图片应用初始缩放与位置。
        /// <paramref name="imageSize"/> 为图片像素尺寸，<paramref name="canvasSize"/> 为画布像素尺寸。
        /// 切图的"旧图滑出 + 新图滑入"双图过渡由 <see cref="CanvasDisplayController"/> 在渲染层实现，
        /// 此处只负责把新图视图重置为默认（适应窗口）。
        /// </summary>
        public void SetNewPhoto(Size imageSize, int imageRotation, Size canvasSize, bool isFirstPhoto)
        {
            _imageSize = imageSize;
            _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
            _canvasViewState.Rotation = imageRotation;
            _canvasViewState.RotationAngle = imageRotation; // 新图无历史旋转，连续角度与逻辑方向同步

            // 取消任何飞行中的动画，避免其残留内部状态污染刚应用的新视图
            StopAnimationSnappingToTarget();

            SetDefaultView(imageSize, canvasSize, isFirstPhoto);
            ViewChanged?.Invoke();
        }

        /// <summary>把新图片设为默认视图：适应窗口（小图同样放大到适应，与旧实现一致）。</summary>
        private void SetDefaultView(Size imageSize, Size canvasSize, bool isFirstPhoto)
        {
            var defaultFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
            var center = new Point(canvasSize.Width / 2, canvasSize.Height / 2);

            _canvasViewState.ImagePos = center;
            _canvasViewState.LastScaleTo = defaultFitScale;

            // 弹簧驱动 log(scale)，非正数适应缩放会喂给 log(0) → -∞；仅当目标有效时才做启动动画。
            if (isFirstPhoto && defaultFitScale > 0)
            {
                _canvasViewState.Scale = 0.01f;
                _suppressZoomUpdateForNextAnimation = true;
                StartSpringPanAndZoomAnimation(defaultFitScale, center, canvasSize, forceReseed: true);
            }
            else
            {
                _canvasViewState.Scale = defaultFitScale;
            }

            _canvasViewState.UpdateTransform();
            IsFittedToScreen = true; // 新图默认即适应
            IsAtOneToOne = Math.Abs(defaultFitScale - 1.0f) < ScaleTolerance;
        }

        /// <summary>响应画布尺寸变化：适应模式重新适应；自定义缩放/平移时按比例调整位置。</summary>
        public void HandleSizeChange(Size newSize, Size previousSize)
        {
            if (IsFittedToScreen)
            {
                var newScale = ZoomGeometry.CalculateScreenFitScale(newSize, _imageSize, _canvasViewState.Rotation);
                _canvasViewState.LastScaleTo = newScale;
                var newCenter = new Point(newSize.Width / 2, newSize.Height / 2);

                // 若适应动画正在进行，更新弹簧目标（而非直接覆盖视图状态），
                // 避免下一帧 Tick 用旧目标把图片拉回原位。
                if (_activeAnimation is PanZoomAnimation panZoom)
                {
                    panZoom.Aim(newScale, newCenter, newSize, reseedPan: true);
                }
                else
                {
                    _canvasViewState.Scale = newScale;
                    _canvasViewState.ImagePos = newCenter;
                    _canvasViewState.UpdateTransform();
                }

                ViewChanged?.Invoke();
                ZoomChanged?.Invoke();
                IsAtOneToOne = Math.Abs(newScale - 1.0f) < ScaleTolerance;
            }
            else if (previousSize.Width > 0 && previousSize.Height > 0)
            {
                // 保持图片相对屏幕位置不变
                var xChangeRatio = newSize.Width / previousSize.Width;
                var yChangeRatio = newSize.Height / previousSize.Height;
                _canvasViewState.ImagePos.X *= xChangeRatio;
                _canvasViewState.ImagePos.Y *= yChangeRatio;
                _canvasViewState.UpdateTransform();
                ViewChanged?.Invoke();
            }
        }

        // --- 缩放与平移 ---

        /// <summary>
        /// 做一次标准缩放，带弹簧动画。锚点统一为"图片中心"（原地缩放）：
        /// 缩放前后图片中心在画布上的位置保持不变，与鼠标指针位置无关。
        /// </summary>
        public void ZoomAtPoint(ZoomDirection zoomDirection, Point zoomAnchor)
        {
            const float scalePercentage = 1.25f; // 每档 25%
            var scaleTo = _canvasViewState.LastScaleTo * (zoomDirection == ZoomDirection.In ? scalePercentage : 1f / scalePercentage);
            if (scaleTo < 0.05f) return; // 最小缩放保护
            _canvasViewState.LastScaleTo = scaleTo;
            StartZoomAnimation(scaleTo, new Point(_canvasViewState.ImagePos.X, _canvasViewState.ImagePos.Y));

            // 任何手动缩放都使适应 / 1:1 失效
            IsFittedToScreen = false;
            IsAtOneToOne = false;
        }

        /// <summary>
        /// 精度缩放（触控板平滑滚动），直接生效无动画——
        /// 因为 delta 已经足够精细，逐帧弹簧反而显得拖沓。
        /// 锚点统一为"图片中心"（原地缩放），与鼠标指针位置无关。
        /// </summary>
        public void ZoomAtPointPrecision(int delta, Point zoomAnchor)
        {
            if (delta == 0) return;

            const float baseZoomIn = 1.25f;   // 一个完整滚轮档（120）的缩放系数
            const float minScale = 0.05f;

            // 缩放系数与 delta 成正比（指数）
            float scaleFactor = (float)Math.Pow(baseZoomIn, delta / 120.0);
            float newScale = _canvasViewState.LastScaleTo * scaleFactor;
            if (newScale < minScale) return;

            // 以图片中心为锚点：缩放前后图片中心位置不变（原地缩放）
            var center = new Point(_canvasViewState.ImagePos.X, _canvasViewState.ImagePos.Y);
            float oldScale = _canvasViewState.Scale;
            var newPos = ZoomGeometry.AnchorPreservingPan(center, _canvasViewState.ImagePos, oldScale, newScale);
            _canvasViewState.Scale = newScale;
            _canvasViewState.LastScaleTo = newScale;
            _canvasViewState.ImagePos = newPos;
            _canvasViewState.UpdateTransform();
            ViewChanged?.Invoke();
            ZoomChanged?.Invoke();

            IsFittedToScreen = false;
            IsAtOneToOne = false;
        }

        public void ZoomAtCenter(ZoomDirection zoomDirection, Size canvasSize) =>
            ZoomAtPoint(zoomDirection, new Point(canvasSize.Width / 2, canvasSize.Height / 2));

        /// <summary>步进缩放：适应窗口 → 100% → 400% 档位切换，锚定指定点（或画布中心）。</summary>
        public void StepZoom(ZoomDirection zoomDirection, Size canvasSize, Point? zoomAnchor = null)
        {
            var screenFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, _imageSize, _canvasViewState.Rotation);
            var zoomStops = ZoomGeometry.BuildZoomStops(screenFitScale);

            const float tolerance = ScaleTolerance;
            var currentScale = _canvasViewState.LastScaleTo;

            int nextStopIndex = zoomDirection == ZoomDirection.In
                ? zoomStops.FindIndex(stop => stop > currentScale + tolerance)
                : zoomStops.FindLastIndex(stop => stop < currentScale - tolerance);

            if (nextStopIndex == -1) return; // 已到边界档位

            var targetScale = zoomStops[nextStopIndex];
            _canvasViewState.LastScaleTo = targetScale;

            var anchor = zoomAnchor ?? new Point(canvasSize.Width / 2, canvasSize.Height / 2);

            // 指定锚点 → 纯缩放动画；否则 → 平移+缩放动画（居中）
            if (zoomAnchor.HasValue)
                StartZoomAnimation(targetScale, anchor);
            else
                StartSpringPanAndZoomAnimation(targetScale, anchor, canvasSize);

            IsFittedToScreen = Math.Abs(targetScale - screenFitScale) < ScaleTolerance;
            IsAtOneToOne = Math.Abs(targetScale - 1.0f) < ScaleTolerance;
        }

        /// <summary>显式把缩放设为 100%（1:1），画布居中。</summary>
        public void ZoomToHundred(Size canvasSize)
        {
            const float targetScale = 1.0f;
            var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
            _canvasViewState.LastScaleTo = targetScale;
            StartSpringPanAndZoomAnimation(targetScale, targetPosition, canvasSize);

            var screenFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, _imageSize, _canvasViewState.Rotation);
            IsFittedToScreen = Math.Abs(targetScale - screenFitScale) < ScaleTolerance;
            IsAtOneToOne = true;
        }

        /// <summary>把缩放设为 100% 但让视图中心落在 <paramref name="anchor"/> 指向的图片像素上。</summary>
        public void ZoomToHundred(Size canvasSize, Point anchor)
        {
            const float targetScale = 1.0f;
            _canvasViewState.LastScaleTo = 1.0f;

            // 计算让 anchor 在缩放后保持同屏位置的图片中心目标
            var oldScale = _canvasViewState.Scale;
            Point targetPos;
            if (Math.Abs(oldScale - targetScale) < 0.0001f)
                targetPos = anchor; // 已在 1:1，直接以锚点为中心
            else
                targetPos = ZoomGeometry.AnchorPreservingPan(anchor, _canvasViewState.ImagePos, oldScale, targetScale);

            StartSpringPanAndZoomAnimation(targetScale, targetPos, canvasSize);

            var screenFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, _imageSize, _canvasViewState.Rotation);
            IsFittedToScreen = Math.Abs(targetScale - screenFitScale) < ScaleTolerance;
            IsAtOneToOne = true;
        }

        /// <summary>显式用户操作：适应窗口（允许放大）。<paramref name="animateChange"/> 控制是否动画。</summary>
        public void ZoomPanToFit(bool animateChange, Size imageSize, Size canvasSize)
        {
            var scaleFactor = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);

            if (!animateChange)
            {
                _canvasViewState.Scale = scaleFactor;
                _canvasViewState.LastScaleTo = scaleFactor;
                _canvasViewState.ImagePos.X = canvasSize.Width / 2;
                _canvasViewState.ImagePos.Y = canvasSize.Height / 2;
                _canvasViewState.UpdateTransform();
                ViewChanged?.Invoke();
                ZoomChanged?.Invoke();
                IsFittedToScreen = true;
                IsAtOneToOne = Math.Abs(scaleFactor - 1.0f) < ScaleTolerance;
                return;
            }

            var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
            _canvasViewState.LastScaleTo = scaleFactor;
            StartSpringPanAndZoomAnimation(scaleFactor, targetPosition, canvasSize);

            // 立即设置 UI 状态（即使动画进行中）
            IsFittedToScreen = true;
            IsAtOneToOne = Math.Abs(scaleFactor - 1.0f) < ScaleTolerance;
        }

        /// <summary>按增量平移（拖动时每帧调用，直接生效跟手）。</summary>
        public void Pan(double dx, double dy)
        {
            if (_activeAnimation != null)
            {
                // 动画中途到来的拖动不能被下一帧 Tick 丢弃：把增量折进激活动画自身的平移状态，
                // 图片跟随光标，动画（如滚轮缩放弹簧）继续收敛。
                _activeAnimation.NudgePan(dx, dy);
            }
            else
            {
                _canvasViewState.ImagePos.X += dx;
                _canvasViewState.ImagePos.Y += dy;
                _canvasViewState.UpdateTransform();
            }
            ViewChanged?.Invoke();

            // 手动平移打破适应状态（不影响 1:1 状态）
            IsFittedToScreen = false;
        }

        /// <summary>
        /// 旋转 90 度（带平滑动画）：角度从当前值连续过渡到目标值（落定必为 90° 倍数），
        /// 旋转前若处于"适应窗口"，落定后平滑重新适应（宽高互换）。
        /// 目标角由逻辑方向累计推导，快速连点旋转时弹簧持续向最新目标收敛、方向不跳变。
        /// </summary>
        public void RotateBy(int rotation, Size canvasSize)
        {
            if (rotation % 90 != 0 || rotation == 0) return;

            var wasFitted = IsFittedToScreen;
            var newRotation = NormalizeRotation(_canvasViewState.Rotation + rotation);
            var targetAngle = ComputeRotationTarget(_canvasViewState.RotationAngle, newRotation, rotation);
            _canvasViewState.Rotation = newRotation;

            StartRotationAnimation(targetAngle, wasFitted, canvasSize);

            // 旋转进行中打破适应（动画完成后若原适应由 FinishRotation 恢复）
            IsFittedToScreen = false;
        }

        /// <summary>把旋转逻辑方向归一化到 [0, 360) 的 90° 倍数。</summary>
        private static int NormalizeRotation(int rotation)
        {
            var normalized = rotation % 360;
            return normalized < 0 ? normalized + 360 : normalized;
        }

        /// <summary>
        /// 计算旋转动画目标角：把逻辑方向（[0,360)）映射到当前角度所在周，
        /// 并沿旋转方向取"最近的等价位置"，避免 270→0 正向绕大圈、0→270 反向绕大圈。
        /// </summary>
        private static float ComputeRotationTarget(float currentAngle, int newRotation, int rotation)
        {
            var fullTurns = Math.Floor(currentAngle / 360f);
            var target = (float)(fullTurns * 360f) + newRotation;
            if (rotation > 0 && target < currentAngle) target += 360f;
            else if (rotation < 0 && target > currentAngle) target -= 360f;
            return target;
        }

        /// <summary>启动（或重新瞄准）旋转动画：向目标角收敛，落定后按需重新适应。</summary>
        private void StartRotationAnimation(float targetAngle, bool refitAfter, Size canvasSize)
        {
            var rotationAnim = _activeAnimation as RotationAnimation ?? new RotationAnimation(this);
            rotationAnim.Aim(targetAngle, refitAfter, canvasSize);

            // 旋转动画共享同一帧时钟：上一动画结束时秒表已 Stop、_lastSpringElapsedMs 停在旧值，
            // 若不重启则 TryGetDt 恒为 0，旋转弹簧永不推进（点击旋转无反应）。此处重启让计时恢复。
            _animationStopwatch.Restart();
            _lastSpringElapsedMs = 0;

            BeginAnimation(rotationAnim);
            ViewChanged?.Invoke();
        }

        /// <summary>触发"抖动"动画：操作被拒绝的视觉反馈（已保留实现，当前页面未使用）。</summary>
        public void Shrug()
        {
            if (PanZoomAnimationOnGoing) return;
            StartShrugAnimation();
        }

        // --- 动画编排 ---

        /// <summary>由 CanvasDisplayController 每帧调用，推进当前动画。</summary>
        public void OnUpdate() => _activeAnimation?.Tick();

        /// <summary>安装 <paramref name="animation"/> 为激活动画，并在其运行期间关闭像素取整（平滑亚像素运动）。</summary>
        private void BeginAnimation(IViewAnimation animation)
        {
            // 旋转动画持有"逻辑方向已更新、角度仍在半途"的状态：被其他类型动画替换前必须先吸附到目标，
            // 否则视图停留在中间角度而逻辑方向已是新值，状态不一致。
            // 旋转动画重瞄准自身时（快速连点旋转）Aim 已重新接管目标，不做吸附，避免视觉跳变。
            // 其他动画（平移/缩放）每帧都把实时视图写回，替换时无需吸附。
            if (_activeAnimation is RotationAnimation && animation is not RotationAnimation)
                _activeAnimation.CompleteImmediately();

            _activeAnimation = animation;
            _canvasViewState.SnapTranslation = false;
        }

        /// <summary>清除激活动画并恢复像素取整（调用方需在之后重建矩阵）。</summary>
        private void ClearActiveAnimation()
        {
            _activeAnimation = null;
            _canvasViewState.SnapTranslation = true;
        }

        /// <summary>
        /// 启动（或重新瞄准）一个纯缩放弹簧，保持屏幕锚点不动。
        /// 若弹簧已在运行，只改变目标/锚点——缩放速度延续，快速滚轮连续换挡平滑无重启顿挫。
        /// </summary>
        private void StartZoomAnimation(float targetScale, Point zoomAnchor)
        {
            // 仅在无弹簧运行时播种（重瞄准保留速度）。检测"全新锚定缩放"（对比重瞄准）须在换 _active 之前，
            // 使锚点轨迹只在全新开始时重置、重瞄准时保留（防止网格偏移反馈）。
            SeedScaleSpringIfFresh();
            if (_activeAnimation is AnchoredZoomAnimation zoom)
            {
                zoom.Aim(targetScale, zoomAnchor, resetAnchorTrack: false);
            }
            else
            {
                zoom = new AnchoredZoomAnimation(this);
                zoom.Aim(targetScale, zoomAnchor, resetAnchorTrack: true);
                BeginAnimation(zoom);
            }
            ViewChanged?.Invoke();
        }

        /// <summary>
        /// 启动（或重新瞄准）一个同时驱动缩放与平移的弹簧（适应/100%/步进/双击）。
        /// 重瞄准时速度延续。<paramref name="forceReseed"/> = true 时（如启动展开）即使弹簧在运行
        /// 也强制从实时视图重新播种并清零速度——这是有意的硬重置，不能继承旧弹簧内部状态。
        /// </summary>
        private void StartSpringPanAndZoomAnimation(float targetScale, Point targetPosition, Size targetCanvasSize, bool forceReseed = false)
        {
            SeedScaleSpringIfFresh(forceReseed);
            // 前一个动画不是平移弹簧时（如锚定缩放，它不追踪平移速度）、或强制重置时重新播种平移
            var reseedPan = forceReseed || _activeAnimation is not PanZoomAnimation;
            var panZoom = _activeAnimation as PanZoomAnimation ?? new PanZoomAnimation(this);
            panZoom.Aim(targetScale, targetPosition, targetCanvasSize, reseedPan);
            BeginAnimation(panZoom);
            ViewChanged?.Invoke();
        }

        /// <summary>
        /// 从实时视图播种缩放弹簧。弹簧已在运行且非强制时保留内部缩放位置/速度（无缝重瞄准）；
        /// <paramref name="force"/> = true 时强制覆盖（有意的硬重置，如启动展开）。
        /// </summary>
        private void SeedScaleSpringIfFresh(bool force = false)
        {
            if (!force && _activeAnimation is AnchoredZoomAnimation or PanZoomAnimation) return;
            _scaleSpring.Reset((float)Math.Log(_canvasViewState.Scale));
            _animationStopwatch.Restart();
            _lastSpringElapsedMs = 0;
        }

        /// <summary>启动抖动动画。</summary>
        private void StartShrugAnimation()
        {
            var shrug = new ShrugAnimation(this);
            shrug.Start();
            _animationStopwatch.Restart();
            BeginAnimation(shrug);
            ViewChanged?.Invoke();
        }

        /// <summary>
        /// 计算每帧 dt（秒，已钳制），长间隙（如渲染循环恢复后的首帧）不会让弹簧大步超调。
        /// 无时间流逝（与弹簧启动同一瞬间触发的 tick）返回 false。
        /// </summary>
        private bool TryGetSpringDt(out float dt)
        {
            var elapsed = _animationStopwatch.Elapsed.TotalMilliseconds;
            dt = (float)Math.Min((elapsed - _lastSpringElapsedMs) / 1000.0, SpringConstants.MaxDtSeconds);
            _lastSpringElapsedMs = elapsed;
            return dt > 0f;
        }

        /// <summary>
        /// 立即终止任何飞行中的动画，先把视图吸附到该动画的既定目标（后续导航因此继承"落定视图"
        /// 而非飞行中间帧）。不发 AnimationCompleted：调用方即将覆盖视图。
        /// </summary>
        private void StopAnimationSnappingToTarget()
        {
            // 先恢复取整，使 CompleteImmediately 重建出对齐像素网格的静止帧
            _canvasViewState.SnapTranslation = true;
            _activeAnimation?.CompleteImmediately();
            _animationStopwatch.Stop();
            _activeAnimation = null;
            _suppressZoomUpdateForNextAnimation = false;
        }

        // --- IAnimationHost 实现 ---

        CanvasViewState IAnimationHost.View => _canvasViewState;
        SpringAxis IAnimationHost.ScaleSpring => _scaleSpring;
        SpringAxis IAnimationHost.PanXSpring => _panXSpring;
        SpringAxis IAnimationHost.PanYSpring => _panYSpring;
        double IAnimationHost.ElapsedMs => _animationStopwatch.Elapsed.TotalMilliseconds;

        float IAnimationHost.TargetScale
        {
            get => _zoomTargetScale;
            set => _zoomTargetScale = value;
        }

        bool IAnimationHost.TryGetDt(out float dt) => TryGetSpringDt(out dt);

        void IAnimationHost.RaiseViewChanged() => ViewChanged?.Invoke();

        void IAnimationHost.RaiseZoomChanged()
        {
            if (!_suppressZoomUpdateForNextAnimation) ZoomChanged?.Invoke();
        }

        void IAnimationHost.ReportSettledFit(Size canvasSize)
        {
            if (canvasSize.Width <= 0 || canvasSize.Height <= 0) return;
            var screenFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, _imageSize, _canvasViewState.Rotation);
            IsFittedToScreen = Math.Abs(_zoomTargetScale - screenFitScale) < ScaleTolerance;
            IsAtOneToOne = Math.Abs(_zoomTargetScale - 1.0f) < ScaleTolerance;
        }

        void IAnimationHost.FinishSpring()
        {
            _animationStopwatch.Stop();
            _suppressZoomUpdateForNextAnimation = false;
            // 恢复取整后重建矩阵：动画已写入精确的目标 Scale/ImagePos，静止帧落在设备像素网格上
            ClearActiveAnimation();
            _canvasViewState.UpdateTransform();
            AnimationCompleted?.Invoke();
        }

        void IAnimationHost.FinishShrug()
        {
            _animationStopwatch.Stop();
            // 先恢复取整再重建矩阵；动画已把位置精确恢复到起点
            ClearActiveAnimation();
            _canvasViewState.UpdateTransform();
            ViewChanged?.Invoke();
        }

        void IAnimationHost.FinishRotation(bool refit, Size canvasSize)
        {
            _animationStopwatch.Stop();
            _suppressZoomUpdateForNextAnimation = false;
            // 先恢复取整再重建矩阵：旋转已吸附到目标角度，静止帧落在像素网格上
            ClearActiveAnimation();
            _canvasViewState.UpdateTransform();

            if (refit)
            {
                // 旋转前适应窗口：旋转后宽高互换，平滑重新适应（新图片有效尺寸由 rotation 判断）
                ZoomPanToFit(true,
                    new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height), canvasSize);
            }
            else
            {
                ViewChanged?.Invoke();
                AnimationCompleted?.Invoke();
            }
        }

        // --- 清理 ---

        public void Dispose()
        {
            ClearActiveAnimation();
            _animationStopwatch.Stop();
        }
    }
}
