namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Models;

/// <summary>
/// 按「单款游戏正常需要多少显存」判定，且分母是本机**实时剩余显存**。
///
/// 修掉的 bug：判定原先拿显卡总容量当分母，别的程序把显存吃满时
/// 库里仍然全绿报「显存充足」，进游戏才发现卡。
/// </summary>
public class GameVramAssessorTests
{
    private static VramBudget Budget(double mb) => new(mb, IsIntegrated: false, IsMeasured: true, Source: "测试显卡");

    /// <param name="freeMb">剩余显存。缺省等于总容量（刚开机、无占用的场景）。</param>
    private static GameVramAssessment Assess(string game, double sizeGb, double budgetMb, double? freeMb = null)
        => new GameVramAssessor().Assess(game, sizeGb, Budget(budgetMb), freeMb ?? budgetMb);

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
    public void 需求恰好占满剩余显存时应判为紧张()
    {
        // ratio = 1.0 已经没有任何余量，绝不能报「充足」
        var need = GameVramDemand.For("永劫无间", 60, GameVramAssessor.NormalQuality, GameVramAssessor.NormalResolution);
        Assess("永劫无间", 60, 16384, need.RequiredMb).Verdict.Should().Be(GameVramVerdict.Tight);
    }

    [Fact]
    public void 余量充足时应判为充足()
    {
        // 永劫无间 5500MB / 8192MB = 67%
        Assess("永劫无间", 60, 8192).Verdict.Should().Be(GameVramVerdict.Fine);
    }

    [Fact]
    public void 总容量够但剩余显存不够时应判为紧张()
    {
        // 16GB 显卡跑永劫无间本来绰绰有余（5500 / 16384 = 34%），
        // 但已被别的程序占到只剩 3GB，按剩余算就是不够
        Assess("永劫无间", 60, 16384, freeMb: 3072).Verdict.Should().Be(GameVramVerdict.Tight);
    }

    [Fact]
    public void 剩余显存不够时依据里必须写剩余而不是总容量()
    {
        var result = Assess("永劫无间", 60, 16384, freeMb: 3072);

        result.FreeMb.Should().Be(3072);
        result.BudgetMb.Should().Be(16384);
        result.Reason.Should().Contain("剩余 3072");
        // 需求占剩余的比例：5500 / 3072 = 179%
        result.Ratio.Should().BeGreaterThan(1);
    }

    [Fact]
    public void 剩余显存为0时应判为紧张而不是无法判定()
    {
        // 显存被占满，容量读到了但一点余量都没有：这是明确的「不够」，不是「不知道」
        var result = Assess("无畏契约", 35, 8192, freeMb: 0);

        result.Verdict.Should().Be(GameVramVerdict.Tight);
        result.FreeMb.Should().Be(0);
        result.Reason.Should().Contain("没有剩余显存");
    }

    [Fact]
    public void 剩余显存读数越界时按总容量封顶()
    {
        // PDH 偶发读数异常：剩余不可能多于总容量，否则依据会自相矛盾
        var result = Assess("无畏契约", 35, 8192, freeMb: 99999);

        result.FreeMb.Should().Be(8192);
        result.Reason.Should().NotContain("99999");
    }

    [Fact]
    public void 读不到显存容量时应返回_Unknown_而不是猜结论()
    {
        var result = new GameVramAssessor().Assess(
            "三角洲行动", 90, new VramBudget(0, false, false, "缺省估算"), freeMb: 0);

        result.Verdict.Should().Be(GameVramVerdict.Unknown);
        result.Reason.Should().Contain("读不到");
    }

    [Fact]
    public void 结论里必须写明这款游戏要多少与当前剩余多少()
    {
        var result = Assess("永劫无间", 60, 16384, freeMb: 8192);

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
