namespace GameOptimizer.Core.Library;

/// <summary>一个被扫描到的游戏。多个平台扫描器共用此结构。</summary>
public sealed record GameEntry(
    string Name,
    string AppId,
    string InstallPath,
    string? ExecutablePath,
    double SizeOnDiskGb,
    string Source);

/// <summary>游戏扫描的统一入口，聚合各平台。</summary>
public static class GameLibrary
{
    public static IReadOnlyList<GameEntry> ScanAll(IEnumerable<IGameLibraryScanner>? scanners = null)
    {
        scanners ??= [new SteamLibraryScanner(), new WeGameLibraryScanner()];

        var results = new List<GameEntry>();
        foreach (var scanner in scanners)
        {
            try { results.AddRange(scanner.Scan()); }
            catch { /* 单个平台异常不应拖垮整个扫描 */ }
        }

        return results
            .GroupBy(g => NormalizePath(g.InstallPath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(g => g.SizeOnDiskGb)
            .ToList();
    }

    /// <summary>
    /// 路径规范化后再比较。不同平台上报的路径可能带尾部反斜杠或用了正斜杠，
    /// 不规范化会把同一个游戏当成两个（Steam 那次重复扫描就是这个问题）。
    /// </summary>
    internal static string NormalizePath(string path)
        => path.Replace('/', '\\').TrimEnd('\\');
}

/// <summary>平台扫描器接口。新增平台只需实现这个即可被聚合。</summary>
public interface IGameLibraryScanner
{
    string PlatformName { get; }
    IReadOnlyList<GameEntry> Scan();
}

/// <summary>
/// 需要从游戏列表中剔除的非游戏项目。
/// Wallpaper Engine 是动态壁纸，Steam 会把它列为应用，但它不是游戏；
/// 各类工具软件同理。
/// </summary>
public static class NonGameFilter
{
    /// <summary>按名称精确/包含匹配的黑名单。</summary>
    private static readonly string[] ByName =
    [
        "wallpaper engine",
        "steamworks common redistributables",
        "steamvr",
        "steam linux runtime",
        "steam linux runtime - soldier",
        "steam linux runtime - sniper",
        "proton",
        "steam client",
        "3dmark",
        "vrchat" // 社交应用，非游戏（如需保留可移除此行）
    ];

    /// <summary>按可执行文件名匹配的黑名单。</summary>
    private static readonly string[] ByExecutable =
    [
        "wallpaper32.exe",
        "wallpaper64.exe",
        "ui32.exe",
        "ui64.exe",
        "steam.exe",
        "steamwebhelper.exe"
    ];

    public static bool IsNonGame(GameEntry entry)
    {
        var name = entry.Name.Trim();

        foreach (var blocked in ByName)
        {
            if (name.Equals(blocked, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains(blocked, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (entry.ExecutablePath is not null)
        {
            var exe = Path.GetFileName(entry.ExecutablePath);
            if (ByExecutable.Contains(exe, StringComparer.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public static IReadOnlyList<GameEntry> Apply(IEnumerable<GameEntry> entries)
        => entries.Where(e => !IsNonGame(e)).ToList();
}
