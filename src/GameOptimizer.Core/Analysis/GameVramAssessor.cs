namespace GameOptimizer.Core.Analysis;

using GameOptimizer.Core.Models;

/// <summary>单款游戏的显存结论。只有「充足 / 紧张」两种，用户不需要理解更多分层。</summary>
public enum GameVramVerdict
{
    /// <summary>数据不足，无法判定（读不到显卡显存）。</summary>
    Unknown = 0,
    /// <summary>这款游戏按正常画质跑得起。</summary>
    Fine = 1,
    /// <summary>这款游戏会吃掉大部分显存，余量不够。</summary>
    Tight = 2,
}

/// <summary>单款游戏的显存结论，含需求、预算与依据。</summary>
public sealed record GameVramAssessment(
    GameVramVerdict Verdict,
    GameVramRequirement Requirement,
    double BudgetMb,
    string Reason)
{
    /// <summary>需求占预算的比例。</summary>
    public double Ratio => BudgetMb > 0 ? Requirement.RequiredMb / BudgetMb : 0;
}

/// <summary>
/// 按「这款游戏自己要多少显存」判定，而不是拿全局占用率一刀切。
///
/// 判定用「正常配置」：1080p + 高画质。这是绝大多数玩家的默认设置，
/// 拿它算出来的需求才代表「这款游戏正常要多少显存」。
/// </summary>
public sealed class GameVramAssessor
{
    /// <summary>正常配置：1080p + 高画质。</summary>
    public const QualityPreset NormalQuality = QualityPreset.High;
    public const ResolutionPreset NormalResolution = ResolutionPreset.P1080;

    public GameVramAssessment Assess(string gameName, double sizeGb, VramBudget budget)
        => Assess(gameName, sizeGb, NormalQuality, NormalResolution, budget);

    internal GameVramAssessment Assess(
        string gameName,
        double sizeGb,
        QualityPreset quality,
        ResolutionPreset resolution,
        VramBudget budget)
    {
        var requirement = GameVramDemand.For(gameName, sizeGb, quality, resolution);

        if (budget.BudgetMb <= 0)
        {
            return new GameVramAssessment(
                GameVramVerdict.Unknown, requirement, 0,
                "读不到显卡的显存容量，无法判定。");
        }

        var ratio = requirement.RequiredMb / budget.BudgetMb;
        var verdict = ratio >= GameVramDemand.TightRatio
            ? GameVramVerdict.Tight
            : GameVramVerdict.Fine;

        // 未收录的游戏必须如实说明需求是估出来的，不能让用户当成实测
        var basis = requirement.IsEstimated ? $"（{requirement.Basis}）" : "";

        return new GameVramAssessment(
            verdict,
            requirement,
            budget.BudgetMb,
            $"{requirement.SettingsText} 约需 {requirement.RequiredMb:F0} MB，" +
            $"本机显存 {budget.BudgetMb:F0} MB，占用 {ratio:P0}{basis}");
    }
}
