using Microsoft.UI.Dispatching;
using SightoHear.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SightoHear.Services
{
    /// <summary>
    /// GPU 监控诊断服务：在应用内部持续采集 GPU 显存 / 帧性能 / 进程内存，
    /// 停止时生成一份完整诊断报告到日志文件夹，用于替代 WPR 抓取定位
    /// "浏览多个页面后 Win2D 渲染卡顿"的显存碎片化 / 爆显存 / 帧长抖根源。
    ///
    /// 采集维度：
    ///   1. 显存（DXGI <see cref="IDXGIAdapter3.QueryVideoMemoryInfo"/>，1Hz）：
    ///      专用显存段当前占用 / 预算、共享系统内存段占用——验证浏览页面时显存是否只涨不落
    ///   2. 帧性能（订阅 <see cref="Win2DPerformanceHud.FrameSample"/> 每帧累积）：
    ///      帧时长分布(avg/p95/p99/max)、掉帧率、Update/Draw 占比——区分 CPU 忙还是 GPU 忙
    ///   3. 进程内存（工作集 / 私有内存 / 托管堆）——与显存趋势对照
    ///   4. 结束时追加 <see cref="ResourceDiagnosticsService.BuildFullReport"/> 资源快照（页面泄漏/缓存/画布数）
    ///
    /// 线程模型：采样定时器在后台线程；每帧回调在 W2D 渲染线程；UI 线程只调用
    /// <see cref="GetStatusText"/>（读汇总）与 Start/Stop（切换状态）。全部数据用锁保护。
    /// </summary>
    public static class GpuMonitorService
    {
        // ═════════════════════════════ 状态 ═════════════════════════════

        /// <summary>是否正在监控中（UI 线程读取切换）</summary>
        public static bool IsMonitoring { get; private set; }

        /// <summary>本次监控开始时间</summary>
        private static DateTime _startTime;

        // ═════════════════════════════ 数据（锁保护） ═════════════════════════════

        private static readonly object _lock = new();

        /// <summary>每秒一次的环境采样点</summary>
        private sealed class GpuSample
        {
            public DateTime Time;
            public long LocalMb;       // 专用显存当前占用（MB）
            public long BudgetMb;      // 专用显存预算（MB）
            public long NonLocalMb;    // 共享系统内存段占用（MB）
            public long WorkingSetMb;  // 进程工作集
            public long PrivateMb;     // 进程私有内存
            public long ManagedHeapMb; // 托管堆总量
            public double RecentFrameMs; // 最近一帧时长
            public double RecentFps;     // 实时帧率
        }

        private static readonly List<GpuSample> _samples = new();

        // 帧数据（W2D 线程每帧累积）
        // ★ 预分配容量：监控 30 秒约 4000+ 帧，若默认 0 起步会在 W2D 渲染线程
        //   反复扩容分配（触发 GC），预分配避免热路径分配
        private static readonly List<double> _frameTimes = new(capacity: 8192);
        private static long _totalFrames;
        private static long _droppedFrames;
        private static double _accumUpdateMs;
        private static double _accumDrawMs;

        // Win2D 长帧事件（帧时长超过阈值时记录，供报告按时间列出）
        private sealed class LongFrameEvent
        {
            public DateTime Time;
            public double FrameMs;
            public double UpdateMs;
            public double DrawMs;
        }

        private const double LongFrameThresholdMs = 33; // 约 30fps 以下视为长帧
        private const int MaxLongFrames = 500;
        private static readonly List<LongFrameEvent> _longFrames = new();

        // UI 线程卡顿检测：DispatcherQueueTimer 心跳间隔异常增大 = UI 线程被阻塞
        private sealed class UiStutterEvent
        {
            public DateTime Time;
            public long DeltaMs; // 心跳实际间隔
        }

        private const int UiWatchIntervalMs = 100;    // 心跳间隔
        private const long UiStutterThresholdMs = 150; // 超过该间隔视为一次 UI 卡顿
        private const int MaxUiStutters = 200;
        private static readonly List<UiStutterEvent> _uiStutters = new();
        private static DispatcherQueueTimer? _uiWatchTimer;
        private static long _lastUiTickMs;

        private static Timer? _sampler;

        // ★ 修复：追踪 Win2D 渲染活跃状态。
        //   QueryVideoMemoryInfo 在 AMD 驱动上会与 Win2D 的 Present/Draw 竞争 KMD 全局锁，
        //   Win2D 活跃时跳过 VRAM 查询可消除 40-55ms 的周期性抽搐帧。
        private static long _lastWin2DFrameTick; // 最近一次 Win2D 帧回调的 TickCount64

        // ★ 修复：缓存 QueryVideoMemoryInfo vtable 委托，避免每 1 秒重建
        private static QueryVideoMemoryInfoDelegate? _cachedQueryVramDelegate;

        // ★ 修复：缓存 Process 实例，避免每秒采样创建 Process 对象（P/Invoke 开销大）
        private static System.Diagnostics.Process? _cachedProcess;

        // ═════════════════════════════ DXGI 状态 ═════════════════════════════

        private static bool _dxgiChecked;
        private static bool _dxgiAvailable;
        private static string _dxgiError = "";
        private static string _gpuDesc = "";
        private static long _vramCapacityMb;   // 显存总容量（DedicatedVideoMemory）
        private static ulong _adapterLuid;     // Win2D 实际使用适配器的 LUID（显存查询按此匹配）

        // ═════════════════════════════ DXGI 缓存（避免每秒重建工厂+枚举适配器） ═════════════════════════════

        /// <summary>
        /// 缓存的 IDXGIFactory1 指针（AddRef 持有，<see cref="Stop"/> 时释放）。
        /// 首次初始化后复用，不再每秒重建 DXGI 工厂与枚举适配器——
        /// AMD 驱动上 CreateDXGIFactory1 + EnumAdapters 会与 W2D 渲染线程的
        /// Present/Draw 竞争驱动级锁（GPU 监控开启期间每 1 秒一次的 UI 阻塞
        /// 与 Draw 长帧 60-80ms 即由此产生）。
        /// </summary>
        private static IntPtr _cachedFactory;

        /// <summary>缓存的 IDXGIAdapter 指针（AddRef 持有，<see cref="Stop"/> 时释放）。</summary>
        private static IntPtr _cachedAdapter;

        // ═════════════════════════════ 对外接口 ═════════════════════════════

        /// <summary>
        /// 开始 GPU 监控：重置统计、订阅每帧事件、启动 1Hz 后台采样器。
        /// 已在监控中时重复调用被忽略。
        /// </summary>
        /// <param name="uiQueue">
        /// UI 线程的 DispatcherQueue（DebugPage 在 Loaded/点击时传入）。非空时启动
        /// UI 线程卡顿检测：以 100ms 心跳监测 UI 线程响应性，间隔异常增大即记录卡顿事件。
        /// </param>
        public static void Start(DispatcherQueue? uiQueue = null)
        {
            lock (_lock)
            {
                if (IsMonitoring) return;
                IsMonitoring = true;
                _startTime = DateTime.Now;
                _samples.Clear();
                _frameTimes.Clear();
                _totalFrames = 0;
                _droppedFrames = 0;
                _accumUpdateMs = 0;
                _accumDrawMs = 0;
                _longFrames.Clear();
                _uiStutters.Clear();
                _lastUiTickMs = Environment.TickCount64;
            }

            // 首次启动时初始化 DXGI 查询能力（失败不影响帧/内存监控）
            EnsureDxgiInitialized();

            Win2DPerformanceHud.FrameSample += OnFrameSample;
            _sampler = new Timer(_ => SampleTick(), null, 1000, 1000);

            // UI 线程卡顿检测（需在拥有 DispatcherQueue 的线程创建 timer）
            if (uiQueue != null && _uiWatchTimer == null)
            {
                try
                {
                    _uiWatchTimer = uiQueue.CreateTimer();
                    _uiWatchTimer.Interval = TimeSpan.FromMilliseconds(UiWatchIntervalMs);
                    _uiWatchTimer.IsRepeating = true;
                    _uiWatchTimer.Tick += OnUiWatchTick;
                    _uiWatchTimer.Start();
                }
                catch
                {
                    _uiWatchTimer = null; // UI 检测不可用时降级：仅保留帧/显存/内存监控
                }
            }
        }

        /// <summary>
        /// 停止监控并生成报告文件（写入日志文件夹）。返回报告文件完整路径，
        /// 写入失败返回空字符串。
        /// </summary>
        public static string Stop()
        {
            // ★ 修复：先停止采样器并等待在途回调结束（Timer.Dispose(WaitHandle)），
            //   确保没有采样回调正在 QueryVram 中使用缓存指针，随后才能安全释放 COM 引用
            if (_sampler != null)
            {
                var samplerDone = new System.Threading.ManualResetEventSlim(false);
                try
                {
                    _sampler.Dispose(samplerDone.WaitHandle);
                    samplerDone.Wait(3000); // 最多等待 3 秒，回调通常毫秒级完成
                }
                catch { /* 等待失败不阻塞停止流程 */ }
                _sampler = null;
            }
            Win2DPerformanceHud.FrameSample -= OnFrameSample;

            // 停止 UI 卡顿检测心跳
            if (_uiWatchTimer != null)
            {
                try
                {
                    _uiWatchTimer.Tick -= OnUiWatchTick;
                    _uiWatchTimer.Stop();
                }
                catch { /* 忽略停止失败 */ }
                _uiWatchTimer = null;
            }

            // 停止前补一次即时采样（当前时刻的环境）
            SampleTick();

            // ★ 释放缓存的 DXGI 工厂/适配器指针（采样器已停，无并发访问）
            ReleaseCachedDxgi();

            lock (_lock)
            {
                IsMonitoring = false;
                string report = BuildReportLocked();
                return WriteReport(report);
            }
        }

        /// <summary>
        /// 当前监控状态摘要（UI 线程每 1 秒调用刷新显示）。
        /// </summary>
        public static string GetStatusText()
        {
            lock (_lock)
            {
                if (!IsMonitoring)
                    return "未在监控。点击「开始 GPU 监控」后按复现路径操作（浏览页面 → 打开播放器），再点「停止并生成报告」。";

                var elapsed = DateTime.Now - _startTime;
                GpuSample? last = _samples.Count > 0 ? _samples[_samples.Count - 1] : null;
                string vram = _dxgiAvailable && last != null
                    ? $"{last.LocalMb} / {last.BudgetMb} MB"
                    : (_dxgiAvailable ? "读取中..." : "不可用");
                string elapsedText = elapsed.ToString(@"mm\:ss");
                string fpsText = last != null && last.RecentFps > 0 ? last.RecentFps.ToString("F0") : "";
                return $"监控中 {elapsedText} · 显存 {vram} MB · 帧 {_totalFrames}（掉帧 {_droppedFrames}）· 内存 {last?.WorkingSetMb ?? 0} MB · {fpsText} fps";
            }
        }

        // ═════════════════════════════ 每帧回调（W2D 线程） ═════════════════════════════

        private static void OnFrameSample(double frameTimeMs, double updateMs, double drawMs)
        {
            // ★ 记录最近一次 Win2D 帧时间戳（TickCount64），用于后续 VRAM 查询门控
            _lastWin2DFrameTick = Environment.TickCount64;

            lock (_lock)
            {
                _frameTimes.Add(frameTimeMs);
                _totalFrames++;
                _accumUpdateMs += updateMs;
                _accumDrawMs += drawMs;
                if (frameTimeMs > 40) // 与 HUD 的掉帧阈值保持一致
                    _droppedFrames++;

                // 长帧事件：记录单帧耗时与 Update/Draw 拆解，报告按时间列出
                if (frameTimeMs > LongFrameThresholdMs)
                {
                    _longFrames.Add(new LongFrameEvent
                    {
                        Time = DateTime.Now,
                        FrameMs = frameTimeMs,
                        UpdateMs = updateMs,
                        DrawMs = drawMs
                    });
                    if (_longFrames.Count > MaxLongFrames)
                        _longFrames.RemoveAt(0);
                }
            }
        }

        // ═════════════════════════════ UI 线程卡顿检测（UI 线程心跳） ═════════════════════════════

        /// <summary>
        /// UI 线程心跳（DispatcherQueueTimer，100ms 一次）。
        /// 若实际间隔异常增大（&gt;150ms），说明 UI 线程被阻塞（同步 I/O / GC / 布局 /
        /// 事件风暴等），记录一次卡顿事件。UI 线程被阻塞会直接导致画面掉帧，
        /// 与 Win2D 帧长事件时间对齐即可区分"UI 忙"与"GPU 忙"。
        /// </summary>
        private static void OnUiWatchTick(DispatcherQueueTimer sender, object args)
        {
            long now = Environment.TickCount64;
            long delta = now - _lastUiTickMs;
            _lastUiTickMs = now;

            if (delta > UiStutterThresholdMs)
            {
                lock (_lock)
                {
                    _uiStutters.Add(new UiStutterEvent { Time = DateTime.Now, DeltaMs = delta });
                    if (_uiStutters.Count > MaxUiStutters)
                        _uiStutters.RemoveAt(0);
                }
            }
        }

        // ═════════════════════════════ 采样（后台线程，1Hz） ═════════════════════════════

        private static void SampleTick()
        {
            var s = new GpuSample { Time = DateTime.Now };

            // ★ 门控：Win2D 活跃渲染时跳过 VRAM 查询。
            //   QueryVideoMemoryInfo 进入 AMD KMD 后可能与 Win2D Present/Draw 竞争驱动全局锁，
            //   导致渲染线程在 Present(0) 处阻塞 40-55ms（"浏览多页面后抽搐"的根因）。
            //   帧回调最近 2 秒内有触发 → Win2D 正在渲染 → 跳过 VRAM 查询，使用零值占位。
            bool win2dActive = (Environment.TickCount64 - _lastWin2DFrameTick) < 2000;
            if (!win2dActive)
            {
                QueryVram(out long localMb, out long budgetMb, out long nonLocalMb);
                s.LocalMb = localMb;
                s.BudgetMb = budgetMb;
                s.NonLocalMb = nonLocalMb;
            }
            // else: Win2D 活跃时 localMb/budgetMb/nonLocalMb 保持默认 0

            // 进程内存（缓存 Process 实例，避免每秒创建 Process 对象）
            try
            {
                _cachedProcess ??= Process.GetCurrentProcess();
                _cachedProcess.Refresh(); // 刷新缓存值，避免读取过期快照
                s.WorkingSetMb = _cachedProcess.WorkingSet64 / 1024 / 1024;
                s.PrivateMb = _cachedProcess.PrivateMemorySize64 / 1024 / 1024;
            }
            catch { /* 忽略单次读取失败 */ }
            s.ManagedHeapMb = GC.GetTotalMemory(false) / 1024 / 1024;

            // 最近帧（读 HUD 快照）
            var snap = Win2DPerformanceHud.GetSnapshot();
            s.RecentFrameMs = snap.FrameTimeMs;
            s.RecentFps = snap.Fps;

            lock (_lock)
            {
                _samples.Add(s);
                // 防止超长监控撑爆内存：最多保留 7200 个采样点（2 小时）
                if (_samples.Count > 7200)
                    _samples.RemoveAt(0);
            }
        }

        // ═════════════════════════════ 报告生成 ═════════════════════════════

        /// <summary>构建报告文本。必须在 <see cref="_lock"/> 锁内调用。</summary>
        private static string BuildReportLocked()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════ GPU 监控诊断报告 ═══════════════════════════════");
            sb.AppendLine($"  生成时间 : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  监控时段 : {_startTime:HH:mm:ss} → {DateTime.Now:HH:mm:ss}  ({(DateTime.Now - _startTime):mm\\:ss})");
            sb.AppendLine($"  GPU      : {(_gpuDesc.Length > 0 ? _gpuDesc : "未获取")} | 显存容量 {_vramCapacityMb} MB");
            sb.AppendLine($"  显存查询 : {(_dxgiAvailable ? "可用 (DXGI QueryVideoMemoryInfo)" : "不可用: " + _dxgiError)}");
            sb.AppendLine();

            AppendFrameReport(sb);
            sb.AppendLine();
            AppendVramReport(sb);
            sb.AppendLine();
            AppendMemoryReport(sb);
            sb.AppendLine();
            AppendUiStutterReport(sb);
            sb.AppendLine();
            AppendLongFrameReport(sb);
            sb.AppendLine();

            // 附：结束时刻的资源快照（页面泄漏 / 缓存 / Win2D 画布数）
            sb.AppendLine("── 附: 停止时刻资源快照 ──");
            try
            {
                sb.AppendLine(ResourceDiagnosticsService.BuildFullReport(null, "GPU 监控停止"));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  (资源快照失败: {ex.GetType().Name}: {ex.Message})");
            }

            sb.AppendLine("═══════════════════════════════ 报告结束 ═══════════════════════════════");
            return sb.ToString();
        }

        /// <summary>帧性能统计段。必须在 <see cref="_lock"/> 锁内调用。</summary>
        private static void AppendFrameReport(StringBuilder sb)
        {
            sb.AppendLine("── Win2D 帧性能统计 ──");
            if (_totalFrames <= 0 || _frameTimes.Count == 0)
            {
                sb.AppendLine("  (监控期间没有 Win2D 渲染循环活动——需进入播放器/图片查看器才能采集帧数据)");
                return;
            }

            var sorted = new List<double>(_frameTimes);
            sorted.Sort();
            double sum = 0;
            foreach (var t in sorted) sum += t;
            double avg = sum / sorted.Count;
            double p50 = sorted[sorted.Count / 2];
            double p95 = sorted[(int)(sorted.Count * 0.95)];
            double p99 = sorted[(int)(sorted.Count * 0.99)];
            double max = sorted[sorted.Count - 1];
            double avgUpdate = _accumUpdateMs / _totalFrames;
            double avgDraw = _accumDrawMs / _totalFrames;
            double cpuShare = avg > 0 ? (avgUpdate + avgDraw) * 100.0 / avg : 0;

            sb.AppendLine($"  总帧数      : {_totalFrames}");
            sb.AppendLine($"  帧时长(ms)  : 平均 {avg:F2} | 中位 {p50:F2} | p95 {p95:F2} | p99 {p99:F2} | 最大 {max:F2}");
            sb.AppendLine($"  掉帧(>40ms) : {_droppedFrames} 次 ({_droppedFrames * 100.0 / _totalFrames:F2}%)");
            sb.AppendLine($"  平均耗时    : Update {avgUpdate:F3} ms | Draw {avgDraw:F3} ms");
            sb.AppendLine($"  性能画像    : CPU 渲染耗时占帧时长 {cpuShare:F1}%，其余为 GPU/Present 等待");
            if (cpuShare < 60)
                sb.AppendLine("  推断        : 大部分帧时间花在 GPU 或 Present——倾向 GPU 侧瓶颈/显存碎片化");
            else
                sb.AppendLine("  推断        : Update/Draw 占帧时长大头——倾向 CPU 侧瓶颈（业务逻辑/资源提交）");
        }

        /// <summary>显存趋势段。必须在 <see cref="_lock"/> 锁内调用。</summary>
        private static void AppendVramReport(StringBuilder sb)
        {
            sb.AppendLine("── 显存趋势 (每秒采样) ──");
            if (_samples.Count == 0)
            {
                sb.AppendLine("  (无采样数据)");
                return;
            }

            if (!_dxgiAvailable)
            {
                sb.AppendLine($"  显存查询不可用，跳过趋势。错误: {_dxgiError}");
                return;
            }

            // 汇总：专用段起始/峰值/结束，共享段峰值
            GpuSample first = _samples[0];
            GpuSample last = _samples[_samples.Count - 1];
            GpuSample peakLocal = first;
            GpuSample peakNonLocal = first;
            foreach (var s in _samples)
            {
                if (s.LocalMb > peakLocal.LocalMb) peakLocal = s;
                if (s.NonLocalMb > peakNonLocal.NonLocalMb) peakNonLocal = s;
            }

            sb.AppendLine($"  采样点数    : {_samples.Count}");
            sb.AppendLine($"  专用显存占用: 起始 {first.LocalMb} MB → 峰值 {peakLocal.LocalMb} MB ({peakLocal.Time:HH:mm:ss}) → 结束 {last.LocalMb} MB");
            sb.AppendLine($"  显存预算    : 结束 {last.BudgetMb} MB | 共享段峰值 {peakNonLocal.NonLocalMb} MB");
            long delta = last.LocalMb - first.LocalMb;
            sb.AppendLine($"  趋势        : 监控期间占用{(delta >= 0 ? "上升 " : "回落 ") + Math.Abs(delta)} MB" +
                          (delta > 64 ? "  ← 需重点排查是否只涨不落（显存泄漏/碎片化）" : ""));
            sb.AppendLine();
            sb.AppendLine("  时间        显存占用  预算      共享段   工作集   实时帧率");
            foreach (var s in _samples)
            {
                sb.AppendLine($"  {s.Time:HH:mm:ss}  {s.LocalMb,8} {s.BudgetMb,8} {s.NonLocalMb,8} {s.WorkingSetMb,7} " +
                              $"{(s.RecentFps > 0 ? s.RecentFps.ToString("F0") : "-"),5} fps");
            }
        }

        /// <summary>进程内存段。必须在 <see cref="_lock"/> 锁内调用。</summary>
        private static void AppendMemoryReport(StringBuilder sb)
        {
            sb.AppendLine("── 进程内存趋势 ──");
            if (_samples.Count == 0)
            {
                sb.AppendLine("  (无采样数据)");
                return;
            }

            GpuSample first = _samples[0];
            GpuSample last = _samples[_samples.Count - 1];
            GpuSample peakWs = first;
            foreach (var s in _samples)
                if (s.WorkingSetMb > peakWs.WorkingSetMb) peakWs = s;

            sb.AppendLine($"  工作集 : 起始 {first.WorkingSetMb} MB → 峰值 {peakWs.WorkingSetMb} MB ({peakWs.Time:HH:mm:ss}) → 结束 {last.WorkingSetMb} MB");
            sb.AppendLine($"  私有内存: 结束 {last.PrivateMb} MB | 托管堆: 结束 {last.ManagedHeapMb} MB");
            long wsDelta = last.WorkingSetMb - first.WorkingSetMb;
            sb.AppendLine($"  趋势   : 监控期间工作集{(wsDelta >= 0 ? "上升 " : "回落 ") + Math.Abs(wsDelta)} MB");
        }

        /// <summary>UI 线程卡顿事件段。必须在 <see cref="_lock"/> 锁内调用。</summary>
        private static void AppendUiStutterReport(StringBuilder sb)
        {
            sb.AppendLine("── UI 线程卡顿事件 (心跳间隔 > 150ms) ──");
            sb.AppendLine("  说明: 间隔异常增大 = UI 线程被阻塞（同步 I/O / GC / 布局 / 事件风暴）。");
            sb.AppendLine("        若此处有事件且同时刻 Win2D 也有长帧 → 卡顿源于 UI 线程忙;");
            sb.AppendLine("        若此处无事件但 Win2D 有长帧 → 卡顿源于渲染管线/GPU 侧。");

            if (_uiStutters.Count == 0)
            {
                sb.AppendLine("  (监控期间 UI 线程无卡顿)");
                return;
            }

            long maxDelta = 0;
            foreach (var s in _uiStutters)
                if (s.DeltaMs > maxDelta) maxDelta = s.DeltaMs;

            sb.AppendLine($"  合计 {_uiStutters.Count} 次 | 最长阻塞 {maxDelta} ms");
            sb.AppendLine("  时间        阻塞(ms)  同时刻显存  同时刻工作集  同时刻帧时长");
            foreach (var s in _uiStutters)
            {
                var nearby = FindClosestSample(s.Time);
                sb.AppendLine($"  {s.Time:HH:mm:ss}  {s.DeltaMs,7} {NearbyMb(nearby?.LocalMb),10} " +
                              $"{NearbyMb(nearby?.WorkingSetMb),11} {(nearby != null && nearby.RecentFrameMs > 0 ? nearby.RecentFrameMs.ToString("F1") : "-"),10} ms");
            }
        }

        /// <summary>Win2D 长帧事件段。必须在 <see cref="_lock"/> 锁内调用。</summary>
        private static void AppendLongFrameReport(StringBuilder sb)
        {
            sb.AppendLine($"── Win2D 长帧事件 (帧时长 > {LongFrameThresholdMs:0}ms) ──");
            sb.AppendLine("  说明: Update 长 = CPU 侧忙; Draw 长 = GPU 提交慢; 两者都短 = GPU/Present 等待。");

            if (_longFrames.Count == 0)
            {
                sb.AppendLine("  (监控期间无长帧——渲染流畅)");
                return;
            }

            sb.AppendLine($"  合计 {_longFrames.Count} 次（最多记录 {MaxLongFrames} 条）");
            sb.AppendLine("  时间        帧时长(ms)  Update(ms)  Draw(ms)  同时刻显存");
            foreach (var f in _longFrames)
            {
                var nearby = FindClosestSample(f.Time);
                sb.AppendLine($"  {f.Time:HH:mm:ss}  {f.FrameMs,9:F1} {f.UpdateMs,9:F1} {f.DrawMs,7:F1} {NearbyMb(nearby?.LocalMb),10}");
            }
        }

        /// <summary>将 long? 格式化为 "1234 MB" 或 "-"。</summary>
        private static string NearbyMb(long? mb) => mb.HasValue ? $"{mb.Value} MB" : "-";

        /// <summary>在 1Hz 采样序列中二分查找时间最接近的样本（_samples 按时间单调递增）。</summary>
        private static GpuSample? FindClosestSample(DateTime time)
        {
            int lo = 0, hi = _samples.Count - 1;
            if (hi < 0) return null;

            // 二分定位第一个 >= time 的位置
            int pos = hi;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_samples[mid].Time < time)
                    lo = mid + 1;
                else
                {
                    pos = mid;
                    hi = mid - 1;
                }
            }

            // 比较 pos 与 pos-1 哪个更接近
            GpuSample best = _samples[pos];
            if (pos > 0 && (best.Time - time).Duration() > (time - _samples[pos - 1].Time))
                best = _samples[pos - 1];
            return best;
        }

        /// <summary>报告文本写入日志文件夹。返回路径，失败返回空字符串。</summary>
        private static string WriteReport(string content)
        {
            try
            {
                string dir = AppLogger.GetLogFolderPath();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"GPU监控报告-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(path, content, new UTF8Encoding(false));
                AppLogger.Info($"GPU 监控报告已生成: {path}");
                return path;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "GPU 监控报告写入失败");
                return "";
            }
        }

        // ═════════════════════════════ DXGI 显存查询（COM vtable 直接调用） ═════════════════════════════

        // IDXGIFactory1
        private static readonly Guid IDXGIFactory1Guid = new("770aae78-f26f-4dba-a829-253c83d1b387");

        // DXGI_MEMORY_SEGMENT_GROUP
        private const uint DXGI_MEMORY_SEGMENT_GROUP_LOCAL = 0;      // 专用显存段
        private const uint DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1;  // 共享系统内存段

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_QUERY_VIDEO_MEMORY_INFO
        {
            public ulong Budget;              // 驱动当前建议预算（字节）
            public ulong CurrentUsage;        // 当前实际使用（字节）
            public ulong AvailableForReservation;
            public ulong CurrentReservation;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;   // 专用显存容量（字节）
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public ulong AdapterLuid;              // 适配器 LUID（用于匹配实际渲染设备）
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdaptersDelegate(IntPtr factory, uint adapterIndex, out IntPtr ppAdapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDescDelegate(IntPtr adapter, out DXGI_ADAPTER_DESC desc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryVideoMemoryInfoDelegate(IntPtr adapter, uint nodeIndex, uint memorySegmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO info);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceDelegate(IntPtr p, ref Guid riid, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetAdapterDelegate(IntPtr dxgiDevice, out IntPtr adapter);

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
            uint flags, IntPtr featureLevels, uint numLevels, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr context);

        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const uint D3D11_SDK_VERSION = 7;
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20; // Win2D/Direct2D 必需

        private static readonly Guid IDXGIDeviceGuid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

        /// <summary>从 COM 对象 vtable 指定槽位取函数指针并转为委托。</summary>
        private static T GetVtableDelegate<T>(IntPtr comPtr, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(comPtr);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }

        /// <summary>
        /// 初始化 DXGI 查询能力：手动指定 GPU 时直接取 <see cref="Win2DDeviceManager.CustomDeviceLuid"/>
        /// 对应的适配器（与 Win2D 实际渲染设备一致），避免再建临时 D3D11 设备去查"默认 GPU"；
        /// 跟随系统时沿用原有 D3D11 默认设备查询。
        /// </summary>
        private static void EnsureDxgiInitialized()
        {
            if (_dxgiChecked) return;
            _dxgiChecked = true;

            try
            {
                // 手动指定 GPU：直接用 Win2D 实际绑定的适配器 LUID，枚举取描述/显存
                if (Win2DDeviceManager.CustomDevice != null && Win2DDeviceManager.CustomDeviceLuid != 0)
                {
                    _adapterLuid = Win2DDeviceManager.CustomDeviceLuid;
                    IntPtr factory;
                    Guid g = IDXGIFactory1Guid;
                    if (CreateDXGIFactory1(ref g, out factory) != 0) return;
                    // ★ 缓存工厂指针：引用计数 1 由本服务持有，Stop 时统一释放，
                    //   避免每次采样重建 DXGI 工厂引发 AMD 驱动级锁竞争
                    _cachedFactory = factory;
                    try
                    {
                        var enumAdapters = GetVtableDelegate<EnumAdaptersDelegate>(factory, 7);
                        uint idx = 0;
                        while (true)
                        {
                            IntPtr adapter;
                            if (enumAdapters(factory, idx, out adapter) != 0) break;
                            idx++;
                            var getDesc = GetVtableDelegate<GetDescDelegate>(adapter, 8);
                            DXGI_ADAPTER_DESC desc;
                            if (getDesc(adapter, out desc) != 0 || desc.AdapterLuid != _adapterLuid)
                            {
                                Marshal.Release(adapter); // 不匹配：归还临时引用
                                continue;
                            }

                            _gpuDesc = desc.Description?.Trim() ?? "";
                            _vramCapacityMb = (long)(desc.DedicatedVideoMemory.ToUInt64() / 1024 / 1024);

                            var queryVram = GetVtableDelegate<QueryVideoMemoryInfoDelegate>(adapter, 14);
                            int hr = queryVram(adapter, 0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out var info);
                            _dxgiAvailable = hr == 0;
                            if (!_dxgiAvailable)
                                _dxgiError = $"QueryVideoMemoryInfo=0x{hr:X8}";

                            // ★ 缓存匹配的适配器指针：引用计数 1 由本服务持有，Stop 时统一释放，
                            //   采样直接复用，不再每秒枚举适配器（AMD 驱动上枚举会与渲染竞争锁）
                            _cachedAdapter = adapter;
                            break;
                        }
                    }
                    catch
                    {
                        // 初始化失败时释放已缓存指针，避免 COM 泄漏；下次 Start 会重新初始化
                        ReleaseCachedDxgi();
                        throw;
                    }
                    return;
                }

                // 跟随系统：查询 D3D11 默认硬件设备实际绑定的 GPU
                IntPtr device;
                int featureLevel;
                IntPtr context;
                int hrDev = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out featureLevel, out context);
                if (hrDev != 0) { _dxgiAvailable = false; _dxgiError = $"D3D11CreateDevice=0x{hrDev:X8}"; return; }
                try
                {
                    // ID3D11Device → IDXGIDevice → IDXGIAdapter，取实际渲染适配器
                    Guid iid = IDXGIDeviceGuid;
                    var qi = GetVtableDelegate<QueryInterfaceDelegate>(device, 0);
                    IntPtr dxgiDevice;
                    hrDev = qi(device, ref iid, out dxgiDevice);
                    if (hrDev != 0) { _dxgiAvailable = false; _dxgiError = $"QI(IDXGIDevice)=0x{hrDev:X8}"; return; }
                    try
                    {
                        var getAdapter = GetVtableDelegate<GetAdapterDelegate>(dxgiDevice, 7);
                        IntPtr adapter;
                        hrDev = getAdapter(dxgiDevice, out adapter);
                        if (hrDev != 0) { _dxgiAvailable = false; _dxgiError = $"GetAdapter=0x{hrDev:X8}"; return; }
                        // ★ 缓存适配器指针：引用计数 1 由本服务持有，Stop 时统一释放
                        _cachedAdapter = adapter;
                        try
                        {
                            var getDesc = GetVtableDelegate<GetDescDelegate>(adapter, 8);
                            DXGI_ADAPTER_DESC desc;
                            if (getDesc(adapter, out desc) == 0)
                            {
                                _gpuDesc = desc.Description?.Trim() ?? "";
                                _vramCapacityMb = (long)(desc.DedicatedVideoMemory.ToUInt64() / 1024 / 1024);
                                _adapterLuid = desc.AdapterLuid;
                            }

                            var queryVram = GetVtableDelegate<QueryVideoMemoryInfoDelegate>(adapter, 14);
                            hrDev = queryVram(adapter, 0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out var info);
                            _dxgiAvailable = hrDev == 0;
                            if (!_dxgiAvailable)
                                _dxgiError = $"QueryVideoMemoryInfo=0x{hrDev:X8}";
                        }
                        finally { /* 适配器指针已缓存，不在此处释放（Stop 统一释放） */ }
                    }
                    finally { Marshal.Release(dxgiDevice); }
                }
                finally { Marshal.Release(context); Marshal.Release(device); }
            }
            catch (Exception ex)
            {
                _dxgiAvailable = false;
                _dxgiError = $"{ex.GetType().Name}: {ex.Message}";
                // 初始化失败：释放已缓存指针，避免 COM 泄漏；下次 Start 重新初始化
                ReleaseCachedDxgi();
            }
        }

        /// <summary>
        /// 查询当前显存占用（仅针对 Win2D 实际使用的适配器）。
        /// 直接复用 <see cref="_cachedAdapter"/> 缓存的适配器指针调用 QueryVideoMemoryInfo，
        /// 不再每秒重建 DXGI 工厂 / 枚举适配器——AMD 驱动上 CreateDXGIFactory1 + EnumAdapters
        /// 会与 W2D 渲染线程的 Present/Draw 竞争驱动级锁，导致监控期间每 1 秒一次的
        /// UI 线程阻塞与 Draw 长帧（60-80ms）。失败时输出 0 并保持前值。
        /// </summary>
        private static void QueryVram(out long localMb, out long budgetMb, out long nonLocalMb)
        {
            localMb = 0; budgetMb = 0; nonLocalMb = 0;
            if (!_dxgiAvailable) return;

            IntPtr adapter = _cachedAdapter;
            if (adapter == IntPtr.Zero) return;

            try
            {
                // ★ 缓存委托：首次调用创建后复用，避免每 1 秒 Marshal.GetDelegateForFunctionPointer
                _cachedQueryVramDelegate ??= GetVtableDelegate<QueryVideoMemoryInfoDelegate>(adapter, 14);
                var queryVram = _cachedQueryVramDelegate;

                if (queryVram(adapter, 0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out var local) == 0)
                {
                    localMb = (long)(local.CurrentUsage / 1024 / 1024);
                    budgetMb = (long)(local.Budget / 1024 / 1024);
                }
                if (queryVram(adapter, 0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, out var nonLocal) == 0)
                    nonLocalMb = (long)(nonLocal.CurrentUsage / 1024 / 1024);
            }
            catch { /* 单次查询失败静默，下一轮重试 */ }
        }

        /// <summary>
        /// 释放缓存的 DXGI 工厂 / 适配器 COM 指针并重置初始化状态。
        /// 必须在采样器停止（<see cref="_sampler"/> 已 Dispose）且不再有采样回调
        /// 并发访问缓存指针后调用（<see cref="Stop"/> 的锁内）。释放后允许
        /// 下次 <see cref="Start"/> 重新枚举（设备变化时适配器指针会失效）。
        /// </summary>
        private static void ReleaseCachedDxgi()
        {
            // ★ 修复：不在 Stop 时 Marshal.Release COM 指针。
            //   Release → 引用计数归零 → COM 对象析构 → AMD KMD 内部同步操作，
            //   与 Win2D 的 Present/Draw 竞争驱动锁导致渲染抽搐。
            //   COM 对象在进程退出时自动清理，此处仅重置指针和状态即可。
            _cachedFactory = IntPtr.Zero;
            _cachedAdapter = IntPtr.Zero;
            _cachedQueryVramDelegate = null;
            _dxgiChecked = false; // 允许下次监控重新初始化（适配器可能已变化）
        }
    }
}
