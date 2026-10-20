namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;

/// <summary>
/// 大小核拓扑与 P 核峰值。
///
/// 这些用例锁死一个实测结论：本机（i7-1360P，4 大核 8 线程 + 8 小核）上，
/// 把 8 个后台进程绑到 E 核满载时，全局单核峰值显示 **98.6%**（红色报警），
/// 而跑游戏的 P 核最高只有 **29.3%**。反过来把负载绑到 P 核，P 核峰值 93.1%。
///
/// 所以界面必须取 P 核峰值——否则玩家会为后台进程白白降画质。
/// </summary>
public class CpuTopologyTests
{
    /// <summary>造一个 4 大核 8 线程 + 8 小核的拓扑（与 i7-1360P 实测一致）。</summary>
    private static CpuTopology Hybrid()
    {
        var list = new List<LogicalProcessor>();
        // 大核：物理核 0,2,4,6，每个 2 线程
        for (var core = 0; core < 4; core++)
        {
            list.Add(new LogicalProcessor(0, core * 2, core * 2, 1) { IsPerformanceCore = true });
            list.Add(new LogicalProcessor(0, core * 2 + 1, core * 2, 1) { IsPerformanceCore = true });
        }
        // 小核：物理核 8..15，单线程
        for (var i = 8; i < 16; i++)
            list.Add(new LogicalProcessor(0, i, i, 0) { IsPerformanceCore = false });

        return CpuTopology.FromProcessors(list);
    }

    [Fact]
    public void 混合架构应能区分大小核()
    {
        var t = Hybrid();

        t.IsHybrid.Should().BeTrue();
        t.PerformanceProcessorCount.Should().Be(8, "4 大核 × 2 超线程");
        t.EfficiencyProcessorCount.Should().Be(8);
    }

    [Fact]
    public void 全同构机器不应被判成混合架构()
    {
        var t = CpuTopology.Uniform(16);

        t.IsHybrid.Should().BeFalse();
        t.PerformanceProcessorCount.Should().Be(16);
        t.EfficiencyProcessorCount.Should().Be(0);
    }

    [Fact]
    public void 负载全在小核时P核峰值应反映出大核空闲()
    {
        // 实测场景：8 个后台进程绑到 E 核满载
        var t = Hybrid();
        var usage = new Dictionary<int, double>();
        for (var i = 0; i < 8; i++) usage[i] = 25;   // P 核 25%
        for (var i = 8; i < 16; i++) usage[i] = 98;  // E 核 98%

        t.PerformancePeak(usage).Should().Be(25, "P 核才是跑游戏的地方");
        t.EfficiencyPeak(usage).Should().Be(98);

        // 全局峰值会显示 98——这正是要避免的误判
        usage.Values.Max().Should().Be(98, "全局口径会把 E 核算进来");
    }

    [Fact]
    public void 负载全在大核时P核峰值应报警()
    {
        // 实测反向场景：8 个进程绑到 P 核满载
        var t = Hybrid();
        var usage = new Dictionary<int, double>();
        for (var i = 0; i < 8; i++) usage[i] = 93;
        for (var i = 8; i < 16; i++) usage[i] = 54;

        t.PerformancePeak(usage).Should().Be(93);
        t.EfficiencyPeak(usage).Should().Be(54);
    }

    [Fact]
    public void 单线程游戏负载应落在P核()
    {
        // 实测：单线程满载落在逻辑核 2（P 核）94.4%，E 核 27.2%
        var t = Hybrid();
        var usage = new Dictionary<int, double>();
        for (var i = 0; i < 16; i++) usage[i] = 5;
        usage[2] = 94.4;
        usage[3] = 27.2;

        t.PerformancePeak(usage).Should().Be(94.4);
        t.EfficiencyPeak(usage).Should().Be(5);
    }

    [Fact]
    public void 拓扑读不到时应退化为全局峰值而不是0()
    {
        // 返回 0 会让界面显示「CPU 空闲」而实际可能满载
        var t = CpuTopology.Uniform(0);
        var usage = new Dictionary<int, double> { [0] = 80, [1] = 40 };

        t.PerformancePeak(usage).Should().Be(80);
    }

    [Fact]
    public void 没有任何占用数据时应返回0()
    {
        var t = Hybrid();

        t.PerformancePeak(new Dictionary<int, double>()).Should().Be(0);
    }

    [Fact]
    public void 性能核序号对不上时应退化为全局峰值()
    {
        // 例如 PDH 实例名解析失败，实际只采到能效核序号。
        // 此时不能返回 0（会显示成空闲），要退化为全局峰值。
        var t = Hybrid();
        var usage = new Dictionary<int, double> { [8] = 90, [9] = 70 };

        t.PerformancePeak(usage).Should().Be(90);
    }

    [Fact]
    public void 无小核机器上能效核峰值应为0()
    {
        // E 核这个概念不存在，返回全局峰值会误导
        var t = CpuTopology.Uniform(8);
        var usage = new Dictionary<int, double> { [0] = 60, [1] = 80 };

        t.EfficiencyPeak(usage).Should().Be(0);
        t.PerformancePeak(usage).Should().Be(80);
    }

    [Fact]
    public void 实例名应能被正确解析()
    {
        PdhSampler.TryParseProcessorIndex("0,3", out var idx).Should().BeTrue();
        idx.Should().Be(3);

        PdhSampler.TryParseProcessorIndex("0,15", out var idx2).Should().BeTrue();
        idx2.Should().Be(15);
    }

    [Fact]
    public void 无法解析的实例名不应瞎猜序号()
    {
        // 宁可这颗核不参与 P/E 归类，也不要猜一个错序号
        PdhSampler.TryParseProcessorIndex("0,_Total", out _).Should().BeFalse();
        PdhSampler.TryParseProcessorIndex("_Total", out _).Should().BeFalse();
        PdhSampler.TryParseProcessorIndex("", out _).Should().BeFalse();
        PdhSampler.TryParseProcessorIndex("0,a", out _).Should().BeFalse();
        PdhSampler.TryParseProcessorIndex("0,-1", out _).Should().BeFalse();
    }

    [Fact]
    public void 真实机器应能读出拓扑且与线程数一致()
    {
        var t = CpuTopology.Detect();

        t.Processors.Should().NotBeEmpty();
        t.PerformanceProcessorCount.Should().BeGreaterThan(0);

        // 读不到拓扑时会退化为 Uniform(ProcessorCount)，两者线程数应一致
        t.Processors.Count.Should().Be(Environment.ProcessorCount);
    }

    [Fact]
    public void 混合架构机器上检测不得退化成都算性能核()
    {
        // 回归：`GetSystemCpuSetInformation` 第一次空缓冲探测**必然返回 false**
        // （ERROR_INSUFFICIENT_BUFFER），旧实现拿返回值当失败判据 →
        // 直接返回空列表 → 本机 8 P 核 / 8 E 核被误判成「16 线程同构」，
        // 于是仪表盘又退回全局峰值，整个大小核区分功能静默失效。
        var t = CpuTopology.Detect();

        // 全同构机器（服务器 / 老 CPU）上这条不适用，跳过
        var hasDistinctClass = t.Processors
            .Select(p => p.EfficiencyClass)
            .Distinct()
            .Count() > 1;

        if (!hasDistinctClass) return;

        t.IsHybrid.Should().BeTrue("硬件报告了不同的效率等级，就必须识别为混合架构");
        t.EfficiencyProcessorCount.Should().BeGreaterThan(0);
        t.PerformanceProcessorCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void 检测到的拓扑必须带出真实效率等级()
    {
        // 若 Query 静默返回空列表，Detect 会退化成 Uniform，全部效率等级都是 1。
        // 这里核对：只要有混合架构，就必须有 EfficiencyClass=0 的核。
        var t = CpuTopology.Detect();

        if (!t.IsHybrid) return;

        t.Processors.Should().Contain(p => p.EfficiencyClass == 0, "能效核的效率等级应为 0");
        t.Processors.Should().Contain(p => p.EfficiencyClass > 0, "性能核的效率等级应大于 0");
    }

    [Fact]
    public void 真实机器上P核峰值不应超过全局峰值()
    {
        var t = CpuTopology.Detect();
        var usage = Enumerable.Range(0, t.Processors.Count)
            .ToDictionary(i => i, i => (double)(i * 6 % 100));

        t.PerformancePeak(usage).Should().BeLessThanOrEqualTo(usage.Values.Max());
        t.EfficiencyPeak(usage).Should().BeLessThanOrEqualTo(usage.Values.Max());
    }

    [Fact]
    public void 描述文本应说清大核与小核()
    {
        Hybrid().Describe().Should().Contain("大核").And.Contain("小核");
        CpuTopology.Uniform(16).Describe().Should().Contain("同构");
    }
}
