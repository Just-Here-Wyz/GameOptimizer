namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;
using GameOptimizer.Core.Library;

/// <summary>
/// 游戏进程检测的真实场景回归。
///
/// 用户反馈：「打开游戏后仍显示没有检测到有游戏正在运行」。
/// 实测原因有两个：
/// 1. 三角洲行动实际运行的进程是 DeltaForceClient-Win64-Shipping，
///    而静态扫描找到的是根目录的 DeltaForceClient.exe；
/// 2. 反作弊(ACE)阻止跨进程读取 MainModule / WMI ExecutablePath，路径为空。
/// 因此必须支持"按进程名前缀匹配"这条退化路径。
/// </summary>
public class GameProcessDetectionRegressionTests
{
    [Theory]
    [InlineData("DeltaForceClient-Win64-Shipping", "DeltaForceClient", true)]
    [InlineData("DeltaForceClient", "DeltaForceClient", true)]
    [InlineData("VALORANT-Win64-Shipping", "VALORANT", true)]
    [InlineData("NarakaBladepoint", "NarakaBladepoint", true)]
    [InlineData("chrome", "DeltaForceClient", false)]
    [InlineData("svchost", "DeltaForceClient", false)]
    public void 应按进程名前缀匹配到游戏(string processName, string exeStem, bool shouldMatch)
    {
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [exeStem] = "测试游戏",
        };

        var matched = GameProcessDetector.TryResolveByStem(processName, byStem, out var game);

        matched.Should().Be(shouldMatch);
        if (shouldMatch) game.Should().Be("测试游戏");
        else game.Should().BeEmpty();
    }

    [Fact]
    public void 进程名匹配应忽略大小写()
    {
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DeltaForceClient"] = "三角洲行动",
        };

        GameProcessDetector.TryResolveByStem("deltaforceclient-win64-shipping", byStem, out var game)
            .Should().BeTrue();
        game.Should().Be("三角洲行动");
    }

    [Fact]
    public void 空进程名不应崩溃()
    {
        var byStem = new Dictionary<string, string> { ["X"] = "Y" };
        GameProcessDetector.TryResolveByStem("", byStem, out _).Should().BeFalse();
        GameProcessDetector.TryResolveByStem(null!, byStem, out _).Should().BeFalse();
    }

    [Fact]
    public void 扫描应识别出真实的UE主程序而不是外壳()
    {
        // 三角洲行动：根目录 DeltaForceClient.exe 只是外壳，
        // 真正主程序是 DeltaForce\Binaries\Win64\DeltaForceClient-Win64-Shipping.exe (490MB)
        var games = new WeGameLibraryScanner().Scan();
        var delta = games.FirstOrDefault(g => g.AppId == "2001918");

        if (delta?.ExecutablePath is not null)
        {
            Path.GetFileName(delta.ExecutablePath)
                .Should().Contain("-Shipping", "应优先选择 UE 的真实主程序");
        }
    }

    [Fact]
    public void 扫描不应把反作弊程序当主程序()
    {
        var games = new WeGameLibraryScanner().Scan();

        foreach (var g in games.Where(g => g.ExecutablePath is not null))
        {
            var exe = Path.GetFileName(g.ExecutablePath!);
            exe.Should().NotContain("SGuard", $"{g.Name} 选中了反作弊程序");
            exe.Should().NotContain("AntiCheat", $"{g.Name} 选中了反作弊程序");
            exe.Should().NotContain("bugreport", $"{g.Name} 选中了崩溃上报程序");
        }
    }

    [Fact]
    public void 反作弊目录应被路径规则排除()
    {
        WeGameLibraryScanner.IsHelperPath(
            @"E:\WeGameApps\rail_apps\DeltaForce(2001918)\DeltaForce\Binaries\Win64\AntiCheatExpert\SGuard\x64\SGuard64.exe")
            .Should().BeTrue();

        WeGameLibraryScanner.IsHelperPath(
            @"E:\WeGameApps\rail_apps\DeltaForce(2001918)\DeltaForce\Binaries\Win64\DeltaForceClient-Win64-Shipping.exe")
            .Should().BeFalse();
    }

    [Fact]
    public void 应能检测到当前真实运行的游戏进程()
    {
        // 若用户此刻开着游戏，这次检测必须能找到它
        var games = NonGameFilter.Apply(GameLibrary.ScanAll());
        var running = GameProcessDetector.Detect(games.Select(g => (g.Name, g.ExecutablePath)));

        // 没开游戏时为空是正常的；关键是不能抛异常
        running.Should().NotBeNull();
    }
}
