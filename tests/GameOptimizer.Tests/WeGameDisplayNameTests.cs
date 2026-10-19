namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// WeGame 显示名映射。
/// 用户反馈：DeltaForce 应显示为「三角洲行动」。
/// WeGame 安装目录名是英文工程名，本地配置全加密、exe 无版本元数据，
/// 所以只能靠映射表。
/// </summary>
public class WeGameDisplayNameTests
{
    [Theory]
    [InlineData("2001918", "DeltaForce", "三角洲行动")]
    [InlineData("2001715", "无畏契约", "无畏契约")]
    public void 已收录的游戏应显示中文名(string appId, string folderName, string expected)
    {
        WeGameDisplayNames.Resolve(appId, folderName).Should().Be(expected);
    }

    [Fact]
    public void 未收录的游戏应回退到目录名而不是显示空()
    {
        WeGameDisplayNames.Resolve("9999999", "SomeNewGame").Should().Be("SomeNewGame");
    }

    [Fact]
    public void AppID为空时应回退到目录名()
    {
        WeGameDisplayNames.Resolve("", "SomeGame").Should().Be("SomeGame");
        WeGameDisplayNames.Resolve(null!, "SomeGame").Should().Be("SomeGame");
    }

    [Fact]
    public void 映射表只收录经过确认的条目()
    {
        // 用户选择只加本机这两个，不应被塞入未验证的翻译
        WeGameDisplayNames.Count.Should().Be(2);
    }

    [Fact]
    public void 真实扫描中DeltaForce应显示为三角洲行动()
    {
        var games = new WeGameLibraryScanner().Scan();

        var delta = games.FirstOrDefault(g => g.AppId == "2001918");
        if (delta is not null)
        {
            delta.Name.Should().Be("三角洲行动");
            delta.Name.Should().NotBe("DeltaForce");
        }
    }

    [Fact]
    public void 真实扫描中有映射的游戏应显示映射后的名字()
    {
        // 注意《无畏契约》的目录名和显示名恰好相同，不能笼统断言"不等于目录名"；
        // 这里直接校验每个有映射的 ID，其显示名等于映射值。
        var games = new WeGameLibraryScanner().Scan();

        foreach (var g in games.Where(g => WeGameDisplayNames.HasMapping(g.AppId)))
        {
            var expected = WeGameDisplayNames.Resolve(g.AppId, "占位名");
            g.Name.Should().Be(expected, $"{g.AppId} 应显示映射后的名字");
        }
    }

    [Fact]
    public void 显示名变化不应影响主程序识别()
    {
        // 主程序用目录名匹配（DeltaForce → DeltaForceClient.exe），
        // 若误用中文显示名会导致匹配不到
        var games = new WeGameLibraryScanner().Scan();
        var delta = games.FirstOrDefault(g => g.AppId == "2001918");

        if (delta is not null && delta.ExecutablePath is not null)
        {
            Path.GetFileName(delta.ExecutablePath)
                .Should().Contain("DeltaForce", "主程序应仍按英文工程名匹配到");
        }
    }
}
