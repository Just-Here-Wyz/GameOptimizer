namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;
using GameOptimizer.Core.Library;

/// <summary>
/// CPU 快速优化执行器。
///
/// 这些用例唯一关心的事：**没有确认就什么都不做**。
/// 空名单、null、纯保护名单都必须零副作用，重跑也不会产生改动。
/// （真正的结束进程路径依赖真实用户级进程，不在单测里造，避免误伤本机程序。）
/// </summary>
public class CpuQuickOptimizeRunnerTests
{
    [Fact]
    public void 空名单不应产生任何改动()
    {
        var runner = new CpuQuickOptimizeRunner();

        var result = runner.Execute([]);

        result.DidChangeAnything.Should().BeFalse();
        result.Error.Should().BeNull();
        runner.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void null名单同样零副作用()
    {
        var runner = new CpuQuickOptimizeRunner();

        runner.Execute(null).DidChangeAnything.Should().BeFalse();
        runner.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void 保护名单里的进程不得被提交结束()
    {
        // explorer 即便被传进来也必须被纵深防御拦掉
        BackgroundProcessWhitelist.IsProtected("explorer").Should().BeTrue();

        var runner = new CpuQuickOptimizeRunner();
        var result = runner.Execute(["explorer"]);

        result.DidChangeAnything.Should().BeFalse();
        runner.ExecutedNames.Should().BeEmpty("保护名单在进入动作前就被过滤掉");
    }

    [Fact]
    public void 不存在的进程应报没有改动而不是假装成功()
    {
        var runner = new CpuQuickOptimizeRunner();

        var result = runner.Execute(["gameoptimizer-test-not-a-real-process"]);

        result.DidChangeAnything.Should().BeFalse();
        result.Summary.Should().Contain("没有进程被结束");
        result.Summary.Should().Contain("系统状态未改变");
    }

    [Fact]
    public void 没有改动时回滚应返回空失败列表()
    {
        var runner = new CpuQuickOptimizeRunner();

        runner.Revert().Should().BeEmpty();
    }

    [Fact]
    public void 重复名单应去重()
    {
        var runner = new CpuQuickOptimizeRunner();

        runner.Execute(["gameoptimizer-test-a", "gameoptimizer-test-a", "  "]);

        runner.ExecutedNames.Should().Equal("gameoptimizer-test-a");
        runner.HasChanges.Should().BeFalse("该进程不存在，结束动作不会产生改动");
    }
}
