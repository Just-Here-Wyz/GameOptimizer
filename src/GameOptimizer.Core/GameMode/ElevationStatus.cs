namespace GameOptimizer.Core.GameMode;

using System.Security.Principal;

/// <summary>
/// 管理员权限的检测与提示文案。
///
/// 为什么单独抽出来：内存清理、电源计划、v2.1 的网络参数都要提权，
/// 而「提权了但目标进程仍受保护」与「压根没提权」是完全不同的两件事——
/// 前者该如实说明限制，后者该明确提示去提权。混成一句「失败」用户无从下手。
/// </summary>
public static class ElevationStatus
{
    /// <summary>当前进程是否以管理员身份运行。读不到身份时按「未提权」处理，宁可多提示。</summary>
    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 内存清理失败原因 → 用户能据以行动的提示文案。纯函数，可单测。
    /// </summary>
    public static string DescribeMemoryCleanFailure(MemoryCleanFailureReason reason, bool elevated) => reason switch
    {
        MemoryCleanFailureReason.ElevationRequired => elevated
            // 已提权还被全部拒绝：这些进程受系统保护，再提权也没用，别让用户白折腾
            ? "目标进程受系统保护，即使管理员权限也无法释放其工作集。"
            : "需要管理员权限：请退出程序后，右键选择「以管理员身份运行」再试。",

        MemoryCleanFailureReason.EnumerationFailed =>
            "无法读取进程列表，本次未做任何改动。",

        MemoryCleanFailureReason.ApiFailed =>
            "释放接口调用失败，本次未释放任何内存。",

        MemoryCleanFailureReason.ReadbackMismatch =>
            "接口报告成功，但回读发现内存实际没有下降，已判定为失败。",

        _ => "内存未释放。",
    };
}
