namespace GameOptimizer.Core.Models;

/// <summary>一条归因结论。</summary>
public sealed record DiagnosisFinding(
    FindingKind Kind,
    string Title,
    string Detail,
    double Confidence);

public enum FindingKind
{
    /// <summary>显存/内存压力。</summary>
    MemoryPressure,
    /// <summary>共享显存越线（核显特有）。</summary>
    VramSpill,
    /// <summary>GPU 利用率低但有尖峰，CPU 或等待瓶颈。</summary>
    CpuBound,
    /// <summary>单核打满。</summary>
    SingleCoreBound,
    /// <summary>帧时间本身很稳，卡顿感可能不来自渲染。</summary>
    FrameStable,
    /// <summary>没有可观测量，数据不足。</summary>
    NoData,
}
