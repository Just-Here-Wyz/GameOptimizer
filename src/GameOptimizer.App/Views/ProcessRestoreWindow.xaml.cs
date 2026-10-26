namespace GameOptimizer.App.Views;

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using GameOptimizer.App.ViewModels;

/// <summary>
/// 「游戏模式已关闭 — 选择需要恢复的后台进程」弹窗。
///
/// 三条生命周期约束：
/// <list type="bullet">
/// <item><b>关闭 ≠ 恢复</b>：窗口不发起任何恢复动作，点「恢复所选」才会走 Core 的恢复队列。
///       所以「取消」天然不需要回滚，它什么都没做；</item>
/// <item><b>恢复中不许关窗</b>：否则进度与结果无处显示，用户会以为已经取消；</item>
/// <item>数据全绑在 <see cref="ProcessRestoreViewModel"/> 上，窗口自己不缓存状态。</item>
/// </list>
/// </summary>
public partial class ProcessRestoreWindow : Window
{
    private ProcessRestoreViewModel? _subscribed;

    public ProcessRestoreWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Attach();
        Closing += OnClosing;

        // Esc = 取消；恢复执行中忽略，避免误触把进行中的窗口关掉
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _subscribed?.IsRunning != true) Close();
        };
    }

    private void Attach()
    {
        if (_subscribed is not null) _subscribed.CloseRequested -= OnCloseRequested;

        _subscribed = DataContext as ProcessRestoreViewModel;
        if (_subscribed is not null) _subscribed.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_subscribed?.IsRunning == true)
        {
            e.Cancel = true;
            return;
        }

        if (_subscribed is not null)
        {
            _subscribed.CloseRequested -= OnCloseRequested;
            _subscribed = null;
        }
    }

    /// <summary>无系统标题栏，整条标题栏负责拖动。</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
