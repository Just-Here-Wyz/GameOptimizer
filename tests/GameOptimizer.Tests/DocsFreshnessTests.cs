namespace GameOptimizer.Tests;

using FluentAssertions;
using System.Text.RegularExpressions;

/// <summary>
/// 文档新鲜度守卫。
///
/// 起因是一次真实的误判：`docs/00-项目总览.md` 里写着「尚未打包发布；最近发布 v2.4.1」，
/// 而实际的 v2.5.2 早已打好 Release 并挂了附件。`AGENTS.md` 要求接手的人和 AI Agent
/// 「动任何代码之前先读 docs/00」，于是每一个新会话读到的第一个结论就是错的，
/// 反复得出「GitHub 上没有最新版本」的判断。
///
/// 教训不是「记得同步这句话」，而是**会过期的事实不该写成散文**：
///   - 发布状态只认 README 顶部「版本状态」行（由 tools/bump-version.ps1 按 Git tag 自动填）
///     与 GitHub Releases 页面；
///   - 用例数只认 README 工程概况那一行，其余文档引用它、不许各写各的。
///
/// 这两条现在由下面的用例钉住。
/// </summary>
public class DocsFreshnessTests
{
    /// <summary>当前用例数的唯一真源：README 工程概况表里的「N 个 xUnit 用例」。</summary>
    private static int DocumentedCaseCount()
    {
        var match = Regex.Match(Read("README.md"), @"(\d+)\s*个 xUnit 用例");
        match.Success.Should().BeTrue("README 工程概况里必须有「N 个 xUnit 用例」这一行，它是用例数的唯一真源");
        return int.Parse(match.Groups[1].Value);
    }

    [Theory]
    [InlineData("docs/00-项目总览.md")]
    [InlineData("docs/01-架构与模块.md")]
    [InlineData("docs/05-开发指南.md")]
    [InlineData("CONTRIBUTING.md")]
    public void 当前文档里写的用例数必须与README一致(string relativePath)
    {
        // 这些文件描述的是「现在」，所以里面的用例数必须等于真源。
        // docs/06 是变更记录、docs/07 是某四天的复盘，里面 574、886、894 都是历史快照，
        // 不该被这条规则约束 —— 强行统一会把历史记录改错。
        var expected = DocumentedCaseCount();
        var text = Read(relativePath);

        var numbers = new List<int>();
        foreach (Match m in Regex.Matches(text, @"(\d+)\s*例")) numbers.Add(int.Parse(m.Groups[1].Value));
        foreach (Match m in Regex.Matches(text, @"(\d+)\s*个用例")) numbers.Add(int.Parse(m.Groups[1].Value));
        foreach (Match m in Regex.Matches(text, @"(\d+)\s*个[）)]")) numbers.Add(int.Parse(m.Groups[1].Value));

        numbers.Should().NotBeEmpty($"{relativePath} 里应当写明当前用例数，方便接手的人判断测试规模");
        numbers.Should().OnlyContain(n => n == expected,
            $"{relativePath} 写的是「现在」的用例数，必须等于 README 里的 {expected}；" +
            "历史数字请留在 docs/06 与 docs/07");
    }

    [Fact]
    public void 文档不得再写死发布状态()
    {
        // 「尚未打包发布 / 最近发布 vX.Y.Z」是典型的会过期的事实：
        // 每次发版都要记得改，忘一次就误导所有读文档的人。
        // 现在只允许写指针（去看 README 版本状态行或 Releases 页面）。
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "docs"), "*.md"))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            if (text.Contains("尚未打包发布") || text.Contains("未打包发布"))
                offenders.Add($"{name}: 写了「尚未打包发布」");

            if (Regex.IsMatch(text, @"最近(正式)?发布\s*v\d"))
                offenders.Add($"{name}: 写了「最近发布 vX.Y.Z」");
        }

        offenders.Should().BeEmpty(
            "发布状态会过期，文档里只放指针。判断远程有没有最新版，去看 Releases 页里的 tag 与附件");
    }

    [Fact]
    public void 总览里的源码版本应与props一致()
    {
        // docs/00 是接手的人读的第一份文档，这里的版本号必须与唯一真源一致
        var declared = Regex.Match(Read("Directory.Build.props"), @"<Version>([\d.]+)</Version>");
        declared.Success.Should().BeTrue("Directory.Build.props 里必须有 <Version>");

        Read("docs/00-项目总览.md")
            .Should().Contain($"源码版本 **v{declared.Groups[1].Value}**");
    }

    [Fact]
    public void 总览里的目录树注释应与实际目录相符()
    {
        // docs/00 的目录树把 src/ 各项目职责写了一遍；项目改名或移动后这里最容易腐坏。
        // 只校验「列出的目录真实存在」，不校验描述文字 —— 描述靠人读，路径靠机器查。
        var text = Read("docs/00-项目总览.md");
        var root = RepoRoot();

        var listed = Regex.Matches(text, @"(?:src|tests)/GameOptimizer\.[A-Za-z]+/")
            .Cast<Match>()
            .Select(m => m.Value.TrimEnd('/'))
            .Distinct()
            .ToList();

        var checkedAny = false;
        foreach (var dir in listed)
        {
            Directory.Exists(Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar)))
                .Should().BeTrue($"docs/00 的目录树列出了 {dir}，它必须真实存在");
            checkedAny = true;
        }

        checkedAny.Should().BeTrue("docs/00 里应当有 src/tests 的目录树");
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

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
