namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 启动自我提权的判定规则。
///
/// 这段逻辑一旦写错，后果不是难看而是危险：
/// · 判成「不需要提权」→ 游戏模式全被拒绝访问，界面却报已开启（假成功）；
/// · 判成「次次都需要提权」→ UAC 被拒后无限重启自己，用户关不掉程序。
/// 所以两条边界各有一条用例钉住。
/// </summary>
public class ElevationLauncherTests
{
    [Fact]
    public void 已提权时不应重启()
    {
        ElevationLauncher.ShouldRelaunch(isElevated: true, args: []).Should().BeFalse();
    }

    [Fact]
    public void 未提权且未带哨兵时应重启自身()
    {
        ElevationLauncher.ShouldRelaunch(isElevated: false, args: null).Should().BeTrue();
        ElevationLauncher.ShouldRelaunch(isElevated: false, args: ["--render", "out"]).Should().BeTrue();
    }

    [Fact]
    public void 已带哨兵时不得再次重启()
    {
        // 用户取消 UAC 后重启会立刻失败回到这里；再重启就是死循环
        ElevationLauncher.ShouldRelaunch(isElevated: false, args: [ElevationLauncher.RelaunchSwitch])
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("--elevated-relaunch")]
    [InlineData("--ELEVATED-RELAUNCH")]
    public void 哨兵判定应不区分大小写(string arg)
    {
        ElevationLauncher.HasRelaunchSwitch([arg]).Should().BeTrue();
    }

    [Fact]
    public void 重启参数应保留原参数并只追加一个哨兵()
    {
        var args = ElevationLauncher.BuildRelaunchArguments(["--foo", ElevationLauncher.RelaunchSwitch, "bar"]);

        args.Should().Equal("--foo", "bar", ElevationLauncher.RelaunchSwitch);
        args.Count(a => a == ElevationLauncher.RelaunchSwitch).Should().Be(1);
    }

    [Fact]
    public void 空参数时也要带上哨兵()
    {
        ElevationLauncher.BuildRelaunchArguments(null).Should().Equal(ElevationLauncher.RelaunchSwitch);
    }

    [Fact]
    public void 用户取消UAC的错误码应被识别()
    {
        ElevationLauncher.IsUserCancelled(ElevationLauncher.ErrorCancelled).Should().BeTrue();
        ElevationLauncher.IsUserCancelled(5).Should().BeFalse();
    }

    [Fact]
    public void 未提权提示必须可行动()
    {
        // 只说「权限不足」用户不知道怎么办；必须给出「右键以管理员身份运行」
        ElevationLauncher.NotElevatedHint.Should().Contain("管理员");
    }
}
