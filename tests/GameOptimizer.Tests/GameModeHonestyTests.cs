namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>模拟"报告成功但实际没生效"的动作，用于验证虚假成功必须被暴露。</summary>
internal sealed class LyingAction : IGameModeAction
{
    /// <summary>故意永远为 false：模拟"Apply 报告成功但实际没生效"。</summary>
    private const bool ActuallyActive = false;

    public string Name => "假成功动作";
    public string Description => "Apply 不抛异常，但状态没变";
    public int RevertCount { get; private set; }

    public void Apply() { /* 什么都不做，静默失败 */ }
    public void Revert() => RevertCount++;
    public bool IsActive() => ActuallyActive;
}

/// <summary>回滚会失败的动作。</summary>
internal sealed class BrokenRevertAction : IGameModeAction
{
    public string Name => "回滚失败动作";
    public string Description => "Revert 抛异常";

    public void Apply() { }
    public void Revert() => throw new InvalidOperationException("还原被拒绝");
    public bool IsActive() => true;
}

public class GameModeHonestyTests
{
    [Fact]
    public void 回滚失败必须返回错误而不是静默吞掉()
    {
        // 电源计划未还原却提示"已还原"比不还原更糟
        var c = new GameModeController();
        c.Add(new BrokenRevertAction());
        c.Enter();

        var errors = c.ExitWithReport();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("还原失败");
        errors[0].Should().Contain("还原被拒绝");
    }

    [Fact]
    public void 回滚失败后状态仍应被清理以避免卡在已激活()
    {
        var c = new GameModeController();
        c.Add(new BrokenRevertAction());
        c.Enter();

        c.ExitWithReport();

        c.IsActive.Should().BeFalse();
        c.Applied.Should().BeEmpty();
    }

    [Fact]
    public void 退出时应把所有动作的错误都收集齐而不是首个就返回()
    {
        var c = new GameModeController();
        c.Add(new BrokenRevertAction());
        c.Add(new BrokenRevertAction());
        c.Enter();

        c.ExitWithReport().Should().HaveCount(2);
    }

    [Fact]
    public void Exit不抛异常以兼容旧调用()
    {
        var c = new GameModeController();
        c.Add(new BrokenRevertAction());
        c.Enter();

        var act = () => c.Exit();

        act.Should().NotThrow();
        c.IsActive.Should().BeFalse();
    }

    [Fact]
    public void 静默失败的动作不应被当成已生效()
    {
        // 这是实测踩到的真实问题：powercfg 失败但退出码为 0，
        // 界面显示"已开启"而系统毫无变化。动作自己必须能识别出没生效。
        var lying = new LyingAction();
        lying.Apply();

        lying.IsActive().Should().BeFalse();
    }

    [Fact]
    public void 动作列表为空时进入应成功且无错误()
    {
        var c = new GameModeController();
        c.Enter().Should().BeEmpty();
        c.IsActive.Should().BeTrue();
    }
}
