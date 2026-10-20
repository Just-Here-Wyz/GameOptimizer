namespace GameOptimizer.Core.Library;

/// <summary>
/// Steam 游戏显示名映射。
///
/// 与 WeGame 同理：Steam 的 appmanifest 里写的是英文原名
/// （实测 NARAKA: BLADEPOINT），用户要看到中文名「永劫无间」。
/// 只收录经过确认的条目，未收录的保持原名。
/// </summary>
public static class SteamDisplayNames
{
    private static readonly Dictionary<string, string> ByAppId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1203220"] = "永劫无间", // NARAKA: BLADEPOINT
    };

    /// <summary>按 AppID 取中文显示名；未收录时退回原名。</summary>
    public static string Resolve(string appId, string originalName)
        => !string.IsNullOrWhiteSpace(appId) && ByAppId.TryGetValue(appId, out var name)
            ? name
            : originalName;

    /// <summary>该 ID 是否已有映射（供测试与诊断用）。</summary>
    public static bool HasMapping(string appId)
        => !string.IsNullOrWhiteSpace(appId) && ByAppId.ContainsKey(appId);

    /// <summary>当前收录的映射数量。</summary>
    public static int Count => ByAppId.Count;
}
