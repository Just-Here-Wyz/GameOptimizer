namespace GameOptimizer.App.Views;

using System.Windows.Controls;

/// <summary>
/// 下载页的代码后置。**没有一行业务逻辑**：数据绑定到
/// <see cref="ViewModels.DownloadViewModel"/>，页面可见性由 <see cref="MainWindow"/> 统一控制。
///
/// 存在的唯一理由是按 F5 时能把焦点放到地址栏（粘贴直链是最常用的入口）。
/// </summary>
public partial class DownloadPage : UserControl
{
    public DownloadPage()
    {
        InitializeComponent();

        // 首次显示时把光标放进地址栏：用户切到下载页通常就是要粘一条链接
        Loaded += (_, _) =>
        {
            if (UrlBox.IsKeyboardFocusWithin) return;

            UrlBox.Focus();
            UrlBox.CaretIndex = UrlBox.Text?.Length ?? 0;
        };
    }
}
