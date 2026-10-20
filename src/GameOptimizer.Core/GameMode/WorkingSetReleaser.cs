namespace GameOptimizer.Core.GameMode;

using System.Diagnostics;
using System.Runtime.InteropServices;
using GameOptimizer.Core.Collection;
using GameOptimizer.Core.Library;

/// <summary>
/// 进程内存快照：清理策略需要的全部客观信息。
/// 抽成纯数据是为了让「要不要清理这个进程」能在没有真实进程的环境里单测
/// ——直接对 Process 断言既慢又不稳定（同 EssentialProcessGuard.ProcessIdentity 的做法）。
/// </summary>
public sealed record ProcessMemorySnapshot(
    int ProcessId,
    string Name,
    int SessionId,
    long WorkingSetBytes,
    string? ExecutablePath);

/// <summary>一次释放尝试的结果。</summary>
public enum ReleaseStatus
{
    /// <summary>API 返回成功。注意：这只代表调用成功，是否真的少了页面由调用方回读确认。</summary>
    Released = 0,

    /// <summary>OpenProcess 被拒绝（多为他账户 / 高权限 / 受保护进程）。</summary>
    AccessDenied = 1,

    /// <summary>API 调用失败（返回 false），错误码见 <see cref="ReleaseOutcome.Win32Error"/>。</summary>
    ApiFailed = 2,

    /// <summary>枚举之后进程已退出：既不算成功，也不算失败。</summary>
    Vanished = 3,
}

/// <summary>释放结果。带上 Win32 错误码与可读原因，失败才解释得清。</summary>
public readonly record struct ReleaseOutcome(ReleaseStatus Status, int Win32Error = 0, string? Message = null);

/// <summary>
/// 工作集释放的底层通道。抽成接口有两个目的：
/// 1. 权限不足 / API 失败这两条路径在真实机器上很难稳定复现，靠假实现才能测；
/// 2. v2.1 的优先级、CPU 亲和性等动作可以复用同一套「枚举 + 逐进程操作 + 回读」模式。
/// </summary>
public interface IWorkingSetReleaser
{
    /// <summary>当前可用物理内存（字节）。读不到返回 0，调用方必须当成「未知」而不是 0 字节。</summary>
    long GetAvailablePhysicalBytes();

    /// <summary>枚举全部进程。失败直接抛，由调用方包装成可读错误。</summary>
    IReadOnlyList<ProcessMemorySnapshot> EnumerateProcesses();

    /// <summary>释放指定进程的工作集（不结束进程）。</summary>
    ReleaseOutcome Release(int processId);

    /// <summary>
    /// 回读进程当前工作集。进程已退出 / 读不到时返回 null——
    /// 调用方必须把「读不到」当成无法验证，不能当成 0 或成功。
    /// </summary>
    long? TryGetWorkingSet(int processId);
}

/// <summary>
/// Win32 实现：SetProcessWorkingSetSizeEx(min=-1,max=-1) + EmptyWorkingSet。
///
/// 两个 API 都只是「让进程自己把暂时用不到的页面交出去」，进程本身照常运行，
/// 与 EndProcessAction 的「结束进程」是两件完全不同的事——
/// 结束进程不可逆，这里最坏的情况只是下次访问时发生一次缺页。
///
/// 权限：调整**其他**进程的工作集需要对该进程有 PROCESS_SET_QUOTA。
/// 普通权限只能动本账户、同样未提权的进程；提权后才能覆盖
/// 服务、高权限进程与其他账户的进程，被拒绝时返回 AccessDenied 而不是抛异常。
/// </summary>
public sealed class Win32WorkingSetReleaser : IWorkingSetReleaser
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessSetQuota = 0x0100;

    /// <summary>允许把工作集缩到 0 的硬限制解除标志（与 PCL2 的实现一致）。</summary>
    private const uint QuotaLimitsHardWsMinDisable = 0x0002;
    private const uint QuotaLimitsHardWsMaxDisable = 0x0008;

    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize, uint flags);

    // EmptyWorkingSet 由 psapi 导出，Win7 起转发到 kernel32 的 K32EmptyWorkingSet
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public long GetAvailablePhysicalBytes() => SystemMemoryInfo.AvailablePhysicalBytes;

    public IReadOnlyList<ProcessMemorySnapshot> EnumerateProcesses()
    {
        var list = new List<ProcessMemorySnapshot>();

        // GetProcesses 自己失败时让异常冒到上层：枚举不到进程就等于什么都没做，不能报成功
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                long workingSet;
                try { workingSet = p.WorkingSet64; }
                catch { continue; } // 读不到占用的进程不碰：无法判断该不该清

                var identity = EssentialProcessGuard.Identify(p);
                list.Add(new ProcessMemorySnapshot(p.Id, identity.Name, identity.SessionId, workingSet, identity.ExecutablePath));
            }
            catch
            {
                // 单个进程读失败不影响整轮清理
            }
            finally { p.Dispose(); }
        }

        return list;
    }

    public ReleaseOutcome Release(int processId)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessSetQuota, false, processId);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return error switch
            {
                ErrorAccessDenied => new(ReleaseStatus.AccessDenied, error, "拒绝访问"),
                // ERROR_INVALID_PARAMETER：枚举之后进程已经退出，PID 不存在
                ErrorInvalidParameter => new(ReleaseStatus.Vanished, error, "进程已退出"),
                _ => new(ReleaseStatus.ApiFailed, error, $"OpenProcess 失败（错误码 {error}）"),
            };
        }

        try
        {
            // min = max = -1：解除硬限制并让系统尽量回收，是「置换到虚拟内存」的标准写法
            var flags = QuotaLimitsHardWsMinDisable | QuotaLimitsHardWsMaxDisable;
            if (SetProcessWorkingSetSizeEx(handle, new IntPtr(-1), new IntPtr(-1), flags))
                return new(ReleaseStatus.Released);

            var setError = Marshal.GetLastWin32Error();

            // 部分受保护进程不认 SetProcessWorkingSetSizeEx，但接受 EmptyWorkingSet，补一次
            if (EmptyWorkingSet(handle)) return new(ReleaseStatus.Released);

            var emptyError = Marshal.GetLastWin32Error();
            return new(ReleaseStatus.ApiFailed, emptyError,
                $"SetProcessWorkingSetSizeEx 失败（{setError}），EmptyWorkingSet 失败（{emptyError}）");
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>回读工作集。进程已退出或读不到时返回 null，调用方据此判定「无法验证」。</summary>
    public long? TryGetWorkingSet(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return p.WorkingSet64;
        }
        catch
        {
            // ArgumentException：进程已退出；其余为权限/句柄问题
            return null;
        }
    }
}

/// <summary>前台窗口进程的取法。取不到时返回 null，不猜。</summary>
public static class ForegroundProcess
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>当前前台窗口所属进程 ID。没有前台窗口或调用失败时返回 null。</summary>
    public static int? GetId()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == 0 ? null : (int)pid;
        }
        catch
        {
            return null;
        }
    }
}
