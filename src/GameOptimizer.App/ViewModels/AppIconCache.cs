namespace GameOptimizer.App.ViewModels;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

/// <summary>
/// 进程名 / exe 路径 → 应用图标的提取与缓存。
///
/// 白名单面板要显示应用图标：用 shell32 的 SHGetFileInfo 从进程可执行文件提取
/// （与任务管理器同源，不引入 System.Drawing 依赖），读不到时界面退化为「首字符色块」。
/// 图标按进程名与路径各缓存一份，扫描一次即可。
/// </summary>
public static class AppIconCache
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按路径缓存的图标：白名单扫描已经拿到路径，不必再按进程名反查。</summary>
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

    /// <summary>取进程名对应的图标；失败返回 null，界面应显示首字符色块兜底。</summary>
    public static ImageSource? For(string processName)
    {
        if (Cache.TryGetValue(processName, out var cached)) return cached;

        ImageSource? icon = null;
        try
        {
            System.Diagnostics.Process[] procs;
            try { procs = System.Diagnostics.Process.GetProcessesByName(processName); }
            catch { procs = []; }

            foreach (var p in procs)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                    {
                        icon = Extract(path);
                        if (icon is not null) break;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        Cache[processName] = icon;
        return icon;
    }

    /// <summary>从 exe 提取 32px 图标并转成 WPF 可用的 ImageSource。</summary>
    private static ImageSource? Extract(string exePath)
    {
        var info = new Shfileinfo();
        var hIcon = SHGetFileInfo(exePath, 0, ref info, (uint)Marshal.SizeOf<Shfileinfo>(),
            ShgfiIcon | ShgfiLargeIcon);
        if (hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
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
            DestroyIcon(hIcon);
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
