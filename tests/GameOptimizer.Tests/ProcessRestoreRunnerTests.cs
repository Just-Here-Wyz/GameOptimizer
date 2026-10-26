namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 顺序恢复队列：只跑选中的、项间留间隔、单项失败不连坐、启动后必须确认真的起来了。
///
/// 所有用例都用注入的启动服务与假的等待实现，<b>不启动也不结束任何真实进程</b>。
/// </summary>
public class ProcessRestoreRunnerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Verify = TimeSpan.FromMilliseconds(150);

    private static ProcessRestoreRunner Runner(
        Func<string, ProcessLaunchResult> startProcess,
        Func<string, bool>? isProcessRunning = null,
        List<TimeSpan>? delays = null)
        => new(
            startProcess: startProcess,
            isProcessRunning: isProcessRunning ?? (_ => true),
            interval: Interval,
            verifyTimeout: Verify,
            delay: (span, _) =>
            {
                delays?.Add(span);
                return Task.CompletedTask;
            },
            verifyDelay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task 只恢复被选中的目标()
    {
        using var log = new RestoreLogScope();
        var selection = new ProcessRestoreSelection(
        [
            RestoreTestData.Eligible("chrome"),
            RestoreTestData.Eligible("kugou"),
            RestoreTestData.Eligible("WeChat"),
        ]);

        selection.SetSelected(0, true);
        selection.SetSelected(2, true);

        var started = new List<string>();
        var runner = Runner(path => { started.Add(path); return ProcessLaunchResult.Ok; });

        var report = await runner.RunAsync(selection.Selected);

        report.Should().NotBeNull();
        report!.Total.Should().Be(2);
        started.Should().HaveCount(2);
        started.Should().Contain(p => p.Contains("chrome")).And.Contain(p => p.Contains("WeChat"));
        started.Should().NotContain(p => p.Contains("kugou"), "没勾的目标一个都不能启动");
    }

    [Fact]
    public async Task 项与项之间应留出启动间隔()
    {
        using var log = new RestoreLogScope();
        var delays = new List<TimeSpan>();
        var runner = Runner(_ => ProcessLaunchResult.Ok, delays: delays);

        var report = await runner.RunAsync(
            [RestoreTestData.Eligible("a"), RestoreTestData.Eligible("b"), RestoreTestData.Eligible("c")]);

        report!.Total.Should().Be(3);
        delays.Should().HaveCount(2, "3 项之间只等 2 次，最后一项之后不必再等");
        delays.Should().OnlyContain(d => d == Interval);
    }

    [Fact]
    public async Task 已有恢复在执行时应拒绝重复触发()
    {
        using var log = new RestoreLogScope();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ProcessRestoreRunner(
            startProcess: _ => ProcessLaunchResult.Ok,
            isProcessRunning: _ => true,
            interval: Interval,
            verifyTimeout: Verify,
            delay: (_, _) => gate.Task,
            verifyDelay: (_, _) => Task.CompletedTask);

        var first = runner.RunAsync([RestoreTestData.Eligible("a"), RestoreTestData.Eligible("b")]);
        runner.IsRunning.Should().BeTrue();

        var second = await runner.RunAsync([RestoreTestData.Eligible("c")]);
        second.Should().BeNull("同一时刻只允许一批恢复，重复点击必须被挡住");

        gate.SetResult(true);
        var report = await first;
        report!.Total.Should().Be(2, "被拒绝的那次没有执行任何项");
        runner.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 单项启动失败不应阻断后续目标()
    {
        using var log = new RestoreLogScope();
        var started = new List<string>();
        var runner = Runner(path =>
        {
            started.Add(path);
            return path.Contains("bad")
                ? ProcessLaunchResult.Fail("拒绝访问", "Win32Exception", 5)
                : ProcessLaunchResult.Ok;
        });

        var report = await runner.RunAsync(
        [
            RestoreTestData.Eligible("good1"),
            RestoreTestData.Eligible("bad"),
            RestoreTestData.Eligible("good2"),
        ]);

        started.Should().HaveCount(3, "中间一项失败不能让后面的合法目标停摆");
        report!.RestoredCount.Should().Be(2);
        report.FailedCount.Should().Be(1);

        var failed = report.Outcomes.Single(o => o.Status == ProcessRestoreStatus.Failed);
        failed.ErrorType.Should().Be("Win32Exception");
        failed.ErrorCode.Should().Be(5, "错误码要留住，否则所有失败都会被显示成「拒绝访问」");
    }

    [Fact]
    public async Task 启动请求成功但进程没起来时不能算恢复成功()
    {
        using var log = new RestoreLogScope();
        var runner = Runner(_ => ProcessLaunchResult.Ok, isProcessRunning: _ => false);

        var report = await runner.RunAsync([RestoreTestData.Eligible("sleepy")]);

        report!.RestoredCount.Should().Be(0, "提交了启动请求不等于程序真的跑起来了");
        report.FailedCount.Should().Be(1);
        report.Outcomes[0].Status.Should().Be(ProcessRestoreStatus.Unconfirmed);
        report.Outcomes[0].Error.Should().Contain("未检测到");
    }

    [Fact]
    public async Task 不可恢复项应被跳过且不调用启动服务()
    {
        using var log = new RestoreLogScope();
        var started = new List<string>();
        var runner = Runner(path => { started.Add(path); return ProcessLaunchResult.Ok; });

        var report = await runner.RunAsync([RestoreTestData.Eligible("ok"), RestoreTestData.Skipped("gone")]);

        started.Should().ContainSingle();
        report!.SkippedCount.Should().Be(1);
        report.RestoredCount.Should().Be(1);
    }

    [Fact]
    public async Task 日志写入失败不得中断恢复()
    {
        using var log = new RestoreLogScope();
        var started = new List<string>();
        var runner = Runner(path => { started.Add(path); return ProcessLaunchResult.Ok; });

        var previous = ProcessRestoreLog.Sink;
        ProcessRestoreLog.Sink = _ => throw new IOException("磁盘已满");
        try
        {
            var report = await runner.RunAsync([RestoreTestData.Eligible("a"), RestoreTestData.Eligible("b")]);

            report!.RestoredCount.Should().Be(2);
            started.Should().HaveCount(2);
        }
        finally
        {
            ProcessRestoreLog.Sink = previous;
        }
    }

    [Fact]
    public async Task 空选择应返回空报告而不是null()
    {
        using var log = new RestoreLogScope();
        var runner = Runner(_ => ProcessLaunchResult.Ok);

        var report = await runner.RunAsync([]);

        report.Should().NotBeNull();
        report!.Total.Should().Be(0);
        report.Summary.Should().Be("已恢复 0 项，跳过 0 项，失败 0 项。");
    }

    [Fact]
    public async Task 摘要应把成功跳过失败分别计数()
    {
        using var log = new RestoreLogScope();
        var runner = Runner(path => path.Contains("bad")
            ? ProcessLaunchResult.Fail("拒绝访问", "Win32Exception", 5)
            : ProcessLaunchResult.Ok);

        var report = await runner.RunAsync(
        [
            RestoreTestData.Eligible("ok"),
            RestoreTestData.Skipped("gone"),
            RestoreTestData.Eligible("bad"),
        ]);

        report!.Summary.Should().Be("已恢复 1 项，跳过 1 项，失败 1 项。");
    }

    [Fact]
    public async Task 按列表口径统计时没勾的也算跳过()
    {
        using var log = new RestoreLogScope();
        var runner = Runner(_ => ProcessLaunchResult.Ok);

        // 列表 4 行：只勾了 2 行，另 2 行没恢复
        var report = await runner.RunAsync(
            [RestoreTestData.Eligible("a"), RestoreTestData.Eligible("b")]);

        report!.SummaryAgainst(4).Should().Be("已恢复 2 项，跳过 2 项，失败 0 项。");
        report.SummaryAgainst(2).Should().Be("已恢复 2 项，跳过 0 项，失败 0 项。",
            "总数不该小于实际处理数时出现负数");
    }

    [Fact]
    public async Task 恢复成功后应把结果写回原记录()
    {
        using var log = new RestoreLogScope();
        var candidate = RestoreTestData.Eligible("done");
        var runner = Runner(_ => ProcessLaunchResult.Ok);

        await runner.RunAsync([candidate]);

        candidate.Record.Launched.Should().BeTrue("界面靠这条记录判断还有哪些没恢复");
        candidate.Record.Failed.Should().BeFalse();
    }

    [Fact]
    public async Task 同一条记录不应被启动两次()
    {
        using var log = new RestoreLogScope();
        var started = new List<string>();
        var candidate = RestoreTestData.Eligible("once");
        var runner = Runner(path => { started.Add(path); return ProcessLaunchResult.Ok; });

        await runner.RunAsync([candidate]);
        var again = await runner.RunAsync([candidate]);

        started.Should().ContainSingle("重复提交同一目标不能把它再启动一遍");
        again!.SkippedCount.Should().Be(1);
    }

    [Fact]
    public async Task 跳过项不应被标记为已处理()
    {
        using var log = new RestoreLogScope();
        var candidate = RestoreTestData.Skipped("gone");
        var runner = Runner(_ => ProcessLaunchResult.Ok);

        await runner.RunAsync([candidate]);

        // 没处理就该继续留在「待恢复」里，兜底快照才会保留它
        candidate.Record.Launched.Should().BeFalse();
    }
}
