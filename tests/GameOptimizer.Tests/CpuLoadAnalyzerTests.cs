namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;

/// <summary>
/// CPU 负载诊断规则。
///
/// 这里锁死的是「持续」二字的含义：**必须是连续有效采样窗口**，
/// 单帧 95% 不能报「持续高负载」；中间缺一帧或间隔超过容差就要断链重算。
/// 这条口径错了，界面就会把「刚打开一个编译任务」说成「CPU 长期过载」。
/// </summary>
public class CpuLoadAnalyzerTests
{
    /// <summary>按 1 秒间隔灌入一串读数，起点固定为 0。</summary>
    private static CpuLoadAnalyzer Feed(params double?[] totals)
    {
        var analyzer = new CpuLoadAnalyzer();
        for (var i = 0; i < totals.Length; i++)
            analyzer.Add(new CpuLoadSample(i * 1000L, totals[i]));

        return analyzer;
    }

    /// <summary>连续 count 帧相同的读数。</summary>
    private static double?[] Constant(double value, int count)
        => Enumerable.Repeat((double?)value, count).ToArray();

    [Fact]
    public void 没有任何数据时结论必须是未知而不是0()
    {
        // 0% 会被理解成「整机空闲」，而实际是「没采到」——两件事不能混
        var d = new CpuLoadAnalyzer().Build();

        d.Level.Should().Be(CpuLoadLevel.Unknown);
        d.CurrentPercent.Should().BeNull();
        d.CurrentText.Should().Be("未知");
        d.AverageText.Should().Contain("未知");
        d.Trend.Should().BeEmpty();
        d.Advices.Should().Contain(a => a.Contains("不显示 0"));
    }

    [Fact]
    public void 单帧高占用不得判为持续高负载()
    {
        var d = Feed(95).Build();

        d.Level.Should().Be(CpuLoadLevel.Sampling);
        d.SustainedHighSeconds.Should().Be(0);
        d.Headline.Should().Contain("采样中");
    }

    [Fact]
    public void 连续9秒高负载仍不够10秒()
    {
        // 10 帧跨 9 秒：差 1 秒就不能说「持续 10 秒」
        var d = Feed(Constant(85, 10)).Build();

        d.Level.Should().NotBe(CpuLoadLevel.HighLoad);
        d.SustainedHighSeconds.Should().Be(9);
    }

    [Fact]
    public void 连续10秒达到80应判为持续高负载()
    {
        var d = Feed(Constant(85, 11)).Build();

        d.Level.Should().Be(CpuLoadLevel.HighLoad);
        d.SustainedHighSeconds.Should().Be(CpuLoadAnalyzer.SustainedSeconds);
        d.Headline.Should().Contain("持续高负载");
        d.HasConclusion.Should().BeTrue();
    }

    [Fact]
    public void 边界值80应计入高负载而不是关注()
    {
        // 阈值是「≥80」，不是「>80」
        var d = Feed(Constant(CpuLoadAnalyzer.HighLoadThreshold, 11)).Build();

        d.Level.Should().Be(CpuLoadLevel.HighLoad);
    }

    [Fact]
    public void 连续10秒在60到80之间应判为关注()
    {
        var d = Feed(Constant(70, 11)).Build();

        d.Level.Should().Be(CpuLoadLevel.Watch);
        d.Headline.Should().Contain("偏高");
        d.Advices.Should().Contain(a => a.Contains("后台任务"));
    }

    [Fact]
    public void 负载正常时结论应说正常()
    {
        var d = Feed(Constant(15, 15)).Build();

        d.Level.Should().Be(CpuLoadLevel.Normal);
        d.Headline.Should().Contain("正常");
    }

    [Fact]
    public void 中间缺一帧必须打断连续链()
    {
        // 前 5 秒忙、缺一帧、后 5 秒又忙：这不是「连续 10 秒」
        double?[] samples =
        [
            90, 90, 90, 90, 90, null, 90, 90, 90, 90, 90,
        ];

        var d = Feed(samples).Build();

        d.Level.Should().NotBe(CpuLoadLevel.HighLoad, "缺帧说明这一段没有连续有效的观测");
        d.Window.MissingSamples.Should().Be(1);
        d.SustainedHighSeconds.Should().Be(4);

        // 当前确实高，但给了结论会说清「为什么不下持续结论」
        d.Advices.Should().Contain(a => a.Contains("连续采样不足"));
    }

    [Fact]
    public void 缺帧不得用0补齐否则平均会被拉低()
    {
        // 平均值只算有效帧：5 帧 90% + 1 帧缺失，平均应是 90 而不是 75
        double?[] samples = [90, 90, 90, 90, 90, null];

        var d = Feed(samples).Build();

        d.Window.ValidSamples.Should().Be(5);
        d.Window.MissingSamples.Should().Be(1);
        d.Window.AveragePercent.Should().BeApproximately(90, 0.001);
        d.CompletenessText.Should().Contain("缺失 1 帧").And.Contain("未用 0 补齐");
    }

    [Fact]
    public void 采样间隔超过容差应视为间断()
    {
        // 托盘隐藏 5 秒再恢复：前后两段不能被拼成一段
        var analyzer = new CpuLoadAnalyzer();
        for (var i = 0; i < 5; i++) analyzer.Add(new CpuLoadSample(i * 1000L, 90));      // 0~4 秒
        for (var i = 0; i < 5; i++) analyzer.Add(new CpuLoadSample(9000L + i * 1000L, 90)); // 9~13 秒

        var d = analyzer.Build();

        d.Level.Should().NotBe(CpuLoadLevel.HighLoad);
        d.Window.Breaks.Should().Be(1);
        d.CompletenessText.Should().Contain("间断 1 处");
        d.SustainedHighSeconds.Should().Be(4);
    }

    [Fact]
    public void 断开之后再连续满10秒应重新判定为高负载()
    {
        // 间断只是打断连续性，不是永久失信：恢复后重新累计满 10 秒就该给结论
        var analyzer = new CpuLoadAnalyzer();
        analyzer.Add(new CpuLoadSample(0, 10));
        for (var i = 0; i < 11; i++) analyzer.Add(new CpuLoadSample(10_000L + i * 1000L, 90));

        var d = analyzer.Build();

        d.Level.Should().Be(CpuLoadLevel.HighLoad);
        d.Window.Breaks.Should().Be(1);
    }

    [Fact]
    public void 单核瞬时超标只记录不提示()
    {
        // 单帧 88% 只进 BusyCores，建议文案里不得出现「单核繁忙」
        var analyzer = new CpuLoadAnalyzer();
        analyzer.Add(new CpuLoadSample(0, 20, new Dictionary<int, double> { [0] = 88, [1] = 10 }));

        var d = analyzer.Build();

        d.BusyCores.Should().ContainSingle();
        d.BusyCores[0].Index.Should().Be(0);
        d.BusyCores[0].IsSustained.Should().BeFalse();
        d.Advices.Should().NotContain(a => a.Contains("逻辑核"));
    }

    [Fact]
    public void 单核持续90满10秒才提示单核繁忙()
    {
        var analyzer = new CpuLoadAnalyzer();
        for (var i = 0; i < 11; i++)
            analyzer.Add(new CpuLoadSample(i * 1000L, 40, new Dictionary<int, double> { [0] = 95, [1] = 20 }));

        var d = analyzer.Build();

        var core = d.BusyCores.Single(c => c.Index == 0);
        core.IsSustained.Should().BeTrue();
        core.SustainedSeconds.Should().Be(CpuLoadAnalyzer.SustainedSeconds);
        d.Advices.Should().Contain(a => a.Contains("逻辑核 0"));
        // 总负载只有 40%，整体结论不该被单核带成高负载
        d.Level.Should().Be(CpuLoadLevel.Normal);
    }

    [Fact]
    public void 单核连续9秒不满足持续条件()
    {
        var analyzer = new CpuLoadAnalyzer();
        for (var i = 0; i < 10; i++)
            analyzer.Add(new CpuLoadSample(i * 1000L, 40, new Dictionary<int, double> { [0] = 95 }));

        var d = analyzer.Build();

        d.BusyCores.Single().IsSustained.Should().BeFalse();
    }

    [Fact]
    public void 越界读数应被钳制()
    {
        // PDH 实测给过 123%
        var d = Feed(Constant(123, 3)).Build();

        d.CurrentPercent.Should().Be(100);
        d.Trend.Should().OnlyContain(p => p.Percent <= 100);
    }

    [Fact]
    public void 趋势只包含30秒窗口内的有效帧()
    {
        var analyzer = new CpuLoadAnalyzer();
        // 40 秒的数据，只有最后 30 秒该留在趋势里
        for (var i = 0; i < 41; i++) analyzer.Add(new CpuLoadSample(i * 1000L, i % 2 == 0 ? 30 : 70));

        var d = analyzer.Build();

        d.Trend.Should().NotBeEmpty();
        d.Trend.Should().OnlyContain(p => p.TimestampMs >= 10_000);
        d.Trend.Select(p => p.TimestampMs).Should().BeInAscendingOrder();
    }

    [Fact]
    public void 最近5秒窗口的统计口径应与30秒平均分离()
    {
        // 5 秒窗口是快速优化前后对比用的口径：只算最后 5 秒，不受更早的数据影响
        var analyzer = Feed([10, 20, 30, 40, 40, 40, 40]);

        var stats = analyzer.StatsOver(5);

        stats.ValidSamples.Should().Be(6);
        stats.AveragePercent.Should().BeApproximately(35, 0.001);
    }

    [Fact]
    public void 重置后不应保留任何历史()
    {
        var analyzer = Feed(Constant(90, 11));
        analyzer.Reset();

        analyzer.Count.Should().Be(0);
        analyzer.Build().Level.Should().Be(CpuLoadLevel.Unknown);
    }

    [Fact]
    public void 缺失帧不计入平均但计入完整性提示()
    {
        double?[] samples = [null, null, null];

        var d = Feed(samples).Build();

        d.Level.Should().Be(CpuLoadLevel.Unknown);
        d.SamplingStatus.Should().Be("采样失败");
        d.CompletenessText.Should().Contain("没有有效帧");
    }

    [Fact]
    public void 不确定的地方不得下结论()
    {
        // 只有 3 秒数据，哪怕每帧都是 99%，也只能说「采样中」
        var d = Feed(Constant(99, 4)).Build();

        d.Level.Should().Be(CpuLoadLevel.Sampling);
        d.HasConclusion.Should().BeFalse();
        d.Headline.Should().Contain("采样中");
    }
}
