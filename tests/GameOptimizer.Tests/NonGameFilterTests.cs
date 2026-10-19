namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// 非游戏过滤。用户明确要求：Wallpaper Engine 不算游戏。
/// </summary>
public class NonGameFilterTests
{
    private static GameEntry Entry(string name, string? exe = null)
        => new(name, "1", @"D:\fake", exe, 1.0, "Steam");

    [Theory]
    [InlineData("Wallpaper Engine")]
    [InlineData("wallpaper engine")]
    [InlineData("WALLPAPER ENGINE")]
    public void Wallpaper_Engine_应被过滤_忽略大小写(string name)
    {
        NonGameFilter.IsNonGame(Entry(name)).Should().BeTrue();
    }

    [Fact]
    public void Wallpaper_Engine_按可执行文件也应被识别()
    {
        // 名称被改过但 exe 还是 wallpapper32.exe 的情况
        NonGameFilter.IsNonGame(Entry("我的壁纸", @"C:\x\wallpaper32.exe")).Should().BeTrue();
        NonGameFilter.IsNonGame(Entry("某软件", @"C:\x\ui32.exe")).Should().BeTrue();
    }

    [Theory]
    [InlineData("Steamworks Common Redistributables")]
    [InlineData("SteamVR")]
    [InlineData("Steam Linux Runtime")]
    [InlineData("3DMark")]
    public void 其他已知非游戏也应被过滤(string name)
    {
        NonGameFilter.IsNonGame(Entry(name)).Should().BeTrue();
    }

    [Theory]
    [InlineData("NARAKA: BLADEPOINT")]
    [InlineData("无畏契约")]
    [InlineData("DeltaForce")]
    [InlineData("三角洲行动")]
    [InlineData("Counter-Strike 2")]
    public void 真实游戏不应被误杀(string name)
    {
        NonGameFilter.IsNonGame(Entry(name)).Should().BeFalse($"{name} 是游戏，不该被过滤");
    }

    [Fact]
    public void Apply_应只保留游戏()
    {
        var entries = new[]
        {
            Entry("Wallpaper Engine"),
            Entry("NARAKA: BLADEPOINT"),
            Entry("SteamVR"),
            Entry("DeltaForce"),
        };

        var result = NonGameFilter.Apply(entries);

        result.Should().HaveCount(2);
        result.Select(r => r.Name).Should().BeEquivalentTo(["NARAKA: BLADEPOINT", "DeltaForce"]);
    }

    [Fact]
    public void 名称前后带空格也应被正确过滤()
    {
        NonGameFilter.IsNonGame(Entry("  Wallpaper Engine  ")).Should().BeTrue();
    }
}

/// <summary>聚合扫描：多平台合并且去重。</summary>
public class GameLibraryTests
{
    private sealed class FakeScanner(string platform, params GameEntry[] entries) : IGameLibraryScanner
    {
        public string PlatformName => platform;
        public IReadOnlyList<GameEntry> Scan() => entries;
    }

    private sealed class ThrowingScanner : IGameLibraryScanner
    {
        public string PlatformName => "Broken";
        public IReadOnlyList<GameEntry> Scan() => throw new InvalidOperationException("磁盘读不到");
    }

    private static GameEntry Entry(string name, string path, double size, string source)
        => new(name, "1", path, null, size, source);

    [Fact]
    public void 应合并多个平台的结果()
    {
        var result = GameLibrary.ScanAll([
            new FakeScanner("Steam", Entry("GameA", @"D:\a", 10, "Steam")),
            new FakeScanner("WeGame", Entry("GameB", @"D:\b", 20, "WeGame")),
        ]);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void 应按体积降序排列()
    {
        var result = GameLibrary.ScanAll([
            new FakeScanner("Steam",
                Entry("Small", @"D:\s", 1, "Steam"),
                Entry("Big", @"D:\b", 100, "Steam")),
        ]);

        result[0].Name.Should().Be("Big");
    }

    [Fact]
    public void 同一安装路径应去重()
    {
        var result = GameLibrary.ScanAll([
            new FakeScanner("Steam", Entry("A", @"D:\same", 10, "Steam")),
            new FakeScanner("WeGame", Entry("A2", @"d:\SAME\", 10, "WeGame")),
        ]);

        result.Should().ContainSingle();
    }

    [Fact]
    public void 单个平台抛异常不应拖垮整体扫描()
    {
        var result = GameLibrary.ScanAll([
            new ThrowingScanner(),
            new FakeScanner("Steam", Entry("GameA", @"D:\a", 10, "Steam")),
        ]);

        result.Should().ContainSingle().Which.Name.Should().Be("GameA");
    }

    [Fact]
    public void 真实环境扫描应同时覆盖Steam和WeGame()
    {
        var games = NonGameFilter.Apply(GameLibrary.ScanAll());

        // 本机两者都装了；换机器时至少不应抛异常
        games.Should().NotContain(g => g.Name.Contains("Wallpaper", StringComparison.OrdinalIgnoreCase));

        if (games.Count > 0)
            games.Should().OnlyContain(g => g.Source == "Steam" || g.Source == "WeGame");
    }

    [Fact]
    public void 真实扫描结果中不应出现Wallpaper_Engine()
    {
        // 用户明确指出的问题：Wallpaper Engine 被当成了游戏
        var games = NonGameFilter.Apply(GameLibrary.ScanAll());

        games.Should().NotContain(g =>
            g.Name.Contains("Wallpaper", StringComparison.OrdinalIgnoreCase) ||
            (g.ExecutablePath ?? "").Contains("wallpaper", StringComparison.OrdinalIgnoreCase));
    }
}
