namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 游戏模式按钮呈现规则的用例。
///
/// 这些规则原本散在 XAML 触发器里，改不动也测不了：
/// 「处理中显示哪个文案」「处理中能不能再点」「按钮什么颜色」全都只能靠肉眼看。
/// 抽成纯函数后，连点导致开→关乱跳这类问题至少能在测试里挡住一半。
/// </summary>
public class GameModeButtonPresentationTests
{
    [Theory]
    [InlineData(false, false, "开启游戏模式")]
    [InlineData(false, true, "关闭游戏模式")]
    [InlineData(true, false, "开启中…")]
    [InlineData(true, true, "关闭中…")]
    public void 按钮文案应随状态与处理方向变化(bool busy, bool on, string expected)
    {
        // busy 时方向由 entering 决定：on=false 表示这次是「开启中」
        var text = GameModeButtonPresentation.ButtonText(busy, on, entering: !on);
        text.Should().Be(expected);
    }

    [Fact]
    public void 处理中不允许再次点击()
    {
        // 连点会叠加出「开了又关」——UI 线程恢复后队列里的点击必须被忽略
        GameModeButtonPresentation.CanToggle(true).Should().BeFalse();
        GameModeButtonPresentation.CanToggle(false).Should().BeTrue();
    }

    [Fact]
    public void 按钮底色应区分未开启已开启与处理中()
    {
        GameModeButtonPresentation.ButtonBackgroundHex(busy: false, on: false)
            .Should().Be(GameModeButtonPresentation.AccentHex);
        GameModeButtonPresentation.ButtonBackgroundHex(busy: false, on: true)
            .Should().Be(GameModeButtonPresentation.DangerHex);
        GameModeButtonPresentation.ButtonBackgroundHex(busy: true, on: false)
            .Should().Be(GameModeButtonPresentation.BusyHex);
    }

    [Fact]
    public void 开启后必须是红色_关闭态不能是红色()
    {
        // 用户明确要求「开启后变红」：红色同时是「再点一下会还原」的警示
        GameModeButtonPresentation.ButtonBackgroundHex(false, true)
            .Should().NotBe(GameModeButtonPresentation.AccentHex);
        GameModeButtonPresentation.ButtonBackgroundHex(false, false)
            .Should().NotBe(GameModeButtonPresentation.DangerHex);
    }

    [Fact]
    public void 状态行颜色应四态可分()
    {
        var working = GameModeButtonPresentation.StatusHex(GameModePhase.Working);
        var on = GameModeButtonPresentation.StatusHex(GameModePhase.On);
        var failed = GameModeButtonPresentation.StatusHex(GameModePhase.Failed);
        var idle = GameModeButtonPresentation.StatusHex(GameModePhase.Idle);

        new[] { working, on, failed, idle }.Distinct().Should().HaveCount(4);
        failed.Should().Be(GameModeButtonPresentation.ErrorHex);
        idle.Should().Be(GameModeButtonPresentation.MutedHex);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 处理中的状态行必须说明在等什么(bool entering)
    {
        // 只说「请稍候」等于没说：用户需要知道这几秒在做什么
        var status = GameModeButtonPresentation.WorkingStatus(entering);
        status.Should().Contain("正在");
        status.Should().Contain("游戏模式");
    }
}
