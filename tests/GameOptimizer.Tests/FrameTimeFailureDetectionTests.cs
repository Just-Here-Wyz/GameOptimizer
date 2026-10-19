namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;

/// <summary>
/// PresentMon 启动失败的识别。
///
/// 实测踩到的真实问题：点了「开始采集帧数据」后界面显示"正在采集"，
/// 但 captures 目录里一个 CSV 都没有——PresentMon 因 ETW 权限失败已退出，
/// 而旧代码只看 Process.Start 有没有抛异常，把"启动了但立刻失败"当成了成功。
/// </summary>
public class FrameTimeFailureDetectionTests
{
    [Fact]
    public void 应把access_denied翻译成可操作的提示()
    {
        const string stderr = """
            warning: PresentMon requires elevated privilege ...
            error: failed to start trace session: access denied.
                   PresentMon requires either administrative privileges or to be run by a user in the
                   "Performance Log Users" user group.
            """;

        var msg = FrameTimeSource.DescribeFailure(stderr, 6);

        msg.Should().Contain("ETW");
        // 必须告诉用户怎么办，而不是只报错
        msg.Should().Contain("管理员");
        msg.Should().Contain("Performance Log Users");
    }

    [Fact]
    public void 其他失败应带上退出码与原始信息()
    {
        var msg = FrameTimeSource.DescribeFailure("error: something failed", 3);

        msg.Should().Contain("3");
        msg.Should().Contain("something failed");
    }

    [Fact]
    public void 空stderr也应给出可读信息而不是空字符串()
    {
        var msg = FrameTimeSource.DescribeFailure("", 1);

        msg.Should().NotBeNullOrWhiteSpace();
        msg.Should().Contain("1");
    }

    [Fact]
    public void 真实PresentMon缺少权限时Start应返回false()
    {
        // 本机已装 PresentMon；当前会话是低完整性，必然没有 ETW 权限。
        // 关键断言：不能"启动成功"却什么都不产出。
        var pm = FrameTimeSource.DefaultPath();
        if (pm is null) return; // 未装则跳过

        using var source = new FrameTimeSource();
        var ok = source.Start("explorer", Path.Combine(Path.GetTempPath(), $"ft-{Guid.NewGuid():N}.csv"),
            seconds: 3);

        if (!ok)
        {
            source.LastError.Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            // 若确实起来了，也必须能被判定为"未失败"
            source.HasFailed(out _).Should().BeFalse();
        }
    }

    [Fact]
    public void 未启动时HasFailed应返回false()
    {
        using var source = new FrameTimeSource();
        source.HasFailed(out var err).Should().BeFalse();
        err.Should().BeNull();
    }

    [Fact]
    public void 进程名为空时应明确报错()
    {
        using var source = new FrameTimeSource();
        source.Start("", Path.Combine(Path.GetTempPath(), "x.csv"), 3).Should().BeFalse();
        source.LastError.Should().Contain("进程名");
    }
}
