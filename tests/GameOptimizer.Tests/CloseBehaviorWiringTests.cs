namespace GameOptimizer.Tests;

using FluentAssertions;

/// <summary>
/// 关闭行为的「接线」守卫（源码级）。
///
/// 判定与持久化本身有单测（<see cref="CloseBehaviorTests"/>），但**界面与关闭事件是不是真的接到那套判定上**
/// 只能读源码才知道：把弹窗做得很漂亮、Closing 里却照旧无条件 Hide，功能等于没做，
/// 而且跑起来看着还挺正常（窗口确实收进了托盘）。这几条把接线钉死。
/// </summary>
public class CloseBehaviorWiringTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static string AppCode => Read("src", "GameOptimizer.App", "App.xaml.cs");
    private static string PromptViewModelCode =>
        Read("src", "GameOptimizer.App", "ViewModels", "CloseBehaviorPromptViewModel.cs");
    private static string PromptXaml => Read("src", "GameOptimizer.App", "Views", "CloseBehaviorWindow.xaml");
    private static string MainXaml => Read("src", "GameOptimizer.App", "Views", "MainWindow.xaml");
    private static string SettingsViewModelCode =>
        Read("src", "GameOptimizer.App", "ViewModels", "CloseBehaviorSettingsViewModel.cs");

    [Fact]
    public void 关闭事件必须按三种结论分流()
    {
        // 只处理其中一种（例如只知道隐藏到托盘）就等于把另外两种需求丢了
        AppCode.Should().Contain("case CloseRoute.HideToTray");
        AppCode.Should().Contain("case CloseRoute.Exit");
        AppCode.Should().Contain("case CloseRoute.Ask");
    }

    [Fact]
    public void 关闭事件必须先拦下再决定结局()
    {
        // Closing 里必须先 Cancel：否则窗口在用户确认之前就自己关掉了，
        // 弹窗会变成「事后告知」，取消也救不回来
        AppCode.Should().Contain("e.Cancel = true");

        // 且必须先判定 _reallyExit 再分流——托盘退出不能被自己拦住
        var index = AppCode.IndexOf("if (_reallyExit) return;", StringComparison.Ordinal);
        index.Should().BeGreaterThan(0, "托盘退出 / 已确认退出要放行真正关闭");
    }

    [Fact]
    public void 托盘退出必须标记已确认退出以避免重复弹窗()
    {
        AppCode.Should().Contain("_closeBehavior?.ConfirmExit()",
            "托盘「退出」要告诉服务：用户已经用行动回答过这个问题了");
    }

    [Fact]
    public void 正常退出不得靠强杀进程()
    {
        AppCode.Should().NotContain("Environment.Exit",
            "正常退出必须走 Shutdown，否则 OnExit 里的游戏模式还原会被跳过");
        AppCode.Should().NotContain(".Kill()");
    }

    [Fact]
    public void 弹窗默认应选中缩小到系统托盘且不勾记住()
    {
        PromptViewModelCode.Should().Contain("= CloseBehavior.MinimizeToTray;",
            "默认推荐「缩小到托盘」，与托盘常驻的老习惯一致");
        PromptViewModelCode.Should().Contain("private bool _remember;",
            "「记住我的选择」默认必须不勾");
        PromptViewModelCode.Should().NotContain("_remember = true");
    }

    [Fact]
    public void 弹窗两个选项与记住复选框都要绑到ViewModel()
    {
        PromptXaml.Should().Contain("{Binding IsMinimizeSelected");
        PromptXaml.Should().Contain("{Binding IsExitSelected");
        PromptXaml.Should().Contain("{Binding Remember");
        PromptXaml.Should().Contain("记住我的选择，下次不再提示");
    }

    [Fact]
    public void 弹窗的取消按钮与取消命令都不能写配置()
    {
        // 取消 = 什么都不做：不隐藏、不退出、不改配置
        PromptXaml.Should().Contain("Command=\"{Binding CancelCommand}\"");
        PromptViewModelCode.Should().Contain("Result = null;");
    }

    [Fact]
    public void 设置页应提供三种关闭行为且可重置为每次询问()
    {
        MainXaml.Should().Contain("{Binding CloseBehaviorSettings.IsAskSelected");
        MainXaml.Should().Contain("{Binding CloseBehaviorSettings.IsMinimizeSelected");
        MainXaml.Should().Contain("{Binding CloseBehaviorSettings.IsExitSelected");
    }

    [Fact]
    public void 截图模式下的配置路径不能露出临时文件()
    {
        // 渲染进程写的是 %TEMP%\go-render-close-behavior.json，而设置页截图要进公开仓库：
        // 图上必须显示真实默认位置，不能把渲染痕迹拍进去（与 BackgroundConfigPath 同一处理）。
        SettingsViewModelCode.Should().Contain("MainViewModel.ScreenshotMode");
        SettingsViewModelCode.Should().Contain(@"%LocalAppData%\GameOptimizer\close-behavior.json");
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
