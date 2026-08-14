using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SightoHear.Helpers;
using SightoHear.Services;

namespace SightoHear
{
    public sealed partial class DebugPage : Page
    {
        private readonly string _settingsPath;
        private bool _isLoading = true;
        private bool _isDevModeSubscribed = false;
        // "所有"开关与子开关之间的状态同步标志：
        // 批量设置子开关时抑制子开关事件里的"所有"状态回写，避免循环触发
        private bool _isSyncingAllToggle;
        // GPU 监控状态刷新定时器（1 秒，页面活跃期间运行）
        private DispatcherQueueTimer? _gpuStatusTimer;

        public DebugPage()
        {
            InitializeComponent();
            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SightoHear", "settings.json");
            this.Loaded += DebugPage_Loaded;
        }

        private void DebugPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            _isLoading = false;
            ApplyWin2DPerfHudSettings();
            EnsureGpuStatusTimer();
            UpdateGpuMonitorUi();
            // 实时日志统计默认文案（开发者模式打开前先占位）
            if (LogStatsText != null && string.IsNullOrEmpty(LogStatsText.Text))
                LogStatsText.Text = "统计加载中...";
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var node = JsonNode.Parse(json);
                    if (node?["DebugSettings"] is JsonObject debug)
                    {
                        if (LogEnabledToggle != null) LogEnabledToggle.IsOn = debug["LogEnabled"]?.GetValue<bool>() ?? false;
                        if (LogLevelComboBox != null) LogLevelComboBox.SelectedIndex = Math.Clamp(debug["LogLevel"]?.GetValue<int>() ?? 2, 0, 3);
                        if (ProtectSensitiveToggle != null) ProtectSensitiveToggle.IsOn = debug["ProtectSensitive"]?.GetValue<bool>() ?? true;
                        if (AutoCleanToggle != null) AutoCleanToggle.IsOn = debug["AutoClean"]?.GetValue<bool>() ?? true;
                        if (CleanDaysNumberBox != null) CleanDaysNumberBox.Value = debug["CleanDays"]?.GetValue<double>() ?? 7;
                        if (DevModeToggle != null) DevModeToggle.IsOn = debug["DevMode"]?.GetValue<bool>() ?? false;
                        UpdateDevModePanel();

                        // ── Win2D 性能监测悬浮窗设置 ──
                        if (Win2DPerfEnabledToggle != null) Win2DPerfEnabledToggle.IsOn = debug["Win2DPerfEnabled"]?.GetValue<bool>() ?? false;
                        if (ShowFpsToggle != null) ShowFpsToggle.IsOn = debug["Win2DShowFps"]?.GetValue<bool>() ?? true;
                        if (ShowAvgFpsToggle != null) ShowAvgFpsToggle.IsOn = debug["Win2DShowAvgFps"]?.GetValue<bool>() ?? false;
                        if (ShowFrameTimeToggle != null) ShowFrameTimeToggle.IsOn = debug["Win2DShowFrameTime"]?.GetValue<bool>() ?? false;
                        if (ShowUpdateTimeToggle != null) ShowUpdateTimeToggle.IsOn = debug["Win2DShowUpdateTime"]?.GetValue<bool>() ?? false;
                        if (ShowDrawTimeToggle != null) ShowDrawTimeToggle.IsOn = debug["Win2DShowDrawTime"]?.GetValue<bool>() ?? false;
                        if (ShowFrameJitterToggle != null) ShowFrameJitterToggle.IsOn = debug["Win2DShowFrameJitter"]?.GetValue<bool>() ?? false;
                        if (ShowDroppedFramesToggle != null) ShowDroppedFramesToggle.IsOn = debug["Win2DShowDroppedFrames"]?.GetValue<bool>() ?? false;
                        if (ShowMemoryToggle != null) ShowMemoryToggle.IsOn = debug["Win2DShowMemory"]?.GetValue<bool>() ?? false;
                        if (ShowResolutionToggle != null) ShowResolutionToggle.IsOn = debug["Win2DShowResolution"]?.GetValue<bool>() ?? false;
                        if (ShowGpuModeToggle != null) ShowGpuModeToggle.IsOn = debug["Win2DShowGpuMode"]?.GetValue<bool>() ?? false;
                    }
                    else
                    {
                        SetDefaults();
                    }
                }
                else
                {
                    SetDefaults();
                }
            }
            catch { SetDefaults(); }
            // 根据各监测开关的加载结果同步"所有"开关的指示状态
            SyncAllTogglesState();
            ApplyLoggerSettings();
        }

        private void SetDefaults()
        {
            if (LogEnabledToggle != null) LogEnabledToggle.IsOn = false;
            if (LogLevelComboBox != null) LogLevelComboBox.SelectedIndex = 2;
            if (ProtectSensitiveToggle != null) ProtectSensitiveToggle.IsOn = true;
            if (AutoCleanToggle != null) AutoCleanToggle.IsOn = true;
            if (CleanDaysNumberBox != null) CleanDaysNumberBox.Value = 7;
            if (DevModeToggle != null) DevModeToggle.IsOn = false;
            UpdateDevModePanel();

            // ── Win2D 性能监测悬浮窗默认值 ──
            if (Win2DPerfEnabledToggle != null) Win2DPerfEnabledToggle.IsOn = false;
            if (ShowFpsToggle != null) ShowFpsToggle.IsOn = true;
            if (ShowAvgFpsToggle != null) ShowAvgFpsToggle.IsOn = false;
            if (ShowFrameTimeToggle != null) ShowFrameTimeToggle.IsOn = false;
            if (ShowUpdateTimeToggle != null) ShowUpdateTimeToggle.IsOn = false;
            if (ShowDrawTimeToggle != null) ShowDrawTimeToggle.IsOn = false;
            if (ShowFrameJitterToggle != null) ShowFrameJitterToggle.IsOn = false;
            if (ShowDroppedFramesToggle != null) ShowDroppedFramesToggle.IsOn = false;
            if (ShowMemoryToggle != null) ShowMemoryToggle.IsOn = false;
            if (ShowResolutionToggle != null) ShowResolutionToggle.IsOn = false;
            if (ShowGpuModeToggle != null) ShowGpuModeToggle.IsOn = false;
        }

        private void SaveSettings()
        {
            if (_isLoading) return;
            if (LogEnabledToggle == null || LogLevelComboBox == null || ProtectSensitiveToggle == null ||
                AutoCleanToggle == null || CleanDaysNumberBox == null || DevModeToggle == null ||
                Win2DPerfEnabledToggle == null || ShowFpsToggle == null || ShowAvgFpsToggle == null ||
                ShowFrameTimeToggle == null || ShowUpdateTimeToggle == null || ShowDrawTimeToggle == null ||
                ShowFrameJitterToggle == null || ShowDroppedFramesToggle == null || ShowMemoryToggle == null ||
                ShowResolutionToggle == null || ShowGpuModeToggle == null)
                return;

            try
            {
                JsonNode? node = null;
                if (File.Exists(_settingsPath))
                {
                    try { node = JsonNode.Parse(File.ReadAllText(_settingsPath)); } catch { }
                }
                if (node == null) node = new JsonObject();

                var debug = new JsonObject();
                debug["LogEnabled"] = LogEnabledToggle.IsOn;
                debug["LogLevel"] = LogLevelComboBox.SelectedIndex;
                debug["ProtectSensitive"] = ProtectSensitiveToggle.IsOn;
                debug["AutoClean"] = AutoCleanToggle.IsOn;
                debug["CleanDays"] = CleanDaysNumberBox.Value;
                debug["DevMode"] = DevModeToggle.IsOn;

                // ── Win2D 性能监测悬浮窗设置 ──
                debug["Win2DPerfEnabled"] = Win2DPerfEnabledToggle.IsOn;
                debug["Win2DShowFps"] = ShowFpsToggle.IsOn;
                debug["Win2DShowAvgFps"] = ShowAvgFpsToggle.IsOn;
                debug["Win2DShowFrameTime"] = ShowFrameTimeToggle.IsOn;
                debug["Win2DShowUpdateTime"] = ShowUpdateTimeToggle.IsOn;
                debug["Win2DShowDrawTime"] = ShowDrawTimeToggle.IsOn;
                debug["Win2DShowFrameJitter"] = ShowFrameJitterToggle.IsOn;
                debug["Win2DShowDroppedFrames"] = ShowDroppedFramesToggle.IsOn;
                debug["Win2DShowMemory"] = ShowMemoryToggle.IsOn;
                debug["Win2DShowResolution"] = ShowResolutionToggle.IsOn;
                debug["Win2DShowGpuMode"] = ShowGpuModeToggle.IsOn;

                node["DebugSettings"] = debug;
                AppLogger.Info($"日志设置变更: 启用={LogEnabledToggle.IsOn}, 级别={LogLevelComboBox.SelectedIndex}, 保护敏感信息={ProtectSensitiveToggle.IsOn}, 自动清理={AutoCleanToggle.IsOn}, 清理天数={CleanDaysNumberBox.Value}, 开发者模式={DevModeToggle.IsOn}, Win2D悬浮窗={Win2DPerfEnabledToggle.IsOn}");

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_settingsPath, node.ToJsonString(options));
                ApplyLoggerSettings();
            }
            catch { }
        }

        private void ApplyLoggerSettings()
        {
            if (LogEnabledToggle == null || LogLevelComboBox == null || ProtectSensitiveToggle == null || AutoCleanToggle == null || CleanDaysNumberBox == null)
                return;

            Helpers.AppLogger.IsEnabled = LogEnabledToggle.IsOn;
            Helpers.AppLogger.CurrentLevel = (Helpers.LogLevel)LogLevelComboBox.SelectedIndex;
            Helpers.AppLogger.ProtectSensitiveInfo = ProtectSensitiveToggle.IsOn;
            if (AutoCleanToggle.IsOn)
                Helpers.AppLogger.CleanupOldLogs((int)CleanDaysNumberBox.Value);
            Helpers.AppLogger.IsDevMode = DevModeToggle.IsOn;
        }

        private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("用户打开日志文件夹");
            var path = Helpers.AppLogger.GetLogFolderPath();
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        /// <summary>
        /// 手动输出一份资源诊断快照（进程内存 / 缓存条目 / 页面泄漏 / Win2D 画布等），
        /// 用于排查浏览多个页面后 Win2D 渲染卡顿的资源累积根源。
        /// </summary>
        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("用户手动触发资源诊断快照");
            ResourceDiagnosticsService.LogSnapshot("调试页手动触发");
        }

        // ───────────────────────── GPU 监控 ─────────────────────────

        /// <summary>
        /// 启动 GPU 监控：重置统计并开始 1Hz 采集（显存 / 帧 / 内存 / UI 线程卡顿）。
        /// 传入 UI 线程 DispatcherQueue 以启用 UI 卡顿检测。
        /// </summary>
        private void GpuMonitorStartButton_Click(object sender, RoutedEventArgs e)
        {
            GpuMonitorService.Start(DispatcherQueue.GetForCurrentThread());
            UpdateGpuMonitorUi();
            AppLogger.Info("开始 GPU 监控（含 UI 线程卡顿检测）");
        }

        /// <summary>
        /// 停止 GPU 监控并生成诊断报告（写入日志文件夹），报告路径显示在状态文本。
        /// </summary>
        private void GpuMonitorStopButton_Click(object sender, RoutedEventArgs e)
        {
            string path = GpuMonitorService.Stop();
            UpdateGpuMonitorUi();
            if (!string.IsNullOrEmpty(path))
            {
                GpuMonitorStatusText.Text = $"监控已停止，报告已生成：{path}";
                AppLogger.Info($"GPU 监控报告已生成: {path}");
            }
            else
            {
                GpuMonitorStatusText.Text = "监控已停止，但报告写入失败（请检查日志文件夹权限）";
            }
        }

        /// <summary>创建并启动 1 秒状态刷新定时器（页面活跃期间持续更新监控状态文本）。</summary>
        private void EnsureGpuStatusTimer()
        {
            if (_gpuStatusTimer != null) return;
            _gpuStatusTimer = DispatcherQueue.CreateTimer();
            _gpuStatusTimer.Interval = TimeSpan.FromSeconds(1);
            _gpuStatusTimer.IsRepeating = true;
            _gpuStatusTimer.Tick += (s, e) => UpdateGpuMonitorUi();
            _gpuStatusTimer.Start();
        }

        /// <summary>根据监控状态刷新按钮可用性与状态文本。</summary>
        private void UpdateGpuMonitorUi()
        {
            if (GpuMonitorStartButton == null || GpuMonitorStopButton == null || GpuMonitorStatusText == null)
                return;
            bool monitoring = GpuMonitorService.IsMonitoring;
            GpuMonitorStartButton.IsEnabled = !monitoring;
            GpuMonitorStopButton.IsEnabled = monitoring;
            GpuMonitorStatusText.Text = GpuMonitorService.GetStatusText();
        }

        private void LogEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            AppLogger.Info($"日志记录: {(LogEnabledToggle.IsOn ? "开启" : "关闭")}");
        }

        private void LogLevelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SaveSettings();
            AppLogger.Info($"日志级别变更为: {LogLevelComboBox.SelectedIndex}");
        }

        private void ProtectSensitiveToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            AppLogger.Info($"敏感信息保护: {(ProtectSensitiveToggle.IsOn ? "开启" : "关闭")}");
        }

        private void AutoCleanToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            AppLogger.Info($"自动清理: {(AutoCleanToggle.IsOn ? "开启" : "关闭")}");
        }

        private void CleanDaysNumberBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            SaveSettings();
            AppLogger.Info($"清理天数变更为: {CleanDaysNumberBox.Value}");
        }

        private void DevModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            AppLogger.Info($"开发者模式: {(DevModeToggle.IsOn ? "开启" : "关闭")}");
            UpdateDevModePanel();
        }

        // ───────────────────────── Win2D 性能监测悬浮窗 ─────────────────────────

        /// <summary>
        /// 将当前页面的悬浮窗设置同步到全局 HUD 控制器并通知 MainWindow 刷新。
        /// </summary>
        private void ApplyWin2DPerfHudSettings()
        {
            if (_isLoading) return;
            Win2DPerformanceHud.IsEnabled = Win2DPerfEnabledToggle?.IsOn ?? false;
            Win2DPerformanceHud.ShowFps = ShowFpsToggle?.IsOn ?? true;
            Win2DPerformanceHud.ShowAvgFps = ShowAvgFpsToggle?.IsOn ?? false;
            Win2DPerformanceHud.ShowFrameTime = ShowFrameTimeToggle?.IsOn ?? false;
            Win2DPerformanceHud.ShowUpdateTime = ShowUpdateTimeToggle?.IsOn ?? false;
            Win2DPerformanceHud.ShowDrawTime = ShowDrawTimeToggle?.IsOn ?? false;
            Win2DPerformanceHud.ShowFrameJitter = ShowFrameJitterToggle?.IsOn ?? false;
            Win2DPerformanceHud.ShowDroppedFrames = ShowDroppedFramesToggle?.IsOn ?? false;
            Win2DPerformanceHud.ShowMemory = ShowMemoryToggle?.IsOn ?? false;
            Win2DPerformanceHud.ShowResolution = ShowResolutionToggle?.IsOn ?? false;
            Win2DPerformanceHud.ShowGpuMode = ShowGpuModeToggle?.IsOn ?? false;
            Win2DPerformanceHud.NotifyChanged();
        }

        private void Win2DPerfEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            AppLogger.Info($"Win2D 性能监测悬浮窗: {(Win2DPerfEnabledToggle.IsOn ? "开启" : "关闭")}");
            ApplyWin2DPerfHudSettings();
        }

        private void Win2DPerfOptionToggled(object sender, RoutedEventArgs e)
        {
            // "所有"开关批量设置期间跳过：子开关事件由"所有"处理器统一保存/应用，
            // 避免连续 10 次写文件与刷新
            if (_isSyncingAllToggle) return;

            // 任一子开关变化后，同步"所有"开关的指示状态（全部开启才为开）
            if (!_isLoading)
                SyncAllTogglesState();

            SaveSettings();
            ApplyWin2DPerfHudSettings();
        }

        /// <summary>
        /// "所有"开关：一键开启时打开全部监测项，一键关闭时关闭全部监测项。
        /// </summary>
        private void Win2DAllToggles_Toggled(object sender, RoutedEventArgs e)
        {
            // 由 SyncAllTogglesState 同步指示状态触发（非用户操作）时忽略
            if (_isSyncingAllToggle) return;
            if (_isLoading) return;

            bool allOn = Win2DAllTogglesToggle.IsOn;
            _isSyncingAllToggle = true;
            try
            {
                SetWin2DOptionToggles(allOn);
            }
            finally
            {
                _isSyncingAllToggle = false;
            }
            AppLogger.Info($"Win2D 监测开关: 一键{(allOn ? "全部开启" : "全部关闭")}");
            SaveSettings();
            ApplyWin2DPerfHudSettings();
        }

        /// <summary>批量设置全部监测项开关状态（不触发子开关的保存/应用逻辑）。</summary>
        private void SetWin2DOptionToggles(bool on)
        {
            if (ShowFpsToggle != null) ShowFpsToggle.IsOn = on;
            if (ShowAvgFpsToggle != null) ShowAvgFpsToggle.IsOn = on;
            if (ShowFrameTimeToggle != null) ShowFrameTimeToggle.IsOn = on;
            if (ShowUpdateTimeToggle != null) ShowUpdateTimeToggle.IsOn = on;
            if (ShowDrawTimeToggle != null) ShowDrawTimeToggle.IsOn = on;
            if (ShowFrameJitterToggle != null) ShowFrameJitterToggle.IsOn = on;
            if (ShowDroppedFramesToggle != null) ShowDroppedFramesToggle.IsOn = on;
            if (ShowMemoryToggle != null) ShowMemoryToggle.IsOn = on;
            if (ShowResolutionToggle != null) ShowResolutionToggle.IsOn = on;
            if (ShowGpuModeToggle != null) ShowGpuModeToggle.IsOn = on;
        }

        /// <summary>
        /// 同步"所有"开关的指示状态：全部监测项开启时为开，任意一项关闭时为关。
        /// 设置 IsOn 会触发 Toggled，用 <see cref="_isSyncingAllToggle"/> 抑制级联。
        /// </summary>
        private void SyncAllTogglesState()
        {
            if (Win2DAllTogglesToggle == null) return;

            bool allOn = (ShowFpsToggle?.IsOn ?? false) &&
                         (ShowAvgFpsToggle?.IsOn ?? false) &&
                         (ShowFrameTimeToggle?.IsOn ?? false) &&
                         (ShowUpdateTimeToggle?.IsOn ?? false) &&
                         (ShowDrawTimeToggle?.IsOn ?? false) &&
                         (ShowFrameJitterToggle?.IsOn ?? false) &&
                         (ShowDroppedFramesToggle?.IsOn ?? false) &&
                         (ShowMemoryToggle?.IsOn ?? false) &&
                         (ShowResolutionToggle?.IsOn ?? false) &&
                         (ShowGpuModeToggle?.IsOn ?? false);

            if (Win2DAllTogglesToggle.IsOn != allOn)
            {
                _isSyncingAllToggle = true;
                Win2DAllTogglesToggle.IsOn = allOn;
                _isSyncingAllToggle = false;
            }
        }

        private void UpdateDevModePanel()
        {
            if (DevModeToggle == null || DevModePanel == null) return;
            if (DevModeToggle.IsOn)
            {
                DevModePanel.Visibility = Visibility.Visible;
                if (!_isDevModeSubscribed)
                {
                    AppLogger.RecentLogsChanged += OnRecentLogsChanged;
                    _isDevModeSubscribed = true;
                }
                RefreshLogStats();
                RefreshRecentLogs();
            }
            else
            {
                DevModePanel.Visibility = Visibility.Collapsed;
                if (_isDevModeSubscribed)
                {
                    AppLogger.RecentLogsChanged -= OnRecentLogsChanged;
                    _isDevModeSubscribed = false;
                }
            }
        }

        private void OnRecentLogsChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => RefreshRecentLogs());
        }

        private void RefreshLogStats()
        {
            if (LogStatsText != null)
                LogStatsText.Text = AppLogger.GetLogStats();
            // ★ 崩溃日志增强：显示上次崩溃摘要（CrashReportService 启动时生成）
            if (CrashSummaryText != null)
            {
                var summary = Services.CrashReportService.LastCrashSummary;
                if (string.IsNullOrEmpty(summary))
                {
                    CrashSummaryText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CrashSummaryText.Text = summary;
                    CrashSummaryText.Visibility = Visibility.Visible;
                }
            }
        }

        private void RefreshRecentLogs()
        {
            if (RecentLogsListView != null)
            {
                RecentLogsListView.ItemsSource = null;
                RecentLogsListView.ItemsSource = AppLogger.GetRecentLogs();
                RefreshLogStats();
            }
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            if (_isDevModeSubscribed)
            {
                AppLogger.RecentLogsChanged -= OnRecentLogsChanged;
                _isDevModeSubscribed = false;
            }
            // 页面离开后停止 GPU 状态刷新定时器，避免后台空转
            if (_gpuStatusTimer != null)
            {
                _gpuStatusTimer.Stop();
                _gpuStatusTimer = null;
            }
        }
    }
}
