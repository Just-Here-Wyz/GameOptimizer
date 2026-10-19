namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// WeGame 库扫描。用本机实测的真实目录结构做依据：
///   D:\WeGameApps\rail_apps\无畏契约(2001715)\live\VALORANT.exe
///   E:\WeGameApps\rail_apps\DeltaForce(2001918)\DeltaForceClient.exe
/// </summary>
public class WeGameLibraryScannerTests
{
    [Theory]
    [InlineData("无畏契约(2001715)", "无畏契约", "2001715")]
    [InlineData("DeltaForce(2001918)", "DeltaForce", "2001918")]
    [InlineData("某游戏（9999）", "某游戏", "9999")] // 全角括号
    public void 应能从目录名拆出游戏名与AppID(string folder, string name, string id)
    {
        var (n, i) = WeGameLibraryScanner.ParseFolderName(folder);

        n.Should().Be(name);
        i.Should().Be(id);
    }

    [Fact]
    public void 目录名不含ID时应原样返回而不是崩溃()
    {
        var (n, i) = WeGameLibraryScanner.ParseFolderName("SomeGame");

        n.Should().Be("SomeGame");
        i.Should().Be("SomeGame");
    }

    [Theory]
    [InlineData("ValorantUninstall.exe", true)]
    [InlineData("无畏契约卸载.exe", true)]
    [InlineData("crashpad_handler.exe", true)]
    [InlineData("bugreport.exe", true)]
    [InlineData("vc_redist.x64.exe", true)]
    [InlineData("launcher.exe", true)]
    [InlineData("VALORANT.exe", false)]
    [InlineData("DeltaForceClient.exe", false)]
    public void 应能识别辅助程序(string exe, bool isHelper)
    {
        WeGameLibraryScanner.IsHelperExecutable(exe).Should().Be(isHelper);
    }

    [Fact]
    public void 应在本机找到WeGame库根目录()
    {
        var roots = WeGameLibraryScanner.FindLibraryRoots();

        // 本机实测存在 D:\WeGameApps 与 E:\WeGameApps
        roots.Should().NotBeEmpty();
        roots.Should().OnlyContain(r => r.Contains("WeGame", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 真实扫描应找到已安装的WeGame游戏()
    {
        var games = new WeGameLibraryScanner().Scan();

        // 本机装了《无畏契约》和《三角洲行动》；换机器时至少不该抛异常
        if (games.Count > 0)
        {
            games.Should().OnlyContain(g => g.Source == "WeGame");
            games.Should().OnlyContain(g => g.AppId.Length > 0);
            games.Should().OnlyContain(g => Directory.Exists(g.InstallPath));
        }
    }

    [Fact]
    public void 扫描结果不应包含依赖库目录()
    {
        var games = new WeGameLibraryScanner().Scan();

        games.Should().NotContain(g => g.Name.Contains("dependency_shared"));
        games.Should().NotContain(g => g.Name.Contains("common_apps"));
    }

    [Fact]
    public void 主程序不应是卸载器或启动器()
    {
        var games = new WeGameLibraryScanner().Scan();

        foreach (var g in games.Where(g => g.ExecutablePath is not null))
        {
            var exe = Path.GetFileName(g.ExecutablePath!);
            WeGameLibraryScanner.IsHelperExecutable(exe).Should().BeFalse($"{g.Name} 选中了辅助程序 {exe}");
        }
    }

    [Fact]
    public void 名称片段应能用于匹配英文exe()
    {
        // "DeltaForce" 应当能匹配到 DeltaForceClient.exe
        var tokens = WeGameLibraryScanner.NameTokens("DeltaForce");
        tokens.Should().Contain("DeltaForce");

        // 中文名的片段不会被用来匹配英文 exe，但不应产生空串
        var cnTokens = WeGameLibraryScanner.NameTokens("无畏契约");
        cnTokens.Should().OnlyContain(t => t.Length >= 4);
    }
}
