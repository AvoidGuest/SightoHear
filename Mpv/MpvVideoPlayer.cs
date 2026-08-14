using OpenTK.Graphics.OpenGL;
using SightoHear.Helpers;
using SightoHear.Mpv.Args;
using SightoHear.Mpv.Common;
using SightoHear.Mpv.Enums.Client;
using SightoHear.Mpv.Enums.Player;
using SightoHear.Mpv.Structs.Client;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SightoHear.Mpv
{
    /// <summary>
    /// 视频播放器超分模式（libmpv 内核）封装。
    /// 负责 mpv 渲染上下文初始化、播放控制、Anime4K 超分 shader 链的加载与切换，
    /// 以及音频输出设备设置。仅供 VideoPlayerPage 在超分模式下使用。
    /// </summary>
    public sealed class MpvVideoPlayer
    {
        private Player? _player;
        private RenderControl? _renderControl;
        private bool _disposed;
        private bool _renderSubscribed;
        private bool _isReady;

        /// <summary>
        /// ★ 委托保活：mpv 渲染更新回调（mpv_render_context_set_update_callback）。
        /// 回调通过 P/Invoke 传给原生 mpv，原生侧持有函数指针并在任意线程调用。
        /// 若托管 Delegate 对象被 GC 回收，原生调用将触发 AccessViolation 崩溃。
        /// 将委托引用存储在此字段中，确保其在整个播放器生命周期内不被回收。
        /// （参考 Player.Properties.cs 中 _glGetProcAddressDelegate 的相同模式）
        /// </summary>
        private SightoHear.Mpv.Interop.MpvRenderContextNative.MpvRenderUpdateCallback? _updateCallbackDelegate;

        /// <summary>播放状态变化事件。</summary>
        public event EventHandler<PlaybackState>? PlaybackStateChanged;

        /// <summary>播放位置变化事件（位置秒数, 时长秒数）。</summary>
        public event Action<double, double>? PositionChanged;

        /// <summary>播放结束事件。</summary>
        public event Action? Ended;

        /// <summary>mpv 日志事件（级别 + 消息文本，供上层按需记录与过滤）。</summary>
        public event Action<MpvLogLevel, string>? LogMessage;

        /// <summary>当前播放状态。</summary>
        public PlaybackState State { get; private set; }

        /// <summary>
        /// mpv 实时暂停状态：直接读取 mpv `pause` 属性（比 State 事件更真实可靠——
        /// 缓冲（paused-for-cache）、外部（SMTC/快捷键）等未触发 PlaybackStateChanged 的
        /// 暂停也能正确反映）。媒体未加载或属性读取失败时返回 null，由调用方兜底。
        /// </summary>
        public bool? IsPausedNow
        {
            get
            {
                if (_player?.Client.IsInitialized != true || !IsMediaLoaded)
                {
                    return null;
                }
                try
                {
                    return _player.IsPaused();
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>当前播放位置（秒）。</summary>
        public double Position { get; private set; }

        /// <summary>媒体时长（秒）。</summary>
        public double Duration { get; private set; }

        /// <summary>mpv 是否已初始化。</summary>
        public bool IsInitialized => _player?.Client.IsInitialized ?? false;

        /// <summary>媒体是否已加载。</summary>
        public bool IsMediaLoaded => _player?.IsMediaLoaded() ?? false;

        /// <summary>
        /// mpv 内核是否已完成全部初始化步骤（InitializeAsync 完整执行完毕）。
        /// 供上层在并发场景（如快速双击）下等待初始化完成后再 loadfile，
        /// 避免 loadfile 撞上尚未完成的 mpv_initialize / render_context 创建。
        /// </summary>
        public bool IsReady => _isReady;

        /// <summary>
        /// 运动补偿（补帧）是否受支持。
        /// 依赖内置 VapourSynth 便携运行时（VSScript + python312 + MVTools/SVPFlow 插件），
        /// 其二进制均为 x64，仅 x64 平台可加载；x86/ARM64 平台不支持（返回 false 时上层禁用入口）。
        /// </summary>
        public static bool IsMotionCompensationSupported
            => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X64;

        /// <summary>
        /// 初始化 mpv 内核：创建渲染宿主（D3D11 + OpenGL 上下文）、初始化渲染上下文、
        /// 设置基础选项，并恢复已保存的音频设备与超分设置。
        /// </summary>
        public async Task InitializeAsync(RenderControl renderControl)
        {
            if (_player != null)
            {
                return;
            }

            // 配置 libmpv 原生库路径（Endpne 包将 libmpv-2.dll 复制到 libmpv\{win-x64|win-arm64} 子目录）
            var archDir = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
            Resolver.SetCustomMpvPath(Path.Combine(AppContext.BaseDirectory, "libmpv", archDir, "libmpv-2.dll"));
            AppLogger.Info("libmpv：libmpv 路径已配置");

            _renderControl = renderControl;

            _player = new Player();
            AppLogger.Info("libmpv：mpv 客户端创建成功");
            _player.PlaybackPositionChanged += OnPositionChanged;
            _player.PlaybackStateChanged += OnStateChanged;
            _player.PlaybackStopped += OnStopped;
            _player.LogMessageReceived += OnLogMessage;

            // 订阅渲染回调：每帧把 mpv 画面渲染进 SwapChainPanel 交换链
            _renderSubscribed = true;
            renderControl.Render += OnRender;
            // ★ Present 完成回调：通知 mpv 帧时序（ReportSwap），
            //   确保渲染线程的 Present 后 mpv 正确计算帧间隔与视频时钟。
            renderControl.Present += OnPresent;

            // 使用 Richasy 生产验证的 GL 上下文配置（4.6 Compatibility）：
            // libplacebo（mpv 0.41 GPU 后端）在 3.3 Core 上下文的某些 AMD 驱动上初始化会崩溃
            renderControl.Setting = new ContextSettings
            {
                MajorVersion = 4,
                MinorVersion = 6,
                GraphicsProfile = OpenTK.Windowing.Common.ContextProfile.Compatability,
            };

            // mpv 初始化完成前暂停渲染循环，避免 GL 操作与 mpv 渲染上下文初始化交错
            renderControl.IsRenderingEnabled = false;

            AppLogger.Info("libmpv：渲染回调已订阅，开始初始化渲染宿主");
            AppLogger.Flush();
            renderControl.Initialize();
            AppLogger.Info("libmpv：渲染宿主初始化完成（D3D11 + OpenGL 上下文已创建）");
            AppLogger.Flush();

            // 初始化 mpv 渲染上下文（OpenGL）。configFile 传 null 表示不使用配置文件
            var initArgs = new InitializeArgument(null!, true, RenderContext.GetProcAddress);

            // 确保 GL 上下文在当前（UI）线程为 current，mpv_render_context_create 需要在此状态下执行
            if (renderControl.Context?.GraphicsContext is { } glContext)
            {
                try { glContext.MakeCurrent(); } catch { }
            }

            // GL 自检：确认上下文有效并记录显卡信息（mpv 渲染上下文初始化失败时用于定位）
            try
            {
                var glVersion = GL.GetString(OpenTK.Graphics.OpenGL.StringName.Version);
                var glRenderer = GL.GetString(OpenTK.Graphics.OpenGL.StringName.Renderer);
                var glVendor = GL.GetString(OpenTK.Graphics.OpenGL.StringName.Vendor);
                AppLogger.Info($"libmpv：GL 自检 版本={glVersion} 渲染器={glRenderer} 厂商={glVendor}");
                AppLogger.Flush();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "libmpv：GL 自检失败");
                AppLogger.Flush();
            }

            await _player.InitializeAsync(initArgs);
            AppLogger.Info("libmpv：mpv 渲染上下文（OpenGL）初始化完成");
            AppLogger.Flush();

            // ★ 设置 mpv 渲染更新回调：mpv 内部新帧就绪时调用此回调，
            //   通过 RenderControl.SignalFrameReady() 唤醒独立渲染线程绘制。
            //   替代此前 CompositionTarget.Rendering 的盲轮询（每 ~16ms 触发一次，
            //   无论是否有新帧），减少不必要的 GPU 操作和 composition 线程占用。
            try
            {
                // ★ 委托保活（参考 _glGetProcAddressDelegate）：
                //   MpvRenderUpdateCallback 通过 P/Invoke 传给原生 mpv，
                //   原生侧存储函数指针并在任意线程调用。
                //   托管 Delegate 对象必须被本字段持有，防止 GC 回收后
                //   原生调用触发 AccessViolation（0xc0000005）崩溃。
                _updateCallbackDelegate = ctx => renderControl.SignalFrameReady();
                _player.RenderContext!.SetUpdateCallback(_updateCallbackDelegate, IntPtr.Zero);
                AppLogger.Info("libmpv：渲染更新回调已设置（按需驱动渲染线程）");
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"libmpv设置渲染更新回调失败（将回退到盲轮询）: {ex.Message}");
                // 回调失败不致命，渲染线程在没有信号时会每 500ms 超时自检一次
            }

            // ★ 释放 UI 线程 GL 上下文所有权：mpv 渲染上下文已创建完毕，
            //   后续操作（属性设置、补帧/超分配置）均不依赖 GL。
            //   渲染线程在首次 Draw() 时将自动获取上下文。
            if (renderControl.Context?.GraphicsContext is { } glCtx)
            {
                try { glCtx.MakeNoneCurrent(); } catch { }
            }

            // 渲染 API（vo=libmpv）模式下 gpu-context 由 render 上下文决定，不能显式设置
            // ★ 防御：所有 mpv 属性设置统一走 SetOptionalProperty——单个属性在特定 mpv
            //   版本不可用（如 mpv 0.33+ 已移除 cscale）时仅记录日志，绝不中断初始化，
            //   否则会导致 loadfile 永远无法执行、视频黑屏无法播放。
            SetOptionalProperty("vo", "libmpv");
            SetOptionalProperty("hwdec", "auto-safe");
            SetOptionalProperty("ao", "wasapi");
            // ★ 修复（画质模糊）：mpv 默认 scale=bilinear，窗口放大播放时画面明显发糊。
            //   这里启用高质量缩放算法：放大用 ewa_lanczossharp（Lanczos 锐化），
            //   缩小用 Mitchell（抗锯齿），并开启缩小校正。
            //   注意：cscale 在 mpv 0.33+ 已并入 scale，0.41 中不存在该属性，
            //   设置会抛错导致初始化中断，因此这里不再设置。
            //   当 Anime4K 超分满足 WHEN 条件（渲染尺寸 > 原分辨率 1.2 倍）时会接管放大，
            //   其余情况（如窗口小于原分辨率）由这些高质量算法保证清晰度。
            SetOptionalProperty("scale", "ewa_lanczossharp");
            SetOptionalProperty("dscale", "mitchell");
            SetOptionalProperty("correct-downscaling", true);
            // ★ 日志瘦身：此前为排查问题注册了 V（非常嘈杂）级日志，
            //   播放时 cplayer/demux/解码细节每条都输出，单次播放即可产生数千行日志。
            //   现收敛为 Warn 级（仅保留警告/错误，便于问题定位），需要详细日志排查时再临时调回 V。
            _player.Client.RequestLogMessage(MpvLogLevel.Warn);

            // 应用已保存的音频输出设备（空 = 跟随系统默认）。
            // 初始化阶段（未加载文件、音频输出未激活）仅设置属性记住设备，
            // 不执行 ao-reload——其为 no-op 但 mpv_command_ret 同步等待核心响应，
            // 渲染循环启动后核心线程可能忙于渲染同步，互相等待会拖死 UI 线程
            // （详见 SetAudioDeviceAsync 的说明）。
            if (!string.IsNullOrEmpty(App.SettingsHelper.VideoOutputDeviceId))
            {
                await SetAudioDeviceAsync(App.SettingsHelper.VideoOutputDeviceId, reload: false);
            }

            // 应用已保存的超分设置
            if (App.SettingsHelper.VideoSuperResolutionEnabled)
            {
                await ApplySuperResolutionAsync(true, App.SettingsHelper.VideoSuperResolutionQuality);
            }

            // 应用已保存的运动补偿（补帧）设置（仅 x64 平台支持 VapourSynth 运行时）
            if (App.SettingsHelper.VideoMotionCompensationEnabled && IsMotionCompensationSupported)
            {
                await ApplyMotionCompensationAsync(true, App.SettingsHelper.VideoMotionCompensationMode);
            }

            // ★ 修复（崩溃/UI 挂起）：渲染循环在全部初始化软设置完成后才启动——
            //   此前 mpv_render_context_render 在属性/命令设置期间与核心线程的同步
            //   命令（如 change-list、audio-device）竞争，渲染回调每帧等待核心响应，
            //   而核心线程被同步命令占住时两者互相等待，UI 线程（CompositionTarget.
            //   Rendering 驱动）随之完全无响应，最终 WinUI 抛 0xc000027b stowed
            //   exception（CoreMessagingXP.dll）崩溃。初始化阶段不渲染帧没有副作用
            //   （loadfile 在 InitializeAsync 返回后才执行，此时渲染循环已启动）。
            renderControl.IsRenderingEnabled = true;

            // ★ 标记初始化完成：上层（LoadVideoMpvAsync）在并发场景下等待该标志
            //   后再执行 loadfile，避免 loadfile 撞上未完成的初始化流程。
            _isReady = true;
            AppLogger.Info("libmpv mpv 内核初始化完成（IsReady）");
        }

        /// <summary>加载并播放视频文件。</summary>
        public Task LoadAsync(string filePath)
        {
            var player = _player;
            if (player == null)
            {
                return Task.CompletedTask;
            }

            // 数组形式传参，避免 mpv 命令字符串对反斜杠路径的转义问题
            return player.Client.ExecuteWithResultAsync(new[] { "loadfile", filePath });
        }

        /// <summary>停止当前播放（保留渲染上下文）。</summary>
        public Task StopAsync()
        {
            var player = _player;
            if (player == null)
            {
                return Task.CompletedTask;
            }

            return player.Client.ExecuteWithResultAsync(new[] { "stop" });
        }

        public void Play()
        {
            if (IsMediaLoaded)
            {
                _player!.Play();
            }
        }

        public void Pause()
        {
            if (IsMediaLoaded)
            {
                _player!.Pause();
            }
        }

        public void TogglePlayPause()
        {
            if (State == PlaybackState.Playing)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        public void Seek(double seconds)
        {
            if (IsMediaLoaded)
            {
                _player!.Seek(TimeSpan.FromSeconds(seconds));
            }
        }

        public void SetSpeed(double rate)
        {
            if (IsMediaLoaded)
            {
                _player!.SetSpeed(rate);
            }
        }

        /// <summary>设置画面比例（"适应"=null；如 "4:3"、"16:9"、"16:10"）。
        /// video-aspect-override 为 mpv 全局属性，切换视频后自动保留。</summary>
        public void SetAspectRatio(string? aspect)
        {
            if (_player?.Client.IsInitialized == true)
            {
                _player.SetAspectRatio(aspect);
            }
        }

        public void SetVolume(int volumePercent)
        {
            if (_player?.Client.IsInitialized == true)
            {
                _player.Client.SetProperty("volume", (long)Math.Clamp(volumePercent, 0, 100));
            }
        }

        public void SetMuted(bool muted)
        {
            if (_player?.Client.IsInitialized == true)
            {
                _player.Client.SetProperty("mute", muted);
            }
        }

        /// <summary>
        /// ★ 防御：设置 mpv 属性，单个属性失败仅记录日志，不抛异常。
        /// 不同 mpv 版本的选项集合不同（如 cscale 在 0.33+ 已移除），若某个可选
        /// 属性设置失败就中断初始化，会导致 loadfile 永远无法执行、视频黑屏无法播放。
        /// </summary>
        private void SetOptionalProperty(string name, object value)
        {
            var client = _player?.Client;
            if (client == null || !client.IsInitialized)
            {
                return;
            }

            try
            {
                switch (value)
                {
                    case bool b:
                        client.SetProperty(name, b);
                        break;
                    case long l:
                        client.SetProperty(name, l);
                        break;
                    case double d:
                        client.SetProperty(name, d);
                        break;
                    case string s:
                        client.SetProperty(name, s);
                        break;
                    default:
                        client.SetProperty(name, value.ToString() ?? string.Empty);
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"libmpv设置 mpv 属性失败（已忽略，不影响播放）: {name}={value} → {ex.Message}");
            }
        }

        /// <summary>
        /// 设置音频输出设备（WASAPI）。deviceId 为空时恢复为系统默认设备。
        /// reload=true（播放中切换设备）时，设置属性后执行 ao-reload 命令立即重启音频输出；
        /// reload=false（初始化阶段/未加载文件）时仅设置属性记住设备，加载文件后自动生效。
        /// </summary>
        public async Task SetAudioDeviceAsync(string? deviceId, bool reload = true)
        {
            if (_player?.Client.IsInitialized != true)
            {
                return;
            }

            try
            {
                // mpv wasapi 输出设备的 ID 格式为 wasapi/{设备标识}。
                // ★ 修复（libmpv 音频输出设备设置不生效）：DeviceInformation.Id 返回的是
                //   完整设备实例路径（\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{GUID}#{类GUID}），
                //   而 mpv 的 ao_wasapi 用 IMMDevice::GetId() 返回的短 ID（枚举时剥掉
                //   {0.0.0.00000000}. 前缀）匹配设备，直接传完整路径会导致
                //   "ao/wasapi: Failed to find device" 且音频输出初始化失败、播放静音，
                //   因此必须先规范化为 mpv 期望的格式再设置。
                var normalized = string.IsNullOrEmpty(deviceId)
                    ? null
                    : NormalizeMpvDeviceId(deviceId);
                var device = string.IsNullOrEmpty(normalized) ? "auto" : $"wasapi/{normalized}";
                _player.Client.SetProperty("audio-device", device);
                AppLogger.Info($"libmpv切换音频输出设备: {device}{(reload ? string.Empty : "（初始化阶段，加载文件后生效）")}");

                // ★ 修复（崩溃/UI 挂起）：ao-reload 仅在播放中切换设备时执行。
                //   mpv_command_ret 是同步命令——会阻塞线程池线程等待 mpv 核心处理并回复。
                //   初始化阶段（未加载文件、音频输出未激活）ao-reload 虽是 no-op，
                //   但渲染循环（IsRenderingEnabled=true）启动后每帧 mpv_render_context_render
                //   都需要核心线程参与；若核心线程此刻被同步命令占住，渲染回调会被拖住，
                //   UI 线程（CompositionTarget.Rendering 驱动）随之完全无响应，
                //   最终 WinUI 在事件分发时抛 0xc000027b stowed exception（CoreMessagingXP.dll）
                //   崩溃（实测：初始化完成后 19 秒无日志 + 崩溃）。因此：
                //     1. 初始化阶段 reload=false，跳过 ao-reload（属性已设置，加载文件时生效）；
                //     2. 播放中切换时 ao-reload 加 3 秒超时兜底，超时后放弃等待不再阻塞。
                if (reload)
                {
                    try
                    {
                        var reloadTask = _player.Client.ExecuteWithResultAsync(new[] { "ao-reload" });
                        var completed = await Task.WhenAny(reloadTask, Task.Delay(3000));
                        if (completed != reloadTask)
                        {
                            AppLogger.Warning("libmpv ao-reload 超时（3 秒），设备将在下次音频重载时生效");
                        }
                        else
                        {
                            // ao-reload 命令本身失败时在此抛出，由内层 catch 记录
                            await reloadTask;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 早期 mpv 版本可能无此命令：属性已设置，下次加载文件时仍会生效
                        AppLogger.Warning($"libmpv ao-reload 失败（属性已设置，不影响后续生效）: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"libmpv设置音频输出设备失败: {deviceId}");
            }
        }

        /// <summary>
        /// 将 Windows 音频设备 ID（DeviceInformation.Id）规范化为 mpv wasapi 期望的设备标识。
        /// 实测（IMMDevice::GetId() vs DeviceInformation.Id）确认两者格式不同：
        ///   IMMDevice::GetId() → "{0.0.0.00000000}.{GUID}"（无完整路径前缀，无 #{类GUID} 后缀）
        ///   DeviceInformation.Id → "\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{GUID}#{类GUID}"（完整路径）
        /// mpv 0.41 的 ao_wasapi（ao_wasapi_utils.c）枚举设备时把 IMMDevice::GetId() 结果
        /// 剥掉 "{0.0.0.00000000}." 前缀作为设备标识（d-&gt;id），匹配时对用户传入的 ID 做同样
        /// 处理（bstr_eatstart0）再精确比较（区分大小写）。因此转换规则需与 mpv 完全对称：
        ///   1. 去掉 "\\?\SWD#MMDEVAPI#" 完整路径前缀；
        ///   2. 剥掉 "{0.0.0.00000000}." 流类别段；
        ///   3. 去掉 "#{类GUID}" 接口类后缀（WinRT 完整路径独有，IMMDevice::GetId() 不含，
        ///      残留会导致 bstrcmp 与 d-&gt;id 永不相等）。
        /// 最终得到 "{GUID}" 形式，与 mpv 枚举的设备标识一致。
        /// 若 ID 已是非完整路径格式（不含上述特征），原样返回。
        /// </summary>
        private static string NormalizeMpvDeviceId(string deviceId)
        {
            const string InstancePathPrefix = @"\\?\SWD#MMDEVAPI#";
            const string CategorySegment = "{0.0.0.00000000}.";

            var id = deviceId;

            // 1. 去掉完整设备实例路径前缀（WinRT 格式专属）
            if (id.StartsWith(InstancePathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                id = id.Substring(InstancePathPrefix.Length);
            }

            // 2. 剥掉流类别段 {0.0.0.00000000}.（与 mpv get_device_desc 的处理对称）
            if (id.StartsWith(CategorySegment, StringComparison.Ordinal))
            {
                id = id.Substring(CategorySegment.Length);
            }

            // 3. 去掉接口类 GUID 后缀 "#{...}"（IMMDevice::GetId() 返回的 ID 不含该段）
            int hashIndex = id.IndexOf('#');
            if (hashIndex >= 0)
            {
                id = id.Substring(0, hashIndex);
            }

            return id;
        }

        /// <summary>
        /// 应用 Anime4K 超分 shader 链。
        /// quality 四档（按画质/开销从低到高）：
        ///   Low    → VL 模型（最低画质、最快速度）
        ///   Medium → S 模型（轻量、速度快）
        ///   High   → M 模型（画质较好、开销较大）
        ///   Ultra  → UL 模型（最高画质、开销最大，充分发挥 GPU 极限性能）
        /// 未知档位回退到 Medium。
        /// </summary>
        public async Task ApplySuperResolutionAsync(bool enabled, string quality)
        {
            if (_player?.Client.IsInitialized != true)
            {
                return;
            }

            try
            {
                // 清空所有 shader（clr 必须带空字符串参数，mpv 0.41 要求）
                await _player.Client.ExecuteWithResultAsync(new[] { "change-list", "glsl-shaders", "clr", "" });

                if (!enabled)
                {
                    AppLogger.Info("libmpv：已关闭超分辨率");
                    return;
                }

                var shaders = quality switch
                {
                    "Low" => new[] { "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_VL.glsl", "Anime4K_Upscale_CNN_x2_VL.glsl" },
                    "High" => new[] { "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_M.glsl", "Anime4K_Upscale_CNN_x2_M.glsl" },
                    "Ultra" => new[] { "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_UL.glsl", "Anime4K_Upscale_CNN_x2_UL.glsl" },
                    _ => new[] { "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_S.glsl", "Anime4K_Upscale_CNN_x2_S.glsl" }
                };

                foreach (var name in shaders)
                {
                    var path = GetShaderPath(name);
                    if (File.Exists(path))
                    {
                        await _player.Client.ExecuteWithResultAsync(new[] { "change-list", "glsl-shaders", "append", path });
                    }
                    else
                    {
                        AppLogger.Warning($"Anime4K shader 文件不存在: {path}");
                    }
                }

                AppLogger.Info($"libmpv：已应用 Anime4K 超分（{quality} 档，{shaders.Length} 个 shader）");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "应用 Anime4K 超分 shader 链失败");
            }
        }

        /// <summary>获取 Anime4K shader 文件完整路径（Assets/Anime4K 目录）。</summary>
        private static string GetShaderPath(string fileName)
            => Path.Combine(AppContext.BaseDirectory, "Assets", "Anime4K", fileName);

        /// <summary>
        /// 应用运动补偿（补帧）滤镜。
        /// 原理：通过 mpv 的 vf vapoursynth 滤镜加载 VapourSynth 脚本（MEMC_*.vpy），
        /// 脚本内由 k7sfunc 调用 MVTools（libmvtools.dll）或 SVPFlow（svpflow1/2_vs64.dll）
        /// 对视频做光流补帧。运行时依赖随应用分发的便携式 VapourSynth（VSScript.dll 等
        /// 位于 exe 同级目录，见 csproj CopyVapourSynthRuntime Target）。
        /// mode 四档：
        ///   MVT_LQ  → MVTools 补帧-LQ（倍帧：帧率 ×2，开销一般）
        ///   MVT_STD → MVTools 补帧-STD（目标 60fps，偏保守、中等消耗）
        ///   SVP_LQ  → SVPFlow 补帧-LQ（倍帧：帧率 ×2，中等消耗）
        ///   SVP_PRO → SVPFlow 补帧-PRO（目标 60fps，高质量）
        /// 未知档位回退 MVT_LQ。
        /// ★ 修复（2026-08-12）：关闭不再"假装成功"。
        ///   mpv 的 vf remove 按 label 或"名称 + 完整参数列表 + 参数顺序"匹配（见 mpv 手册
        ///   vf-remove）；此前开启用 vf set vapoursynth="path"（带 script 参数、无 label），
        ///   关闭却用 vf remove vapoursynth（仅名称、无参数）必然匹配失败，且失败被 catch
        ///   静默吞掉，随后打印"已关闭"直接返回——滤镜残留，运动补偿实际仍生效（症状：
        ///   关闭开关后画面依旧流畅，调试信息却显示"未开启"）。
        ///   现改为：开启时给滤镜打 label（@memc:），关闭时按 label 精确移除（vf remove @memc）；
        ///   若 label 不存在（旧版本遗留的无 label 滤镜等），回退 vf clr 清空整个 vf 滤镜链
        ///   （本项目 vf 链唯一用途即运动补偿，Anime4K 超分走 glsl-shaders，不受影响）。
        /// </summary>
        public async Task ApplyMotionCompensationAsync(bool enabled, string mode)
        {
            if (_player?.Client.IsInitialized != true)
            {
                return;
            }

            try
            {
                if (!IsMotionCompensationSupported)
                {
                    AppLogger.Warning($"libmpv：当前平台不支持运动补偿（需 x64），已忽略");
                    return;
                }

                if (!enabled)
                {
                    // 关闭：必须真正移除滤镜（移除失败不得静默，否则运动补偿残留）
                    await RemoveMotionCompensationFilterAsync();
                    AppLogger.Info("libmpv：已关闭运动补偿（补帧）");
                    return;
                }

                var scriptPath = GetMotionCompensationScriptPath(mode);
                if (!File.Exists(scriptPath))
                {
                    AppLogger.Warning($"运动补偿 VapourSynth 脚本不存在: {scriptPath}");
                    return;
                }

                // ★ 路径必须用正斜杠：mpv 选项字符串中反斜杠是转义符，
                //   直接传 Windows 路径会被转义破坏导致选项解析失败（错误 -7）。
                //   数组传参（mpv_command_ret）避免 shell 级二次转义。
                //   vf set 会替换整个滤镜链，因此无需先移除旧滤镜；
                //   滤镜带 label（@memc:），保证关闭时能按 label 精确移除。
                var normalizedPath = scriptPath.Replace('\\', '/');
                await _player.Client.ExecuteWithResultAsync(new[] { "vf", "set", $"@memc:vapoursynth=\"{normalizedPath}\"" });
                AppLogger.Info($"libmpv：已应用运动补偿（补帧）（{mode} 档，脚本 {Path.GetFileName(scriptPath)}）");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "应用运动补偿（补帧）滤镜失败");
            }
        }

        /// <summary>
        /// 移除运动补偿（补帧）滤镜（关闭时调用）。
        /// 优先级：按 label（@memc）精确移除 → 回退 vf clr 清空整个 vf 滤镜链。
        /// 回退原因：旧版本添加的滤镜无 label 且带参数，vf remove 无法按"仅名称"匹配；
        /// vf clr 清空链对本项目安全（vf 链唯一用途即运动补偿，Anime4K 超分走 glsl-shaders）。
        /// </summary>
        private async Task RemoveMotionCompensationFilterAsync()
        {
            // 按 label 精确移除（mpv 滤镜 label 语法 @name:，remove 匹配时若任一侧有 label 则只比较 label）
            try
            {
                await _player!.Client.ExecuteWithResultAsync(new[] { "vf", "remove", "@memc" });
                return;
            }
            catch (Exception ex)
            {
                // label 不存在（滤镜从未添加 / 旧版本遗留的无 label 滤镜）时移除失败属预期，继续回退
                AppLogger.Warning($"libmpv：按 label 移除运动补偿滤镜失败（{ex.Message}），回退 vf clr");
            }

            // 回退：清空整个 vf 滤镜链（幂等：链为空时也安全）
            try
            {
                await _player!.Client.ExecuteWithResultAsync(new[] { "vf", "clr" });
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"libmpv：vf clr 清空滤镜链失败: {ex.Message}");
            }
        }

        /// <summary>获取运动补偿 VapourSynth 脚本完整路径（Assets/Mpv/VS 目录）。</summary>
        private static string GetMotionCompensationScriptPath(string mode)
        {
            var fileName = mode switch
            {
                "MVT_STD" => "MEMC_MVT_STD.vpy",
                "SVP_LQ" => "MEMC_SVP_LQ.vpy",
                "SVP_PRO" => "MEMC_SVP_PRO.vpy",
                _ => "MEMC_MVT_LQ.vpy"
            };
            return Path.Combine(AppContext.BaseDirectory, "Assets", "Mpv", "VS", fileName);
        }

        /// <summary>
        /// 读取 mpv 当前播放的调试信息（供播放器"调试信息"悬浮窗轮询显示）。
        /// 关键字段：ContainerFps（原始帧率）与 EstimatedVfFps（vf 滤镜输出帧率估计），
        /// 两者对比即可确认运动补偿（补帧）是否真正生效（如 24fps 源 + 补帧 ≈ 48fps）。
        /// 属性读取失败时相应字段为默认值（0/空），不影响其他字段。
        /// </summary>
        public MpvVideoDebugInfo GetDebugInfo()
        {
            var info = new MpvVideoDebugInfo();
            if (_player?.Client.IsInitialized != true)
            {
                return info;
            }

            info.VideoWidth = GetPropertyLongSafe("video-params/w");
            info.VideoHeight = GetPropertyLongSafe("video-params/h");
            info.DisplayWidth = GetPropertyLongSafe("video-params/dw");
            info.DisplayHeight = GetPropertyLongSafe("video-params/dh");
            info.VideoFormat = GetPropertyStringSafe("video-format");
            info.VideoBitrate = GetPropertyDoubleSafe("video-bitrate");
            info.AudioBitrate = GetPropertyDoubleSafe("audio-bitrate");
            info.ContainerFps = GetPropertyDoubleSafe("container-fps");
            info.EstimatedVfFps = GetPropertyDoubleSafe("estimated-vf-fps");
            // ★ 硬解状态用 hwdec-current（返回实际解码器名，如 d3d11va-copy/no）：
            //   hwdec-active 在 libmpv 场景实测返回 NULL，会导致硬解被误判为软解
            info.HwdecCurrent = GetPropertyStringSafe("hwdec-current");
            // 实时帧率（vo-passes 实际渲染帧率，卡顿掉帧时下降；container/estimated-vf-fps 是静态估计）
            info.RealTimeFps = ReadRealTimeFpsFromVoPasses();
            // 超分状态：设置 + 实际加载的 glsl shaders（含 Anime4K 即生效）
            info.SuperResolutionEnabled = App.SettingsHelper.VideoSuperResolutionEnabled;
            info.SuperResolutionQuality = App.SettingsHelper.VideoSuperResolutionQuality;
            info.SuperResolutionModel = App.SettingsHelper.VideoSuperResolutionModel;
            info.GlslShaders = GetPropertyStringSafe("glsl-shaders");
            // 运动补偿真实状态：读 mpv vf 滤镜链（设置开关只代表意图，此处看实际是否加载滤镜）
            info.MotionCompensationActive = IsVapourSynthFilterActive();
            return info;
        }

        /// <summary>
        /// 从 mpv 的 vo-passes 属性解析实时渲染帧率。
        /// 结构（0.35+）：{ "vo": { "passes": [ { desc,last,avg,max,count,fps,duration,skipped } ] }, "render": {...} }
        /// 取 vo 部分首个含 fps 的 pass（优先 vo，回退 render）；失败返回 -1（上层回退 estimated-vf-fps）。
        /// 读取的 Node 由 mpv 递归分配，解析完成后必须 mpv_free_node_contents 释放。
        /// </summary>
        private double ReadRealTimeFpsFromVoPasses()
        {
            var client = _player?.Client;
            if (client == null)
            {
                return -1;
            }

            MpvNode root = default;
            try
            {
                root = client.GetPropertyToNodeWithFree("vo-passes");
            }
            catch
            {
                return -1; // 属性不可用（如 vo 未初始化）
            }

            try
            {
                if (root.Format != MpvFormat.NodeMap)
                {
                    return -1;
                }

                // 优先 vo 部分，回退 render 部分
                foreach (var section in new[] { "vo", "render" })
                {
                    if (TryGetMapValue(root, section, out var sectionNode) &&
                        sectionNode.Format == MpvFormat.NodeMap &&
                        TryGetMapValue(sectionNode, "passes", out var passesNode) &&
                        passesNode.Format == MpvFormat.NodeArray)
                    {
                        var list = ReadNodeList(passesNode);
                        if (list.Num <= 0 || list._nodesPtr == IntPtr.Zero)
                        {
                            continue;
                        }

                        for (int i = 0; i < list.Num; i++)
                        {
                            var pass = ReadNode(list._nodesPtr, i);
                            if (pass.Format == MpvFormat.NodeMap &&
                                TryGetMapValue(pass, "fps", out var fpsNode) &&
                                fpsNode.Format == MpvFormat.Double &&
                                fpsNode.DoubleValue > 0)
                            {
                                return fpsNode.DoubleValue;
                            }
                        }
                    }
                }

                return -1;
            }
            finally
            {
                client.FreeNodeContents(ref root);
            }
        }

        /// <summary>
        /// 检测当前 mpv vf 滤镜链中是否实际存在 vapoursynth 滤镜（运动补偿真实生效状态）。
        /// 读取 vf 属性（MPV_FORMAT_NODE 数组），逐项检查滤镜 name 是否为 vapoursynth。
        /// 注意：设置开关（VideoMotionCompensationEnabled）只代表"意图"，实际是否生效
        /// 必须看滤镜链——修复前的 bug 正是设置已关闭但滤镜残留、实际仍在补帧，
        /// 此方法让调试信息能暴露"设置与真实状态不一致"（残留时显示异常警示）。
        /// 读取的 Node 由 mpv 递归分配，解析完成后必须 FreeNodeContents 释放。
        /// </summary>
        private bool IsVapourSynthFilterActive()
        {
            var client = _player?.Client;
            if (client == null)
            {
                return false;
            }

            MpvNode root = default;
            try
            {
                root = client.GetPropertyToNodeWithFree("vf");
            }
            catch
            {
                return false; // 属性不可用（如未加载文件）
            }

            try
            {
                if (root.Format != MpvFormat.NodeArray)
                {
                    return false;
                }

                var list = ReadNodeList(root);
                if (list.Num <= 0 || list._nodesPtr == IntPtr.Zero)
                {
                    return false;
                }

                for (int i = 0; i < list.Num; i++)
                {
                    var filter = ReadNode(list._nodesPtr, i);
                    if (filter.Format != MpvFormat.NodeMap ||
                        !TryGetMapValue(filter, "name", out var nameNode) ||
                        nameNode.Format != MpvFormat.String)
                    {
                        continue;
                    }

                    var name = nameNode.StringValue;
                    if (string.Equals(name, "vapoursynth", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                client.FreeNodeContents(ref root);
            }
        }

        // ---- Node 解析辅助（mpv_node：union(8)+format(4)+pad(4)=16；mpv_node_list：num(4)+pad(4)+values(8)+keys(8)=24）----

        private static MpvNodeList ReadNodeList(MpvNode node)
            => System.Runtime.InteropServices.Marshal.PtrToStructure<MpvNodeList>(node._structuredValue);

        private static MpvNode ReadNode(IntPtr nodesPtr, int index)
            => System.Runtime.InteropServices.Marshal.PtrToStructure<MpvNode>(nodesPtr + (index * 16));

        private static bool TryGetMapValue(MpvNode mapNode, string key, out MpvNode value)
        {
            value = default;
            if (mapNode.Format != MpvFormat.NodeMap)
            {
                return false;
            }

            var list = ReadNodeList(mapNode);
            if (list.Num <= 0 || list._nodesPtr == IntPtr.Zero || list._keysPtr == IntPtr.Zero)
            {
                return false;
            }

            for (int i = 0; i < list.Num; i++)
            {
                var keyPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(list._keysPtr, i * IntPtr.Size);
                if (System.Runtime.InteropServices.Marshal.PtrToStringUTF8(keyPtr) == key)
                {
                    value = ReadNode(list._nodesPtr, i);
                    return true;
                }
            }

            return false;
        }

        private long GetPropertyLongSafe(string name)
        {
            try { return _player?.Client.GetPropertyToLong(name) ?? 0; }
            catch { return 0; }
        }

        private double GetPropertyDoubleSafe(string name)
        {
            try { return _player?.Client.GetPropertyToDouble(name) ?? 0; }
            catch { return 0; }
        }

        private string GetPropertyStringSafe(string name)
        {
            try { return _player?.Client.GetPropertyToString(name) ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 诊断：输出当前各事件订阅者数量（页面泄漏排查用）。
        /// 调用方（页面 Unloaded）在退订后调用，正常应为 0；
        /// 若 > 0 说明存在未退订的委托，需据此定位泄漏引用源。
        /// </summary>
        public void LogSubscriptionDiagnostics()
        {
            try
            {
                AppLogger.Debug(
                    $"libmpv订阅诊断: PlaybackStateChanged={PlaybackStateChanged?.GetInvocationList().Length ?? 0} " +
                    $"PositionChanged={PositionChanged?.GetInvocationList().Length ?? 0} " +
                    $"Ended={Ended?.GetInvocationList().Length ?? 0} " +
                    $"LogMessage={LogMessage?.GetInvocationList().Length ?? 0}");
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"libmpv订阅诊断失败: {ex.Message}");
            }
        }

        /// <summary>释放 mpv 内核与渲染上下文。</summary>
        public async Task DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // ★ 修复（页面泄漏双保险）：无条件清空本对象抛出的全部事件。
            //   事件源（本对象）一旦销毁，任何已订阅的外部委托（页面方法等）都
            //   不应再被持有——即使页面侧的 -= 退订因初始化竞态/异常路径遗漏，
            //   此处清空也能断开"事件源 → 订阅者"引用链，保证 VideoPlayerPage
            //   在播放器销毁后可被 GC 回收（实测此前每次进出 mpv 播放器泄漏一个页面实例）。
            //   注意：必须先清空事件再销毁内核，避免 mpv 销毁过程中触发的事件
            //   仍被已失效的订阅者处理（SMTC 已在页面卸载时先行 Dispose 退订，无影响）。
            PlaybackStateChanged = null;
            PositionChanged = null;
            Ended = null;
            LogMessage = null;

            if (_renderSubscribed && _renderControl != null)
            {
                _renderControl.Render -= OnRender;
                _renderControl.Present -= OnPresent;
                _renderControl.Release();
                _renderSubscribed = false;
            }

            if (_player != null)
            {
                try
                {
                    await _player.DisposeAsync();
                }
                catch (Exception ex)
                {
                    AppLogger.Warning($"释放 libmpv 播放器失败: {ex.Message}");
                }
                _player = null;
            }

            _renderControl = null;
        }

        // ── 渲染回调（每帧由独立渲染线程驱动，替代原 CompositionTarget.Rendering） ──

        private void OnRender(TimeSpan e)
        {
            var player = _player;
            var render = _renderControl;
            if (player == null || render == null || !player.Client.IsInitialized)
            {
                return;
            }

            try
            {
                // 清屏并让 mpv 渲染到当前帧缓冲（WGL_NV_DX_interop 桥接的 D3D11 交换链纹理）
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                // ★ 修复（窗口放大白色区域）：渲染尺寸必须与交换链实际尺寸一致。
                //   窗口放大时 ResizeBuffers 可能在 GPU 忙时失败并进入待处理重试，
                //   若此处用面板尺寸（ActualWidth×ScaleX）而交换链仍是旧尺寸，
                //   画面会被裁剪/比例错乱。改用 RenderControl.RenderWidth/Height
                //   （= 交换链当前实际尺寸），ResizeBuffers 成功后下一帧自动铺满。
                player.RenderGL(
                    render.RenderWidth,
                    render.RenderHeight,
                    render.GetBufferHandle());
            }
            catch (Exception ex)
            {
                // 渲染互操作异常（设备丢失等）不应中断渲染循环
                AppLogger.Warning($"libmpv渲染帧失败: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ Present 完成回调（RenderControl.Present 事件）。
        /// 交换链 Present 后通知 mpv 本帧已提交，确保 mpv 正确计算帧间隔与视频时钟。
        /// 在独立渲染线程上调用。
        /// </summary>
        private void OnPresent()
        {
            try
            {
                _player?.ReportSwap();
            }
            catch
            {
                // ReportSwap 失败不影响渲染循环
            }
        }

        // ── mpv 事件 ──

        private void OnPositionChanged(object? sender, PlaybackPositionChangedEventArgs e)
        {
            Position = e.Position;
            Duration = e.Duration;
            PositionChanged?.Invoke(e.Position, e.Duration);
        }

        private void OnStateChanged(object? sender, PlaybackStateChangedEventArgs e)
        {
            State = e.NewState;
            PlaybackStateChanged?.Invoke(this, e.NewState);
        }

        private void OnStopped(object? sender, PlaybackStoppedEventArgs e)
        {
            State = PlaybackState.None;
            Ended?.Invoke();
        }

        private void OnLogMessage(object? sender, LogMessageReceivedEventArgs e)
        {
            // 携带结构化级别，供上层（VideoPlayerPage）按需过滤：只记录 Warn+，V/Info 等细节不落盘
            LogMessage?.Invoke(e.Level, $"{e.Prefix}: {e.Message}");
        }
    }
}
