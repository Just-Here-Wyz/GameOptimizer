namespace GameOptimizer.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GameOptimizer.Core.Analysis;

/// <summary>
/// 外设卡片上的电量环，造型对齐参考图（GearPower）里那个 `25 / 94 / 83` 的圆环。
///
/// 与首页仪表盘的区别：这里画的是**整圈**（参考图的电量环是闭合的），
/// 中心显示电量数字，下方一行小字说明状态。
/// 读不到电量时中心显示「—」，绝不编造数字。
/// </summary>
public sealed class BatteryRing : UserControl
{
    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(int?), typeof(BatteryRing),
            new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty IsWiredProperty =
        DependencyProperty.Register(nameof(IsWired), typeof(bool), typeof(BatteryRing),
            new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(BatteryRing),
            new PropertyMetadata("", OnVisualChanged));

    /// <summary>电量 0~100；null = 读不到。有线设备忽略此值。</summary>
    public int? Percent
    {
        get => (int?)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    /// <summary>有线设备：环画成蓝色整圈，中心显示「有线」。</summary>
    public bool IsWired
    {
        get => (bool)GetValue(IsWiredProperty);
        set => SetValue(IsWiredProperty, value);
    }

    /// <summary>环下方的说明文字（状态 / 采集时间等）。</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    private readonly Canvas _canvas = new();
    private readonly Path _track = new();
    private readonly Path _arc = new();
    private readonly TextBlock _value = new();
    private readonly TextBlock _unit = new();

    public BatteryRing()
    {
        _canvas.Background = Brushes.Transparent;
        _canvas.Children.Add(_track);
        _canvas.Children.Add(_arc);

        _value.FontFamily = new FontFamily("Microsoft YaHei UI");
        _value.FontWeight = FontWeights.SemiBold;
        _value.TextAlignment = TextAlignment.Center;
        _value.FontSize = 22;
        _value.Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xED, 0xEF));
        _canvas.Children.Add(_value);

        _unit.FontFamily = new FontFamily("Microsoft YaHei UI");
        _unit.TextAlignment = TextAlignment.Center;
        _unit.FontSize = 10;
        _unit.Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x5F, 0x66));
        _canvas.Children.Add(_unit);

        Content = _canvas;

        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((BatteryRing)d).Redraw();

    private void Redraw()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        const double stroke = 4.0;
        const double trackStroke = 4.0;
        var radius = (size - stroke) / 2;
        if (radius <= 0) return;
        var center = size / 2;

        _track.Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x33));
        _track.StrokeThickness = trackStroke;
        _track.Fill = null;
        _track.Data = BuildCircle(center, radius);

        Color color;
        string text;
        string unit;

        if (IsWired)
        {
            // 有线：安静地平铺一圈蓝色（用户要求的模块主题色），中心直接写「有线」。
            // 不显示百分比 —— 有线设备没有电量可显示，写个 100% 反而误导。
            color = ParseColor(OccupancyPalette.ColorHex(OccupancyLevel.Normal));
            text = "有线";
            unit = "";
        }
        else if (Percent is { } percent)
        {
            var clamped = (int)OccupancyPalette.Clamp(percent);
            // 电量与占用率语义相反：越低越危险。这里保持「越低越红」的直觉判色
            color = ParseColor(OccupancyPalette.ColorHex(
                clamped <= 20 ? OccupancyLevel.Critical
                : clamped <= 40 ? OccupancyLevel.High
                : OccupancyLevel.Low));
            text = clamped.ToString();
            unit = "%";
        }
        else
        {
            color = Color.FromRgb(0x3A, 0x3D, 0x44);
            text = "—";
            unit = "未知";
        }

        _arc.Stroke = new SolidColorBrush(color);
        _arc.StrokeThickness = stroke;
        _arc.StrokeStartLineCap = PenLineCap.Round;
        _arc.StrokeEndLineCap = PenLineCap.Round;
        _arc.Fill = null;
        _arc.Data = BuildArc(center, radius, IsWired || Percent is null ? 100 : Percent.Value);

        _value.Text = text;
        // 有线时中心是两个字，字号要收一档，否则会撑出圆心
        _value.FontSize = Math.Max(14, size * (IsWired ? 0.2 : 0.3));
        _value.Width = size;
        Canvas.SetLeft(_value, 0);
        Canvas.SetTop(_value, center - _value.FontSize * 0.72);

        _unit.Text = unit;
        _unit.Width = size;
        _unit.FontSize = Math.Max(8, size * 0.11);
        Canvas.SetLeft(_unit, 0);
        Canvas.SetTop(_unit, center + _value.FontSize * 0.35);

        // Caption 显示在环下方（控件高度比环大时才有空间）
        if (!string.IsNullOrEmpty(Caption))
        {
            if (_caption is null)
            {
                _caption = new TextBlock
                {
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    TextAlignment = TextAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x83, 0x86, 0x8D)),
                };
                _canvas.Children.Add(_caption);
            }

            _caption.Text = Caption;
            _caption.FontSize = 12;
            _caption.Width = Math.Max(size, 120);
            Canvas.SetLeft(_caption, (size - _caption.Width) / 2);
            Canvas.SetTop(_caption, center + radius + _unit.FontSize + 10);
        }
        else if (_caption is not null)
        {
            _canvas.Children.Remove(_caption);
            _caption = null;
        }
    }

    private TextBlock? _caption;

    private static Geometry BuildCircle(double center, double radius)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(center, center - radius), isFilled: false, isClosed: true);
            ctx.ArcTo(new Point(center, center + radius), new Size(radius, radius), 0, false,
                SweepDirection.Clockwise, true, true);
            ctx.ArcTo(new Point(center, center - radius), new Size(radius, radius), 0, false,
                SweepDirection.Clockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry BuildArc(double center, double radius, double percent)
    {
        var p = OccupancyPalette.Clamp(percent);
        if (p <= 0.5) p = 0.5;

        var sweep = GaugeGeometry.FullCircle * p / 100.0;
        if (p >= 99.9) sweep = GaugeGeometry.FullCircle - 0.001;

        const double startAngle = -90;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var start = GaugeGeometry.PointAt(center, center, radius, startAngle);
            var end = GaugeGeometry.PointAt(center, center, radius, startAngle + sweep);

            ctx.BeginFigure(new Point(start.X, start.Y), isFilled: false, isClosed: false);
            ctx.ArcTo(new Point(end.X, end.Y), new Size(radius, radius), 0,
                GaugeGeometry.IsLargeArc(sweep), SweepDirection.Clockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.Gray; }
    }
}
