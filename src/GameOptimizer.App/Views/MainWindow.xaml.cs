namespace GameOptimizer.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using GameOptimizer.App.ViewModels;
using GameOptimizer.Core.Library;

/// <summary>
/// 无系统白框窗口的代码后置：自绘标题栏拖动、双击最大化、三个窗口按钮。
///
/// 用 `WindowStyle=None` + `WindowChrome` 而不是 `AllowsTransparency=True`：
/// 后者会关掉硬件加速并让文字发虚，参考图里的界面是清晰的。
///
/// 页面切换不在这里做：可见性全部由 ViewModel 绑定下来（见 <see cref="NavList_SelectionChanged"/>）。
/// </summary>
public partial class MainWindow : Window
{
    private HwndSource? _source;

    public MainWindow()
    {
        InitializeComponent();

        WindowChromeHelper.ApplyMaximizeFix(this);

        // 版本号从程序集读，避免界面上写死一个和 csproj 对不上的数字
        var version = typeof(MainWindow).Assembly.GetName().Version;
        if (version is not null)
            VersionText.Text = $"版本 v{version.Major}.{version.Minor}.{version.Build}";

        StateChanged += (_, _) => UpdateMaximizeGlyph();

        // 窗口可能被重建（托盘「显示主界面」走的就是这条路），
        // ListBox 新建后 SelectedIndex 是 XAML 写死的 0，必须按 ViewModel 当前页面校正，
        // 否则高亮停在「总览」而实际显示的是外设 / 设置页。
        //
        // 只在这一处写 NavList.SelectedIndex：它的 SelectionChanged 会回调 ViewModel，
        // 而 PageRouter.Navigate 对「切到同一分区」返回 false，所以校正不会引起递归。
        Loaded += (_, _) => SyncNavSelection();
    }

    /// <summary>把左侧导航的高亮对齐到 ViewModel 当前所在的页面。</summary>
    private void SyncNavSelection()
    {
        if (DataContext is not MainViewModel vm) return;

        var target = vm.CurrentNavIndex;
        if (NavList.SelectedIndex != target) NavList.SelectedIndex = target;
    }

    /// <summary>
    /// 挂窗口消息钩子：第二个实例启动时会广播一条唤醒消息，这里负责把窗口顶到前面。
    /// 必须在 SourceInitialized 之后挂——那时才有真正的 HWND。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(WndProc);
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WndProc);
        _source = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// 收到「已有实例被再次双击」的广播：显示并前置窗口。
    /// 窗口此时通常是隐藏到托盘的状态（Close 只是 Hide），必须 Show 而不是只 Activate。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!SingleInstanceGate.IsActivationMessage(msg)) return IntPtr.Zero;

        handled = true;
        Dispatcher.BeginInvoke(new Action(BringToFront));
        return IntPtr.Zero;
    }

    private void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        // 前台窗口权限被系统限制时 Activate 可能只闪一下任务栏，
        // 翻一次 Topmost 能把窗口真正顶到最前（前后各一次，不留置顶副作用）。
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// 最大化按钮的图形随窗口状态切换：
    /// 还原态画「一个方框」，最大化态画「两个叠在一起的方框」。
    /// 自绘 Path 而不是用 Segoe MDL2 字体图标——实测该字体在本机不渲染，
    /// 窗口按钮会变成三个空白方块。
    /// </summary>
    private void UpdateMaximizeGlyph()
    {
        if (MaximizeGlyph is null) return;

        MaximizeGlyph.Data = WindowState == WindowState.Maximized
            ? System.Windows.Media.Geometry.Parse("M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z M2.5,2.5 L2.5,0.5 L9.5,0.5 L9.5,7.5 L7.5,7.5")
            : System.Windows.Media.Geometry.Parse("M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击标题栏切换最大化，与系统行为一致
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// 左侧窄导航切换。用 Visibility 而不是 TabControl：
    /// 顶部标题栏与页内页头要长期可见，TabControl 会把它们一起装进页签里。
    /// v1.0.1 起右上角页签已删除，这里是唯一的页面切换入口。
    ///
    /// **这里绝不写 Visibility**：四个页面的可见性都由 ViewModel（<c>PageRouter</c>）绑定下来。
    /// v2.1.1 及以前这里直接给 <c>HomePage.Visibility</c> 赋值、切回时又 <c>ClearValue</c>，
    /// 而 Visibility 上有绑定——赋值会顶掉绑定，ClearValue 之后没有绑定可回落，
    /// 于是总览页与 CPU 详情页同时回落到默认的 Visible 叠在一起，
    /// 「← 返回」也因为绑定已失效而点了没反应。
    /// </summary>
    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        vm.NavigateTo(NavList.SelectedIndex);
    }
}
