namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;

/// <summary>
/// 仪表盘圆弧角度。这类逻辑画在 WPF 里出错时肉眼很难发现
/// （弧长算成 0 就整圈消失，算成 359.99 就留一条缝），所以必须在无 UI 环境里锁死。
/// </summary>
public class GaugeGeometryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 90)]    // 整圈 360° 的 1/4
    [InlineData(50, 180)]
    [InlineData(75, 270)]
    [InlineData(100, 360)]
    public void 整圈仪表应按百分比换算扫过角度(double percent, double expectedSweep)
    {
        var arc = GaugeGeometry.ForPercent(percent);
        arc.Sweep.Should().BeApproximately(expectedSweep, 1e-9);
    }

    [Fact]
    public void 百分之百必须扫满一圈_否则圆环留缝()
    {
        // 浮点乘除可能算出 359.99999，圆环上会出现一条肉眼可见的缝
        GaugeGeometry.ForPercent(100).Sweep.Should().Be(GaugeGeometry.FullCircle);
        GaugeGeometry.ForPercent(100, GaugeGeometry.OpenBottomSweep).Sweep
            .Should().Be(GaugeGeometry.OpenBottomSweep);
    }

    [Fact]
    public void 缺口罩按最大角度缩放()
    {
        // 270° 的表盘：50% 应该是 135°，不是 180°
        GaugeGeometry.ForPercent(50, GaugeGeometry.OpenBottomSweep).Sweep
            .Should().BeApproximately(135, 1e-9);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(150)]
    [InlineData(double.NaN)]
    public void 非法百分比不应产生越界角度(double percent)
    {
        var arc = GaugeGeometry.ForPercent(percent);
        arc.Sweep.Should().BeInRange(0, GaugeGeometry.FullCircle);
    }

    [Fact]
    public void 起点默认为正上方()
    {
        // -90° 在屏幕坐标系里就是 12 点方向，参考图的电量环从这里开始
        GaugeGeometry.ForPercent(50).StartAngle.Should().Be(-90);
    }

    [Fact]
    public void 角度转坐标应顺时针绕行()
    {
        // 0° = 正上方，90° = 正右方
        var top = GaugeGeometry.PointAt(50, 50, 10, 0);
        top.X.Should().BeApproximately(50, 1e-9);
        top.Y.Should().BeApproximately(40, 1e-9);

        var right = GaugeGeometry.PointAt(50, 50, 10, 90);
        right.X.Should().BeApproximately(60, 1e-9);
        right.Y.Should().BeApproximately(50, 1e-9);

        var bottom = GaugeGeometry.PointAt(50, 50, 10, 180);
        bottom.X.Should().BeApproximately(50, 1e-9);
        bottom.Y.Should().BeApproximately(60, 1e-9);
    }

    [Theory]
    [InlineData(180, false)]   // 正好半圈走短弧才不出错
    [InlineData(181, true)]
    [InlineData(270, true)]
    [InlineData(90, false)]
    public void 超过半圈才标记大弧(double sweep, bool expected)
    {
        // ArcSegment 的 IsLargeArc 用错会把进度画成反向短弧
        GaugeGeometry.IsLargeArc(sweep).Should().Be(expected);
    }

    [Fact]
    public void 满值应判定为整圆()
    {
        GaugeGeometry.IsFullCircle(100).Should().BeTrue();
        GaugeGeometry.IsFullCircle(99.9).Should().BeFalse();
        GaugeGeometry.IsFullCircle(120).Should().BeTrue(); // 越界先钳制
    }
}
