namespace GameOptimizer.App.Views;

using System.Windows;
using System.Windows.Controls;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// 左侧导航切换。用 Visibility 而不是 TabControl：
    /// 顶部状态栏要长期可见，TabControl 会把它一起装进页签里。
    /// </summary>
    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HomePage is null || DataPage is null) return; // 初始化期间可能尚未构建

        var index = NavList.SelectedIndex;
        HomePage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        DataPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
    }
}
