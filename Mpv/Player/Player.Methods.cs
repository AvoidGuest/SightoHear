// Copyright (c) Bili Copilot. All rights reserved.

using SightoHear.Mpv.Args;
using SightoHear.Mpv.Enums.Client;
using SightoHear.Mpv.Enums.Player;
using SightoHear.Mpv.Structs.Client;
using System.Runtime.InteropServices;

namespace SightoHear.Mpv;

public sealed partial class Player
{
    private void EventLoop()
    {
        while (!_isDisposed)
        {
            var clientEvent = Client.WaitEvent();
            switch (clientEvent.EventId)
            {
                case MpvEventId.Shutdown:
                    _ = DisposeAsync();
                    Destroyed?.Invoke(this, EventArgs.Empty);
                    return;
                case MpvEventId.LogMessage:
                    {
                        if (!IsLoggingEnabled)
                        {
                            break;
                        }

                        var logMessage = clientEvent.GetData<MpvEventLogMessage>();
                        var args = new LogMessageReceivedEventArgs(logMessage.Prefix, logMessage.Text, logMessage.Level.ToLogLevel());
                        TranslateLogMessage(logMessage);
                        LogMessageReceived?.Invoke(this, args);
                    }
                    break;
                case MpvEventId.StartFile:
                    {
                        // var startFileData = clientEvent.GetData<MpvEventStartFile>();
                        ChangeState(PlaybackState.Opening);
                    }
                    break;
                case MpvEventId.FileLoaded:
                    _isLoaded = true;
                    ChangeState(PlaybackState.Decoding);
                    break;
                case MpvEventId.EndFile:
                    {
                        _isLoaded = false;
                        var endFileData = clientEvent.GetData<MpvEventEndFile>();
                        if (endFileData.Reason == MpvEndFileReason.Error)
                        {
                            ChangeState(PlaybackState.Failed);
                        }
                        else
                        {
                            ChangeState(PlaybackState.None);
                            RaiseEnd(endFileData);
                        }
                    }
                    break;
                case MpvEventId.PlaybackRestart:
                    {
                        ChangeState(PlaybackState.Playing);
                    }
                    break;
                case MpvEventId.PropertyChange:
                    {
                        var propData = clientEvent.GetData<MpvEventProperty>();
                        TranslateProperty(propData);
                    }
                    break;
            }
        }
    }

    private void TranslateLogMessage(MpvEventLogMessage logMessage)
    {
        if (logMessage.Prefix == "cplayer")
        {
            var text = logMessage.Text.Trim();
            if (text.StartsWith("built on"))
            {
                var t = DateTimeOffset.Parse(text.Replace("built on", string.Empty).Trim());
                Dependencies.BuildTime = t;
            }
            else if (text.StartsWith("FFmpeg version"))
            {
                Dependencies.FFmpegVersion = text.Replace("FFmpeg version:", string.Empty).Trim();
            }
            else if (text.StartsWith("libplacebo version"))
            {
                Dependencies.LibplaceboVersion = text.Replace("libplacebo version:", string.Empty).Trim();
            }
            else if (text.StartsWith("libavutil"))
            {
                Dependencies.LibavutilVersion = text.Replace("libavutil", string.Empty).Trim();
            }
            else if (text.StartsWith("libavcodec"))
            {
                Dependencies.LibavcodecVersion = text.Replace("libavcodec", string.Empty).Trim();
            }
            else if (text.StartsWith("libavformat"))
            {
                Dependencies.LibavformatVersion = text.Replace("libavformat", string.Empty).Trim();
            }
            else if (text.StartsWith("libavfilter"))
            {
                Dependencies.LibavfilterVersion = text.Replace("libavfilter", string.Empty).Trim();
            }
            else if (text.StartsWith("libswscale"))
            {
                Dependencies.LibswscaleVersion = text.Replace("libswscale", string.Empty).Trim();
            }
            else if (text.StartsWith("libswresample"))
            {
                Dependencies.LibswresampleVersion = text.Replace("libswresample", string.Empty).Trim();
            }
        }
    }

    private void TranslateProperty(MpvEventProperty property)
    {
        if (property.DataPtr == IntPtr.Zero)
        {
            return;
        }

        if (property.Name == PauseProperty)
        {
            var isPaused = Marshal.ReadInt32(property.DataPtr) == 1;
            ChangeState(isPaused ? PlaybackState.Paused : PlaybackState.Playing);
        }
        else if (property.Name == PositionProperty)
        {
            _currentPosition = Marshal.ReadInt64(property.DataPtr);
            RaisePositionChanged();
        }
        else if (property.Name == DurationProperty)
        {
            var duration = Marshal.ReadInt64(property.DataPtr);
            _currentDuration = duration;
            RaisePositionChanged();
        }
        else if (property.Name == PausedForCacheProperty)
        {
            var isPaused = Marshal.ReadInt32(property.DataPtr) == 1;
            if (isPaused)
            {
                ChangeState(PlaybackState.Buffering);
            }
            else
            {
                ChangeState(PlaybackState.Playing);
            }
        }
    }

    private void ChangeState(PlaybackState state)
    {
        if (state == PlaybackState)
        {
            return;
        }

        var args = new PlaybackStateChangedEventArgs(PlaybackState, state);
        PlaybackState = state;
        PlaybackStateChanged?.Invoke(this, args);
    }

    private void RaiseEnd(MpvEventEndFile e)
    {
        // ★ 修复（下一视频连锁跳跃）：仅在"自然播放到结尾（Eof）"时才视为播放结束。
        //   loadfile 切换文件、手动停止等场景下，旧文件同样会触发 EndFile 事件
        //   （Reason=Stop）。若都把 Stop 当作"播放结束"，上层 OnMpvEnded 会执行
        //   自动下一曲（_currentIndex++ + LoadVideo），而 LoadVideo 的 loadfile 又
        //   触发新的 EndFile(Stop) → 再次下一曲 → 连锁反应一路跳到列表最后一个文件。
        //   因此这里只对 Eof 触发播放结束事件；Stop/Quit/Redirect/Error 一律忽略
        //   （Error 已在上层 ChangeState(Failed) 处理，不重复触发）。
        if (e.Reason != MpvEndFileReason.Eof)
        {
            return;
        }

        var args = new PlaybackStoppedEventArgs(false, string.Empty);
        PlaybackStopped?.Invoke(this, args);
    }

    private void RaisePositionChanged()
    {
        if (_currentDuration > 0 && _currentPosition >= 0)
        {
            var args = new PlaybackPositionChangedEventArgs(_currentDuration, _currentPosition);
            PlaybackPositionChanged?.Invoke(this, args);
        }
    }
}
