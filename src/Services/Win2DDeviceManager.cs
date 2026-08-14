using Microsoft.Graphics.Canvas;
using SightoHear.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace SightoHear.Services
{
    /// <summary>
    /// Win2D 硬件加速 GPU 选择管理。
    /// 负责：
    ///   1. 枚举电脑可用 GPU（DXGI 硬件适配器，排除软件渲染兜底适配器），供设置页列表展示；
    ///   2. 按用户设置（跟随系统 / 手动指定 LUID）创建自定义渲染设备（CanvasDevice）；
    ///   3. 向各 Win2D 渲染页面（CanvasAnimatedControl）提供 <see cref="CustomDevice"/>。
    ///
    /// 设备创建链路：
    ///   D3D11CreateDevice(指定适配器) → ID3D11Device → IDXGIDevice →
    ///   CreateDirect3D11DeviceFromDXGIDevice → IDirect3DDevice →
    ///   CanvasDevice.CreateFromDirect3D11Device。
    /// 跟随系统时默认不设 CustomDevice，页面走 Win2D 默认共享设备
    /// （受 Windows「高性能 GPU」设置 / 驱动层重定向影响）；
    /// 但若同一张物理卡被枚举为多个同名实例（驱动残留/虚拟化），会自动改用
    /// 「无显示器输出」的离屏实例创建设备，规避默认实例受显示同步拖累导致的掉帧。
    /// </summary>
    public static class Win2DDeviceManager
    {
        /// <summary>GPU 选择：跟随系统（Windows 决定）</summary>
        public const string PreferenceAuto = "Auto";

        /// <summary>GPU 选择：手动指定</summary>
        public const string PreferenceManual = "Manual";

        // ── D3D11 / DXGI 常量与 GUID ──

        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const int D3D_DRIVER_TYPE_UNKNOWN = 0; // 指定适配器时 D3D11CreateDevice 必须用 UNKNOWN
        private const uint D3D11_SDK_VERSION = 7;
        private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2; // 软件适配器（Microsoft Basic Render Driver 等）
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20; // Win2D/Direct2D 必需

        private static readonly Guid IDXGIFactory1Guid = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IDXGIDeviceGuid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

        /// <summary>手动指定模式下创建的渲染设备；跟随系统时为 null（页面用共享设备）。</summary>
        private static CanvasDevice? _customDevice;

        /// <summary>自定义设备绑定的 GPU 名称（手动指定创建成功时记录，供悬浮窗显示）。</summary>
        private static string _customDeviceName = "";

        /// <summary>自定义设备绑定的 GPU LUID（手动指定创建成功时记录，供显存监控匹配）。</summary>
        private static ulong _customDeviceLuid;

        /// <summary>当前 D3D11 默认硬件设备 LUID（跟随系统时实际使用的 GPU）。</summary>
        private static ulong _defaultLuid;

        /// <summary>GPU 适配器信息（供设置页列表展示）。</summary>
        public sealed class GpuAdapterInfo
        {
            public string Description { get; init; } = "";
            public ulong AdapterLuid { get; init; }
            public bool IsDefault { get; init; }

            /// <summary>是否连接显示器输出（DXGI Output）。连接输出的实例在渲染/呈现时
            /// 受显示同步（VSync / 合成器）影响，帧率可能被压低；无输出实例为离屏渲染，帧率通常更高。</summary>
            public bool HasOutput { get; init; }

            // ── 硬件标识（用于识别"同一张物理显卡被枚举为多个实例"）──
            public uint VendorId { get; init; }
            public uint DeviceId { get; init; }
            public uint SubSysId { get; init; }
            public uint Revision { get; init; }

            /// <summary>同一张物理显卡的实例分组键（VendorId+DeviceId+SubSysId+Revision 相同视为同一张卡）。</summary>
            public string HardwareKey => $"{VendorId:X4}:{DeviceId:X4}:{SubSysId:X8}:{Revision:X2}";

            /// <summary>带 0x 前缀的十六进制 LUID（显示用）。</summary>
            public string LuidHex => "0x" + AdapterLuid.ToString("X16");

            /// <summary>列表展示名称：默认 GPU 附加「当前默认」标记，无显示器输出的离屏实例附加「离屏·满帧」标记。</summary>
            public string DisplayName
            {
                get
                {
                    if (IsDefault) return $"{Description}（当前默认）";
                    if (!HasOutput) return $"{Description}（离屏·满帧）";
                    return Description;
                }
            }
        }

        /// <summary>当前生效的自定义设备（手动指定时非空）。</summary>
        public static CanvasDevice? CustomDevice => _customDevice;

        /// <summary>自定义设备绑定的 GPU 名称（跟随系统时为空，由悬浮窗显示用）。</summary>
        public static string CustomDeviceName => _customDeviceName;

        /// <summary>自定义设备绑定的 GPU LUID（跟随系统时为 0，由显存监控按此匹配适配器）。</summary>
        public static ulong CustomDeviceLuid => _customDeviceLuid;

        /// <summary>当前 D3D11 默认硬件设备 LUID（用于标记「当前默认」）。</summary>
        public static ulong DefaultAdapterLuid => _defaultLuid;

        /// <summary>
        /// 应用启动时调用（SettingsHelper.Load 之后）：读取设置并按需创建自定义渲染设备。
        /// 手动指定但创建设备失败 / LUID 无效时自动回退为跟随系统，不影响应用运行。
        /// </summary>
        public static void Initialize()
        {
            // 记录跟随系统时 D3D11 默认设备（供列表标记「当前默认」）
            _defaultLuid = QueryDefaultAdapterLuid();

            if (App.SettingsHelper.Win2DGpuPreference != PreferenceManual)
            {
                // 跟随系统：自动优化——当驱动残留/虚拟化导致同一张显卡被枚举为多个实例时，
                // 优先选用「无显示器输出」的离屏实例创建设备，规避默认实例受显示同步（VSync）
                // 限制导致帧率偏低的问题；仅一个实例或全都有输出时保持原跟随系统行为。
                _customDevice = TryCreateBestAutoDevice(out string autoName, out ulong autoLuid);
                if (_customDevice != null)
                {
                    _customDeviceName = autoName;
                    _customDeviceLuid = autoLuid;
                    AppLogger.Info($"跟随系统模式自动选用离屏实例: {autoName} (LUID=0x{autoLuid:X16})");
                }
                else
                {
                    _customDeviceName = "";
                    _customDeviceLuid = 0;
                }
                return;
            }

            ulong luid = 0;
            string raw = App.SettingsHelper.Win2DGpuAdapterLuid ?? "";
            if (ulong.TryParse(raw, NumberStyles.HexNumber, null, out luid) && luid != 0)
            {
                _customDevice = CreateDeviceForLuid(luid, out string gpuName);
                if (_customDevice == null)
                {
                    _customDeviceName = "";
                    _customDeviceLuid = 0;
                    AppLogger.Warning($"手动指定 GPU 创建设备失败（LUID=0x{luid:X16}），已回退跟随系统");
                }
                else
                {
                    _customDeviceName = gpuName;
                    _customDeviceLuid = luid;
                    AppLogger.Info($"Win2D 自定义 GPU 设备已创建: {gpuName} (LUID=0x{luid:X16})");
                }
            }
            else
            {
                _customDeviceName = "";
                _customDeviceLuid = 0;
                AppLogger.Warning($"手动指定 GPU 设置中的 LUID 无效（{raw}），已回退跟随系统");
            }
        }

        /// <summary>
        /// 应用退出时释放自定义 CanvasDevice。静态设备全应用生命周期持有，
        /// 显式释放可将 GPU 资源及时归还，而非依赖进程退出时的终结器清理。
        /// </summary>
        public static void Shutdown()
        {
            if (_customDevice != null)
            {
                try { _customDevice.Dispose(); } catch { }
                _customDevice = null;
                _customDeviceName = "";
                _customDeviceLuid = 0;
            }
        }

        /// <summary>
        /// 强制清理 GPU 资源碎片。
        /// 
        /// 浏览多个 Win2D 页面后，大量 CanvasBitmap / CanvasRenderTarget 被创建和销毁，
        /// 虽然 C# 侧正确 Dispose，但 GPU 驱动通过 WDDM 延迟销毁（deferred destruction），
        /// 导致 D3D11 设备内部的分配记录碎片化。每次 Draw 提交时，驱动在碎片化的记录表
        /// 中搜索/管理资源，造成 Draw 耗时从 ~4ms 暴涨到 30-55ms。
        /// 
        /// 调用本方法会触发：
        ///   1. GC + WaitForPendingFinalizers：确保已 Dispose 的 Win2D 对象终结器完成 COM Release
        ///   2. CanvasDevice.Trim()：调用底层 IDXGIDevice3::Trim()，
        ///      迫使 GPU 驱动立即释放所有已标记删除的 D3D 资源，清除碎片化的分配记录。
        /// 
        /// 应在以下时机调用：
        ///   - 页面切换后（RunPageCleanupAsync）
        ///   - FreeRunCanvas 停止渲染循环后
        ///   - 播放器页面退出后（MusicPlayerPage / VideoPlayerPage / ImageViewerPage Unloaded）
        /// 
        /// 注意：Trim 涉及阻塞操作（WaitForPendingFinalizers + 驱动同步），
        /// 必须在后台线程执行，避免卡 UI。
        /// </summary>
        public static void TrimGpuResources()
        {
            try
            {
                // 步骤 1：回收托管内存，让 Dispose 生效
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);

                // 步骤 2：等待终结器执行（完成 Win2D 对象的 COM Release，
                //   将 GPU 资源标记为"可回收"）
                GC.WaitForPendingFinalizers();

                // 步骤 3：第二次回收（终结器释放的对象在 GC 视角需要第二次回收才最终清除）
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);

                // 步骤 4：通知 GPU 驱动立即清理已标记为删除的 D3D 资源
                //   CanvasDevice.Trim() → IDXGIDevice3::Trim() 清除驱动内部的碎片化记录
                _customDevice?.Trim();
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"TrimGpuResources 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 枚举可用硬件 GPU 适配器（排除软件适配器），按枚举顺序返回。
        /// 虚拟显示适配器（Parsec/向日葵等）也可能被枚举为硬件适配器，由用户自行甄别。
        /// </summary>
        public static List<GpuAdapterInfo> EnumerateGpus()
        {
            var result = new List<GpuAdapterInfo>();
            Guid g = IDXGIFactory1Guid;
            IntPtr factory;
            if (CreateDXGIFactory1(ref g, out factory) != 0) return result;
            try
            {
                var enumAdapters = GetVtbl<EnumAdaptersDelegate>(factory, 7);
                uint idx = 0;
                while (true)
                {
                    IntPtr adapter;
                    if (enumAdapters(factory, idx, out adapter) != 0) break;
                    idx++;
                    try
                    {
                        var getDesc1 = GetVtbl<GetAdapterDesc1Delegate>(adapter, 10);
                        DXGI_ADAPTER_DESC1 desc;
                        if (getDesc1(adapter, out desc) != 0) continue;

                        // 排除软件适配器（Microsoft Basic Render Driver / Basic Display 兜底）
                        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;

                        result.Add(new GpuAdapterInfo
                        {
                            Description = desc.Description?.Trim() ?? "未知 GPU",
                            AdapterLuid = desc.AdapterLuid,
                            IsDefault = desc.AdapterLuid == _defaultLuid,
                            HasOutput = HasAdapterOutput(adapter),
                            VendorId = desc.VendorId,
                            DeviceId = desc.DeviceId,
                            SubSysId = desc.SubSysId,
                            Revision = desc.Revision
                        });
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
            return result;
        }

        /// <summary>
        /// 跟随系统模式下的自动优化：当「同一张物理显卡」被驱动/系统枚举为多个同名实例时
        /// （AMD 驱动残留、虚拟化软件等常见现象），默认选中的实例往往连接显示器输出，
        /// Present 受 VSync/合成器同步限制导致帧率被压低（实测约 80fps）；
        /// 而其余无显示器输出的离屏实例可满帧运行（实测约 120+ fps）。
        /// 因此在此场景下自动改用「无输出离屏实例」创建渲染设备，让跟随系统也能满血。
        /// 规则：
        ///   1. 按硬件 ID（VendorId:DeviceId:SubSysId:Revision）分组，仅处理"同一张卡有多个实例"的分组；
        ///   2. 该分组内优先取无显示器输出的实例（不受 VSync 限制）创建设备；
        ///   3. 正常场景（单实例 / 独显+核显不同卡）不干预，返回 null 保持原跟随系统行为；
        ///   4. 任何异常回退跟随系统。
        /// </summary>
        private static CanvasDevice? TryCreateBestAutoDevice(out string gpuName, out ulong luid)
        {
            gpuName = "";
            luid = 0;
            try
            {
                var gpus = EnumerateGpus();

                // 按硬件 ID 分组，找出"同一张卡被枚举为多个实例"的分组
                foreach (var group in gpus.GroupBy(g => g.HardwareKey))
                {
                    var instances = group.ToList();
                    if (instances.Count <= 1) continue; // 该卡只有一个实例，无需优化

                    // 同卡多实例：优先选无显示器输出的离屏实例（不受 VSync 限制，帧率满血）
                    foreach (var gpu in instances.Where(i => !i.HasOutput))
                    {
                        var device = CreateDeviceForLuid(gpu.AdapterLuid, out string name);
                        if (device == null) continue;
                        gpuName = name;
                        luid = gpu.AdapterLuid;
                        return device;
                    }
                }
            }
            catch { /* 任何异常都回退跟随系统 */ }
            return null;
        }

        /// <summary>判断指定 DXGI 适配器实例是否连接了显示器输出（IDXGIAdapter::EnumOutputs 首个输出）。
        /// 连接输出的实例参与显示合成，Present 可能被 VSync 同步；无输出实例为纯离屏渲染设备。</summary>
        private static bool HasAdapterOutput(IntPtr adapter)
        {
            var enumOutputs = GetVtbl<EnumOutputsDelegate>(adapter, 7);
            IntPtr output;
            int hr = enumOutputs(adapter, 0, out output);
            if (hr == 0) Marshal.Release(output);
            return hr == 0;
        }

        // ═════════════════════════════ D3D11/DXGI 互操作 ═════════════════════════════

        /// <summary>查询 D3D11CreateDevice(NULL, HARDWARE) 默认设备绑定的适配器 LUID。</summary>
        private static ulong QueryDefaultAdapterLuid()
        {
            IntPtr device;
            int featureLevel;
            IntPtr context;
            int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out featureLevel, out context);
            if (hr != 0) return 0;
            try
            {
                Guid iid = IDXGIDeviceGuid;
                var qi = GetVtbl<QueryInterfaceDelegate>(device, 0);
                IntPtr dxgiDevice;
                if (qi(device, ref iid, out dxgiDevice) != 0) return 0;
                try
                {
                    var getAdapter = GetVtbl<GetAdapterDelegate>(dxgiDevice, 7);
                    IntPtr adapter;
                    if (getAdapter(dxgiDevice, out adapter) != 0) return 0;
                    try
                    {
                        var getDesc1 = GetVtbl<GetAdapterDesc1Delegate>(adapter, 10);
                        DXGI_ADAPTER_DESC1 desc;
                        return getDesc1(adapter, out desc) == 0 ? desc.AdapterLuid : 0;
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

        /// <summary>在指定 LUID 的适配器上创建 Win2D CanvasDevice。失败返回 null。
        /// <paramref name="gpuName"/> 输出适配器描述（创建成功时有效）。</summary>
        private static CanvasDevice? CreateDeviceForLuid(ulong luid, out string gpuName)
        {
            gpuName = "";
            Guid g = IDXGIFactory1Guid;
            IntPtr factory;
            if (CreateDXGIFactory1(ref g, out factory) != 0) return null;
            try
            {
                var enumAdapters = GetVtbl<EnumAdaptersDelegate>(factory, 7);
                uint idx = 0;
                while (true)
                {
                    IntPtr adapter;
                    if (enumAdapters(factory, idx, out adapter) != 0) break;
                    idx++;
                    try
                    {
                        var getDesc1 = GetVtbl<GetAdapterDesc1Delegate>(adapter, 10);
                        DXGI_ADAPTER_DESC1 desc;
                        if (getDesc1(adapter, out desc) != 0 || desc.AdapterLuid != luid) continue;

                        // 指定适配器时必须用 D3D_DRIVER_TYPE_UNKNOWN，并开启 BGRA 支持（Win2D/Direct2D 必需）
                        IntPtr device;
                        int featureLevel;
                        IntPtr context;
                        int hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                            IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out featureLevel, out context);
                        if (hr != 0) return null;
                        try
                        {
                            gpuName = desc.Description?.Trim() ?? "";
                            var canvasDevice = CreateCanvasDeviceFromD3D11(device);
                            if (canvasDevice == null)
                            {
                                Marshal.Release(device);
                                return null;
                            }
                            // 成功：CanvasDevice 内部持有 ID3D11Device，此处不释放，由 CanvasDevice 生命周期管理
                            Marshal.Release(context);
                            return canvasDevice;
                        }
                        catch
                        {
                            Marshal.Release(device);
                            throw;
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
                return null;
            }
            finally { Marshal.Release(factory); }
        }

        /// <summary>
        /// 把 ID3D11Device COM 指针包装为 IDirect3DDevice（Windows.Graphics.DirectX.Direct3D11
        /// 互操作：CreateDirect3D11DeviceFromDXGIDevice），再交给 Win2D 创建 CanvasDevice。
        /// </summary>
        private static CanvasDevice? CreateCanvasDeviceFromD3D11(IntPtr d3dDevice)
        {
            Guid iid = IDXGIDeviceGuid;
            var qi = GetVtbl<QueryInterfaceDelegate>(d3dDevice, 0);
            IntPtr dxgiDevice;
            if (qi(d3dDevice, ref iid, out dxgiDevice) != 0) return null;
            try
            {
                IntPtr inspectable;
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable);
                if (hr != 0) return null;
                try
                {
                    // CsWinRT：从 COM 指针创建 WinRT 投影对象（FromAbi 内部 AddRef，用完需 Release 原指针）
                    var direct3DDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                    return CanvasDevice.CreateFromDirect3D11Device(direct3DDevice);
                }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgiDevice); }
        }

        // ── P/Invoke 与结构 ──

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
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
            public uint Flags; // DXGI_ADAPTER_FLAG
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdaptersDelegate(IntPtr factory, uint index, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumOutputsDelegate(IntPtr adapter, uint index, out IntPtr output);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetAdapterDesc1Delegate(IntPtr adapter, out DXGI_ADAPTER_DESC1 desc);

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

        // Windows 10+：把 IDXGIDevice 包装为 WinRT IDirect3DDevice（IInspectable）
        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        /// <summary>从 COM 对象 vtable 指定槽位取函数指针并转为委托。</summary>
        private static T GetVtbl<T>(IntPtr comPtr, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(comPtr);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }
    }
}
