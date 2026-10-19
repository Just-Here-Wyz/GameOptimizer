namespace GameOptimizer.Core.Collection;

using System.Diagnostics;

/// <summary>
/// 找出当前正在运行的游戏进程，供帧时间采集使用。
///
/// 为什么不能只靠 exe 路径匹配：
/// 1. 带反作弊的游戏（三角洲行动的 ACE、无畏契约的 Vanguard）会阻止跨进程读取
///    MainModule / WMI ExecutablePath，实测返回空（Access denied）；
/// 2. WeGame/UE 游戏实际启动的是 DeltaForceClient-Win64-Shipping.exe，
///    而静态扫描找到的是根目录的 DeltaForceClient.exe，两者不是同一个文件。
///
/// 所以采用"多级判定"：先试路径精确匹配，再退化为进程名与游戏目录内所有 exe 名做前缀匹配。
/// </summary>
public static class GameProcessDetector
{
    /// <summary>在给定游戏目录中找出正在运行的进程。</summary>
    public static IReadOnlyList<RunningGame> Detect(IEnumerable<(string Name, string? ExecutablePath)> games)
    {
        var list = games.ToList();
        if (list.Count == 0) return [];

        // 索引 1：exe 完整路径 → 游戏名（最可靠，但反作弊游戏读不到）
        var byFullPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 索引 2：exe 去扩展名的主名 → 游戏名（如 DeltaForceClient）
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, exe) in list)
        {
            if (string.IsNullOrWhiteSpace(exe)) continue;
            byFullPath[exe] = name;

            var stem = Path.GetFileNameWithoutExtension(exe);
            if (stem.Length >= 4) byStem[stem] = name;
        }

        if (byFullPath.Count == 0 && byStem.Count == 0) return [];

        var found = new List<RunningGame>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (TryResolve(p, byFullPath, out var matched))
                {
                    found.Add(new RunningGame(matched, p.ProcessName, p.Id));
                }
                else if (TryResolveByStem(p.ProcessName, byStem, out var matchedByStem))
                {
                    found.Add(new RunningGame(matchedByStem, p.ProcessName, p.Id));
                }
            }
            catch { }
            finally { p.Dispose(); }
        }

        return found;
    }

    /// <summary>路径精确匹配。读不到路径时返回 false。</summary>
    private static bool TryResolve(Process p, Dictionary<string, string> byFullPath, out string gameName)
    {
        gameName = string.Empty;
        try
        {
            var path = p.MainModule?.FileName;
            if (path is not null && byFullPath.TryGetValue(path, out var name))
            {
                gameName = name;
                return true;
            }
        }
        catch
        {
            // 反作弊/不同账户会拒绝读取，退化为下一个判定方式
        }
        return false;
    }

    /// <summary>
    /// 按进程名与已知 exe 主名做前缀匹配。
    /// DeltaForceClient-Win64-Shipping 以 DeltaForceClient 开头 → 命中三角洲行动。
    /// 用长度下限避免 svchost 这类短名造成大面积误判。
    /// </summary>
    internal static bool TryResolveByStem(string processName, Dictionary<string, string> byStem, out string gameName)
    {
        gameName = string.Empty;
        if (string.IsNullOrWhiteSpace(processName)) return false;

        foreach (var (stem, name) in byStem)
        {
            if (processName.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            {
                gameName = name;
                return true;
            }
        }
        return false;
    }

    /// <summary>进程是否仍在运行。</summary>
    public static bool IsRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>正在运行的游戏。</summary>
public sealed record RunningGame(string DisplayName, string ProcessName, int ProcessId);
