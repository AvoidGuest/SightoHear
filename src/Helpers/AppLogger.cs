using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Timer = System.Timers.Timer;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 日志级别（枚举数值与 DebugPage 的 ComboBox 索引一致：0=Trace 1=Debug 2=Info 3=Warning）。
    /// </summary>
    public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warning = 3, Error = 4, Fatal = 5 }

    public static class AppLogger
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "logs");

        /// <summary>日志缓冲达到该条数时立即落盘，保证实时性（避免长时间依赖定时器）</summary>
        private const int FlushBufferThreshold = 32;

        /// <summary>当前启动会话的日志文件路径，在 Initialize() 中确定</summary>
        private static string? _currentLogFilePath = null;

        private static readonly object _recentLock = new object();
        private static readonly List<string> _recentLogs = new List<string>();
        private const int MaxRecentLogs = 50;
        private static Logger? _logger = null;
        private static LoggingLevelSwitch? _levelSwitch = null;
        private static readonly ConcurrentQueue<string> _logBuffer = new();
        private static Timer? _flushTimer;

        public static bool IsEnabled { get; set; } = true;
        public static LogLevel CurrentLevel { get; set; } = LogLevel.Info;
        public static bool ProtectSensitiveInfo { get; set; } = true;
        public static bool IsDevMode { get; set; } = false;

        public static event EventHandler? RecentLogsChanged;

        public static IReadOnlyList<string> GetRecentLogs()
        {
            lock (_recentLock) return _recentLogs.ToList().AsReadOnly();
        }

        /// <summary>获取当前启动会话的日志文件路径</summary>
        public static string GetCurrentLogFilePath()
        {
            return _currentLogFilePath ?? Path.Combine(LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");
        }

        /// <summary>
        /// 保留 GetTodayLogFilePath 作为兼容别名，指向当前会话日志文件。
        /// </summary>
        public static string GetTodayLogFilePath() => GetCurrentLogFilePath();

        public static string GetLogStats()
        {
            try
            {
                var path = GetCurrentLogFilePath();
                if (!File.Exists(path)) return "当前会话暂无日志";
                var info = new FileInfo(path);
                long lines = 0;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs))
                {
                    while (reader.ReadLine() != null) lines++;
                }
                var sizeKb = info.Length / 1024.0;
                return $"文件: {info.Name} | 大小: {sizeKb:F1} KB | 行数: {lines}";
            }
            catch { return "统计失败"; }
        }

        public static void Initialize()
        {
            Directory.CreateDirectory(LogDir);

            // 每次应用启动生成一个独立的日志文件，文件名包含启动时间戳
            var now = DateTime.Now;
            _currentLogFilePath = Path.Combine(LogDir, $"app-{now:yyyyMMdd}-{now:HHmmss}.log");

            _levelSwitch = new LoggingLevelSwitch(MapLevel(CurrentLevel));

            _logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(_levelSwitch)
                .WriteTo.Debug(outputTemplate: "[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            // 定期兜底刷新：正常路径下缓冲达到阈值即刷新，定时器仅作保险
            _flushTimer = new Timer(30000);
            _flushTimer.Elapsed += (s, e) => FlushToFile();
            _flushTimer.AutoReset = true;
            _flushTimer.Start();
        }

        public static void UpdateLevel()
        {
            if (_levelSwitch != null)
                _levelSwitch.MinimumLevel = MapLevel(CurrentLevel);
        }

        public static void CloseAndFlush()
        {
            _logger?.Dispose();
        }

        /// <summary>
        /// 立即将缓冲中的日志写入文件（供关键路径排查崩溃点使用，
        /// 正常情况下日志由定时器兜底刷新，无需手动调用）。
        /// </summary>
        public static void Flush()
        {
            try
            {
                FlushToFile();
            }
            catch { }
        }

        public static void FlushAndClose()
        {
            try
            {
                _flushTimer?.Stop();
                _flushTimer?.Dispose();
            }
            catch { }
            finally
            {
                _flushTimer = null;
            }

            FlushToFile();
            _logger?.Dispose();
        }

        private static void FlushToFile()
        {
            if (_logBuffer.IsEmpty) return;

            var list = new List<string>();
            while (_logBuffer.TryDequeue(out var line))
            {
                list.Add(line);
            }

            if (list.Count > 0)
            {
                try
                {
                    File.AppendAllLines(GetTodayLogFilePath(), list);
                }
                catch { }
            }
        }

        public static void Trace(string msg) => Write(LogLevel.Trace, msg);
        public static void Debug(string msg) => Write(LogLevel.Debug, msg);
        public static void Info(string msg) => Write(LogLevel.Info, msg);
        public static void Warning(string msg) => Write(LogLevel.Warning, msg);

        public static void Error(Exception ex, string context = "")
        {
            var msg = string.IsNullOrEmpty(context)
                ? $"{ex.GetType().Name}: {ex.Message}"
                : $"[{context}] {ex.GetType().Name}: {ex.Message}";
            Write(LogLevel.Error, msg);
            // 堆栈仅首帧（文件:行号）用于快速定位；开发者模式下输出完整堆栈便于排查
            if (ex.StackTrace != null)
                Write(LogLevel.Error, IsDevMode
                    ? CompactStackTrace(ex.StackTrace)
                    : FirstFrameOf(ex.StackTrace));
        }

        public static void Fatal(Exception ex, string context = "")
        {
            var msg = string.IsNullOrEmpty(context)
                ? $"FATAL | {ex.GetType().Name}: {ex.Message}"
                : $"FATAL | [{context}] {ex.GetType().Name}: {ex.Message}";
            Write(LogLevel.Fatal, msg);
            // ★ 崩溃场景（Fatal）始终记录完整堆栈——不受 DevMode 限制，
            //   否则非调试模式下崩溃日志只剩首帧，难以定位问题源头。
            if (ex.StackTrace != null)
                Write(LogLevel.Fatal, FullStackTrace(ex.StackTrace));
        }

        /// <summary>将多行堆栈压缩为单行（完整保留全部帧，用于崩溃场景定位）。</summary>
        private static string FullStackTrace(string stackTrace)
        {
            return string.Join(" ⏎ ", stackTrace.Split(
                new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>将多行堆栈压缩为单行（去除换行与缩进），减少日志文件行数</summary>
        private static string CompactStackTrace(string stackTrace)
        {
            return string.Join(" ", stackTrace.Split(
                new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                .Replace("at ", "← "); // 保留定位信息，压缩体积
        }

        /// <summary>仅保留堆栈第一帧（方法 + 文件:行号），用于快速定位错误源头</summary>
        private static string FirstFrameOf(string stackTrace)
        {
            var firstLine = stackTrace.Split(
                new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            return string.IsNullOrEmpty(firstLine) ? stackTrace : firstLine;
        }

        private static void Write(LogLevel level, string message)
        {
            if (!IsEnabled || level < CurrentLevel) return;

            var sanitized = Sanitize(message);
            // 标准格式：[时间] [级别标签] 消息内容
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{LevelTag(level)}] {sanitized}";

            _logBuffer.Enqueue(line);

            // 缓冲达到阈值立即落盘，保证日志实时可查
            if (_logBuffer.Count >= FlushBufferThreshold)
                FlushToFile();

            lock (_recentLock)
            {
                _recentLogs.Add(line);
                while (_recentLogs.Count > MaxRecentLogs) _recentLogs.RemoveAt(0);
            }
            RecentLogsChanged?.Invoke(null, EventArgs.Empty);

            if (_logger == null) return;
            switch (level)
            {
                case LogLevel.Trace: _logger.Verbose(sanitized); break;
                case LogLevel.Debug: _logger.Debug(sanitized); break;
                case LogLevel.Info: _logger.Information(sanitized); break;
                case LogLevel.Warning: _logger.Warning(sanitized); break;
                case LogLevel.Error: _logger.Error(sanitized); break;
                case LogLevel.Fatal: _logger.Fatal(sanitized); break;
            }
        }

        /// <summary>级别缩写标签（与日志文件保持一致，便于阅读与检索）</summary>
        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Fatal => "FTL",
            _ => "???"
        };

        private static string Sanitize(string input)
        {
            if (!ProtectSensitiveInfo) return input;
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
                input = input.Replace(userProfile, "%USERPROFILE%");
            input = Regex.Replace(input, @"[a-fA-F0-9]{32,}", "[HASH]");
            input = Regex.Replace(input, @"token=[^\s&]+", "token=[REDACTED]");
            return input;
        }

        public static void CleanupOldLogs(int daysToKeep = 7)
        {
            try
            {
                if (!Directory.Exists(LogDir)) return;
                var cutoff = DateTime.Now.AddDays(-daysToKeep);
                foreach (var file in Directory.GetFiles(LogDir, "app-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                            File.Delete(file);
                    }
                    catch { }
                }
                // 清理旧版单文件日志
                try
                {
                    var oldLogFile = Path.Combine(LogDir, "app.log");
                    if (File.Exists(oldLogFile) && File.GetLastWriteTime(oldLogFile) < cutoff)
                        File.Delete(oldLogFile);
                }
                catch { }
            }
            catch { }
        }

        public static string GetLogFolderPath() => LogDir;

        private static LogEventLevel MapLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Info => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Fatal => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
    }
}