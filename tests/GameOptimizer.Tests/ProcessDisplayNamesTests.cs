namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// 白名单显示名的用例。
///
/// 需求来源：白名单要显示应用名。
/// 显示 wallpaper64、telemetry_agent 这种东西，用户根本不知道该不该关。
/// </summary>
public class ProcessDisplayNamesTests
{
    [Theory]
    [InlineData("wallpaper64", "壁纸引擎 Wallpaper Engine")]
    [InlineData("PcOptimizationCenter", "华为电脑管家")]
    [InlineData("telemetry_agent", "遥测上报组件")]
    [InlineData("TranslucentTB", "任务栏透明美化")]
    [InlineData("chrome", "Chrome 浏览器")]
    [InlineData("explorer", "资源管理器")]
    public void 已知进程应解析成看得懂的应用名(string process, string expected)
    {
        ProcessDisplayNames.Resolve(process).Should().Be(expected);
    }

    [Fact]
    public void 未收录的进程应原样返回而不是编一个名字()
    {
        // 猜出来的译名比显示进程名更糟
        ProcessDisplayNames.Resolve("some_unknown_app_xyz").Should().Be("some_unknown_app_xyz");
    }

    [Fact]
    public void 解析应忽略大小写()
    {
        ProcessDisplayNames.Resolve("WALLPAPER64").Should().Be("壁纸引擎 Wallpaper Engine");
    }

    [Fact]
    public void 默认名单里的每一项都应有显示名()
    {
        // 用户要求白名单显示应用名；有漏项就会冒出裸进程名
        var missing = BackgroundProcessWhitelist.Strong
            .Where(n => !ProcessDisplayNames.HasName(n))
            .ToList();

        missing.Should().BeEmpty($"这些项还没有中文名：{string.Join(", ", missing)}");
    }

    [Fact]
    public void 保护名单里的进程不需要显示名()
    {
        // 必要进程已从白名单界面隐藏，不会展示给用户。
        // 但仍要求「默认名单」全覆盖，否则界面会冒出裸进程名（见上一条用例）。
        ProcessDisplayNames.HasName("explorer").Should().BeTrue("常见的系统项仍值得有名字");
    }

    [Fact]
    public void 空名字应返回空串而不是抛异常()
    {
        ProcessDisplayNames.Resolve("").Should().Be("");
        ProcessDisplayNames.Resolve("   ").Should().Be("");
    }
}
