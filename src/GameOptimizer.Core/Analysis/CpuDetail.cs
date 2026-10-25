namespace GameOptimizer.Core.Analysis;

using GameOptimizer.Core.Collection;
using GameOptimizer.Core.Models;

/// <summary>详情页里的一根核条：哪颗核、多少占用、属于哪一类。</summary>
/// <param name="Index">逻辑核序号。</param>
/// <param name="Percent">占用率 0~100。</param>
/// <param name="IsPerformanceCore">是否性能核。</param>
/// <param name="PhysicalCoreIndex">物理核序号：同一物理核的两个超线程共享它。</param>
/// <param name="IsTopCore">是不是本组里占用最高的那颗（界面高亮用）。</param>
public sealed record CoreBar(
    int Index,
    double Percent,
    bool IsPerformanceCore,
    int PhysicalCoreIndex,
    bool IsTopCore)
{
    /// <summary>条形图上这根条该用什么颜色档（与仪表盘同一套口径）。</summary>
    public OccupancyLevel Level => OccupancyPalette.Level(Percent);

    /// <summary>颜色十六进制，界面直接绑。</summary>
    public string ColorHex => OccupancyPalette.ColorHex(Level);

    /// <summary>条形宽度百分比。0% 时也留一丝可见宽度，表示「读到了 0」而不是「没画出来」。</summary>
    public double BarWidthPercent => Percent <= 0 ? 1.5 : Math.Max(1.5, Percent);
}

/// <summary>一组核（性能核组 / 能效核组）的统计。</summary>
public sealed record CoreGroup(
    string Title,
    string Subtitle,
    bool IsPerformance,
    IReadOnlyList<CoreBar> Bars,
    double PeakPercent,
    double AveragePercent)
{
    public bool IsEmpty => Bars.Count == 0;
}

/// <summary>
/// CPU 详情页的数据模型：把「逻辑核 → 占用」整理成按大小核分组、可直接绑定的结构。
///
/// 为什么放 Core：分组规则、峰值挑选、超线程合并都是可测逻辑，
/// 放在 App 层就测不到（测试项目不引用 App）。界面只负责画。
/// </summary>
public sealed class CpuDetail
{
    private CpuDetail(
        CoreGroup performance,
        CoreGroup efficiency,
        double? totalCpuPercent,
        double globalPeakPercent,
        string topologyText,
        bool isHybrid)
    {
        PerformanceCore = performance;
        EfficiencyCore = efficiency;
        TotalCpuPercent = totalCpuPercent;
        GlobalPeakPercent = globalPeakPercent;
        TopologyText = topologyText;
        IsHybrid = isHybrid;
    }

    /// <summary>性能核组。</summary>
    public CoreGroup PerformanceCore { get; }

    /// <summary>能效核组。全同构机器上为空。</summary>
    public CoreGroup EfficiencyCore { get; }

    /// <summary>
    /// 整机平均占用（与首页 CPU 仪表同源，详情页里并排展示便于对照）。
    ///
    /// 可空：总 CPU 计数器读不到时它是 null，界面显示「未知」。
    /// 不给它默认 0——0% 会被理解成「整机空闲」，那是另一回事。
    /// </summary>
    public double? TotalCpuPercent { get; }

    /// <summary>总 CPU 的展示文本。读不到就是「未知」。</summary>
    public string TotalCpuText => TotalCpuPercent is { } v ? $"{v:F0}%" : "未知";

    /// <summary>全局单核峰值（跨全部逻辑核）。与 P 核峰值并列，解释两者差异。</summary>
    public double GlobalPeakPercent { get; }

    /// <summary>拓扑描述，例如「4 大核 8 线程 · 8 小核」。</summary>
    public string TopologyText { get; }

    /// <summary>本机是否有大小核。</summary>
    public bool IsHybrid { get; }

    /// <summary>P 核峰值（= 性能核组的峰值）。</summary>
    public double PerformancePeakPercent => PerformanceCore.PeakPercent;

    /// <summary>E 核峰值（无小核时为 0）。</summary>
    public double EfficiencyPeakPercent => EfficiencyCore.PeakPercent;

    /// <summary>
    /// 一句话说明当前 CPU 状态。这是详情页最上面那行结论，
    /// 必须能在 P 核 / E 核分离时给出不同说法——不然详情页只是堆数字。
    /// </summary>
    public string VerdictText
    {
        get
        {
            // 全同构机器：只有「整机忙不忙」这一个维度
            if (!IsHybrid)
            {
                return GlobalPeakPercent switch
                {
                    >= 85 => $"单核接近满载（{GlobalPeakPercent:F0}%），可能是单线程瓶颈",
                    >= 60 => $"单核偏高（{GlobalPeakPercent:F0}%），留意是否持续",
                    _ => "CPU 压力正常",
                };
            }

            var p = PerformancePeakPercent;
            var e = EfficiencyPeakPercent;

            // 关键分支：小核被挤满而大核空闲 —— 这正是全局峰值会误报的场景
            if (p < 40 && e >= 80)
                return $"小核被后台挤满（{e:F0}%），跑游戏的大核仍空闲（{p:F0}%）";

            if (p >= 85)
                return $"性能核接近满载（{p:F0}%），高概率是游戏主线程瓶颈";

            if (p >= 60)
                return $"性能核偏高（{p:F0}%），留意是否持续";

            if (e >= 80)
                return $"性能核 {p:F0}% 空闲，但小核较忙（{e:F0}%）";

            return "CPU 压力正常";
        }
    }

    /// <summary>
    /// 由一帧采样 + 拓扑构造详情页数据。
    /// </summary>
    /// <param name="frame">采样帧。其 <see cref="SampleFrame.PerCorePercent"/> 为空时返回空的详情。</param>
    /// <param name="topology">CPU 拓扑。</param>
    /// <param name="totalCpuPercent">整机平均占用。读不到时传 null，界面显示「未知」。</param>
    public static CpuDetail From(SampleFrame frame, CpuTopology topology, double? totalCpuPercent)
    {
        var usage = frame.PerCorePercent;

        var pBars = BuildBars(topology.PerformanceCores, usage, isPerformance: true);
        var eBars = BuildBars(topology.EfficiencyCores, usage, isPerformance: false);

        var pGroup = BuildGroup("性能核 P-core", topology, pBars, isPerformance: true);
        var eGroup = BuildGroup("能效核 E-core", topology, eBars, isPerformance: false);

        return new CpuDetail(
            pGroup,
            eGroup,
            totalCpuPercent,
            frame.MaxCorePercent,
            topology.Describe(),
            topology.IsHybrid);
    }

    private static CoreGroup BuildGroup(
        string fallbackTitle, CpuTopology topology, List<CoreBar> bars, bool isPerformance)
    {
        // 标题带上物理核数：玩家关心「几个大核」，不是「几个线程」
        var physical = bars.Select(b => b.PhysicalCoreIndex).Distinct().Count();
        var title = bars.Count == 0 ? fallbackTitle : $"{fallbackTitle} ×{physical}";

        var subtitle = isPerformance
            ? "跑游戏主线程的地方，这项满了才是真瓶颈"
            : "承接后台任务，满了通常不影响帧率";

        var peak = bars.Count == 0 ? 0 : bars.Max(b => b.Percent);
        var avg = bars.Count == 0 ? 0 : bars.Average(b => b.Percent);

        return new CoreGroup(title, subtitle, isPerformance, bars, peak, avg);
    }

    /// <summary>把拓扑里的逻辑核转成条形，占用读不到的核直接略过（不显示假的 0）。</summary>
    private static List<CoreBar> BuildBars(
        IReadOnlyList<LogicalProcessor> processors, IReadOnlyDictionary<int, double> usage, bool isPerformance)
    {
        var bars = new List<CoreBar>(processors.Count);
        if (processors.Count == 0) return bars;

        // 先确定峰值，用于标记「哪颗最高」
        double peak = 0;
        var hasAny = false;
        foreach (var p in processors)
        {
            if (!usage.TryGetValue(p.Index, out var v)) continue;
            hasAny = true;
            if (v > peak) peak = v;
        }

        if (!hasAny) return bars;

        foreach (var p in processors)
        {
            if (!usage.TryGetValue(p.Index, out var v)) continue;

            bars.Add(new CoreBar(
                p.Index,
                OccupancyPalette.Clamp(v),
                isPerformance,
                p.CoreIndex,
                // 只有真峰值那颗（且峰值 > 0）才标顶，避免多颗同时 0% 都被标成最高
                IsTopCore: peak > 0 && Math.Abs(v - peak) < 0.01));
        }

        return bars.OrderBy(b => b.Index).ToList();
    }
}
