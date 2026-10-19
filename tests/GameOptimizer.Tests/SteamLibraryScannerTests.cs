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
            entry!.Name.Should().Be("NARAKA: BLADEPOINT");
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
}
