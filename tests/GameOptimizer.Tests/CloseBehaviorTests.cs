namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// 「点 × 时怎么处理」的用例。
///
/// 这块逻辑的危险点不在功能多少，而在**猜错方向的代价不对称**：
/// 该问的时候没问 = 用户以为程序崩了 / 关不掉；
/// 该记住的时候没记住 = 下次又要选一遍（烦，但可接受）。
/// 所以所有失败路径都必须回到「每次询问」，用例也按这个方向钉。
///
/// 全部走内存 + 临时文件，不依赖真实桌面交互。
/// </summary>
public class CloseBehaviorTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"go-close-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        try { if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); } catch { }
    }

    private CloseBehaviorService NewService() => new(new CloseBehaviorStore(_path));

    /// <summary>模拟「重启程序」：换一个全新的 service 实例，只共享磁盘上的配置。</summary>
    private CloseBehaviorService Restart() => NewService();

    private void WriteRaw(string json)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_path, json);
    }

    // ── 1. 首次点 ×：必须弹窗 ─────────────────────────────────────────────

    [Fact]
    public void 没有保存过行为时点关闭应弹窗()
    {
        var service = NewService();

        service.RequestClose().Should().Be(CloseRoute.Ask);
        service.PromptPending.Should().BeTrue("弹窗已经打开，正在等用户选择");
        service.IsExiting.Should().BeFalse("用户还没确认任何事，绝不能提前进入退出流程");
    }

    [Fact]
    public void 弹窗期间再次点关闭应被忽略而不是叠出第二个弹窗()
    {
        var service = NewService();

        service.RequestClose().Should().Be(CloseRoute.Ask);
        service.RequestClose().Should().Be(CloseRoute.Ignore, "重复点 × 不该叠窗口，也不能把窗口提前藏掉");
        service.PromptPending.Should().BeTrue();
    }

    // ── 2/3/4. 三种弹窗结果 ─────────────────────────────────────────────

    [Fact]
    public void 选择缩小到托盘应隐藏窗口并保持运行()
    {
        var service = NewService();
        service.RequestClose();

        var route = service.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.MinimizeToTray, Remember: false));

        route.Should().Be(CloseRoute.HideToTray);
        service.IsExiting.Should().BeFalse("缩到托盘不是退出，进程必须继续跑");
        service.PromptPending.Should().BeFalse();
        service.SavedBehavior.Should().Be(CloseBehavior.Ask, "没勾「记住」时不该改长期默认值");
    }

    [Fact]
    public void 选择退出应走退出流程()
    {
        var service = NewService();
        service.RequestClose();

        var route = service.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.Exit, Remember: false));

        route.Should().Be(CloseRoute.Exit);
        service.IsExiting.Should().BeTrue();
        service.SavedBehavior.Should().Be(CloseBehavior.Ask);
    }

    [Fact]
    public void 取消应什么都不做且不改配置()
    {
        var service = NewService();
        service.RequestClose();

        // null = 取消 / 直接关掉弹窗，两者语义完全一致
        var route = service.CompletePrompt(null);

        route.Should().Be(CloseRoute.None);
        service.PromptPending.Should().BeFalse();
        service.IsExiting.Should().BeFalse("取消不能退出程序");
        service.SavedBehavior.Should().Be(CloseBehavior.Ask);
        File.Exists(_path).Should().BeFalse("取消不该写任何配置");
    }

    [Fact]
    public void 取消后主窗口应保持可见并能再次询问()
    {
        var service = NewService();
        service.RequestClose();
        service.CompletePrompt(null);

        // 再来一次点 ×：仍然要问（说明状态已经复位，没有卡在「正在询问」）
        service.RequestClose().Should().Be(CloseRoute.Ask);
    }

    [Fact]
    public void 弹窗给出Ask这种不可执行的选择时应拒绝执行()
    {
        var service = NewService();
        service.RequestClose();

        // Ask 是「还没决定」，不是一个可以执行的动作；真传进来也不能当成有效选择
        var route = service.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.Ask, Remember: false));

        route.Should().Be(CloseRoute.None);
        service.SavedBehavior.Should().Be(CloseBehavior.Ask);
    }

    // ── 5/6/7. 记住与不记住 ─────────────────────────────────────────────

    [Fact]
    public void 勾选记住并确认后应写入配置()
    {
        var service = NewService();
        service.RequestClose();

        service.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.Exit, Remember: true))
            .Should().Be(CloseRoute.Exit);

        service.SavedBehavior.Should().Be(CloseBehavior.Exit);
        File.Exists(_path).Should().BeTrue();
        File.ReadAllText(_path).Should().Contain("exit");
    }

    [Fact]
    public void 重启后已保存的选择仍然有效()
    {
        var first = NewService();
        first.RequestClose();
        first.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.MinimizeToTray, Remember: true));

        var second = Restart();

        second.SavedBehavior.Should().Be(CloseBehavior.MinimizeToTray);
        second.RequestClose().Should().Be(CloseRoute.HideToTray, "记住之后不该再问");
        second.PromptPending.Should().BeFalse();
    }

    [Fact]
    public void 重启后记住退出时点关闭应直接退出且不弹窗()
    {
        var first = NewService();
        first.RequestClose();
        first.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.Exit, Remember: true));

        var second = Restart();

        second.RequestClose().Should().Be(CloseRoute.Exit);
        second.PromptPending.Should().BeFalse();
        second.IsExiting.Should().BeTrue();
    }

    [Fact]
    public void 未勾选记住时不得把本次选择写进配置()
    {
        var service = NewService();
        service.RequestClose();

        service.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.MinimizeToTray, Remember: false))
            .Should().Be(CloseRoute.HideToTray, "本次仍按用户的选择隐藏到托盘");

        File.Exists(_path).Should().BeFalse("没勾记住就一个字都不该写");
        Restart().SavedBehavior.Should().Be(CloseBehavior.Ask, "下次点 × 还得再问一次");
    }

    [Fact]
    public void 未勾选记住时应保留配置里原有的值()
    {
        // 之前记住过「缩小到托盘」
        var first = NewService();
        first.RequestClose();
        first.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.MinimizeToTray, Remember: true));

        // 用户在设置页重置成「每次询问」，于是弹窗又出现了
        var second = Restart();
        second.Save(CloseBehavior.Ask, out _).Should().BeTrue();
        second.RequestClose().Should().Be(CloseRoute.Ask);

        // 这次选「退出」但不勾记住
        second.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.Exit, Remember: false))
            .Should().Be(CloseRoute.Exit);

        File.ReadAllText(_path).Should().Contain("ask", "原配置是每次询问，不能被这次临时选择改成退出");
        File.ReadAllText(_path).Should().NotContain("exit");
    }

    [Fact]
    public void 设置页改为每次询问后下次点关闭应重新弹窗()
    {
        var service = NewService();
        service.RequestClose();
        service.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.MinimizeToTray, Remember: true));

        service.RequestClose().Should().Be(CloseRoute.HideToTray);

        // 用户在设置页把行为重置回「每次询问」
        service.Save(CloseBehavior.Ask, out var error).Should().BeTrue();
        error.Should().BeNull();

        service.RequestClose().Should().Be(CloseRoute.Ask, "重置后必须重新询问");
        service.PromptPending.Should().BeTrue();

        // 且重启后仍然是每次询问
        Restart().SavedBehavior.Should().Be(CloseBehavior.Ask);
    }

    // ── 9. 托盘退出 ─────────────────────────────────────────────────────

    [Fact]
    public void 托盘菜单退出不应再弹关闭询问()
    {
        var service = NewService();

        service.ConfirmExit();
        service.RequestClose().Should().Be(CloseRoute.Exit, "用户已经从托盘选了退出，再问一遍等于不信任他的选择");
        service.PromptPending.Should().BeFalse();
    }

    [Fact]
    public void 托盘退出时应清掉未完成的询问()
    {
        var service = NewService();
        service.RequestClose();          // 弹窗开着
        service.ConfirmExit();           // 用户从托盘退了

        service.PromptPending.Should().BeFalse();

        // 随后弹窗被系统关掉，迟到的结果不能再驱动一次「隐藏到托盘」
        service.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.MinimizeToTray, Remember: true))
            .Should().Be(CloseRoute.None);
        service.SavedBehavior.Should().Be(CloseBehavior.Ask, "迟到的结果连配置都不该改");
    }

    // ── 10/11. 重复与幂等 ───────────────────────────────────────────────

    [Fact]
    public void 已记住缩小到托盘时重复点关闭应始终返回隐藏()
    {
        var service = NewService();
        service.Save(CloseBehavior.MinimizeToTray, out _);

        // 托盘恢复窗口后又会点 ×，每次都必须是同一个结论（不能第二次就变成退出）
        for (var i = 0; i < 3; i++)
        {
            service.RequestClose().Should().Be(CloseRoute.HideToTray);
            service.IsExiting.Should().BeFalse();
        }
    }

    [Fact]
    public void 进入退出流程后所有关闭请求都应直接放行()
    {
        var service = NewService();
        service.Save(CloseBehavior.MinimizeToTray, out _);

        service.ConfirmExit();

        service.RequestClose().Should().Be(CloseRoute.Exit);
        service.RequestClose().Should().Be(CloseRoute.Exit, "退出过程中窗口关闭事件可能再来一次，必须放行而不是重新询问");
    }

    [Fact]
    public void 放弃询问后应允许再次询问()
    {
        var service = NewService();
        service.RequestClose();

        // 弹窗根本没能打开（异常路径）：清掉挂起标记，别让 × 永久失效
        service.AbandonPrompt();

        service.PromptPending.Should().BeFalse();
        service.RequestClose().Should().Be(CloseRoute.Ask);
    }

    // ── 配置的读写健壮性 ────────────────────────────────────────────────

    [Fact]
    public void 配置文件损坏时应回退到每次询问()
    {
        WriteRaw("{ 这不是 JSON");

        var service = NewService();

        service.SavedBehavior.Should().Be(CloseBehavior.Ask);
        service.LoadError.Should().NotBeNull("读坏了要让用户知道，不能装作没这回事");
        service.RequestClose().Should().Be(CloseRoute.Ask);
    }

    [Fact]
    public void 配置文件为空时应回退到每次询问()
    {
        WriteRaw("");

        NewService().SavedBehavior.Should().Be(CloseBehavior.Ask);
    }

    [Fact]
    public void 配置里没有behavior字段时应回退到每次询问()
    {
        WriteRaw("""{ "schemaVersion": 1 }""");

        var service = NewService();

        service.SavedBehavior.Should().Be(CloseBehavior.Ask);
        service.LoadNotice.Should().NotBeNull();
    }

    [Fact]
    public void 配置里的值认不出来时应回退到每次询问并说明()
    {
        WriteRaw("""{ "schemaVersion": 1, "behavior": "最小化到托盘" }""");

        var service = NewService();

        service.SavedBehavior.Should().Be(CloseBehavior.Ask);
        service.LoadNotice.Should().Contain("无法识别");
    }

    [Fact]
    public void 来自更新版本的配置应按每次询问处理()
    {
        WriteRaw("""{ "schemaVersion": 99, "behavior": "exit" }""");

        var service = NewService();

        service.SavedBehavior.Should().Be(CloseBehavior.Ask, "字段含义可能已经变了，不能照着旧理解直接退出程序");
        service.LoadNotice.Should().Contain("更新的版本");
    }

    [Fact]
    public void 配置取值不应区分大小写()
    {
        WriteRaw("""{ "schemaVersion": 1, "behavior": "MinimizeToTray" }""");

        NewService().SavedBehavior.Should().Be(CloseBehavior.MinimizeToTray);
    }

    [Fact]
    public void 写盘失败时应让本次行为照常生效并如实报错()
    {
        // 把「目录」指到一个已存在的文件上：CreateDirectory 会失败，写盘必然失败
        var blocker = Path.Combine(Path.GetTempPath(), $"go-close-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "x");
        try
        {
            var service = new CloseBehaviorService(new CloseBehaviorStore(Path.Combine(blocker, "close.json")));
            service.RequestClose();

            // 写不进去也要按用户选的在本次执行，但错误必须能看见
            service.CompletePrompt(new CloseBehaviorChoice(CloseBehavior.Exit, Remember: true))
                .Should().Be(CloseRoute.Exit);

            service.SavedBehavior.Should().Be(CloseBehavior.Exit);
            service.SaveError.Should().NotBeNull("写盘失败必须上报，不能假装记住了");
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }

    [Fact]
    public void 保存配置只应写入一个文件不碰其他配置()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"go-close-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "close-behavior.json");
        try
        {
            var service = new CloseBehaviorService(new CloseBehaviorStore(path));
            service.Save(CloseBehavior.MinimizeToTray, out _).Should().BeTrue();

            Directory.EnumerateFiles(dir).Select(Path.GetFileName)
                .Should().BeEquivalentTo(["close-behavior.json"],
                    "关闭行为只能动自己的配置文件，白名单 / 快照 / 下载记录都不该被牵连");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void 保存配置不应留下临时文件()
    {
        var service = NewService();
        service.Save(CloseBehavior.Exit, out _).Should().BeTrue();

        File.Exists(_path).Should().BeTrue();
        File.Exists(_path + ".tmp").Should().BeFalse("改名成功就不该留下 .tmp");
    }

    [Fact]
    public void 保存认不出的行为应被拒绝()
    {
        var service = NewService();

        service.Save((CloseBehavior)42, out var error).Should().BeFalse();
        error.Should().NotBeNull();
        service.SavedBehavior.Should().Be(CloseBehavior.Ask);
        File.Exists(_path).Should().BeFalse();
    }

    // ── 口径本身 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CloseBehavior.Ask, CloseRoute.Ask)]
    [InlineData(CloseBehavior.MinimizeToTray, CloseRoute.HideToTray)]
    [InlineData(CloseBehavior.Exit, CloseRoute.Exit)]
    [InlineData((CloseBehavior)42, CloseRoute.Ask)]
    public void 已保存行为到关闭动作的映射应固定(CloseBehavior saved, CloseRoute expected)
    {
        CloseBehaviorPolicy.Route(saved).Should().Be(expected);
    }

    [Fact]
    public void 只有勾选记住才产生持久化目标()
    {
        CloseBehaviorPolicy.PersistTarget(new CloseBehaviorChoice(CloseBehavior.Exit, Remember: true))
            .Should().Be(CloseBehavior.Exit);

        CloseBehaviorPolicy.PersistTarget(new CloseBehaviorChoice(CloseBehavior.Exit, Remember: false))
            .Should().BeNull("没勾记住就不该写配置");

        CloseBehaviorPolicy.PersistTarget(new CloseBehaviorChoice(CloseBehavior.Ask, Remember: true))
            .Should().BeNull("Ask 不是可执行的动作，写进配置只会让下次也问一次");
    }

    [Fact]
    public void 三种行为都应有给用户看的说法()
    {
        foreach (var behavior in Enum.GetValues<CloseBehavior>())
        {
            CloseBehaviorPolicy.Describe(behavior).Should().NotBeNullOrWhiteSpace();
            CloseBehaviorPolicy.Explain(behavior).Should().NotBeNullOrWhiteSpace();
        }
    }
}
