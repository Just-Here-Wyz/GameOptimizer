namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// Steam 显示名映射。
/// 用户反馈：NARAKA: BLADEPOINT 就是永劫无间，直接改显示名（无需验证）。
/// appmanifest 里写的是英文原名，界面要显示中文。
/// </summary>
public class SteamDisplayNamesTests
{
    [Theory]
    [InlineData("1203220", "NARAKA: BLADEPOINT", "永劫无间")]
    public void 已收录的游戏应显示中文名(string appId, string originalName, string expected)
    {
        SteamDisplayNames.Resolve(appId, originalName).Should().Be(expected);
    }

    [Fact]
    public void 未收录的游戏应保持原名()
    {
        SteamDisplayNames.Resolve("431960", "Wallpaper Engine").Should().Be("Wallpaper Engine");
        SteamDisplayNames.Resolve("", "SomeGame").Should().Be("SomeGame");
    }

    [Fact]
    public void 映射表只收录经过确认的条目()
    {
        SteamDisplayNames.Count.Should().Be(1);
    }

    [Fact]
    public void 清单解析NARAKA时应直接得到永劫无间()
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
            entry!.Name.Should().Be("永劫无间");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 真实扫描中NARAKA应显示为永劫无间()
    {
        var games = NonGameFilter.Apply(GameLibrary.ScanAll());

        var naraka = games.FirstOrDefault(g => g.AppId == "1203220");
        if (naraka is not null)
        {
            naraka.Name.Should().Be("永劫无间");
            naraka.Name.Should().NotBe("NARAKA: BLADEPOINT");
        }
    }
}
