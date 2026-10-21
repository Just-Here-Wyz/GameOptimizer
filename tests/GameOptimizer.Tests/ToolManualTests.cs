namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Help;

/// <summary>
/// 设置页「使用说明书」的内容用例。
///
/// 界面各页的说明性文字已收进这里，因此这份内容必须覆盖每个页面，
/// 并且如实写明「按实时剩余显存判定」这个口径。
/// </summary>
public class ToolManualTests
{
    [Fact]
    public void 说明书不应为空()
    {
        ToolManual.Sections.Should().NotBeEmpty();
    }

    [Fact]
    public void 每节都必须有标题与正文()
    {
        foreach (var s in ToolManual.Sections)
        {
            s.Title.Should().NotBeNullOrWhiteSpace();
            s.Body.Should().NotBeNullOrWhiteSpace($"{s.Title} 缺正文");
        }
    }

    [Fact]
    public void 标题不应重复()
    {
        ToolManual.Sections.Select(s => s.Title).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 说明书应覆盖全部页面()
    {
        var titles = ToolManual.Sections.Select(s => s.Title).ToList();

        titles.Should().Contain(t => t.Contains("总览"));
        titles.Should().Contain(t => t.Contains("外设"));
        titles.Should().Contain(t => t.Contains("下载"));
        titles.Should().Contain(t => t.Contains("游戏模式"));
        titles.Should().Contain(t => t.Contains("游戏库"));
    }

    [Fact]
    public void 说明书应写明显存判定用的是实时剩余显存()
    {
        // 口径变了（总容量 → 实时剩余），说明书必须跟着说清楚，否则用户看不懂结论
        var all = string.Join("\n", ToolManual.Sections.Select(s => s.Body));

        all.Should().Contain("剩余显存");
        all.Should().Contain("1080p");
    }

    [Fact]
    public void 说明书应说明关闭窗口不退出程序()
    {
        var all = string.Join("\n", ToolManual.Sections.Select(s => s.Body));

        all.Should().Contain("托盘");
    }
}
