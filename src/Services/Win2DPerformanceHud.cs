using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;

namespace SightoHear.Services
{
    /// <summary>
    /// Win2D 性能监测悬浮窗（HUD）全局状态控制器。
    /// 负责持有 HUD 的启用状态、监测行开关与当前界面类型，
    /// 并通过 <see cref="Changed"/> 事件通知 MainWindow 刷新悬浮窗显示。
    /// 同时负责性能数据采集：各 Win2D 渲染循环（W2D 线程）每帧上报
    /// 帧时长 / Update 耗时 / Draw 耗时 / 画布尺寸，UI 线程定时读取快照显示。
    /// </summary>
    public static class Win2DPerformanceHud
    {
        /// <summary>设置或界面状态变化时触发，MainWindow 订阅后刷新悬浮窗</summary>
        public static event Action? Changed;

        /// <summary>
        /// 每帧数据上报事件（W2D 渲染线程触发，在 <see cref="ReportFrame"/> 锁外调用）。
        /// 参数依次为：帧时长 / Update 耗时 / Draw 耗时（毫秒）。
        /// 供 GPU 监控（<see cref="GpuMonitorService"/>）等外部组件累积全量帧数据；
        /// 无订阅者为空判断，不影响渲染热路径。
        /// </summary>
        public static event Action<double, double, double>? FrameSample;

        // ── 启用状态 ──
        /// <summary>总开关：是否启用 Win2D 性能监测悬浮窗</summary>
        public static bool IsEnabled { get; set; }

        // ── 界面状态（由 MainWindow 维护）──
        /// <summary>当前是否处于 Win2D 界面（图片查看器 / 音乐播放器）</summary>
        public static bool IsWin2DSurface { get; set; }

        // ── 监测行开关（与设置页展开区一一对应）──
        public static bool ShowFps { get; set; } = true;
        public static bool ShowAvgFps { get; set; }
        public static bool ShowFrameTime { get; set; }
        public static bool ShowUpdateTime { get; set; }
        public static bool ShowDrawTime { get; set; }
        public static bool ShowFrameJitter { get; set; }
        public static bool ShowDroppedFrames { get; set; }
        public static bool ShowMemory { get; set; }
        public static bool ShowResolution { get; set; }
        public static bool ShowGpuMode { get; set; }

        // ═════════════════════════════ 性能数据采集 ═════════════════════════════
        // 线程模型：写端只有 W2D 渲染线程（单线程，两个页面不会同时渲染），
        // 读端为 UI 线程（MainWindow 定时器）。用锁保护跨线程读写一致性。

        /// <summary>帧时长滑动窗口容量（约 2 秒 @60fps），用于 FPS 与抖动计算。</summary>
        private const int WindowFrames = 120;

        /// <summary>掉帧判定阈值（毫秒）：单帧时长超过该值计一次掉帧。</summary>
        private const double DroppedFrameThresholdMs = 40;

        private static readonly object _sync = new();
        private static readonly Queue<double> _frameTimes = new();
        private static long _totalFrames;               // 会话累计帧数
        private static double _accumFrameTimeMs;        // 会话累计帧时长
        private static long _droppedFrames;             // 会话累计掉帧数
        private static double _lastFrameTimeMs;         // 最近一帧时长
        private static double _lastUpdateMs;            // 最近一帧 Update 耗时
        private static double _lastDrawMs;              // 最近一帧 Draw 耗时
        private static double _surfaceDipWidth;         // 画布 DIP 宽度
        private static double _surfaceDipHeight;        // 画布 DIP 高度
        private static double _surfaceDpi = 1;          // 画布 DPI 缩放
        private static bool _gpuInfoLoaded;
        private static string _gpuInfo = "";

        // ★ 修复：缓存 Process 实例，避免 HUD 定时器（500ms）与 GPU 采样（1Hz）
        //   高频调用 Process.GetCurrentProcess()（每次创建 Process 对象，P/Invoke 开销大，
        //   且在 UI 线程执行会引入不必要的停顿）
        private static readonly Process _cachedProcess = Process.GetCurrentProcess();

        /// <summary>HUD 性能快照（UI 线程读取）。</summary>
        public readonly struct HudSnapshot
        {
            public double Fps { get; init; }             // 实时帧率（滑动窗口平均）
            public double AvgFps { get; init; }          // 会话平均帧率
            public double FrameTimeMs { get; init; }     // 最近一帧时长
            public double UpdateMs { get; init; }        // 最近一帧 Update 耗时
            public double DrawMs { get; init; }          // 最近一帧 Draw 耗时
            public double JitterMs { get; init; }        // 帧时长标准差（抖动）
            public long DroppedFrames { get; init; }     // 会话掉帧计数
            public double MemoryMb { get; init; }        // 进程工作集
            public string Resolution { get; init; }      // 渲染分辨率（像素）
            public string GpuInfo { get; init; }         // GPU 渲染模式
        }

        /// <summary>
        /// 会话重置：进入 / 离开 Win2D 界面时调用，清空统计（平均帧率、掉帧从零开始）。
        /// </summary>
        public static void ResetSampling()
        {
            lock (_sync)
            {
                _frameTimes.Clear();
                _totalFrames = 0;
                _accumFrameTimeMs = 0;
                _droppedFrames = 0;
                _lastFrameTimeMs = 0;
                _lastUpdateMs = 0;
                _lastDrawMs = 0;
            }
        }

        /// <summary>
        /// 报告画布尺寸（W2D 线程调用，每帧一次）。
        /// <paramref name="dipWidth"/> / <paramref name="dipHeight"/> 为 DIP 布局尺寸，
        /// <paramref name="dpi"/> 为 DPI 缩放系数（渲染分辨率 = DIP × DPI）。
        /// </summary>
        public static void ReportSurface(double dipWidth, double dipHeight, double dpi)
        {
            lock (_sync)
            {
                _surfaceDipWidth = dipWidth;
                _surfaceDipHeight = dipHeight;
                _surfaceDpi = dpi;
            }
        }

        /// <summary>
        /// 每帧上报性能数据（W2D 渲染线程调用，在每帧 Update 末尾）。
        /// </summary>
        /// <param name="frameTimeMs">距上一帧的时间（帧时长）。</param>
        /// <param name="updateMs">本次 Update 回调耗时。</param>
        /// <param name="drawMs">上一帧 Draw 回调耗时。</param>
        public static void ReportFrame(double frameTimeMs, double updateMs, double drawMs)
        {
            lock (_sync)
            {
                _lastFrameTimeMs = frameTimeMs;
                _lastUpdateMs = updateMs;
                _lastDrawMs = drawMs;

                // 滑动窗口：供实时帧率 / 抖动计算
                _frameTimes.Enqueue(frameTimeMs);
                while (_frameTimes.Count > WindowFrames)
                    _frameTimes.Dequeue();

                // 会话累计：供平均帧率 / 掉帧计数
                _totalFrames++;
                _accumFrameTimeMs += frameTimeMs;
                if (frameTimeMs > DroppedFrameThresholdMs)
                    _droppedFrames++;

                // 惰性读取一次 GPU 信息（首次进入渲染循环时）
                if (!_gpuInfoLoaded)
                    EnsureGpuInfoLoaded();
            }

            // 锁外触发，避免外部订阅者放大锁粒度；无订阅时为空判断开销可忽略
            FrameSample?.Invoke(frameTimeMs, updateMs, drawMs);
        }

        /// <summary>
        /// 读取性能快照（UI 线程调用，供 HUD 定时刷新）。
        /// </summary>
        public static HudSnapshot GetSnapshot()
        {
            double fps, avgFps, frameTimeMs, updateMs, drawMs, jitterMs;
            long droppedFrames;
            string resolution, gpuInfo;

            lock (_sync)
            {
                double sum = 0;
                foreach (var t in _frameTimes)
                    sum += t;
                int count = _frameTimes.Count;

                double avg = count > 0 ? sum / count : 0;
                fps = count > 0 && avg > 0 ? 1000.0 / avg : 0;

                // 帧时长标准差（抖动）
                double variance = 0;
                if (count > 0 && avg > 0)
                {
                    foreach (var t in _frameTimes)
                    {
                        double d = t - avg;
                        variance += d * d;
                    }
                    variance /= count;
                }
                jitterMs = count > 0 ? Math.Sqrt(variance) : 0;

                // 会话平均帧率
                avgFps = _totalFrames > 0 && _accumFrameTimeMs > 0
                    ? 1000.0 / (_accumFrameTimeMs / _totalFrames)
                    : 0;

                // 渲染分辨率（像素）
                resolution = _surfaceDipWidth > 0 && _surfaceDipHeight > 0
                    ? $"{(int)Math.Round(_surfaceDipWidth * _surfaceDpi)}×{(int)Math.Round(_surfaceDipHeight * _surfaceDpi)}"
                    : "--";

                frameTimeMs = _lastFrameTimeMs;
                updateMs = _lastUpdateMs;
                drawMs = _lastDrawMs;
                droppedFrames = _droppedFrames;
                gpuInfo = string.IsNullOrEmpty(_gpuInfo) ? "--" : _gpuInfo;
            }

            // ★ 修复：进程内存读取移出锁外——Process.Refresh() 含 P/Invoke 系统调用，
            //   若在 _sync 锁内执行会拉长持锁时间，与 W2D 渲染线程每帧的 ReportFrame
            //   抢锁，间接拖慢渲染循环。复用缓存的 Process 实例避免创建开销。
            double memoryMb = 0;
            try
            {
                _cachedProcess.Refresh();
                memoryMb = _cachedProcess.WorkingSet64 / 1024.0 / 1024.0;
            }
            catch { /* 忽略读取失败 */ }

            return new HudSnapshot
            {
                Fps = fps,
                AvgFps = avgFps,
                FrameTimeMs = frameTimeMs,
                UpdateMs = updateMs,
                DrawMs = drawMs,
                JitterMs = jitterMs,
                DroppedFrames = droppedFrames,
                MemoryMb = memoryMb,
                Resolution = resolution,
                GpuInfo = gpuInfo
            };
        }

        /// <summary>通知 MainWindow 刷新悬浮窗</summary>
        public static void NotifyChanged() => Changed?.Invoke();

        /// <summary>
        /// 惰性获取 GPU 渲染信息（需在 W2D 渲染循环中调用，保证 Win2D 已初始化）。
        /// 渲染模式取自 Win2D 设备（手动指定时用 <see cref="Win2DDeviceManager.CustomDevice"/>，
        /// 否则用共享设备）的 ForceSoftwareRenderer（软件/WARP 或硬件加速）；
        /// GPU 名称在手动指定时取自定义设备绑定的 GPU，否则经 D3D11 默认硬件设备
        /// （ID3D11Device→IDXGIDevice→IDXGIAdapter）查询实际渲染设备。
        /// 必须在 <see cref="_sync"/> 锁内调用。
        /// </summary>
        private static void EnsureGpuInfoLoaded()
        {
            try
            {
                string mode = "未知";
                try
                {
                    // 手动指定时优先取自定义设备，避免误用共享设备的状态
                    var device = Win2DDeviceManager.CustomDevice ?? CanvasDevice.GetSharedDevice();
                    mode = device.ForceSoftwareRenderer ? "软件渲染" : "硬件加速";
                }
                catch { /* 设备不可用时保持未知 */ }

                // GPU 名称：手动指定时显示自定义设备绑定的 GPU（否则查询默认设备会误导为核显）
                string name = Win2DDeviceManager.CustomDeviceName;
                if (string.IsNullOrEmpty(name))
                    name = QueryDefaultD3D11AdapterName();

                _gpuInfo = string.IsNullOrEmpty(name) ? mode : $"{name} · {mode}";
            }
            catch
            {
                _gpuInfo = "未知 GPU";
            }
            _gpuInfoLoaded = true;
        }

        // ═════════════════════════════ D3D11/DXGI：渲染设备实际绑定的 GPU ═════════════════════════════

        /// <summary>
        /// 查询当前进程 D3D11 默认硬件设备实际绑定的 GPU 名称。
        /// 用 D3D11CreateDevice(NULL, HARDWARE) 创建临时设备——与 Win2D 共享设备的创建行为一致，
        /// 且受 Windows「高性能 GPU」设置 / NVIDIA Optimus 等驱动层重定向影响——
        /// 再经 ID3D11Device → IDXGIDevice → IDXGIAdapter → GetDesc 取适配器描述。
        /// 失败返回空字符串（调用方降级为仅显示渲染模式）。
        /// </summary>
        private static string QueryDefaultD3D11AdapterName()
        {
            IntPtr device;
            int featureLevel;
            IntPtr context;
            int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out featureLevel, out context);
            if (hr != 0) return "";

            try
            {
                Guid iid = IDXGIDeviceGuid;
                var qi = GetVtableFunc<QueryInterfaceDelegate>(device, 0);
                IntPtr dxgiDevice;
                if (qi(device, ref iid, out dxgiDevice) != 0) return "";
                try
                {
                    var getAdapter = GetVtableFunc<GetAdapterDelegate>(dxgiDevice, 7);
                    IntPtr adapter;
                    if (getAdapter(dxgiDevice, out adapter) != 0) return "";
                    try
                    {
                        var getDesc = GetVtableFunc<GetAdapterDescDelegate>(adapter, 8);
                        DXGI_ADAPTER_DESC desc;
                        if (getDesc(adapter, out desc) != 0) return "";
                        return desc.Description?.Trim() ?? "";
                    }
                    finally { Marshal.Release(adapter); }
                }
                finally { Marshal.Release(dxgiDevice); }
            }
            finally
            {
                Marshal.Release(context);
                Marshal.Release(device);
            }
        }

        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const uint D3D11_SDK_VERSION = 7;
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20; // Win2D/Direct2D 必需

        private static readonly Guid IDXGIDeviceGuid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public ulong AdapterLuid;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceDelegate(IntPtr p, ref Guid riid, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetAdapterDelegate(IntPtr dxgiDevice, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetAdapterDescDelegate(IntPtr adapter, out DXGI_ADAPTER_DESC desc);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
            uint flags, IntPtr featureLevels, uint numLevels, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr context);

        /// <summary>从 COM 对象 vtable 指定槽位取函数指针并转为委托。</summary>
        private static T GetVtableFunc<T>(IntPtr comPtr, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(comPtr);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }
    }
}
