namespace GameOptimizer.Core.Analysis;

using GameOptimizer.Core.Models;

/// <summary>单款游戏的显存结论。只有「充足 / 紧张」两种，用户不需要理解更多分层。</summary>
public enum GameVramVerdict
{
    /// <summary>数据不足，无法判定（读不到显卡显存）。</summary>
    Unknown = 0,
    /// <summary>按当前剩余显存跑得起这款游戏。</summary>
    Fine = 1,
    /// <summary>当前剩余显存不够跑这款游戏。</summary>
    Tight = 2,
}

/// <summary>单款游戏的显存结论，含需求、总容量、实时剩余与依据。</summary>
public sealed record GameVramAssessment(
    GameVramVerdict Verdict,
    GameVramRequirement Requirement,
    double BudgetMb,
    double FreeMb,
    string Reason)
{
    /// <summary>需求占「当前剩余显存」的比例。</summary>
    public double Ratio => FreeMb > 0 ? Requirement.RequiredMb / FreeMb : 0;
}

/// <summary>
/// 按「这款游戏自己要多少显存」对比「本机现在还剩多少显存」判定。
///
/// 判定用「正常配置」：1080p + 高画质。拿它算出来的需求才代表「这款游戏正常要多少显存」。
/// 分母必须是实时剩余显存：总容量再大，被别的程序占满时这款游戏一样跑不动。
/// </summary>
public sealed class GameVramAssessor
{
    /// <summary>正常配置：1080p + 高画质。</summary>
    public const QualityPreset NormalQuality = QualityPreset.High;
    public const ResolutionPreset NormalResolution = ResolutionPreset.P1080;

    /// <param name="freeMb">本机当前剩余显存（总容量 − 当前占用）。</param>
    public GameVramAssessment Assess(string gameName, double sizeGb, VramBudget budget, double freeMb)
        => Assess(gameName, sizeGb, NormalQuality, NormalResolution, budget, freeMb);

    internal GameVramAssessment Assess(
        string gameName,
        double sizeGb,
        QualityPreset quality,
        ResolutionPreset resolution,
        VramBudget budget,
        double freeMb)
    {
        var requirement = GameVramDemand.For(gameName, sizeGb, quality, resolution);

        if (budget.BudgetMb <= 0)
        {
            return new GameVramAssessment(
                GameVramVerdict.Unknown, requirement, 0, 0,
                "读不到显卡的显存容量，无法判定。");
        }

        // 剩余显存不可能多于总容量：PDH 读数偶发越界时按容量封顶，
        // 否则会算出「剩余 9000 MB / 共 8192 MB」这种自相矛盾的依据。
        var free = Math.Clamp(freeMb, 0, budget.BudgetMb);

        if (free <= 0)
        {
            return new GameVramAssessment(
                GameVramVerdict.Tight, requirement, budget.BudgetMb, 0,
                $"{requirement.SettingsText} 约需 {requirement.RequiredMb:F0} MB，当前没有剩余显存。");
        }

        var ratio = requirement.RequiredMb / free;
        var verdict = ratio >= GameVramDemand.TightRatio
            ? GameVramVerdict.Tight
            : GameVramVerdict.Fine;

        // 未收录的游戏必须如实说明需求是估出来的，不能让用户当成实测
        var basis = requirement.IsEstimated ? $"（{requirement.Basis}）" : "";

        return new GameVramAssessment(
            verdict,
            requirement,
            budget.BudgetMb,
            free,
            $"{requirement.SettingsText} 约需 {requirement.RequiredMb:F0} MB，" +
            $"当前剩余 {free:F0} MB，占剩余 {ratio:P0}{basis}");
    }
}
