namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 崩溃 / 强杀兜底快照的序列化与清理行为。
/// 快照是崩溃恢复的全部依据：写不进去必须报错，读出来损坏必须报错，
/// 只有「干净退出」才能清掉。
/// </summary>
public class GameModeStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"go-state-{Guid.NewGuid():N}.json");
    private GameModeStateStore _store = null!;

    public GameModeStateStoreTests() => _store = new GameModeStateStore(_path);

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void 文件不存在时应返回null而不是报错()
    {
        _store.Load().Should().BeNull();
        _store.Exists().Should().BeFalse();
    }

    [Fact]
    public void 保存后应能读回原快照()
    {
        var snapshot = new GameModeSnapshot
        {
            PreviousPowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
            Killed =
            [
                new GameModeKilledProcess { Name = "WeChat", LaunchPath = @"C:\Program Files\Tencent\WeChat\WeChat.exe" },
            ],
        };

        _store.Save(snapshot);

        _store.Exists().Should().BeTrue();
        var loaded = _store.Load();
        loaded.Should().NotBeNull();
        loaded!.PreviousPowerPlanGuid.Should().Be(snapshot.PreviousPowerPlanGuid);
        loaded.Killed.Should().HaveCount(1);
        loaded.Killed[0].Name.Should().Be("WeChat");
        loaded.Killed[0].LaunchPath.Should().Be(snapshot.Killed[0].LaunchPath);
    }

    [Fact]
    public void 清除后文件应消失且读取返回null()
    {
        _store.Save(new GameModeSnapshot());
        _store.Clear();

        _store.Exists().Should().BeFalse();
        _store.Load().Should().BeNull();
    }

    [Fact]
    public void 文件损坏时应抛异常而不是假装没有残留()
    {
        // 兜底信息不可信时绝不能装作一切正常
        File.WriteAllText(_path, "{ 这不是合法 JSON");

        var act = () => _store.Load();

        act.Should().Throw<InvalidOperationException>().WithMessage("*损坏*");
    }

    [Fact]
    public void 保存到不存在的目录时应自动创建目录()
    {
        var nested = new GameModeStateStore(Path.Combine(Path.GetTempPath(), $"go-state-dir-{Guid.NewGuid():N}", "sub", "active-state.json"));
        try
        {
            nested.Save(new GameModeSnapshot { PreviousPowerPlanGuid = "x" });

            nested.Exists().Should().BeTrue();
            nested.Load().Should().NotBeNull();
        }
        finally
        {
            try { File.Delete(nested.FilePath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(nested.FilePath)!, true); } catch { }
        }
    }
}
