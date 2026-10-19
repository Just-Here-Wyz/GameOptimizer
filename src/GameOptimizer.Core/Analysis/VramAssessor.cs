namespace GameOptimizer.Core.Analysis;

using GameOptimizer.Core.Models;

/// <summary>显存达标判定的阈值。集中放在这里，方便用户按机器调整。</summary>
public sealed record VramThresholds(
    double AvailableRamWarnMb = 1024,
    double AvailableRamCriticalMb = 512,
    double CommittedRatioWarn = 0.80,
    double CommittedRatioCritical = 0.90)
{
    public static VramThresholds Default { get; } = new();
}

/// <summary>
/// 显存判定器。核显（Intel Iris Xe 等）没有独立显存，Dedicated 恒为 0，
/// 真实占用体现在 Shared 与 Total Committed 上，因此必须双指标判定。
/// </summary>
public sealed class VramAssessor
{
    private readonly VramThresholds _t;

    public VramAssessor(VramThresholds? thresholds = null) => _t = thresholds ?? VramThresholds.Default;

    /// <param name="frames">一次会话的全部采样。</param>
    /// <param name="vramBudgetMb">
    /// 可用显存预算。核显按共享显存上限，通常取系统内存的约一半；独显取专用显存。
    /// </param>
    public VramAssessment Assess(IReadOnlyList<SampleFrame> frames, double vramBudgetMb)
    {
        if (frames.Count == 0)
            return new VramAssessment(VramVerdict.Unknown, 0, 0, 0, ["没有采样数据"]);

        var peakShared = frames.Max(f => f.SharedUsedMb);
        var peakCommitted = frames.Max(f => f.CommittedMb);
        var minRam = frames.Min(f => f.AvailableRamMb);

        var reasons = new List<string>();
        var verdict = VramVerdict.Fine;

        void Raise(VramVerdict v)
        {
            if (v > verdict) verdict = v;
        }

        // 指标一：显存预算占用率
        if (vramBudgetMb > 0)
        {
            var ratio = peakCommitted / vramBudgetMb;
            if (ratio >= _t.CommittedRatioCritical)
            {
                Raise(VramVerdict.Insufficient);
                reasons.Add($"显存占用峰值 {peakCommitted:F0} MB，达到预算的 {ratio:P0}，超出 90% 安全线");
            }
            else if (ratio >= _t.CommittedRatioWarn)
            {
                Raise(VramVerdict.Tight);
                reasons.Add($"显存占用峰值 {peakCommitted:F0} MB，达到预算的 {ratio:P0}，接近上限");
            }
        }

        // 指标二：系统内存余量（核显的显存来自内存，内存见底会直接挤掉显存）
        if (minRam <= _t.AvailableRamCriticalMb)
        {
            Raise(VramVerdict.Insufficient);
            reasons.Add($"可用内存最低跌到 {minRam:F0} MB，显存会被迫向磁盘换页");
        }
        else if (minRam <= _t.AvailableRamWarnMb)
        {
            Raise(VramVerdict.Tight);
            reasons.Add($"可用内存最低 {minRam:F0} MB，余量偏紧");
        }

        return new VramAssessment(verdict, peakShared, peakCommitted, minRam, reasons);
    }
}
