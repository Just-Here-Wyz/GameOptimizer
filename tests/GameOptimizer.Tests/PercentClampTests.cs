namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;

/// <summary>
/// 百分比钳制。
/// 实测踩到的真实问题：界面上 GPU 3D 显示 123%。
/// PDH 在两次读数间隔异常（例如定时器被阻塞后立刻又采一次）时，
/// 会用错误的时间基算出超过 100 的利用率；而利用率语义上不可能越界。
/// </summary>
public class PercentClampTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(123, 100)]     // 实际见过的值
    [InlineData(999, 100)]
    [InlineData(-5, 0)]
    [InlineData(-100, 0)]
    public void 百分比应钳制到合法区间(double input, double expected)
    {
        PdhSampler.ClampPercent(input).Should().Be(expected);
    }

    [Fact]
    public void NaN应返回0而不是污染图表()
    {
        // 一旦把 NaN 塞进曲线，Polyline 坐标全变成 NaN，整条线会消失
        PdhSampler.ClampPercent(double.NaN).Should().Be(0);
    }

    [Fact]
    public void 正负无穷应返回0()
    {
        PdhSampler.ClampPercent(double.PositiveInfinity).Should().Be(0);
        PdhSampler.ClampPercent(double.NegativeInfinity).Should().Be(0);
    }

    [Fact]
    public void 采样结果中的百分比都应落在0到100之间()
    {
        using var sampler = new PdhSampler();
        if (!sampler.Initialize()) return;
        sampler.Warmup(200);

        for (var i = 0; i < 5; i++)
        {
            var frame = sampler.Sample(i * 1000L, 0);
            frame.Gpu3DPercent.Should().BeInRange(0, 100);
            frame.MaxCorePercent.Should().BeInRange(0, 100);
        }
    }

    [Fact]
    public void 连续快速采样也不应产生越界值()
    {
        // 这正是当初触发 123% 的场景：定时器被阻塞后连续补采
        using var sampler = new PdhSampler();
        if (!sampler.Initialize()) return;
        sampler.Warmup(200);

        for (var i = 0; i < 20; i++)
            sampler.Sample(i, 0).Gpu3DPercent.Should().BeInRange(0, 100);
    }
}
