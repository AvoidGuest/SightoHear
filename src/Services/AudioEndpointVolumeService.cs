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
    /// 关键点（依据微软文档与社区最佳实践）：
    /// 1. 使用前必须初始化 COM（CoInitializeEx），且在同一 STA 线程内完成全部 COM 操作；
    /// 2. 设备 ID 必须以 IMMDevice::GetId 返回的官方 ID 为准——不直接信任外部传入的 ID，
    ///    而是通过 EnumAudioEndpoints 枚举后按 ID（忽略大小写）匹配目标设备；
    /// 3. Activate 返回的接口指针必须显式 Release，避免 COM 引用计数泄漏。
    /// </summary>
    public static class AudioEndpointVolumeService
    {
        // ---- WASAPI COM 接口与 CLSID ----

        // CLSID_MMDeviceEnumerator
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        // IID_IMMDeviceEnumerator
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice(
                [MarshalAs(UnmanagedType.LPWStr)] string pwszId, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        // IID_IMMDeviceCollection
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        // IID_IMMDevice
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        // IID_IAudioEndpointVolume
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out int channelCount);
            [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr eventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, IntPtr eventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
            [PreserveSig] int VolumeStepUp(IntPtr eventContext);
            [PreserveSig] int VolumeStepDown(IntPtr eventContext);
            [PreserveSig] int QueryHardwareSupport(out uint hardwareMask);
            [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
        }

        // EDataFlow.eRender：渲染（输出）端点
        private const int EDataFlowRender = 0;
        // DEVICE_STATE_ACTIVE：仅活动设备
        private const int DeviceStateActive = 0x1;
        // CLSCTX_INPROC_SERVER
        private const int ClsCtxInprocServer = 1;
        // COINIT_APARTMENTTHREADED
        private const uint CoinitApartmentThreaded = 0x0;
        // IID_IAudioEndpointVolume
        private static readonly Guid IidAudioEndpointVolume =
            new("5CDF2C82-841E-4546-9722-0CF74078229A");

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        /// <summary>
        /// 确保当前线程的 COM 环境已初始化（幂等）。
        /// S_OK(0)/S_FALSE(1) 均视为成功；RPC_E_CHANGED_MODE 表示线程模式不同，忽略即可。
        /// </summary>
        private static void EnsureComInitialized()
        {
            int hr = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
            if (hr != 0 && hr != 1 && hr != unchecked((int)0x80010106))
                AppLogger.Warning($"CoInitializeEx 返回异常 HRESULT=0x{hr:X8}（继续尝试调用）");
        }

        /// <summary>
        /// 获取指定输出设备的设备音量（0~1 标量）。失败或设备不可用时返回 null。
        /// </summary>
        public static float? GetVolume(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return null;

            IAudioEndpointVolume? volume = null;
            try
            {
                volume = GetEndpointVolume(deviceId);
                if (volume == null)
                    return null;

                int hr = volume.GetMasterVolumeLevelScalar(out float level);
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
                if (volume != null)
                    Marshal.ReleaseComObject(volume);
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

            IAudioEndpointVolume? volume = null;
            try
            {
                volume = GetEndpointVolume(deviceId);
                if (volume == null)
                    return false;

                int hr = volume.SetMasterVolumeLevelScalar(
                    Math.Clamp(scalar, 0f, 1f), IntPtr.Zero);
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
                if (volume != null)
                    Marshal.ReleaseComObject(volume);
            }
        }

        /// <summary>
        /// 获取指定输出设备的静音状态。失败或设备不可用时返回 null。
        /// </summary>
        public static bool? GetMute(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return null;

            IAudioEndpointVolume? volume = null;
            try
            {
                volume = GetEndpointVolume(deviceId);
                if (volume == null)
                    return null;

                int hr = volume.GetMute(out bool mute);
                if (hr < 0)
                    return null;

                return mute;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"读取输出设备静音状态失败: {deviceId}");
                return null;
            }
            finally
            {
                if (volume != null)
                    Marshal.ReleaseComObject(volume);
            }
        }

        /// <summary>
        /// 设置指定输出设备的静音状态。返回是否设置成功。
        /// </summary>
        public static bool SetMute(string? deviceId, bool mute)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return false;

            IAudioEndpointVolume? volume = null;
            try
            {
                volume = GetEndpointVolume(deviceId);
                if (volume == null)
                    return false;

                int hr = volume.SetMute(mute, IntPtr.Zero);
                return hr >= 0;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"设置输出设备静音状态失败: {deviceId}");
                return false;
            }
            finally
            {
                if (volume != null)
                    Marshal.ReleaseComObject(volume);
            }
        }

        /// <summary>
        /// 按设备 ID 查找并激活音频端点的 IAudioEndpointVolume 接口。
        /// 设备匹配以 IMMDevice::GetId 返回的官方 ID 为准（忽略大小写），
        /// 避免外部传入的 UWP 格式 ID 与 WASAPI 期望格式不一致导致 GetDevice 失败。
        /// </summary>
        private static IAudioEndpointVolume? GetEndpointVolume(string deviceId)
        {
            EnsureComInitialized();

            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                // 1. 枚举活动输出设备
                int hr = enumerator.EnumAudioEndpoints(
                    EDataFlowRender, DeviceStateActive, out IMMDeviceCollection? collection);
                if (hr < 0 || collection == null)
                {
                    AppLogger.Warning($"EnumAudioEndpoints 失败，HRESULT=0x{hr:X8}");
                    return null;
                }

                IMMDevice? targetDevice = null;
                try
                {
                    hr = collection.GetCount(out int count);
                    if (hr < 0 || count <= 0)
                    {
                        AppLogger.Warning($"设备集合为空，GetCount HRESULT=0x{hr:X8}");
                        return null;
                    }

                    // 2. 遍历设备，用 GetId 返回的官方 ID 匹配目标设备
                    for (int i = 0; i < count; i++)
                    {
                        IMMDevice? device = null;
                        try
                        {
                            hr = collection.Item(i, out device);
                            if (hr < 0 || device == null)
                                continue;

                            hr = device.GetId(out string officialId);
                            if (hr < 0 || string.IsNullOrEmpty(officialId))
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
                                device = null;
                                break;
                            }
                        }
                        finally
                        {
                            if (device != null)
                                Marshal.ReleaseComObject(device);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(collection);
                }

                if (targetDevice == null)
                {
                    AppLogger.Warning($"在活动输出设备中未找到匹配设备: {deviceId}");
                    return null;
                }

                // 3. 激活 IAudioEndpointVolume 接口
                try
                {
                    // 局部变量（static readonly 字段不能作为 ref 实参）
                    Guid iid = IidAudioEndpointVolume;
                    hr = targetDevice.Activate(
                        ref iid, ClsCtxInprocServer, IntPtr.Zero, out IntPtr iface);
                    if (hr < 0 || iface == IntPtr.Zero)
                    {
                        AppLogger.Warning($"Activate IAudioEndpointVolume 失败，HRESULT=0x{hr:X8}: {deviceId}");
                        return null;
                    }

                    // 创建 RCW（内部 AddRef），随后释放 Activate 返回的原始引用，引用计数保持平衡
                    var volume = (IAudioEndpointVolume)Marshal.GetTypedObjectForIUnknown(
                        iface, typeof(IAudioEndpointVolume));
                    Marshal.Release(iface);
                    return volume;
                }
                finally
                {
                    Marshal.ReleaseComObject(targetDevice);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }
}
