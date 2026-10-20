namespace GameOptimizer.Core.Library;

using System.Text.RegularExpressions;

/// <summary>
/// Steam 库扫描。只读解析 libraryfolders.vdf 与 appmanifest_*.acf，
/// 不写任何文件、不调用 Steam 进程。
/// </summary>
public sealed class SteamLibraryScanner : IGameLibraryScanner
{
    public string PlatformName => "Steam";

    public IReadOnlyList<GameEntry> Scan() => Scan(null);

    public static IReadOnlyList<GameEntry> Scan(string? steamRoot)
    {
        steamRoot ??= DetectSteamRoot();
        if (steamRoot is null || !Directory.Exists(steamRoot))
            return [];

        var libraries = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            libraries.AddRange(ParseLibraryPaths(File.ReadAllText(vdf)));

        var results = new List<GameEntry>();
        // vdf 里的路径常写作 d:/steam，与注册表给的 d:\steam 字符串不同但是同一个目录，
        // 必须规范化后再去重，否则每个游戏会被扫到两次。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in libraries)
        {
            var normalized = NormalizePath(lib);
            if (!seen.Add(normalized)) continue;
            var appsDir = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(appsDir)) continue;

            // manifest 路径 → GameEntry，用于兜底扫描时判断目录是否已被清单收录
            var byInstallPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var acf in Directory.EnumerateFiles(appsDir, "appmanifest_*.acf"))
            {
                var entry = ParseManifest(acf, lib);
                if (entry is null) continue;
                byInstallPath.Add(NormalizePath(entry.InstallPath));
                results.Add(entry);
            }

            // 兜底扫描（借鉴原工具做法）：steamapps\common 下没有 appmanifest 的
            // 游戏目录同样入库——实测 ChainedTogether 移库后只剩目录没有 acf，
            // 只靠清单会漏掉它。
            var commonDir = Path.Combine(appsDir, "common");
            if (!Directory.Exists(commonDir)) continue;

            foreach (var dir in Directory.EnumerateDirectories(commonDir))
            {
                if (byInstallPath.Contains(NormalizePath(dir))) continue;

                var entry = BuildFallbackEntry(dir);
                if (entry is not null) results.Add(entry);
            }
        }

        return results.OrderByDescending(g => g.SizeOnDiskGb).ToList();
    }

    /// <summary>
    /// 兜底扫描的单目录判定（借鉴原工具做法）：
    /// steamapps\common 下没有 appmanifest 的目录同样入库——但必须真的还有游戏本体。
    /// 实测：游戏卸载后 Steam 会留一个只剩云存档占位文件（steam_autocloud.vdf）的空壳目录，
    /// 里面没有任何 exe；这类卸载残留不能入库，否则用户看到「已卸载的游戏还在库里」。
    /// </summary>
    internal static GameEntry? BuildFallbackEntry(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        var dirName = Path.GetFileName(dir.TrimEnd('\\'));
        // 依赖库等非游戏目录
        if (NonGameFilter.IsNonGameDirectoryName(dirName)) return null;

        // 卸载残留：游戏本体 exe 已被删除，只剩云存档占位文件 → 不入库
        var exe = FindMainExecutable(dir);
        if (exe is null) return null;

        return new GameEntry(
            Name: SteamDisplayNames.Resolve("", dirName),
            AppId: "",
            InstallPath: dir,
            ExecutablePath: exe,
            SizeOnDiskGb: Math.Round(DirectorySizeGb(dir), 2),
            Source: "Steam");
    }

    private static double DirectorySizeGb(string path)
    {
        try
        {
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total / 1024.0 / 1024.0 / 1024.0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 统一盘符大小写与路径分隔符，并去掉尾部斜杠。
    /// vdf 里写作 d:/steam，注册表里写作 D:\Steam，两者是同一目录。
    /// </summary>
    internal static string NormalizePath(string path)
        => path.Replace('/', '\\').TrimEnd('\\');

    /// <summary>从注册表读 Steam 安装路径，读不到再试常见位置。</summary>
    public static string? DetectSteamRoot()
    {
        foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
        {
            var p = Microsoft.Win32.Registry.GetValue(key, "SteamPath", null) as string;
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) return p;
        }
        foreach (var guess in new[] { @"C:\Program Files (x86)\Steam", @"D:\Steam", @"E:\Steam" })
            if (Directory.Exists(guess)) return guess;
        return null;
    }

    /// <summary>libraryfolders.vdf 里所有 "path" 值。</summary>
    internal static IEnumerable<string> ParseLibraryPaths(string vdf)
    {
        foreach (Match m in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    internal static GameEntry? ParseManifest(string acfPath, string libraryRoot)
    {
        string text;
        try { text = File.ReadAllText(acfPath); }
        catch { return null; }

        var name = Field(text, "name");
        var appId = Field(text, "appid") ?? Path.GetFileNameWithoutExtension(acfPath).Replace("appmanifest_", "");
        var dir = Field(text, "installdir");
        var sizeBytes = long.TryParse(Field(text, "SizeOnDisk"), out var s) ? s : 0;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dir)) return null;

        var fullPath = Path.Combine(libraryRoot, "steamapps", "common", dir);
        return new GameEntry(
            Name: SteamDisplayNames.Resolve(appId, name),
            AppId: appId,
            InstallPath: fullPath,
            ExecutablePath: FindMainExecutable(fullPath),
            SizeOnDiskGb: Math.Round(sizeBytes / 1024.0 / 1024.0 / 1024.0, 2),
            Source: "Steam");
    }

    private static string? Field(string text, string key)
    {
        var m = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// 挑主程序：优先与目录同名的 exe，其次最大的 exe，排除常见的辅助/反作弊程序。
    /// </summary>
    internal static string? FindMainExecutable(string installPath)
    {
        if (!Directory.Exists(installPath)) return null;
        var candidates = Directory.EnumerateFiles(installPath, "*.exe", SearchOption.AllDirectories)
            .Where(f => !IsHelperExecutable(Path.GetFileName(f)))
            .Select(f => new FileInfo(f))
            .ToArray();

        if (candidates.Length == 0) return null;
        var dirName = new DirectoryInfo(installPath).Name;
        return candidates
            .OrderByDescending(f => string.Equals(Path.GetFileNameWithoutExtension(f.Name), dirName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.Length)
            .First().FullName;
    }

    private static readonly string[] HelperNames =
        ["crash", "report", "unins", "setup", "install", "redist", "vcredist", "dxsetup", "anticheat", "easyanti", "neac", "bootstrap"];

    internal static bool IsHelperExecutable(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return HelperNames.Any(lower.Contains);
    }
}
