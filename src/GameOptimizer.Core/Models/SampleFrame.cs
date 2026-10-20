namespace GameOptimizer.Core.Models;

/// <summary>一次采样的原始快照，所有采集源最终归一到这里。</summary>
public sealed record SampleFrame(
    long TimestampMs,
    double DedicatedUsedMb,
    double SharedUsedMb,
    double CommittedMb,
    double AvailableRamMb,
    double Gpu3DPercent,
    double MaxCorePercent)
{
    /// <summary>
    /// 性能核（P-core）峰值占用。
    ///
    /// 为什么界面要用它而不是 <see cref="MaxCorePercent"/>：
    /// 实测本机（i7-1360P，4 大核 8 线程 + 8 小核）把 8 个后台进程绑到 E 核满载时，
    /// 全局单核峰值显示 98.6%（红色报警）而跑游戏的 P 核最高只有 29.3%——
    /// 玩家会白白去降画质。P 核峰值才是影响帧生成的信号。
    ///
    /// 全同构机器（无大小核）上它等于 <see cref="MaxCorePercent"/>。
    /// 默认值与 MaxCorePercent 相同，保证既有调用方行为不变。
    /// </summary>
    public double PerformanceCorePercent { get; init; } = MaxCorePercent;

    /// <summary>能效核（E-core）峰值占用。无小核的机器上为 0。</summary>
    public double EfficiencyCorePercent { get; init; }

    /// <summary>
    /// 逐逻辑核占用率，键是逻辑核序号（与 PDH 实例名 "组,核" 的后者一致）。
    /// 供 CPU 详情页展示每核条形图；空字典表示这次没采到逐核数据。
    /// </summary>
    public IReadOnlyDictionary<int, double> PerCorePercent { get; init; }
        = new Dictionary<int, double>();
}

/// <summary>显存判定口径：核显没有独立显存，必须同时看共享显存与系统内存余量。</summary>
public enum VramVerdict
{
    /// <summary>数据不足，无法判定。</summary>
    Unknown = 0,
    /// <summary>充裕。</summary>
    Fine = 1,
    /// <summary>接近上限，建议降低画质。</summary>
    Tight = 2,
    /// <summary>已越线，是高概率的卡顿来源。</summary>
    Insufficient = 3,
}

/// <summary>显存判定的完整结果，含触发原因。</summary>
public sealed record VramAssessment(
    VramVerdict Verdict,
    double PeakSharedMb,
    double PeakCommittedMb,
    double MinAvailableRamMb,
    IReadOnlyList<string> Reasons)
{
    public bool IsProblem => Verdict is VramVerdict.Tight or VramVerdict.Insufficient;

    /// <summary>真正有实测数据时才为 true。首页据此决定要不要用实测结论覆盖单款游戏的判定。</summary>
    public bool HasMeasurement => Verdict != VramVerdict.Unknown;
}
