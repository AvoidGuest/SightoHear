using FFmpegInteropX;
using SightoHear.Helpers;
using SightoHear.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SightoHear.Services
{
    public sealed class MusicPlaybackService
    {
        private readonly Random _random = new();

        public MediaPlayer Player { get; } = new()
        {
            AutoPlay = true
        };

        public IReadOnlyList<MediaItem> PlayQueue => _playQueue;
        public IReadOnlyList<MediaItem> ExternalPlayQueue => _externalPlayQueue;
        public int ExternalCurrentIndex => _externalCurrentIndex;
        public MediaItem? CurrentItem { get; private set; }
        public int CurrentIndex { get; private set; } = -1;
        public int PlayMode { get; private set; }

        public MediaPlayer? ExternalPlayer { get; private set; }
        public MediaItem? ExternalItem { get; private set; }
        public MediaPlayer ActivePlayer => ExternalPlayer ?? Player;
        public MediaItem? ActiveItem => ExternalItem ?? CurrentItem;
        public bool HasExternalPlayback => ExternalPlayer != null;

        public bool CanAccessPlaybackSession
        {
            get
            {
                try
                {
                    _ = ActivePlayer.PlaybackSession;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool IsMuted => ActivePlayer.IsMuted;
        public double VolumePercent => Math.Clamp(ActivePlayer.Volume * 100.0, 0, 100);
        public TimeSpan Position => CanAccessPlaybackSession
            ? ActivePlayer.PlaybackSession.Position
            : TimeSpan.Zero;
        public TimeSpan Duration => CanAccessPlaybackSession
            ? ActivePlayer.PlaybackSession.NaturalDuration
            : TimeSpan.Zero;
        public MediaPlaybackState PlaybackState => CanAccessPlaybackSession
            ? ActivePlayer.PlaybackSession.PlaybackState
            : MediaPlaybackState.None;

        public event EventHandler? CurrentItemChanged;
        public event EventHandler? QueueChanged;
        public event EventHandler? PlaybackStateChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? PlayModeChanged;
        public event EventHandler<string>? PlaybackFailed;
        public event EventHandler? ExternalPlaybackChanged;

        private List<MediaItem> _playQueue = new();
        private List<MediaItem>? _playQueueOriginal;
        private List<MediaItem> _externalPlayQueue = new();
        private List<MediaItem>? _externalPlayQueueOriginal;
        private int _externalCurrentIndex = -1;
        private FFmpegMediaSource? _ffmpegMediaSource;
        private IRandomAccessStream? _ffmpegStream;
        private bool _isUsingFfmpeg;
        private bool _isRetryingWithFfmpeg;
        private FFmpegMediaSource? _externalFfmpegMediaSource;
        private IRandomAccessStream? _externalFfmpegStream;
        private readonly SystemMediaTransportControls _smtc;

        public MusicPlaybackService()
        {
            // 初始化系统媒体传输控件 (SMTC)，实现 Windows 任务栏媒体预览。
            // 重要：不要设置 CommandManager.IsEnabled = false，必须保持 MediaPlayer 默认的
            // 自动 SMTC 集成。禁用自动集成后，系统媒体会话不会自动上报播放状态/时间线/
            // 媒体属性，外部软件（如歌词软件 BetterLyrics，通过
            // GlobalSystemMediaTransportControlsSessionManager 监听）将无法读取播放信息。
            _smtc = Player.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsStopEnabled = true;
            // 通过 CommandManager 事件自定义系统媒体按钮行为（自动集成模式下推荐方式），
            // 并强制启用上一曲/下一曲按钮（单个媒体项时系统默认禁用）。
            ConfigureMediaCommands(Player);

            Player.MediaEnded += Player_MediaEnded;
            Player.MediaFailed += Player_MediaFailed;
            if (Player.PlaybackSession != null)
                Player.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
            ApplySettings();
            // 注意：MusicPlaybackService 作为 App 的静态属性，在 App 类型初始化时（早于
            // OnLaunched）即被创建，此时 SettingsHelper.Load() 尚未执行，构造函数中读取
            // 的 MusicOutputDeviceId 永远是默认空值，即使在此调用 ApplyAudioDeviceAsync
            // 也只会应用到"跟随系统设备"，无法恢复用户保存的特定设备。
            // 已保存输出设备的真正恢复由 App.OnLaunched 在设置加载完成后统一调用
            // ApplyAudioDeviceAsync 完成，构造函数内不再重复调用。
        }

        public void ApplySettings()
        {
            Player.Volume = Math.Clamp(App.SettingsHelper.MusicVolume, 0, 1);
            Player.IsMuted = App.SettingsHelper.MusicMuted;
            VolumeChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 将音乐播放器单独输出到指定的音频渲染设备。
        /// 传入空字符串或 null 时跟随系统默认输出设备（置空 AudioDevice）。
        /// 遵循 Windows API 规范：通过 DeviceInformation.CreateFromIdAsync 创建设备对象，
        /// 再赋值给 MediaPlayer.AudioDevice，实现软件级单独输出（不影响其他应用）。
        /// </summary>
        /// <param name="deviceId">音频渲染设备的设备 ID，空字符串表示跟随系统默认设备。</param>
        public async Task ApplyAudioDeviceAsync(string? deviceId)
        {
            try
            {
                // 记录当前播放状态，切换设备后恢复，避免切换瞬间产生静音或卡顿。
                bool wasPlaying = Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
                TimeSpan? keepPosition = null;
                if (wasPlaying && Player.PlaybackSession.CanSeek)
                    keepPosition = Player.PlaybackSession.Position;

                DeviceInformation? device = null;
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    device = await DeviceInformation.CreateFromIdAsync(deviceId);
                    if (device == null)
                    {
                        AppLogger.Warning($"输出设备 ID 无效，回退到系统默认设备: {deviceId}");
                        deviceId = string.Empty;
                    }
                }

                // 设备切换需要在媒体管线空闲时进行，先暂停再切换，完成后恢复播放状态。
                if (wasPlaying)
                    Player.Pause();
                Player.AudioDevice = device;
                if (wasPlaying)
                {
                    if (keepPosition.HasValue && Player.PlaybackSession.CanSeek)
                        Player.PlaybackSession.Position = keepPosition.Value;
                    Player.Play();
                }

                if (string.IsNullOrEmpty(deviceId))
                    AppLogger.Info("音乐播放器输出设备：跟随系统默认设备");
                else
                    AppLogger.Info($"音乐播放器输出设备：{device?.Name ?? deviceId}");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"应用音频输出设备失败: {deviceId}");
            }
        }

        /// <summary>
        /// 配置 MediaPlayer 的系统媒体命令（CommandManager）事件处理。
        /// 保持 SMTC 自动集成（播放状态/时间线/媒体属性自动上报给系统）的同时，
        /// 自定义上一曲/下一曲/播放/暂停等按钮行为。
        /// </summary>
        private void ConfigureMediaCommands(MediaPlayer player)
        {
            var commandManager = player.CommandManager;
            // 单个媒体项（非 MediaPlaybackList）时，系统默认禁用上一曲/下一曲按钮，
            // 强制启用，便于通过系统媒体控件/键盘媒体键切换歌曲。
            commandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;

            commandManager.PlayReceived += CommandManager_PlayReceived;
            commandManager.PauseReceived += CommandManager_PauseReceived;
            commandManager.NextReceived += CommandManager_NextReceived;
            commandManager.PreviousReceived += CommandManager_PreviousReceived;

            // 系统媒体命令管理器没有"停止"事件，通过 ButtonPressed 事件单独处理停止按钮。
            // 注意：仅响应 Stop，播放/暂停/上一曲/下一曲均由上面的 CommandManager 事件处理，
            // 避免同一命令被重复执行。
            player.SystemMediaTransportControls.ButtonPressed += (s, e) => Smtc_StopButtonPressed(player, e);
        }

        /// <summary>
        /// 仅处理系统媒体控件的"停止"按钮（等价于暂停，与之前行为一致）。
        /// 该事件在后台线程触发，需回到 UI 线程执行操作。
        /// </summary>
        private static void Smtc_StopButtonPressed(MediaPlayer player, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            if (args.Button != SystemMediaTransportControlsButton.Stop) return;
            App.MainWindow?.DispatcherQueue.TryEnqueue(() => player.Pause());
        }

        /// <summary>
        /// 处理系统媒体控件的播放命令（该事件在后台线程触发，需回到 UI 线程执行操作）。
        /// </summary>
        private void CommandManager_PlayReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerPlayReceivedEventArgs args)
        {
            args.Handled = true;
            var player = sender.MediaPlayer;
            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                if (player.Source != null)
                    player.Play();
            });
        }

        /// <summary>
        /// 处理系统媒体控件的暂停命令。
        /// </summary>
        private void CommandManager_PauseReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerPauseReceivedEventArgs args)
        {
            args.Handled = true;
            var player = sender.MediaPlayer;
            App.MainWindow?.DispatcherQueue.TryEnqueue(() => player.Pause());
        }

        /// <summary>
        /// 处理系统媒体控件的下一曲命令。
        /// </summary>
        private void CommandManager_NextReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerNextReceivedEventArgs args)
        {
            args.Handled = true;
            var player = sender.MediaPlayer;
            App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (player == ExternalPlayer)
                        await PlayExternalAdjacentAsync(1);
                    else
                        await PlayAdjacentAsync(1);
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "SMTC 下一曲命令处理失败");
                }
            });
        }

        /// <summary>
        /// 处理系统媒体控件的上一曲命令。
        /// </summary>
        private void CommandManager_PreviousReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerPreviousReceivedEventArgs args)
        {
            args.Handled = true;
            var player = sender.MediaPlayer;
            App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (player == ExternalPlayer)
                        await PlayExternalAdjacentAsync(-1);
                    else
                        await PlayAdjacentAsync(-1);
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "SMTC 上一曲命令处理失败");
                }
            });
        }

        /// <summary>
        /// 为指定的 MediaPlayer 配置系统媒体传输控件 (SMTC) 按钮能力。
        /// </summary>
        private static void ConfigureSmtcButtons(SystemMediaTransportControls smtc)
        {
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsPreviousEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsStopEnabled = true;
        }

        /// <summary>
        /// 为 MediaPlaybackItem 应用系统媒体传输控件 (SMTC) 的显示属性（标题、艺术家、专辑、封面等）。
        /// 这是自动 SMTC 集成模式下官方推荐的方式：
        /// 元数据随媒体项一起上报给系统，外部软件通过
        /// GlobalSystemMediaTransportControlsSession.TryGetMediaPropertiesAsync() 即可读取。
        /// 注意：不要改用 smtc.DisplayUpdater 手动设置——那会被 MediaPlayer 在加载 Source 时覆盖。
        /// </summary>
        private static async Task ApplyItemDisplayPropertiesAsync(MediaPlaybackItem playbackItem, MediaItem item)
        {
            try
            {
                var props = playbackItem.GetDisplayProperties();

                if (string.Equals(item.MediaType, "Video", StringComparison.OrdinalIgnoreCase))
                {
                    props.Type = MediaPlaybackType.Video;
                    props.VideoProperties.Title = string.IsNullOrEmpty(item.Title) ? item.FileName : item.Title;
                }
                else
                {
                    // 默认当作音乐处理，确保 SMTC 类型有效
                    props.Type = MediaPlaybackType.Music;
                    props.MusicProperties.Title = string.IsNullOrEmpty(item.Title) ? item.FileName : item.Title;
                    props.MusicProperties.Artist = item.Artist;
                    props.MusicProperties.AlbumTitle = item.Album;
                    props.MusicProperties.AlbumArtist = item.Artist;
                    props.MusicProperties.TrackNumber = item.TrackNumber;
                }

                // 设置封面缩略图
                if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.ThumbnailPath);
                    var stream = await file.OpenReadAsync();
                    props.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                }

                playbackItem.ApplyDisplayProperties(props);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"应用 SMTC 显示属性失败: {item.FileName}");
            }
            // 确保 async 方法始终包含 await，避免编译器警告 CS1998。
            await Task.CompletedTask;
        }

        public async Task PlayAsync(MediaItem item, IEnumerable<MediaItem>? queue = null)
        {
            try
            {
                // 先暂停内部播放器，防止与外部队列出现重叠
                if (Player.Source != null)
                {
                    Player.Pause();
                    Player.Source = null;
                }

                ClearExternalPlayback();

                var nextQueue = (queue ?? _playQueue).ToList();
                if (nextQueue.Count == 0 ||
                    !nextQueue.Any(candidate => SameFile(candidate, item)))
                {
                    nextQueue = new List<MediaItem> { item };
                }

                bool isExplicitQueueChange = queue != null && !ReferenceEquals(queue, _playQueue);

                _playQueue = nextQueue;
                CurrentIndex = _playQueue.FindIndex(candidate => SameFile(candidate, item));
                if (CurrentIndex < 0)
                    CurrentIndex = 0;

                // 仅在显式传入新队列、或尚未保存过原始顺序、或队列长度变化时，更新原始顺序快照
                if (isExplicitQueueChange || _playQueueOriginal == null || _playQueueOriginal.Count != _playQueue.Count)
                {
                    _playQueueOriginal = _playQueue.ToList();
                }

                CurrentItem = item;
                // 媒体元数据（标题/艺术家/封面等）已通过 MediaPlaybackItem 的显示属性
                // 在 SetPlayerSourceAsync/SetFfmpegSourceAsync 中随 Source 一起上报给系统 SMTC。
                // ★ 修复（主页"上次打开"记录丢失）：超分模式下视频播放器退出时会把视频
                //   转交给本服务继续播放（TransferVideoToMusicAsync → PlayAsync）。
                //   此前无条件把 LastMusicPath/LastMusicTime 写成该路径，导致主页
                //   GetMostRecentOpened 把它当作"最近打开的音乐"从音乐库查找，找不到
                //   视频 → 显示"暂无最近打开记录"。这里按媒体类型分类：
                //   视频 → 记录到"上次打开的视频"；音乐 → 记录到"上次打开的音乐"。
                if (string.Equals(item.MediaType, "Video", StringComparison.OrdinalIgnoreCase))
                {
                    App.SettingsHelper.LastVideoPath = item.FilePath;
                    App.SettingsHelper.LastVideoTime = DateTime.Now;
                }
                else
                {
                    App.SettingsHelper.LastMusicPath = item.FilePath;
                    App.SettingsHelper.LastMusicTime = DateTime.Now;
                }
                App.SettingsHelper.Save();
                await SetPlayerSourceAsync(item, ShouldPreferFfmpeg(item.FilePath));
                CurrentItemChanged?.Invoke(this, EventArgs.Empty);
                QueueChanged?.Invoke(this, EventArgs.Empty);
                Player.Play();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Play music failed: {item.FilePath}");
                PlaybackFailed?.Invoke(this, "无法播放此音乐文件");
            }
        }

        public async Task PlayAdjacentAsync(int offset)
        {
            if (HasExternalPlayback)
            {
                await PlayExternalAdjacentAsync(offset);
                return;
            }

            if (_playQueue.Count == 0)
                return;

            int nextIndex = CurrentIndex;
            if (PlayMode == 2 && _playQueue.Count > 1)
            {
                do
                {
                    nextIndex = _random.Next(_playQueue.Count);
                }
                while (nextIndex == CurrentIndex);
            }
            else if (nextIndex < 0)
            {
                nextIndex = 0;
            }
            else
            {
                nextIndex = (nextIndex + offset + _playQueue.Count) % _playQueue.Count;
            }

            await PlayAsync(_playQueue[nextIndex], _playQueue);
        }

        private async Task PlayExternalAdjacentAsync(int offset)
        {
            if (_externalPlayQueue.Count == 0)
            {
                AppLogger.Warning("[外部播放] PlayExternalAdjacentAsync 调用时播放队列为空");
                return;
            }

            int nextIndex = _externalCurrentIndex;
            if (PlayMode == 2 && _externalPlayQueue.Count > 1)
            {
                do
                {
                    nextIndex = _random.Next(_externalPlayQueue.Count);
                }
                while (nextIndex == _externalCurrentIndex);
            }
            else
            {
                nextIndex = (nextIndex + offset + _externalPlayQueue.Count) % _externalPlayQueue.Count;
            }

            var nextItem = _externalPlayQueue[nextIndex];
            var oldPlayer = ExternalPlayer;

            AppLogger.Info($"[外部播放] 切换外部播放: 从={ExternalItem?.FileName}({_externalCurrentIndex}) 到={nextItem.FileName}({nextIndex}), 偏移={offset}");

            if (oldPlayer != null)
            {
                oldPlayer.MediaEnded -= OnExternalMediaEnded;
                oldPlayer.MediaFailed -= OnExternalMediaFailed;
                oldPlayer.PlaybackSession.PlaybackStateChanged -= OnExternalPlaybackStateChanged;
                oldPlayer.Pause();
                try { oldPlayer.Source = null; } catch { /* MediaPlayer 可能已在播放新内容 */ }
                oldPlayer.Dispose();
            }

            ExternalPlayer = null;
            ExternalItem = null;

            var newPlayer = new MediaPlayer { AutoPlay = true };
            // 配置新播放器的系统媒体传输控件 (SMTC)：启用按钮 + CommandManager 事件
            ConfigureSmtcButtons(newPlayer.SystemMediaTransportControls);
            ConfigureMediaCommands(newPlayer);

            newPlayer.MediaEnded += OnExternalMediaEnded;
            newPlayer.MediaFailed += OnExternalMediaFailed;
            newPlayer.PlaybackSession.PlaybackStateChanged += OnExternalPlaybackStateChanged;
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(nextItem.FilePath);
                // 通过 MediaPlaybackItem 的显示属性上报媒体元数据（自动 SMTC 集成模式）
                var playbackItem = new MediaPlaybackItem(MediaSource.CreateFromStorageFile(file));
                await ApplyItemDisplayPropertiesAsync(playbackItem, nextItem);
                newPlayer.Source = playbackItem;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"[外部播放] 创建 MediaSource 失败: {nextItem.FilePath}");
                newPlayer.Dispose();
                ExternalPlaybackChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            ExternalPlayer = newPlayer;
            ExternalItem = nextItem;
            _externalCurrentIndex = nextIndex;

            AppLogger.Info($"[外部播放] 外部播放器已切换: 文件={nextItem.FileName}, 索引={nextIndex}");

            ExternalPlaybackChanged?.Invoke(this, EventArgs.Empty);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            CurrentItemChanged?.Invoke(this, EventArgs.Empty);
        }

        public void TogglePlayPause()
        {
            if (PlaybackState == MediaPlaybackState.Playing)
                ActivePlayer.Pause();
            else if (ActivePlayer.Source != null)
                ActivePlayer.Play();
        }

        public void ToggleMute()
        {
            SetMuted(!ActivePlayer.IsMuted);
        }

        public void SetMuted(bool muted)
        {
            ActivePlayer.IsMuted = muted;
            App.SettingsHelper.MusicMuted = muted;
            App.SettingsHelper.Save();
            VolumeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetVolumePercent(double value)
        {
            double percent = Math.Clamp(value, 0, 100);
            ActivePlayer.Volume = percent / 100.0;
            if (percent > 0 && ActivePlayer.IsMuted)
            {
                ActivePlayer.IsMuted = false;
                App.SettingsHelper.MusicMuted = false;
            }

            App.SettingsHelper.MusicVolume = ActivePlayer.Volume;
            App.SettingsHelper.Save();
            VolumeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetPosition(TimeSpan position)
        {
            TimeSpan duration = Duration;
            if (duration.TotalSeconds <= 0)
                return;

            ActivePlayer.PlaybackSession.Position = TimeSpan.FromSeconds(
                Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds));
        }

        public void CyclePlayMode()
        {
            int oldMode = PlayMode;
            PlayMode = (PlayMode + 1) % 3;

            if (PlayMode != oldMode)
            {
                if (!HasExternalPlayback)
                {
                    RearrangeInternalQueue();
                }
                else
                {
                    RearrangeExternalQueue();
                }

                QueueChanged?.Invoke(this, EventArgs.Empty);
            }

            PlayModeChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RearrangeInternalQueue()
        {
            if (_playQueue.Count == 0 || CurrentIndex < 0) return;

            if (PlayMode == 2)
            {
                // 切换到随机播放：若尚未保存原始顺序，则先保存一份快照
                if (_playQueueOriginal == null || _playQueueOriginal.Count != _playQueue.Count)
                {
                    _playQueueOriginal = _playQueue.ToList();
                }

                // 仅将当前播放位置之后的队列随机打乱，当前歌曲保持在原位
                ShuffleAfterIndex(_playQueue, CurrentIndex);
            }
            else if (PlayMode == 0)
            {
                // 切换到顺序播放：若存在原始顺序快照且长度匹配，则恢复原始顺序
                if (_playQueueOriginal != null && _playQueueOriginal.Count == _playQueue.Count)
                {
                    var currentItem = _playQueue[CurrentIndex];
                    int originalIndex = _playQueueOriginal.FindIndex(m => m.Id == currentItem.Id);

                    if (originalIndex >= 0)
                    {
                        var newQueue = new List<MediaItem>(_playQueue.Count);
                        for (int i = 0; i < originalIndex; i++)
                        {
                            newQueue.Add(_playQueueOriginal[i]);
                        }
                        newQueue.Add(currentItem);
                        for (int i = originalIndex + 1; i < _playQueueOriginal.Count; i++)
                        {
                            newQueue.Add(_playQueueOriginal[i]);
                        }

                        _playQueue = newQueue;
                        CurrentIndex = originalIndex;
                    }
                }
            }
        }

        private void RearrangeExternalQueue()
        {
            if (_externalPlayQueue.Count == 0 || _externalCurrentIndex < 0) return;

            if (PlayMode == 2)
            {
                if (_externalPlayQueueOriginal == null || _externalPlayQueueOriginal.Count != _externalPlayQueue.Count)
                {
                    _externalPlayQueueOriginal = _externalPlayQueue.ToList();
                }
                ShuffleAfterIndex(_externalPlayQueue, _externalCurrentIndex);
            }
            else if (PlayMode == 0)
            {
                if (_externalPlayQueueOriginal != null && _externalPlayQueueOriginal.Count == _externalPlayQueue.Count)
                {
                    var currentItem = _externalPlayQueue[_externalCurrentIndex];
                    int originalIndex = _externalPlayQueueOriginal.FindIndex(m => m.Id == currentItem.Id);

                    if (originalIndex >= 0)
                    {
                        var newQueue = new List<MediaItem>(_externalPlayQueue.Count);
                        for (int i = 0; i < originalIndex; i++)
                        {
                            newQueue.Add(_externalPlayQueueOriginal[i]);
                        }
                        newQueue.Add(currentItem);
                        for (int i = originalIndex + 1; i < _externalPlayQueueOriginal.Count; i++)
                        {
                            newQueue.Add(_externalPlayQueueOriginal[i]);
                        }

                        _externalPlayQueue = newQueue;
                        _externalCurrentIndex = originalIndex;
                    }
                }
            }
        }

        /// <summary>
        /// 仅将当前播放位置之后的队列项随机打乱，当前歌曲保持在原位不变。
        /// </summary>
        private void ShuffleAfterIndex(List<MediaItem> queue, int currentIndex)
        {
            if (queue.Count <= 1 || currentIndex < 0) return;

            int start = currentIndex + 1;
            if (start >= queue.Count) return;

            for (int i = queue.Count - 1; i > start; i--)
            {
                int j = _random.Next(start, i + 1);
                (queue[i], queue[j]) = (queue[j], queue[i]);
            }
        }

        public void RegisterExternalPlayback(MediaPlayer player, MediaItem item,
            List<MediaItem>? playlist = null, int playlistIndex = -1)
        {
            ClearExternalPlayback();

            ExternalPlayer = player;
            ExternalItem = item;
            _externalPlayQueue = playlist?.ToList() ?? new List<MediaItem> { item };
            _externalPlayQueueOriginal = _externalPlayQueue.ToList();
            _externalCurrentIndex = playlistIndex >= 0 ? playlistIndex : 0;
            // 启用外部播放器的系统媒体传输控件 (SMTC) 按钮。
            // 注意：外部播放器的 CommandManager 事件已由创建方（VideoPlayerPage）订阅，
            // 媒体元数据已通过 MediaPlaybackItem 的显示属性上报，这里无需重复处理。

            player.MediaEnded += OnExternalMediaEnded;
            player.MediaFailed += OnExternalMediaFailed;
            player.PlaybackSession.PlaybackStateChanged += OnExternalPlaybackStateChanged;

            AppLogger.Info($"[外部播放] 注册外部播放器: 文件={item.FileName}, 路径={item.FilePath}, 播放列表大小={_externalPlayQueue.Count}, 索引={_externalCurrentIndex}");

            ExternalPlaybackChanged?.Invoke(this, EventArgs.Empty);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            CurrentItemChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RegisterExternalFfmpegResources(
            FFmpegMediaSource? ffmpegSource, IRandomAccessStream? ffmpegStream)
        {
            _externalFfmpegMediaSource = ffmpegSource;
            _externalFfmpegStream = ffmpegStream;
            AppLogger.Debug("[外部播放] 注册外部 FFmpeg 资源");
        }

        public void ClearExternalPlayback()
        {
            if (ExternalPlayer == null) return;

            var oldPlayer = ExternalPlayer;
            var oldItem = ExternalItem;
            oldPlayer.MediaEnded -= OnExternalMediaEnded;
            oldPlayer.MediaFailed -= OnExternalMediaFailed;
            oldPlayer.PlaybackSession.PlaybackStateChanged -= OnExternalPlaybackStateChanged;

            ExternalPlayer = null;
            ExternalItem = null;
            _externalPlayQueue.Clear();
            _externalPlayQueueOriginal = null;
            _externalCurrentIndex = -1;
            // ★ 修复：外部 FFmpeg 流必须显式释放（与 ClearFfmpegResources 一致），
            //   否则视频→音乐反复切换会累积文件句柄和内存泄漏。
            try { _externalFfmpegMediaSource?.Dispose(); } catch { }
            _externalFfmpegMediaSource = null;
            try { _externalFfmpegStream?.Dispose(); } catch { }
            _externalFfmpegStream = null;

            AppLogger.Info($"[外部播放] 清除外部播放器: 文件={oldItem?.FileName}, 路径={oldItem?.FilePath}");

            ExternalPlaybackChanged?.Invoke(this, EventArgs.Empty);

            try
            {
                oldPlayer.Pause();
                oldPlayer.Source = null;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "清理外部播放器时出错");
            }

            try
            {
                oldPlayer.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "释放外部播放器时出错");
            }
        }

        public MediaPlayer? DetachExternalPlayback()
        {
            var player = ExternalPlayer;
            if (player == null) return null;

            var oldItem = ExternalItem;
            player.MediaEnded -= OnExternalMediaEnded;
            player.MediaFailed -= OnExternalMediaFailed;
            player.PlaybackSession.PlaybackStateChanged -= OnExternalPlaybackStateChanged;

            ExternalPlayer = null;
            ExternalItem = null;

            AppLogger.Info($"[外部播放] 分离外部播放器: 文件={oldItem?.FileName}, 路径={oldItem?.FilePath}");

            ExternalPlaybackChanged?.Invoke(this, EventArgs.Empty);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            CurrentItemChanged?.Invoke(this, EventArgs.Empty);

            return player;
        }

        /// <summary>
        /// 停止内部音乐播放，清除当前曲目。外部视频播放不受影响。
        /// </summary>
        public void StopPlayback()
        {
            Player.Pause();
            try { Player.Source = null; } catch { /* MediaPlayer 可能已在播放新内容 */ }
            CurrentItem = null;
            _playQueue.Clear();
            _playQueueOriginal = null;
            CurrentIndex = -1;
            ClearFfmpegResources();
            CurrentItemChanged?.Invoke(this, EventArgs.Empty);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnExternalMediaEnded(MediaPlayer sender, object args)
        {
            if (ExternalPlayer == null || sender != ExternalPlayer)
                return;

            try
            {
                double duration = sender.PlaybackSession.NaturalDuration.TotalSeconds;
                double position = sender.PlaybackSession.Position.TotalSeconds;
                AppLogger.Info($"[外部播放] 媒体结束事件: 文件={ExternalItem?.FileName}, 位置={position:F1}s, 时长={duration:F1}s");
                if (duration > 0 && position < duration - 0.5)
                {
                    AppLogger.Warning($"[外部播放] 媒体在未播放完成时触发结束事件，可能是进度异常: 位置={position:F1}s, 时长={duration:F1}s");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "[外部播放] 检查媒体结束位置时出错");
                return;
            }

            if (PlayMode == 1 || _externalPlayQueue.Count > 1)
            {
                AppLogger.Info($"[外部播放] 准备播放下一项: 播放模式={PlayMode}, 队列大小={_externalPlayQueue.Count}");
                _ = PlayExternalAdjacentAsync(PlayMode == 1 ? 0 : 1);
            }
            else
            {
                AppLogger.Info("[外部播放] 媒体自然结束，队列已耗尽");
                PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnExternalMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            AppLogger.Info($"[外部播放] 媒体播放失败: 文件={ExternalItem?.FileName}, 错误={args.ErrorMessage}, 错误码={args.Error}");
            ClearExternalPlayback();
            PlaybackFailed?.Invoke(this, "视频播放失败");
        }

        private void OnExternalPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            try
            {
                var state = sender.PlaybackState;
                var position = sender.Position;
                var duration = sender.NaturalDuration;
                AppLogger.Debug($"[外部播放] 状态变更: 文件={ExternalItem?.FileName}, 状态={state}, 位置={position.TotalSeconds:F1}s, 时长={duration.TotalSeconds:F1}s");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "[外部播放] 记录状态变更时出错");
            }

            // 播放状态已由 MediaPlayer 自动上报给系统 SMTC，这里仅触发业务事件。
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private async Task SetPlayerSourceAsync(MediaItem item, bool preferFfmpeg)
        {
            ClearFfmpegResources();
            _isUsingFfmpeg = false;
            _isRetryingWithFfmpeg = false;

            StorageFile file = await StorageFile.GetFileFromPathAsync(item.FilePath);
            if (preferFfmpeg)
            {
                await SetFfmpegSourceAsync(file, item);
                return;
            }

            // 通过 MediaPlaybackItem 的显示属性上报媒体元数据（自动 SMTC 集成模式）
            var playbackItem = new MediaPlaybackItem(MediaSource.CreateFromStorageFile(file));
            await ApplyItemDisplayPropertiesAsync(playbackItem, item);
            Player.Source = playbackItem;
        }

        private async Task RetryWithFfmpegAsync(MediaItem item)
        {
            try
            {
                _isRetryingWithFfmpeg = true;
                StorageFile file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                await SetFfmpegSourceAsync(file, item);
                Player.Play();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"FFmpeg music decode failed: {item.FilePath}");
                PlaybackFailed?.Invoke(this, "播放失败");
            }
            finally
            {
                _isRetryingWithFfmpeg = false;
            }
        }

        private async Task SetFfmpegSourceAsync(StorageFile file, MediaItem item)
        {
            ClearFfmpegResources();

            IRandomAccessStream? pendingStream = null;
            try
            {
                pendingStream = await file.OpenReadAsync();
                FFmpegMediaSource ffmpegSource = await FFmpegMediaSource.CreateFromStreamAsync(
                    pendingStream,
                    new MediaSourceConfig());

                _ffmpegMediaSource = ffmpegSource;
                _ffmpegStream = pendingStream;
                pendingStream = null;
                _isUsingFfmpeg = true;
                // 通过 MediaPlaybackItem 的显示属性上报媒体元数据（自动 SMTC 集成模式）
                var playbackItem = ffmpegSource.CreateMediaPlaybackItem();
                await ApplyItemDisplayPropertiesAsync(playbackItem, item);
                Player.Source = playbackItem;
            }
            finally
            {
                pendingStream?.Dispose();
            }
        }

        private void ClearFfmpegResources()
        {
            _ffmpegMediaSource = null;
            _ffmpegStream?.Dispose();
            _ffmpegStream = null;
        }

        private static bool ShouldPreferFfmpeg(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension is ".flac" or ".ogg" or ".opus";
        }

        private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            // 播放状态已由 MediaPlayer 自动上报给系统 SMTC，这里仅触发业务事件。
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Player_MediaEnded(MediaPlayer sender, object args)
        {
            _ = PlayMode == 1 && CurrentItem != null
                ? PlayAsync(CurrentItem, _playQueue)
                : PlayAdjacentAsync(1);
        }

        private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            if (!_isUsingFfmpeg && !_isRetryingWithFfmpeg && CurrentItem != null)
            {
                _ = RetryWithFfmpegAsync(CurrentItem);
                return;
            }

            PlaybackFailed?.Invoke(this, "播放失败");
        }

        private static bool SameFile(MediaItem left, MediaItem right)
        {
            return !string.IsNullOrWhiteSpace(left.FilePath) &&
                   !string.IsNullOrWhiteSpace(right.FilePath) &&
                   Path.GetFullPath(left.FilePath).Equals(
                       Path.GetFullPath(right.FilePath),
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
