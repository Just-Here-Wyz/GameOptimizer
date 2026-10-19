namespace GameOptimizer.Core.Models;

/// <summary>一次采样的原始快照，所有采集源最终归一到这里。</summary>
public sealed record SampleFrame(
    long TimestampMs,
    double DedicatedUsedMb,
    double SharedUsedMb,
    double CommittedMb,
    double AvailableRamMb,
    double Gpu3DPercent,
    double MaxCorePercent,
    double FrameTimeMs);

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
