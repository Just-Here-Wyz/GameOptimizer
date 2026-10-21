namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Navigation;

/// <summary>
/// 页面导航状态机。
///
/// 锁死的是 v2.1.1 的真实事故：点「外设」再回「总览」时，总览页与 CPU 详情页
/// 同时可见叠成一片、「← 返回」点了没反应。
///
/// 根因不在状态机本身，而在可见性有两个写入方：XAML 绑定 + 代码后置的
/// <c>ClearValue</c> / <c>Visibility =</c>。WPF 里绑定就是一个本地值，
/// 后置代码一赋值绑定就被顶掉，之后再 <c>ClearValue</c> 没有绑定可回落，
/// 两个页面各自回落到默认的 Visible。
///
/// 所以这些用例只断言「同一时刻至多一个页面可见」——只要这个不变式成立，
/// 界面那层就算写错也叠不起来。
/// </summary>
public class PageRouterTests
{
    [Fact]
    public void 初始状态只显示总览页()
    {
        var r = new PageRouter();

        r.Section.Should().Be(AppPage.Overview);
        r.IsOverviewVisible.Should().BeTrue();
        r.IsCpuDetailOpen.Should().BeFalse();
        r.IsCpuDetailVisible.Should().BeFalse();
        r.IsPeripheralsVisible.Should().BeFalse();
        r.IsSettingsVisible.Should().BeFalse();
    }

    [Fact]
    public void 切到外设页时总览页必须收起()
    {
        var r = new PageRouter();

        r.Navigate(AppPage.Peripherals).Should().BeTrue();

        r.IsPeripheralsVisible.Should().BeTrue();
        r.IsOverviewVisible.Should().BeFalse();
        r.IsSettingsVisible.Should().BeFalse();
    }

    [Fact]
    public void 外设页返回总览应恢复总览且详情页不复活()
    {
        // 这就是用户报的那条路径：总览 → 外设 → 总览。
        // 旧实现走到最后一步时两个页面都是 Visible，屏幕上叠成一片。
        var r = new PageRouter();

        r.Navigate(AppPage.Peripherals);
        r.Navigate(AppPage.Overview);

        r.IsOverviewVisible.Should().BeTrue();
        r.IsCpuDetailVisible.Should().BeFalse(
            "详情页从未被打开过，返回总览不该凭空冒出详情页");
        r.IsPeripheralsVisible.Should().BeFalse();
    }

    [Fact]
    public void 详情页展开时切走再切回应保持展开()
    {
        // 用户的意图是「我只是去看一眼外设」，回来还该在详情页上，不该被偷偷关掉。
        var r = new PageRouter();
        r.OpenCpuDetail();

        r.Navigate(AppPage.Peripherals);
        r.Navigate(AppPage.Overview);

        r.IsCpuDetailOpen.Should().BeTrue();
        r.IsCpuDetailVisible.Should().BeTrue();
        r.IsOverviewVisible.Should().BeFalse("详情页与总览页共用同一块区域");
    }

    [Fact]
    public void 详情页展开后切到外设页必须让位()
    {
        // 旧实现里详情页的可见性只看 IsCpuDetailOpen，切走后它会盖在外设页上。
        var r = new PageRouter();
        r.OpenCpuDetail();

        r.Navigate(AppPage.Peripherals);

        r.IsCpuDetailVisible.Should().BeFalse();
        r.IsPeripheralsVisible.Should().BeTrue();
    }

    [Fact]
    public void 返回按钮在任何分区下都必须能关掉详情页()
    {
        // 「点返回没有用」是用户报的第二个症状：
        // 旧实现的返回只改 ViewModel 属性，而该属性上的绑定早已被代码后置顶掉。
        var r = new PageRouter();
        r.OpenCpuDetail();
        r.Navigate(AppPage.Settings);

        r.CloseCpuDetail().Should().BeTrue();

        r.IsCpuDetailOpen.Should().BeFalse();
        r.IsSettingsVisible.Should().BeTrue("关详情页不该把用户弹回总览");
    }

    [Fact]
    public void 打开详情页会自动回到总览分区()
    {
        // 详情页只在总览分区里有意义；从设置页打开它必须先把页面切回去。
        var r = new PageRouter();
        r.Navigate(AppPage.Settings);

        r.OpenCpuDetail().Should().BeTrue();

        r.Section.Should().Be(AppPage.Overview);
        r.IsCpuDetailVisible.Should().BeTrue();
        r.IsSettingsVisible.Should().BeFalse();
    }

    [Fact]
    public void 重复切换同一分区不应报变化()
    {
        // 界面上重复点同一个导航项很常见，返回 false 让 ViewModel 跳过刷新
        var r = new PageRouter();

        r.Navigate(AppPage.Overview).Should().BeFalse();
        r.CloseCpuDetail().Should().BeFalse("本来就没展开");

        r.OpenCpuDetail().Should().BeTrue();
        r.OpenCpuDetail().Should().BeFalse("已经展开了");
    }

    [Fact]
    public void 任何操作序列下至多只有一个页面可见()
    {
        // 不变式用穷举保证，避免以后加页面时漏掉某个组合。
        var pages = Enum.GetValues<AppPage>();
        var r = new PageRouter();

        foreach (var first in pages)
        foreach (var detail in new[] { false, true })
        foreach (var second in pages)
        {
            r.Navigate(first);
            if (detail) r.OpenCpuDetail();
            else r.CloseCpuDetail();
            r.Navigate(second);

            var visible = new[]
            {
                r.IsOverviewVisible,
                r.IsCpuDetailVisible,
                r.IsPeripheralsVisible,
                r.IsSettingsVisible,
            }.Count(v => v);

            visible.Should().Be(1,
                $"{first}(详情={detail}) → {second} 之后必须恰好一个页面可见");
        }
    }

    [Theory]
    [InlineData(0, AppPage.Overview)]
    [InlineData(1, AppPage.Peripherals)]
    [InlineData(2, AppPage.Settings)]
    public void 导航下标应映射到对应分区(int index, AppPage expected)
    {
        PageRouter.FromNavIndex(index).Should().Be(expected);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void 越界下标应返回空而不是抛异常(int index)
    {
        // 下标来自界面控件，越界不能把整个窗口带崩
        PageRouter.FromNavIndex(index).Should().BeNull();
    }

    [Theory]
    [InlineData(AppPage.Overview, 0)]
    [InlineData(AppPage.Peripherals, 1)]
    [InlineData(AppPage.Settings, 2)]
    public void 分区应能反查回导航下标(AppPage page, int expected)
    {
        // 窗口被销毁后重建（托盘「显示主界面」）要靠它恢复高亮
        PageRouter.ToNavIndex(page).Should().Be(expected);
    }

    [Fact]
    public void 下标与分区的相互转换必须自洽()
    {
        // 两个映射表分头维护，写岔了会出现「点外设却高亮总览」这种错位
        foreach (var page in Enum.GetValues<AppPage>())
        {
            PageRouter.FromNavIndex(PageRouter.ToNavIndex(page)).Should().Be(page);
        }
    }

    /// <summary>
    /// 源码级防回退：代码后置里**不允许**再出现 Visibility 写入。
    ///
    /// 这条用例守的就是事故本身。渲染层的叠放没法在无 UI 的环境里断言，
    /// 但「可见性被两个写入方管」这个根因可以直接从源码上查死：
    /// 只要有人又在 code-behind 里写 <c>xxx.Visibility = ...</c> 或
    /// <c>ClearValue(VisibilityProperty)</c>，绑定就会再次被顶掉，用例立刻失败。
    /// </summary>
    [Fact]
    public void 代码后置不应再出现Visibility写入()
    {
        var viewsDir = Path.Combine(RepoRoot(), "src", "GameOptimizer.App", "Views");
        Directory.Exists(viewsDir).Should().BeTrue();

        var codeBehind = Directory.EnumerateFiles(viewsDir, "*.xaml.cs", SearchOption.TopDirectoryOnly).ToList();
        codeBehind.Should().NotBeEmpty("Views 下应当有 code-behind 文件");

        foreach (var file in codeBehind)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            text.Should().NotContain("ClearValue(VisibilityProperty",
                $"{name} 用 ClearValue 清 Visibility 会让绑定永久丢失（v2.1.2 的叠页事故）");

            // 排除注释里的说明文字：只看真正赋值的那一行
            var assigns = text
                .Split('\n')
                .Select((line, i) => (line: line.Trim(), no: i + 1))
                .Where(x => !x.line.StartsWith("//") && !x.line.StartsWith("///"))
                .Where(x => System.Text.RegularExpressions.Regex.IsMatch(
                    x.line, @"\.Visibility\s*=\s*Visibility\."))
                .ToList();

            assigns.Should().BeEmpty(
                $"{name} 不应直接改 Visibility，页面可见性必须由 PageRouter 绑定下来");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }
}
