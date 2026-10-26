namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 能提供待恢复进程的假动作：不碰任何真实进程，只记下「Revert 时开关是什么状态」。
/// </summary>
internal sealed class FakeRestoreSourceAction : IGameModeAction, IProcessRestoreSource
{
    private readonly List<RestoredProcess> _records;

    public FakeRestoreSourceAction(params RestoredProcess[] records) => _records = [.. records];

    public string Name => "假结束进程";
    public string Description => "测试用";

    public bool AutoRestoreOnRevert { get; set; } = true;

    /// <summary>Revert 执行那一刻的开关值，用来验证「延迟恢复」真的生效了。</summary>
    public bool? AutoRestoreDuringRevert { get; private set; }

    /// <summary>还没成功恢复的记录：处理过但失败的也算（下次启动还要提醒用户）。</summary>
    public IReadOnlyList<RestoredProcess> PendingRestores => [.. _records.Where(r => !r.Launched || r.Failed)];

    public IReadOnlyList<RestoredProcess> Restored => _records;

    public void Apply() { }

    public void Revert() => AutoRestoreDuringRevert = AutoRestoreOnRevert;

    public bool IsActive() => true;
}

/// <summary>
/// 关闭游戏模式 → 由用户选择恢复哪些进程 这条新链路的控制器行为：
/// 关的时候不许顺手把进程全拉起来，快照要按「还剩谁没恢复」维护，取消也不能把记录删掉。
/// </summary>
public class GameModeDeferredRestoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"go-defer-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private GameModeStateStore Store() => new(_path);

    [Fact]
    public void 延迟关闭时不应在回滚阶段自动恢复进程()
    {
        var action = new FakeRestoreSourceAction(new RestoredProcess("chrome", @"D:\Apps\chrome.exe"));
        var controller = new GameModeController();
        controller.Add(action);
        controller.Enter();

        controller.ExitWithReport(deferProcessRestore: true).Should().BeEmpty();

        action.AutoRestoreDuringRevert.Should().BeFalse("先关游戏模式，恢复哪些等用户选");
        action.PendingRestores.Should().ContainSingle("还没恢复的记录要留给弹窗");
        action.AutoRestoreOnRevert.Should().BeTrue("开关必须复位，托盘退出等路径仍要全恢复");
    }

    [Fact]
    public void 普通退出仍应按原行为自动恢复()
    {
        var action = new FakeRestoreSourceAction(new RestoredProcess("chrome", @"D:\Apps\chrome.exe"));
        var controller = new GameModeController();
        controller.Add(action);
        controller.Enter();

        controller.ExitWithReport();

        action.AutoRestoreDuringRevert.Should().BeTrue("没有界面的退出路径只能全恢复");
    }

    [Fact]
    public void 延迟关闭后游戏模式应处于关闭状态()
    {
        var controller = new GameModeController();
        controller.Add(new FakeRestoreSourceAction(new RestoredProcess("chrome", @"D:\Apps\chrome.exe")));
        controller.Enter();

        controller.ExitWithReport(deferProcessRestore: true);

        controller.IsActive.Should().BeFalse();
        controller.Applied.Should().BeEmpty();
    }

    [Fact]
    public void 用户放弃恢复时兜底快照应保留()
    {
        // 取消 = 本次不恢复，但「这些进程还没回来」是事实，记录不能被删
        var store = Store();
        var controller = new GameModeController(store);
        controller.Add(new FakeRestoreSourceAction(new RestoredProcess("chrome", @"D:\Apps\chrome.exe")));
        controller.Enter();

        controller.ExitWithReport(deferProcessRestore: true);

        store.Exists().Should().BeTrue();
        var snapshot = store.Load();
        snapshot!.Killed.Should().ContainSingle();
        snapshot.PreviousPowerPlanGuid.Should().BeNull("电源计划那一步已经还原，不能再留旧 GUID");
        controller.IsActive.Should().BeFalse("取消不会重新开启游戏模式");
    }

    [Fact]
    public void 全部恢复后兜底快照应被清除()
    {
        var store = Store();
        var action = new FakeRestoreSourceAction(new RestoredProcess("chrome", @"D:\Apps\chrome.exe"));
        var controller = new GameModeController(store);
        controller.Add(action);
        controller.Enter();
        controller.ExitWithReport(deferProcessRestore: true);

        // 模拟弹窗里恢复成功
        action.PendingRestores[0].MarkLaunched();
        controller.SyncPendingRestoreSnapshot().Should().BeNull();

        store.Exists().Should().BeFalse("都恢复完了还留着快照，下次启动会白问一遍");
    }

    [Fact]
    public void 部分失败时快照只保留没恢复的那些()
    {
        var store = Store();
        var ok = new RestoredProcess("chrome", @"D:\Apps\chrome.exe");
        var broken = new RestoredProcess("kugou", @"D:\Apps\kugou.exe");
        var controller = new GameModeController(store);
        controller.Add(new FakeRestoreSourceAction(ok, broken));
        controller.Enter();
        controller.ExitWithReport(deferProcessRestore: true);

        ok.MarkLaunched();
        broken.MarkFailed("拒绝访问");
        controller.SyncPendingRestoreSnapshot().Should().BeNull();

        var snapshot = store.Load();
        snapshot!.Killed.Should().ContainSingle().Which.Name.Should().Be("kugou");
    }

    [Fact]
    public void 关闭本身失败时不应改写快照()
    {
        // 回滚没走完 = 状态不明，这时把快照改成「只剩进程」会把电源计划那条信息弄丢
        var store = Store();
        var controller = new GameModeController(store);
        controller.Add(new BrokenRevertAction());
        controller.Enter();

        var errors = controller.ExitWithReport(deferProcessRestore: true);

        errors.Should().ContainSingle().Which.Should().Contain("还原失败");
        store.Exists().Should().BeTrue("回滚失败时必须留着快照，下次启动继续提示处理");
    }

    [Fact]
    public void 无状态存储时同步快照不应抛异常()
    {
        var controller = new GameModeController();
        controller.Add(new FakeRestoreSourceAction(new RestoredProcess("chrome", @"D:\Apps\chrome.exe")));

        var act = () => controller.SyncPendingRestoreSnapshot();

        act.Should().NotThrow();
    }
}
