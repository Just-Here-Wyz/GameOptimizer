namespace GameOptimizer.App;

using System.ComponentModel;
using System.Drawing;
using System.Windows;
using GameOptimizer.App.ViewModels;
using GameOptimizer.App.Views;
using GameOptimizer.Core.Collection;
using GameOptimizer.Core.GameMode;
using GameOptimizer.Core.Library;

public partial class App : Application
{
    private PdhSampler? _sampler;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private NotifyIcon? _tray;
    private bool _reallyExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 托盘运行：关窗口 = 隐藏到托盘，只有托盘菜单「退出」才真正结束。
        // 因此不能用 OnMainWindowClose——主窗口一关进程就没了。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _sampler = new PdhSampler();
        var ready = _sampler.Initialize();
        if (ready) _sampler.Warmup();

        var mode = BuildGameMode();
        _vm = new MainViewModel(_sampler, ready, mode);
        _window = new MainWindow { DataContext = _vm };
        MainWindow = _window;

        _window.Closing += OnWindowClosing;
        _window.Show();

        CreateTrayIcon();
    }

    /// <summary>
    /// 系统托盘图标 + 菜单：显示主界面 / 退出。
    /// 图标用运行时自绘：exe 没有嵌入的 .ico 资源，用画笔画一个「优」字方块，
    /// 托盘上是清晰的 16px 深色图标。
    /// </summary>
    private void CreateTrayIcon()
    {
        _tray = new NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Text = "游戏优化助手",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    /// <summary>恢复并激活主窗口；窗口被用户销毁时（异常路径）重新建一个。</summary>
    private void ShowMainWindow()
    {
        if (_window is null || !_window.IsLoaded)
        {
            _window = new MainWindow { DataContext = _vm };
            MainWindow = _window;
            _window.Closing += OnWindowClosing;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>关窗口不退出：隐藏到托盘。只有 _reallyExit 时才放行真正关闭。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyExit) return;

        // 关窗口 = 收进托盘（最初要求：托盘常驻，后台运行）
        e.Cancel = true;
        _window?.Hide();
    }

    /// <summary>托盘「退出」：还原游戏模式后真正结束进程。</summary>
    private void ExitApplication()
    {
        _reallyExit = true;
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出前务必还原游戏模式，避免把用户的电源计划永久改成高性能
        _vm?.Shutdown();
        _sampler?.Dispose();
        _tray?.Dispose();
        _tray = null;
        base.OnExit(e);
    }

    /// <summary>运行时自绘托盘图标：深色圆角方块 + 白色「优」字。</summary>
    private static Icon BuildTrayIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 深色圆角方块（与窗口标题栏同色系）
            using var bg = new SolidBrush(Color.FromArgb(0xFF, 0x2A, 0x2C, 0x33));
            g.FillRectangle(bg, 3, 3, 26, 26);

            // 白色「优」字
            using var font = new System.Drawing.Font("Microsoft YaHei UI", 14,
                System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.FromArgb(0xFF, 0xEC, 0xED, 0xEF));
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("优", font, text, new RectangleF(0, 1, 32, 32), format);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            // Clone 出来再销毁句柄，否则 Dispose 图标时会把 hIcon 一起释放
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 组装游戏模式：只关闭不必要的后台进程。
    ///
    /// 刻意不包含「显卡性能偏好」——核显机器上没有可调度的独立 GPU，
    /// 写那个注册表键不会有任何效果，属于看着优化了实则空转的项。
    /// 白名单与力度由用户可改（见 BackgroundWhitelistSettings）。
    /// </summary>
    private static GameModeController BuildGameMode()
    {
        var mode = new GameModeController();
        mode.Add(new PowerPlanAction());
        mode.Add(new EndProcessAction(BackgroundProcessWhitelist.Default));
        return mode;
    }
}
