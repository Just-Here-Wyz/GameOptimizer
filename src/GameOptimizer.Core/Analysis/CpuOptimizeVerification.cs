namespace GameOptimizer.Core.Analysis;

/// <summary>
/// 快速优化的「回读验证」文案（纯逻辑）。
///
/// 前后对比必须用**同一口径、同样长度的窗口**，并把窗口长度写进结论里；
/// 验证不成立时如实说「无法对比」，不拿优化前的数字充数。
/// </summary>
public static class CpuOptimizeVerification
{
    /// <summary>前后对比各自的观察窗口（秒）。</summary>
    public const double CompareWindowSeconds = 5;

    /// <summary>窗口内至少要有几帧有效数据才谈得上平均。</summary>
    public const int MinimumSamples = 3;

    /// <summary>口径说明，界面必须与结论一起展示。</summary>
    public const string CaliberNote =
        "口径：总 CPU（PDH \\Processor\\% Processor Time\\_Total），优化前后各取 5 秒平均；" +
        "CPU 占用受其他程序影响，该变化不代表游戏帧率提升。";

    /// <summary>
    /// 生成前后对比结论。
    /// </summary>
    /// <param name="before">执行前的窗口统计。</param>
    /// <param name="after">执行后的窗口统计（同长度）。</param>
    public static string DescribeComparison(CpuWindowStats before, CpuWindowStats after)
    {
        if (before.AveragePercent is not { } b || before.ValidSamples < MinimumSamples)
            return $"无法对比：执行前 {CompareWindowSeconds:F0} 秒内只有 {before.ValidSamples} 帧有效数据" +
                   $"（至少需要 {MinimumSamples} 帧）。";

        if (after.AveragePercent is not { } a || after.ValidSamples < MinimumSamples)
            return $"无法对比：执行后 {CompareWindowSeconds:F0} 秒内只有 {after.ValidSamples} 帧有效数据" +
                   $"（至少需要 {MinimumSamples} 帧），结论不作数。";

        var delta = a - b;
        var direction = delta <= -0.5 ? "下降" : delta >= 0.5 ? "上升" : "基本持平";

        return $"总 CPU：执行前 {b:F0}%（{before.ValidSamples} 帧平均）→ 执行后 {a:F0}%" +
               $"（{after.ValidSamples} 帧平均），{direction} {Math.Abs(delta):F0} 个百分点。";
    }
}
