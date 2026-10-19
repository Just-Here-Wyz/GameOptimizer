namespace GameOptimizer.App.Views;

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GameOptimizer.Core.Analysis;

/// <summary>
/// 极简单折线图。不引入第三方图表库：曲线只是等间隔时间序列，
/// 用 Polyline 画即可，省掉几 MB 依赖和一套渲染开销。
/// 坐标计算委托给 <see cref="ChartGeometry"/>，那部分有单元测试覆盖。
/// </summary>
public sealed class Sparkline : UserControl
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double>), typeof(Sparkline),
            new PropertyMetadata(null, OnValuesChanged));

    public static readonly DependencyProperty LineBrushProperty =
        DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(Sparkline),
            new PropertyMetadata(Brushes.DodgerBlue, OnVisualChanged));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    private readonly Polyline _line;
    private readonly Canvas _canvas;

    public Sparkline()
    {
        _line = new Polyline { StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
        _canvas = new Canvas { Background = Brushes.Transparent };
        _canvas.Children.Add(_line);
        Content = _canvas;

        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged oldCol)
            oldCol.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCol)
            newCol.CollectionChanged += chart.OnCollectionChanged;
        chart.Redraw();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Sparkline)d).Redraw();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        // Stroke 必须设在 Polyline 自己身上。之前误用了本控件注册的 DependencyProperty，
        // WPF 会静默忽略，导致线条颜色为空、图表一片空白。
        _line.Stroke = LineBrush;

        var data = Values?.ToArray() ?? [];
        var geometry = ChartGeometry.Build(data);
        var scaled = ChartGeometry.Scale(geometry, ActualWidth, ActualHeight);

        if (scaled.Count == 0)
        {
            _line.Points = [];
            return;
        }

        var pts = new PointCollection(scaled.Count);
        foreach (var (x, y) in scaled) pts.Add(new Point(x, y));
        _line.Points = pts;
    }
}
