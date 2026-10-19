namespace GameOptimizer.Core.Analysis;

/// <summary>一个归一化后的绘图点（0..1 范围，原点在左上）。</summary>
public readonly record struct ChartPoint(double X, double Y);

/// <summary>
/// 折线图的坐标计算。抽出来是为了能脱离 WPF 单独测试——
/// 之前这段逻辑写在 UserControl 里，画不出来时只能靠肉眼发现。
/// </summary>
public static class ChartGeometry
{
    /// <summary>
    /// 把数值序列映射到 [0,1] 的归一化坐标。
    /// Y 轴向上为小值，所以数值越大 Y 越小（越靠上）。
    /// </summary>
    /// <returns>点数不足 2 个时返回空，调用方据此清空画布。</returns>
    public static IReadOnlyList<ChartPoint> Build(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return [];

        var min = values.Min();
        var max = values.Max();
        var range = max - min;

        // 全平数据：画一条水平中线，避免除零
        if (range < 1e-9)
        {
            return [new ChartPoint(0, 0.5), new ChartPoint(1, 0.5)];
        }

        var step = 1.0 / (values.Count - 1);
        var points = new ChartPoint[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var normalized = (values[i] - min) / range;
            points[i] = new ChartPoint(i * step, 1.0 - normalized);
        }
        return points;
    }

    /// <summary>把归一化点映射到实际像素尺寸。</summary>
    public static IReadOnlyList<(double X, double Y)> Scale(IReadOnlyList<ChartPoint> points, double width, double height)
    {
        if (points.Count == 0 || width <= 0 || height <= 0) return [];
        return points.Select(p => (p.X * width, p.Y * height)).ToArray();
    }
}
