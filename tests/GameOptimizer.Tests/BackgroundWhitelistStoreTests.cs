namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// 自定义白名单的持久化用例。
///
/// 需求来源：默认名单覆盖不到用户机器上真正抢资源的软件
/// （实测是壁纸引擎、厂商管家、任务栏美化），必须让用户自己能改。
/// 但保护名单是硬约束——写进配置文件也不接受。
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

    [Fact]
    public void 文件不存在时应返回默认名单()
    {
        Store.Load().Should().BeEquivalentTo(BackgroundProcessWhitelist.Default);
    }

    [Fact]
    public void 保存后应能原样读回()
    {
        var store = Store;
        store.Save(["wallpaper64", "telemetry_agent"], out var error).Should().BeTrue();
        error.Should().BeNull();

        store.Load().Should().BeEquivalentTo(["wallpaper64", "telemetry_agent"]);
    }

    [Fact]
    public void 保存时应剔除保护名单内的进程()
    {
        // 用户手改配置写进 explorer，也不能让它生效
        var store = Store;
        store.Save(["explorer", "wallpaper64", "MsMpEng"], out _).Should().BeTrue();

        store.Load().Should().BeEquivalentTo(["wallpaper64"]);
    }

    [Fact]
    public void 应去掉重复项与空项()
    {
        var store = Store;
        store.Save(["chrome", "CHROME", "  ", "", "wallpaper64"], out _).Should().BeTrue();

        store.Load().Should().BeEquivalentTo(["chrome", "wallpaper64"]);
    }

    [Fact]
    public void 应接受带exe后缀的写法()
    {
        // 用户常直接粘贴 "wallpaper64.exe"
        var store = Store;
        store.Save(["wallpaper64.exe", "TranslucentTB.EXE"], out _).Should().BeTrue();

        store.Load().Should().BeEquivalentTo(["wallpaper64", "TranslucentTB"]);
    }

    [Fact]
    public void 文件损坏时应退回默认名单而不是抛异常()
    {
        var store = Store;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ 这不是合法 JSON ");

        store.Load().Should().BeEquivalentTo(BackgroundProcessWhitelist.Default);
    }

    [Fact]
    public void 保存空名单后读回应为空而不是退回默认()
    {
        // 用户把所有勾选都取消了，这个意图必须被记住。
        // 退回默认名单会导致「我明明全取消了，它还是关了我的软件」。
        var store = Store;
        store.Save([], out _).Should().BeTrue();

        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void 保存应自动创建目录()
    {
        var nested = Path.Combine(Path.GetTempPath(), $"go-{Guid.NewGuid():N}", "sub", "list.json");
        try
        {
            new BackgroundWhitelistStore(nested).Save(["chrome"], out var error)
                .Should().BeTrue(error);
            File.Exists(nested).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(nested)!, recursive: true); } catch { }
        }
    }
}
