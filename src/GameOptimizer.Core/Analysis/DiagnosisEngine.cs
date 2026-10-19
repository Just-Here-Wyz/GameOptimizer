namespace GameOptimizer.Core.Analysis;

using GameOptimizer.Core.Models;

/// <summary>
/// 归因引擎：把帧时间尖峰与同一时刻的显存/内存/GPU/CPU 指标做时间轴对齐，
/// 判断尖峰到底是显存、内存、CPU 单核，还是根本不是渲染问题。
/// </summary>
public sealed class DiagnosisEngine
{
    /// <summary>尖峰前后多少毫秒内的采样算作"同一时刻"。</summary>
    private const long CorrelationWindowMs = 1000;

    private readonly VramAssessor _vram;

    public DiagnosisEngine(VramAssessor? vram = null) => _vram = vram ?? new VramAssessor();

    public IReadOnlyList<DiagnosisFinding> Diagnose(
        IReadOnlyList<SampleFrame> frames,
        double vramBudgetMb)
    {
        if (frames.Count == 0)
            return [new DiagnosisFinding(FindingKind.NoData, "数据不足", "本次会话没有采集到任何样本。", 1.0)];

        var findings = new List<DiagnosisFinding>();
        var vramAssessment = _vram.Assess(frames, vramBudgetMb);

        var spikeFrames = frames.Where(f => f.FrameTimeMs > FrameTimeAnalyzer.SpikeThresholdMs).ToArray();
        var stats = FrameTimeAnalyzer.Analyze(frames.Select(f => f.FrameTimeMs).ToArray());

        if (spikeFrames.Length == 0)
        {
            findings.Add(new DiagnosisFinding(
                FindingKind.FrameStable,
                "帧时间平稳",
                $"全程 {stats.TotalFrames} 帧无超过 {FrameTimeAnalyzer.SpikeThresholdMs:F0}ms 的尖峰，" +
                $"平均 {stats.AverageFps:F0} FPS。若仍有卡顿感，更可能来自网络或输入延迟。",
                0.9));
        }

        // 显存归因：只有在尖峰时刻确实存在压力才算数
        if (vramAssessment.IsProblem)
        {
            var spikingUnderPressure = spikeFrames.Length > 0
                ? DescribePressureOnSpikes(spikeFrames, frames, vramBudgetMb)
                : "本段没有帧时间尖峰，压力尚未转化为卡顿。";

            findings.Add(new DiagnosisFinding(
                FindingKind.MemoryPressure,
                vramAssessment.Verdict == VramVerdict.Insufficient ? "显存/内存不足" : "显存/内存偏紧",
                $"{string.Join("；", vramAssessment.Reasons)}。{spikingUnderPressure}",
                vramAssessment.Verdict == VramVerdict.Insufficient ? 0.85 : 0.6));

            if (vramAssessment.PeakSharedMb > 0 && vramAssessment.PeakCommittedMb > vramBudgetMb * 0.8)
            {
                findings.Add(new DiagnosisFinding(
                    FindingKind.VramSpill,
                    "共享显存溢出",
                    $"峰值共享显存 {vramAssessment.PeakSharedMb:F0} MB。核显的显存直接取自内存，" +
                    "降低纹理质量与阴影分辨率是收益最高的两项。",
                    0.8));
            }
        }

        // CPU 归因：尖峰时刻 GPU 利用率低 → 瓶颈不在 GPU
        var cpuBoundSpikes = spikeFrames.Where(s =>
            Near(s, frames).Any(n => n.Gpu3DPercent < 60)).Count();
        if (cpuBoundSpikes > 0)
        {
            findings.Add(new DiagnosisFinding(
                FindingKind.CpuBound,
                "CPU 瓶颈",
                $"{cpuBoundSpikes} 次尖峰发生时 GPU 3D 利用率低于 60%，GPU 在等 CPU 送帧。",
                0.75));
        }

        // 单核归因：尖峰时刻单核打满
        var coreBoundSpikes = spikeFrames.Where(s =>
            Near(s, frames).Any(n => n.MaxCorePercent > 95)).Count();
        if (coreBoundSpikes > 0)
        {
            findings.Add(new DiagnosisFinding(
                FindingKind.SingleCoreBound,
                "单核打满",
                $"{coreBoundSpikes} 次尖峰发生时存在单核占用超过 95%，典型的游戏主线程瓶颈。",
                0.7));
        }

        // 一条结论都没有时，明确说明"没找到问题"，避免用户误以为工具有 bug
        if (findings.Count == 0)
        {
            findings.Add(new DiagnosisFinding(
                FindingKind.NoData,
                "未发现明显异常",
                "显存、内存、GPU、CPU 指标均未越线。请把采集窗口拉长到一次完整的对局再判断。",
                0.5));
        }

        return findings;
    }

    private static IEnumerable<SampleFrame> Near(SampleFrame center, IReadOnlyList<SampleFrame> frames)
        => frames.Where(f => Math.Abs(f.TimestampMs - center.TimestampMs) <= CorrelationWindowMs);

    private static string DescribePressureOnSpikes(
        SampleFrame[] spikes,
        IReadOnlyList<SampleFrame> frames,
        double budgetMb)
    {
        var spikesUnderPressure = spikes.Count(s =>
            Near(s, frames).Any(n => budgetMb > 0 && n.CommittedMb > budgetMb * 0.85
                                     || n.AvailableRamMb < 1024));

        return spikesUnderPressure > 0
            ? $"其中 {spikesUnderPressure} 次尖峰与高占用时刻重合。"
            : "但尖峰时刻占用并未明显抬升，需结合其他指标判断。";
    }
}
