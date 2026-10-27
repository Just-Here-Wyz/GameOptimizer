namespace GameOptimizer.Core.GameMode;

/// <summary>
/// 启动时对「残留快照」的处理方案：<b>只描述该做什么，不做事</b>。
///
/// 分成两步是故意的——「该不该还原电源计划」「有几条进程记录」这类判断是纯函数，
/// 可以脱离真实文件系统与 MessageBox 单测；真正动系统的那一下留给调用方。
///
/// v2.5.2 起这里不再产出任何「要不要现在恢复」的询问：启动时问一遍用户
/// 在上次关闭游戏模式时已经做过的决定，只会每一开机被点掉一次。
/// </summary>
public sealed record StartupRecoveryPlan
{
    /// <summary>什么都没有：干净启动。</summary>
    public static StartupRecoveryPlan Empty { get; } = new();

    /// <summary>崩落前正在使用的电源计划 GUID。非空即意味着系统可能还停在高性能。</summary>
    public string? PowerPlanGuid { get; init; }

    /// <summary>
    /// 还没恢复、且<b>将来还有可能恢复</b>的进程记录。
    /// 只保留记录，不自动拉起——用户什么时候恢复由他自己决定。
    /// </summary>
    public IReadOnlyList<RestoredProcess> PendingProcesses { get; init; } = [];

    /// <summary>快照读不出来时的原因（文件损坏 / 无权限）。有值即代表这份兜底信息不可信。</summary>
    public string? UnreadableReason { get; init; }

    /// <summary>电源计划是否需要还原。崩溃 / 强杀才可能为 true。</summary>
    public bool ShouldRestorePowerPlan => !string.IsNullOrWhiteSpace(PowerPlanGuid);

    public bool HasPendingProcesses => PendingProcesses.Count > 0;

    public int PendingCount => PendingProcesses.Count;

    /// <summary>是否完全无事可做（干净的启动，界面不必多说一句）。</summary>
    public bool IsClean => !ShouldRestorePowerPlan && !HasPendingProcesses && UnreadableReason is null;
}

/// <summary>
/// 把落盘快照翻译成启动方案。
///
/// 两条边界必须分清，否则会做错事：
/// <list type="bullet">
/// <item><b>电源计划</b>是程序崩溃留在系统上的改动，不是用户的待办 → 启动时直接还原；</item>
/// <item><b>进程记录</b>是「用户选了这次不恢复」的结果 → 原样保留，等他自己来点。</item>
/// </list>
/// 把后者也顺手恢复，等于替用户做决定；把前者也留着不问，等于让系统永远停在高性能。
/// </summary>
public static class StartupRecoveryPlanner
{
    /// <summary>
    /// 生成启动方案。快照为 null（没有残留）时返回 <see cref="StartupRecoveryPlan.Empty"/>。
    ///
    /// 路径缺失的记录一律丢弃：没有可执行文件路径就永远拉不起来，
    /// 留着只会让「还有 N 条未恢复」这个数字永远降不到 0——那正是反复提醒的根源。
    /// </summary>
    public static StartupRecoveryPlan Plan(GameModeSnapshot? snapshot)
    {
        if (snapshot is null) return StartupRecoveryPlan.Empty;

        var pending = new List<RestoredProcess>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in snapshot.Killed ?? [])
        {
            if (record is null || string.IsNullOrWhiteSpace(record.LaunchPath)) continue;

            var path = record.LaunchPath.Trim();
            if (!seenPaths.Add(NormalizePath(path))) continue;

            pending.Add(new RestoredProcess(record.Name, path));
        }

        return new StartupRecoveryPlan
        {
            PowerPlanGuid = string.IsNullOrWhiteSpace(snapshot.PreviousPowerPlanGuid)
                ? null
                : snapshot.PreviousPowerPlanGuid!.Trim(),
            PendingProcesses = pending,
        };
    }

    /// <summary>快照读不出来：不能装作没有残留，也不能因此让程序起不来。</summary>
    public static StartupRecoveryPlan Unreadable(string reason)
        => new() { UnreadableReason = string.IsNullOrWhiteSpace(reason) ? "原因未知" : reason };

    /// <summary>
    /// 首页状态行上的启动说明。返回空串表示这次启动没什么要交代的。
    ///
    /// 措辞刻意<b>不含任何询问</b>：这里只是陈述「发生过什么、现在还剩什么」，
    /// 要不要恢复由用户自己点首页那个按钮决定。
    /// </summary>
    /// <param name="powerPlanErrors">还原电源计划的结果；只有真的要还原时才需要传。</param>
    public static string Describe(
        StartupRecoveryPlan plan,
        IReadOnlyList<string>? powerPlanErrors = null)
    {
        if (plan.UnreadableReason is not null)
        {
            return $"启动检查：残留的游戏模式状态读不出来（{plan.UnreadableReason}），已跳过；"
                   + "若电源计划仍是「高性能」，请到系统电源选项改回。";
        }

        var parts = new List<string>();

        if (plan.ShouldRestorePowerPlan)
        {
            parts.Add(powerPlanErrors is { Count: > 0 }
                ? $"原电源计划没还原（{powerPlanErrors[0]}），记录已保留"
                : "已还原上次未正常退出时的电源计划");
        }

        if (plan.HasPendingProcesses)
        {
            parts.Add($"{plan.PendingCount} 个后台进程还没恢复，记录已保留，"
                      + "点「恢复遗留进程」自己挑");
        }

        return parts.Count == 0 ? "" : "启动检查：" + string.Join("；", parts) + "。";
    }

    /// <summary>路径规范化后再去重：`D:/Apps/a.exe` 与 `d:\apps\A.EXE` 是同一个程序。</summary>
    internal static string NormalizePath(string path)
        => path.Replace('/', '\\').Trim().TrimEnd('\\');
}
