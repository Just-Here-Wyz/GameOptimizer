namespace GameOptimizer.Core.Library;

using System.Runtime.InteropServices;

/// <summary>
/// 一次枚举出「拥有顶层窗口的进程」。
///
/// 为什么要这么绕：白名单扫描需要判断「这个进程是不是用户开的桌面程序」。
/// 直觉写法是对每个进程读 <c>Process.MainWindowHandle</c>——但那是逐个跨进程调用，
/// 本机两百多个进程时实测要几百毫秒，个别进程还会卡住；而且它对「隐藏到托盘的窗口」
/// 返回值并不稳定，微信关到托盘后到底算不算有窗口，取决于枚举时机。
///
/// <see cref="EnumWindows"/> 一次就能拿到全部顶层窗口句柄（含不可见的），
/// 再用 <see cref="GetWindowThreadProcessId"/> 换出 PID —— 一次系统调用换来整张表。
/// </summary>
internal static class TopLevelWindowIndex
{
    /// <summary>收集拥有顶层窗口的进程 ID。</summary>
    public static HashSet<int> Collect()
    {
        var pids = new HashSet<int>();

        try
        {
            // 委托必须活到 EnumWindows 返回之后，所以用变量接住再 KeepAlive
            EnumWindowsProc callback = (hWnd, _) =>
            {
                if (GetWindowThreadProcessId(hWnd, out var pid) != 0 && pid != 0)
                    pids.Add((int)pid);
                return true;
            };

            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        catch
        {
            // 枚举失败就退化为「看不出谁有窗口」，扫描结果会少一些用户程序，
            // 但内置名单那条路仍然有效，不至于整块功能不可用
        }

        return pids;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
