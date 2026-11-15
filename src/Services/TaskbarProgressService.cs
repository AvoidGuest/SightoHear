using SightoHear.Helpers;
using System;
using System.Runtime.InteropServices;

namespace SightoHear.Services
{
    /// <summary>
    /// 任务栏播放进度（Windows 任务栏图标上的绿色进度条）。
    ///
    /// <para>
    /// 实现方式：外壳接口 <c>ITaskbarList3</c>（CLSID_TaskbarList / IID_ITaskbarList3），
    /// 调用 <c>SetProgressState</c> + <c>SetProgressValue</c>。
    /// </para>
    /// <para>
    /// ★ Native AOT 兼容性：与 <see cref="AudioEndpointVolumeService"/> 同样的约束——
    /// Native AOT 不内置 COM 支持，<c>[ComImport]</c> 在 AOT 下会生成无效 IL。
    /// 因此这里不用任何 RCW，而是 CoCreateInstance 拿裸指针 + 虚表槽位 +
    /// <c>delegate* unmanaged[Stdcall]</c> 直接调用，手工 Release 管理引用计数。
    /// </para>
    /// <para>
    /// 调用时机：TaskbarList 属于 STA COM 组件，所有调用都必须在 UI 线程执行。
    /// 进度更新频率 ≤ 4Hz（由调用方的位置定时器驱动），开销可忽略。
    /// </para>
    /// </summary>
    public static unsafe class TaskbarProgressService
    {
        // ---- CLSID / IID ----

        /// <summary>CLSID_TaskbarList</summary>
        private static readonly Guid ClsidTaskbarList =
            new("56FDF344-FD6D-11d0-958A-006097C9A090");

        /// <summary>
        /// IID_ITaskbarList4。
        /// ★ 实测：本机（Win11 24H2）上 CoCreateInstance 请求 IID_ITaskbarList3
        ///   会返回 E_NOINTERFACE(0x80004002)，而 ITaskbarList4 可以正常创建。
        ///   ITaskbarList4 继承自 ITaskbarList3，SetProgressValue / SetProgressState
        ///   的虚表槽位完全一致，因此优先用它；两者都失败时才退化为不可用。
        /// </summary>
        private static readonly Guid IidTaskbarList4 =
            new("C43DC798-95D1-4BEA-9030-BB99E2983A1A");

        /// <summary>IID_ITaskbarList3（部分系统上不可创建，作为备选）</summary>
        private static readonly Guid IidTaskbarList3 =
            new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84");

        // ---- 虚表槽位（前 3 个固定为 IUnknown）----

        /// <summary>ITaskbarList::HrInit（必须最先调用，否则后续方法返回 E_UNEXPECTED）</summary>
        private const int SlotHrInit = 3;

        /// <summary>ITaskbarList3::SetProgressValue(HWND, ULONGLONG, ULONGLONG)</summary>
        private const int SlotSetProgressValue = 9;

        /// <summary>ITaskbarList3::SetProgressState(HWND, TBPFLAG)</summary>
        private const int SlotSetProgressState = 10;

        // ---- TBPFLAG ----

        /// <summary>移除进度条（恢复普通任务栏图标）</summary>
        private const int TbpfNoProgress = 0x0;

        /// <summary>正常进度（绿色）</summary>
        private const int TbpfNormal = 0x2;

        /// <summary>暂停态（黄色）</summary>
        private const int TbpfPaused = 0x8;

        /// <summary>CLSCTX_INPROC_SERVER</summary>
        private const int ClsCtxInprocServer = 1;

        /// <summary>COINIT_APARTMENTTHREADED</summary>
        private const uint CoinitApartmentThreaded = 0x0;

        /// <summary>RPC_E_CHANGED_MODE：线程已用其它模式初始化过 COM</summary>
        private const int RpcEChangedMode = unchecked((int)0x80010106);

        // ---- P/Invoke ----

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext, ref Guid riid, out IntPtr ppv);

        // ---- 状态 ----

        private static IntPtr _taskbarList;
        private static IntPtr _hwnd;
        private static bool _initFailed;
        private static int _lastState = -1;

        /// <summary>绑定窗口句柄并创建 ITaskbarList3（幂等；失败后不再重试，避免反复起 COM）。</summary>
        /// <param name="hwnd">主窗口句柄。</param>
        public static void Initialize(IntPtr hwnd)
        {
            if (_taskbarList != IntPtr.Zero)
            {
                _hwnd = hwnd;
                return;
            }
            if (_initFailed || hwnd == IntPtr.Zero)
                return;

            try
            {
                EnsureComInitialized();

                Guid clsid = ClsidTaskbarList;
                // 优先 ITaskbarList4（本机实测 3 会返回 E_NOINTERFACE），失败再退回 ITaskbarList3
                Guid iid = IidTaskbarList4;
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out IntPtr ptr);
                if (hr != 0 || ptr == IntPtr.Zero)
                {
                    AppLogger.Debug($"[Taskbar] ITaskbarList4 创建失败 hr=0x{hr:X8}，尝试 ITaskbarList3");
                    iid = IidTaskbarList3;
                    hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out ptr);
                }
                if (hr != 0 || ptr == IntPtr.Zero)
                {
                    _initFailed = true;
                    AppLogger.Warning($"[Taskbar] 创建 ITaskbarList3/4 均失败 hr=0x{hr:X8}，任务栏进度不可用");
                    return;
                }

                // HrInit 必须最先调用
                var hrInit = (delegate* unmanaged[Stdcall]<IntPtr, int>)VtableEntry(ptr, SlotHrInit);
                hr = hrInit(ptr);
                if (hr != 0)
                {
                    _initFailed = true;
                    Marshal.Release(ptr);
                    AppLogger.Warning($"[Taskbar] ITaskbarList3::HrInit 失败 hr=0x{hr:X8}，任务栏进度不可用");
                    return;
                }

                _taskbarList = ptr;
                _hwnd = hwnd;
                AppLogger.Info("[Taskbar] 任务栏播放进度已就绪");
            }
            catch (Exception ex)
            {
                _initFailed = true;
                AppLogger.Error(ex, "[Taskbar] 初始化任务栏进度失败");
            }
        }

        /// <summary>
        /// 更新任务栏进度。
        /// </summary>
        /// <param name="position">当前位置（秒）。</param>
        /// <param name="duration">总时长（秒）；&lt;=0 时移除进度条。</param>
        /// <param name="isPaused">暂停时显示黄色暂停态。</param>
        public static void Update(double position, double duration, bool isPaused)
        {
            if (_taskbarList == IntPtr.Zero || _hwnd == IntPtr.Zero)
                return;
            if (duration <= 0 || double.IsNaN(duration) || double.IsNaN(position))
            {
                Clear();
                return;
            }

            try
            {
                int state = isPaused ? TbpfPaused : TbpfNormal;
                SetState(state);

                // 进度值用 0~1000 的整数刻度，精度足够且避免浮点跨边界问题
                const ulong scale = 1000;
                double clamped = Math.Clamp(position, 0, duration);
                ulong completed = (ulong)Math.Round(clamped / duration * scale);
                var setValue = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, ulong, int>)
                    VtableEntry(_taskbarList, SlotSetProgressValue);
                setValue(_taskbarList, _hwnd, completed, scale);
            }
            catch (Exception ex)
            {
                // 任务栏进度是锦上添花，任何异常都不应影响播放
                AppLogger.Debug($"[Taskbar] 更新进度失败: {ex.Message}");
            }
        }

        /// <summary>移除任务栏进度条（停止播放 / 关闭播放器 / 切歌时调用）。</summary>
        public static void Clear()
        {
            if (_taskbarList == IntPtr.Zero)
                return;

            try
            {
                SetState(TbpfNoProgress);
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"[Taskbar] 清除进度失败: {ex.Message}");
            }
        }

        /// <summary>释放 COM 引用（应用退出时调用）。</summary>
        public static void Shutdown()
        {
            if (_taskbarList == IntPtr.Zero)
                return;

            try
            {
                SetState(TbpfNoProgress);
            }
            catch { /* 退出路径，无需记录 */ }

            try
            {
                Marshal.Release(_taskbarList);
            }
            catch { /* 退出路径，无需记录 */ }

            _taskbarList = IntPtr.Zero;
            _hwnd = IntPtr.Zero;
            _lastState = -1;
        }

        // ---- 内部辅助 ----

        /// <summary>设置进度状态（相同状态不重复调用，减少跨进程消息）。</summary>
        private static void SetState(int state)
        {
            if (_lastState == state)
                return;

            var setState = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)
                VtableEntry(_taskbarList, SlotSetProgressState);
            setState(_taskbarList, _hwnd, state);
            _lastState = state;
        }

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
                AppLogger.Warning($"[Taskbar] CoInitializeEx 返回异常 HRESULT=0x{hr:X8}（继续尝试调用）");
        }
    }
}
