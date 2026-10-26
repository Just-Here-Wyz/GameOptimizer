namespace GameOptimizer.Tests;

using FluentAssertions;
using System.Reflection;

/// <summary>
/// 版本号一致性检查。
///
/// 版本号只在 Directory.Build.props 里定义一处，三个项目自动继承。
/// 这些用例保证：改版本号时不会漏掉某个项目，也不会出现程序集版本与
/// 文档里写的不一致。
/// </summary>
public class VersionConsistencyTests
{
    private static Version AssemblyVersion => typeof(GameOptimizer.Core.Models.SampleFrame).Assembly.GetName().Version!;

    [Fact]
    public void Core程序集应带有版本号()
    {
        AssemblyVersion.Should().NotBeNull();
        AssemblyVersion.Major.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void 三个项目应共享同一版本号()
    {
        var core = typeof(GameOptimizer.Core.Models.SampleFrame).Assembly.GetName().Version;
        var tests = Assembly.GetExecutingAssembly().GetName().Version;

        // 都来自 Directory.Build.props，主次版本必须一致
        tests!.Major.Should().Be(core!.Major);
        tests.Minor.Should().Be(core.Minor);
    }

    [Fact]
    public void 程序集版本应与DirectoryBuildProps一致()
    {
        // 旧写法是「版本不应是默认的 1.0.0」，但 v1.0.0 之后这个数字本身就是合法版本，
        // 那条断言会永久失败。改为直接比对 props 里的值 —— 真正要防的是
        // 「改了 props 没生效」和「改了 props 忘了改测试」。
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var match = System.Text.RegularExpressions.Regex.Match(props, @"<Version>([\d.]+)</Version>");
        match.Success.Should().BeTrue("Directory.Build.props 里必须有 <Version>");

        var declared = Version.Parse(match.Groups[1].Value);

        AssemblyVersion.Major.Should().Be(declared.Major);
        AssemblyVersion.Minor.Should().Be(declared.Minor);
        AssemblyVersion.Build.Should().Be(declared.Build);
    }

    [Fact]
    public void 当前版本应为2_5_0()
    {
        // 版本号按 docs/03-版本规则.md 的规则自动推进（tools/bump-version.ps1 同步此处）。
        // 保留这条硬断言：改了 props 却漏掉某个文件时，这里第一个变红。
        AssemblyVersion.Major.Should().Be(2);
        AssemblyVersion.Minor.Should().Be(5);
        AssemblyVersion.Build.Should().Be(0);
    }

    [Fact]
    public void 文档与README里的版本号应与props一致()
    {
        // 版本号由 tools/bump-version.ps1 一处推进、七处同步；
        // 漏掉任何一处，用户就会在不同地方看到两个版本号，比没有版本号更糟。
        var tag = "v" + DeclaredVersion();

        File.ReadAllText(Path.Combine(RepoRoot(), "docs", "02-功能清单.md"))
            .Should().StartWith($"# 功能清单（{tag}）");

        File.ReadAllText(Path.Combine(RepoRoot(), "docs", "04-已知限制.md"))
            .Should().StartWith($"# 已知限制（{tag}）");

        File.ReadAllText(Path.Combine(RepoRoot(), "docs", "README.md"))
            .Should().Contain($"当前版本：**{tag}**");

        File.ReadAllText(Path.Combine(RepoRoot(), "README.md"))
            .Should().Contain($"**{tag}** ｜");
    }

    [Fact]
    public void 版本推进脚本应存在且覆盖同步清单()
    {
        // 手工改版本号就是漏改的开始：脚本必须知道全部落点，缺一个就等于没有自动化
        var script = Path.Combine(RepoRoot(), "tools", "bump-version.ps1");
        File.Exists(script).Should().BeTrue("版本号按规则自动推进，脚本是唯一入口");

        var text = File.ReadAllText(script);
        text.Should().Contain("Directory.Build.props");
        text.Should().Contain("VersionConsistencyTests.cs");
        text.Should().Contain("docs\\02-功能清单.md");
        text.Should().Contain("docs\\04-已知限制.md");
        text.Should().Contain("docs\\README.md");
        text.Should().Contain("README.md");
        text.Should().Contain("'feature'").And.Contain("'fix'").And.Contain("'major'");
    }

    [Fact]
    public void 项目根目录应存在版本定义文件()
    {
        var root = RepoRoot();
        var props = Path.Combine(root, "Directory.Build.props");

        File.Exists(props).Should().BeTrue("版本号必须集中定义在 Directory.Build.props");

        var text = File.ReadAllText(props);
        text.Should().Contain("<Version>");
    }

    [Fact]
    public void 各csproj不应自己硬编码版本号()
    {
        // 重复定义会导致目录里的版本被覆盖，出现"改了没生效"的困惑
        foreach (var csproj in Directory.EnumerateFiles(RepoRoot(), "*.csproj", SearchOption.AllDirectories))
        {
            if (csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            var text = File.ReadAllText(csproj);
            text.Should().NotContain("<Version>", $"{Path.GetFileName(csproj)} 不应自己定义 <Version>");
        }
    }

    /// <summary>从 Directory.Build.props 读出「唯一真源」的版本号，如 2.5.0。</summary>
    private static string DeclaredVersion()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var match = System.Text.RegularExpressions.Regex.Match(props, @"<Version>([\d.]+)</Version>");
        match.Success.Should().BeTrue("Directory.Build.props 里必须有 <Version>");
        return match.Groups[1].Value;
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
