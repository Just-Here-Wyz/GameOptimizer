namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Models;

public class VramAssessorTests
{
    private static SampleFrame Frame(double committedMb, double availableRamMb, double sharedMb = 0)
        => new(0, 0, sharedMb, committedMb, availableRamMb, 0, 0);

    [Fact]
    public void 没有采样时应返回_Unknown()
    {
        var result = new VramAssessor().Assess([], 4096);
        result.Verdict.Should().Be(VramVerdict.Unknown);
        result.IsProblem.Should().BeFalse();
    }

    [Fact]
    public void 核显_专用显存为0时仍能靠共享显存判定()
    {
        // Iris Xe 实测：Dedicated 恒为 0，真实占用在 Committed。
        // 预算按共享显存上限取 8GB（Windows 通常给物理内存的一半）。
        var frames = new[] { Frame(3200, 3000), Frame(3500, 2800), Frame(3300, 2900) };
        var result = new VramAssessor().Assess(frames, 8192);

        result.PeakCommittedMb.Should().Be(3500);
        result.Verdict.Should().Be(VramVerdict.Fine);
    }

    [Theory]
    [InlineData(3500, VramVerdict.Tight)]        // 85.4% —— 越过 80% 警戒线
    [InlineData(3800, VramVerdict.Insufficient)] // 92.8% —— 越过 90% 临界线
    public void 显存占用率应分级判定(double committedMb, VramVerdict expected)
    {
        // 内存充足，排除内存指标干扰，单独验证占用率分支
        var frames = new[] { Frame(committedMb, 4000) };
        new VramAssessor().Assess(frames, 4096).Verdict.Should().Be(expected);
    }

    [Theory]
    [InlineData(900, VramVerdict.Tight)]        // 低于 1GB 警戒线
    [InlineData(400, VramVerdict.Insufficient)] // 低于 512MB 临界线
    public void 内存不足应单独触发告警(double availableRamMb, VramVerdict expected)
    {
        // 显存占用率很低，只有内存越线，验证双指标不会被前者掩盖
        var frames = new[] { Frame(100, availableRamMb) };
        new VramAssessor().Assess(frames, 4096).Verdict.Should().Be(expected);
    }

    [Fact]
    public void 双指标同时越线时取更严重的结论并给出两条原因()
    {
        var frames = new[] { Frame(3900, 300) };
        var result = new VramAssessor().Assess(frames, 4096);

        result.Verdict.Should().Be(VramVerdict.Insufficient);
        result.Reasons.Should().HaveCount(2);
    }

    [Fact]
    public void 预算为0时应跳过占用率判定而不是除零()
    {
        var frames = new[] { Frame(3900, 4000) };
        new VramAssessor().Assess(frames, 0).Verdict.Should().Be(VramVerdict.Fine);
    }

    [Fact]
    public void 内存余量取全程最小值而非最后一帧()
    {
        var frames = new[] { Frame(1000, 4000), Frame(1000, 200) };
        new VramAssessor().Assess(frames, 4096).MinAvailableRamMb.Should().Be(200);
    }
}
