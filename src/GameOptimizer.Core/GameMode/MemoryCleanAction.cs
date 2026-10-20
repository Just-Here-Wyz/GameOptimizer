namespace GameOptimizer.Core.GameMode;

using GameOptimizer.Core.Library;

/// <summary>清理失败的结构化原因。UI 据此给出「需要管理员权限」这类明确提示，而不是笼统报错。</summary>
public enum MemoryCleanFailureReason
{
    /// <summary>成功（未抛异常）。</summary>
    None = 0,

    /// <summary>进程列表枚举不到，等于什么都没做。</summary>
    EnumerationFailed = 1,

    /// <summary>所有可清理的目标都拒绝访问——典型原因是没以管理员身份运行。</summary>
    ElevationRequired = 2,

    /// <summary>API 调用本身失败。</summary>
    ApiFailed = 3,

    /// <summary>API 报告成功，但回读发现工作集根本没降——不能报成功。</summary>
    ReadbackMismatch = 4,
}

/// <summary>内存清理失败。带结构化原因，界面能区分「没权限」与「接口失败」。</summary>
public sealed class MemoryCleanException(MemoryCleanFailureReason reason, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public MemoryCleanFailureReason Reason { get; } = reason;
}

/// <summary>单个进程的释放明细：清理前后各多少、结果如何。</summary>
public sealed record MemoryReleaseEntry(
    int ProcessId,
    string Name,
    long BeforeBytes,
    long AfterBytes,
    ReleaseStatus Status,
    string? Reason = null)
{
    /// <summary>这个进程减少了多少（失败项恒为 0，不虚报）。</summary>
    public long ReleasedBytes => Math.Max(0, BeforeBytes - AfterBytes);

    /// <summary>API 成功且回读确实降了，才算真的释放。</summary>
    public bool Verified => Status == ReleaseStatus.Released && AfterBytes < BeforeBytes;

    /// <summary>API 说成功，但回读没降——静默失败，必须计入失败而不是成功。</summary>
    public bool SilentNoOp => Status == ReleaseStatus.Released && AfterBytes >= BeforeBytes;
}

/// <summary>
/// 一次内存清理的完整结果。
///
/// 两个数字口径必须分开写清楚，否则用户会以为「释放了多少」是系统可用内存的变化：
/// · <see cref="VerifiedReleasedBytes"/>：各进程工作集实际减少量之和，这是本动作直接造成的；
/// · <see cref="SystemAvailableDeltaBytes"/>：系统可用内存的前后差，含其他进程的并发干扰，只作参考。
/// </summary>
public sealed class MemoryReleaseReport
{
    internal MemoryReleaseReport(
        long availableBeforeBytes,
        long availableAfterBytes,
        IReadOnlyList<MemoryReleaseEntry> entries,
        int scanned,
        int skipped)
    {
        AvailableBeforeBytes = availableBeforeBytes;
        AvailableAfterBytes = availableAfterBytes;
        Entries = entries;
        Scanned = scanned;
        Skipped = skipped;
    }

    /// <summary>清理前系统可用物理内存（字节）。0 表示读不到。</summary>
    public long AvailableBeforeBytes { get; }

    /// <summary>清理后系统可用物理内存（字节）。0 表示读不到。</summary>
    public long AvailableAfterBytes { get; }

    /// <summary>系统可用内存变化量（字节）。含并发干扰，界面必须标注是参考值。</summary>
    public long SystemAvailableDeltaBytes =>
        AvailableBeforeBytes > 0 && AvailableAfterBytes > 0 ? AvailableAfterBytes - AvailableBeforeBytes : 0;

    /// <summary>系统可用内存有没有测到。测不到就不许展示差值。</summary>
    public bool HasSystemMeasurement => AvailableBeforeBytes > 0 && AvailableAfterBytes > 0;

    /// <summary>参与尝试的进程明细。</summary>
    public IReadOnlyList<MemoryReleaseEntry> Entries { get; }

    /// <summary>枚举到的进程总数。</summary>
    public int Scanned { get; }

    /// <summary>按策略主动跳过的进程数（系统必要进程、前台进程、占用太小、本程序自身）。</summary>
    public int Skipped { get; }

    public int Attempted => Entries.Count;
    public int VerifiedCount => Entries.Count(e => e.Verified);
    public int DeniedCount => Entries.Count(e => e.Status == ReleaseStatus.AccessDenied);
    public int FailedCount => Entries.Count(e => e.Status == ReleaseStatus.ApiFailed);
    public int VanishedCount => Entries.Count(e => e.Status == ReleaseStatus.Vanished);
    public int NoEffectCount => Entries.Count(e => e.SilentNoOp);

    /// <summary>本动作直接释放的内存（已验证）。这是各进程工作集减少量之和。</summary>
    public long VerifiedReleasedBytes => Entries.Where(e => e.Verified).Sum(e => e.ReleasedBytes);

    /// <summary>
    /// 应当展示给用户的收益数字。
    ///
    /// 为什么不能直接用 <see cref="VerifiedReleasedBytes"/>：实测两者差一个数量级。
    /// 本机一次清理：逐进程工作集减少合计 2.9 GB，但系统可用内存只涨了 461 MB。
    /// 原因是大量页面被多个进程共享（同一份 DLL / .NET 运行时），
    /// 每个进程都把自己的那份算一遍，就会重复计数。
    /// 拿 2.9 GB 去展示等于虚假宣传，所以能测到系统可用内存时以它为准。
    /// </summary>
    public long ReportedReleasedBytes => HasSystemMeasurement
        ? Math.Max(0, SystemAvailableDeltaBytes)
        : VerifiedReleasedBytes;

    /// <summary>收益数字是否来自系统可用内存的实测（false 表示退化为逐进程口径）。</summary>
    public bool ReportedFromSystem => HasSystemMeasurement;

    /// <summary>没有任何进程需要清理（内存本身充裕）。此时不是失败，如实说明即可。</summary>
    public bool NothingToDo => Attempted == 0;

    /// <summary>
    /// 形如「已释放 461 MB（26 个进程）」。
    /// 失败与跳过如实附在后面，绝不只说好的一半；
    /// 两个口径差异明显时额外说明，避免用户对着任务管理器觉得数字对不上。
    /// </summary>
    public string Summary
    {
        get
        {
            if (NothingToDo) return "没有需要释放的进程（内存已足够充裕）";

            if (VerifiedCount == 0)
            {
                var nothing = "未能释放内存";
                if (DeniedCount > 0) nothing += $"，{DeniedCount} 个进程拒绝访问";
                if (FailedCount > 0) nothing += $"，{FailedCount} 个进程调用失败";
                return nothing;
            }

            var text = ReportedFromSystem
                ? $"已释放 {MemoryReleaseText.Format(ReportedReleasedBytes)}（{VerifiedCount} 个进程）"
                : $"已释放 {MemoryReleaseText.Format(VerifiedReleasedBytes)}（{VerifiedCount} 个进程，系统可用内存读数不可用）";

            if (DeniedCount > 0) text += $"，{DeniedCount} 个进程拒绝访问";
            if (FailedCount > 0) text += $"，{FailedCount} 个进程调用失败";
            if (NoEffectCount > 0) text += $"，{NoEffectCount} 个进程回读无变化";

            return text;
        }
    }
}

/// <summary>内存量的可读格式化。纯函数，界面与测试共用同一口径。</summary>
public static class MemoryReleaseText
{
    private const double Kb = 1024.0;
    private const double Mb = 1024.0 * 1024.0;
    private const double Gb = 1024.0 * 1024.0 * 1024.0;

    /// <summary>
    /// 字节 → 「1.2 GB」/「236 MB」/「512 KB」/「0 B」。
    /// GB 保留一位小数（用户要的就是「已释放 1.2 GB」这种量级），MB 取整避免假精度。
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes >= Gb) return $"{bytes / Gb:F1} GB";
        if (bytes >= Mb) return $"{bytes / Mb:F0} MB";
        if (bytes >= Kb) return $"{bytes / Kb:F0} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// 界面上那句「已释放 X」。
    ///
    /// 放在 Core 而不是 ViewModel：这是「报什么数字」的判定，属于本项目的核心口径，
    /// 必须能单测。放 App 层就测不到了（测试项目不引用 App）。
    ///
    /// 展示量优先用系统可用内存的实测差（<see cref="MemoryReleaseReport.ReportedReleasedBytes"/>）：
    /// 逐进程合计会把共享页面重复计数，实测两者差 6 倍（2.9 GB vs 461 MB），
    /// 拿大的那个展示就是虚假宣传。
    /// </summary>
    public static string DescribeResult(MemoryReleaseReport report)
    {
        if (report.NothingToDo)
            return "内存已经很充裕，没有需要释放的进程。";

        if (report.VerifiedCount == 0)
            return report.Summary; // 有尝试但没释放到：把真实情况说清楚，别粉饰

        if (!report.ReportedFromSystem)
            return report.Summary; // 系统读数不可用，Summary 里已注明退化口径

        // 系统可用内存没涨：页面只是挪到了备用列表，或被其他进程同时吃掉。
        // 这种情况必须如实说明，不能挂着「已释放 0 B」让人以为坏了。
        if (report.ReportedReleasedBytes <= 0)
            return "已回收闲置页面，但系统可用内存未明显变化（有其他进程同时占用）";

        var text = $"已释放 {Format(report.ReportedReleasedBytes)}（{report.VerifiedCount} 个进程）";

        if (report.DeniedCount > 0) text += $"；{report.DeniedCount} 个进程需管理员权限";
        if (report.FailedCount > 0) text += $"；{report.FailedCount} 个进程调用失败";

        return text;
    }
}

/// <summary>清理策略。默认值都是「宁可少清，不要造成卡顿」。</summary>
public sealed class MemoryCleanOptions
{
    /// <summary>低于这个工作集的进程直接跳过：清它省不下多少，反而多一次系统调用。</summary>
    public long MinWorkingSetBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>是否保护当前前台窗口进程（用户正在看的程序被清工作集，会明显卡一下）。</summary>
    public bool ProtectForegroundProcess { get; init; } = true;

    /// <summary>前台窗口进程 ID 的取法。可注入，便于测试。</summary>
    public Func<int?> ForegroundProcessId { get; init; } = ForegroundProcess.GetId;

    /// <summary>本程序自己的 PID。清理自己会让界面在重绘时缺页卡顿，一律跳过。</summary>
    public int SelfProcessId { get; init; } = Environment.ProcessId;

    /// <summary>已注册服务的主程序名集合。为 null 时由 <see cref="EssentialProcessGuard"/> 现场加载。</summary>
    public IReadOnlySet<string>? ServiceNames { get; init; }
}

/// <summary>
/// 释放闲置工作集：把物理内存里「暂时用不到的页」置换到虚拟内存，进程继续运行。
///
/// 与 <see cref="EndProcessAction"/> 的区别必须写清楚：
/// · 结束进程不可逆，用户没保存的东西就没了；
/// · 这里只动工作集，进程、句柄、内存内容全都在，下次访问只是多一次缺页，
///   最坏情况是卡顿一下，不会丢数据。因此 <see cref="Revert"/> 是空操作——
///   放出去的页会随着进程继续运行自然回到内存，没有「恢复」这回事。
///
/// 实现走 <see cref="IWorkingSetReleaser"/>，接口化是为了让「权限不足」「API 失败」
/// 「回读无变化」这三条真实机器上难以复现的路径都能被单测覆盖。
/// </summary>
public sealed class MemoryCleanAction : IGameModeAction
{
    private readonly IWorkingSetReleaser _releaser;
    private readonly MemoryCleanOptions _options;

    public MemoryCleanAction(IWorkingSetReleaser? releaser = null, MemoryCleanOptions? options = null)
    {
        _releaser = releaser ?? new Win32WorkingSetReleaser();
        _options = options ?? new MemoryCleanOptions();
    }

    public string Name => "释放闲置内存";
    public string Description => "遍历进程释放闲置工作集，把物理内存置换到虚拟内存；不结束任何进程，无需还原。";

    /// <summary>最近一次清理结果。未执行过为 null。</summary>
    public MemoryReleaseReport? Report { get; private set; }

    /// <summary>最近一次失败原因；成功为 null。界面据此给出「需要管理员权限」这类明确提示。</summary>
    public MemoryCleanFailureReason FailureReason { get; private set; }

    public string? FailureMessage { get; private set; }

    /// <summary>最近一次是否真的释放出了可验证的内存。内存会随使用自然涨回去，这是时点事实。</summary>
    public bool IsActive() => Report?.VerifiedReleasedBytes > 0;

    public void Apply()
    {
        Report = null;
        FailureReason = MemoryCleanFailureReason.None;
        FailureMessage = null;

        var availableBefore = SafeAvailable();

        IReadOnlyList<ProcessMemorySnapshot> processes;
        try
        {
            processes = _releaser.EnumerateProcesses();
        }
        catch (Exception ex)
        {
            throw Fail(MemoryCleanFailureReason.EnumerationFailed,
                $"无法枚举进程，内存未做任何处理：{ex.Message}", ex);
        }

        var skipped = 0;
        var entries = new List<MemoryReleaseEntry>();

        foreach (var p in processes)
        {
            if (ShouldSkip(p, ref skipped)) continue;

            var outcome = _releaser.Release(p.ProcessId);
            var status = outcome.Status;
            var reason = outcome.Message;

            // 回读：API 说成功不算成功，必须重新读一次工作集。
            // 读不到（进程已退出）就退回清理前的值，并如实标成「已退出」——
            // 既不算我们释放的，也不能假装清理过。
            var after = _releaser.TryGetWorkingSet(p.ProcessId);
            if (after is null)
            {
                status = ReleaseStatus.Vanished;
                reason ??= "回读时进程已退出";
                after = p.WorkingSetBytes;
            }

            entries.Add(new MemoryReleaseEntry(p.ProcessId, p.Name, p.WorkingSetBytes, after.Value, status, reason));
        }

        var availableAfter = SafeAvailable();
        var report = new MemoryReleaseReport(availableBefore, availableAfter, entries, processes.Count, skipped);
        Report = report;

        Verify(report);
    }

    /// <summary>
    /// 无操作，且必须明确记录理由：工作集释放不是状态改动，没有可回滚的内容。
    /// 放出去的页随进程继续运行自然回到内存，强行「恢复」既不必要也做不到。
    /// </summary>
    public void Revert()
    {
        // 空实现是设计决定，不是遗漏。崩溃快照也因此不需要记录本动作。
    }

    /// <summary>回读校验：没有一句「已释放」可以建立在推测之上。</summary>
    private void Verify(MemoryReleaseReport report)
    {
        if (report.NothingToDo) return; // 没有候选进程 = 内存本来就充裕，如实说明即可
        if (report.VerifiedCount > 0) return; // 至少有一个进程确实降了，交给界面展示明细

        if (report.DeniedCount == report.Attempted)
        {
            throw Fail(MemoryCleanFailureReason.ElevationRequired,
                $"尝试的 {report.Attempted} 个进程全部拒绝访问，内存未释放。请以管理员身份运行本程序后重试。");
        }

        if (report.NoEffectCount > 0)
        {
            throw Fail(MemoryCleanFailureReason.ReadbackMismatch,
                $"接口报告成功，但回读 {report.NoEffectCount} 个进程的工作集均未下降，不能认定释放成功。");
        }

        throw Fail(MemoryCleanFailureReason.ApiFailed,
            $"内存释放接口调用失败（{report.FailedCount} 个进程），未释放任何内存。");
    }

    private MemoryCleanException Fail(MemoryCleanFailureReason reason, string message, Exception? inner = null)
    {
        FailureReason = reason;
        FailureMessage = message;
        return new MemoryCleanException(reason, message, inner);
    }

    /// <summary>是否跳过这个进程。<paramref name="skipped"/> 累加跳过计数。</summary>
    private bool ShouldSkip(ProcessMemorySnapshot p, ref int skipped)
    {
        if (p.ProcessId <= 4) { skipped++; return true; }               // System / Idle
        if (p.ProcessId == _options.SelfProcessId) { skipped++; return true; } // 动自己会让界面缺页卡顿
        if (p.WorkingSetBytes < _options.MinWorkingSetBytes) { skipped++; return true; }

        var identity = new EssentialProcessGuard.ProcessIdentity(p.Name, p.SessionId, p.ExecutablePath);
        if (EssentialProcessGuard.IsEssential(identity, _options.ServiceNames)) { skipped++; return true; }

        if (_options.ProtectForegroundProcess)
        {
            var foreground = _options.ForegroundProcessId();
            if (foreground == p.ProcessId) { skipped++; return true; }
        }

        return false;
    }

    /// <summary>系统可用内存读不到时返回 0（未知），由界面决定不展示差值。</summary>
    private long SafeAvailable()
    {
        try { return _releaser.GetAvailablePhysicalBytes(); }
        catch { return 0; }
    }
}
