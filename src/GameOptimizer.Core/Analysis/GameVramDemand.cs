namespace GameOptimizer.Core.Analysis;

using System.Text.RegularExpressions;
using GameOptimizer.Core.Models;

/// <summary>
/// 单款游戏在「某画质 + 某分辨率」下的显存需求估算。
///
/// 为什么需要它：首页原先拿同一个全局占用率去套所有游戏，
/// 一款 2GB 显存的小游戏和一款 12GB 显存的大作会得到完全一样的结论。
/// 实际「显存够不够」是「这款游戏在这个画质下要多少」对「这台机器有多少」。
/// </summary>
public sealed record GameVramRequirement(
    string GameName,
    QualityPreset Quality,
    ResolutionPreset Resolution,
    double RequiredMb,
    bool IsEstimated,
    string Basis)
{
    public string SettingsText => $"{Resolution.Text()} {Quality.Text()}";
}

/// <summary>一台机器的显存预算。</summary>
public sealed record VramBudget(double BudgetMb, bool IsIntegrated, bool IsMeasured, string Source)
{
    /// <summary>
    /// 兜底预算。只在 DXGI 与注册表都读不到时使用，并标记为未实测，
    /// 界面必须显示「估算」——不能让用户把它当实测值。
    /// </summary>
    public static VramBudget Fallback(double physicalMemoryMb, bool isIntegrated) => new(
        isIntegrated ? physicalMemoryMb / 2 : 4096,
        isIntegrated,
        false,
        "缺省估算");

    public static VramBudget From(GameOptimizer.Core.Collection.GpuMemoryInfo.Snapshot snapshot) => new(
        snapshot.UsableBudgetMb,
        !snapshot.IsDiscrete,
        true,
        snapshot.Name);

    /// <summary>
    /// 探测本机显存预算：优先读 DXGI 的实测值，读不到才退回估算并标记未实测。
    /// 不探测就编一个「内存的一半」，正是这次要修的 bug 的根源。
    /// </summary>
    public static VramBudget Detect()
    {
        var snapshot = GameOptimizer.Core.Collection.GpuMemoryInfo.Current;
        if (snapshot is not null) return From(snapshot);

        var totalMb = GameOptimizer.Core.Collection.SystemMemoryInfo.TotalPhysicalMb;
        return Fallback(totalMb, isIntegrated: true);
    }

    /// <summary>界面展示用：如「8182 MB（Intel(R) Iris(R) Xe Graphics，实测）」。</summary>
    public string Describe() => IsMeasured
        ? $"{BudgetMb:F0} MB（{Source}，实测）"
        : $"{BudgetMb:F0} MB（{Source}）";
}

/// <summary>
/// 游戏显存需求表。数值为「1080p + 高画质」的量级，取偏保守的上沿，宁高勿低：
/// 低估会让用户以为够用，进游戏才掉帧。
///
/// 未收录的游戏按安装体积推断，并在界面标注为估算，不假装是实测。
/// </summary>
public static class GameVramDemand
{
    /// <summary>
    /// 需求占「当前剩余显存」≥ 这个比例判「显存紧张」。
    /// 留 15% 给游戏运行中的显存增长——显存吃满后驱动会向内存换页，直接表现为掉帧。
    /// </summary>
    public const double TightRatio = 0.85;

    /// <summary>未收录游戏的基础需求（MB），四档对应 Low/Medium/High/Ultra。</summary>
    private static readonly double[] UnknownBaseMb = [1800, 2600, 3400, 4400];

    /// <summary>已确认条目：四档需求（MB），索引同 <see cref="QualityPreset"/>。</summary>
    private static readonly (string[] Keywords, double[] Mb)[] Known =
    [
        // 三角洲行动：UE5 + 大尺寸贴图，2K 极致会吃到 10GB 以上
        (["三角洲", "deltaforce", "delta force"], [3500, 4500, 6000, 8000]),
        // 无畏契约：画面简单，显存需求很低
        (["无畏契约", "valorant"], [1000, 1400, 1800, 2200]),
        // 永劫无间：贴图与阴影吃显存，极致档明显抬升
        (["永劫无间", "naraka"], [3000, 4200, 5500, 7500]),
        // 赛博朋克 2077：极致 + 光追是 8GB 显卡的公开痛点
        (["赛博朋克", "cyberpunk"], [4200, 5500, 7200, 9500]),
        (["荒野大镖客", "red dead", "rdr2"], [3800, 5000, 6500, 8500]),
        (["黑神话", "wukong"], [4000, 5200, 6800, 9000]),
        (["绝地求生", "pubg", "battlegrounds"], [3500, 4500, 5800, 7000]),
        (["原神", "genshin"], [2200, 3000, 4000, 5200]),
        (["崩坏", "star rail"], [2000, 2600, 3400, 4200]),
        (["英雄联盟", "league of legends"], [1500, 2000, 2600, 3200]),
        (["反恐精英", "counter-strike", "cs2", "csgo"], [1800, 2400, 3200, 4000]),
        (["apex"], [3000, 3800, 4800, 6000]),
        (["艾尔登法环", "elden ring"], [3200, 4200, 5400, 6800]),
        (["地平线5", "forza horizon"], [3500, 4500, 6000, 7800]),
    ];

    /// <summary>分辨率相对 1080p 的显存倍数（按像素数近似）。</summary>
    internal static double ResolutionFactor(ResolutionPreset resolution) => resolution switch
    {
        ResolutionPreset.P1080 => 1.00,
        ResolutionPreset.P1440 => 1.40,
        ResolutionPreset.P4K => 2.20,
        _ => 1.00,
    };

    /// <summary>按名称查已收录的需求表；未收录返回 null。</summary>
    internal static double[]? LookUp(string gameName)
    {
        var normalized = Normalize(gameName);
        foreach (var (keywords, mb) in Known)
        {
            if (keywords.Any(k => normalized.Contains(Normalize(k), StringComparison.Ordinal)))
                return mb;
        }
        return null;
    }

    private static string Normalize(string text)
        => Regex.Replace(text.ToLowerInvariant(), @"[\s\-:：·・_/\\]+", "");

    /// <summary>
    /// 未收录游戏的推断：以 1080p 各档为基线，按安装体积微调。
    /// 体积是唯一能稳定拿到的信号（20GB 与 150GB 的游戏贴图量差距很大），
    /// 但信号很弱，所以只允许 ±35% 的浮动，并标记为估算。
    /// </summary>
    internal static double InferUnknown(double sizeGb, QualityPreset quality)
    {
        var baseMb = UnknownBaseMb[(int)quality];
        if (sizeGb <= 0) return baseMb;

        var scale = sizeGb switch
        {
            < 20 => 0.65 + sizeGb / 20 * 0.15,               // 0.65 ~ 0.80
            < 80 => 0.80 + (sizeGb - 20) / 60 * 0.35,        // 0.80 ~ 1.15
            _ => 1.15 + Math.Min(0.20, (sizeGb - 80) / 200), // 1.15 ~ 1.35
        };

        return baseMb * scale;
    }

    /// <summary>计算需求。已收录走需求表，未收录按体积推断并标记 IsEstimated。</summary>
    public static GameVramRequirement For(
        string gameName,
        double sizeGb,
        QualityPreset quality,
        ResolutionPreset resolution)
    {
        var table = LookUp(gameName);
        double baseMb;
        bool estimated;
        string basis;

        if (table is not null)
        {
            baseMb = table[(int)quality];
            estimated = false;
            basis = "已收录";
        }
        else
        {
            baseMb = InferUnknown(sizeGb, quality);
            estimated = true;
            basis = sizeGb > 0 ? $"按体积 {sizeGb:F0}GB 推算" : "按同类游戏推算";
        }

        var required = baseMb * ResolutionFactor(resolution);
        return new GameVramRequirement(gameName, quality, resolution, Math.Round(required), estimated, basis);
    }
}
