namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;
using GameOptimizer.Core.Library;

/// <summary>
/// 进程级 CPU 占用采集。
///
/// 口径是这里唯一重要的事：**处理器时间增量 ÷ 墙钟时间 ÷ 逻辑处理器数**，
/// 分母错了会让「一个核跑满」显示成 6%（16 核机器上）或 1600%。
/// 另外「进程尚未采集到第二次」必须如实说「采样中」，不能给 0。
/// </summary>
public class ProcessCpuSamplerTests
{
    [Fact]
    public void 单核跑满在单核机器上应算100()
    {
        // 1 个逻辑处理器上跑满 1 秒
        var percent = ProcessCpuSampler.ToPercent(TimeSpan.FromMilliseconds(1000), 1000, 1);

        percent.Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void 单核跑满在16核机器上应算6点25()
    {
        var percent = ProcessCpuSampler.ToPercent(TimeSpan.FromMilliseconds(1000), 1000, 16);

        percent.Should().BeApproximately(6.25, 0.001);
    }

    [Fact]
    public void 墙钟间隔为零时返回0而不是无穷大()
    {
        // 同一毫秒采两次会得到 0 间隔；此时除零会得到 Infinity
        ProcessCpuSampler.ToPercent(TimeSpan.FromMilliseconds(100), 0, 8).Should().Be(0);
        ProcessCpuSampler.ToPercent(TimeSpan.FromMilliseconds(100), -5, 8).Should().Be(0);
    }

    [Fact]
    public void 逻辑核数非法时退化为1而不是除零()
    {
        ProcessCpuSampler.ToPercent(TimeSpan.FromMilliseconds(1000), 1000, 0)
            .Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void 结果必须钳制在0到100()
    {
        // 计数器回绕或时间基异常会给出离谱的差值
        ProcessCpuSampler.ToPercent(TimeSpan.FromMilliseconds(999_999), 1000, 1).Should().Be(100);
        ProcessCpuSampler.ToPercent(TimeSpan.FromMilliseconds(-500), 1000, 4).Should().Be(0);
    }

    [Fact]
    public void 第一次采样必须说采样中而不是给0()
    {
        // 没有差分基准时任何数字都是假的
        var sampler = new ProcessCpuSampler();

        var first = sampler.Sample(1_000);

        first.IsReady.Should().BeFalse();
        first.Top.Should().BeEmpty();
        first.Note.Should().Contain("采样中");
    }

    [Fact]
    public void 第二次采样后应给出可信标记与统计数量()
    {
        var sampler = new ProcessCpuSampler();

        sampler.Sample(1_000);
        Thread.Sleep(220);
        var second = sampler.Sample(2_000);

        second.IsReady.Should().BeTrue();
        second.Note.Should().NotContain("采样中");
        second.WatchedCount.Should().BeGreaterThan(0);
        second.Top.Should().OnlyContain(p => p.CpuPercent >= ProcessCpuSampler.MinimumPercentToShow);
        second.Top.Should().OnlyContain(p => p.CpuPercent <= 100);
    }

    [Fact]
    public void 受保护进程必须给出原因且不可结束()
    {
        var sampler = new ProcessCpuSampler();
        sampler.Sample(1_000);
        Thread.Sleep(220);
        var ranking = sampler.Sample(2_000);

        foreach (var p in ranking.Top.Where(p => p.IsProtected))
            p.ProtectionReason.Should().NotBeNullOrWhiteSpace("受保护就必须说清为什么");
    }

    [Fact]
    public void 重置后必须重新等待两次采样()
    {
        var sampler = new ProcessCpuSampler();
        sampler.Sample(1_000);
        Thread.Sleep(220);
        sampler.Sample(2_000).IsReady.Should().BeTrue();

        sampler.Reset();

        sampler.Sample(3_000).IsReady.Should().BeFalse();
    }
}
