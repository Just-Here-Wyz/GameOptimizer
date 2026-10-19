namespace GameOptimizer.Core.Collection;

using System.Diagnostics;

/// <summary>
/// 找出当前正在运行的游戏进程，供帧时间采集使用。
///
/// 判定依据：进程的可执行文件路径落在已扫描到的游戏目录里。
/// 不靠进程名猜测——WeGame/Steam 的游戏名与 exe 名并不总是一致
/// （三角洲行动的主程序叫 DeltaForceClient.exe）。
/// </summary>
public static class GameProcessDetector
{
    /// <summary>在给定游戏目录中找出正在运行的进程。</summary>
    public static IReadOnlyList<RunningGame> Detect(IEnumerable<(string Name, string? ExecutablePath)> games)
    {
        var byExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, exe) in games)
        {
            if (string.IsNullOrWhiteSpace(exe)) continue;
            byExe[exe] = name;
        }
        if (byExe.Count == 0) return [];

        var found = new List<RunningGame>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (path is null) continue;

                if (byExe.TryGetValue(path, out var gameName))
                {
                    found.Add(new RunningGame(gameName, Path.GetFileNameWithoutExtension(path), p.Id));
                }
            }
            catch
            {
                // 不同账户的进程读不到 MainModule，忽略
            }
            finally
            {
                p.Dispose();
            }
        }

        return found;
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
