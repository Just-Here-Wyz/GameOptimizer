namespace GameOptimizer.App;

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
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

    /// <summary>
    /// 「点 × 之后怎么处理」的判定与持久化。正常启动路径上一定会被创建；
    /// 只有离屏渲染（--render）那种不建窗口的路径才没有它。
    /// </summary>
    private CloseBehaviorService? _closeBehavior;

    /// <summary>单实例锁。必须常驻字段：被 GC 回收等于主动放开锁，第二个实例就能挤进来。</summary>
    private SingleInstanceGate? _gate;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 离屏渲染模式（仅调试用，见 OffscreenRenderer）：把真实页面渲染成 PNG 后立刻退出。
        // 不建窗口、不建托盘、不碰游戏模式，因此不会在用户机器上留下任何痕迹。
        if (e.Args.Length >= 2 && e.Args[0] == "--render")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var output = OffscreenRenderer.RenderAll(
                    e.Args[1],
                    e.Args.Length >= 3 ? e.Args[2] : null);
                Console.WriteLine(output);
            }
            catch (Exception ex)
            {
                Console.WriteLine("RENDER FAILED: " + ex);
            }

            Shutdown();
            return;
        }

        // 托盘运行：关窗口的行为由用户选择（缩到托盘 / 退出 / 每次询问）。
        // 三种结局都不会走 OnMainWindowClose——即便选了「退出程序」，
        // 也是先 Cancel 掉这次系统关闭、再由 ExitApplication 走正常退出流程，
        // 否则 OnExit 里的游戏模式还原可能被打断。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 提权保险：清单已声明 requireAdministrator，双击本应直接弹 UAC。
        // 但计划任务、启动器、从已提权宿主派生的子进程都会绕过内嵌清单，
        // 届时界面照开、游戏模式却全被拒绝访问（假成功）。这里主动重启自身补上。
        //
        // 顺序很关键：必须先提权重启、再抢单实例锁。
        // 反过来的话，当前进程握着锁去拉起新进程，新进程会把自己当成「第二个实例」而退出。
        if (TrySelfElevate(e.Args))
        {
            // 新进程已拉起：本进程立即退出，避免两个实例同时改电源计划
            Shutdown();
            return;
        }

        // 单实例：会改电源计划、结束进程，两个实例同时跑会各写各的状态，
        // 第二个实例还以为「游戏模式未开启」，重复执行一遍开启流程。
        _gate = SingleInstanceGate.Acquire();
        if (!_gate.IsFirstInstance)
        {
            // 已在运行：把它的窗口从托盘里叫出来，本次启动直接退掉
            SingleInstanceGate.RequestActivation();
            Shutdown();
            return;
        }

        var startupRecovery = RunStartupRecovery();

        // 关闭行为：启动时读一次盘。读不出来就是「每次询问」，原因留给设置页显示，
        // 绝不因为一个坏掉的配置让程序起不来，也绝不替用户猜「退出」还是「缩到托盘」。
        _closeBehavior = new CloseBehaviorService();

        _sampler = new PdhSampler();
        var ready = _sampler.Initialize();
        if (ready) _sampler.Warmup();

        var mode = BuildGameMode();

        // 设置页与关闭流程共用同一个 service：各建一个实例的话，
        // 设置页改完只是写进了磁盘，关闭流程手里的还是旧值，要重启才生效。
        _vm = new MainViewModel(
            _sampler, ready, mode,
            closeBehaviorService: _closeBehavior,
            startupRecovery: startupRecovery.Plan,
            startupPowerPlanErrors: startupRecovery.PowerPlanErrors);
        _window = new MainWindow { DataContext = _vm };
        MainWindow = _window;

        _window.Closing += OnWindowClosing;
        _window.Show();

        CreateTrayIcon();
    }

    /// <summary>
    /// 未提权时带 UAC 重启自身。返回 true 表示「新进程已拉起，本进程应立刻退出」。
    ///
    /// 用户取消 UAC 不算错误：程序继续以普通权限运行，由界面如实标注受限项，
    /// 比直接退出更符合预期（用户至少能看到发生了什么）。
    /// </summary>
    private static bool TrySelfElevate(string[] args)
    {
        if (!ElevationLauncher.ShouldRelaunch(ElevationStatus.IsCurrentProcessElevated(), args))
            return false;

        // 单文件发布时 ProcessPath 就是自身 exe；拿不到或宿主是 dotnet.exe 时直接降级运行
        var exe = Environment.ProcessPath;
        if (!ElevationLauncher.CanSelfRelaunch(exe)) return false;

        var psi = new ProcessStartInfo(exe!) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in ElevationLauncher.BuildRelaunchArguments(args)) psi.ArgumentList.Add(a);

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ElevationLauncher.IsUserCancelled(ex.NativeErrorCode))
        {
            MessageBox.Show(
                "已取消管理员授权，程序将以普通权限继续运行。\n\n" +
                "游戏模式会关闭进程、切换电源计划，这些操作在普通权限下会失败，届时界面会如实提示。",
                "游戏优化助手 — 未提权",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"以管理员身份启动失败：{ex.Message}\n\n程序将以普通权限继续运行，游戏模式可能无法生效。",
                "游戏优化助手 — 提权失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>
    /// 启动时处理上一次留下的游戏模式状态。<b>这条路径永远不弹窗。</b>
    ///
    /// v2.4.1 ~ v2.5.1 这里会用「游戏优化助手 — 恢复」问「上次关闭游戏模式后还有 N 个后台进程
    /// 没有恢复，是否现在恢复？」。选「否」就保留快照、下次启动再问——于是每一次开机
    /// 都要重新回答一个用户在上次关闭游戏模式时已经做过的决定。
    ///
    /// 现在只做两件必须做的事，两件事都不需要用户点头：
    /// <list type="number">
    /// <item>崩溃 / 强杀留下的<b>电源计划</b>必须还原——那是程序留在系统上的改动，
    /// 不是用户的待办，留着不问等于让系统永远停在高性能；</item>
    /// <item>未恢复的<b>进程记录</b>原样保留，交给首页「恢复遗留进程」手动处理：
    /// 不自动拉起（不替用户决定），也不清空（不丢掉有效记录）。</item>
    /// </list>
    ///
    /// 读不出快照、还原失败这类异常同样不弹窗，写进恢复日志并交给首页状态行说明——
    /// 但绝不静默吞掉（硬约定 1：失败必须可见）。
    /// </summary>
    /// <returns>
    /// 启动方案 + 电源计划还原的失败项。<b>文案不在这里拼</b>：原因只有一个——
    /// 首页还要先按资格判定筛掉「其实早就回来了」的记录，数字才对得上，
    /// 所以那句话由 ViewModel 用同一份 <see cref="StartupRecoveryPlanner.Describe"/> 生成。
    /// </returns>
    private (StartupRecoveryPlan Plan, IReadOnlyList<string> PowerPlanErrors) RunStartupRecovery()
    {
        GameModeStateStore store;
        GameModeSnapshot? snapshot;
        try
        {
            store = new GameModeStateStore();
            snapshot = store.Load();
        }
        catch (Exception ex)
        {
            // 兜底信息不可信：留着文件不删（不擅自清空用户数据），只把这件事说出来
            ProcessRestoreLog.Write($"[启动] 残留状态读取失败 · {ex.GetType().Name} · {ex.Message}");
            return (StartupRecoveryPlanner.Unreadable(ex.Message), []);
        }

        var plan = StartupRecoveryPlanner.Plan(snapshot);

        // 只剩进程记录 = 用户上次自己选的「这次不恢复」，不是异常退出：
        // 一句话都不问，记录留着，等他哪天想起来点首页那个按钮
        if (!plan.ShouldRestorePowerPlan) return (plan, []);

        // 电源计划走与 PowerPlanAction 相同的 powercfg 通道（含回读验证）。
        // 只传 GUID、不带进程：这一步只还原系统状态，一个进程都不拉起。
        IReadOnlyList<string> errors;
        try
        {
            errors = new GameModeRecovery().Restore(
                new GameModeSnapshot { PreviousPowerPlanGuid = plan.PowerPlanGuid },
                PowerPlanAction.RestorePlan);
        }
        catch (Exception ex)
        {
            errors = [$"原电源计划还原失败：{ex.Message}"];
        }

        if (errors.Count > 0)
        {
            // 还原没做成：快照原样保留（GUID 还在，下次启动会再试一次），只记录不打扰
            foreach (var error in errors) ProcessRestoreLog.Write("[启动] " + error);
            return (plan, errors);
        }

        // 还原成功：快照退化成「只剩进程」，没有剩余就整个清掉
        var saveError = store.SavePendingProcesses(plan.PendingProcesses);
        if (saveError is not null) ProcessRestoreLog.Write("[启动] " + saveError);

        return (plan, errors);
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

        // 从托盘回来：恢复每秒采集（关闭时被 SetSamplingActive(false) 停掉了）
        _vm?.SetSamplingActive(true);
    }

    /// <summary>
    /// 点主窗口 × 时的分流：询问 / 收进托盘 / 退出。
    ///
    /// <b>这里先 Cancel，再决定结局</b>：无论用户怎么选，都不能让窗口在
    /// 「还没确认」的时候自己消失。真正的关闭只有一条路——<see cref="ExitApplication"/>，
    /// 它先把 <see cref="_reallyExit"/> 置位，下一次 Closing 才会被放行。
    ///
    /// 决策本身不在这里：已保存行为 → 该做什么由 Core 的
    /// <see cref="CloseBehaviorService.RequestClose"/> 判定，重入守卫也在那边
    /// （弹窗开着时重复点 × 返回 Ignore，不会叠出第二个窗口、也不会提前隐藏）。
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // 托盘「退出」/ 已确认退出：放行真正的关闭，绝不再问一遍
        if (_reallyExit) return;

        var route = _closeBehavior?.RequestClose() ?? CloseRoute.HideToTray;

        e.Cancel = true;

        switch (route)
        {
            case CloseRoute.HideToTray:
                HideToTray();
                break;

            case CloseRoute.Exit:
                RequestExit();
                break;

            case CloseRoute.Ask:
                // 弹窗必须等 Closing 这一轮跑完再开：
                // 在 Closing 处理函数里同步 ShowDialog，等于让一个「正在关闭」的窗口当 Owner。
                Dispatcher.BeginInvoke(new Action(ShowCloseBehaviorPrompt), DispatcherPriority.Normal);
                break;

            // CloseRoute.Ignore：弹窗已经在等结果，这次点击什么都不做
            // CloseRoute.None：用户取消过，主窗口保持原样
        }
    }

    /// <summary>
    /// 收进托盘：隐藏主窗口、停掉每秒采集，后台功能（托盘图标、定时器外的逻辑）继续跑。
    /// 与 v2.3.0 的行为完全一致，只是不再无条件执行——要用户选过或保存过才会走到这里。
    /// </summary>
    private void HideToTray()
    {
        // 托盘常驻可能一整天不再打开，没人看的读数纯属白烧 CPU
        _vm?.SetSamplingActive(false);

        // 浮窗跟着一起隐藏：否则主界面收进托盘后，屏幕角落会留一个「没有主人」的窗口
        _window?.HideBackgroundWindow();
        _window?.Hide();
    }

    /// <summary>
    /// 弹窗询问关闭行为，并按结果执行。
    ///
    /// 三条失败路径都必须「什么都不做」：
    /// 用户取消、直接关窗、弹窗根本打不开——主窗口保持显示，程序继续跑。
    /// </summary>
    private void ShowCloseBehaviorPrompt()
    {
        CloseBehaviorChoice? choice;

        try
        {
            var viewModel = new CloseBehaviorPromptViewModel();
            var dialog = new CloseBehaviorWindow { DataContext = viewModel };

            // Owner 只用于居中与遮挡：必要时没有主窗口也要能开出来
            if (_window is not null) dialog.Owner = _window;

            dialog.ShowDialog();

            // 关窗 / Esc / 取消都让 Result 保持 null
            choice = viewModel.Result;
        }
        catch (Exception ex)
        {
            // 弹窗起不来不能悄悄当成「取消」之外的任何结果，更不能顺手把窗口藏掉
            _closeBehavior?.AbandonPrompt();
            MessageBox.Show(
                $"关闭行为选择窗口无法打开：{ex.Message}\n\n主窗口保持显示，本次关闭已取消。",
                "游戏优化助手 — 关闭",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var route = _closeBehavior?.CompletePrompt(choice) ?? CloseRoute.None;

        switch (route)
        {
            case CloseRoute.HideToTray:
                HideToTray();
                break;

            case CloseRoute.Exit:
                ExitApplication();
                break;
        }
    }

    /// <summary>
    /// 按保存的行为「退出程序」时调用。
    /// 先 Cancel 掉当前这次关闭，再把真正的退出推到下一轮消息循环：
    /// 直接在 Closing 的调用栈里 Shutdown，会在「正在关窗」的栈上再关同一个窗口。
    /// </summary>
    private void RequestExit()
    {
        if (_reallyExit) return;
        Dispatcher.BeginInvoke(new Action(ExitApplication), DispatcherPriority.Normal);
    }

    /// <summary>托盘「退出」与「关闭行为=退出」共用的唯一退出路径：还原游戏模式后真正结束进程。</summary>
    private void ExitApplication()
    {
        if (_reallyExit) return;

        _reallyExit = true;

        // 记下「已确认退出」：之后任何关闭请求都直接放行，不会再弹询问
        _closeBehavior?.ConfirmExit();

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
        _gate?.Dispose();
        _gate = null;
        base.OnExit(e);
    }

    /// <summary>
    /// 托盘图标：用项目 logo（app.ico 里最接近 16px 的一帧）。
    /// 资源读不到时退回自绘的「优」字方块——图标缺失不该让程序起不来。
    /// </summary>
    private static Icon BuildTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var loaded = new Icon(stream, new System.Drawing.Size(16, 16));

                // 必须 Clone：原对象与 stream 绑定，stream 释放后就不可再用了
                return (Icon)loaded.Clone();
            }
        }
        catch
        {
            // 落到下面的自绘兜底
        }

        return DrawFallbackTrayIcon();
    }

    /// <summary>兜底托盘图标：深色方块 + 白色「优」字（logo 资源不可用时才会用到）。</summary>
    private static Icon DrawFallbackTrayIcon()
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
    /// 组装游戏模式：关闭用户标记为结束目标的后台进程 + 释放闲置内存。
    ///
    /// 刻意不包含「显卡性能偏好」——核显机器上没有可调度的独立 GPU，
    /// 写那个注册表键不会有任何效果，属于看着优化了实则空转的项。
    ///
    /// 结束进程的初始名单是<b>空</b>：v2.4 起「勾选」表示保护，没有勾选不等于要结束，
    /// 真正要结束哪些由用户显式标记，进入游戏模式前从 <see cref="MainViewModel"/> 同步过来。
    /// 用默认名单打底会重演 v0.5.1 的事故（一点开启就关掉用户正在用的软件）。
    ///
    /// 内存清理用 AddOptional 注册：受限环境下可能整轮被拒（全部 AccessDenied），
    /// 属于锦上添花的一项，不该让用户连电源计划都开不起来；失败进 Warnings 上报界面。
    /// </summary>
    private static GameModeController BuildGameMode()
    {
        var mode = new GameModeController(new GameModeStateStore());
        mode.Add(new PowerPlanAction());
        mode.Add(new EndProcessAction([]));
        mode.AddOptional(new MemoryCleanAction());
        return mode;
    }
}
