namespace GameOptimizer.App.ViewModels;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

/// <summary>
/// 可执行文件路径 → 应用图标的提取与缓存。
///
/// 进程列表要显示应用自己的图标：用 shell32 的 SHGetFileInfo 从进程可执行文件提取
/// （与任务管理器同源，不引入 System.Drawing 依赖），读不到时界面退化为「首字符色块」。
/// 图标按路径缓存，扫描一次即可。
/// </summary>
public static class AppIconCache
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;

    /// <summary>按路径缓存的图标：扫描阶段已经拿到路径，不必再按进程名反查。</summary>
    private static readonly Dictionary<string, ImageSource?> PathCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 直接按可执行文件路径取图标。路径为空/读不到时返回 null，
    /// 界面退化为「首字符色块」，不影响整行显示。
    /// </summary>
    public static ImageSource? ForPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;

        if (PathCache.TryGetValue(executablePath, out var cached)) return cached;

        ImageSource? icon = null;
        try
        {
            if (System.IO.File.Exists(executablePath)) icon = Extract(executablePath);
        }
        catch
        {
            // 受保护目录 / 文件被独占：图标读不到不影响任何功能
        }

        PathCache[executablePath] = icon;
        return icon;
    }

    /// <summary>从 exe 提取 32px 图标并转成 WPF 可用的 ImageSource。</summary>
    private static ImageSource? Extract(string exePath)
    {
        var info = new Shfileinfo();
        var ok = SHGetFileInfo(exePath, 0, ref info, (uint)Marshal.SizeOf<Shfileinfo>(),
            ShgfiIcon | ShgfiLargeIcon);

        // SHGetFileInfo 的返回值只表示「成功 / 失败」，真正的图标句柄在 info.hIcon 里。
        // 以前错把返回值当句柄传给 CreateBitmapSourceFromHIcon，必然失败并退化成首字符块。
        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            // 句柄是调用方负责释放的，不还回去会慢慢泄 GDI 句柄
            DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Shfileinfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref Shfileinfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
