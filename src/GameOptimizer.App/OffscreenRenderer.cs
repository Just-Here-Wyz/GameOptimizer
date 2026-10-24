namespace GameOptimizer.App;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// 离屏渲染工具（仅调试用）：把真实 XAML 页面排版后渲染成 PNG。
///
/// 存在的理由：本项目验收界面只能靠「看一眼」，而自动化环境下窗口可能不可见
/// （提权 / 会话隔离），屏幕截图会拍到空桌面。
///
/// 实现要点（踩过的坑）：
/// 对**未 Show 的 Window** 直接 <c>RenderTargetBitmap.Render(window)</c> 得到的是全白，
/// 因为窗口没有 PresentationSource，渲染管线不会真正绘制。
/// 正确做法是把窗口内容填进一个**已附加到可视化树**的 <see cref="Border"/>，
/// 再渲染那个 Border。
///
/// 用法：<c>GameOptimizer.App.dll --render &lt;输出目录&gt; [页面名,...]</c>
/// 页面名可选 home / peripherals / download / settings / cpu，缺省渲染全部。
/// </summary>
internal static class OffscreenRenderer
{
    private const int Width = 1180;
    private const int Height = 760;

    /// <summary>画布上限：内容再高也不再撑（再高就该滚动而不是截一张巨图）。</summary>
    private const int MaxHeight = 1400;

    /// <summary>内容比默认窗口高的页面，单独指定画布高度。</summary>
    private static readonly Dictionary<string, int> PageHeights = new()
    {
        ["cpu"] = 1250,
    };

    /// <summary>页面名 → 导航序号（cpu 详情页不走导航，由 OpenCpuDetailCommand 打开）。</summary>
    private static readonly (string Name, int NavIndex)[] Pages =
    [
        ("home", 0),
        ("peripherals", 1),
        ("download", 2),
        ("settings", 3),
        ("cpu", 0),
    ];

    public static string RenderAll(string outputDirectory, string? only = null)
    {
        Directory.CreateDirectory(outputDirectory);
        _tracePath = Path.Combine(outputDirectory, "_render.log");
        Trace("render all start, only=" + (only ?? "(all)"));

        var wanted = string.IsNullOrWhiteSpace(only)
            ? null
            : only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var written = new List<string>();

        // 每一页单独一个窗口实例：页面的初始状态（空状态 / 列表）与导航高亮才可信
        foreach (var (name, navIndex) in Pages)
        {
            if (wanted is not null && !wanted.Contains(name)) continue;

            var path = Path.Combine(outputDirectory, $"page-{name}.png");
            Render(name, navIndex, path);
            written.Add(path);
        }

        return string.Join(Environment.NewLine, written);
    }

    private static void Render(string name, int navIndex, string outputPath)
    {
        Trace("start " + name);

        var window = new GameOptimizer.App.Views.MainWindow
        {
            DataContext = CreateViewModel(),
        };
        Trace("vm created");

        if (window.DataContext is GameOptimizer.App.ViewModels.MainViewModel vm)
        {
            vm.NavigateTo(navIndex);

            // CPU 详情页是总览页上的浮层，靠命令打开，不走导航
            if (name == "cpu") vm.OpenCpuDetailCommand.Execute(null);
        }
        Trace("navigated");

        // 取出窗口内容，放到一个临时宿主里排版并渲染
        var content = (UIElement)window.Content;
        window.Content = null;

        // 画布高度：默认与窗口一致；个别页面内容比窗口高，单独给更高的画布，
        // 否则截图里会把后半截裁掉（CPU 详情页的逐核条形图就是这种）。
        //
        // 不能靠 content.DesiredSize 自动撑：页面主体在 ScrollViewer 里，
        // 它报的是视口高度而不是内容高度，永远是 717。
        var canvasHeight = PageHeights.TryGetValue(name, out var overrideHeight)
            ? Math.Clamp(overrideHeight, Height, MaxHeight)
            : Height;

        var host = new Border
        {
            Width = Width,
            Height = canvasHeight,
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0E)),
            Child = content,

            // 关键：内容从窗口上摘下来后就不再继承窗口的 DataContext，
            // 页面可见性等绑定会全部失效（结果是所有页叠在一起渲染）。
            DataContext = window.DataContext,
        };

        host.Measure(new Size(Width, canvasHeight));
        host.Arrange(new Rect(0, 0, Width, canvasHeight));
        host.UpdateLayout();

        // 让布局与绑定跑完（数据模板展开、每秒采集的首次刷新都在这一轮）
        PumpDispatcher();

        host.Measure(new Size(Width, canvasHeight));
        host.Arrange(new Rect(0, 0, Width, canvasHeight));
        host.UpdateLayout();
        PumpDispatcher();

        // 关键：把宿主挂到一个真正可见的窗口上，否则 RenderTargetBitmap 得到全白
        var shell = new Window
        {
            Width = Width,
            Height = canvasHeight,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = -20_000,
            Top = -20_000,
            Content = host,
            Background = Brushes.Black,
        };

        shell.Show();
        Trace("shown");
        PumpDispatcher();
        host.UpdateLayout();
        PumpDispatcher();
        Trace("pumped");

        var bitmap = new RenderTargetBitmap(Width, canvasHeight, 96, 96, PixelFormats.Pbgra32);

        // 先铺一层窗口底色：内容没填满画布时剩下的区域是透明的，
        // 直接存 PNG 会在查看器里显示成白条。
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
        {
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0E)), null, new Rect(0, 0, Width, canvasHeight));
        }

        bitmap.Render(backdrop);
        bitmap.Render(host);
        Trace("rendered");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (var stream = File.Create(outputPath)) encoder.Save(stream);
        Trace("saved " + Path.GetFileName(outputPath));

        shell.Close();
    }

    /// <summary>
    /// 把 UI 队列跑一段有限时间，让绑定、布局、渲染都处理一遍。
    ///
    /// 刻意不用 <c>Dispatcher.Invoke(..., ContextIdle)</c> 那种「等队列跑空」的写法：
    /// 只要有页面挂着一个持续自激的 DispatcherTimer，队列永远回不到 ContextIdle，
    /// 这里就会无限等下去（曾经因此让 --render 直接卡死）。改成推一个有上限的帧循环。
    /// </summary>
    private static void PumpDispatcher(int milliseconds = 400)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };

        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>渲染过程没有控制台（WinExe 的 stdout 是空的），进度只能落文件。</summary>
    private static string _tracePath = "";

    private static void Trace(string message)
    {
        if (_tracePath.Length == 0) return;

        try
        {
            File.AppendAllText(
                _tracePath,
                $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志写不进去不影响主流程
        }
    }

    private static object CreateViewModel()
    {
        Trace("sampler new");
        var sampler = new GameOptimizer.Core.Collection.PdhSampler();
        Trace("sampler init");
        var ready = sampler.Initialize();
        Trace("sampler ready=" + ready);
        if (ready) sampler.Warmup();
        Trace("sampler warmed");

        return new GameOptimizer.App.ViewModels.MainViewModel(
            sampler,
            ready,
            new GameOptimizer.Core.GameMode.GameModeController(
                new GameOptimizer.Core.GameMode.GameModeStateStore(
                    Path.Combine(Path.GetTempPath(), "go-render-state.json"))),

            // 每次渲染都用全新的空任务表：截图不该带上某个人的真实下载记录
            new GameOptimizer.Core.Download.DownloadStore(
                Path.Combine(Path.GetTempPath(), "go-render-downloads.json")));
    }
}
