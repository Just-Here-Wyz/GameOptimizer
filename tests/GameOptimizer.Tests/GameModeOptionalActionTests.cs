namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;
using GameOptimizer.Core.Library;

/// <summary>
/// 可选动作（AddOptional）的语义：
/// 失败不中断游戏模式，但必须进入 Warnings 让界面展示，绝不静默消失。
/// </summary>
public class GameModeOptionalActionTests
{
    [Fact]
    public void 可选动作失败不应挡住游戏模式()
    {
        var log = new List<string>();
        var controller = new GameModeController();
        controller.Add(new RecordingAction("必需", log));
        controller.AddOptional(new RecordingAction("可选", log, throws: true));

        var errors = controller.Enter();

        errors.Should().BeEmpty("可选动作失败不该回滚整个游戏模式");
        controller.IsActive.Should().BeTrue();
        log.Should().Equal("apply:必需");
    }

    [Fact]
    public void 可选动作失败必须出现在警告里()
    {
        var controller = new GameModeController();
        controller.AddOptional(new RecordingAction("可选", new List<string>(), throws: true));

        controller.Enter();

        controller.Warnings.Should().ContainSingle()
            .Which.Should().Contain("可选").And.Contain("未生效");
    }

    [Fact]
    public void 必需动作失败仍应回滚包括可选动作在内的全部改动()
    {
        var log = new List<string>();
        var controller = new GameModeController();
        controller.AddOptional(new RecordingAction("可选", log));
        controller.Add(new RecordingAction("必需", log, throws: true));

        var errors = controller.Enter();

        errors.Should().ContainSingle().Which.Should().Contain("必需");
        log.Should().Equal("apply:可选", "revert:可选");
        controller.IsActive.Should().BeFalse();
    }

    [Fact]
    public void 可选动作成功时不应产生警告()
    {
        var log = new List<string>();
        var controller = new GameModeController();
        controller.AddOptional(new RecordingAction("可选", log));

        controller.Enter().Should().BeEmpty();
        controller.Warnings.Should().BeEmpty();
        log.Should().Equal("apply:可选");
    }

    [Fact]
    public void 重新进入时应清空上一次的警告()
    {
        // 上一次失败的原因不能残留，否则界面会一直显示一条早已失效的告警
        var controller = new GameModeController();
        controller.AddOptional(new RecordingAction("可选", new List<string>(), throws: true));
        controller.Enter();
        controller.Warnings.Should().NotBeEmpty();

        controller.Exit();
        controller.Enter();

        controller.Warnings.Should().ContainSingle("本轮同样失败，但只应有一条本轮记录");
    }

    [Fact]
    public void 可选动作应能被识别出来()
    {
        var required = new RecordingAction("必需", []);
        var optional = new RecordingAction("可选", []);
        var controller = new GameModeController();
        controller.Add(required);
        controller.AddOptional(optional);

        controller.IsOptional(required).Should().BeFalse();
        controller.IsOptional(optional).Should().BeTrue();
        controller.Actions.Should().HaveCount(2, "可选动作同样参与执行与回滚");
    }

    [Fact]
    public void 内存清理可作为可选动作接入游戏模式()
    {
        // 受限令牌下全部进程都可能 AccessDenied：此时游戏模式仍要能开起来
        var fake = new FakeWorkingSetReleaser().Add(1001, "locked", 200L * 1024 * 1024);
        fake.Outcomes[1001] = new ReleaseOutcome(ReleaseStatus.AccessDenied, 5, "拒绝访问");

        var memory = new MemoryCleanAction(fake, new MemoryCleanOptions
        {
            SelfProcessId = 0,
            ServiceNames = new HashSet<string>(),
            ForegroundProcessId = () => null,
        });

        var controller = new GameModeController();
        controller.AddOptional(memory);

        controller.Enter().Should().BeEmpty();
        controller.IsActive.Should().BeTrue();
        controller.Warnings.Should().ContainSingle().Which.Should().Contain("释放闲置内存");
    }
}
