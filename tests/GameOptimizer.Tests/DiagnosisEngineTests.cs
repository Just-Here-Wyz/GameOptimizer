namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Models;

public class DiagnosisEngineTests
{
    private static SampleFrame Frame(
        long ts, double frameMs, double committed = 1000, double ram = 4000,
        double gpu = 90, double core = 50, double shared = 0)
        => new(ts, 0, shared, committed, ram, gpu, core, frameMs);

    [Fact]
    public void 空数据应给出_NoData()
    {
        var findings = new DiagnosisEngine().Diagnose([], 4096);
        findings.Should().ContainSingle().Which.Kind.Should().Be(FindingKind.NoData);
    }

    [Fact]
    public void 无尖峰时应报告帧时间平稳()
    {
        var frames = Enumerable.Range(0, 50)
            .Select(i => Frame(i * 100L, 16.7))
            .ToArray();

        var findings = new DiagnosisEngine().Diagnose(frames, 4096);
        findings.Should().ContainSingle(f => f.Kind == FindingKind.FrameStable);
    }

    [Fact]
    public void 尖峰伴随显存越线应归因为内存压力()
    {
        var frames = new List<SampleFrame>();
        for (var i = 0; i < 20; i++) frames.Add(Frame(i * 100L, 16.7, committed: 2000, ram: 4000));
        // 尖峰时刻：显存 95% + 内存见底 + 共享显存确实用上了（核显场景）
        frames.Add(Frame(2000, 250, committed: 3900, ram: 300, shared: 3800));

        var findings = new DiagnosisEngine().Diagnose(frames, 4096);
        findings.Should().Contain(f => f.Kind == FindingKind.MemoryPressure);
        findings.Should().Contain(f => f.Kind == FindingKind.VramSpill);
    }

    [Fact]
    public void 尖峰时GPU利用率低应归因为_CPU瓶颈()
    {
        var frames = new List<SampleFrame>();
        for (var i = 0; i < 10; i++) frames.Add(Frame(i * 100L, 16.7, gpu: 95));
        frames.Add(Frame(1000, 180, gpu: 30));

        var findings = new DiagnosisEngine().Diagnose(frames, 4096);
        findings.Should().Contain(f => f.Kind == FindingKind.CpuBound);
    }

    [Fact]
    public void 尖峰时单核打满应归因为_单核瓶颈()
    {
        var frames = new List<SampleFrame>();
        for (var i = 0; i < 10; i++) frames.Add(Frame(i * 100L, 16.7, core: 40));
        frames.Add(Frame(1000, 180, core: 99));

        var findings = new DiagnosisEngine().Diagnose(frames, 4096);
        findings.Should().Contain(f => f.Kind == FindingKind.SingleCoreBound);
    }

    [Fact]
    public void 各项正常且无尖峰时不应报出问题结论()
    {
        var frames = Enumerable.Range(0, 30)
            .Select(i => Frame(i * 100L, 16.7, committed: 1500, ram: 4000, gpu: 92, core: 45))
            .ToArray();

        var findings = new DiagnosisEngine().Diagnose(frames, 4096);

        findings.Should().NotContain(f => f.Kind == FindingKind.MemoryPressure);
        findings.Should().NotContain(f => f.Kind == FindingKind.CpuBound);
        findings.Should().Contain(f => f.Kind == FindingKind.FrameStable);
    }

    [Fact]
    public void 每条结论都应带有置信度与说明()
    {
        var frames = new[] { Frame(0, 16.7), Frame(500, 300, committed: 3950, ram: 200) };
        var findings = new DiagnosisEngine().Diagnose(frames, 4096);

        findings.Should().NotBeEmpty();
        findings.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Title));
        findings.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Detail));
        findings.Should().OnlyContain(f => f.Confidence > 0 && f.Confidence <= 1);
    }
}
