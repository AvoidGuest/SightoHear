using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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

        /// <summary>定时清理旧日志的计时器（每 6 小时一次）</summary>
        private static Timer? _cleanupTimer;

        public static bool IsEnabled { get; set; } = true;
        public static LogLevel CurrentLevel { get; set; } = LogLevel.Info;
        public static bool ProtectSensitiveInfo { get; set; } = true;
        public static bool IsDevMode { get; set; } = false;

        /// <summary>自动清理旧日志的总开关（由调试页「自动清理旧日志」写入）</summary>
        public static bool AutoCleanEnabled { get; set; } = true;

        /// <summary>日志保留天数（由调试页「保留天数」写入，范围 1~30）</summary>
        public static int LogRetentionDays { get; set; } = 7;

        /// <summary>日志文件夹里最多保留的文件数，超出时按时间从旧到新删除（防止同一天启动几十次把磁盘塞满）</summary>
        private const int MaxLogFiles = 200;

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

            // 定期清理旧日志：每 6 小时检查一次。
            // ★ 这是"保留 N 天"真正生效的关键——旧实现只在启动时清一次，
            //   用户连续用几天不重启，日志就一直堆着删不掉。
            _cleanupTimer = new Timer(TimeSpan.FromHours(6).TotalMilliseconds);
            _cleanupTimer.Elapsed += (s, e) =>
            {
                try { CleanupOldLogsInBackground(); }
                catch (Exception ex) { Debug($"[日志清理] 定时清理触发失败: {ex.Message}"); }
            };
            _cleanupTimer.AutoReset = true;
            _cleanupTimer.Start();
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
                _cleanupTimer?.Stop();
                _cleanupTimer?.Dispose();
            }
            catch { }
            finally
            {
                _flushTimer = null;
                _cleanupTimer = null;
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

        // ==================== 敏感信息脱敏 ====================
        // ★ 设计目标：日志要能用来排查问题，所以"能定位到哪个软件、哪类文件"的信息要保留，
        //   "能定位到具体某个人"的信息要抹掉。因此：
        //     路径 → 抹掉用户名，保留后面的目录结构（否则排查文件相关问题全靠猜）
        //     密钥/令牌/密码 → 整体替换
        //     GUID / 长哈希 / Base64 → 保留前后少量字符，可做关联比对但不能还原

        /// <summary>用户名出现的常见路径根（含网络路径），统一替换为占位符并保留尾部目录结构。</summary>
        private static readonly Regex UserPathRegex = new(
            @"(?:[A-Za-z]:\\Users\\|\\\?\?\\[A-Za-z]:\\Users\\|/Users/|/home/)([^\\/\s""'<>|]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>占位符常量，供兜底规则跳过已替换的内容。</summary>
        private const string UserPlaceholder = "[USER]";

        /// <summary>形如 key=value / key: value 的凭据，key 命中敏感词时整体打码。</summary>
        private static readonly Regex SecretPairRegex = new(
            @"(?<key>\b(?:token|access[_-]?token|refresh[_-]?token|api[_-]?key|apikey|secret|client[_-]?secret|password|passwd|pwd|auth|authorization|session[_-]?id|cookie|sig|signature|credential)\b\s*[:=]\s*)(?<val>[^\s,;""'&]{3,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Bearer / Basic 认证头。</summary>
        private static readonly Regex AuthHeaderRegex = new(
            @"\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/]{8,}=*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>URL 里的 query 凭据（?token=xxx&amp;key=yyy）。</summary>
        private static readonly Regex UrlSecretRegex = new(
            @"(?<pre>[?&](?:token|key|secret|password|sig|signature|auth|access_token)=)(?<val>[^&\s]{3,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>GUID（设备 ID、播放列表 ID 等）。保留首尾各 4 位便于关联，中间打码。</summary>
        private static readonly Regex GuidRegex = new(
            @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
            RegexOptions.Compiled);

        /// <summary>长十六进制串（哈希、密钥）。32 位以上才处理，避免误伤普通编号。</summary>
        private static readonly Regex LongHexRegex = new(
            @"\b[0-9a-fA-F]{32,}\b",
            RegexOptions.Compiled);

        /// <summary>形似邮箱的地址：保留域名，抹掉本地部分。</summary>
        private static readonly Regex EmailRegex = new(
            @"\b([A-Za-z0-9._%+\-]+)@([A-Za-z0-9.\-]+\.[A-Za-z]{2,})\b",
            RegexOptions.Compiled);

        /// <summary>手机号（中国大陆 11 位）：保留前 3 后 4。</summary>
        private static readonly Regex PhoneRegex = new(
            @"(?<![0-9])(1[3-9]\d)\d{4}(\d{4})(?![0-9])",
            RegexOptions.Compiled);

        /// <summary>IP 地址（含端口）：保留网段，末位打码。</summary>
        private static readonly Regex Ipv4Regex = new(
            @"(?<![0-9.])((?:\d{1,3}\.){3})\d{1,3}(?![0-9.])",
            RegexOptions.Compiled);

        /// <summary>
        /// 脱敏入口。按"从强到弱"的顺序处理，先抹掉凭据再抹路径，
        /// 避免凭据里恰好含路径片段导致规则互相干扰。
        /// </summary>
        private static string Sanitize(string input)
        {
            if (!ProtectSensitiveInfo || string.IsNullOrEmpty(input)) return input;

            string result = input;

            try
            {
                // ① 凭据类：整体打码，不保留任何可还原信息
                //    先用 AuthHeader 吃掉 "Bearer xxx" 整段，避免后续 SecretPair 再打一次
                result = AuthHeaderRegex.Replace(result, "[CREDENTIAL-REDACTED]");
                result = SecretPairRegex.Replace(result, "${key}[REDACTED]");
                result = UrlSecretRegex.Replace(result, "${pre}[REDACTED]");

                // ② 带盘符的用户路径：抹掉用户名，保留后续目录结构
                //    （排查仍需要知道具体子目录，所以只替换用户名这一段）
                result = UserPathRegex.Replace(result, m =>
                {
                    string full = m.Value;
                    string userNameInPath = m.Groups[1].Value;
                    string prefix = full[..full.LastIndexOf(userNameInPath, StringComparison.Ordinal)];
                    return prefix + UserPlaceholder;
                });

                // ③ 当前登录用户名兜底：覆盖没写盘符的裸路径（如 AppData\Local\<用户名>\...）
                //    以及任何零散出现用户名的场合。
                //    ★ 只做这一处通用替换即可，不需要再单独处理 AppData 前缀，
                //      否则会和 ② 叠加成 [USER]\[USER]。
                string userName = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3)
                {
                    result = Regex.Replace(
                        result,
                        Regex.Escape(userName),
                        UserPlaceholder,
                        RegexOptions.IgnoreCase);
                }

                // ④ 长哈希 / GUID：保留首尾便于比对，中间打码
                result = LongHexRegex.Replace(result, m =>
                    m.Value.Length <= 12
                        ? m.Value
                        : $"{m.Value[..4]}…[REDACTED]…{m.Value[^4..]}");

                result = GuidRegex.Replace(result, m =>
                    $"{m.Value[..4]}-****-****-****-****{m.Value[^4..]}");

                // ⑤ 联系方式与网络标识
                result = EmailRegex.Replace(result, "[EMAIL]@$2");
                result = PhoneRegex.Replace(result, "$1****$2");
                result = Ipv4Regex.Replace(result, "${1}xxx");

                return result;
            }
            catch
            {
                // 脱敏失败绝不能影响日志本身落盘，失败时返回原文（宁可日志有敏感信息也不能丢日志）
                return input;
            }
        }

        /// <summary>
        /// 清理过期日志。两个维度同时生效，任一超限即删除：
        /// <list type="number">
        /// <item><b>时间</b>：最后写入超过 <paramref name="daysToKeep"/> 天的文件</item>
        /// <item><b>数量</b>：文件数超过 <see cref="MaxLogFiles"/> 时，从最旧开始删到限额内</item>
        /// </list>
        /// </summary>
        /// <param name="daysToKeep">保留天数（&lt;=0 时按 1 天处理）</param>
        /// <param name="dryRun">为 true 时只统计不实际删除（供调试页预览"将清理多少个"）</param>
        /// <returns>本次实际删除的文件数</returns>
        /// <remarks>
        /// ★ 旧实现的问题（实测过的坑）：
        /// <list type="bullet">
        /// <item>只在启动时和改设置时各跑一次，"7 天后删"其实要等下次启动才生效 ——
        ///       用户长期不重启的话日志就一直堆着。</item>
        /// <item>删除失败被 <c>catch { }</c> 完全吞掉，被占用（比如正在写的当前日志）的文件
        ///       永远删不掉且毫无提示，磁盘上就一直是那堆老文件。</item>
        /// <item>没有数量上限，一天内反复启动会积累大量小文件。</item>
        /// </list>
        /// 现在会返回删除统计并写入日志，便于在调试页确认清理真的发生了。
        /// </remarks>
        public static int CleanupOldLogs(int daysToKeep = 7, bool dryRun = false)
        {
            int deleted = 0;
            try
            {
                if (!Directory.Exists(LogDir)) return 0;

                int keepDays = daysToKeep <= 0 ? 1 : daysToKeep;
                var cutoff = DateTime.Now.AddDays(-keepDays);

                // 只处理本应用的会话日志，按最后写入时间升序（最旧在前）
                var logFiles = Directory.GetFiles(LogDir, "app-*.log")
                    .Select(p => new FileInfo(p))
                    .OrderBy(f => f.LastWriteTime)
                    .ToList();

                // ── 维度一：时间 ──
                var toDelete = logFiles.Where(f => f.LastWriteTime < cutoff).ToList();

                // ── 维度二：数量（时间清理后仍超限时，继续从最旧删）──
                int remainingAfterTime = logFiles.Count - toDelete.Count;
                if (remainingAfterTime > MaxLogFiles)
                {
                    int needMore = remainingAfterTime - MaxLogFiles;
                    toDelete.AddRange(logFiles
                        .Where(f => f.LastWriteTime >= cutoff)
                        .Take(needMore));
                }

                foreach (var file in toDelete)
                {
                    // 当前会话正在写的文件绝不删除
                    if (string.Equals(file.FullName, _currentLogFilePath,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (dryRun)
                    {
                        deleted++;
                        continue;
                    }

                    if (TryDeleteLogFile(file))
                        deleted++;
                }

                // 顺带清理旧版单文件日志
                try
                {
                    var oldLog = Path.Combine(LogDir, "app.log");
                    if (File.Exists(oldLog) && File.GetLastWriteTime(oldLog) < cutoff && !dryRun)
                        File.Delete(oldLog);
                }
                catch (Exception ex)
                {
                    Debug($"[日志清理] 删除旧版 app.log 失败: {ex.Message}");
                }

                if (deleted > 0 && !dryRun)
                {
                    Info($"[日志清理] 已删除 {deleted} 个超过 {keepDays} 天的日志文件" +
                         $"（上限 {MaxLogFiles} 个，当前保留 {logFiles.Count - deleted} 个）");
                    // ★ 清理是在后台线程跑的，缓冲区要等 32 条或 30 秒定时器才落盘；
                    //   这里主动刷一次，保证"清理"这件事在日志里留得下痕迹（否则用户无从确认）。
                    FlushToFile();
                }
            }
            catch (Exception ex)
            {
                // 清理失败不能影响主流程，但必须留下痕迹（旧实现是静默吞掉）
                Error(ex, "[日志清理] 清理旧日志失败");
            }
            return deleted;
        }

        /// <summary>删除单个日志文件，带一次延迟重试（应对秒级文件占用）。</summary>
        private static bool TryDeleteLogFile(FileInfo file)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    file.Refresh();
                    if (!file.Exists) return true;
                    // 文件可能被别的日志查看器打开，用 Delete 而非覆盖删，失败再重试
                    file.Delete();
                    return true;
                }
                catch (IOException)
                {
                    // 首次失败多半是被短暂占用，等 120ms 再试
                    if (attempt == 0) Thread.Sleep(120);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Debug($"[日志清理] 无权限删除 {file.Name}: {ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    Debug($"[日志清理] 删除 {file.Name} 失败: {ex.Message}");
                    return false;
                }
            }

            Debug($"[日志清理] {file.Name} 被占用，本次跳过（下次启动会重试）");
            return false;
        }

        /// <summary>
        /// 按当前设置（<see cref="AutoCleanEnabled"/> / <see cref="LogRetentionDays"/>）
        /// 在后台线程清理旧日志，不阻塞启动流程。
        /// </summary>
        public static void CleanupOldLogsInBackground()
        {
            if (!AutoCleanEnabled) return;
            int days = LogRetentionDays;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    CleanupOldLogs(days);
                }
                catch (Exception ex)
                {
                    Debug($"[日志清理] 后台清理异常: {ex.Message}");
                }
            });
        }

        public static (int FileCount, long TotalBytes, int ExpiredCount) GetLogFolderStats()
        {
            try
            {
                if (!Directory.Exists(LogDir)) return (0, 0, 0);

                var files = Directory.GetFiles(LogDir, "app-*.log")
                    .Select(p => new FileInfo(p))
                    .ToList();

                long totalBytes = files.Sum(f => f.Length);
                var cutoff = DateTime.Now.AddDays(-(LogRetentionDays <= 0 ? 1 : LogRetentionDays));
                int expired = files.Count(f => f.LastWriteTime < cutoff);

                // 数量超限的部分也算"待清理"
                int byCount = Math.Max(0, files.Count - MaxLogFiles);
                return (files.Count, totalBytes, Math.Max(expired, byCount));
            }
            catch
            {
                return (0, 0, 0);
            }
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