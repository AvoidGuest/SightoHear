using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System.Threading;
using System;
using OpenTK.Windowing.Common;
using System.Reflection;
using OpenTK.Graphics.Wgl;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SightoHear.Mpv.Common;

public unsafe class RenderContext
{
    private static IGraphicsContext? _sharedContext;
    private static ContextSettings? _sharedContextSettings;
    private static int _sharedContextReferenceCount;
    private static OpenTK.IBindingsContext? _sharedBindingContext;

    public Format Format { get; }

    public IntPtr DxDeviceFactory { get; }

    public IntPtr DxDeviceHandle { get; }

    public IntPtr DxDeviceContext { get; }

    public IntPtr GlDeviceHandle { get; }

    public IGraphicsContext GraphicsContext { get; }

    public RenderContext(ContextSettings settings)
    {
        IDXGIFactory2* factory;
        ID3D11Device* device;
        ID3D11DeviceContext* devCtx;

        // Factory
        {
            Guid guid = typeof(IDXGIFactory2).GetTypeInfo().GUID;
            DXGI.GetApi(null).CreateDXGIFactory2(0, &guid, (void**)&factory);
            SightoHear.Helpers.AppLogger.Info("libmpv：DXGI 工厂创建完成");
            SightoHear.Helpers.AppLogger.Flush();
        }

        // Device
        {
            var flags = CreateDeviceFlag.BgraSupport | CreateDeviceFlag.VideoSupport;
            D3D11.GetApi(null).CreateDevice(null, D3DDriverType.Hardware, 0, Convert.ToUInt32(flags), null, 0, D3D11.SdkVersion, &device, null, &devCtx);
            SightoHear.Helpers.AppLogger.Info("libmpv：D3D11 设备创建完成");
            SightoHear.Helpers.AppLogger.Flush();
        }

        DxDeviceFactory = (IntPtr)factory;
        DxDeviceHandle = (IntPtr)device;
        DxDeviceContext = (IntPtr)devCtx;

        GraphicsContext = GetOrCreateSharedOpenGLContext(settings);
        SightoHear.Helpers.AppLogger.Info("libmpv：OpenGL 上下文创建完成");
        SightoHear.Helpers.AppLogger.Flush();

        GlDeviceHandle = Wgl.DXOpenDeviceNV((IntPtr)device);
        SightoHear.Helpers.AppLogger.Info("libmpv：WGL_NV_DX_interop 设备打开完成");
        SightoHear.Helpers.AppLogger.Flush();
    }

    /// <summary>
    /// ★ 释放 D3D11 设备/工厂/上下文及 GL 互操作句柄。
    /// 此前 RenderContext 从未释放，每次进出播放器泄漏一整套 D3D11 资源，
    /// 且 WGL_NV_DX_interop 设备句柄累积在静态 GL 上下文中，导致第二次创建
    /// RenderContext 后 mpv 渲染时 OpenGL 报 INVALID_OPERATION 并黑屏。
    /// </summary>
    public void Dispose()
    {
        // 关闭 GL 互操作设备句柄
        if (GlDeviceHandle != IntPtr.Zero)
        {
            try { Wgl.DXCloseDeviceNV(GlDeviceHandle); } catch { }
        }

        // 释放 D3D11 设备上下文
        if (DxDeviceContext != IntPtr.Zero)
        {
            try { ((ID3D11DeviceContext*)DxDeviceContext)->Release(); } catch { }
        }

        // 释放 D3D11 设备
        if (DxDeviceHandle != IntPtr.Zero)
        {
            try { ((ID3D11Device*)DxDeviceHandle)->Release(); } catch { }
        }

        // 释放 DXGI 工厂
        if (DxDeviceFactory != IntPtr.Zero)
        {
            try { ((IDXGIFactory2*)DxDeviceFactory)->Release(); } catch { }
        }

        // 递减共享 GL 上下文引用计数，归零时解绑
        int remaining = Interlocked.Decrement(ref _sharedContextReferenceCount);
        if (remaining <= 0 && _sharedContext != null)
        {
            try { _sharedContext.MakeNoneCurrent(); } catch { }
            _sharedContext = null;
            _sharedContextSettings = null;
            _sharedBindingContext = null;
        }
    }

    public static IntPtr GetProcAddress(string name)
    {
        if(_sharedBindingContext == null)
        {
            return IntPtr.Zero;
        }

        return _sharedBindingContext.GetProcAddress(name);
    }

    private static IGraphicsContext GetOrCreateSharedOpenGLContext(ContextSettings settings)
    {
        if (_sharedContext == null)
        {
            NativeWindowSettings windowSettings = NativeWindowSettings.Default;
            windowSettings.StartFocused = false;
            windowSettings.StartVisible = false;
            windowSettings.NumberOfSamples = 0;
            windowSettings.APIVersion = new Version(settings.MajorVersion, settings.MinorVersion);
            windowSettings.Flags = ContextFlags.Offscreen | settings.GraphicsContextFlags;
            windowSettings.Profile = settings.GraphicsProfile;
            windowSettings.WindowBorder = WindowBorder.Hidden;
            windowSettings.WindowState = WindowState.Minimized;

            // ★★ Native AOT 必须关闭 OpenTK 的自动绑定加载 ★★
            //
            // OpenTK 的 NativeWindow 默认会反射地"猜"绑定类：
            //     Type t = assembly.GetType("OpenTK.Graphics." + name + ".GL");
            //     if (t == null) return;                       // ← 静默返回，不抛异常！
            //     t.GetMethod("LoadBindings").Invoke(null, ...);
            // AOT 下 Assembly.GetType(string) 取不到元数据 → 返回 null → 直接 return，
            // 于是绑定从未加载。之后第一次碰 GL/WGL 就抛：
            //     InvalidOperationException: You need to initialize the OpenGL binding first
            //       by calling LoadBindings() or creating a compatible OpenGL window.
            // 表现为「一打开超分视频就闪退（0xc000027b）」，且日志里只有一条无关痛痒的
            //   「Cannot load assembly 'OpenTK.Graphics'. No metadata found for this assembly.」
            // 极难定位。OpenTK 的异常信息本身就建议这条路：AutoLoadBindings=false + 手动加载。
            windowSettings.AutoLoadBindings = false;

            NativeWindow nativeWindow = new(windowSettings);

            _sharedContext = nativeWindow.Context;
            _sharedContextSettings = settings;

            // 先让上下文成为当前上下文，再解析函数指针：
            // GLFWBindingsContext.GetProcAddress 走 glfwGetProcAddress，没有当前上下文时
            // 部分 GL 入口点会解析失败（返回 IntPtr.Zero → 调用时 AccessViolation）。
            _sharedContext.MakeCurrent();

            // 手动加载绑定：GL 供 mpv 渲染回调与 FrameBuffer 直接调用，
            // WGL 供随后的 WGL_NV_DX_interop（D3D11 纹理与 GL 互操作）使用。
            // 两者必须用同一个 IBindingsContext，避免函数指针表来自不同的解析上下文。
            _sharedBindingContext = new GLFWBindingsContext();
            OpenTK.Graphics.OpenGL.GL.LoadBindings(_sharedBindingContext);
            Wgl.LoadBindings(_sharedBindingContext);
        }
        else
        {
            if (!ContextSettings.WouldResultInSameContext(settings, _sharedContextSettings!))
            {
                throw new ArgumentException($"The provided {nameof(ContextSettings)} would result" +
                                                $"in a different context creation to one previously created. To fix this," +
                                                $" either ensure all of your context settings are identical, or provide an " +
                                                $"external context via the '{nameof(ContextSettings.ContextToUse)}' field.");
            }
        }

        Interlocked.Increment(ref _sharedContextReferenceCount);

        return _sharedContext;
    }
}
