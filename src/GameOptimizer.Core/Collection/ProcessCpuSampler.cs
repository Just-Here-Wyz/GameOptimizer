namespace GameOptimizer.Core.Collection;

using System.Diagnostics;
using GameOptimizer.Core.Library;

/// <summary>
/// 一个进程的 CPU 占用读数。
///
/// <paramref name="CpuPercent"/> 的口径：**该进程占用全部逻辑处理器能力的百分比**
/// （与任务管理器一致，分母是逻辑处理器数量），所以全机进程合计不会超过 100%。
/// </summary>
public sealed record ProcessCpuUsage(
    int ProcessId,
    string Name,
    double CpuPercent,
    string? ExecutablePath,
    bool IsProtected,
    string? ProtectionReason);

/// <summary>一次进程占用排名的结果。</summary>
/// <param name="IsReady">false 表示还没有可用的两次采样，数字尚不可信。</param>
/// <param name="Note">给界面直接显示的状态说明（采样中 / 读不到几个）。</param>
public sealed record ProcessCpuRanking(
    IReadOnlyList<ProcessCpuUsage> Top,
    int WatchedCount,
    int UnreadableCount,
    int ElapsedMs,
    bool IsReady,
    string Note)
{
    public static ProcessCpuRanking NotReady(string note)
        => new([], 0, 0, 0, false, note);
}

/// <summary>
/// 进程级 CPU 占用采集。
///
/// **数据来源**：`Process.TotalProcessorTime` 的差分 ÷ 墙钟时间差 ÷ 逻辑处理器数。
/// 这是 Windows 上唯一不需要驱动、不需要 ETW、不需要注入进程的可靠口径，
/// 也是任务管理器同一套语义。
///
/// **为什么不用 PDH `Process\% Processor Time`**：那需要为每个进程实例建计数器，
/// 实例名会随 PID 变化反复失效，本机实测重建开销远高于一次差分。
///
/// **限制（必须在界面说明，不得掩盖）**：
/// · 受保护进程（PPL、反作弊、其他账户）读不到 `TotalProcessorTime`，会计入「读不到」而不是当作 0%；
/// · 第一次采样没有差分基准，必须等第二次才出数字——此时界面显示「采样中」；
/// · 瞬时值受采样间隔影响，1 秒窗口内短促的尖峰会被摊平。
/// </summary>
public sealed class ProcessCpuSampler
{
    /// <summary>低于这个百分比不进列表：0.3% 这类噪声列出来只会干扰判断。</summary>
    public const double MinimumPercentToShow = 1.0;

    /// <summary>分母：逻辑处理器数量。与 <see cref="Environment.ProcessorCount"/> 同源。</summary>
    public int LogicalProcessorCount { get; } = Math.Max(1, Environment.ProcessorCount);

    private readonly Dictionary<int, Baseline> _baseline = [];
    private IReadOnlySet<string>? _serviceNames;
    private long? _lastSampleMs;

    private readonly record struct Baseline(string Name, TimeSpan TotalProcessorTime, long TimestampMs);

    /// <summary>
    /// 进程 CPU 百分比 = 处理器时间增量 ÷ 墙钟时间 ÷ 逻辑处理器数 × 100。
    ///
    /// 纯函数，可单测：非正的墙钟间隔返回 0（而不是无穷大），结果钳制在 [0,100]。
    /// </summary>
    public static double ToPercent(TimeSpan deltaProcessorTime, long deltaWallMs, int logicalProcessors)
    {
        if (logicalProcessors <= 0) logicalProcessors = 1;
        if (deltaWallMs <= 0) return 0;

        var percent = deltaProcessorTime.TotalMilliseconds / deltaWallMs * 100.0 / logicalProcessors;
        if (double.IsNaN(percent) || double.IsInfinity(percent)) return 0;
        return Math.Clamp(percent, 0, 100);
    }

    /// <summary>清空差分基准。页面关闭或采集重启时调用，避免跨会话的陈旧基准。</summary>
    public void Reset()
    {
        _baseline.Clear();
        _lastSampleMs = null;
    }

    /// <summary>
    /// 采一帧排名。
    /// </summary>
    /// <param name="timestampMs">本帧时间戳（毫秒）。</param>
    /// <param name="gameProcessStems">已安装游戏的 exe 主名：游戏本体一律不进「可结束」候选。</param>
    /// <param name="top">最多返回几项。</param>
    public ProcessCpuRanking Sample(
        long timestampMs,
        IReadOnlySet<string>? gameProcessStems = null,
        int top = 6)
    {
        var hadBaseline = _lastSampleMs is not null && _baseline.Count > 0;
        var elapsed = _lastSampleMs is { } last ? (int)(timestampMs - last) : 0;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            // 枚举失败必须可见：静默返回空列表会让人以为「没有进程占 CPU」
            return ProcessCpuRanking.NotReady($"进程枚举失败：{ex.Message}");
        }

        var next = new Dictionary<int, Baseline>(processes.Length);
        var measured = new List<(int Pid, string Name, double Percent)>(processes.Length);
        var unreadable = 0;

        foreach (var p in processes)
        {
            try
            {
                var pid = p.Id;
                var name = p.ProcessName;

                TimeSpan total;
                try { total = p.TotalProcessorTime; }
                catch
                {
                    // 受保护进程读不到处理器时间：计数，不当 0%
                    unreadable++;
                    continue;
                }

                next[pid] = new Baseline(name, total, timestampMs);

                if (!hadBaseline) continue;
                if (!_baseline.TryGetValue(pid, out var prev)) continue;
                // PID 复用：同名才算同一个进程，否则这一帧的差值毫无意义
                if (!string.Equals(prev.Name, name, StringComparison.Ordinal)) continue;

                var percent = ToPercent(total - prev.TotalProcessorTime, timestampMs - prev.TimestampMs,
                    LogicalProcessorCount);

                if (percent >= MinimumPercentToShow) measured.Add((pid, name, percent));
            }
            catch
            {
                unreadable++;
            }
            finally
            {
                p.Dispose();
            }
        }

        _baseline.Clear();
        foreach (var kv in next) _baseline[kv.Key] = kv.Value;
        _lastSampleMs = timestampMs;

        if (!hadBaseline)
            return new ProcessCpuRanking([], next.Count, unreadable, 0, false,
                "采样中（进程占用需要两次采样才能算出）");

        var topList = new List<ProcessCpuUsage>();
        foreach (var item in measured.OrderByDescending(m => m.Percent).Take(Math.Max(0, top)))
            topList.Add(Describe(item.Pid, item.Name, item.Percent, gameProcessStems));

        var note = topList.Count == 0
            ? $"未发现占用 ≥{MinimumPercentToShow:F0}% 的进程（统计 {next.Count} 个" +
              (unreadable > 0 ? $"，{unreadable} 个读不到" : "") + "）"
            : $"统计 {next.Count} 个进程" +
              (unreadable > 0 ? $"，{unreadable} 个读不到（可能受系统保护）" : "");

        return new ProcessCpuRanking(topList, next.Count, unreadable, elapsed, true, note);
    }

    /// <summary>补齐路径与「能否结束」判定。单独取详情只对进榜的几项做，避免全量读 MainModule。</summary>
    private ProcessCpuUsage Describe(
        int pid, string name, double percent, IReadOnlySet<string>? gameProcessStems)
    {
        _serviceNames ??= EssentialProcessGuard.LoadServiceImageNames();

        string? path = null;
        var session = -1;

        try
        {
            using var p = Process.GetProcessById(pid);
            session = p.SessionId;
            path = p.MainModule?.FileName;
        }
        catch
        {
            // 反作弊 / 高权限进程读不到路径：保持 null，后面据此判定「无法自动恢复」
        }

        var isSelf = pid == Environment.ProcessId;
        var isGame = BackgroundCandidateScan.MatchesGame(name, gameProcessStems);

        var identity = new EssentialProcessGuard.ProcessIdentity(name, session, path);
        var essential = EssentialProcessGuard.IsEssential(identity, _serviceNames);

        var reason = isSelf ? "本程序自身"
            : isGame ? "游戏本体（结束后无法恢复游戏内进度）"
            : !essential ? null
            : session == 0 ? "系统服务会话"
            : EssentialProcessGuard.IsUnderSystemRoot(path,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ? "Windows 系统组件"
            : "系统必要进程或安全软件";

        return new ProcessCpuUsage(pid, name, percent, path, isSelf || isGame || essential, reason);
    }
}
