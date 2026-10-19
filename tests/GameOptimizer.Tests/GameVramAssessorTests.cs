namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Models;

/// <summary>
/// 按「单款游戏正常需要多少显存」判定的回归用例。
///
/// 修掉的 bug：首页所有游戏共用同一个全局显存占用率结论，
/// 于是 8GB 显卡上「无畏契约」和「三角洲行动」会同时显示「显存充足」或同时「紧张」，
/// 跟游戏本身的需求毫无关系。
/// </summary>
public class GameVramAssessorTests
{
    private static VramBudget Budget(double mb) => new(mb, IsIntegrated: false, IsMeasured: true, Source: "测试显卡");

    private static GameVramAssessment Assess(string game, double sizeGb, double budgetMb)
        => new GameVramAssessor().Assess(game, sizeGb, Budget(budgetMb));

    [Fact]
    public void 同一台机器上不同游戏应得到不同结论()
    {
        // 6GB 预算：无畏契约 1080p 高只要 1.8GB，永劫无间要 5.5GB
        const double budget = 6144;

        var light = Assess("无畏契约", 35, budget);
        var heavy = Assess("永劫无间", 60, budget);

        light.Verdict.Should().Be(GameVramVerdict.Fine);
        heavy.Verdict.Should().Be(GameVramVerdict.Tight);
        light.Ratio.Should().BeLessThan(heavy.Ratio);
    }

    [Fact]
    public void 大显存机器上同一款游戏应判为充足()
    {
        // 三角洲行动 1080p 高需 6000MB：6GB 预算紧张，16GB 预算充足
        Assess("三角洲行动", 90, 6144).Verdict.Should().Be(GameVramVerdict.Tight);
        Assess("三角洲行动", 90, 16384).Verdict.Should().Be(GameVramVerdict.Fine);
    }

    [Fact]
    public void 只有充足与紧张两种结论()
    {
        // 用户只需要知道够不够，不需要更多分层
        Enum.GetValues<GameVramVerdict>().Should().BeEquivalentTo(
        [
            GameVramVerdict.Unknown,
            GameVramVerdict.Fine,
            GameVramVerdict.Tight,
        ]);
    }

    [Fact]
    public void 判定依据是正常配置的1080p高画质()
    {
        var result = Assess("永劫无间", 60, 8192);

        result.Requirement.SettingsText.Should().Be("1080p 高");
        result.Requirement.Quality.Should().Be(QualityPreset.High);
        result.Requirement.Resolution.Should().Be(ResolutionPreset.P1080);
    }

    [Fact]
    public void 需求恰好占满预算时应判为紧张()
    {
        // ratio = 1.0 已经没有任何余量，绝不能报「充足」
        var need = GameVramDemand.For("永劫无间", 60, GameVramAssessor.NormalQuality, GameVramAssessor.NormalResolution);
        Assess("永劫无间", 60, need.RequiredMb).Verdict.Should().Be(GameVramVerdict.Tight);
    }

    [Fact]
    public void 余量充足时应判为充足()
    {
        // 永劫无间 5500MB / 8192MB = 67%
        Assess("永劫无间", 60, 8192).Verdict.Should().Be(GameVramVerdict.Fine);
    }

    [Fact]
    public void 读不到显存容量时应返回_Unknown_而不是猜结论()
    {
        var result = new GameVramAssessor().Assess(
            "三角洲行动", 90, new VramBudget(0, false, false, "缺省估算"));

        result.Verdict.Should().Be(GameVramVerdict.Unknown);
        result.Reason.Should().Contain("读不到");
    }

    [Fact]
    public void 结论里必须写明这款游戏要多少与本机有多少()
    {
        var result = Assess("永劫无间", 60, 8192);

        result.Reason.Should().Contain("5500");
        result.Reason.Should().Contain("8192");
        result.Reason.Should().Contain("1080p 高");
    }

    [Fact]
    public void 未收录游戏应在依据里标明是估算值()
    {
        var result = Assess("某个没人收录的独立游戏", 12, 8192);

        result.Requirement.IsEstimated.Should().BeTrue();
        result.Reason.Should().Contain("推算");
    }

    [Fact]
    public void 已收录游戏不应标成估算()
    {
        var result = Assess("永劫无间", 60, 8192);

        result.Requirement.IsEstimated.Should().BeFalse();
        result.Requirement.Basis.Should().Be("已收录");
    }

    [Fact]
    public void 估算的小游戏需求应低于估算的大作()
    {
        var small = Assess("小型独立游戏", 8, 8192);
        var large = Assess("大型 3A 游戏", 140, 8192);

        small.Requirement.RequiredMb.Should().BeLessThan(large.Requirement.RequiredMb);
    }
}
