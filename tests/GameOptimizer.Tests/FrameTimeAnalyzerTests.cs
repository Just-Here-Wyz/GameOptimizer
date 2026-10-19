namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;

public class FrameTimeAnalyzerTests
{
    [Fact]
    public void 空输入不应抛异常()
    {
        var stats = FrameTimeAnalyzer.Analyze([]);
        stats.TotalFrames.Should().Be(0);
        stats.AverageFps.Should().Be(0);
    }

    [Fact]
    public void 恒定16点7毫秒应得到约60FPS()
    {
        var stats = FrameTimeAnalyzer.Analyze(Enumerable.Repeat(16.7, 100).ToArray());
        stats.AverageFps.Should().BeApproximately(59.88, 0.1);
        stats.SpikeCount.Should().Be(0);
    }

    [Fact]
    public void 超过100毫秒的帧应计入尖峰()
    {
        var frames = Enumerable.Repeat(16.7, 98).Append(150.0).Append(200.0).ToArray();
        var stats = FrameTimeAnalyzer.Analyze(frames);
        stats.SpikeCount.Should().Be(2);
        stats.WorstFrameMs.Should().Be(200.0);
    }

    [Fact]
    public void 恰好等于阈值不算尖峰()
    {
        var frames = Enumerable.Repeat(16.7, 10).Append(FrameTimeAnalyzer.SpikeThresholdMs).ToArray();
        FrameTimeAnalyzer.Analyze(frames).SpikeCount.Should().Be(0);
    }

    [Fact]
    public void 百分之一Low应低于平均值()
    {
        var frames = Enumerable.Repeat(16.7, 90).Concat(Enumerable.Repeat(100.0, 10)).ToArray();
        var stats = FrameTimeAnalyzer.Analyze(frames);

        stats.OnePercentLowFps.Should().BeLessThan(stats.AverageFps);
        stats.PointOnePercentLowFps.Should().BeLessThanOrEqualTo(stats.OnePercentLowFps);
    }

    [Fact]
    public void 应忽略非正的帧时间()
    {
        var frames = new[] { 16.7, 0, -5, 16.7 };
        FrameTimeAnalyzer.Analyze(frames).TotalFrames.Should().Be(2);
    }

    [Fact]
    public void 单帧输入不应越界()
    {
        var stats = FrameTimeAnalyzer.Analyze([20.0]);
        stats.AverageFps.Should().BeApproximately(50.0, 0.01);
        stats.TotalFrames.Should().Be(1);
    }
}
