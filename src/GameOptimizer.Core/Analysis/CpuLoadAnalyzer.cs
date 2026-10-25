namespace GameOptimizer.Core.Analysis;

/// <summary>
/// 一次 CPU 负载采样输入。字段为 null 表示**这一秒没采到**，不是 0。
///
/// 这条约定是整个诊断的地基：把「没采到」当成 0 会让 30 秒平均、连续负载判定
/// 全部失真，界面上还会出现「CPU 突然很闲」的假象。
/// </summary>
public sealed record CpuLoadSample(
    long TimestampMs,
    double? TotalPercent,
    IReadOnlyDictionary<int, double>? PerCorePercent = null);

/// <summary>负载结论档位。阈值的具体数值在 <see cref="CpuLoadAnalyzer"/> 里，界不另写一套。</summary>
public enum CpuLoadLevel
{
    /// <summary>采不到数据，不作任何结论。</summary>
    Unknown = 0,

    /// <summary>有数据但还不够 10 秒，判不出「持续」，只能说明在采样。</summary>
    Sampling = 1,

    /// <summary>负载正常。</summary>
    Normal = 2,

    /// <summary>总体占用持续偏高（≥60% 满 10 秒），值得关注。</summary>
    Watch = 3,

    /// <summary>总体占用持续高负载（≥80% 满 10 秒）。</summary>
    HighLoad = 4,
}

/// <summary>趋势图上的一个点。时间戳保留原始值，横坐标换算交给 <see cref="CpuTrendGeometry"/>。</summary>
public sealed record CpuTrendPoint(long TimestampMs, double Percent);

/// <summary>
/// 一颗逻辑核的繁忙记录。
/// <paramref name="IsSustained"/> 为 false 时只「记录」，界面不得据此提示「单核繁忙」。
/// </summary>
public sealed record BusyCore(int Index, double PeakPercent, double SustainedSeconds, bool IsSustained);

/// <summary>一段窗口内的统计。缺失帧数与间断次数一并给出，供界面提示数据完整性。</summary>
public sealed record CpuWindowStats(
    double? AveragePercent,
    int ValidSamples,
    int MissingSamples,
    double CoveredSeconds,
    int Breaks)
{
    public bool HasData => ValidSamples > 0;
}

/// <summary>
/// CPU 诊断结果：一次构建得到界面需要的全部文案与数据。
///
/// 所有结论都带「依据窗口」：说「持续高负载」就必须能指出连续多少秒。
/// 数据不足时宁可只说「采样中」，也不猜。
/// </summary>
public sealed record CpuDiagnosis(
    CpuLoadLevel Level,
    double? CurrentPercent,
    CpuWindowStats Window,
    double SustainedHighSeconds,
    double SustainedWatchSeconds,
    IReadOnlyList<CpuTrendPoint> Trend,
    IReadOnlyList<BusyCore> BusyCores,
    string Headline,
    IReadOnlyList<string> Advices,
    string SamplingStatus,
    string CompletenessText)
{
    /// <summary>当前占用文本；无数据时「未知」。</summary>
    public string CurrentText => CurrentPercent is { } v ? $"{v:F0}%" : "未知";

    /// <summary>30 秒平均文本；无数据时「未知」。</summary>
    public string AverageText => Window.AveragePercent is { } v
        ? $"{CpuLoadAnalyzer.TrendWindowSeconds:F0} 秒平均 {v:F0}%"
        : $"{CpuLoadAnalyzer.TrendWindowSeconds:F0} 秒平均未知";

    /// <summary>是否已经能给出明确结论（正常 / 关注 / 高负载）。</summary>
    public bool HasConclusion => Level is CpuLoadLevel.Normal or CpuLoadLevel.Watch or CpuLoadLevel.HighLoad;

    /// <summary>空诊断：采集不可用时用它，界面据此显示「未知」而不是一排 0。</summary>
    public static CpuDiagnosis Empty(string reason) => new(
        CpuLoadLevel.Unknown,
        null,
        new CpuWindowStats(null, 0, 0, 0, 0),
        0,
        0,
        [],
        [],
        "CPU 占用未知",
        [$"未采到 CPU 总占用：{reason}", "数据不可用时不下结论，也不显示 0 冒充读数。"],
        "采集不可用",
        "无有效采样");
}

/// <summary>
/// CPU 负载诊断规则（纯逻辑，可单测）。默认每秒一帧、趋势窗口 30 秒。
///
/// 判定口径（全部要求**连续有效采样**，不看单帧）：
///
/// | 条件 | 结论 |
/// |---|---|
/// | 总体占用 ≥80% 连续 ≥10 秒 | 持续高负载 |
/// | 总体占用 ≥60% 连续 ≥10 秒 | 持续偏高，建议关注（≤80% 段） |
/// | 单核瞬时 ≥80% | 仅记录，不提示 |
/// | 单核 ≥90% 连续 ≥10 秒 | 可提示单核繁忙 |
///
/// 三条硬规则：
/// 1. **间断即断链**：相邻两帧间隔超过 <see cref="IntervalToleranceMs"/> 视为采样中断，
///    连续性从断点之后重新累计——托盘隐藏、最小化都会造成这种中断。
/// 2. **缺帧不补零**：null 帧既不计入平均，也打断连续链。
/// 3. **不足 10 秒不下持续结论**：此时只报「采样中」。
///
/// 阈值只用于提示，不代表硬件故障；温度、功耗这类无法可靠采集的数据一律不出现。
/// </summary>
public sealed class CpuLoadAnalyzer
{
    /// <summary>趋势窗口（秒）。界面上的「最近 30 秒」与它同源。</summary>
    public const double TrendWindowSeconds = 30;

    /// <summary>判定「持续」所需的连续时长（秒）。</summary>
    public const double SustainedSeconds = 10;

    /// <summary>持续高负载阈值。</summary>
    public const double HighLoadThreshold = 80;

    /// <summary>持续偏高（关注）阈值。高负载段与关注段都从这一条起算。</summary>
    public const double WatchThreshold = 60;

    /// <summary>单核「繁忙」的持续阈值。</summary>
    public const double CoreBusyThreshold = 90;

    /// <summary>单核只记录不提示的瞬时阈值。</summary>
    public const double CoreNoteThreshold = 80;

    /// <summary>
    /// 相邻两帧允许的最大间隔。默认 1 秒一帧，超过 2.5 秒说明中间漏采
    /// （窗口隐藏、托盘、系统休眠），连续链必须断开。
    /// </summary>
    public const long IntervalToleranceMs = 2500;

    /// <summary>保留时长比趋势窗口略长，够算 30 秒平均即可。</summary>
    private const long RetentionMs = 36_000;

    private readonly List<CpuLoadSample> _samples = [];

    /// <summary>当前缓存的帧数（含缺帧）。</summary>
    public int Count => _samples.Count;

    /// <summary>最近一帧的时间戳；没有帧时为 null。</summary>
    public long? LastTimestampMs => _samples.Count == 0 ? null : _samples[^1].TimestampMs;

    /// <summary>加入一帧。时间戳来自 <see cref="Environment.TickCount64"/> 或测试构造值。</summary>
    public void Add(CpuLoadSample sample)
    {
        _samples.Add(sample);

        // 只保留窗口附近的帧：常驻托盘一整天也不会无限增长
        var cutoff = sample.TimestampMs - RetentionMs;
        _samples.RemoveAll(s => s.TimestampMs < cutoff);
    }

    /// <summary>清空历史。页面关闭或采集器重置时调用，避免把旧数据混进新窗口。</summary>
    public void Reset() => _samples.Clear();

    /// <summary>
    /// 最近 N 秒的统计。用于「30 秒平均」与快速优化前后的同口径对比（5 秒窗口）。
    /// </summary>
    public CpuWindowStats StatsOver(double seconds)
    {
        if (_samples.Count == 0 || seconds <= 0) return new CpuWindowStats(null, 0, 0, 0, 0);

        var end = _samples[^1].TimestampMs;
        var windowMs = (long)(seconds * 1000);
        var start = end - windowMs;

        var inWindow = _samples.Where(s => s.TimestampMs >= start).ToList();
        if (inWindow.Count == 0) return new CpuWindowStats(null, 0, 0, 0, 0);

        var valid = inWindow.Where(s => s.TotalPercent.HasValue).Select(s => s.TotalPercent!.Value).ToList();
        var average = valid.Count == 0 ? (double?)null : valid.Average();

        return new CpuWindowStats(
            AveragePercent: average,
            ValidSamples: valid.Count,
            MissingSamples: inWindow.Count - valid.Count,
            CoveredSeconds: (inWindow[^1].TimestampMs - inWindow[0].TimestampMs) / 1000.0,
            Breaks: CountBreaks(inWindow));
    }

    /// <summary>构建诊断结果。内部只读缓存，不修改状态。</summary>
    public CpuDiagnosis Build()
    {
        if (_samples.Count == 0) return CpuDiagnosis.Empty("尚未采到任何帧");

        var window = StatsOver(TrendWindowSeconds);
        var current = _samples[^1].TotalPercent is { } v ? OccupancyPalette.Clamp(v) : (double?)null;
        var trend = BuildTrend();
        var busyCores = BuildBusyCores();

        if (!window.HasData)
        {
            var reason = window.MissingSamples > 0
                ? $"最近 {window.MissingSamples} 帧都没采到"
                : "采样尚未就绪";
            return CpuDiagnosis.Empty(reason) with
            {
                SamplingStatus = "采样失败",
                CompletenessText = "最近窗口内没有有效帧，无法计算平均与趋势。",
            };
        }

        var highRun = ContinuousRun(HighLoadThreshold);
        var watchRun = ContinuousRun(WatchThreshold);

        var level = ResolveLevel(window, highRun, watchRun);
        var headline = BuildHeadline(level, current, highRun, window);
        var advices = BuildAdvices(level, window, current, highRun, watchRun, busyCores);

        return new CpuDiagnosis(
            level,
            current,
            window,
            highRun,
            watchRun,
            trend,
            busyCores,
            headline,
            advices,
            BuildSamplingStatus(level, window),
            BuildCompletenessText(window));
    }

    private static CpuLoadLevel ResolveLevel(CpuWindowStats window, double highRun, double watchRun)
    {
        if (highRun >= SustainedSeconds) return CpuLoadLevel.HighLoad;
        if (watchRun >= SustainedSeconds) return CpuLoadLevel.Watch;

        // 数据够长（≥10 秒）又没触发持续条件，才算「正常」
        if (window.CoveredSeconds >= SustainedSeconds) return CpuLoadLevel.Normal;

        return CpuLoadLevel.Sampling;
    }

    private static string BuildHeadline(
        CpuLoadLevel level, double? current, double highRunSeconds, CpuWindowStats window)
    {
        var cur = current is { } c ? $"{c:F0}%" : "未知";

        return level switch
        {
            CpuLoadLevel.HighLoad => $"持续高负载（当前 {cur}，已连续 ≥{HighLoadThreshold:F0}% 达 {highRunSeconds:F0} 秒）",
            CpuLoadLevel.Watch => $"总体负载偏高（当前 {cur}，持续 ≥{WatchThreshold:F0}% 已满 {SustainedSeconds:F0} 秒）",
            CpuLoadLevel.Sampling => $"采样中（当前 {cur}，已覆盖 {window.CoveredSeconds:F0} 秒，持续判定需 {SustainedSeconds:F0} 秒）",
            CpuLoadLevel.Normal => $"CPU 负载正常（当前 {cur}）",
            _ => "CPU 占用未知",
        };
    }

    private static IReadOnlyList<string> BuildAdvices(
        CpuLoadLevel level,
        CpuWindowStats window,
        double? current,
        double highRun,
        double watchRun,
        IReadOnlyList<BusyCore> busyCores)
    {
        var advices = new List<string>();

        switch (level)
        {
            case CpuLoadLevel.HighLoad:
                advices.Add(
                    $"总体占用已连续 {highRun:F0} 秒保持 ≥{HighLoadThreshold:F0}%。" +
                    "展开下方高占用进程，确认是哪个程序在占用；本工具不会自动结束进程。");
                advices.Add("若正在玩游戏，先看性能核是否也满（高级详情 → P 核峰值），再决定是否降低画质。");
                break;
            case CpuLoadLevel.Watch:
                advices.Add(
                    $"总体占用连续 {watchRun:F0} 秒在 {WatchThreshold:F0}% 以上。" +
                    "间歇性偏高通常来自后台任务：下载、网盘同步、杀毒扫描、Windows 索引。");
                break;
            case CpuLoadLevel.Sampling:
                advices.Add(
                    $"已采集 {window.CoveredSeconds:F0} 秒，判定「持续」需要 {SustainedSeconds:F0} 秒连续数据，" +
                    "请保持窗口可见。");
                break;
            case CpuLoadLevel.Normal:
                advices.Add("当前与最近 30 秒都没有持续高负载。");
                break;
        }

        // 瞬时高但连续链被打断：说明白「为什么不给结论」，别让用户以为工具没看到
        if (level == CpuLoadLevel.Normal && current >= HighLoadThreshold)
            advices.Add(
                $"当前读数 {current:F0}% 已超过 {HighLoadThreshold:F0}%，但连续采样不足 {SustainedSeconds:F0} 秒" +
                "（期间有间断），暂不判定为持续高负载。");

        foreach (var core in busyCores.Where(c => c.IsSustained).Take(3))
            advices.Add(
                $"逻辑核 {core.Index} 连续 {core.SustainedSeconds:F0} 秒 ≥{CoreBusyThreshold:F0}%：" +
                "单线程任务会受这颗核的性能限制。");

        advices.Add("阈值只用于提示，不代表硬件故障；本工具不采集温度与功耗（无法可靠获取）。");
        return advices;
    }

    private static string BuildSamplingStatus(CpuLoadLevel level, CpuWindowStats window) => level switch
    {
        CpuLoadLevel.Unknown => "采集不可用",
        CpuLoadLevel.Sampling => $"采样中（{window.ValidSamples} 帧 / {window.CoveredSeconds:F0} 秒）",
        _ => $"采样正常（每秒 1 帧，最近 {window.ValidSamples} 帧有效）",
    };

    private static string BuildCompletenessText(CpuWindowStats window)
    {
        var head = $"{TrendWindowSeconds:F0} 秒窗口：有效 {window.ValidSamples} 帧";

        if (window.MissingSamples == 0 && window.Breaks == 0) return head + "，无间断";

        var parts = new List<string> { head };
        if (window.MissingSamples > 0) parts.Add($"缺失 {window.MissingSamples} 帧（按未知处理，未用 0 补齐）");
        if (window.Breaks > 0) parts.Add($"间断 {window.Breaks} 处（间断前后的数据不参与同一段持续判定）");
        return string.Join("，", parts);
    }

    /// <summary>
    /// 从最新一帧往回累计「连续且都 ≥ threshold」的时长。
    /// 遇到缺帧或间隔超容差立即停止——这就是「必须连续有效窗口」的落地。
    /// </summary>
    private double ContinuousRun(double threshold)
    {
        if (_samples.Count == 0) return 0;

        var last = _samples[^1];
        if (last.TotalPercent is not { } lastValue || lastValue < threshold) return 0;

        var start = last.TimestampMs;
        for (var i = _samples.Count - 2; i >= 0; i--)
        {
            var cur = _samples[i];
            var next = _samples[i + 1];

            if (next.TimestampMs - cur.TimestampMs > IntervalToleranceMs) break;
            if (cur.TotalPercent is not { } value || value < threshold) break;

            start = cur.TimestampMs;
        }

        return (last.TimestampMs - start) / 1000.0;
    }

    private IReadOnlyList<CpuTrendPoint> BuildTrend()
    {
        if (_samples.Count == 0) return [];

        var end = _samples[^1].TimestampMs;
        var start = end - (long)(TrendWindowSeconds * 1000);

        return _samples
            .Where(s => s.TimestampMs >= start && s.TotalPercent.HasValue)
            .Select(s => new CpuTrendPoint(s.TimestampMs, OccupancyPalette.Clamp(s.TotalPercent!.Value)))
            .ToList();
    }

    /// <summary>
    /// 找出窗口内值得记录的核。峰值 ≥80% 才进列表（瞬时也算「记录」），
    /// 连续 ≥90% 满 10 秒才标 <see cref="BusyCore.IsSustained"/>。
    /// </summary>
    private IReadOnlyList<BusyCore> BuildBusyCores()
    {
        var last = _samples[^1];
        if (last.PerCorePercent is null || last.PerCorePercent.Count == 0) return [];

        var result = new List<BusyCore>();
        foreach (var index in last.PerCorePercent.Keys)
        {
            var peak = 0.0;
            foreach (var s in _samples)
            {
                if (s.PerCorePercent is null) continue;
                if (!s.PerCorePercent.TryGetValue(index, out var v)) continue;
                if (v > peak) peak = v;
            }

            if (peak < CoreNoteThreshold) continue;

            var sustained = CoreContinuousRun(index, CoreBusyThreshold);
            result.Add(new BusyCore(index, OccupancyPalette.Clamp(peak), sustained, sustained >= SustainedSeconds));
        }

        return result.OrderByDescending(c => c.PeakPercent).Take(6).ToList();
    }

    private double CoreContinuousRun(int index, double threshold)
    {
        var samples = _samples.Where(s => s.PerCorePercent is not null).ToList();
        if (samples.Count == 0) return 0;

        // 必须从最新一帧起算：历史里某段连续繁忙不能代表「现在还在忙」
        var last = samples[^1];
        if (!last.PerCorePercent!.TryGetValue(index, out var lastValue) || lastValue < threshold) return 0;

        var start = last.TimestampMs;
        for (var i = samples.Count - 2; i >= 0; i--)
        {
            var cur = samples[i];
            var next = samples[i + 1];

            if (next.TimestampMs - cur.TimestampMs > IntervalToleranceMs) break;
            if (!cur.PerCorePercent!.TryGetValue(index, out var value) || value < threshold) break;

            start = cur.TimestampMs;
        }

        return (last.TimestampMs - start) / 1000.0;
    }

    /// <summary>统计窗口内的采样间断次数（相邻帧间隔超容差即算一处）。</summary>
    private static int CountBreaks(IReadOnlyList<CpuLoadSample> samples)
    {
        var breaks = 0;
        for (var i = 1; i < samples.Count; i++)
            if (samples[i].TimestampMs - samples[i - 1].TimestampMs > IntervalToleranceMs) breaks++;

        return breaks;
    }
}
