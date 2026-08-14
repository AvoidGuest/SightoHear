using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SightoHear.Models
{
    public class VideoFolderGroup
    {
        public string FolderPath { get; set; } = string.Empty;
        public int VideoCount { get; set; }

        // 缓存文件夹本地化显示名称，避免频繁调用 Win32 API
        private static readonly Dictionary<string, string> _displayNameCache = new(StringComparer.OrdinalIgnoreCase);

        public string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(FolderPath))
                    return string.Empty;

                if (_displayNameCache.TryGetValue(FolderPath, out var cachedName))
                    return cachedName;

                var name = GetLocalizedFolderName(FolderPath);
                _displayNameCache[FolderPath] = name;
                return name;
            }
        }

        public string VideoCountText => $"{VideoCount} 个视频";

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_DISPLAYNAME = 0x000000200;

        /// <summary>
        /// 获取文件夹的本地化显示名称（与文件资源管理器一致）。
        /// 如果文件夹没有本地化名称，则回退到实际文件夹名。
        /// </summary>
        private static string GetLocalizedFolderName(string folderPath)
        {
            try
            {
                if (Directory.Exists(folderPath))
                {
                    SHGetFileInfo(folderPath, 0, out SHFILEINFO psfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME);
                    if (!string.IsNullOrEmpty(psfi.szDisplayName))
                        return psfi.szDisplayName;
                }
            }
            catch { }
            return Path.GetFileName(folderPath);
        }

        public static List<VideoFolderGroup> BuildFrom(List<MediaItem> videos)
        {
            _displayNameCache.Clear();
            
            return videos
                .GroupBy(v => Path.GetDirectoryName(v.FilePath) ?? string.Empty)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .Select(g => new VideoFolderGroup
                {
                    FolderPath = g.Key,
                    VideoCount = g.Count()
                })
                .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
