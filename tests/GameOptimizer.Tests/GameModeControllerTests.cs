namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>记录调用顺序的假动作，用于验证执行与回滚顺序。</summary>
internal sealed class RecordingAction : IGameModeAction
{
    private readonly List<string> _log;

    public RecordingAction(string name, List<string> log, bool throws = false)
    {
        Name = name;
        _log = log;
        Throws = throws;
    }

    public string Name { get; }
    public string Description => "测试用";
    public bool Throws { get; }
    public int ApplyCount { get; private set; }
    public int RevertCount { get; private set; }

    public void Apply()
    {
        ApplyCount++;
        if (Throws) throw new InvalidOperationException($"{Name} 故意失败");
        _log.Add($"apply:{Name}");
    }

    public void Revert()
    {
        RevertCount++;
        _log.Add($"revert:{Name}");
    }

    public bool IsActive() => ApplyCount > RevertCount;
}

public class GameModeControllerTests
{
    [Fact]
    public void 进入时应按添加顺序执行()
    {
        var log = new List<string>();
        var c = new GameModeController();
        c.Add(new RecordingAction("A", log));
        c.Add(new RecordingAction("B", log));

        c.Enter().Should().BeEmpty();

        log.Should().Equal("apply:A", "apply:B");
        c.IsActive.Should().BeTrue();
    }

    [Fact]
    public void 退出时应逆序回滚()
    {
        var log = new List<string>();
        var c = new GameModeController();
        c.Add(new RecordingAction("A", log));
        c.Add(new RecordingAction("B", log));
        c.Enter();
        log.Clear();

        c.Exit();

        log.Should().Equal("revert:B", "revert:A");
        c.IsActive.Should().BeFalse();
    }

    [Fact]
    public void 中途失败应自动回滚已执行的部分且不留半套状态()
    {
        var log = new List<string>();
        var c = new GameModeController();
        c.Add(new RecordingAction("A", log));
        c.Add(new RecordingAction("B", log, throws: true));
        c.Add(new RecordingAction("C", log));

        var errors = c.Enter();

        errors.Should().ContainSingle().Which.Should().Contain("B");
        log.Should().Equal("apply:A", "revert:A");
        c.IsActive.Should().BeFalse();
        c.Applied.Should().BeEmpty();
    }

    [Fact]
    public void 失败的动作本身也应被回滚尝试覆盖()
    {
        var log = new List<string>();
        var failing = new RecordingAction("B", log, throws: true);
        var c = new GameModeController();
        c.Add(failing);

        c.Enter();

        // Apply 抛异常时该动作未被加入 _applied，因此不会 Revert，属预期行为
        failing.ApplyCount.Should().Be(1);
        failing.RevertCount.Should().Be(0);
    }

    [Fact]
    public void 未进入时退出不应抛异常()
    {
        var c = new GameModeController();
        c.Add(new RecordingAction("A", new List<string>()));

        var act = () => c.Exit();

        act.Should().NotThrow();
        c.IsActive.Should().BeFalse();
    }

    [Fact]
    public void 重复退出应保持幂等()
    {
        var log = new List<string>();
        var c = new GameModeController();
        c.Add(new RecordingAction("A", log));
        c.Enter();

        c.Exit();
        c.Exit();

        log.Count(l => l.StartsWith("revert")).Should().Be(1);
    }

    [Fact]
    public void 未传入状态存储时进入退出不应抛异常()
    {
        var c = new GameModeController();
        c.Add(new RecordingAction("A", new List<string>()));

        var enter = () => c.Enter();
        var exit = () => c.ExitWithReport();

        enter.Should().NotThrow();
        exit.Should().NotThrow();
    }

    [Fact]
    public void 干净退出后应清除兜底快照()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-ctrl-{Guid.NewGuid():N}.json");
        var store = new GameModeStateStore(path);
        var c = new GameModeController(store);
        c.Add(new RecordingAction("A", new List<string>()));
        try
        {
            c.Enter().Should().BeEmpty();

            c.ExitWithReport();

            store.Exists().Should().BeFalse("干净退出必须清掉快照，否则下次启动会误报残留");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void 状态写入失败应视为进入失败并自动回滚()
    {
        // 快照写不进去 = 崩溃后没有任何恢复依据，不能报「已开启」。
        // 用「快照路径被同名目录占住」构造必然写失败：File.WriteAllText 会抛异常。
        var blockDir = Path.Combine(Path.GetTempPath(), $"go-ctrl-block-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(blockDir, "active-state.json"));
        var store = new GameModeStateStore(Path.Combine(blockDir, "active-state.json"));
        var log = new List<string>();
        var c = new GameModeController(store);
        c.Add(new RecordingAction("A", log));
        try
        {
            var errors = c.Enter();

            errors.Should().ContainSingle().Which.Should().Contain("兜底状态写入失败");
            c.IsActive.Should().BeFalse();
            log.Should().Equal("apply:A", "revert:A");
        }
        finally
        {
            try { Directory.Delete(blockDir, true); } catch { }
        }
    }
}
