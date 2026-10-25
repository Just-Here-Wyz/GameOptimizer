namespace GameOptimizer.App.Views;

using System.Windows;
using System.Windows.Input;
using GameOptimizer.App.ViewModels;

/// <summary>
/// 后台进程保护浮窗。
///
/// 生命周期约束（本次改造的明确要求）：
/// <list type="bullet">
/// <item><b>关闭不等于执行</b>：关闭本窗口只关窗口，不开启游戏模式、不结束任何进程。
///       所以这里没有任何「保存并应用」逻辑，也不会在 Closing 里跑动作；</item>
/// <item><b>重复打开不会开出第二个窗口</b>：由 <see cref="MainWindow"/> 持有单实例并前置；</item>
/// <item>数据全部绑在 <see cref="MainViewModel"/> 上，浮窗自己不缓存任何状态。</item>
/// </list>
/// </summary>
public partial class BackgroundWhitelistWindow : Window
{
    public BackgroundWhitelistWindow()
    {
        InitializeComponent();

        // Esc 关闭。刻意不做「Esc 取消未保存改动」之类的花样：
        // 勾选状态是即时持久化的，关闭窗口不会丢掉任何设置。
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>无系统标题栏，整条标题栏负责拖动。</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>「详情」按钮：展开 / 收起当前行的完整路径与识别依据。</summary>
    private void ToggleDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BackgroundProcessEntry entry })
            entry.IsExpanded = !entry.IsExpanded;
    }
}
