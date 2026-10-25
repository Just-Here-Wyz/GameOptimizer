namespace GameOptimizer.App;

using System.ComponentModel;
using System.Diagnostics;
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

        // 托盘运行：关窗口 = 隐藏到托盘，只有托盘菜单「退出」才真正结束。
        // 因此不能用 OnMainWindowClose——主窗口一关进程就没了。
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

        RecoverFromAbnormalExit();

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
    /// 崩溃 / 任务管理器强杀后的兜底：检测残留快照并询问是否还原。
    /// 选「否」保留快照，下次启动再问，直到用户处理掉。
    /// </summary>
    private void RecoverFromAbnormalExit()
    {
        GameModeStateStore store;
        GameModeSnapshot? snapshot;
        try
        {
            store = new GameModeStateStore();
            snapshot = store.Load();
            if (snapshot is null) return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"上次运行残留的游戏模式状态无法读取，请手动确认电源计划：\n{ex.Message}",
                "游戏优化助手 — 残留状态",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var planMention = string.IsNullOrWhiteSpace(snapshot.PreviousPowerPlanGuid)
            ? ""
            : "电源计划";
        var count = snapshot.Killed?.Count(k => !string.IsNullOrWhiteSpace(k.LaunchPath)) ?? 0;
        var items = new List<string>();
        if (planMention.Length > 0) items.Add(planMention);
        if (count > 0) items.Add($"{count} 个进程");
        var what = items.Count > 0 ? string.Join("和", items) : "上次的改动";

        var answer = MessageBox.Show(
            $"上次程序未正常退出，检测到 {what} 未还原，是否现在还原？",
            "游戏优化助手 — 恢复",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return; // 保留快照，下次启动再问

        // 还原：电源计划走与 PowerPlanAction 相同的 powercfg 通道（含回读验证），
        // 进程用 GameModeRecovery 按原路径拉起，失败项必须逐条展示。
        var errors = new GameModeRecovery().Restore(
            snapshot,
            PowerPlanAction.RestorePlan);

        if (errors.Count > 0)
        {
            // 还原没做完：保留快照，下次启动继续问
            MessageBox.Show(
                "还原未完成，请手动确认：\n" + string.Join("\n", errors),
                "游戏优化助手 — 还原未完成",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        store.Clear();
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

    /// <summary>关窗口不退出：隐藏到托盘。只有 _reallyExit 时才放行真正关闭。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyExit) return;

        // 关窗口 = 收进托盘（最初要求：托盘常驻，后台运行）
        // 同时停掉每秒采集：托盘常驻可能一整天不再打开，没人看的读数纯属白烧 CPU
        _vm?.SetSamplingActive(false);
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
        _gate?.Dispose();
        _gate = null;
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
    /// 组装游戏模式：关闭不必要的后台进程 + 释放闲置内存。
    ///
    /// 刻意不包含「显卡性能偏好」——核显机器上没有可调度的独立 GPU，
    /// 写那个注册表键不会有任何效果，属于看着优化了实则空转的项。
    /// 白名单与力度由用户可改（见 BackgroundWhitelistSettings）。
    ///
    /// 内存清理用 AddOptional 注册：受限环境下可能整轮被拒（全部 AccessDenied），
    /// 属于锦上添花的一项，不该让用户连电源计划都开不起来；失败进 Warnings 上报界面。
    /// </summary>
    private static GameModeController BuildGameMode()
    {
        var mode = new GameModeController(new GameModeStateStore());
        mode.Add(new PowerPlanAction());
        mode.Add(new EndProcessAction(BackgroundProcessWhitelist.Default));
        mode.AddOptional(new MemoryCleanAction());
        return mode;
    }
}
