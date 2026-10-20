namespace GameOptimizer.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GameOptimizer.Core.Analysis;

/// <summary>
/// 游戏内悬浮窗：置顶小窗，显示显存 / 内存 / GPU / CPU 占用。
///
/// **不注入游戏进程、不读游戏进程信息**——所有数字来自系统级性能计数器。
/// 这是用户在「保证账号安全」前提下唯一可接受的实现方式。
///
/// **只对窗口化 / 无边框窗口化游戏可见**：普通置顶窗口会被独占全屏画面盖住。
/// 要覆盖独占全屏必须注入或 Hook D3D 交换链，那正是反作弊的判定目标。
///
/// 刻意做成"无边框 + 半透明 + 可拖动 + 记住位置"，避免遮挡游戏 HUD。
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly StackPanel _rows = new();
    private readonly TextBlock _hint = new();

    /// <summary>拖动停止后延迟落盘，避免拖动过程中每帧都写文件。</summary>
    private readonly DispatcherTimer _pendingSave = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // 不抢焦点：否则点游戏会变成点悬浮窗
        Focusable = false;
        ShowActivated = false;

        // 允许透明窗口做像素级命中（只有真的画了东西的地方才吃点击）
        SnapsToDevicePixels = true;

        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x14, 0x15, 0x19)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2C, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.5,
            },
        };

        var stack = new StackPanel();

        var title = new TextBlock
        {
            Text = "实时监控",
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x5F, 0x66)),
            Margin = new Thickness(0, 0, 0, 4),
        };
        stack.Children.Add(title);
        stack.Children.Add(_rows);

        _hint.FontFamily = new FontFamily("Microsoft YaHei UI");
        _hint.FontSize = 10;
        _hint.Margin = new Thickness(0, 4, 0, 0);
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.MaxWidth = 180;
        _hint.Visibility = Visibility.Collapsed;
        stack.Children.Add(_hint);

        panel.Child = stack;
        Content = panel;

        // 拖动：按住任意位置移动。
        //
        // 位置保存**不能**放在 MouseLeftButtonUp 里：DragMove() 内部会起一个模态的
        // 拖动：按住任意位置移动。
        //
        // 位置保存**不能**放在 MouseLeftButtonUp 里：DragMove() 内部会起一个模态的
        // 窗口移动循环，松开鼠标时那个事件根本不会派发到这里（实测拖完位置没保存）。
        // 监听 LocationChanged 更可靠。
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { /* 非左键按下时 DragMove 会抛，忽略 */ }
        };

        LocationChanged += (_, _) =>
        {
            _pendingSave.Stop();
            _pendingSave.Start();
        };

        _pendingSave.Tick += (_, _) =>
        {
            _pendingSave.Stop();
            SavePosition();
        };

        Closing += (_, _) =>
        {
            _pendingSave.Stop();
            SavePosition();
        };
    }

    /// <summary>把 Core 算好的内容渲染到窗口上。</summary>
    public void Update(OverlayContent content)
    {
        if (content.HasWarning)
        {
            _hint.Text = content.Hint;
            _hint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            _hint.Visibility = Visibility.Visible;
        }
        else
        {
            _hint.Visibility = Visibility.Collapsed;
        }

        // 指标行数量会变（磁盘吞吐可能不可用），不足时补齐、多余时移除
        while (_rows.Children.Count > content.Metrics.Count)
            _rows.Children.RemoveAt(_rows.Children.Count - 1);

        while (_rows.Children.Count < content.Metrics.Count)
            _rows.Children.Add(BuildRow());

        for (var i = 0; i < content.Metrics.Count; i++)
            Apply((StackPanel)_rows.Children[i], content.Metrics[i]);
    }

    private static StackPanel BuildRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

        var label = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x83, 0x86, 0x8D)),
            Width = 32,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var value = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(label);
        row.Children.Add(value);
        return row;
    }

    private static void Apply(StackPanel row, OverlayMetric metric)
    {
        var label = (TextBlock)row.Children[0];
        var value = (TextBlock)row.Children[1];

        label.Text = metric.Label;
        value.Text = metric.Value;

        // 颜色来自 Core 的 OccupancyPalette，与主界面同一套口径
        try { value.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(metric.ColorHex)); }
        catch { value.Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xED, 0xEF)); }
    }

    /// <summary>把窗口放到工作区右上角，避开大多数游戏的 HUD 与血量条。</summary>
    public void PlaceDefault()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Top + 24;
    }

    /// <summary>载入上次的位置；没有记录时用默认位置。</summary>
    public void RestorePosition(double? left, double? top)
    {
        if (left is null || top is null) { PlaceDefault(); return; }

        // 屏幕分辨率变化后旧坐标可能落在可见区域外，此时退回默认位置
        var area = SystemParameters.WorkArea;
        if (left < area.Left - 50 || left > area.Right || top < area.Top - 50 || top > area.Bottom)
        {
            PlaceDefault();
            return;
        }

        Left = left.Value;
        Top = top.Value;
    }

    /// <summary>当前位置，供调用方持久化。</summary>
    public (double Left, double Top) Position => (Left, Top);

    /// <summary>位置保存回调。由 App 注入，避免这个窗口自己碰文件系统。</summary>
    public Action<double, double>? PositionChanged { get; set; }

    private void SavePosition() => PositionChanged?.Invoke(Left, Top);
}
