namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;

/// <summary>
/// 占用率四档配色。用户约定：0~30 绿、31~60 蓝、61~80 橙、81~100 红。
/// 这条口径直接被首页仪表盘使用，错一档就会让「吃紧」看起来像「正常」。
/// </summary>
public class OccupancyPaletteTests
{
    [Theory]
    [InlineData(0, OccupancyLevel.Low)]
    [InlineData(15, OccupancyLevel.Low)]
    [InlineData(30, OccupancyLevel.Low)]        // 边界属于绿色档
    [InlineData(30.1, OccupancyLevel.Normal)]
    [InlineData(31, OccupancyLevel.Normal)]
    [InlineData(60, OccupancyLevel.Normal)]     // 边界属于蓝色档
    [InlineData(60.1, OccupancyLevel.High)]
    [InlineData(61, OccupancyLevel.High)]
    [InlineData(80, OccupancyLevel.High)]       // 边界属于橙色档
    [InlineData(80.1, OccupancyLevel.Critical)]
    [InlineData(81, OccupancyLevel.Critical)]   // 用户明确写了 81~100 为红色
    [InlineData(100, OccupancyLevel.Critical)]
    public void 占用率应按约定分档(double percent, OccupancyLevel expected)
        => OccupancyPalette.Level(percent).Should().Be(expected);

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(150, 100)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void 越界与非法值应被钳制(double input, double expected)
    {
        // PDH 在读数间隔异常时给过 123%，界面不能因此出现「123% 的红色仪表」
        OccupancyPalette.Clamp(input).Should().Be(expected);
    }

    [Fact]
    public void 越界值不应落到错误的颜色档()
    {
        OccupancyPalette.Level(123).Should().Be(OccupancyLevel.Critical);
        OccupancyPalette.Level(12345).Should().Be(OccupancyLevel.Critical);
        OccupancyPalette.Level(double.NaN).Should().Be(OccupancyLevel.Low);
    }

    [Theory]
    [InlineData(OccupancyLevel.Low, "#34C759")]
    [InlineData(OccupancyLevel.Normal, "#4C8DFF")]
    [InlineData(OccupancyLevel.High, "#FF9F0A")]
    [InlineData(OccupancyLevel.Critical, "#FF453A")]
    public void 每档颜色应与界面资源一致(OccupancyLevel level, string hex)
        => OccupancyPalette.ColorHex(level).Should().Be(hex);

    [Fact]
    public void 四档颜色必须互不相同()
    {
        // 两档撞色会让用户完全看不出差别
        var colors = Enum.GetValues<OccupancyLevel>().Select(OccupancyPalette.ColorHex).ToList();
        colors.Should().OnlyHaveUniqueItems();
    }
}
