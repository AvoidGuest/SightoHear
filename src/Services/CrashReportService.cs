using Microsoft.Win32;
using SightoHear.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;

namespace SightoHear.Services
{
    /// <summary>
    /// 崩溃报告服务（三层防线，弥补"日志只记录到中断点、丢失崩溃原因"的痛点）：
    /// 1. 配置 WER LocalDumps：让 Windows 在进程任何崩溃（含托管异常捕获不到的
    ///    原生崩溃，如 0xc000027b stowed exception / AccessViolation）时自动把
    ///    MiniDump 保存到 %LocalAppData%\SightoHear\Crashes，作为崩溃现场保留；
    /// 2. 启动时自检上次崩溃：读取 Windows 事件日志（Application Error 事件）解析
    ///    异常码、崩溃模块、崩溃时间，并检查崩溃转储目录，生成"上次崩溃摘要"
    ///    写入日志（DebugPage 通过 <see cref="LastCrashSummary"/> 展示）。
    /// 说明：.NET 托管异常由 App 的 UnhandledException 系列处理器记录完整堆栈；
    /// 原生崩溃则由本服务的 WER 转储 + 事件日志兜底（异常码 + 模块 + dump 文件）。
    /// </summary>
    public static class CrashReportService
    {
        /// <summary>崩溃转储目录（%LocalAppData%\SightoHear\Crashes）。</summary>
        public static string CrashDumpDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SightoHear", "Crashes");

        /// <summary>上次崩溃摘要文本（供 DebugPage 展示；无崩溃记录时为 null）。</summary>
        public static string? LastCrashSummary { get; private set; }

        /// <summary>
        /// 配置 WER LocalDumps：Windows 在进程崩溃时自动保存 MiniDump 到崩溃目录。
        /// 用户级 HKCU 注册表配置，对未打包的桌面应用有效，无需管理员权限。
        /// 崩溃后 dump 文件可供后续用 WinDbg/Visual Studio 打开反解原生堆栈。
        /// </summary>
        public static void ConfigureLocalDumps()
        {
            try
            {
                // 预先创建转储目录，确保 WER 崩溃时可直接写入
                Directory.CreateDirectory(CrashDumpDirectory);

                using var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\SightoHear.exe");
                if (key == null)
                {
                    return;
                }

                key.SetValue("DumpFolder", CrashDumpDirectory, RegistryValueKind.ExpandString);
                key.SetValue("DumpType", 2, RegistryValueKind.DWord); // 2 = MiniDump
                key.SetValue("DumpCount", 10, RegistryValueKind.DWord); // 最多保留 10 个转储
                AppLogger.Info($"崩溃转储已配置: {CrashDumpDirectory}");
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"配置崩溃转储失败（不影响使用）: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动时自检上次崩溃：读取 Windows 事件日志 + 检查崩溃转储目录，
        /// 生成"上次崩溃摘要"写入日志（Warning 级），供 DebugPage 展示。
        /// 应在本进程刚启动、日志系统初始化完成后调用一次。
        /// </summary>
        public static void CheckAndLogPreviousCrash()
        {
            try
            {
                // 1. 从 Application 事件日志解析本进程的崩溃事件（近 7 天）
                var crashEvents = QuerySightoHearCrashEvents(TimeSpan.FromDays(7));
                // 2. 检查崩溃转储目录中的 dump 文件
                var dumps = GetCrashDumpFiles();

                if (crashEvents.Count == 0 && dumps.Count == 0)
                {
                    AppLogger.Debug("崩溃自检：未发现上次崩溃记录");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("══════════ 上次崩溃记录 ══════════");
                if (crashEvents.Count > 0)
                {
                    var e = crashEvents[0];
                    sb.AppendLine($"崩溃时间: {e.CrashTime:yyyy-MM-dd HH:mm:ss}");
                    if (!string.IsNullOrEmpty(e.ExceptionCode))
                    {
                        sb.AppendLine($"异常码: {FormatExceptionCode(e.ExceptionCode)}");
                    }

                    if (!string.IsNullOrEmpty(e.FaultingModule))
                    {
                        sb.AppendLine($"崩溃模块: {e.FaultingModule}");
                    }

                    if (!string.IsNullOrEmpty(e.FaultingOffset))
                    {
                        sb.AppendLine($"崩溃偏移: {e.FaultingOffset}");
                    }

                    if (crashEvents.Count > 1)
                    {
                        sb.AppendLine($"近 7 天共崩溃 {crashEvents.Count} 次（其余 {crashEvents.Count - 1} 次见事件查看器）");
                    }
                }

                if (dumps.Count > 0)
                {
                    sb.AppendLine($"崩溃转储: {dumps.Count} 个（{CrashDumpDirectory}）");
                    foreach (var d in dumps.Take(3))
                    {
                        try
                        {
                            sb.AppendLine($"  {Path.GetFileName(d)} ({new FileInfo(d).Length / 1024.0:F0} KB)");
                        }
                        catch { }
                    }
                }

                sb.AppendLine("══════════ 崩溃记录结束 ══════════");

                LastCrashSummary = sb.ToString();
                AppLogger.Warning(sb.ToString());
            }
            catch (Exception ex)
            {
                // 自检失败不应影响启动，仅 Debug 级记录
                AppLogger.Debug($"崩溃自检失败: {ex.Message}");
            }
        }

        /// <summary>查询 Application 事件日志中近 window 内本进程（SightoHear.exe）的崩溃事件。</summary>
        private static List<CrashEventInfo> QuerySightoHearCrashEvents(TimeSpan window)
        {
            var result = new List<CrashEventInfo>();
            try
            {
                // Application Error 事件（EventId 1000）：P1=应用名 P4=故障模块 P7=异常码 P8=偏移
                string xpath =
                    $"*[System[Provider[@Name='Application Error'] and EventID=1000 and TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]";
                var query = new EventLogQuery("Application", PathType.LogName, xpath);
                using var reader = new EventLogReader(query);
                while (true)
                {
                    EventRecord? record = null;
                    try
                    {
                        record = reader.ReadEvent();
                        if (record == null)
                        {
                            break;
                        }

                        // 仅关心本进程的崩溃
                        string appName = GetEventProperty(record, 0);
                        if (string.IsNullOrEmpty(appName) ||
                            !appName.Contains("SightoHear", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        result.Add(new CrashEventInfo
                        {
                            CrashTime = record.TimeCreated?.ToLocalTime() ?? DateTime.MinValue,
                            FaultingModule = GetEventProperty(record, 3),
                            ExceptionCode = GetEventProperty(record, 6),
                            FaultingOffset = GetEventProperty(record, 7),
                        });
                    }
                    catch
                    {
                        // 单条事件解析失败不影响其余事件
                    }
                    finally
                    {
                        record?.Dispose();
                    }
                }

                // 按崩溃时间倒序（最近在前）
                result.Sort((a, b) => b.CrashTime.CompareTo(a.CrashTime));
            }
            catch
            {
                // 事件日志不可用（权限/服务未启动等）时静默返回空列表
            }

            return result;
        }

        /// <summary>读取事件属性（Application Error 事件的 P1..P10 依次对应 Properties[0..9]）。</summary>
        private static string GetEventProperty(EventRecord record, int index)
        {
            try
            {
                if (record.Properties != null && index < record.Properties.Count)
                {
                    return record.Properties[index]?.Value?.ToString() ?? string.Empty;
                }
            }
            catch { }

            return string.Empty;
        }

        /// <summary>格式化异常码：事件日志存的是无前缀十六进制（如 c000027b），补全为 0xc000027b 便于识别。</summary>
        private static string FormatExceptionCode(string code)
        {
            var c = code.Trim();
            if (c.Length > 0 && c.Length <= 8)
            {
                bool isHex = true;
                foreach (var ch in c)
                {
                    if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
                    {
                        isHex = false;
                        break;
                    }
                }

                if (isHex)
                {
                    return "0x" + c;
                }
            }

            return c;
        }

        /// <summary>获取崩溃转储目录中的 dump 文件（按修改时间倒序）。</summary>
        private static List<string> GetCrashDumpFiles()
        {
            try
            {
                if (!Directory.Exists(CrashDumpDirectory))
                {
                    return new List<string>();
                }

                return Directory.GetFiles(CrashDumpDirectory, "*.dmp")
                    .OrderByDescending(f =>
                    {
                        try { return File.GetLastWriteTime(f); }
                        catch { return DateTime.MinValue; }
                    })
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>崩溃事件信息（从 Application Error 事件解析）。</summary>
        private sealed class CrashEventInfo
        {
            public DateTime CrashTime { get; set; }
            public string? FaultingModule { get; set; }
            public string? ExceptionCode { get; set; }
            public string? FaultingOffset { get; set; }
        }
    }
}
