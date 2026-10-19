namespace GameOptimizer.Core.Analysis;

/// <summary>仪表盘的一段圆弧（角度单位为度，0° 指向正上方，顺时针为正）。</summary>
public readonly record struct GaugeArc(double StartAngle, double Sweep);

/// <summary>
/// 环形仪表盘的角度计算。
///
/// 抽成纯函数的原因和坐标计算一样：画在 WPF 里的东西出错时看不出来
/// （弧为 0 长度就整圈消失，算成 359.99° 就留一条缝），必须在没有 UI 的环境里锁死。
/// </summary>
public static class GaugeGeometry
{
    /// <summary>完整一圈 360°。</summary>
    public const double FullCircle = 360.0;

    /// <summary>顶部留 90° 缺口的常见表盘样式。</summary>
    public const double OpenBottomSweep = 270.0;

    /// <summary>
    /// 把百分比换算成圆弧。
    /// </summary>
    /// <param name="percent">占用率，会被钳制到 [0,100]。</param>
    /// <param name="maxSweep">表盘最大扫过角度，默认整圈。</param>
    /// <param name="startAngle">起始角，默认从顶部（-90°，即 12 点方向）开始顺时针。</param>
    public static GaugeArc ForPercent(double percent, double maxSweep = FullCircle, double startAngle = -90)
    {
        if (maxSweep <= 0 || double.IsNaN(maxSweep) || double.IsInfinity(maxSweep))
        {
            maxSweep = FullCircle;
        }

        var p = OccupancyPalette.Clamp(percent);
        var sweep = maxSweep * p / 100.0;

        // 100% 时必须扫满一圈：浮点乘除可能算出 359.99999，
        // 圆环会留下一条肉眼可见的缝，看起来像「没画满」。
        if (p >= 100) sweep = maxSweep;

        return new GaugeArc(startAngle, sweep);
    }

    /// <summary>
    /// 圆弧终点坐标（以圆心为原点、半径 r 的局部坐标系）。
    /// WPF 的 ArcSegment 需要终点而不是「扫过多少度」，这里统一算好。
    /// </summary>
    public static (double X, double Y) PointAt(double centerX, double centerY, double radius, double angleDegrees)
    {
        var rad = angleDegrees * Math.PI / 180.0;
        return (centerX + radius * Math.Sin(rad), centerY - radius * Math.Cos(rad));
    }

    /// <summary>ArcSegment 需要 IsLargeArc：扫过角度超过 180° 时必须置位，否则会画成短弧。</summary>
    public static bool IsLargeArc(double sweep) => Math.Abs(sweep) > 180.0;

    /// <summary>100% 时退化成整圆，用 Ellipse 画比 ArcSegment 更稳（零长度圆弧画不出来）。</summary>
    public static bool IsFullCircle(double percent) => OccupancyPalette.Clamp(percent) >= 100;
}
