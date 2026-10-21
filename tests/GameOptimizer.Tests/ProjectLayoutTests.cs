namespace GameOptimizer.Tests;

using FluentAssertions;

/// <summary>
/// 目录布局守卫。
///
/// `docs/01-架构与模块.md` 的「分层」一节就是项目的布局规划：Core 下只有那几层。
/// 新代码总想给自己开个新目录（说明书、帮助文案、工具类最容易这样），
/// 一旦开口子，分层文档就再也对不上实际结构。这条用例把规划钉死。
/// </summary>
public class ProjectLayoutTests
{
    private static readonly string[] CoreFolders =
        ["Analysis", "Collection", "Download", "GameMode", "Library", "Models", "Navigation", "Peripherals"];

    [Fact]
    public void Core顶层目录应与布局规划一致()
    {
        var core = Path.Combine(RepoRoot(), "src", "GameOptimizer.Core");

        var actual = Directory.EnumerateDirectories(core)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name is not ("bin" or "obj"))
            // 点开头的是本地产物目录（.artifacts 等，已在 .gitignore 里），不算分层
            .Where(name => !name!.StartsWith('.'))
            .ToArray();

        actual.Should().BeSubsetOf(CoreFolders,
            "新增职责应归入既有分层；确需新目录时，先改 docs/01 的布局规划与这条用例");
    }

    [Fact]
    public void Core不应引用WPF()
    {
        // 「所有能测的逻辑都在 Core」的前提是 Core 可脱离界面运行
        var core = Path.Combine(RepoRoot(), "src", "GameOptimizer.Core");

        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            var text = File.ReadAllText(file);
            text.Should().NotContain("using System.Windows", $"{Path.GetFileName(file)} 不能引用 WPF");
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
