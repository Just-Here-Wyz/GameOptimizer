namespace GameOptimizer.Core.Analysis;

using GameOptimizer.Core.Models;

/// <summary>帧时间统计：平均 FPS、1% Low、0.1% Low 与尖峰。</summary>
public sealed record FrameTimeStats(
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    int SpikeCount,
    double WorstFrameMs,
    int TotalFrames);

/// <summary>从逐帧耗时做统计与尖峰提取。</summary>
public static class FrameTimeAnalyzer
{
    /// <summary>超过该毫秒数算一次卡顿尖峰。</summary>
    public const double SpikeThresholdMs = 100.0;

    public static FrameTimeStats Analyze(IReadOnlyList<double> frameTimesMs)
    {
        var valid = frameTimesMs.Where(t => t > 0).OrderBy(t => t).ToArray();
        if (valid.Length == 0)
            return new FrameTimeStats(0, 0, 0, 0, 0, 0);

        var avgMs = valid.Average();
        return new FrameTimeStats(
            AverageFps: 1000.0 / avgMs,
            OnePercentLowFps: PercentileFps(valid, 0.99),
            PointOnePercentLowFps: PercentileFps(valid, 0.999),
            SpikeCount: valid.Count(t => t > SpikeThresholdMs),
            WorstFrameMs: valid[^1],
            TotalFrames: valid.Length);
    }

    /// <summary>
    /// 取第 p 分位的帧时间换算成 FPS。帧时间是"越小越好"，
    /// 所以低 1% 对应的是耗时最长的那 1%，即索引从尾部倒着数。
    /// </summary>
    private static double PercentileFps(double[] sortedAsc, double p)
    {
        var idx = (int)Math.Floor((sortedAsc.Length - 1) * p);
        idx = Math.Clamp(idx, 0, sortedAsc.Length - 1);
        return 1000.0 / sortedAsc[idx];
    }
}
