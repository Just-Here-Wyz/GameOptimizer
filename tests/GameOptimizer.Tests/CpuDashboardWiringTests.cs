namespace GameOptimizer.Tests;

using FluentAssertions;

/// <summary>
/// 首页与 CPU 页的「接线」守卫（源码级）。
///
/// 收集与判定逻辑本身各有单测，但**界面上绑的是不是那个数字**只有读源码才能验证：
/// 把 P 核峰值改个名当总体占用率显示，界面上看不出来、测试也测不到，
/// 却会让「CPU 占用 25%」这种读数彻底失真。这几条用例把接线钉死。
/// </summary>
public class CpuDashboardWiringTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void 首页CPU仪表应绑定总体占用而不是P核峰值()
    {
        var xaml = Read("src", "GameOptimizer.App", "Views", "MainWindow.xaml");

        xaml.Should().Contain("{Binding CpuLoadPercent}", "首页仪表显示的是整机总体占用率");
        xaml.Should().NotContain("CpuCorePercent", "P 核峰值不能直接改名当成总体占用率（口径不同）");
        xaml.Should().NotContain("CpuCoreLabel");
    }

    [Fact]
    public void 总体占用读不到时必须能显示未知()
    {
        // 环中心文字走覆盖值，读不到时写「未知」而不是画一个 0%
        var xaml = Read("src", "GameOptimizer.App", "Views", "MainWindow.xaml");
        xaml.Should().Contain("ValueTextOverride=\"{Binding CpuGaugeText}\"");
    }

    [Fact]
    public void 总CPU计数器只允许在一处采集()
    {
        // 首页与 CPU 页共用同一个读数；两个采集器各读一次会互相干扰（NextValue 是区间平均）
        var core = Path.Combine(RepoRoot(), "src", "GameOptimizer.Core");
        var hits = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Count(f => File.ReadAllText(f).Contains("\"% Processor Time\", \"_Total\"", StringComparison.Ordinal));

        hits.Should().Be(1, "总 CPU 必须只有一个采集出处（SystemPressureSampler），否则同一秒读两次会失真");
    }

    [Fact]
    public void CPU页应同时提供诊断与高级详情两个页签()
    {
        var xaml = Read("src", "GameOptimizer.App", "Views", "MainWindow.xaml");

        xaml.Should().Contain("Content=\"CPU 诊断\"");
        xaml.Should().Contain("Content=\"高级详情\"");
    }

    [Fact]
    public void CPU页三块内容都必须由导航状态决定可见性()
    {
        var xaml = Read("src", "GameOptimizer.App", "Views", "MainWindow.xaml");

        xaml.Should().Contain("{Binding IsCpuDiagnosisVisible");
        xaml.Should().Contain("{Binding IsCpuAdvancedVisible");
        xaml.Should().Contain("{Binding IsCpuOptimizeVisible");
    }

    [Fact]
    public void 快速优化入口与诊断入口必须挂在两个控件上()
    {
        // 嵌套按钮时点「CPU 优化」会连带触发外层导航，一次点击跑两条流程
        var xaml = Read("src", "GameOptimizer.App", "Views", "MainWindow.xaml");

        xaml.Should().Contain("Command=\"{Binding OpenCpuDetailCommand}\"");
        xaml.Should().Contain("Command=\"{Binding OpenCpuOptimizeCommand}\"");
        xaml.Should().Contain("<MouseBinding MouseAction=\"LeftClick\"",
            "CPU 卡片用 Border + MouseBinding 承接点击，避免按钮嵌套冒泡");
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
