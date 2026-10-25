namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// 保护名单的持久化与迁移用例。
///
/// v2.4 第三次语义调整后，磁盘上<b>只存保护名单</b>：
/// 未勾选保护的进程默认就是结束目标，不需要再存一份「要关谁」的清单
/// （存了也会和实时扫描结果对不上）。
///
/// 迁移的关键约束：旧格式里的 <c>processes</c>（v1 勾选=结束）与
/// <c>endTargets</c>（v2 显式标记）都<b>不会被倒成保护</b>——
/// 那会让用户以为「游戏模式还在关那些程序」，实际一个都没关。
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
    public void 文件不存在时应返回空保护名单而不是默认全选()
    {
        // 保护名单为空 = 默认所有候选都会被结束。这个默认值必须由「什么都没配」推出，
        // 而不是由某个内置名单推出——否则用户机器上装了什么都不受控。
        var load = Store.LoadState();

        load.State.Protected.Should().BeEmpty();
        load.Error.Should().BeNull();
        load.MigratedFromLegacy.Should().BeFalse();
    }

    [Fact]
    public void 保存后应能原样读回保护名单()
    {
        var store = Store;
        store.SaveState(new BackgroundProtectionState(["WeChat", "chrome"]), out var error)
            .Should().BeTrue();
        error.Should().BeNull();

        var load = store.LoadState();
        load.State.Protected.Should().BeEquivalentTo(["WeChat", "chrome"]);
        load.Error.Should().BeNull();
    }

    [Fact]
    public void 保存时应剔除保护名单内的系统进程()
    {
        // 用户手改配置写进 explorer，也不能让它生效：那是系统外壳，动不得
        var store = Store;
        store.SaveState(new BackgroundProtectionState(["explorer", "MsMpEng", "chrome"]), out _)
            .Should().BeTrue();

        store.LoadState().State.Protected.Should().BeEquivalentTo(["chrome"]);
    }

    [Fact]
    public void 应去掉重复项与空项()
    {
        var store = Store;
        store.SaveState(new BackgroundProtectionState(["chrome", "CHROME", "  ", ""]), out _)
            .Should().BeTrue();

        store.LoadState().State.Protected.Should().BeEquivalentTo(["chrome"]);
    }

    [Fact]
    public void 应接受带exe后缀的写法()
    {
        // 用户常直接粘贴 "TranslucentTB.exe"
        var store = Store;
        store.SaveState(new BackgroundProtectionState(["TranslucentTB.EXE"]), out _).Should().BeTrue();

        store.LoadState().State.Protected.Should().BeEquivalentTo(["TranslucentTB"]);
    }

    [Fact]
    public void 文件损坏时应退回空保护名单并明确报错()
    {
        WriteRaw("{ 这不是合法 JSON ");

        var load = Store.LoadState();

        load.State.Protected.Should().BeEmpty();
        load.Error.Should().NotBeNullOrWhiteSpace("读取失败必须可见，不能静默当成正常配置");
    }

    [Fact]
    public void 保存空名单后读回应为空而不是退回默认()
    {
        var store = Store;
        store.SaveState(BackgroundProtectionState.Empty, out _).Should().BeTrue();

        store.LoadState().State.Protected.Should().BeEmpty();
    }

    [Fact]
    public void 保存应自动创建目录()
    {
        var nested = Path.Combine(Path.GetTempPath(), $"go-{Guid.NewGuid():N}", "sub", "list.json");
        try
        {
            new BackgroundWhitelistStore(nested)
                .SaveState(new BackgroundProtectionState(["chrome"]), out var error)
                .Should().BeTrue(error);
            File.Exists(nested).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(nested)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public void 写入的应是v3格式且只含保护名单()
    {
        var store = Store;
        store.SaveState(new BackgroundProtectionState(["chrome"]), out _).Should().BeTrue();

        var json = File.ReadAllText(_path);

        json.Should().Contain("\"schemaVersion\": 3");
        json.Should().Contain("protected");
        json.Should().NotContain("endTargets", "结束目标已并入默认行为，不再单独存");
        json.Should().NotContain("processes");
    }

    // ---------- 旧配置迁移 ----------

    [Fact]
    public void 旧版勾选项不得被倒成受保护()
    {
        // 旧版：{"processes": [...]} 里的项「勾选 = 会被结束」。
        // 现在「没勾保护就会结束」，这些项本来就在结束范围内，
        // 因此清单可以直接丢弃；但绝不能反过来把它们当保护项——
        // 那会让用户以为游戏模式还在关它们，实际一个都没关。
        WriteRaw("""
        {
          "processes": ["wallpaper64", "telemetry_agent"]
        }
        """);

        var load = Store.LoadState();

        load.MigratedFromLegacy.Should().BeTrue();
        load.State.Protected.Should().BeEmpty("旧配置里没有「保护」的概念，不能凭空生出来");
        load.Notice.Should().NotBeNullOrWhiteSpace("迁移必须提示用户核对");
        load.Notice.Should().Contain("结束范围");
    }

    [Fact]
    public void 旧版结束目标清单应被丢弃并保留保护名单()
    {
        // v2 文件：protected + endTargets。endTargets 现在没有意义（默认都结束），
        // 但 protected 必须一字不差地保留下来。
        WriteRaw("""
        {
          "schemaVersion": 2,
          "protected": ["WeChat"],
          "endTargets": ["wallpaper64"]
        }
        """);

        var load = Store.LoadState();

        load.State.Protected.Should().BeEquivalentTo(["WeChat"]);
        load.MigratedFromLegacy.Should().BeTrue();
        load.Notice.Should().NotBeNullOrWhiteSpace();
        load.Notice.Should().Contain("并入默认行为");
    }

    [Fact]
    public void 迁移后的保存应写成v3且不再重复迁移()
    {
        WriteRaw("""{ "processes": ["wallpaper64"] }""");

        var store = Store;
        var first = store.LoadState();
        first.MigratedFromLegacy.Should().BeTrue();

        store.SaveState(first.State, out var error).Should().BeTrue(error);

        var second = store.LoadState();
        second.MigratedFromLegacy.Should().BeFalse();
        second.Notice.Should().BeNull();
        second.State.Protected.Should().BeEmpty();
    }

    [Fact]
    public void 空旧配置也应给出提示()
    {
        WriteRaw("""{ "processes": [] }""");

        var load = Store.LoadState();

        load.MigratedFromLegacy.Should().BeTrue();
        load.State.Protected.Should().BeEmpty();
        load.Notice.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 结构无法识别的配置应保守处理为空并提示()
    {
        // 认不出结构时不能猜，给空保护名单 + 提示，让用户有机会先去勾保护
        WriteRaw("""{ "whatever": ["wallpaper64"] }""");

        var load = Store.LoadState();

        load.State.Protected.Should().BeEmpty();
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

            store.SaveState(new BackgroundProtectionState(["chrome"]), out var error)
                .Should().BeFalse();
            error.Should().NotBeNullOrWhiteSpace("保存失败必须能显示给用户");
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }
}
