namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Collection;

/// <summary>
/// CPU 快速优化的「建议」生成规则。
///
/// 这些用例守的是安全边界，而不是文案：
/// 系统进程、安全软件、游戏本体、读不到路径的进程**一律不给结束入口**，
/// 没有安全动作时明确说「没有」，绝不为了按钮去凑一个动作。
/// </summary>
public class CpuOptimizePlannerTests
{
    private static CpuDiagnosis HighLoad() => new(
        CpuLoadLevel.HighLoad,
        92,
        new CpuWindowStats(90, 30, 0, 29, 0),
        12,
        12,
        [],
        [],
        "持续高负载",
        ["总体占用已连续 12 秒 ≥80%"],
        "采样正常",
        "30 秒窗口：有效 30 帧，无间断");

    private static ProcessCpuUsage Process(
        string name,
        double percent,
        bool isProtected = false,
        string? path = @"C:\Apps\app.exe",
        string? reason = null)
        => new(1000, name, percent, path, isProtected, isProtected ? reason ?? "系统必要进程" : null);

    [Fact]
    public void 没有诊断数据时不得产生任何动作()
    {
        var plan = CpuOptimizePlanner.Build(null, [Process("chrome", 20)], 16);

        plan.Items.Should().BeEmpty();
        plan.HasExecutableAction.Should().BeFalse();
        plan.Summary.Should().Contain("不做任何改动");
        plan.Notes.Should().Contain(n => n.Contains("数据不可用"));
    }

    [Fact]
    public void 采集失败时同样不给动作()
    {
        var empty = CpuDiagnosis.Empty("采不到总 CPU");

        var plan = CpuOptimizePlanner.Build(empty, [Process("chrome", 20)], 16);

        plan.Items.Should().BeEmpty();
    }

    [Fact]
    public void 系统进程即使占用很高也不给结束入口()
    {
        var processes = new[]
        {
            Process("MsMpEng", 30, isProtected: true, reason: "安全软件"),
        };

        var plan = CpuOptimizePlanner.Build(HighLoad(), processes, 16);

        plan.Items.Should().BeEmpty();
        plan.Notes.Should().Contain(n => n.Contains("MsMpEng") && n.Contains("不提供结束操作"));
    }

    [Fact]
    public void 读不到路径的进程不给结束入口()
    {
        // 结束之后拉不回来，等于制造无法回滚的改动
        var processes = new[] { Process("unknownapp", 12, path: null) };

        var plan = CpuOptimizePlanner.Build(HighLoad(), processes, 16);

        plan.Items.Should().BeEmpty();
        plan.Notes.Should().Contain(n => n.Contains("读不到可执行文件路径"));
    }

    [Fact]
    public void 占用低于阈值的进程不列为候选()
    {
        var processes = new[] { Process("noteapp", 1.5) };

        var plan = CpuOptimizePlanner.Build(HighLoad(), processes, 16);

        plan.Items.Should().BeEmpty();
        plan.HasExecutableAction.Should().BeFalse();
    }

    [Fact]
    public void 高占用且可恢复的进程应给出带依据的建议()
    {
        var processes = new[] { Process("videosoft", 22.5) };

        var plan = CpuOptimizePlanner.Build(HighLoad(), processes, 16);

        plan.Items.Should().ContainSingle();
        var item = plan.Items[0];

        item.CanExecute.Should().BeTrue();
        item.ProcessName.Should().Be("videosoft");
        item.Evidence.Should().Contain("22.5%").And.Contain("16 逻辑核");
        item.Impact.Should().Contain("未保存");
        item.RevertText.Should().Contain("重新打开");
    }

    [Fact]
    public void 最多只列三项候选()
    {
        var processes = new[]
        {
            Process("a1", 30), Process("a2", 20), Process("a3", 15), Process("a4", 10), Process("a5", 8),
        };

        var plan = CpuOptimizePlanner.Build(HighLoad(), processes, 16);

        plan.Items.Should().HaveCount(CpuOptimizePlanner.MaxCandidates);
        plan.Items.Select(i => i.MeasuredPercent).Should().BeInDescendingOrder();
    }

    [Fact]
    public void 永远不得声称能提升帧率()
    {
        var plan = CpuOptimizePlanner.Build(HighLoad(), [Process("videosoft", 22)], 16);

        plan.Notes.Should().Contain(n => n.Contains("不承诺"));
        plan.Items.Should().NotContain(i => i.Impact.Contains("帧率") || i.Evidence.Contains("帧率"));
    }

    [Fact]
    public void 明确声明不改优先级与频率()
    {
        var plan = CpuOptimizePlanner.Build(HighLoad(), [], 16);

        plan.Notes.Should().Contain(n => n.Contains("优先级") && n.Contains("频率"));
    }

    [Fact]
    public void 没有候选时应明确说没有可执行动作()
    {
        var plan = CpuOptimizePlanner.Build(HighLoad(), [Process("idleapp", 0.5)], 16);

        plan.HasExecutableAction.Should().BeFalse();
        plan.Summary.Should().Contain("没有可安全执行");
    }

    [Fact]
    public void 进程数据尚未就绪时不下结论()
    {
        var plan = CpuOptimizePlanner.Build(HighLoad(), [], 16);

        plan.Items.Should().BeEmpty();
        plan.Summary.Should().Contain("不提供操作");
    }
}
