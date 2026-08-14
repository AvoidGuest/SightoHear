// Copyright (c) Bili Copilot. All rights reserved.

using SightoHear.Mpv.Args;
using SightoHear.Mpv.Interop;
using SightoHear.Mpv.Structs.Render;
using SightoHear.Mpv.Structs.RenderGL;
using System.Runtime.InteropServices;
using System.Threading;

namespace SightoHear.Mpv;

public sealed partial class Player
{
    public Player()
    {
        Client = new MpvClientNative();
        Dependencies = new Structs.Player.LibMpvDependencies();
    }

    public async Task DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Client.UnObserveProperties();
        RenderContext?.Destroy();
        await Client.DestroyAsync();
    }

    public async Task TerminateAsync()
    {
        _isDisposed = true;
        await Client.DestroyAsync(true);
    }

    public async Task InitializeAsync(InitializeArgument? argument = null)
    {
        if (Client.IsInitialized)
        {
            return;
        }

        AutoPlay = argument?.AutoPlay ?? true;
        await Client.InitializeAsync();
        SightoHear.Helpers.AppLogger.Info("libmpv：mpv_initialize 完成");
        RerunEventLoop();
        SightoHear.Helpers.AppLogger.Info("libmpv：mpv 事件循环已启动");
        if (argument?.ConfigFile is not null)
        {
            await Client.LoadConfigAsync(argument.ConfigFile);
        }

        if (argument?.OpenGLGetProcAddress is not null)
        {
            var glParams = new MpvOpenGLInitParams
            {
                GetProcAddrFn = (ctx, name) =>
                {
                    return argument!.OpenGLGetProcAddress!(name);
                },

                GetProcAddressCtx = IntPtr.Zero
            };

            // ★ 修复：保持 get_proc_address 委托存活（mpv 会在渲染期间持续调用该回调，
            //   若托管委托被 GC 回收，后续调用将导致 AccessViolation 崩溃）
            _glGetProcAddressDelegate = glParams.GetProcAddrFn;

            var glParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(glParams));
            Marshal.StructureToPtr(glParams, glParamsPtr, false);
            var glStringPtr = Marshal.StringToCoTaskMemUTF8("opengl");
            SightoHear.Helpers.AppLogger.Info("libmpv：开始创建 mpv 渲染上下文（mpv_render_context_create）");
            SightoHear.Helpers.AppLogger.Flush();
            RenderContext = new MpvRenderContextNative(
                Client.Handle,
                [
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.ApiType, Data = glStringPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.OpenGLInitParams, Data = glParamsPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero },
                ]);
            SightoHear.Helpers.AppLogger.Info("libmpv：mpv_render_context_create 完成");
            SightoHear.Helpers.AppLogger.Flush();

            Marshal.FreeHGlobal(glParamsPtr);
            Marshal.FreeCoTaskMem(glStringPtr);
        }
    }

    // ★ 预分配渲染参数数组，消除每帧 Marshal.AllocHGlobal/FreeHGlobal。
    //   原实现每帧两次 AllocHGlobal + FreeHGlobal，在 60fps 下每秒产生 ~120 次
    //   不必要的堆分配，增加 GC 压力且触发 GlobalAlloc 锁竞争。
    //   现改为栈分配结构体 + fixed 取地址 + 复用参数数组，零 GC 压力。
    private readonly MpvRenderParam[] _renderParams = new MpvRenderParam[3];

    public void RenderGL(int width, int height, int fboInt)
    {
        var fbo = new MpvOpenGLFBO
        {
            Fbo = fboInt,
            W = width,
            H = height,
            InternalFormat = 0
        };
        int flipY = 0;

        unsafe
        {
            MpvOpenGLFBO* fboPtr = &fbo;
            int* flipYPtr = &flipY;
            _renderParams[0] = new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Fbo, Data = (IntPtr)fboPtr };
            _renderParams[1] = new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.FlipY, Data = (IntPtr)flipYPtr };
            _renderParams[2] = new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero };
            RenderContext!.Render(_renderParams);
        }
    }

    /// <summary>
    /// 通知 mpv 渲染上下文当前帧已提交（Present 完成）。
    /// 必须在交换链 Present 之后调用，让 mpv 正确计算帧时序与帧率。
    /// </summary>
    public void ReportSwap()
    {
        try { RenderContext?.ReportSwap(); } catch { }
    }

    public void RerunEventLoop()
    {
        if (_eventLoopCancellationTokenSource != null)
        {
            _eventLoopCancellationTokenSource.Cancel();
            _eventLoopCancellationTokenSource.Dispose();
        }

        Client.UnObserveProperties();
        _eventLoopCancellationTokenSource = new CancellationTokenSource();
        _eventLoopTask = Task.Run(EventLoop, _eventLoopCancellationTokenSource.Token);
        Client.ObserveProperty(PauseProperty, Enums.Client.MpvFormat.Flag);
        Client.ObserveProperty(DurationProperty, Enums.Client.MpvFormat.Int64);
        Client.ObserveProperty(PositionProperty, Enums.Client.MpvFormat.Int64);
        Client.ObserveProperty(PausedForCacheProperty, Enums.Client.MpvFormat.Flag);
    }

    public bool IsMediaLoaded()
        => _isLoaded && !_isDisposed;

    public async Task ExecuteAfterMediaLoadedAsync(string command)
    {
        if (IsMediaLoaded())
        {
            await Client.ExecuteAsync(command);
        }
    }

    public void Play()
    {
        if (IsMediaLoaded())
        {
            Client.SetProperty(PauseProperty, false);
        }
    }

    public void Pause()
    {
        if (IsMediaLoaded())
        {
            Client.SetProperty(PauseProperty, true);
        }
    }

    public void Seek(TimeSpan ts)
    {
        var pos = ts.TotalSeconds;
        if (pos < 0)
        {
            pos = 0;
        }

        // ★ 修复（断点续播可靠性）：改用 mpv 原生 seek 命令（absolute）实现绝对定位，
        //   不再依赖 _currentDuration（由 Int64 观察 duration 维护，异常为 0 时
        //   原来的 SetProperty 分支会全部跳过导致 seek 静默失败、每次重开都从 0 播放）。
        var posStr = pos.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _ = Client.ExecuteAsync(new[] { "seek", posStr, "absolute" });
    }

    public void SetSpeed(double rate)
    {
        if (IsMediaLoaded())
        {
            Client.SetProperty(SpeedProperty, rate);
        }
    }

    /// <summary>设置画面比例（null = 适应/原始比例；如 "4:3"、"16:9"、"16:10"）。
    /// video-aspect-override 是 mpv 全局属性，不依赖媒体加载，切换视频后仍保留。</summary>
    public void SetAspectRatio(string? aspect)
    {
        if (Client.IsInitialized)
        {
            Client.SetProperty(AspectRatioProperty, aspect ?? "no");
        }
    }

    public void SetVolume(int volume)
    {
        if (IsMediaLoaded())
        {
            Client.SetProperty(VolumeProperty, volume);
        }
    }

    public void ResetDuration()
    {
        if (IsMediaLoaded())
        {
            _currentDuration = Client.GetPropertyToLong(DurationProperty);
        }
    }

    public bool IsPaused()
        => Client.GetPropertyToBoolean(PauseProperty);

    public async Task TakeScreenshotAsync(string filePath)
    {
        if (IsMediaLoaded())
        {
            await Client.ExecuteAsync($"screenshot-to-file {filePath}");
        }
    }
}
