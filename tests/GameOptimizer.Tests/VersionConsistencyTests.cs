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
    public void 版本号不应是默认的1_0_0()
    {
        // 若 Directory.Build.props 失效，会回退成 1.0.0.0
        var v = AssemblyVersion;
        (v.Major == 1 && v.Minor == 0 && v.Build == 0 && v.Revision == 0)
            .Should().BeFalse("Directory.Build.props 里的版本号可能没有生效");
    }

    [Fact]
    public void 当前版本应为0_5_0()
    {
        // 版本号由用户手动设定；升级时改这里和 Directory.Build.props
        AssemblyVersion.Major.Should().Be(0);
        AssemblyVersion.Minor.Should().Be(5);
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
