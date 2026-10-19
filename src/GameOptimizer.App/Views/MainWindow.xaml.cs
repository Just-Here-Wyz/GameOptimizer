namespace GameOptimizer.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

/// <summary>
/// 无系统白框窗口的代码后置：自绘标题栏拖动、双击最大化、三个窗口按钮、页面切换。
///
/// 用 `WindowStyle=None` + `WindowChrome` 而不是 `AllowsTransparency=True`：
/// 后者会关掉硬件加速并让文字发虚，参考图里的界面是清晰的。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        WindowChromeHelper.ApplyMaximizeFix(this);

        // 版本号从程序集读，避免界面上写死一个和 csproj 对不上的数字
        var version = typeof(MainWindow).Assembly.GetName().Version;
        if (version is not null)
            VersionText.Text = $"版本 v{version.Major}.{version.Minor}.{version.Build}";

        StateChanged += (_, _) => UpdateMaximizeGlyph();
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
    /// </summary>
    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HomePage is null || PeripheralPage is null || SettingsPage is null) return;

        var index = NavList.SelectedIndex;
        if (index < 0) return;

        ShowPage(index);

        // 顶部页签与左侧导航保持一致，不在两者之间制造两套状态
        TabHome.IsChecked = index == 0;
        TabPeripherals.IsChecked = index == 1;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage is null || PeripheralPage is null) return;

        var index = ReferenceEquals(sender, TabPeripherals) ? 1 : 0;
        NavList.SelectedIndex = index;
    }

    private void ShowPage(int index)
    {
        HomePage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeripheralPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}
