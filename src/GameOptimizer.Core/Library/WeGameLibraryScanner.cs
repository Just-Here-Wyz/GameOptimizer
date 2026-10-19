namespace GameOptimizer.Core.Library;

using System.Text.RegularExpressions;

/// <summary>
/// 腾讯 WeGame 库扫描。
///
/// 目录结构（实测）：
///   D:\WeGame\wegame.exe                          客户端
///   D:\WeGameApps\rail_apps\无畏契约(2001715)\     游戏目录，名称(数字ID)
///   E:\WeGameApps\rail_apps\DeltaForce(2001918)\
///
/// 游戏主程序位置不固定（VALORANT 在 live\ 下，DeltaForce 在根目录），
/// 所以只能递归找，再按规则挑主程序。
/// </summary>
public sealed class WeGameLibraryScanner : IGameLibraryScanner
{
    public string PlatformName => "WeGame";

    /// <summary>WeGame 可能把游戏装到多个盘，这些是常见的根目录。</summary>
    private static readonly string[] DefaultRoots = ["D:\\WeGameApps", "E:\\WeGameApps", "C:\\WeGameApps"];

    public IReadOnlyList<GameEntry> Scan()
    {
        var roots = FindLibraryRoots();
        var results = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var railApps = Path.Combine(root, "rail_apps");
            if (!Directory.Exists(railApps)) continue;

            foreach (var dir in Directory.EnumerateDirectories(railApps))
            {
                var normalized = dir.TrimEnd('\\');
                if (!seen.Add(normalized)) continue;

                var entry = BuildEntry(dir);
                if (entry is not null) results.Add(entry);
            }
        }

        return results.OrderByDescending(g => g.SizeOnDiskGb).ToList();
    }

    /// <summary>找所有存在的 WeGame 库根目录（一台机器可能装在多个盘）。</summary>
    internal static IReadOnlyList<string> FindLibraryRoots()
    {
        var roots = new List<string>();

        // 优先从 WeGame 客户端的安装位置推断，再看默认盘符
        foreach (var clientRoot in new[] { @"D:\WeGame", @"E:\WeGame", @"C:\WeGame" })
        {
            if (Directory.Exists(clientRoot)) roots.Add(clientRoot + "Apps");
        }

        foreach (var candidate in DefaultRoots)
        {
            if (Directory.Exists(candidate)) roots.Add(candidate);
        }

        // 盘符可能被改过，扫一遍所有固定盘
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            var guess = Path.Combine(drive.Name, "WeGameApps");
            if (Directory.Exists(guess)) roots.Add(guess);
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>目录名形如 "无畏契约(2001715)"，拆出名称与 AppID。</summary>
    internal static (string Name, string AppId) ParseFolderName(string folderName)
    {
        var m = Regex.Match(folderName, @"^(?<name>.+?)[\(（](?<id>\d+)[\)）]$");
        if (m.Success)
            return (m.Groups["name"].Value.Trim(), m.Groups["id"].Value);

        return (folderName.Trim(), folderName);
    }

    internal static GameEntry? BuildEntry(string gameDir)
    {
        if (!Directory.Exists(gameDir)) return null;

        var folder = Path.GetFileName(gameDir.TrimEnd('\\'));
        var (folderName, appId) = ParseFolderName(folder);

        // 依赖库等非游戏目录
        if (folderName.Contains("dependency_shared", StringComparison.OrdinalIgnoreCase)) return null;
        if (folderName.Contains("common_redist", StringComparison.OrdinalIgnoreCase)) return null;

        // 目录名是英文工程名（DeltaForce），显示名走映射表（三角洲行动）
        var displayName = WeGameDisplayNames.Resolve(appId, folderName);

        // 挑主程序时用目录名匹配英文 exe，不能用中文显示名
        var exe = FindMainExecutable(gameDir, folderName);
        return new GameEntry(
            Name: displayName,
            AppId: appId,
            InstallPath: gameDir,
            ExecutablePath: exe,
            SizeOnDiskGb: Math.Round(DirectorySizeGb(gameDir), 2),
            Source: "WeGame");
    }

    /// <summary>
    /// 挑主程序。WeGame 游戏的可执行文件位置不固定，规则（按优先级）：
    /// 1. UE 游戏的真实主程序带 -Win64-Shipping 后缀（实测 490MB），
    ///    根目录那个同名的 DeltaForceClient.exe 只是外壳，必须优先选前者；
    /// 2. 路径中包含 Binaries\Win64 的优先（UE 的惯例位置）；
    /// 3. 排除卸载器、崩溃上报、启动器外壳、依赖安装程序、反作弊；
    /// 4. 文件名与游戏名相近的优先；
    /// 5. 同等条件下取体积最大的。
    /// </summary>
    internal static string? FindMainExecutable(string gameDir, string gameName)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.AllDirectories);
        }
        catch { return null; }

        var candidates = new List<FileInfo>();
        foreach (var f in files)
        {
            try
            {
                var info = new FileInfo(f);
                if (IsHelperExecutable(info.Name)) continue;
                // 反作弊目录下的 exe 一律排除：AntiCheatExpert\SGuard\x64\SGuard64.exe
                // 文件名本身不含 anti，只能按路径判定
                if (IsHelperPath(info.FullName)) continue;
                candidates.Add(info);
            }
            catch { }
        }

        if (candidates.Count == 0) return null;

        var tokens = NameTokens(gameName);
        return candidates
            .OrderByDescending(f => IsShippingExecutable(f.Name))
            .ThenByDescending(f => f.FullName.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => tokens.Any(t => f.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(f => f.Length)
            .First().FullName;
    }

    /// <summary>UE 打包的游戏真实主程序以 -Win64-Shipping.exe 结尾。</summary>
    internal static bool IsShippingExecutable(string fileName)
        => fileName.Contains("-Shipping.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>按目录排除反作弊、依赖、启动器外壳等非游戏主程序。</summary>
    internal static bool IsHelperPath(string fullPath)
    {
        string[] segments =
        [
            @"\AntiCheatExpert\", @"\ACE\", @"\SGuard\", @"\TenProtect\",
            @"\WeGameLauncher\", @"\Redist\", @"\TCLS\", @"\rail_files\",
            @"\tinydl\", @"\TinyDL\", @"\crash",
        ];

        foreach (var s in segments)
            if (fullPath.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>把中文/英文游戏名拆成可用于匹配英文 exe 名的片段。</summary>
    internal static IReadOnlyList<string> NameTokens(string gameName)
    {
        var tokens = new List<string>();
        foreach (var part in Regex.Split(gameName, @"[^\p{L}\p{N}]+"))
        {
            if (part.Length < 4) continue;          // 太短的片段容易误匹配
            tokens.Add(part);
        }
        return tokens;
    }

    private static readonly string[] HelperNames =
    [
        "unins", "卸载", "crash", "report", "setup", "install",
        "redist", "vcredist", "dxsetup", "launcher", "tcls", "tinydl",
        "repair", "update", "anti", "ace", "aclos"
    ];

    /// <summary>
    /// 判断是否为辅助程序。用归一化后的名字匹配：
    /// 原文件名常带版本号与点号（vc_redist.x64.exe、VC_redist.2019.exe），
    /// 直接 Contains("redistributable") 会漏掉。
    /// </summary>
    internal static bool IsHelperExecutable(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        // 去掉扩展名与版本号风格的点段，只保留主体
        var stem = Path.GetFileNameWithoutExtension(lower);
        var normalized = stem.Replace('.', '_');

        foreach (var h in HelperNames)
        {
            if (normalized.Contains(h) || lower.Contains(h)) return true;
        }
        return false;
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
}
