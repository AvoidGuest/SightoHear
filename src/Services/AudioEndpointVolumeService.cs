using SightoHear.Helpers;
using System;
using System.Runtime.InteropServices;

namespace SightoHear.Services
{
    /// <summary>
    /// 音频端点（输出设备）音量控制服务。
    /// 遵循 Windows API 规范：通过 Core Audio API（MMDeviceAPI + EndpointVolumeAPI）的
    /// IMMDeviceEnumerator → IMMDeviceCollection → IMMDevice → IAudioEndpointVolume 链路，
    /// 按设备 ID 直接读写指定音频输出设备的"设备音量"（与任务栏系统音量一致），
    /// 而非应用内软件音量（MediaPlayer.Volume）。
    ///
    /// ★ Native AOT 兼容性（本文件的关键约束）：
    ///   Native AOT 不内置 COM 支持，`[ComImport]` 类/接口在 AOT 下会生成无效 IL——
    ///   构造 CLSID 对象时直接抛 InvalidProgramException（ILC 构建期警告
    ///   "will always throw because: Invalid IL or CLR metadata"），且 Marshal.GetTypedObjectForIUnknown
    ///   等 RCW 路径同样不可用。
    ///   因此这里不使用任何 [ComImport] / RCW，而是：
    ///     · 用 CoCreateInstance 直接拿到裸 IUnknown*；
    ///     · 用「虚表槽位 + 非托管函数指针（delegate* unmanaged[Stdcall]）」手工调用接口方法；
    ///     · 用 Marshal.Release 手工管理引用计数。
    ///   该方式在 AOT 与 JIT 下行为完全一致（已在 AOT 构建中实测通过）。
    ///
    /// 关键点（依据微软文档与社区最佳实践）：
    /// 1. 使用前必须初始化 COM（CoInitializeEx），且在同一 STA 线程内完成全部 COM 操作；
    /// 2. 设备 ID 必须以 IMMDevice::GetId 返回的官方 ID 为准——不直接信任外部传入的 ID，
    ///    而是通过 EnumAudioEndpoints 枚举后按 ID（忽略大小写）匹配目标设备；
    /// 3. 所有接口指针必须显式 Release，避免 COM 引用计数泄漏。
    /// </summary>
    public static unsafe class AudioEndpointVolumeService
    {
        // ---- CLSID / IID ----

        /// <summary>CLSID_MMDeviceEnumerator</summary>
        private static readonly Guid ClsidMMDeviceEnumerator =
            new("BCDE0395-E52F-467C-8E3D-C4579291692E");

        /// <summary>IID_IMMDeviceEnumerator</summary>
        private static readonly Guid IidMMDeviceEnumerator =
            new("A95664D2-9614-4F35-A746-DE8DB63617E6");

        /// <summary>IID_IAudioEndpointVolume</summary>
        private static readonly Guid IidAudioEndpointVolume =
            new("5CDF2C82-841E-4546-9722-0CF74078229A");

        // ---- 虚表槽位（前 3 个槽位固定为 IUnknown: QueryInterface/AddRef/Release）----

        // IMMDeviceEnumerator
        private const int EnumeratorEnumAudioEndpoints = 3;

        // IMMDeviceCollection
        private const int CollectionGetCount = 3;
        private const int CollectionItem = 4;

        // IMMDevice
        private const int DeviceActivate = 3;
        private const int DeviceGetId = 5;

        // IAudioEndpointVolume
        private const int VolumeSetMasterVolumeLevelScalar = 7;
        private const int VolumeGetMasterVolumeLevelScalar = 9;
        private const int VolumeSetMute = 14;
        private const int VolumeGetMute = 15;

        // ---- 常量 ----

        /// <summary>EDataFlow.eRender：渲染（输出）端点</summary>
        private const int EDataFlowRender = 0;

        /// <summary>DEVICE_STATE_ACTIVE：仅活动设备</summary>
        private const int DeviceStateActive = 0x1;

        /// <summary>CLSCTX_INPROC_SERVER</summary>
        private const int ClsCtxInprocServer = 1;

        /// <summary>COINIT_APARTMENTTHREADED</summary>
        private const uint CoinitApartmentThreaded = 0x0;

        /// <summary>RPC_E_CHANGED_MODE：线程已用其它模式初始化过 COM</summary>
        private const int RpcEChangedMode = unchecked((int)0x80010106);

        // ---- P/Invoke（均为 blittable 签名，Native AOT 完全支持）----

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext, ref Guid riid, out IntPtr ppv);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        // ---- 虚表调用辅助 ----

        /// <summary>读取实例对象首字段（虚表指针）中指定槽位的函数地址。</summary>
        private static IntPtr VtableEntry(IntPtr instance, int slot)
            => ((IntPtr*)Marshal.ReadIntPtr(instance))[slot];

        /// <summary>
        /// 确保当前线程的 COM 环境已初始化（幂等）。
        /// S_OK(0)/S_FALSE(1) 均视为成功；RPC_E_CHANGED_MODE 表示线程模式不同，忽略即可。
        /// </summary>
        private static void EnsureComInitialized()
        {
            int hr = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
            if (hr != 0 && hr != 1 && hr != RpcEChangedMode)
                AppLogger.Warning($"CoInitializeEx 返回异常 HRESULT=0x{hr:X8}（继续尝试调用）");
        }

        /// <summary>
        /// 获取指定输出设备的设备音量（0~1 标量）。失败或设备不可用时返回 null。
        /// </summary>
        public static float? GetVolume(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return null;

            IntPtr volume = IntPtr.Zero;
            try
            {
                volume = GetEndpointVolume(deviceId);
                if (volume == IntPtr.Zero)
                    return null;

                var getScalar =
                    (delegate* unmanaged[Stdcall]<IntPtr, float*, int>)VtableEntry(volume, VolumeGetMasterVolumeLevelScalar);
                float level;
                int hr = getScalar(volume, &level);
                if (hr < 0)
                {
                    AppLogger.Warning($"GetMasterVolumeLevelScalar 失败，HRESULT=0x{hr:X8}: {deviceId}");
                    return null;
                }

                return Math.Clamp(level, 0f, 1f);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"读取输出设备音量失败: {deviceId}");
                return null;
            }
            finally
            {
                if (volume != IntPtr.Zero)
                    Marshal.Release(volume);
            }
        }

        /// <summary>
        /// 设置指定输出设备的设备音量（0~1 标量，与任务栏系统音量一致）。
        /// 返回是否设置成功。
        /// </summary>
        public static bool SetVolume(string? deviceId, float scalar)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return false;

            IntPtr volume = IntPtr.Zero;
            try
            {
                volume = GetEndpointVolume(deviceId);
                if (volume == IntPtr.Zero)
                    return false;

                var setScalar =
                    (delegate* unmanaged[Stdcall]<IntPtr, float, IntPtr, int>)VtableEntry(volume, VolumeSetMasterVolumeLevelScalar);
                int hr = setScalar(volume, Math.Clamp(scalar, 0f, 1f), IntPtr.Zero);
                if (hr < 0)
                    AppLogger.Warning($"SetMasterVolumeLevelScalar 失败，HRESULT=0x{hr:X8}: {deviceId}");
                return hr >= 0;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"设置输出设备音量失败: {deviceId}");
                return false;
            }
            finally
            {
                if (volume != IntPtr.Zero)
                    Marshal.Release(volume);
            }
        }

        /// <summary>
        /// 获取指定输出设备的静音状态。失败或设备不可用时返回 null。
        /// </summary>
        public static bool? GetMute(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return null;

            IntPtr volume = IntPtr.Zero;
            try
            {
                volume = GetEndpointVolume(deviceId);
                if (volume == IntPtr.Zero)
                    return null;

                var getMute =
                    (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)VtableEntry(volume, VolumeGetMute);
                int mute;
                int hr = getMute(volume, &mute);
                if (hr < 0)
                    return null;

                return mute != 0;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"读取输出设备静音状态失败: {deviceId}");
                return null;
            }
            finally
            {
                if (volume != IntPtr.Zero)
                    Marshal.Release(volume);
            }
        }

        /// <summary>
        /// 设置指定输出设备的静音状态。返回是否设置成功。
        /// </summary>
        public static bool SetMute(string? deviceId, bool mute)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return false;

            IntPtr volume = IntPtr.Zero;
            try
            {
                volume = GetEndpointVolume(deviceId);
                if (volume == IntPtr.Zero)
                    return false;

                var setMute =
                    (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)VtableEntry(volume, VolumeSetMute);
                int hr = setMute(volume, mute ? 1 : 0, IntPtr.Zero);
                return hr >= 0;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"设置输出设备静音状态失败: {deviceId}");
                return false;
            }
            finally
            {
                if (volume != IntPtr.Zero)
                    Marshal.Release(volume);
            }
        }

        /// <summary>
        /// 按设备 ID 查找并激活音频端点的 IAudioEndpointVolume 接口指针（调用方负责 Release）。
        /// 设备匹配以 IMMDevice::GetId 返回的官方 ID 为准（忽略大小写），
        /// 避免外部传入的 UWP 格式 ID 与 WASAPI 期望格式不一致导致 GetDevice 失败。
        /// </summary>
        private static IntPtr GetEndpointVolume(string deviceId)
        {
            EnsureComInitialized();

            // 1. 创建 IMMDeviceEnumerator
            Guid clsid = ClsidMMDeviceEnumerator;
            Guid iidEnumerator = IidMMDeviceEnumerator;
            int hr = CoCreateInstance(
                ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iidEnumerator, out IntPtr enumerator);
            if (hr < 0 || enumerator == IntPtr.Zero)
            {
                AppLogger.Warning($"CoCreateInstance(MMDeviceEnumerator) 失败，HRESULT=0x{hr:X8}");
                return IntPtr.Zero;
            }

            try
            {
                // 2. 枚举活动输出设备
                var enumAudioEndpoints =
                    (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)VtableEntry(enumerator, EnumeratorEnumAudioEndpoints);
                IntPtr collection;
                hr = enumAudioEndpoints(enumerator, EDataFlowRender, DeviceStateActive, &collection);
                if (hr < 0 || collection == IntPtr.Zero)
                {
                    AppLogger.Warning($"EnumAudioEndpoints 失败，HRESULT=0x{hr:X8}");
                    return IntPtr.Zero;
                }

                IntPtr targetDevice = IntPtr.Zero;
                try
                {
                    var getCount =
                        (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)VtableEntry(collection, CollectionGetCount);
                    int count;
                    hr = getCount(collection, &count);
                    if (hr < 0 || count <= 0)
                    {
                        AppLogger.Warning($"设备集合为空，GetCount HRESULT=0x{hr:X8}");
                        return IntPtr.Zero;
                    }

                    var item =
                        (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)VtableEntry(collection, CollectionItem);

                    // 3. 遍历设备，用 GetId 返回的官方 ID 匹配目标设备
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr device;
                        hr = item(collection, i, &device);
                        if (hr < 0 || device == IntPtr.Zero)
                            continue;

                        try
                        {
                            var getId =
                                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)VtableEntry(device, DeviceGetId);
                            IntPtr pwszId;
                            hr = getId(device, &pwszId);
                            if (hr < 0 || pwszId == IntPtr.Zero)
                                continue;

                            string officialId;
                            try
                            {
                                officialId = Marshal.PtrToStringUni(pwszId) ?? string.Empty;
                            }
                            finally
                            {
                                // GetId 返回的字符串由 COM 任务分配器分配，必须 CoTaskMemFree
                                CoTaskMemFree(pwszId);
                            }

                            if (string.IsNullOrEmpty(officialId))
                                continue;

                            // ID 匹配（忽略大小写）：
                            // WASAPI 官方 ID 形如 {0.0.0.00000000}.{GUID}，
                            // 而 UWP 枚举得到的 DeviceInformation.Id 是完整路径
                            // \\?\SWD#MMDEVAPI#{0.0.0.00000000}.{GUID}#{类GUID}。
                            // 官方 ID 是 UWP ID 的子串，故同时支持两种格式匹配。
                            if (string.Equals(officialId, deviceId, StringComparison.OrdinalIgnoreCase) ||
                                deviceId.Contains(officialId, StringComparison.OrdinalIgnoreCase))
                            {
                                // 转移所有权：targetDevice 接管 device，不再由本循环释放
                                targetDevice = device;
                                device = IntPtr.Zero;
                                break;
                            }
                        }
                        finally
                        {
                            if (device != IntPtr.Zero)
                                Marshal.Release(device);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(collection);
                }

                if (targetDevice == IntPtr.Zero)
                {
                    AppLogger.Warning($"在活动输出设备中未找到匹配设备: {deviceId}");
                    return IntPtr.Zero;
                }

                // 4. 激活 IAudioEndpointVolume 接口
                try
                {
                    var activate =
                        (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, IntPtr, IntPtr*, int>)VtableEntry(targetDevice, DeviceActivate);
                    Guid iidVolume = IidAudioEndpointVolume;
                    IntPtr volume;
                    hr = activate(targetDevice, &iidVolume, ClsCtxInprocServer, IntPtr.Zero, &volume);
                    if (hr < 0 || volume == IntPtr.Zero)
                    {
                        AppLogger.Warning($"Activate IAudioEndpointVolume 失败，HRESULT=0x{hr:X8}: {deviceId}");
                        return IntPtr.Zero;
                    }

                    return volume;
                }
                finally
                {
                    Marshal.Release(targetDevice);
                }
            }
            finally
            {
                Marshal.Release(enumerator);
            }
        }
    }
}
