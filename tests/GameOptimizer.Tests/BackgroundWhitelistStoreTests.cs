namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// 保护 / 结束目标名单的持久化与迁移用例。
///
/// 需求来源：默认名单覆盖不到用户机器上真正抢资源的软件
/// （实测是壁纸引擎、厂商管家、任务栏美化），必须让用户自己能改。
/// 但保护名单是硬约束——写进配置文件也不接受。
///
/// v2.4 起「勾选」的语义从「要结束」翻转为「受保护」，
/// 因此旧文件的迁移必须<b>保留原语义</b>：旧的勾选项迁到结束目标，
/// 绝不能倒成受保护，否则用户会以为游戏模式还在关那些程序。
/// </summary>
public class BackgroundWhitelistStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"go-whitelist-{Guid.NewGuid():N}.json");

    private BackgroundWhitelistStore Store => new(_path);

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, json);
    }

    [Fact]
    public void 文件不存在时应返回空名单而不是默认全选()
    {
        // 勾选意味着「受保护」/「会被结束」，两种都是用户的决定。
        // 不能替他决定——曾因默认全选，用户一点开启就关掉了他正在用的软件。
        var load = Store.LoadState();

        load.State.Protected.Should().BeEmpty();
        load.State.EndTargets.Should().BeEmpty();
        load.Error.Should().BeNull();
        load.MigratedFromLegacy.Should().BeFalse();
    }

    [Fact]
    public void 保存后应能原样读回保护与结束目标()
    {
        var store = Store;
        store.SaveState(new BackgroundProtectionState(
            ["WeChat", "chrome"],
            ["wallpaper64", "telemetry_agent"]), out var error).Should().BeTrue();
        error.Should().BeNull();

        var load = store.LoadState();
        load.State.Protected.Should().BeEquivalentTo(["WeChat", "chrome"]);
        load.State.EndTargets.Should().BeEquivalentTo(["wallpaper64", "telemetry_agent"]);
        load.Error.Should().BeNull();
    }

    [Fact]
    public void 保存时应剔除保护名单内的进程()
    {
        // 用户手改配置写进 explorer，也不能让它生效
        var store = Store;
        store.SaveState(new BackgroundProtectionState(["explorer"], ["wallpaper64", "MsMpEng"]), out _)
            .Should().BeTrue();

        var load = store.LoadState();
        load.State.Protected.Should().BeEmpty();
        load.State.EndTargets.Should().BeEquivalentTo(["wallpaper64"]);
    }

    [Fact]
    public void 应去掉重复项与空项()
    {
        var store = Store;
        store.SaveState(new BackgroundProtectionState(
            ["chrome", "CHROME", "  ", ""],
            ["wallpaper64", "Wallpaper64", ""]), out _).Should().BeTrue();

        var load = store.LoadState();
        load.State.Protected.Should().BeEquivalentTo(["chrome"]);
        load.State.EndTargets.Should().BeEquivalentTo(["wallpaper64"]);
    }

    [Fact]
    public void 应接受带exe后缀的写法()
    {
        // 用户常直接粘贴 "wallpaper64.exe"
        var store = Store;
        store.SaveState(new BackgroundProtectionState(
            ["TranslucentTB.EXE"], ["wallpaper64.exe"]), out _).Should().BeTrue();

        var load = store.LoadState();
        load.State.Protected.Should().BeEquivalentTo(["TranslucentTB"]);
        load.State.EndTargets.Should().BeEquivalentTo(["wallpaper64"]);
    }

    [Fact]
    public void 文件损坏时应退回空名单并明确报错()
    {
        WriteRaw("{ 这不是合法 JSON ");

        var load = Store.LoadState();

        load.State.Protected.Should().BeEmpty();
        load.State.EndTargets.Should().BeEmpty();
        load.Error.Should().NotBeNullOrWhiteSpace("读取失败必须可见，不能静默当成空配置");
    }

    [Fact]
    public void 保存空名单后读回应为空而不是退回默认()
    {
        // 用户把所有勾选都取消了，这个意图必须被记住。
        // 退回默认名单会导致「我明明全取消了，它还是关了我的软件」。
        var store = Store;
        store.SaveState(BackgroundProtectionState.Empty, out _).Should().BeTrue();

        var load = store.LoadState();
        load.State.Protected.Should().BeEmpty();
        load.State.EndTargets.Should().BeEmpty();
    }

    [Fact]
    public void 保存应自动创建目录()
    {
        var nested = Path.Combine(Path.GetTempPath(), $"go-{Guid.NewGuid():N}", "sub", "list.json");
        try
        {
            new BackgroundWhitelistStore(nested)
                .SaveState(new BackgroundProtectionState(["chrome"], []), out var error)
                .Should().BeTrue(error);
            File.Exists(nested).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(nested)!, recursive: true); } catch { }
        }
    }

    // ---------- 旧配置迁移 ----------

    [Fact]
    public void 旧版勾选项应按原语义迁移为结束目标而不是受保护()
    {
        // 旧版：{"processes": [...]} 里的项「勾选 = 会被结束」。
        // 迁移必须保留这个含义；倒成 protected 等于悄悄改掉用户的决定。
        WriteRaw("""
        {
          "processes": ["wallpaper64", "telemetry_agent"]
        }
        """);

        var load = Store.LoadState();

        load.MigratedFromLegacy.Should().BeTrue();
        load.State.EndTargets.Should().BeEquivalentTo(["wallpaper64", "telemetry_agent"]);
        load.State.Protected.Should().BeEmpty("旧配置里没有「保护」这个概念，不能凭空生出来");
        load.Notice.Should().NotBeNullOrWhiteSpace("迁移必须提示用户核对");
        load.Notice.Should().Contain("结束目标");
    }

    [Fact]
    public void 迁移后的保存应写成新版格式且不再重复迁移()
    {
        WriteRaw("""{ "processes": ["wallpaper64"] }""");

        var store = Store;
        var first = store.LoadState();
        first.MigratedFromLegacy.Should().BeTrue();

        store.SaveState(first.State, out var error).Should().BeTrue(error);

        var second = store.LoadState();
        second.MigratedFromLegacy.Should().BeFalse();
        second.Notice.Should().BeNull();
        second.State.EndTargets.Should().BeEquivalentTo(["wallpaper64"]);
        second.State.Protected.Should().BeEmpty();
    }

    [Fact]
    public void 旧配置里的系统进程同样不得迁移为结束目标()
    {
        WriteRaw("""{ "processes": ["explorer", "wallpaper64"] }""");

        var load = Store.LoadState();

        load.State.EndTargets.Should().BeEquivalentTo(["wallpaper64"]);
    }

    [Fact]
    public void 空旧配置应迁移为空并给出提示()
    {
        WriteRaw("""{ "processes": [] }""");

        var load = Store.LoadState();

        load.MigratedFromLegacy.Should().BeTrue();
        load.State.EndTargets.Should().BeEmpty();
        load.Notice.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 结构无法识别的配置应保守处理为空并提示()
    {
        // 认不出结构时绝不能猜「这些项大概是要结束的」——一项都不结束才是保守策略
        WriteRaw("""{ "whatever": ["wallpaper64"] }""");

        var load = Store.LoadState();

        load.State.Protected.Should().BeEmpty();
        load.State.EndTargets.Should().BeEmpty();
        load.Notice.Should().NotBeNullOrWhiteSpace();
        load.Error.Should().BeNull();
    }

    [Fact]
    public void 保存失败时必须返回原因而不是静默成功()
    {
        // 用一个「父路径是文件」的位置，目录建不出来 → 必须报错
        var blocker = Path.Combine(Path.GetTempPath(), $"go-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "x");
        try
        {
            var store = new BackgroundWhitelistStore(Path.Combine(blocker, "list.json"));

            store.SaveState(new BackgroundProtectionState(["chrome"], []), out var error)
                .Should().BeFalse();
            error.Should().NotBeNullOrWhiteSpace("保存失败必须能显示给用户");
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }
}
