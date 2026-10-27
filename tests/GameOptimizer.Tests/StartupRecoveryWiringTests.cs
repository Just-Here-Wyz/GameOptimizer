namespace GameOptimizer.Tests;

using FluentAssertions;

/// <summary>
/// 「启动时不再弹恢复询问」的接线守卫（源码级）。
///
/// 判定逻辑本身有单测（<see cref="StartupRecoveryTests"/>），但**启动流程有没有真的改用那套判定**
/// 只能读源码才知道：把弹窗文字改软、MessageBox 照旧留在 OnStartup 上，用户照样每次开机被问一次。
/// 这几条把接线钉死。
/// </summary>
public class StartupRecoveryWiringTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static string AppCode => Read("src", "GameOptimizer.App", "App.xaml.cs");
    private static string MainVmCode => Read("src", "GameOptimizer.App", "ViewModels", "MainViewModel.cs");
    private static string MainXaml => Read("src", "GameOptimizer.App", "Views", "MainWindow.xaml");

    /// <summary>
    /// 去掉注释行后的源码。断言「不许出现某段文字」时必须只看代码：
    /// 注释里记录「以前这里问的是什么」是有价值的，不该被守卫测试当成残留。
    /// </summary>
    private static string Code(string source) => string.Join(
        "\n",
        source.Split('\n').Where(line =>
        {
            var trimmed = line.TrimStart();
            return !trimmed.StartsWith("//", StringComparison.Ordinal);
        }));

    [Fact]
    public void 启动流程不得再出现恢复询问窗口()
    {
        // 那个窗口的标题与正文，一处都不许留（注释里记录历史不算）
        var code = Code(AppCode);
        code.Should().NotContain("游戏优化助手 — 恢复");
        code.Should().NotContain("上次关闭游戏模式后还有");
        code.Should().NotContain("是否现在恢复");
        code.Should().NotContain("是否现在还原");
    }

    [Fact]
    public void 启动恢复路径整体不得弹窗()
    {
        // 除了 UAC 那两条（用户主动授权失败要告知），OnStartup 这一路不该再有任何 MessageBox。
        // 否则等于「把弹窗从 A 挪到 B」，用户还是每次开机被拦一次。
        var start = AppCode.IndexOf("private (StartupRecoveryPlan Plan,", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "启动恢复必须走 RunStartupRecovery");

        var end = AppCode.IndexOf("private void CreateTrayIcon()", StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);

        var body = AppCode[start..end];
        body.Should().NotContain("MessageBox.Show", "启动恢复只写状态行与日志，不拦用户");
        body.Should().Contain("ProcessRestoreLog.Write", "不弹窗不等于不报告：失败要进日志");
    }

    [Fact]
    public void 启动恢复必须真的用上判定层()
    {
        AppCode.Should().Contain("RunStartupRecovery()");
        AppCode.Should().Contain("StartupRecoveryPlanner.Plan(", "判定必须来自可单测的纯函数");
        AppCode.Should().Contain("startupRecovery:", "读到的遗留记录要交给 ViewModel 挂到手动入口上");
    }

    [Fact]
    public void 还原电源计划时不得顺手拉起进程()
    {
        // 只把 GUID 交给 GameModeRecovery：带 Killed 进去就等于「点了是」，
        // 而现在的语义是「只还原系统状态，进程等用户自己决定」
        AppCode.Should().Contain("new GameModeSnapshot { PreviousPowerPlanGuid = plan.PowerPlanGuid }");
        AppCode.Should().NotContain("new GameModeSnapshot { PreviousPowerPlanGuid = plan.PowerPlanGuid, Killed");
    }

    [Fact]
    public void 首页必须提供手动恢复入口()
    {
        MainXaml.Should().Contain("{Binding OpenPendingRestoreCommand}", "没有这个按钮，遗留记录就再也点不到了");
        MainXaml.Should().Contain("{Binding PendingRestoreText",
            "没有记录时按钮要整块收起，不能留一个空壳");
        MainVmCode.Should().Contain("private async Task OpenPendingRestoreAsync()");
    }

    [Fact]
    public void 启动时统计的遗留数量必须先按资格筛一遍()
    {
        // 磁盘上的记录数 ≠ 真的还没回来：本机实测 14 条里 8 条其实早就被用户自己开回来了。
        // 不筛就会在状态行与按钮上写一个比事实大的数字。
        MainVmCode.Should().Contain("PrunePendingRestores()");
        MainVmCode.Should().Contain("var candidates = ProcessRestorePlanner.Build(_pendingRestores);");
    }

    [Fact]
    public void 手动恢复必须复用同一套选择窗口与落盘语义()
    {
        // 复用 ProcessRestoreWindow 与 SavePendingProcesses：各写一套迟早走样
        MainVmCode.Should().Contain("new ProcessRestoreViewModel(candidates, _processRestore)");
        MainVmCode.Should().Contain("SavePendingProcesses(");
    }

    [Fact]
    public void 取消恢复后不得声称下次启动会提醒()
    {
        // 旧文案是「下次启动仍可处理」——那个「处理」其实是弹窗，现在没有了
        MainVmCode.Should().NotContain("下次启动仍可处理");
        MainVmCode.Should().Contain("恢复遗留进程");
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
