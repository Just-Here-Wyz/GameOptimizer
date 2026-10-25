namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;

/// <summary>
/// 趋势图坐标换算。
///
/// 画在界面上的东西算错时肉眼极难发现：整条贴底、整片空白、缺口被压缩掉，
/// 看起来都像「没数据」。所以坐标逻辑放 Core 并单测。
/// </summary>
public class CpuTrendGeometryTests
{
    private const double Width = 300;
    private const double Height = 100;

    [Fact]
    public void 没有数据时返回空集合()
    {
        CpuTrendGeometry.Map([], 0, 30, Width, Height).Should().BeEmpty();
        CpuTrendGeometry.Map(null, 0, 30, Width, Height).Should().BeEmpty();
    }

    [Fact]
    public void 画布尺寸非法时不产生坐标()
    {
        var points = new[] { new CpuTrendPoint(0, 50) };

        CpuTrendGeometry.Map(points, 0, 30, 0, Height).Should().BeEmpty();
        CpuTrendGeometry.Map(points, 0, 30, Width, 0).Should().BeEmpty();
        CpuTrendGeometry.Map(points, 0, 0, Width, Height).Should().BeEmpty();
    }

    [Fact]
    public void 最新一帧应落在右边缘且零占用落在底边()
    {
        var points = new[] { new CpuTrendPoint(30_000, 0) };

        var mapped = CpuTrendGeometry.Map(points, 30_000, 30, Width, Height);

        mapped.Should().ContainSingle();
        mapped[0].X.Should().Be(Width);
        mapped[0].Y.Should().Be(Height, "0% 在底边");
    }

    [Fact]
    public void 满占用应落在顶边()
    {
        var points = new[] { new CpuTrendPoint(30_000, 100) };

        var mapped = CpuTrendGeometry.Map(points, 30_000, 30, Width, Height);

        mapped[0].Y.Should().Be(0);
    }

    [Fact]
    public void 横轴按时间线性排布而不是按帧序号()
    {
        // 30 秒窗口里，20 秒前的点应落在 1/3 宽度处
        var points = new[]
        {
            new CpuTrendPoint(10_000, 50),
            new CpuTrendPoint(20_000, 50),
            new CpuTrendPoint(30_000, 50),
        };

        var mapped = CpuTrendGeometry.Map(points, 30_000, 30, Width, Height);

        mapped.Should().HaveCount(3);
        mapped[0].X.Should().BeApproximately(Width / 3, 0.001);
        mapped[1].X.Should().BeApproximately(Width * 2 / 3, 0.001);
        mapped[2].X.Should().BeApproximately(Width, 0.001);
    }

    [Fact]
    public void 窗口外的点应被丢弃()
    {
        // 35 秒前的点不属于「最近 30 秒」，画出来会让人以为窗口更长
        var points = new[]
        {
            new CpuTrendPoint(5_000, 80),
            new CpuTrendPoint(40_000, 40),
        };

        var mapped = CpuTrendGeometry.Map(points, 40_000, 30, Width, Height);

        mapped.Should().ContainSingle();
        mapped[0].X.Should().Be(Width);
    }

    [Fact]
    public void 越界占用应被钳制在画布内()
    {
        var points = new[]
        {
            new CpuTrendPoint(10_000, 150),
            new CpuTrendPoint(20_000, -20),
        };

        var mapped = CpuTrendGeometry.Map(points, 20_000, 30, Width, Height);

        mapped.All(p => p.Y >= 0 && p.Y <= Height).Should().BeTrue();
        mapped[0].Y.Should().Be(0);
        mapped[1].Y.Should().Be(Height);
    }

    [Fact]
    public void 时间戳乱序时不得画出回头线()
    {
        var points = new[]
        {
            new CpuTrendPoint(20_000, 30),
            new CpuTrendPoint(10_000, 60),
            new CpuTrendPoint(25_000, 40),
        };

        var mapped = CpuTrendGeometry.Map(points, 25_000, 30, Width, Height);

        mapped.Select(p => p.X).Should().BeInAscendingOrder();
    }

    [Fact]
    public void 漏采造成的缺口应体现为横轴空白而不是压缩()
    {
        // 两个点之间隔了 20 秒：它们的 X 距离必须是宽度的 2/3，缺口才看得见
        var points = new[]
        {
            new CpuTrendPoint(9_000, 50),
            new CpuTrendPoint(29_000, 50),
        };

        var mapped = CpuTrendGeometry.Map(points, 30_000, 30, Width, Height);

        mapped[1].X.Should().BeApproximately(Width * 29.0 / 30, 0.001);
        mapped[0].X.Should().BeApproximately(Width * 9.0 / 30, 0.001);
    }
}
