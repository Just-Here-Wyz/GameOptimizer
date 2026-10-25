namespace GameOptimizer.Core.GameMode;

/// <summary>
/// 启动时自我提权。
///
/// 清单里已声明 requireAdministrator，正常情况下双击就会弹 UAC，这一层是冗余保险。
/// 但冗余是必要的：清单在下列路径上并不生效——被计划任务/服务以非提权令牌拉起、
/// 部分启动器忽略内嵌清单、以及用户从已提权宿主进程派生的子进程。
/// 此时界面照样能开，可游戏模式会「显示已开启、实际全被拒绝访问」，
/// 正是本项目明令禁止的假成功。所以启动时主动检查并重启自身，比事后报错更省事。
/// </summary>
public static class ElevationLauncher
{
    /// <summary>
    /// 自我重启的哨兵参数。重启后的进程带它启动，用来避免 UAC 被拒后无限重试。
    /// </summary>
    public const string RelaunchSwitch = "--elevated-relaunch";

    /// <summary>Win32 错误码：用户在 UAC 弹窗上点了「否」。</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>
    /// 是否需要以管理员身份重启自身。
    /// 已提权 → 不重启；已带哨兵（上次重启失败或用户取消）→ 不重启，避免死循环。
    /// </summary>
    public static bool ShouldRelaunch(bool isElevated, IReadOnlyList<string>? args)
    {
        if (isElevated) return false;
        return !HasRelaunchSwitch(args);
    }

    /// <summary>
    /// 当前宿主能否作为「重启目标」。
    ///
    /// 用 `dotnet GameOptimizer.App.dll` 调试运行时，`Environment.ProcessPath` 是 dotnet.exe，
    /// 照着重启等于把整个 .NET 宿主再拉一遍（还会对着 dotnet.exe 弹 UAC），
    /// 结果是调试运行时程序刚起来就自己退掉。这种情况必须跳过自提权——
    /// 开发/调试本来就不该被 UAC 打断，正式以 exe 启动时才需要。
    /// </summary>
    public static bool CanSelfRelaunch(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return false;

        var fileName = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)) return false;

        // 没有文件名（路径异常）时无法判断，宁可不重启
        return !string.IsNullOrWhiteSpace(fileName);
    }

    /// <summary>命令行里是否已带哨兵参数（判定不区分大小写，Windows 命令行习惯如此）。</summary>
    public static bool HasRelaunchSwitch(IReadOnlyList<string>? args)
    {
        if (args is null) return false;
        foreach (var a in args)
        {
            if (string.Equals(a, RelaunchSwitch, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 重启时传给新进程的参数：原参数（去掉已有哨兵）+ 哨兵。
    /// 原参数必须保留——用户可能带了自己的开关，重启后不该丢。
    /// </summary>
    public static string[] BuildRelaunchArguments(IReadOnlyList<string>? args)
    {
        var kept = new List<string>();
        if (args is not null)
        {
            foreach (var a in args)
            {
                if (string.Equals(a, RelaunchSwitch, StringComparison.OrdinalIgnoreCase)) continue;
                kept.Add(a);
            }
        }
        kept.Add(RelaunchSwitch);
        return kept.ToArray();
    }

    /// <summary>UAC 被用户取消：这不是错误，程序应继续以普通权限运行并如实提示受限项。</summary>
    public static bool IsUserCancelled(int win32ErrorCode) => win32ErrorCode == ErrorCancelled;

    /// <summary>提权成功但仍受限时的界面提示（与内存优化共用同一口径）。</summary>
    public static string NotElevatedHint =>
        "当前未以管理员身份运行：游戏模式的电源计划切换与部分进程结束会失败。请右键「以管理员身份运行」。";
}
