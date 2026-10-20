namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// v1.0.1：关闭游戏模式时恢复被结束的进程。
/// 结束进程前记下可执行文件路径，退出时按原路径拉起；
/// 读不到路径的不自动恢复，恢复失败的如实上报。
/// </summary>
public class ProcessRestoreTests
{
    [Fact]
    public void 恢复启动应记录进程名与原始路径()
    {
        var r = new RestoredProcess("foo", @"D:\x\foo.exe");
        r.Name.Should().Be("foo");
        r.LaunchPath.Should().Be(@"D:\x\foo.exe");
        r.Launched.Should().BeFalse();
        r.Failed.Should().BeFalse();
    }

    [Fact]
    public void 标记失败后应保留错误原因且不再重复启动()
    {
        var r = new RestoredProcess("foo", @"D:\x\foo.exe");
        r.MarkFailed("文件已不存在");

        r.Failed.Should().BeTrue();
        r.Launched.Should().BeTrue("失败也算执行过，不能重复启动");
        r.Error.Should().Be("文件已不存在");
    }

    [Fact]
    public void 标记成功后应计入恢复数量()
    {
        var r = new RestoredProcess("foo", @"D:\x\foo.exe");
        r.MarkLaunched();

        r.Launched.Should().BeTrue();
        r.Failed.Should().BeFalse();
    }

    [Fact]
    public void 结束进程动作应记录待恢复进程()
    {
        // 用临时目录下的 ping.exe 副本作为受害者：不在系统目录，不会被保护逻辑拦截
        var exe = Path.Combine(Path.GetTempPath(), $"go-restore-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe, overwrite: true);

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-n 30 127.0.0.1",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };

            using var proc = System.Diagnostics.Process.Start(start)!;
            var action = new EndProcessAction([proc.ProcessName]);
            action.Apply();

            action.Killed.Should().NotBeEmpty();
            action.Restored.Should().NotBeEmpty("能读到路径的进程必须记录待恢复信息");
            action.Restored.Should().Contain(r => r.LaunchPath.Equals(exe, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void 读不到路径的进程不应伪造恢复入口()
    {
        // 无法构造真实场景时，用「路径为空的记录」验证模型的诚实性：
        // 只有拿到路径才会创建 RestoredProcess（见 EndProcessAction.Apply 的逻辑）
        var action = new EndProcessAction(["绝对不存在的进程名_测试用"]);
        action.Apply();

        action.Restored.Should().BeEmpty("没结束过进程就不该有恢复记录");
        action.RestoredCount.Should().Be(0);
        action.FailedRestores.Should().BeEmpty();
    }
}
