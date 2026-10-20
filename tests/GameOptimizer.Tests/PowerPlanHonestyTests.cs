namespace GameOptimizer.Tests;

using System.Diagnostics;
using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 电源计划回读校验（bug 修复【2】）：
/// Apply 读不到原计划必须取消切换，Revert 回读失败 / 不一致必须抛错，
/// 不能静默假装还原成功。
/// </summary>
public class PowerPlanHonestyTests
{
    [Fact]
    public void Apply前读不到原计划必须抛错取消切换()
    {
        var act = () => PowerPlanAction.EnsurePreviousReadable(null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*无法回读*可回滚*");
    }

    [Fact]
    public void Apply前读到原计划应放行()
    {
        var act = () => PowerPlanAction.EnsurePreviousReadable("381b4222-f694-41f0-9685-ff5bb260df2e");

        act.Should().NotThrow();
    }

    [Fact]
    public void Revert后回读为null必须抛错而不是静默成功()
    {
        // 这是本次修复的核心场景：CurrentPlanGuid() 吞掉异常返回 null，
        // 旧代码 null?.Equals(...)==true 为 false 却不抛，用户以为还原了实际没有
        var act = () => PowerPlanAction.EnsureReverted(null, "381b4222-f694-41f0-9685-ff5bb260df2e");

        act.Should().Throw<InvalidOperationException>().WithMessage("*无法回读*手动确认*");
    }

    [Fact]
    public void Revert后回读到别的计划必须抛错()
    {
        var act = () => PowerPlanAction.EnsureReverted(
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "381b4222-f694-41f0-9685-ff5bb260df2e");

        act.Should().Throw<InvalidOperationException>().WithMessage("*还原失败*");
    }

    [Fact]
    public void Revert后回读到原计划应放行()
    {
        const string guid = "381b4222-f694-41f0-9685-ff5bb260df2e";

        var act = () => PowerPlanAction.EnsureReverted(guid, guid);

        act.Should().NotThrow();
    }
}

/// <summary>
/// 进程结束的三态分类（bug 修复【1】）：
/// 真退出 → Exited；Kill 抛拒绝访问 → Denied；Kill 没抛但进程仍在 → Stuck。
/// </summary>
public class EndProcessKillTests
{
    [Fact]
    public void 正常进程被结束应归类为Exited()
    {
        // 用 ping 副本：临时目录下不被必要进程保护拦截，Kill 后能正常退出
        var exe = Path.Combine(Path.GetTempPath(), $"go-kill-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe, overwrite: true);

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-n 60 127.0.0.1",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(start)!;

            var result = EndProcessAction.TryKill(proc);

            result.Should().Be(EndProcessAction.KillResult.Exited);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void 已退出的进程应归类为Exited()
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        proc.Start();
        proc.WaitForExit(5000);

        var result = EndProcessAction.TryKill(proc);

        result.Should().Be(EndProcessAction.KillResult.Exited);
    }
}
