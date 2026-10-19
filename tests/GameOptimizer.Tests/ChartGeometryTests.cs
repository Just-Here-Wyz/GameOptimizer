namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;

/// <summary>
/// 折线图坐标计算。之前这段逻辑写在 WPF UserControl 里且用了错误的
/// DependencyProperty，图表整片空白却没被任何测试发现——现在拆到 Core 里锁死。
/// </summary>
public class ChartGeometryTests
{
    [Fact]
    public void 少于两个点应返回空_调用方据此清空画布()
    {
        ChartGeometry.Build([]).Should().BeEmpty();
        ChartGeometry.Build([42.0]).Should().BeEmpty();
    }

    [Fact]
    public void 两点应映射到左右两端()
    {
        var pts = ChartGeometry.Build([10.0, 20.0]);

        pts.Should().HaveCount(2);
        pts[0].X.Should().Be(0);
        pts[1].X.Should().Be(1);
    }

    [Fact]
    public void 数值越大纵坐标越小_因为屏幕Y轴向下()
    {
        var pts = ChartGeometry.Build([10.0, 20.0]);

        // 20 是较大值，应该画在更靠上的位置（Y 更小）
        pts[1].Y.Should().BeLessThan(pts[0].Y);
    }

    [Fact]
    public void 最小值应落在底部_最大值落在顶部()
    {
        var pts = ChartGeometry.Build([5.0, 50.0, 500.0]);

        pts[0].Y.Should().BeApproximately(1.0, 1e-9); // 最小值 → 底部
        pts[2].Y.Should().BeApproximately(0.0, 1e-9); // 最大值 → 顶部
    }

    [Fact]
    public void 所有数值相同时应画一条中线而不是除零()
    {
        var pts = ChartGeometry.Build([3.0, 3.0, 3.0, 3.0]);

        pts.Should().HaveCount(2);
        pts.Should().OnlyContain(p => p.Y == 0.5);
    }

    [Fact]
    public void 归一化坐标必须落在0到1之间()
    {
        var pts = ChartGeometry.Build([1.0, 999.0, 3.0, 45.0, 0.5]);

        pts.Should().OnlyContain(p => p.X >= 0 && p.X <= 1);
        pts.Should().OnlyContain(p => p.Y >= 0 && p.Y <= 1);
    }

    [Fact]
    public void X轴应等间隔分布()
    {
        var pts = ChartGeometry.Build([1.0, 2.0, 3.0]);

        pts[0].X.Should().BeApproximately(0.0, 1e-9);
        pts[1].X.Should().BeApproximately(0.5, 1e-9);
        pts[2].X.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void 缩放到像素时应按尺寸线性放大()
    {
        var pts = ChartGeometry.Build([10.0, 20.0]);
        var scaled = ChartGeometry.Scale(pts, 200, 100);

        scaled.Should().HaveCount(2);
        scaled[0].X.Should().Be(0);
        scaled[1].X.Should().Be(200);
        scaled[0].Y.Should().Be(100); // 最小值在底部
        scaled[1].Y.Should().Be(0);   // 最大值在顶部
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-10, 50)]
    public void 尺寸非法时缩放应返回空_避免产生NaN坐标(double w, double h)
    {
        var pts = ChartGeometry.Build([1.0, 2.0]);
        ChartGeometry.Scale(pts, w, h).Should().BeEmpty();
    }

    [Fact]
    public void 空点集缩放不应抛异常()
    {
        ChartGeometry.Scale([], 100, 100).Should().BeEmpty();
    }

    [Fact]
    public void 大量数据点不应退化为异常()
    {
        var values = Enumerable.Range(0, 120).Select(i => (double)i).ToArray();
        var pts = ChartGeometry.Build(values);

        pts.Should().HaveCount(120);
        pts.Should().OnlyContain(p => !double.IsNaN(p.X) && !double.IsNaN(p.Y));
    }
}
