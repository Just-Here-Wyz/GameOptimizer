namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Collection;
using GameOptimizer.Core.Models;

/// <summary>
/// CPU 详情页的数据模型。
///
/// 重点是 <see cref="CpuDetail.VerdictText"/>：详情页最上面那行结论。
/// 它必须在「小核被挤满、大核空闲」时给出与「大核满载」不同的说法，
/// 否则详情页只是把数字堆在一起，用户仍然分不清要不要担心。
/// </summary>
public class CpuDetailTests
{
    /// <summary>4 大核 8 线程 + 8 小核，与 i7-1360P 实测一致。</summary>
    private static CpuTopology Hybrid()
    {
        var list = new List<LogicalProcessor>();
        for (var core = 0; core < 4; core++)
        {
            list.Add(new LogicalProcessor(0, core * 2, core * 2, 1) { IsPerformanceCore = true });
            list.Add(new LogicalProcessor(0, core * 2 + 1, core * 2, 1) { IsPerformanceCore = true });
        }
        for (var i = 8; i < 16; i++)
            list.Add(new LogicalProcessor(0, i, i, 0) { IsPerformanceCore = false });

        return CpuTopology.FromProcessors(list);
    }

    private static SampleFrame Frame(
        CpuTopology topology,
        Func<int, double> usage,
        double total = 0,
        double globalPeak = 0)
    {
        var dict = topology.Processors.ToDictionary(p => p.Index, p => usage(p.Index));
        return new SampleFrame(0, 0, 0, 0, 0, 0, globalPeak)
        {
            PerCorePercent = dict,
            PerformanceCorePercent = topology.PerformancePeak(dict),
            EfficiencyCorePercent = topology.EfficiencyPeak(dict),
        };
    }

    [Fact]
    public void 应按大小核分组并统计峰值()
    {
        var t = Hybrid();
        // P 核 0-7：最高 93（逻辑核 2）；E 核 8-15：最高 54
        var frame = Frame(t, i => i switch
        {
            0 => 20, 1 => 15, 2 => 93, 3 => 10, 4 => 5, 5 => 8, 6 => 3, 7 => 2,
            _ => 50 + (i % 5),
        }, total: 53.4, globalPeak: 93);

        var d = CpuDetail.From(frame, t, 53.4);

        d.PerformanceCore.Bars.Should().HaveCount(8);
        d.EfficiencyCore.Bars.Should().HaveCount(8);
        d.PerformancePeakPercent.Should().Be(93);
        d.EfficiencyPeakPercent.Should().Be(54);
        d.TotalCpuPercent.Should().Be(53.4);
        d.GlobalPeakPercent.Should().Be(93);
    }

    [Fact]
    public void 组标题应写物理核数而不是线程数()
    {
        // 玩家关心「几个大核」，不是「几个线程」
        var d = CpuDetail.From(Frame(Hybrid(), _ => 10), Hybrid(), 10);

        d.PerformanceCore.Title.Should().Contain("×4", "4 个物理大核（8 线程）");
        d.EfficiencyCore.Title.Should().Contain("×8");
    }

    [Fact]
    public void 每组最高的一颗应被标记()
    {
        var t = Hybrid();
        var frame = Frame(t, i => i == 2 ? 93 : (i == 9 ? 88 : 10));
        var d = CpuDetail.From(frame, t, 20);

        d.PerformanceCore.Bars.Where(b => b.IsTopCore).Should().ContainSingle()
            .Which.Index.Should().Be(2);
        d.EfficiencyCore.Bars.Where(b => b.IsTopCore).Should().ContainSingle()
            .Which.Index.Should().Be(9);
    }

    [Fact]
    public void 全为0时不应把多颗核都标成最高()
    {
        // 峰值是 0 时不该有任何核被高亮，否则 16 颗全亮
        var t = Hybrid();
        var d = CpuDetail.From(Frame(t, _ => 0), t, 0);

        d.PerformanceCore.Bars.Should().NotContain(b => b.IsTopCore);
        d.EfficiencyCore.Bars.Should().NotContain(b => b.IsTopCore);
    }

    // ---------- 结论文案：这是详情页存在的意义 ----------

    [Fact]
    public void 小核被挤满而大核空闲时结论应指出这是后台负载()
    {
        // 实测场景：8 个后台进程绑 E 核满载，全局峰值 100% 但 P 核只有 24.9%
        var t = Hybrid();
        var frame = Frame(t, i => i >= 8 ? 98 : 24, total: 60, globalPeak: 100);

        var d = CpuDetail.From(frame, t, 60);

        d.VerdictText.Should().Contain("小核").And.Contain("大核");
        d.VerdictText.Should().Contain("空闲");
        d.VerdictText.Should().NotContain("瓶颈", "大核没满，说成瓶颈会让用户白降画质");
    }

    [Fact]
    public void 大核满载时结论应指出游戏主线程瓶颈()
    {
        var t = Hybrid();
        var frame = Frame(t, i => i < 8 ? 95 : 15, total: 55, globalPeak: 95);

        var d = CpuDetail.From(frame, t, 55);

        d.VerdictText.Should().Contain("性能核");
        d.VerdictText.Should().Contain("瓶颈");
    }

    [Fact]
    public void 大核偏高时结论应提示留意而不是报警()
    {
        var t = Hybrid();
        var d = CpuDetail.From(Frame(t, i => i < 8 ? 70 : 10), t, 35);

        d.VerdictText.Should().Contain("偏高");
        d.VerdictText.Should().NotContain("瓶颈");
    }

    [Fact]
    public void 大核空闲小核较忙时结论应说明受影响有限()
    {
        var t = Hybrid();
        var d = CpuDetail.From(Frame(t, i => i >= 8 ? 85 : 20), t, 50);

        d.VerdictText.Should().Contain("小核");
        d.VerdictText.Should().NotContain("瓶颈");
    }

    [Fact]
    public void 都空闲时结论应说正常()
    {
        var t = Hybrid();
        var d = CpuDetail.From(Frame(t, _ => 5), t, 5);

        d.VerdictText.Should().Be("CPU 压力正常");
    }

    [Fact]
    public void 全同构机器上结论只看全局峰值()
    {
        // 没有大小核时不该出现「大核 / 小核」的说法
        var t = CpuTopology.Uniform(16);
        var d = CpuDetail.From(Frame(t, _ => 90, globalPeak: 90), t, 90);

        d.IsHybrid.Should().BeFalse();
        d.VerdictText.Should().Contain("单核");
        d.VerdictText.Should().NotContain("大核");
        d.EfficiencyCore.IsEmpty.Should().BeTrue();
    }

    // ---------- 逐核读数缺失时的降级 ----------

    [Fact]
    public void 没有逐核数据时应返回空组而不是假的0()
    {
        // 采不到逐核数据时显示 16 根 0% 的条，会让人以为 CPU 全闲
        var t = Hybrid();
        var frame = new SampleFrame(0, 0, 0, 0, 0, 0, 42);

        var d = CpuDetail.From(frame, t, 42);

        d.PerformanceCore.IsEmpty.Should().BeTrue();
        d.EfficiencyCore.IsEmpty.Should().BeTrue();
        d.PerformancePeakPercent.Should().Be(0);
        // 全局峰值仍来自 SampleFrame，不受影响
        d.GlobalPeakPercent.Should().Be(42);
    }

    [Fact]
    public void 缺个别核的数据时只画读到的那些()
    {
        var t = Hybrid();
        var partial = new Dictionary<int, double> { [0] = 50, [1] = 30, [8] = 70 };
        var frame = new SampleFrame(0, 0, 0, 0, 0, 0, 70) { PerCorePercent = partial };

        var d = CpuDetail.From(frame, t, 40);

        d.PerformanceCore.Bars.Should().HaveCount(2, "只画读到的 0 和 1");
        d.EfficiencyCore.Bars.Should().HaveCount(1);
    }

    // ---------- 条形绘制属性 ----------

    [Fact]
    public void 条形颜色应与仪表盘同一套四档口径()
    {
        var t = Hybrid();
        var d = CpuDetail.From(Frame(t, i => i switch
        {
            0 => 10, 1 => 45, 2 => 70, 3 => 95,
            _ => 5,
        }), t, 20);

        d.PerformanceCore.Bars.Single(b => b.Index == 0).Level.Should().Be(OccupancyLevel.Low);
        d.PerformanceCore.Bars.Single(b => b.Index == 1).Level.Should().Be(OccupancyLevel.Normal);
        d.PerformanceCore.Bars.Single(b => b.Index == 2).Level.Should().Be(OccupancyLevel.High);
        d.PerformanceCore.Bars.Single(b => b.Index == 3).Level.Should().Be(OccupancyLevel.Critical);
    }

    [Fact]
    public void 零占用也要留可见宽度表示读到了0()
    {
        // 宽度为 0 的条看起来像没画出来，用户会以为是 bug
        var t = Hybrid();
        var d = CpuDetail.From(Frame(t, _ => 0), t, 0);

        d.PerformanceCore.Bars.Should().OnlyContain(b => b.BarWidthPercent > 0);
    }

    [Fact]
    public void 条形应按逻辑核序号排序()
    {
        var t = Hybrid();
        var d = CpuDetail.From(Frame(t, i => i * 2 % 100), t, 50);

        d.PerformanceCore.Bars.Select(b => b.Index).Should().BeInAscendingOrder();
        d.EfficiencyCore.Bars.Select(b => b.Index).Should().BeInAscendingOrder();
    }

    [Fact]
    public void 超范围读数应被钳制()
    {
        // PDH 曾给出 123%，条形宽度不能超过 100%
        var t = Hybrid();
        var dict = new Dictionary<int, double> { [0] = 123, [1] = -5 };
        var frame = new SampleFrame(0, 0, 0, 0, 0, 0, 123) { PerCorePercent = dict };

        var d = CpuDetail.From(frame, t, 0);

        d.PerformanceCore.Bars.Single(b => b.Index == 0).Percent.Should().Be(100);
        d.PerformanceCore.Bars.Single(b => b.Index == 0).BarWidthPercent.Should().Be(100);
        d.PerformanceCore.Bars.Single(b => b.Index == 1).Percent.Should().Be(0);
    }

    [Fact]
    public void 真实机器上应能构造出详情数据()
    {
        var topology = CpuTopology.Detect();
        using var sampler = new PdhSampler();
        if (!sampler.Initialize()) return;
        sampler.Warmup(300);

        var frame = sampler.Sample(0);
        var d = CpuDetail.From(frame, topology, 20);

        d.TopologyText.Should().NotBeNullOrWhiteSpace();
        d.PerformanceCore.Bars.Should().NotBeEmpty();
        d.VerdictText.Should().NotBeNullOrWhiteSpace();
        // 条形数不应超过逻辑核数
        (d.PerformanceCore.Bars.Count + d.EfficiencyCore.Bars.Count)
            .Should().BeLessThanOrEqualTo(topology.Processors.Count);
    }

    [Fact]
    public void 总CPU读不到时应显示未知而不是0()
    {
        // 总 CPU 计数器缺失/读取失败时传 null：0% 会被读成「整机空闲」
        var t = Hybrid();
        var d = CpuDetail.From(Frame(t, _ => 10), t, null);

        d.TotalCpuPercent.Should().BeNull();
        d.TotalCpuText.Should().Be("未知");
    }

    [Fact]
    public void 总CPU有值时展示文本应带百分号()
    {
        var t = Hybrid();
        var d = CpuDetail.From(Frame(t, _ => 10), t, 37.6);

        d.TotalCpuPercent.Should().Be(37.6);
        d.TotalCpuText.Should().Be("38%");
    }
}
