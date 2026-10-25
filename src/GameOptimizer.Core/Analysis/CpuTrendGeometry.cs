namespace GameOptimizer.Core.Analysis;

/// <summary>趋势图上的一个绘图点（像素坐标）。Core 不引用 WPF，所以用自己的结构。</summary>
public sealed record TrendChartPoint(double X, double Y);

/// <summary>
/// 趋势图的坐标换算。纯函数，放 Core 才能单测——
/// 画在界面上的东西算错时肉眼极难发现（曲线整条贴底或整片空白都像「没数据」）。
///
/// 横轴按**时间**而不是帧序号排布：中间漏采时曲线会留出空白，
/// 这正是「数据不完整」该有的样子，而不是把缺口压缩掉假装连续。
/// </summary>
public static class CpuTrendGeometry
{
    /// <summary>
    /// 把 30 秒窗口内的采样点映射到 width × height 的画布。
    /// </summary>
    /// <param name="points">按时间升序的采样点。</param>
    /// <param name="nowMs">窗口右边界（通常是最新一帧的时间戳）。</param>
    /// <param name="windowSeconds">窗口长度，与 <see cref="CpuLoadAnalyzer.TrendWindowSeconds"/> 同源。</param>
    /// <param name="width">画布宽度。</param>
    /// <param name="height">画布高度。</param>
    public static IReadOnlyList<TrendChartPoint> Map(
        IReadOnlyList<CpuTrendPoint>? points,
        long nowMs,
        double windowSeconds,
        double width,
        double height)
    {
        if (points is null || points.Count == 0) return [];
        if (width <= 0 || height <= 0 || windowSeconds <= 0) return [];

        var windowMs = windowSeconds * 1000;
        var result = new List<TrendChartPoint>(points.Count);
        var lastX = double.NegativeInfinity;

        foreach (var p in points)
        {
            var ageMs = nowMs - p.TimestampMs;

            // 窗口外的点丢弃：横轴是固定的 0~30 秒，超出就不该画在图里
            if (ageMs > windowMs || ageMs < 0) continue;

            var x = (1 - ageMs / windowMs) * width;
            if (x < lastX) continue; // 时间戳乱序时宁可丢点，也不要画出回头的折线

            var percent = OccupancyPalette.Clamp(p.Percent);
            var y = height - percent / 100.0 * height;

            result.Add(new TrendChartPoint(x, y));
            lastX = x;
        }

        return result;
    }
}
