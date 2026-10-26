namespace GameOptimizer.App.Views;

using System.Windows;
using System.Windows.Input;
using GameOptimizer.App.ViewModels;

/// <summary>
/// 「点 × 之后怎么处理」弹窗。
///
/// 三条生命周期约束：
/// <list type="bullet">
/// <item><b>关窗 = 取消</b>：点右上角 ×、按 Esc、点「取消」三者等价——
///       都不改配置、不隐藏主窗口、不退出。所以 Closing 里不需要任何回滚逻辑；</item>
/// <item><b>不替用户先做一半</b>：窗口开着的时候主窗口必须原样可见，
///       隐藏与退出都由调用方拿到结果之后再执行（见 App.OnWindowClosing）；</item>
/// <item>状态全在 <see cref="CloseBehaviorPromptViewModel"/> 上，窗口自己不缓存。</item>
/// </list>
/// </summary>
public partial class CloseBehaviorWindow : Window
{
    private CloseBehaviorPromptViewModel? _subscribed;

    public CloseBehaviorWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Attach();

        // Esc = 取消（与关窗完全一致）
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void Attach()
    {
        if (_subscribed is not null) _subscribed.CloseRequested -= OnCloseRequested;

        _subscribed = DataContext as CloseBehaviorPromptViewModel;
        if (_subscribed is not null) _subscribed.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>无系统标题栏，整条标题栏负责拖动。</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.CloseRequested -= OnCloseRequested;
            _subscribed = null;
        }

        base.OnClosed(e);
    }
}
