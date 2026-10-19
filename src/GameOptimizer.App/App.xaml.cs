namespace GameOptimizer.App;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 关窗口即退出；托盘最小化由窗口自己处理
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        _sampler = new PdhSampler();
        var ready = _sampler.Initialize();
        if (ready) _sampler.Warmup();

        var mode = BuildGameMode();
        _vm = new MainViewModel(_sampler, ready, mode);
        var window = new MainWindow { DataContext = _vm };
        MainWindow = window;
        window.Show();
    }

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

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出前务必还原游戏模式，避免把用户的电源计划永久改成高性能
        _vm?.Shutdown();
        _sampler?.Dispose();
        base.OnExit(e);
    }
}
