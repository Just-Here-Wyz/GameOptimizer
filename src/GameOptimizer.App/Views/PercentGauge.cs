namespace GameOptimizer.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GameOptimizer.Core.Analysis;

/// <summary>
/// 环形百分比仪表盘（首页四项占用）。
///
/// 视觉对齐参考图里外设卡片上的电量环：细环 + 顶部留缺口的底色轨道 + 一截高亮进度。
/// 颜色由 <see cref="OccupancyPalette"/> 决定：0~30 绿、31~60 蓝、61~80 橙、81~100 红。
///
/// 为什么用 Path 而不是 Ellipse 的 StrokeDashArray：
/// 后者要按周长换算 dash 长度，尺寸一变就偏；这里直接算圆弧终点坐标，
/// 角度逻辑放在 <see cref="GaugeGeometry"/> 里，已被单元测试覆盖。
/// </summary>
public sealed class PercentGauge : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(PercentGauge),
            new PropertyMetadata(0.0, OnVisualChanged));

    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(PercentGauge),
            new PropertyMetadata(7.0, OnVisualChanged));

    /// <summary>
    /// 环中心文字的覆盖值。默认空字符串 = 按百分比画数字。
    ///
    /// 存在的理由：CPU 总占用读不到时必须显示「未知」，而环本身只能画数字。
    /// 用 0% 的环 + 「未知」字样，比画一个 0% 的环诚实——0% 是「测到了空闲」，不是「没测到」。
    /// </summary>
    public static readonly DependencyProperty ValueTextOverrideProperty =
        DependencyProperty.Register(nameof(ValueTextOverride), typeof(string), typeof(PercentGauge),
            new PropertyMetadata(string.Empty, OnVisualChanged));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(PercentGauge),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x33)), OnVisualChanged));

    /// <summary>占用率 0~100。超出范围会被钳制，非有限值按 0。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>环宽。</summary>
    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>环中心文字覆盖值。空 = 自动画百分比数字。</summary>
    public string ValueTextOverride
    {
        get => (string)GetValue(ValueTextOverrideProperty);
        set => SetValue(ValueTextOverrideProperty, value);
    }

    /// <summary>底色轨道画刷。</summary>
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    private readonly Canvas _canvas = new();
    private readonly Path _track = new();
    private readonly Path _arc = new();
    private readonly TextBlock _valueText = new();

    public PercentGauge()
    {
        _canvas.Background = Brushes.Transparent;
        _canvas.Children.Add(_track);
        _canvas.Children.Add(_arc);

        _valueText.FontFamily = new FontFamily("Microsoft YaHei UI");
        _valueText.FontWeight = FontWeights.SemiBold;
        _valueText.TextAlignment = TextAlignment.Center;
        _valueText.Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xED, 0xEF));
        _canvas.Children.Add(_valueText);

        Content = _canvas;

        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PercentGauge)d).Redraw();

    private void Redraw()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        // 轨道环与进度环共用同一套几何，避免两者错位
        var stroke = Math.Max(1, Thickness);
        var radius = (size - stroke) / 2;
        if (radius <= 0) return;

        var center = size / 2;
        var percent = OccupancyPalette.Clamp(Value);
        var color = ParseColor(OccupancyPalette.ColorHex(OccupancyPalette.Level(Value)));

        // 顶部留 90° 缺口 —— 参考图的电量环就是这个造型
        const double maxSweep = GaugeGeometry.OpenBottomSweep;
        const double startAngle = -90 - (GaugeGeometry.FullCircle - maxSweep) / 2;

        _track.Stroke = TrackBrush;
        _track.StrokeThickness = stroke;
        _track.StrokeStartLineCap = PenLineCap.Round;
        _track.StrokeEndLineCap = PenLineCap.Round;
        _track.Fill = null;
        _track.Data = BuildArc(center, radius, startAngle, maxSweep);

        _arc.Stroke = new SolidColorBrush(color);
        _arc.StrokeThickness = stroke;
        _arc.StrokeStartLineCap = PenLineCap.Round;
        _arc.StrokeEndLineCap = PenLineCap.Round;
        _arc.Fill = null;

        // 进度为 0 时画一小段可见的圆点，表示「读到了 0」而不是「画坏了」
        _arc.Data = percent <= 0.5
            ? BuildDot(center, radius, startAngle)
            : BuildArc(center, radius, startAngle, maxSweep * percent / 100.0);

        // 中心文字：有覆盖值（「未知」）就写它，否则写百分比
        var overrideText = ValueTextOverride;
        var hasOverride = !string.IsNullOrEmpty(overrideText);

        _valueText.Text = hasOverride ? overrideText : $"{percent:F0}";
        _valueText.FontSize = Math.Max(12, size * (hasOverride ? 0.18 : 0.26));
        _valueText.Width = size;
        Canvas.SetLeft(_valueText, 0);
        Canvas.SetTop(_valueText, center - _valueText.FontSize * 0.82);
    }

    private static Geometry BuildArc(double center, double radius, double startAngle, double sweep)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var start = GaugeGeometry.PointAt(center, center, radius, startAngle);
            var end = GaugeGeometry.PointAt(center, center, radius, startAngle + sweep);

            ctx.BeginFigure(new Point(start.X, start.Y), isFilled: false, isClosed: false);
            ctx.ArcTo(
                new Point(end.X, end.Y),
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: GaugeGeometry.IsLargeArc(sweep),
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>0% 时画一个点：用极小角度的圆弧，线帽是圆的，看起来就是个圆点。</summary>
    private static Geometry BuildDot(double center, double radius, double startAngle)
        => BuildArc(center, radius, startAngle, 0.01);

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.Gray; }
    }
}
