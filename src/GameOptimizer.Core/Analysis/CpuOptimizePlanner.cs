namespace GameOptimizer.Core.Analysis;

using GameOptimizer.Core.Collection;

/// <summary>快速优化里可以真正执行的动作类型。</summary>
public enum CpuOptimizeKind
{
    /// <summary>结束一个高占用的用户级进程。可回滚（按原路径重新拉起）。</summary>
    EndProcess = 0,
}

/// <summary>
/// 一条待确认的优化项。
///
/// 每条都必须自带**依据**（实测数字）、**影响**、**回滚方式**——
/// 只写「结束 xxx 可优化」而不给依据，就是在让用户凭猜测操作自己的电脑。
/// </summary>
public sealed record CpuOptimizeItem(
    CpuOptimizeKind Kind,
    string Title,
    string? ProcessName,
    string Evidence,
    string Impact,
    string RevertText,
    bool CanExecute,
    string? BlockedReason,
    double MeasuredPercent);

/// <summary>一次轻量检测得到的完整建议。</summary>
public sealed record CpuOptimizePlan(
    IReadOnlyList<CpuOptimizeItem> Items,
    IReadOnlyList<string> Notes,
    string Summary)
{
    /// <summary>是否存在「可以执行、且用户勾上前不该自动做」的动作。</summary>
    public bool HasExecutableAction => Items.Any(i => i.CanExecute);

    /// <summary>只在能执行时才允许出现「执行」按钮；没有安全动作就只给诊断。</summary>
    public IReadOnlyList<CpuOptimizeItem> ExecutableItems => Items.Where(i => i.CanExecute).ToList();

    public static CpuOptimizePlan Empty(string summary) => new([], [], summary);
}

/// <summary>
/// CPU 快速优化的「轻量检测 → 建议」阶段（纯逻辑，可单测）。
///
/// 设计底线（每条都对应一条硬约束）：
///
/// 1. **CPU 高 ≠ 垃圾进程**：只把「实测占用达到阈值」的进程列为候选，
///    并在影响说明里写清「这是你自己打开的应用就请取消勾选」。
/// 2. **系统进程、安全软件、游戏本体、读不到路径的进程一律不给操作**：
///    前两类结束会伤系统/失效防护，后两类结束之后拉不回来。
/// 3. **不做优先级、亲和性、睿频、频率的改动**：这些既不可验证也不可回滚，
///    收益还无法测量，属于本项目明确排除的范围。
/// 4. **不承诺帧率**：CPU 占用下降与帧率提升之间没有必然关系，界面不能暗示。
/// 5. 没有安全动作时**明确说没有**，不为了按钮去凑动作。
/// </summary>
public static class CpuOptimizePlanner
{
    /// <summary>进入「可结束」候选的最低占用。低于它属于噪声，不值得为它关一个应用。</summary>
    public const double MinimumPercentToOffer = 3;

    /// <summary>最多列几项候选：一次弹十个只会让人盲选。</summary>
    public const int MaxCandidates = 3;

    /// <param name="diagnosis">当前诊断结果。无数据时不产生任何动作。</param>
    /// <param name="processes">进程占用排名（同一次采样的结果）。</param>
    /// <param name="logicalProcessors">逻辑核数，用于说明百分比口径。</param>
    public static CpuOptimizePlan Build(
        CpuDiagnosis? diagnosis,
        IReadOnlyList<ProcessCpuUsage>? processes,
        int logicalProcessors)
    {
        var notes = new List<string>
        {
            "结束进程不是「清理垃圾」：请逐项确认它不是你需要保留的应用。",
            "本工具不会修改进程优先级、CPU 亲和性、睿频或频率——这些改动不可验证也不可回滚。",
            "CPU 占用下降不代表游戏帧率提升，本页不承诺任何帧率收益。",
        };

        if (diagnosis is null || diagnosis.Level == CpuLoadLevel.Unknown)
        {
            notes.Insert(0, "CPU 数据不可用，未生成任何优化动作；采集恢复后可重新检测。");
            return new CpuOptimizePlan([], notes, "CPU 数据不可用，本次只记录状态，不做任何改动。");
        }

        var current = diagnosis.CurrentPercent;
        var list = processes ?? [];

        if (list.Count == 0)
        {
            notes.Insert(0, "进程占用列表还没有可用数据（进程占用需要两次采样），稍后重试。");
            return new CpuOptimizePlan([], notes, "进程数据尚不可用，本次不提供操作。");
        }

        var items = new List<CpuOptimizeItem>();

        foreach (var p in list.OrderByDescending(p => p.CpuPercent))
        {
            if (p.IsProtected)
            {
                if (p.CpuPercent >= MinimumPercentToOffer)
                    notes.Add($"{p.Name} 当前 {p.CpuPercent:F1}%，属{p.ProtectionReason}，不提供结束操作。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(p.ExecutablePath))
            {
                if (p.CpuPercent >= MinimumPercentToOffer)
                    notes.Add($"{p.Name} 当前 {p.CpuPercent:F1}%，但读不到可执行文件路径，" +
                              "结束后无法按原路径恢复，因此不提供操作。");
                continue;
            }

            if (p.CpuPercent < MinimumPercentToOffer) continue;
            if (items.Count >= MaxCandidates) break;

            items.Add(new CpuOptimizeItem(
                CpuOptimizeKind.EndProcess,
                Title: $"结束 {p.Name}",
                ProcessName: p.Name,
                Evidence: $"实测占用 {p.CpuPercent:F1}%（{logicalProcessors} 逻辑核口径，1 秒窗口）" +
                          (current is { } c ? $"；当前整机 {c:F0}%" : ""),
                Impact: "该应用会被关闭，未保存的内容可能丢失；如果你正在用它，请取消勾选。",
                RevertText: "可从开始菜单重新打开；本工具会尝试按原路径拉起，但不保证恢复窗口与未保存内容。",
                CanExecute: true,
                BlockedReason: null,
                MeasuredPercent: p.CpuPercent));
        }

        if (items.Count == 0)
        {
            notes.Insert(0, $"没有占用 ≥{MinimumPercentToOffer:F0}% 且可以安全结束的进程。");
            return new CpuOptimizePlan(items, notes,
                "没有可安全执行的 CPU 优化动作，本次只提供诊断与建议。");
        }

        var total = items.Sum(i => i.MeasuredPercent);
        return new CpuOptimizePlan(items, notes,
            $"检测到 {items.Count} 个高占用应用（合计约 {total:F1}%），默认不勾选，请逐项确认后再执行。");
    }
}
