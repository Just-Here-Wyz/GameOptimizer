namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 崩溃恢复逻辑：电源计划还原失败必须可见、进程拉起失败必须可见、
/// 全部成功才允许清除快照。这里的拉起用注入的假函数，不真的启动进程。
/// </summary>
public class GameModeRecoveryTests
{
    [Fact]
    public void 快照为null时不应做任何事()
    {
        var recovery = new GameModeRecovery(_ => throw new Exception("不该被调用"));

        var errors = recovery.Restore(null, _ => "不该被调用");

        errors.Should().BeEmpty();
    }

    [Fact]
    public void 全部成功时应返回空错误列表()
    {
        var started = new List<string>();
        var recovery = new GameModeRecovery(psi =>
        {
            started.Add(psi.FileName);
            return true;
        });

        var snapshot = new GameModeSnapshot
        {
            PreviousPowerPlanGuid = "g-u-i-d",
            Killed =
            [
                new GameModeKilledProcess { Name = "WeChat", LaunchPath = @"C:\x\WeChat.exe" },
                new GameModeKilledProcess { Name = "chrome", LaunchPath = @"C:\x\chrome.exe" },
            ],
        };

        var errors = recovery.Restore(snapshot, _ => null);

        errors.Should().BeEmpty();
        started.Should().Equal(@"C:\x\WeChat.exe", @"C:\x\chrome.exe");
    }

    [Fact]
    public void 电源计划还原失败必须出现在错误里()
    {
        var recovery = new GameModeRecovery(_ => true);
        var snapshot = new GameModeSnapshot { PreviousPowerPlanGuid = "g-u-i-d" };

        var errors = recovery.Restore(snapshot, _ => "切换未生效");

        errors.Should().ContainSingle().Which.Should().Contain("还原失败").And.Contain("切换未生效");
    }

    [Fact]
    public void 电源计划还原函数抛异常也必须可见()
    {
        var recovery = new GameModeRecovery(_ => true);
        var snapshot = new GameModeSnapshot { PreviousPowerPlanGuid = "g-u-i-d" };

        var errors = recovery.Restore(snapshot, _ => throw new InvalidOperationException("powercfg 调用失败"));

        errors.Should().ContainSingle().Which.Should().Contain("还原失败").And.Contain("powercfg 调用失败");
    }

    [Fact]
    public void 进程拉起失败必须出现在错误里()
    {
        var recovery = new GameModeRecovery(_ => false);
        var snapshot = new GameModeSnapshot
        {
            Killed = [new GameModeKilledProcess { Name = "WeChat", LaunchPath = @"C:\x\WeChat.exe" }],
        };

        var errors = recovery.Restore(snapshot, null);

        errors.Should().ContainSingle().Which.Should().Contain("未能拉起").And.Contain("WeChat");
    }

    [Fact]
    public void 拉起抛异常时必须记录进程名与原因()
    {
        var recovery = new GameModeRecovery(_ => throw new InvalidOperationException("文件已不存在"));
        var snapshot = new GameModeSnapshot
        {
            Killed = [new GameModeKilledProcess { Name = "chrome", LaunchPath = @"C:\x\chrome.exe" }],
        };

        var errors = recovery.Restore(snapshot, null);

        errors.Should().ContainSingle().Which.Should().Contain("chrome").And.Contain("拉起失败").And.Contain("文件已不存在");
    }

    [Fact]
    public void 无路径的进程不应尝试拉起()
    {
        var recovery = new GameModeRecovery(_ => throw new Exception("不该被调用"));
        var snapshot = new GameModeSnapshot
        {
            Killed = [new GameModeKilledProcess { Name = "unknown", LaunchPath = "" }],
        };

        var errors = recovery.Restore(snapshot, null);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void 多个失败项应全部收集齐()
    {
        var recovery = new GameModeRecovery(_ => false);
        var snapshot = new GameModeSnapshot
        {
            PreviousPowerPlanGuid = "g-u-i-d",
            Killed =
            [
                new GameModeKilledProcess { Name = "a", LaunchPath = @"C:\a.exe" },
                new GameModeKilledProcess { Name = "b", LaunchPath = @"C:\b.exe" },
            ],
        };

        var errors = recovery.Restore(snapshot, _ => "失败");

        errors.Should().HaveCount(3);
    }
}
