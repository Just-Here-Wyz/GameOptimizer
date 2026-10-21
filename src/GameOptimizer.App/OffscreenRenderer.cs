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
/// 用法：<c>GameOptimizer.App.dll --render &lt;输出目录&gt;</c>
/// </summary>
internal static class OffscreenRenderer
{
    private const int Width = 1180;
    private const int Height = 760;

    public static string RenderAll(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var written = new List<string>();

        // 每一页单独一个窗口实例：页面的初始状态（空状态 / 列表）与导航高亮才可信
        foreach (var (name, navIndex) in new[]
                 {
                     ("download", 2),
                     ("home", 0),
                     ("peripherals", 1),
                     ("settings", 3),
                 })
        {
            var path = Path.Combine(outputDirectory, $"page-{name}.png");
            Render(navIndex, path);
            written.Add(path);
        }

        return string.Join(Environment.NewLine, written);
    }

    private static void Render(int navIndex, string outputPath)
    {
        var window = new GameOptimizer.App.Views.MainWindow
        {
            DataContext = CreateViewModel(),
        };

        if (window.DataContext is GameOptimizer.App.ViewModels.MainViewModel vm)
            vm.NavigateTo(navIndex);

        // 取出窗口内容，放到一个临时宿主里排版并渲染
        var content = (UIElement)window.Content;
        window.Content = null;

        var host = new Border
        {
            Width = Width,
            Height = Height,
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0E)),
            Child = content,
        };

        host.Measure(new Size(Width, Height));
        host.Arrange(new Rect(0, 0, Width, Height));
        host.UpdateLayout();

        // 让布局与绑定跑完（数据模板展开、每秒采集的首次刷新都在这一轮）
        PumpDispatcher();

        host.Measure(new Size(Width, Height));
        host.Arrange(new Rect(0, 0, Width, Height));
        host.UpdateLayout();
        PumpDispatcher();

        // 关键：把宿主挂到一个真正可见的窗口上，否则 RenderTargetBitmap 得到全白
        var shell = new Window
        {
            Width = Width,
            Height = Height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = -20_000,
            Top = -20_000,
            Content = host,
            Background = Brushes.Black,
        };

        shell.Show();
        PumpDispatcher();
        host.UpdateLayout();
        PumpDispatcher();

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (var stream = File.Create(outputPath)) encoder.Save(stream);

        shell.Close();
    }

    /// <summary>把 UI 队列跑空：绑定、布局、渲染优先级都处理一遍。</summary>
    private static void PumpDispatcher()
    {
        for (var i = 0; i < 6; i++)
        {
            Application.Current?.Dispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    private static object CreateViewModel()
    {
        var sampler = new GameOptimizer.Core.Collection.PdhSampler();
        var ready = sampler.Initialize();
        if (ready) sampler.Warmup();

        return new GameOptimizer.App.ViewModels.MainViewModel(
            sampler,
            ready,
            new GameOptimizer.Core.GameMode.GameModeController(
                new GameOptimizer.Core.GameMode.GameModeStateStore(
                    Path.Combine(Path.GetTempPath(), "go-render-state.json"))));
    }
}
