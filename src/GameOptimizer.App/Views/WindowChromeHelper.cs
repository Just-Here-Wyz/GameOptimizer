namespace GameOptimizer.App.Views;

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

/// <summary>
/// 无系统白边窗口的公共做法：WindowStyle=None + 自绘边框，
/// 最大化时按屏幕工作区补偿，避免盖住任务栏。
///
/// 参考图（GearPower）就是这种窗体：一条深色标题栏、圆角、没有系统白框。
/// </summary>
public static class WindowChromeHelper
{
    /// <summary>窗口圆角半径（像素）。</summary>
    public const int CornerRadius = 12;

    /// <summary>可缩放边框的命中宽度。</summary>
    public const int ResizeBorder = 6;

    /// <summary>
    /// 让窗口在最大化时贴合工作区而不是整个屏幕。
    /// WindowStyle=None 的窗口默认会覆盖任务栏，必须手动补偿。
    /// </summary>
    public static void ApplyMaximizeFix(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(WndProc);
        };
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        // MINMAXINFO.ptMaxPosition / ptMaxSize 是给最大化用的
        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
        mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;

        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
