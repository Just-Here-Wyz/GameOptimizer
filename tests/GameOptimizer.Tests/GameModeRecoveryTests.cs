namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;
using GameOptimizer.Core.Library;

/// <summary>
/// 崩溃恢复逻辑：电源计划还原失败必须可见、进程拉起失败必须可见、
/// 全部成功才允许清除快照。这里的拉起用注入的假函数，不真的启动进程。
///
/// v2.4.1 起快照里的记录不再被无条件启动：先过资格判定（文件在不在、是不是系统组件、
/// 是否已在运行），跳过项单独上报且不算「没还原完」。
/// </summary>
public class GameModeRecoveryTests
{
    /// <summary>资格判定用的假环境：假设文件都在、进程都没在跑。</summary>
    private static ProcessRestoreEnvironment Env(
        Func<string, bool>? fileExists = null,
        Func<string, bool>? isProcessRunning = null)
        => new(
            fileExists: fileExists ?? (_ => true),
            isProcessRunning: isProcessRunning ?? (_ => false),
            readMetadata: _ => ProcessFileMetadata.Empty,
            systemRoot: @"C:\Windows",
            packageRoot: @"C:\Program Files\WindowsApps");

    [Fact]
    public void 快照为null时不应做任何事()
    {
        var recovery = new GameModeRecovery(_ => throw new Exception("不该被调用"), Env());

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
        }, Env());

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
        recovery.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void 电源计划还原失败必须出现在错误里()
    {
        var recovery = new GameModeRecovery(_ => true, Env());
        var snapshot = new GameModeSnapshot { PreviousPowerPlanGuid = "g-u-i-d" };

        var errors = recovery.Restore(snapshot, _ => "切换未生效");

        errors.Should().ContainSingle().Which.Should().Contain("还原失败").And.Contain("切换未生效");
    }

    [Fact]
    public void 电源计划还原函数抛异常也必须可见()
    {
        var recovery = new GameModeRecovery(_ => true, Env());
        var snapshot = new GameModeSnapshot { PreviousPowerPlanGuid = "g-u-i-d" };

        var errors = recovery.Restore(snapshot, _ => throw new InvalidOperationException("powercfg 调用失败"));

        errors.Should().ContainSingle().Which.Should().Contain("还原失败").And.Contain("powercfg 调用失败");
    }

    [Fact]
    public void 进程拉起失败必须出现在错误里()
    {
        var recovery = new GameModeRecovery(_ => false, Env());
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
        var recovery = new GameModeRecovery(_ => throw new InvalidOperationException("文件已不存在"), Env());
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
        var recovery = new GameModeRecovery(_ => throw new Exception("不该被调用"), Env());
        var snapshot = new GameModeSnapshot
        {
            Killed = [new GameModeKilledProcess { Name = "unknown", LaunchPath = "" }],
        };

        var errors = recovery.Restore(snapshot, null);

        errors.Should().BeEmpty();
        recovery.Skipped.Should().ContainSingle().Which.Should().Contain("unknown");
    }

    [Fact]
    public void 多个失败项应全部收集齐()
    {
        var recovery = new GameModeRecovery(_ => false, Env());
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

    [Fact]
    public void 文件已删除的快照记录不应被启动()
    {
        // 落盘记录 ≠ 现在还能启动：路径还在，文件早被卸载了，硬启动只会弹错误框
        var started = new List<string>();
        var recovery = new GameModeRecovery(psi => { started.Add(psi.FileName); return true; },
            Env(fileExists: _ => false));

        var snapshot = new GameModeSnapshot
        {
            Killed = [new GameModeKilledProcess { Name = "old", LaunchPath = @"C:\x\old.exe" }],
        };

        var errors = recovery.Restore(snapshot, null);

        errors.Should().BeEmpty("跳过项不是「没还原完」，不能让快照永远留着");
        started.Should().BeEmpty();
        recovery.Skipped.Should().ContainSingle().Which.Should().Contain("old").And.Contain("已不存在");
    }

    [Fact]
    public void 已在运行的进程不应被重复拉起()
    {
        var started = new List<string>();
        var recovery = new GameModeRecovery(psi => { started.Add(psi.FileName); return true; },
            Env(isProcessRunning: name => name == "chrome"));

        var snapshot = new GameModeSnapshot
        {
            Killed = [new GameModeKilledProcess { Name = "chrome", LaunchPath = @"C:\x\chrome.exe" }],
        };

        recovery.Restore(snapshot, null).Should().BeEmpty();
        started.Should().BeEmpty();
        recovery.Skipped.Should().ContainSingle().Which.Should().Contain("已在运行");
    }

    [Fact]
    public void 同一目标重复出现时只启动一次()
    {
        var started = new List<string>();
        var recovery = new GameModeRecovery(psi => { started.Add(psi.FileName); return true; }, Env());

        var snapshot = new GameModeSnapshot
        {
            Killed =
            [
                new GameModeKilledProcess { Name = "a", LaunchPath = @"D:\Apps\a.exe" },
                new GameModeKilledProcess { Name = "a", LaunchPath = @"d:/apps/A.EXE" },
            ],
        };

        recovery.Restore(snapshot, null).Should().BeEmpty();
        started.Should().ContainSingle();
    }

    [Fact]
    public void 应用商店应用不应按包内exe启动()
    {
        var started = new List<string>();
        var recovery = new GameModeRecovery(psi => { started.Add(psi.FileName); return true; }, Env());

        var snapshot = new GameModeSnapshot
        {
            Killed =
            [
                new GameModeKilledProcess
                {
                    Name = "app",
                    LaunchPath = @"C:\Program Files\WindowsApps\Vendor.App_1.0.0.0_x64__abc\App.exe",
                },
            ],
        };

        recovery.Restore(snapshot, null).Should().BeEmpty();
        started.Should().BeEmpty("包目录里的 exe 受 ACL 保护且依赖包身份，直接启动只会失败");
        recovery.Skipped.Should().ContainSingle().Which.Should().Contain("WindowsApps");
    }
}
