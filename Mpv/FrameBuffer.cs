using Microsoft.UI.Xaml.Media;
using Silk.NET.DXGI;
using OpenTK.Graphics.OpenGL;
using OpenTK.Graphics.Wgl;
using System;
using Silk.NET.Core.Native;
using OpenTK.Platform.Windows;
using Silk.NET.Direct3D11;
using System.Reflection;

namespace SightoHear.Mpv.Common;

/// <summary>
/// GL 帧缓冲封装：创建 D3D11 交换链 + GL 帧缓冲 + WGL_NV_DX_interop 桥接。
/// 尺寸固定，不支持动态 resize。尺寸变化时由 RenderControl 整体销毁重建。
/// </summary>
public unsafe class FrameBuffer : FrameBufferBase
{
    public RenderContext Context { get; }

    /// <summary>GL 颜色渲染缓冲句柄（DX 互操作对象对应的 GL 对象名）。</summary>
    public int GLColorRenderBufferHandle { get; private set; }
    public int GLDepthRenderBufferHandle { get; private set; }
    public int GLFrameBufferHandle { get; private set; }
    public IntPtr DxInteropColorHandle { get; private set; }

    private bool _interopDirty = true;
    private bool _depthDirty = true;
    private bool _disposed;

    public override int BufferWidth { get; protected set; }
    public override int BufferHeight { get; protected set; }
    public override nint SwapChainHandle { get; protected set; }

    public FrameBuffer(
        RenderContext context,
        int frameBufferWidth,
        int frameBufferHeight,
        double compositionScaleX,
        double compositionScaleY)
    {
        Context = context;
        BufferWidth = Convert.ToInt32(frameBufferWidth * compositionScaleX);
        BufferHeight = Convert.ToInt32(frameBufferHeight * compositionScaleY);

        IDXGISwapChain1* swapChain;
        {
            SwapChainDesc1 swapChainDesc = new()
            {
                Width = (uint)BufferWidth,
                Height = (uint)BufferHeight,
                Format = Format.FormatB8G8R8A8Unorm,
                Stereo = 0,
                SampleDesc = new SampleDesc() { Count = 1, Quality = 0 },
                BufferUsage = DXGI.UsageRenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                Flags = 0,
                AlphaMode = AlphaMode.Ignore,
            };
            ((IDXGIFactory2*)Context.DxDeviceFactory)->CreateSwapChainForComposition(
                (IUnknown*)Context.DxDeviceHandle, &swapChainDesc, null, &swapChain);
            SwapChainHandle = (IntPtr)swapChain;
        }

        GLFrameBufferHandle = GL.GenFramebuffer();
    }

    private void UnbindInterop()
    {
        if (DxInteropColorHandle != IntPtr.Zero)
        {
            Wgl.DXUnregisterObjectNV(Context.GlDeviceHandle, DxInteropColorHandle);
            DxInteropColorHandle = IntPtr.Zero;
        }
        if (GLColorRenderBufferHandle != 0)
        {
            GL.DeleteRenderbuffer(GLColorRenderBufferHandle);
            GLColorRenderBufferHandle = 0;
        }
    }

    private void EnsureInterop(ID3D11Texture2D* colorbuffer)
    {
        if (!_interopDirty) return;
        UnbindInterop();
        GLColorRenderBufferHandle = GL.GenRenderbuffer();
        DxInteropColorHandle = Wgl.DXRegisterObjectNV(
            Context.GlDeviceHandle,
            (nint)colorbuffer,
            (uint)GLColorRenderBufferHandle,
            (uint)RenderbufferTarget.Renderbuffer,
            WGL_NV_DX_interop.AccessReadWrite);
        _interopDirty = false;
    }

    private void EnsureDepthBuffer()
    {
        if (!_depthDirty) return;
        if (GLDepthRenderBufferHandle != 0)
        {
            GL.DeleteRenderbuffer(GLDepthRenderBufferHandle);
            GLDepthRenderBufferHandle = 0;
        }
        GLDepthRenderBufferHandle = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, GLDepthRenderBufferHandle);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, BufferWidth, BufferHeight);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, (uint)GLDepthRenderBufferHandle);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.StencilAttachment, RenderbufferTarget.Renderbuffer, (uint)GLDepthRenderBufferHandle);
        _depthDirty = false;
    }

    /// <summary>
    /// 开始一帧渲染：获取交换链后台缓冲、绑定 GL 帧缓冲、锁定 DX 互操作。
    /// 返回 false 时调用方应跳过本帧渲染。
    /// </summary>
    public bool Begin()
    {
        ID3D11Texture2D* colorbuffer = null;
        Guid guid = typeof(ID3D11Texture2D).GetTypeInfo().GUID;
        int hr = ((IDXGISwapChain1*)SwapChainHandle)->GetBuffer(0, &guid, (void**)&colorbuffer);
        if (hr < 0 || colorbuffer == null) return false;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, GLFrameBufferHandle);
        EnsureInterop(colorbuffer);
        EnsureDepthBuffer();
        colorbuffer->Release();

        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, (uint)GLColorRenderBufferHandle);
        Wgl.DXLockObjectsNV(Context.GlDeviceHandle, 1, new[] { DxInteropColorHandle });
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, GLFrameBufferHandle);
        GL.Viewport(0, 0, BufferWidth, BufferHeight);
        return true;
    }

    /// <summary>
    /// 在新创建的 FrameBuffer 上执行一次 Begin→清黑→End，确保交换链后台缓冲
    /// 在被 SwapChainPanel 绑定时显示黑色而非垃圾数据。仅在新 FB 创建后调用一次。
    /// </summary>
    public void PreFillBlack()
    {
        if (Begin())
        {
            try
            {
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            }
            catch { }
            End();
        }
    }

    public void End()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (DxInteropColorHandle != IntPtr.Zero)
            Wgl.DXUnlockObjectsNV(Context.GlDeviceHandle, 1, new[] { DxInteropColorHandle });
        try { ((IDXGISwapChain1*)SwapChainHandle)->Present(0, 0); } catch { }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DeleteFramebuffer(GLFrameBufferHandle);
        UnbindInterop();
        if (GLDepthRenderBufferHandle != 0)
        {
            GL.DeleteRenderbuffer(GLDepthRenderBufferHandle);
            GLDepthRenderBufferHandle = 0;
        }
        if (SwapChainHandle != IntPtr.Zero)
        {
            try { ((IDXGISwapChain1*)SwapChainHandle)->Release(); } catch { }
            SwapChainHandle = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }
}
