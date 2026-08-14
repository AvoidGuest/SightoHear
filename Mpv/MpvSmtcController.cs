using Microsoft.UI.Dispatching;
using SightoHear.Helpers;
using SightoHear.Models;
using SightoHear.Mpv.Enums.Player;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SightoHear.Mpv
{
    /// <summary>
    /// 超分模式（libmpv）的系统媒体传输控件（SMTC）手动集成。
    /// mpv 不是 MediaPlayer，播放状态/时间线/媒体元数据不会自动上报给系统。
    /// WinUI 3 桌面应用没有 CoreWindow，SystemMediaTransportControls.GetForCurrentView()
    /// 会抛 Invalid window handle（0x80070578），因此这里通过 ISystemMediaTransportControlsInterop
    /// COM 互操作（GetForWindow）绑定主窗口句柄获取视图级 SMTC，手动维护媒体会话，
    /// 使任务栏媒体预览、系统媒体键与外部软件（BetterLyrics 等）可读取播放信息。
    /// 仅应在超分模式（VideoPlayerPage._isMpvMode）下创建，且必须在 UI 线程构造。
    /// </summary>
    public sealed class MpvSmtcController : IDisposable
    {
        // ISystemMediaTransportControlsInterop（Windows SDK systemmediatransportcontrolsinterop.h）：
        // MIDL_INTERFACE("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a")
        // virtual HRESULT GetForWindow(HWND appWindow, REFIID riid, void** mediaTransportControl) = 0;
        // 注意：.NET 8 的 Marshal.GetTypedObjectForIUnknown 不支持 IInspectable 接口封送
        //（PlatformNotSupportedException: Marshalling as IInspectable is not supported），
        // 因此这里通过 COM vtable 槽位手动调用（IUnknown 0-2 / IInspectable 3-5 / GetForWindow = 6）。
        private static readonly Guid InteropIid = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");

        // GetForWindow 的 riid 应为 ISystemMediaTransportControls 接口 IID
        //（从 Windows.Foundation.UniversalApiContract.winmd 的 GuidAttribute 提取，
        //  Firefox/Chromium 均使用该接口；99fa3ff4-1742-42a6-902e-087d41f965ec）
        private static readonly Guid SystemMediaTransportControlsIid =
            new("99fa3ff4-1742-42a6-902e-087d41f965ec");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetForWindowDelegate(
            IntPtr thisPtr, IntPtr appWindow, [In] ref Guid riid, out IntPtr mediaTransportControl);

        // RoGetActivationFactory（combase.dll）：按类名激活 WinRT 类型并返回 IActivationFactory。
        // 注意：activatableClassId 参数是 HSTRING（Windows 运行时字符串），不能用 LPWSTR 替代，
        // 必须通过 WindowsCreateString 创建，否则原生代码按 HSTRING 头解析 LPWSTR 会 AccessViolation。
        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId, [In] ref Guid iid, out IntPtr factory);

        // WindowsCreateString / WindowsDeleteString（combase.dll）：HSTRING 生命周期管理
        [DllImport("combase.dll")]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        // IID_IActivationFactory
        private static readonly Guid IActivationFactoryIid = new("00000035-0000-0000-C000-000000000046");
        // Windows.Media.SystemMediaTransportControls 的激活类名
        private const string SmtcClassName = "Windows.Media.SystemMediaTransportControls";

        private readonly MpvVideoPlayer _mpv;
        private readonly SystemMediaTransportControls? _smtc;
        private readonly DispatcherQueue _dispatcher;
        private bool _disposed;

        // 进程级标记：SMTC 获取失败后永久降级，避免反复执行高风险 COM 互操作链
        //（RoGetActivationFactory / 手动 vtable 调用偶发 AccessViolation，失败即不再重试）
        private static volatile bool _smTcUnavailable;

        /// <summary>
        /// 创建并激活视图级 SMTC 会话（必须在 UI 线程调用，windowHandle 为应用主窗口 HWND）。
        /// 获取失败时仅记录日志并降级为"无 SMTC"（不中断视频播放）。
        /// 若本进程内此前已失败过，则直接降级不再重试（防崩溃复发）。
        /// </summary>
        public MpvSmtcController(MpvVideoPlayer mpv, DispatcherQueue dispatcher, IntPtr windowHandle)
        {
            _mpv = mpv;
            _dispatcher = dispatcher;

            if (_smTcUnavailable)
            {
                AppLogger.Warning("libmpv：SMTC 此前获取失败，本次进程内已永久降级为无系统媒体控件集成");
                return;
            }

            _smtc = TryGetForWindow(windowHandle);
            if (_smtc == null)
            {
                _smTcUnavailable = true;
                AppLogger.Warning("libmpv：SMTC 初始化失败，本次进程内不再重试（降级为无系统媒体控件集成）");
                AppLogger.Flush();
                return;
            }

            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;

            // SMTC 按钮事件在后台线程触发，需回 UI 线程操作 mpv
            _smtc.ButtonPressed += Smtc_ButtonPressed;

            // mpv 事件在 mpv 线程触发，需回 UI 线程更新 SMTC
            _mpv.PlaybackStateChanged += OnPlaybackStateChanged;
            _mpv.PositionChanged += OnPositionChanged;
            _mpv.Ended += OnEnded;
        }

        /// <summary>
        /// 通过 ISystemMediaTransportControlsInterop::GetForWindow 获取绑定指定窗口的 SMTC。
        /// </summary>
        private static SystemMediaTransportControls? TryGetForWindow(IntPtr hwnd)
        {
            try
            {
                // 创建 HSTRING 类名（不能用 StringToHGlobalUni 的 LPWSTR 代替，
                // 否则 RoGetActivationFactory 按 HSTRING 头解析 LPWSTR 会 AccessViolation）
                Marshal.ThrowExceptionForHR(
                    WindowsCreateString(SmtcClassName, SmtcClassName.Length, out IntPtr classNameHString));
                try
                {
                    // 静态只读字段不能直接作为 ref 参数，先复制到局部变量
                    var iidFactory = IActivationFactoryIid;
                    Marshal.ThrowExceptionForHR(
                        RoGetActivationFactory(classNameHString, ref iidFactory, out IntPtr factoryPtr));
                    try
                    {
                        // 手动 QueryInterface：IActivationFactory → ISystemMediaTransportControlsInterop
                        var interopIid = InteropIid;
                        Marshal.ThrowExceptionForHR(
                            Marshal.QueryInterface(factoryPtr, ref interopIid, out IntPtr interopPtr));
                        try
                        {
                            // 手动调用 vtable 槽位 6（IUnknown 0-2 / IInspectable 3-5 / GetForWindow = 6）
                            var getForWindow = GetVtableDelegate<GetForWindowDelegate>(interopPtr, 6);
                            var smtcIid = SystemMediaTransportControlsIid;
                            Marshal.ThrowExceptionForHR(
                                getForWindow(interopPtr, hwnd, ref smtcIid, out IntPtr smtcPtr));
                            try
                            {
                                return SystemMediaTransportControls.FromAbi(smtcPtr);
                            }
                            finally
                            {
                                Marshal.Release(smtcPtr);
                            }
                        }
                        finally
                        {
                            Marshal.Release(interopPtr);
                        }
                    }
                    finally
                    {
                        Marshal.Release(factoryPtr);
                    }
                }
                finally
                {
                    WindowsDeleteString(classNameHString);
                }
            }
            catch (AccessViolationException)
            {
                // 手动 COM 互操作偶发内存访问违规：非托管侧崩溃托管 catch 无法拦截，
                // 但显式捕获可拦截托管边界处的 AV，并让调用方永久降级不再重试
                AppLogger.Error(new AccessViolationException(),
                    "SMTC：获取窗口 SMTC 时发生内存访问违规（COM 互操作），libmpv SMTC 降级");
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "SMTC：获取窗口 SMTC 失败（libmpv将降级为无系统媒体控件集成）");
                return null;
            }
        }

        /// <summary>
        /// 从 COM 对象 vtable 的指定槽位取出函数指针并封装为委托。
        /// </summary>
        private static T GetVtableDelegate<T>(IntPtr comPtr, int slot) where T : Delegate
        {
            IntPtr vtablePtr = Marshal.ReadIntPtr(comPtr);
            IntPtr methodPtr = Marshal.ReadIntPtr(vtablePtr, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
        }

        /// <summary>
        /// 设置当前媒体元数据（标题/封面）并标记为播放中。每次加载新视频时调用（UI 线程）。
        /// </summary>
        public async Task SetMediaItemAsync(MediaItem item)
        {
            if (_disposed || _smtc == null)
            {
                return;
            }

            try
            {
                var smtc = _smtc;
                var updater = smtc.DisplayUpdater;
                updater.Type = MediaPlaybackType.Video;
                updater.VideoProperties.Title = string.IsNullOrEmpty(item.Title) ? item.FileName : item.Title;
                updater.VideoProperties.Subtitle = item.FileName;

                // 封面缩略图（与普通模式一致，使用 MediaScanner 提取的视频缩略图）
                if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                {
                    try
                    {
                        var file = await StorageFile.GetFileFromPathAsync(item.ThumbnailPath);
                        var stream = await file.OpenReadAsync();
                        updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning($"libmpv SMTC 设置封面失败: {ex.Message}");
                    }
                }

                updater.Update();
                smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"libmpv SMTC 设置媒体元数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 立即同步时间线（一般无需手动调用，PositionChanged 事件会自动更新）。
        /// </summary>
        public void UpdateTimeline(double position, double duration)
        {
            if (_disposed || _smtc == null || duration <= 0)
            {
                return;
            }

            try
            {
                // 注意：CsWinRT 投影中方法名为 UpdateTimelineProperties（WinRT 原始名为 UpdateTimeline）
                _smtc.UpdateTimelineProperties(new Windows.Media.SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    EndTime = TimeSpan.FromSeconds(duration),
                    Position = TimeSpan.FromSeconds(Math.Max(0, position)),
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"libmpv SMTC 更新时间线失败: {ex.Message}");
            }
        }

        private void OnPlaybackStateChanged(object? sender, PlaybackState state)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || _smtc == null)
                {
                    return;
                }

                try
                {
                    // mpv 缓冲/解码等中间状态统一映射为 Paused，仅 Playing 映射为 Playing
                    _smtc.PlaybackStatus = state == PlaybackState.Playing
                        ? MediaPlaybackStatus.Playing
                        : MediaPlaybackStatus.Paused;
                }
                catch (Exception ex)
                {
                    AppLogger.Warning($"libmpv SMTC 同步播放状态失败: {ex.Message}");
                }
            });
        }

        private void OnPositionChanged(double position, double duration)
        {
            _dispatcher.TryEnqueue(() => UpdateTimeline(position, duration));
        }

        private void OnEnded()
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || _smtc == null)
                {
                    return;
                }

                try
                {
                    _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                }
                catch (Exception ex)
                {
                    AppLogger.Warning($"libmpv SMTC 同步停止状态失败: {ex.Message}");
                }
            });
        }

        private void Smtc_ButtonPressed(object? sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            // SMTC 按钮事件在后台线程触发，需回 UI 线程执行 mpv 操作
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    _dispatcher.TryEnqueue(() => _mpv.Play());
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    _dispatcher.TryEnqueue(() => _mpv.Pause());
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    // 与普通模式行为一致：停止等价于暂停
                    _dispatcher.TryEnqueue(() => _mpv.Pause());
                    break;
            }
        }

        /// <summary>
        /// 释放 SMTC 会话：退订事件、关闭会话（页面卸载时调用，避免残留媒体会话）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _mpv.PlaybackStateChanged -= OnPlaybackStateChanged;
                _mpv.PositionChanged -= OnPositionChanged;
                _mpv.Ended -= OnEnded;
                if (_smtc != null)
                {
                    _smtc.ButtonPressed -= Smtc_ButtonPressed;
                    _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                    _smtc.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"libmpv SMTC 释放失败: {ex.Message}");
            }
        }
    }
}
