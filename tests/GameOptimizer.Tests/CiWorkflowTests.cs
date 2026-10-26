namespace GameOptimizer.Tests;

using FluentAssertions;

/// <summary>
/// CI 配置守卫。
///
/// workflow 是纯文本，改坏了没有任何编译期报错：
/// 把 <c>windows-latest</c> 写成 ubuntu，要等推送后才知道；把 <c>dotnet test</c>
/// 那一步删掉，则是永远不会红——只是从此再也没人验证过任何东西。
/// 这两类错都必须在这里先红。
/// </summary>
public class CiWorkflowTests
{
    private static string WorkflowPath => Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml");

    private static string WorkflowText => File.ReadAllText(WorkflowPath);

    [Fact]
    public void CI工作流应存在()
    {
        File.Exists(WorkflowPath).Should().BeTrue(
            "CI 是改完代码后的第二道门，删掉它不会让任何东西编译失败，只会静默失去保护");
    }

    [Fact]
    public void 必须跑在Windows上()
    {
        // Core / App / Tests 全是 net8.0-windows。换成 Linux runner 不是「跑得慢」，
        // 是还原阶段就失败——而且改这一行不会编译报错。
        WorkflowText.Should().Contain("runs-on: windows-latest");
    }

    [Fact]
    public void 必须完整执行还原构建与测试三步()
    {
        var yaml = WorkflowText;

        // 少任何一步都会让 CI 变成形式主义：
        // 没 restore 会因缺少包而假失败，没 build 就测不到编译问题，没 test 等于没 CI。
        yaml.Should().Contain("dotnet restore");
        yaml.Should().Contain("dotnet build");
        yaml.Should().Contain("dotnet test");

        // 测试必须在构建之后用 --no-build 跑，否则等于把编译跑两遍，
        // 且 --no-build 能保证测的就是刚构建出来的产物。
        yaml.Should().Contain("--no-build");
    }

    [Fact]
    public void 应在推送main与打tag时触发()
    {
        var yaml = WorkflowText;

        // main 覆盖日常改动；v* tag 覆盖发布，保证 tag 指向的源码本身是绿的。
        yaml.Should().Contain("branches: [main]");
        yaml.Should().Contain("tags: ['v*']");
    }

    [Fact]
    public void 构建应使用Release配置()
    {
        // 与真正出包的配置一致：Debug 下有些问题（裁剪、ReadyToRun、单文件）根本不会暴露。
        WorkflowText.Should().Contain("--configuration Release");
    }

    [Fact]
    public void 失败时也应上传测试结果()
    {
        var yaml = WorkflowText;

        // 没有 if: always()，失败时上传步骤会被跳过——而那正是唯一需要 trx 的时刻。
        yaml.Should().Contain("if: always()");
        yaml.Should().Contain("trx");
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
