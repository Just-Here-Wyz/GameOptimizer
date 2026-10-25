namespace GameOptimizer.Core.Navigation;

/// <summary>
/// 左侧导航的四个分区。CPU 详情页不是导航项，它寄居在「总览」分区里。
/// </summary>
public enum AppPage
{
    Overview = 0,
    Peripherals = 1,

    /// <summary>下载器（v2.2.0）。导航下标排在「设置」之前。</summary>
    Download = 2,

    Settings = 3,
}

/// <summary>
/// CPU 页面内部的两个二级视图。CPU 页仍然是「总览」分区里的一页，
/// 二级视图只决定页内显示哪一块，不影响左侧导航。
/// </summary>
public enum CpuView
{
    /// <summary>CPU 诊断（默认进入的就是它）。</summary>
    Diagnosis = 0,

    /// <summary>高级详情：型号、核心 / 线程、拓扑、P/E 核分组与逐逻辑处理器。</summary>
    Advanced = 1,
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

    /// <summary>
    /// CPU 页当前选中的二级视图。默认诊断页——用户点首页 CPU 卡片是要看「现在怎么样」，
    /// 不是先看每颗核的条形图。
    /// </summary>
    public CpuView CpuTab { get; private set; } = CpuView.Diagnosis;

    /// <summary>快速优化流程是否展开。它寄居在诊断视图里，是流程而不是第三个页签。</summary>
    public bool IsCpuOptimizeOpen { get; private set; }

    /// <summary>总览页主体。详情页展开时收起——两者共用同一块区域。</summary>
    public bool IsOverviewVisible => Section == AppPage.Overview && !IsCpuDetailOpen;

    /// <summary>
    /// CPU 页容器。只看「是否展开」是不够的：用户展开后切到外设，
    /// 容器必须跟着让位，否则它会盖在外设页上。
    /// </summary>
    public bool IsCpuDetailVisible => Section == AppPage.Overview && IsCpuDetailOpen;

    /// <summary>CPU 诊断视图。快速优化展开时让位给它——同一区域只能显示一块内容。</summary>
    public bool IsCpuDiagnosisVisible => IsCpuDetailVisible && CpuTab == CpuView.Diagnosis && !IsCpuOptimizeOpen;

    /// <summary>快速优化流程视图。</summary>
    public bool IsCpuOptimizeVisible => IsCpuDetailVisible && CpuTab == CpuView.Diagnosis && IsCpuOptimizeOpen;

    /// <summary>高级详情视图。</summary>
    public bool IsCpuAdvancedVisible => IsCpuDetailVisible && CpuTab == CpuView.Advanced;

    public bool IsPeripheralsVisible => Section == AppPage.Peripherals;

    /// <summary>下载页（v2.2.0）。</summary>
    public bool IsDownloadVisible => Section == AppPage.Download;

    public bool IsSettingsVisible => Section == AppPage.Settings;

    /// <summary>切到某个导航分区。返回是否真的发生了变化（false 时不必刷新界面）。</summary>
    public bool Navigate(AppPage section)
    {
        if (Section == section) return false;

        Section = section;
        return true;
    }

    /// <summary>
    /// 打开 CPU 页。默认进诊断视图；详情页只属于总览分区，所以顺带切回总览。
    /// 同时关掉快速优化——从首页点进来是要看页面，不是继续上一次的流程。
    /// </summary>
    public bool OpenCpuDetail(CpuView view = CpuView.Diagnosis)
    {
        var changed = Section != AppPage.Overview
                      || !IsCpuDetailOpen
                      || CpuTab != view
                      || IsCpuOptimizeOpen;

        Section = AppPage.Overview;
        IsCpuDetailOpen = true;
        CpuTab = view;
        IsCpuOptimizeOpen = false;

        return changed;
    }

    /// <summary>关闭 CPU 页（「← 返回」）。返回是否真的发生了变化。</summary>
    public bool CloseCpuDetail()
    {
        if (!IsCpuDetailOpen) return false;

        IsCpuDetailOpen = false;
        IsCpuOptimizeOpen = false;
        return true;
    }

    /// <summary>
    /// 切换 CPU 页的二级视图（诊断 / 高级详情）。切换即退出快速优化流程，
    /// 避免「页签已经是详情、下面还挂着优化面板」。
    /// </summary>
    public bool SelectCpuTab(CpuView view)
    {
        if (!IsCpuDetailOpen) return false;
        if (CpuTab == view && !IsCpuOptimizeOpen) return false;

        CpuTab = view;
        IsCpuOptimizeOpen = false;
        return true;
    }

    /// <summary>
    /// 打开快速优化流程。幂等：重复触发返回 false，不会出现两份视图同时可见。
    /// </summary>
    public bool OpenCpuOptimize()
    {
        var changed = Section != AppPage.Overview
                      || !IsCpuDetailOpen
                      || CpuTab != CpuView.Diagnosis
                      || !IsCpuOptimizeOpen;

        Section = AppPage.Overview;
        IsCpuDetailOpen = true;
        CpuTab = CpuView.Diagnosis;
        IsCpuOptimizeOpen = true;

        return changed;
    }

    /// <summary>退出快速优化流程，回到诊断视图。</summary>
    public bool CloseCpuOptimize()
    {
        if (!IsCpuOptimizeOpen) return false;

        IsCpuOptimizeOpen = false;
        return true;
    }

    /// <summary>
    /// 导航列表下标 → 分区。越界返回 null，不抛异常（下标来自界面）。
    ///
    /// 下标顺序与界面「总览 / 外设 / 下载 / 设置」一一对应，下载排在设置**之前**。
    /// </summary>
    public static AppPage? FromNavIndex(int index) => index switch
    {
        0 => AppPage.Overview,
        1 => AppPage.Peripherals,
        2 => AppPage.Download,
        3 => AppPage.Settings,
        _ => null,
    };

    /// <summary>
    /// 分区 → 导航列表下标。窗口被销毁后重建时用它把高亮恢复到当前页面，
    /// 否则新建的 ListBox 恒为 0（总览），会和实际显示的页面不一致。
    /// </summary>
    public static int ToNavIndex(AppPage page) => page switch
    {
        AppPage.Peripherals => 1,
        AppPage.Download => 2,
        AppPage.Settings => 3,
        _ => 0,
    };
}
