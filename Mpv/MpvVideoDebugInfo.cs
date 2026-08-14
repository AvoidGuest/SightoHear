namespace SightoHear.Mpv
{
    /// <summary>
    /// mpv 播放调试信息（供播放器"调试信息"悬浮窗轮询显示）。
    /// 由 <see cref="MpvVideoPlayer.GetDebugInfo"/> 从 mpv 属性读取，
    /// 属性不可用时相应字段为默认值（0 / 空字符串）。
    /// </summary>
    public sealed class MpvVideoDebugInfo
    {
        /// <summary>视频编码宽度（video-params/w）。</summary>
        public long VideoWidth { get; set; }

        /// <summary>视频编码高度（video-params/h）。</summary>
        public long VideoHeight { get; set; }

        /// <summary>显示宽度（video-params/dw，含旋转/缩放后的实际显示尺寸）。</summary>
        public long DisplayWidth { get; set; }

        /// <summary>显示高度（video-params/dh）。</summary>
        public long DisplayHeight { get; set; }

        /// <summary>视频编码格式（video-format，如 h264/hevc）。</summary>
        public string VideoFormat { get; set; } = string.Empty;

        /// <summary>视频码率（video-bitrate，单位 kbps）。</summary>
        public double VideoBitrate { get; set; }

        /// <summary>音频码率（audio-bitrate，单位 kbps）。</summary>
        public double AudioBitrate { get; set; }

        /// <summary>容器帧率（container-fps，原始帧率）。</summary>
        public double ContainerFps { get; set; }

        /// <summary>vf 滤镜链输出帧率估计（estimated-vf-fps，补帧后帧率；未启用滤镜时≈原始帧率）。</summary>
        public double EstimatedVfFps { get; set; }

        /// <summary>硬件解码状态（hwdec-current，如 d3d11va-copy/dxva2-copy/no——返回实际解码器名）。</summary>
        /// <remarks>
        /// 不用 hwdec-active（布尔 yes/no）：实测 mpv 0.41 在 libmpv 场景下该属性返回 NULL
        /// （不可用），导致硬解被误判为软解；hwdec-current 返回实际解码器名（no = 软解）。
        /// </remarks>
        public string HwdecCurrent { get; set; } = string.Empty;

        /// <summary>实时帧率（vo-passes 中 vo/render 部分的首个可用 pass 的 fps；-1 = 不可用）。</summary>
        /// <remarks>
        /// container-fps / estimated-vf-fps 是静态估计（源帧率/滤镜输出帧率），卡顿掉帧时不变；
        /// vo-passes 的 fps 是实际渲染帧率（掉帧时下降），用于反映真实播放流畅度。
        /// </remarks>
        public double RealTimeFps { get; set; } = -1;

        /// <summary>超分是否开启（设置 VideoSuperResolutionEnabled）。</summary>
        public bool SuperResolutionEnabled { get; set; }

        /// <summary>超分质量档位（设置 VideoSuperResolutionQuality：Low/Medium/High/Ultra）。</summary>
        public string SuperResolutionQuality { get; set; } = string.Empty;

        /// <summary>超分模型（设置 VideoSuperResolutionModel，如 anime4k）。</summary>
        public string SuperResolutionModel { get; set; } = string.Empty;

        /// <summary>当前已加载的 glsl shaders（分号分隔路径；含 Anime4K shader 即超分生效）。</summary>
        public string GlslShaders { get; set; } = string.Empty;

        /// <summary>运动补偿是否真实生效（mpv vf 滤镜链中实际存在 vapoursynth 滤镜）。</summary>
        /// <remarks>
        /// 与设置开关（VideoMotionCompensationEnabled，只代表意图）不同，此字段读取
        /// mpv 实际滤镜链状态，能反映"设置已关闭但滤镜残留"这类意图与真实不一致的场景
        /// （修复前关闭开关后运动补偿仍生效的 bug，即由此暴露）。
        /// </remarks>
        public bool MotionCompensationActive { get; set; }
    }
}
