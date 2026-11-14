using System;
using System.Runtime.InteropServices;

namespace SightoHear.Mpv.Common;

/// <summary>
/// ISwapChainPanelNative（Windows SDK <c>swapchainpanel.h</c>）的 Native AOT 安全调用助手。
/// 只有一个用途：把 mpv 渲染用的 DXGI 交换链挂到 XAML 的 SwapChainPanel 上。
///
/// ★★ 为什么不用原来的 <c>[ComImport] ISwapChainPanelNative</c> + <c>.As&lt;T&gt;()</c> ★★
///
/// 原写法在 Native AOT 下有**两处**必然失败，且都表现为「打开超分视频后黑屏 + 时长 0:00」：
///
///   1) <c>[ComImport]</c> 接口：Native AOT 不内置 COM 支持，这类接口生成的 IL 无效，
///      用 RCW 路径构造对象时会直接抛 InvalidProgramException。本项目的
///      Services/AudioEndpointVolumeService.cs 曾因同样原因整体改写为函数指针调用。
///
///   2) <c>winrt::As&lt;T&gt;()</c>：CsWinRT 的 <c>IObjectReference.AsInterface&lt;T&gt;()</c> 是**开放泛型**，
///      要在运行时为每个 T 构造 COM 包装；AOT 下没有运行时代码生成，于是抛：
///        NotSupportedException: COM Interop requires ComWrapper instance registered for marshalling.
///        at System.Runtime.InteropServices.ComWrappers.ComObjectForInterface
///        at WinRT.IObjectReference.AsInterface[TInterface]()
///        at SightoHear.Mpv.Common.RenderControl.TryLoadFrameBuffer()
///      注意：<c>As&lt;T&gt;</c> 对 CsWinRT 自己生成的投影接口（如 ISwapChainPanel 本身）是可用的，
///      因为生成器为它们预生成了包装；只有**手写的** [ComImport] / 非投影接口才会失败。
///
/// 因此这里改为：拿到 XAML 对象背后的裸 IUnknown* → QueryInterface 出 ISwapChainPanelNative
/// → 按虚表槽位调用 SetSwapChain → Release。与 JIT 下行为完全一致，AOT 下实测可用。
/// </summary>
internal static unsafe class SwapChainPanelNative
{
    /// <summary>IID_ISwapChainPanelNative</summary>
    private static readonly Guid IidISwapChainPanelNative =
        new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    // 虚表槽位：0=QueryInterface 1=AddRef 2=Release，其后才是接口方法。
    // ISwapChainPanelNative 只声明了一个方法：
    //   HRESULT SetSwapChain([in] IDXGISwapChain* swapChain);
    private const int SlotSetSwapChain = 3;

    /// <summary>
    /// 把交换链挂到 <paramref name="swapChainPanel"/> 上（替代 <c>panel.As&lt;ISwapChainPanelNative&gt;().SetSwapChain(h)</c>）。
    /// </summary>
    /// <param name="swapChainPanel">XAML 的 SwapChainPanel 对象（其背后是 WinRT 对象的 CCW/IUnknown）。</param>
    /// <param name="swapChainHandle">IDXGISwapChain* 裸指针。</param>
    /// <returns>true 表示成功；失败时不抛异常，由调用方决定如何降级。</returns>
    public static bool SetSwapChain(object swapChainPanel, IntPtr swapChainHandle)
    {
        if (swapChainPanel is null || swapChainHandle == IntPtr.Zero)
            return false;

        IntPtr unknown = IntPtr.Zero;
        IntPtr panelNative = IntPtr.Zero;
        try
        {
            // 取 WinRT 对象背后的裸 IUnknown*。
            // 走 IWinRTObject.NativeObject.ThisPtr：SwapChainPanel 是 CsWinRT 生成的投影类型，
            // 必定实现该接口，且该路径在 AOT 下不依赖运行时代码生成（已实测可用）。
            if (swapChainPanel is not WinRT.IWinRTObject winrtObject)
            {
                Helpers.AppLogger.Warning(
                    "SwapChainPanelNative：对象不是 WinRT 投影类型（未实现 IWinRTObject），无法取裸接口指针");
                return false;
            }

            unknown = winrtObject.NativeObject.ThisPtr;
            if (unknown == IntPtr.Zero)
                return false;

            Guid iid = IidISwapChainPanelNative;
            // 槽位 0 = QueryInterface
            var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                (*(void***)unknown)[0];
            int hr = queryInterface(unknown, &iid, &panelNative);
            if (hr < 0 || panelNative == IntPtr.Zero)
            {
                Helpers.AppLogger.Warning($"SwapChainPanelNative：QueryInterface 失败 hr=0x{hr:X8}");
                return false;
            }

            // 槽位 3 = SetSwapChain
            var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)
                (*(void***)panelNative)[SlotSetSwapChain];
            hr = setSwapChain(panelNative, swapChainHandle);
            if (hr < 0)
            {
                Helpers.AppLogger.Warning($"SwapChainPanelNative：SetSwapChain 失败 hr=0x{hr:X8}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warning($"SwapChainPanelNative：调用异常 {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            // 只释放自己 QueryInterface 得到的那一份引用；
            // unknown 是 CsWinRT 内部持有的指针，不额外 AddRef，不能 Release。
            if (panelNative != IntPtr.Zero)
                Marshal.Release(panelNative);
        }
    }
}
