namespace GameOptimizer.Core.Navigation;

/// <summary>
/// 左侧导航的三个分区。CPU 详情页不是导航项，它寄居在「总览」分区里。
/// </summary>
public enum AppPage
{
    Overview = 0,
    Peripherals = 1,
    Settings = 2,
}

/// <summary>
/// 页面导航状态机：**唯一**决定「哪个页面可见」的地方。
///
/// 存在的理由是一次真实事故（v2.1.2 修复）：页面可见性原本由 XAML 绑定与代码后置
/// 的 <c>ClearValue</c> / <c>Visibility =</c> 共同决定，而绑定在 WPF 里就是一个本地值，
/// 两者会互相打架：
///
/// <list type="number">
/// <item>切到外设时写 <c>HomePage.Visibility = Collapsed</c>，绑定被本地值顶掉（永久丢失）；</item>
/// <item>切回总览时 <c>ClearValue</c> 想「把控制权交还绑定」，可绑定已经没了，
///       两个页面各自回落到默认值 <c>Visible</c>，于是总览与 CPU 详情叠在一起；</item>
/// <item>「← 返回」只改 ViewModel 属性，没有绑定可通知，点击看起来完全没反应。</item>
/// </list>
///
/// 现在所有可见性都由这里算出来，界面只做单向绑定，代码后置不再碰 <c>Visibility</c>。
/// </summary>
public sealed class PageRouter
{
    /// <summary>当前所在的导航分区，默认总览。</summary>
    public AppPage Section { get; private set; } = AppPage.Overview;

    /// <summary>CPU 详情页是否展开。切走再切回来会保持原样，不偷偷关掉。</summary>
    public bool IsCpuDetailOpen { get; private set; }

    /// <summary>总览页主体。详情页展开时收起——两者共用同一块区域。</summary>
    public bool IsOverviewVisible => Section == AppPage.Overview && !IsCpuDetailOpen;

    /// <summary>
    /// CPU 详情页。只看「是否展开」是不够的：用户展开详情后切到外设，
    /// 详情页必须跟着让位，否则它会盖在外设页上。
    /// </summary>
    public bool IsCpuDetailVisible => Section == AppPage.Overview && IsCpuDetailOpen;

    public bool IsPeripheralsVisible => Section == AppPage.Peripherals;

    public bool IsSettingsVisible => Section == AppPage.Settings;

    /// <summary>切到某个导航分区。返回是否真的发生了变化（false 时不必刷新界面）。</summary>
    public bool Navigate(AppPage section)
    {
        if (Section == section) return false;

        Section = section;
        return true;
    }

    /// <summary>打开 CPU 详情页。详情页只属于总览分区，所以顺带切回总览。</summary>
    public bool OpenCpuDetail()
    {
        var changed = Section != AppPage.Overview || !IsCpuDetailOpen;

        Section = AppPage.Overview;
        IsCpuDetailOpen = true;

        return changed;
    }

    /// <summary>关闭 CPU 详情页（「← 返回」）。返回是否真的发生了变化。</summary>
    public bool CloseCpuDetail()
    {
        if (!IsCpuDetailOpen) return false;

        IsCpuDetailOpen = false;
        return true;
    }

    /// <summary>导航列表下标 → 分区。越界返回 null，不抛异常（下标来自界面）。</summary>
    public static AppPage? FromNavIndex(int index) => index switch
    {
        0 => AppPage.Overview,
        1 => AppPage.Peripherals,
        2 => AppPage.Settings,
        _ => null,
    };

    /// <summary>
    /// 分区 → 导航列表下标。窗口被销毁后重建时用它把高亮恢复到当前页面，
    /// 否则新建的 ListBox 恒为 0（总览），会和实际显示的页面不一致。
    /// </summary>
    public static int ToNavIndex(AppPage page) => page switch
    {
        AppPage.Peripherals => 1,
        AppPage.Settings => 2,
        _ => 0,
    };
}
