namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

public class SteamLibraryScannerTests
{
    [Fact]
    public void 应解析_libraryfolders_vdf_中的所有路径()
    {
        const string vdf = """
        "libraryfolders"
        {
            "0" { "path" "D:\\STEAM" "label" "" }
            "1" { "path" "E:\\SteamLibrary" }
        }
        """;

        SteamLibraryScanner.ParseLibraryPaths(vdf)
            .Should().BeEquivalentTo([@"D:\STEAM", @"E:\SteamLibrary"]);
    }

    [Fact]
    public void 空_vdf_应返回空集合()
    {
        SteamLibraryScanner.ParseLibraryPaths("").Should().BeEmpty();
    }

    [Fact]
    public void 反作弊与崩溃上报程序不应被当作游戏主程序()
    {
        SteamLibraryScanner.IsHelperExecutable("EasyAntiCheat.exe").Should().BeTrue();
        SteamLibraryScanner.IsHelperExecutable("UnityCrashHandler64.exe").Should().BeTrue();
        SteamLibraryScanner.IsHelperExecutable("NeacClient.exe").Should().BeTrue();
        SteamLibraryScanner.IsHelperExecutable("vcredist_x64.exe").Should().BeTrue();
        SteamLibraryScanner.IsHelperExecutable("NarakaBladepoint.exe").Should().BeFalse();
    }

    [Fact]
    public void 清单文件缺少必需字段时应跳过()
    {
        var dir = Path.Combine(Path.GetTempPath(), "go-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var acf = Path.Combine(dir, "appmanifest_1.acf");
            File.WriteAllText(acf, "\"AppState\" { \"appid\" \"1\" }"); // 无 name / installdir

            SteamLibraryScanner.ParseManifest(acf, dir).Should().BeNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 清单文件应正确解析名称与体积()
    {
        var dir = Path.Combine(Path.GetTempPath(), "go-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var acf = Path.Combine(dir, "appmanifest_1203220.acf");
            File.WriteAllText(acf, """
            "AppState"
            {
                "appid"       "1203220"
                "name"        "NARAKA: BLADEPOINT"
                "installdir"  "NARAKA BLADEPOINT"
                "SizeOnDisk"  "99269144523"
            }
            """);

            var entry = SteamLibraryScanner.ParseManifest(acf, dir);

            entry.Should().NotBeNull();
            // v1.0.1：NARAKA 显示名映射为「永劫无间」
            entry!.Name.Should().Be("永劫无间");
            entry.AppId.Should().Be("1203220");
            entry.SizeOnDiskGb.Should().BeApproximately(92.45, 0.1);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 目录不存在时应返回空而不是抛异常()
    {
        SteamLibraryScanner.Scan(@"Z:\definitely-not-exist").Should().BeEmpty();
    }

    [Fact]
    public void 兜底扫描不应把卸载残留的空壳目录当游戏()
    {
        // 实测：ChainedTogether 卸载后 Steam 只留一个 53 字节的云存档占位文件，
        // 目录里没有任何 exe。这类残留绝不能入库，否则用户看到「已卸载的游戏还在库里」。
        var dir = Path.Combine(Path.GetTempPath(), "go-test-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(dir, "ChainedTogether");
        Directory.CreateDirectory(Path.Combine(gameDir, "ChainedTogether", "CustomLevels"));
        try
        {
            File.WriteAllText(
                Path.Combine(gameDir, "ChainedTogether", "CustomLevels", "steam_autocloud.vdf"),
                "\"steam_autocloud.vdf\" { }");

            SteamLibraryScanner.BuildFallbackEntry(gameDir).Should().BeNull("没有 exe 的目录是卸载残留");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 兜底扫描应保留带主程序的目录()
    {
        // 移库场景：目录还在、里面有 exe，但没有 appmanifest → 必须入库
        var dir = Path.Combine(Path.GetTempPath(), "go-test-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(dir, "MyGame");
        Directory.CreateDirectory(gameDir);
        try
        {
            File.WriteAllText(Path.Combine(gameDir, "MyGame.exe"), "fake");

            var entry = SteamLibraryScanner.BuildFallbackEntry(gameDir);

            entry.Should().NotBeNull();
            entry!.Name.Should().Be("MyGame");
            entry.ExecutablePath.Should().EndWith("MyGame.exe");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 真实扫描不应包含已卸载的ChainedTogether()
    {
        // 本机实测：ChainedTogether 只剩云存档占位文件（53 字节），已卸载
        var games = NonGameFilter.Apply(GameLibrary.ScanAll());

        games.Should().NotContain(g => g.Name.Contains("ChainedTogether", StringComparison.OrdinalIgnoreCase));
    }
}
