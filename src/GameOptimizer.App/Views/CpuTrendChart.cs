namespace GameOptimizer.App.Views;

using System.Windows;
using System.Windows.Media;
using GameOptimizer.Core.Analysis;

/// <summary>
/// 最近 30 秒总体负载趋势图（自绘）。
///
/// 为什么自己画而不是拉一个图表库：这里只需要一条折线 + 两条阈值参考线，
/// 引一个库会让单文件发布体积和依赖都变复杂，而坐标换算已经在
/// <see cref="CpuTrendGeometry"/> 里被单测覆盖，渲染层只剩「连线」这一件事。
///
/// 横轴按**时间**排布，中间漏采就会留出空白——数据不完整要看得见，
/// 不能把缺口压缩掉假装连续。
/// </summary>
public sealed class CpuTrendChart : FrameworkElement
{
    /// <summary>趋势点集合。为空时只画底板与参考线。</summary>
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<CpuTrendPoint>), typeof(CpuTrendChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>折线颜色（十六进制）。默认用主题主色。</summary>
    public static readonly DependencyProperty AccentHexProperty =
        DependencyProperty.Register(nameof(AccentHex), typeof(string), typeof(CpuTrendChart),
            new FrameworkPropertyMetadata("#FF4C8DFF", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<CpuTrendPoint>? Points
    {
        get => (IReadOnlyList<CpuTrendPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public string AccentHex
    {
        get => (string)GetValue(AccentHexProperty);
        set => SetValue(AccentHexProperty, value);
    }

    /// <summary>画布内边距：留白让 0% 与 100% 的线不被裁掉一半。</summary>
    private const double Padding = 6;

    private static readonly SolidColorBrush BackBrush = Frozen(Color.FromRgb(0x0B, 0x0C, 0x0E));
    private static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x33, 0x5C, 0x5F, 0x66), 1);
    private static readonly Pen WarnPen = FrozenPen(Color.FromArgb(0x40, 0xFF, 0x9F, 0x0A), 1);
    private static readonly Pen BadPen = FrozenPen(Color.FromArgb(0x40, 0xFF, 0x45, 0x3A), 1);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // 底板与描边：与卡片内的小面板一致
        dc.DrawRoundedRectangle(BackBrush, null, new Rect(0, 0, width, height), 8, 8);

        var innerWidth = Math.Max(1, width - Padding * 2);
        var innerHeight = Math.Max(1, height - Padding * 2);
        var baseY = Padding + innerHeight;

        // 60% / 80% 参考线：与诊断阈值同一口径，用户能直接看出「刚才那一段是不是高负载」
        DrawThreshold(dc, CpuLoadAnalyzer.WatchThreshold, innerHeight, innerWidth, baseY, WarnPen);
        DrawThreshold(dc, CpuLoadAnalyzer.HighLoadThreshold, innerHeight, innerWidth, baseY, BadPen);

        var points = Points;
        if (points is null || points.Count == 0) return;

        // 右边界取最新一帧：页面停采时曲线不会凭「当前时间」被推走
        var mapped = CpuTrendGeometry.Map(
            points,
            nowMs: points[^1].TimestampMs,
            windowSeconds: CpuLoadAnalyzer.TrendWindowSeconds,
            width: innerWidth,
            height: innerHeight);

        if (mapped.Count == 0) return;

        var accent = ParseColor(AccentHex);
        var linePen = FrozenPen(accent, 1.6);
        var fill = new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        fill.Freeze();

        // 折线（不闭合）用于描边；面积（闭合到底边）用于填充
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(Offset(mapped[0]), isFilled: false, isClosed: false);
            for (var i = 1; i < mapped.Count; i++)
                ctx.LineTo(Offset(mapped[i]), isStroked: true, isSmoothJoin: true);
        }
        line.Freeze();

        if (mapped.Count > 1)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(Offset(mapped[0]), isFilled: true, isClosed: true);
                for (var i = 1; i < mapped.Count; i++)
                    ctx.LineTo(Offset(mapped[i]), isStroked: false, isSmoothJoin: true);
                ctx.LineTo(new Point(Padding + mapped[^1].X, baseY), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(Padding + mapped[0].X, baseY), isStroked: false, isSmoothJoin: false);
            }
            area.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        dc.DrawGeometry(null, linePen, line);

        // 末端点：让「当前值」在曲线上有落点
        var last = Offset(mapped[^1]);
        dc.DrawEllipse(new SolidColorBrush(accent), null, last, 2.6, 2.6);
    }

    private static Point Offset(TrendChartPoint p) => new(Padding + p.X, Padding + p.Y);

    private void DrawThreshold(
        DrawingContext dc, double percent, double innerHeight, double innerWidth, double baseY, Pen pen)
    {
        var y = Padding + (innerHeight - percent / 100.0 * innerHeight);
        // 参考线允许落在画布边上，所以只有明显越界时才跳过
        if (y < Padding - 0.5 || y > baseY + 0.5) return;
        dc.DrawLine(pen, new Point(Padding, y), new Point(Padding + innerWidth, y));
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0x4C, 0x8D, 0xFF); }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
