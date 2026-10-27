namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 启动时对遗留状态的处理方案（v2.5.2）。
///
/// 这些用例钉住两件事，都是「曾经做错过」的地方：
/// <list type="bullet">
/// <item>遗留的进程记录<b>不再</b>变成启动时的询问；数量是 0、1 还是 14 都不问，也不自动恢复；</item>
/// <item>崩溃留下的电源计划仍然<b>必须</b>还原——取消弹窗不等于把系统状态扔在那里。</item>
/// </list>
/// 全部是纯函数判定，不碰文件系统、不启动进程、不弹窗。
/// </summary>
public class StartupRecoveryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"go-startup-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private GameModeStateStore Store() => new(_path);

    /// <summary>造一份「关了游戏模式、进程还没恢复」的快照（与 v2.4.1 落盘形态一致）。</summary>
    private static GameModeSnapshot PendingOnly(int count) => new()
    {
        PreviousPowerPlanGuid = null,
        Killed = [.. Enumerable.Range(0, count).Select(i => new GameModeKilledProcess
        {
            Name = $"app{i}",
            LaunchPath = $@"D:\Apps\app{i}.exe",
        })],
    };

    [Fact]
    public void 从磁盘读回十四条遗留记录也不会产生启动询问()
    {
        // 端到端走一遍真实文件：落盘 → 读回 → 判定。上一版就是在这条链路的终点
        // 弹了一个「是否现在恢复？」，数量正好是 14 条时要能复现「不再问」。
        var store = Store();
        store.Save(PendingOnly(14));

        var plan = StartupRecoveryPlanner.Plan(store.Load());

        plan.ShouldRestorePowerPlan.Should().BeFalse("启动不该对系统做任何改动");
        plan.PendingProcesses.Should().HaveCount(14, "记录必须原样留着给手动入口");
        plan.PendingProcesses.Should().OnlyContain(p => !p.Launched, "一条都不许自动拉起");
        store.Load()!.Killed.Should().HaveCount(14, "只读判定不得顺手清空快照");
    }

    [Fact]
    public void 没有残留快照时启动无事可做()
    {
        var plan = StartupRecoveryPlanner.Plan(null);

        plan.IsClean.Should().BeTrue("干净启动不该在界面上多说一句");
        plan.ShouldRestorePowerPlan.Should().BeFalse();
        plan.PendingProcesses.Should().BeEmpty();
        StartupRecoveryPlanner.Describe(plan).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(14)]
    public void 无论遗留几条进程记录启动方案都不要求做任何系统改动(int count)
    {
        // 这就是那个「上次关闭游戏模式后还有 14 个后台进程没有恢复，是否现在恢复？」
        // 的触发数据：以前它会变成一次 MessageBox，现在只剩一份记录
        var plan = StartupRecoveryPlanner.Plan(PendingOnly(count));

        plan.ShouldRestorePowerPlan.Should().BeFalse("没有电源计划残留，启动不该动系统");
        plan.PendingProcesses.Should().HaveCount(count, "记录要留着，不能被清空");
        plan.PendingCount.Should().Be(count);
    }

    [Fact]
    public void 十四条遗留记录的状态行文案不含任何询问()
    {
        var plan = StartupRecoveryPlanner.Plan(PendingOnly(14));
        var text = StartupRecoveryPlanner.Describe(plan);

        text.Should().Contain("14", "得让用户知道还剩多少件没处理");
        text.Should().Contain("恢复遗留进程", "要说清去哪里处理");
        text.Should().NotContain("是否", "启动时不再问问题");
        text.Should().NotContain("现在", "更不允许出现「是否现在恢复」这种措辞");
    }

    [Fact]
    public void 崩溃快照必须还原电源计划()
    {
        var snapshot = new GameModeSnapshot
        {
            PreviousPowerPlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            Killed =
            [
                new GameModeKilledProcess { Name = "chrome", LaunchPath = @"D:\Apps\chrome.exe" },
            ],
        };

        var plan = StartupRecoveryPlanner.Plan(snapshot);

        plan.ShouldRestorePowerPlan.Should().BeTrue("系统还停在高性能，不还原就是永久改动");
        plan.PowerPlanGuid.Should().Be("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    }

    [Fact]
    public void 还原电源计划的同时进程记录仍只作保留不自动恢复()
    {
        var snapshot = new GameModeSnapshot
        {
            PreviousPowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
            Killed =
            [
                new GameModeKilledProcess { Name = "chrome", LaunchPath = @"D:\Apps\chrome.exe" },
                new GameModeKilledProcess { Name = "kugou", LaunchPath = @"D:\Apps\kugou.exe" },
            ],
        };

        var plan = StartupRecoveryPlanner.Plan(snapshot);

        plan.PendingProcesses.Should().HaveCount(2, "快照里的进程仍然要被记下来供手动恢复");
        plan.PendingProcesses.Should().OnlyContain(p => !p.Launched,
            "方案阶段一个进程都不许被拉起来：取消弹窗 ≠ 自动恢复");
    }

    [Fact]
    public void 路径缺失的记录不应反复提醒()
    {
        // 读不到 exe 路径的进程永远拉不起来；留着它会让「还有 N 条未恢复」永远降不到 0，
        // 那正是「每次启动都被提醒一次」的根源
        var snapshot = new GameModeSnapshot
        {
            Killed =
            [
                new GameModeKilledProcess { Name = "ghost", LaunchPath = "" },
                new GameModeKilledProcess { Name = "chrome", LaunchPath = @"D:\Apps\chrome.exe" },
            ],
        };

        var plan = StartupRecoveryPlanner.Plan(snapshot);

        plan.PendingProcesses.Should().ContainSingle().Which.Name.Should().Be("chrome");
    }

    [Fact]
    public void 重复路径只留一条()
    {
        var snapshot = new GameModeSnapshot
        {
            Killed =
            [
                new GameModeKilledProcess { Name = "chrome", LaunchPath = @"D:\Apps\chrome.exe" },
                new GameModeKilledProcess { Name = "chrome", LaunchPath = "d:/apps/CHROME.exe" },
            ],
        };

        StartupRecoveryPlanner.Plan(snapshot).PendingProcesses.Should().ContainSingle();
    }

    [Fact]
    public void 电源计划还原成功的文案()
    {
        var plan = StartupRecoveryPlanner.Plan(new GameModeSnapshot
        {
            PreviousPowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
        });

        var text = StartupRecoveryPlanner.Describe(plan, []);

        text.Should().Contain("已还原");
        text.Should().NotContain("是否");
    }

    [Fact]
    public void 电源计划还原失败的文案必须带上原因()
    {
        var plan = StartupRecoveryPlanner.Plan(new GameModeSnapshot
        {
            PreviousPowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
        });

        var text = StartupRecoveryPlanner.Describe(plan, ["原电源计划还原失败：拒绝访问"]);

        text.Should().Contain("拒绝访问", "失败必须可见，不许装作还原成功");
        text.Should().Contain("记录已保留");
    }

    [Fact]
    public void 快照读不出来时要说明而不是装作没事()
    {
        var plan = StartupRecoveryPlanner.Unreadable("JSON 已损坏");

        plan.UnreadableReason.Should().Be("JSON 已损坏");
        plan.IsClean.Should().BeFalse();
        StartupRecoveryPlanner.Describe(plan).Should().Contain("JSON 已损坏");
    }

    [Fact]
    public void 全部恢复后快照应被清除()
    {
        var store = Store();
        var record = new RestoredProcess("chrome", @"D:\Apps\chrome.exe");
        record.MarkLaunched();

        store.SavePendingProcesses([record]).Should().BeNull();

        store.Exists().Should().BeFalse("都回来了还留着快照，首页那个按钮就永远消不掉");
    }

    [Fact]
    public void 部分失败时快照只保留没恢复的那些且不写电源计划()
    {
        var store = Store();
        var ok = new RestoredProcess("chrome", @"D:\Apps\chrome.exe");
        var broken = new RestoredProcess("kugou", @"D:\Apps\kugou.exe");
        ok.MarkLaunched();
        broken.MarkFailed("拒绝访问");

        store.SavePendingProcesses([ok, broken]).Should().BeNull();

        var snapshot = store.Load();
        snapshot!.Killed.Should().ContainSingle().Which.Name.Should().Be("kugou");
        snapshot.PreviousPowerPlanGuid.Should().BeNull("电源计划那一步已经还原，留着旧 GUID 会把它再改一遍");
    }

    [Fact]
    public void 取消恢复时记录原样保留()
    {
        // 取消 = 这次不恢复：一条都不许丢，否则「下次还能处理」就是空话
        var store = Store();
        var a = new RestoredProcess("chrome", @"D:\Apps\chrome.exe");
        var b = new RestoredProcess("kugou", @"D:\Apps\kugou.exe");

        store.SavePendingProcesses([a, b]).Should().BeNull();

        store.Load()!.Killed.Should().HaveCount(2);
    }
}
