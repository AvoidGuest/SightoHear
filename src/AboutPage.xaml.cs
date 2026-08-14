using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SightoHear.Helpers;

namespace SightoHear
{
    public sealed partial class AboutPage : Page
    {
        private const string GitHubUrl = "https://github.com/AvoidGuest/SightoHear";

        /// <summary>
        /// 版本号最终回退时的默认显示（与当前发布版本保持一致）。
        /// 格式：语义化版本 + 括号内无点数字（四段版本号连写）。
        /// 例如 v0.2.0-beta (0200)，其中 0200 = 程序集四段版本 0.2.0.0 去掉点号。
        /// </summary>
        private const string DefaultVersionDisplay = "v0.3.2-beta (0320)";

        public AboutPage()
        {
            InitializeComponent();
            LoadVersionInfo();
        }

        /// <summary>
        /// 加载版本信息
        /// </summary>
        private void LoadVersionInfo()
        {
            try
            {
                // 尝试从程序集获取版本号
                var assembly = Assembly.GetEntryAssembly();
                if (assembly != null)
                {
                    // 语义化版本（含预发布后缀，如 0.1.0-beta），截断可能附加的提交哈希（+ 之后部分）
                    var informational = assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion;
                    if (!string.IsNullOrEmpty(informational))
                    {
                        var plusIndex = informational.IndexOf('+');
                        if (plusIndex >= 0)
                        {
                            informational = informational.Substring(0, plusIndex);
                        }
                    }

                    // 四段版本号（程序集/MSIX 格式，如 0.1.0.0）
                    var version = assembly.GetName().Version;
                    if (version != null && !string.IsNullOrEmpty(informational))
                    {
                        // 括号内为四段版本号去掉点号连写（用户指定的显示习惯）
                        string numeric = $"{version.Major}{version.Minor}{version.Build}{version.Revision}";
                        VersionCard.Description = $"v{informational} ({numeric})";
                        return;
                    }
                }

                // 回退：使用进程的主模块版本信息
                var process = Process.GetCurrentProcess();
                if (process.MainModule != null)
                {
                    var fileVersion = process.MainModule.FileVersionInfo;
                    if (!string.IsNullOrEmpty(fileVersion.FileVersion))
                    {
                        // 文件版本本身就是四段数字，去掉点号连写显示（与主显示格式一致）
                        VersionCard.Description = $"v{fileVersion.FileVersion.Replace(".", "")}";
                        return;
                    }
                }

                // 最终回退
                VersionCard.Description = DefaultVersionDisplay;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "获取版本信息失败");
                VersionCard.Description = DefaultVersionDisplay;
            }
        }

        /// <summary>
        /// 检查更新按钮点击事件
        /// </summary>
        private void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("用户点击检查更新");
            // TODO: 实现检查更新逻辑
            // 可以打开 GitHub Releases 页面或检查 API
            OpenUrl($"{GitHubUrl}/releases");
        }

        /// <summary>
        /// 打开指定URL
        /// </summary>
        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "打开URL失败");
            }
        }
    }
}