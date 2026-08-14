using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 将文件移入回收站的工具类（基于 SHFileOperation，支持撤销/回收站语义）。
    /// 相比 File.Delete 永久删除，移入回收站更安全，用户可随时恢复。
    /// </summary>
    public static class RecycleBinHelper
    {
        // SHFileOperation 操作类型：删除
        private const uint FO_DELETE = 0x0003;
        // 标志位组合：
        //   FOF_ALLOWUNDO（允许撤销 → 进入回收站，而不是永久删除）
        //   FOF_NOCONFIRMATION（不弹出确认对话框，由应用自行确认）
        //   FOF_SILENT（不显示进度对话框）
        //   FOF_NOERRORUI（不显示错误对话框）
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOERRORUI = 0x0400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom; // 双 null 结尾的文件路径列表
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        /// <summary>
        /// 将单个文件移入回收站。成功或文件不存在返回 true，失败返回 false。
        /// </summary>
        public static bool DeleteToRecycleBin(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = filePath + "\0\0",
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                };
                int result = SHFileOperation(ref op);
                bool success = result == 0;
                if (!success)
                    AppLogger.Warning($"移入回收站失败 (SHFileOperation={result}): {filePath}");
                return success;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"移入回收站异常: {filePath}");
                return false;
            }
        }
    }
}
