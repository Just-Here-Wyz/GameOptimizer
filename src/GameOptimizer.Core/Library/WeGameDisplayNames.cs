namespace GameOptimizer.Core.Library;

/// <summary>
/// WeGame 游戏显示名映射。
///
/// 为什么需要这张表：WeGame 的安装目录名是英文工程名（DeltaForce），
/// 而本地配置（package_info.dat、commonconfig.cfg）全部加密，exe 也没有版本元数据，
/// 无法在运行时可靠地拿到中文显示名。所以用一张小映射表兜底。
///
/// 只收录经过实机确认的条目——猜出来的译名比显示英文名更糟。
/// 新游戏只需在这里加一行；未收录的保持目录名。
/// </summary>
public static class WeGameDisplayNames
{
    private static readonly Dictionary<string, string> ByAppId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2001918"] = "三角洲行动",   // DeltaForce
        ["2001715"] = "无畏契约",     // VALORANT
    };

    /// <summary>
    /// 取显示名。有映射用映射，否则回退到目录名。
    /// </summary>
    public static string Resolve(string appId, string folderName)
        => !string.IsNullOrWhiteSpace(appId) && ByAppId.TryGetValue(appId, out var name)
            ? name
            : folderName;

    /// <summary>该 ID 是否已有映射（供测试与诊断用）。</summary>
    public static bool HasMapping(string appId)
        => !string.IsNullOrWhiteSpace(appId) && ByAppId.ContainsKey(appId);

    /// <summary>当前收录的映射数量。</summary>
    public static int Count => ByAppId.Count;
}
