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
public unsafe partial class FrameBuffer : FrameBufferBase
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
        double frameBufferWidth,
        double frameBufferHeight,
        double compositionScaleX,
        double compositionScaleY)
    {
        Context = context;
        // ★ 尺寸计算必须与 RenderControl.Draw() 里的判定逻辑用同一条公式，
        //   否则「建出来的尺寸」与「判定期望的尺寸」永远差 1 像素，
        //   会导致帧缓冲无限重建（表现为播放时黑屏闪烁、画面抽搐）。
        //   参数故意用 double：一旦在调用方先 (int) 截断，就会复现该 bug。
        BufferWidth = FrameBufferBase.ComputePixels(frameBufferWidth, compositionScaleX);
        BufferHeight = FrameBufferBase.ComputePixels(frameBufferHeight, compositionScaleY);

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

        // ★★★ 抵消 SwapChainPanel 的合成缩放（修复 125%/150% DPI 下画面放大） ★★★
        //
        // 现象：系统缩放 100% 时画面正常，调到 125% 后视频被放大约 1.25 倍，
        //       右下角被挤出可视区域，等于是从左上角向右下角整体放大。
        //
        // 根因：XAML 的 SwapChainPanel 会把挂上去的交换链按 CompositionScale
        //       （= 系统 DPI 缩放，125% 时为 1.25）自动放大一次，然后按 DIP 布局绘制。
        //       而本项目的交换链已经按"物理像素"创建（DIP × scale，2000×1125），
        //       于是 1.25 的放大被应用了两次 —— 这正是 125% 下偏大 1.25 倍的原因。
        //
        // 标准解法（微软官方 SwapChainPanel 高 DPI 文档）：给交换链设置一个
        //       1 / scale 的矩阵变换，让 XAML 的自动放大被精确抵消，
        //       交换链的物理像素就能 1:1 映射到屏幕像素，画面清晰且尺寸正确。
        //
        // 注意：CreateSwapChainForComposition 创建的交换链必须用 IDXGISwapChain2
        //       及以上接口才能设置矩阵变换；拿不到时静默跳过（退回原行为，不崩溃）。
        ApplyInverseCompositionScale(compositionScaleX, compositionScaleY);

        GLFrameBufferHandle = GL.GenFramebuffer();
    }

    /// <summary>
    /// 给交换链设置 1/scale 的变换矩阵，抵消 SwapChainPanel 对交换链的自动 DPI 放大。
    /// </summary>
    /// <remarks>
    /// <c>DXGI_MATRIX_3X2_F</c> 是行主序的 3x2 仿射矩阵，这里只需要对角线上的缩放：
    /// <code>
    ///   [ 1/scaleX    0      0 ]
    ///   [    0      1/scaleY  0 ]
    /// </code>
    /// 当系统缩放为 100%（scale=1.0）时矩阵是单位阵，行为与不加变换完全一致，
    /// 因此这个修复对 100% 场景零影响。
    /// </remarks>
    private unsafe void ApplyInverseCompositionScale(double scaleX, double scaleY)
    {
        if (SwapChainHandle == IntPtr.Zero) return;
        if (scaleX <= 0 || scaleY <= 0) return;

        try
        {
            // 100% 缩放：单位阵，无需设置（也避免多一次 COM 调用）
            if (Math.Abs(scaleX - 1.0) < 0.001 && Math.Abs(scaleY - 1.0) < 0.001)
                return;

            var swapChain1 = (IDXGISwapChain1*)SwapChainHandle;
            Guid iidSwapChain2 = typeof(IDXGISwapChain2).GetTypeInfo().GUID;
            IDXGISwapChain2* swapChain2 = null;
            int hr = ((IUnknown*)swapChain1)->QueryInterface(&iidSwapChain2, (void**)&swapChain2);
            if (hr < 0 || swapChain2 == null)
            {
                Helpers.AppLogger.Warning(
                    $"libmpv：获取 IDXGISwapChain2 失败 hr=0x{hr:X8}，高 DPI 下画面可能偏大");
                return;
            }

            try
            {
                // DXGI_MATRIX_3X2_F 的内存布局（行主序）：
                //   _11 _12 _21 _22 _31 _32
                // Silk.NET 把下划线去掉后作为字段名：DXGI11/DXGI12/DXGI21/DXGI22/DXGI31/DXGI32。
                // 这里只需要对角线缩放，其余保持 0（default 已全 0）。
                Matrix3X2F matrix = default;
                matrix.DXGI11 = (float)(1.0 / scaleX);
                matrix.DXGI12 = 0f;
                matrix.DXGI21 = 0f;
                matrix.DXGI22 = (float)(1.0 / scaleY);
                matrix.DXGI31 = 0f;
                matrix.DXGI32 = 0f;

                hr = swapChain2->SetMatrixTransform(&matrix);
                if (hr < 0)
                {
                    Helpers.AppLogger.Warning(
                        $"libmpv：设置交换链变换矩阵失败 hr=0x{hr:X8}（缩放 {scaleX:0.###}×{scaleY:0.###}）");
                }
                else
                {
                    Helpers.AppLogger.Info(
                        $"libmpv：已抵消 SwapChainPanel 合成缩放（{scaleX:0.###}×{scaleY:0.###} → 矩阵 1/{scaleX:0.###}）");
                }
            }
            finally
            {
                ((IUnknown*)swapChain2)->Release();
            }
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warning($"libmpv：应用交换链缩放补偿失败（将退回默认行为）: {ex.Message}");
        }
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
