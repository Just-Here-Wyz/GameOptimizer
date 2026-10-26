namespace GameOptimizer.Core.GameMode;

using System.Diagnostics;
using GameOptimizer.Core.Library;

/// <summary>
/// 某一项为什么进不了恢复队列。<see cref="None"/> 表示可以恢复。
///
/// 单独建枚举而不是只留一句文案，是因为「跳过」这一侧要能被测试钉住：
/// 界面显示「跳过」时，界面上写的理由必须来自同一处判据，
/// 否则迟早出现「界面说系统关键、实际却尝试启动」这种自相矛盾。
/// </summary>
public enum RestoreSkipKind
{
    /// <summary>可以恢复。</summary>
    None = 0,

    /// <summary>快照里根本没记下可执行文件路径。</summary>
    MissingLaunchPath = 1,

    /// <summary>文件已被删除 / 移动，按原路径拉起只会失败。</summary>
    MissingFile = 2,

    /// <summary>系统关键进程（系统目录组件、外壳、安全软件、本程序），不通过恢复队列启动。</summary>
    SystemCritical = 3,

    /// <summary>应用商店应用（WindowsApps / MSIX），包目录里的 exe 不能当普通进程启动。</summary>
    PackagedApp = 4,

    /// <summary>同名进程已经在运行，再拉起只会多开一份。</summary>
    AlreadyRunning = 5,

    /// <summary>与列表中另一项指向同一个可执行文件。</summary>
    Duplicate = 6,
}

/// <summary>
/// 一条待恢复项在界面上的完整描述：应用名 / exe 名 / 发布者 / 简短说明 / 恢复资格与原因。
///
/// 所有字段都只来自快照记录与 exe 版本资源，读不到就写「未知」，
/// <b>不猜、不补</b>——用户要据此决定「这个程序要不要重新打开」。
/// </summary>
/// <param name="Record">执行侧的真实记录。恢复成功/失败会写回它，界面与统计都读这一份。</param>
/// <param name="LaunchPath">可执行文件完整路径；仅用于启动与图标，界面上不直接展示全路径。</param>
public sealed record ProcessRestoreCandidate(
    RestoredProcess Record,
    string ProcessName,
    string AppName,
    string ExecutableName,
    string? Publisher,
    string? Description,
    string? LaunchPath,
    RestoreSkipKind SkipKind,
    string Reason)
{
    /// <summary>是否可以进入恢复队列。</summary>
    public bool Eligible => SkipKind == RestoreSkipKind.None;

    /// <summary>卡片右侧的资格标签。</summary>
    public string BadgeText => Eligible ? "可恢复" : "跳过";
}

/// <summary>
/// 判断恢复资格时要用到的外部事实。全部可注入，测试因此不需要真的碰文件系统或进程表。
///
/// 默认实现读的是本机真实状态：文件是否存在、同名进程是否在跑、exe 版本资源。
/// </summary>
public sealed class ProcessRestoreEnvironment
{
    public ProcessRestoreEnvironment(
        Func<string, bool>? fileExists = null,
        Func<string, bool>? isProcessRunning = null,
        Func<string, ProcessFileMetadata>? readMetadata = null,
        string? systemRoot = null,
        string? packageRoot = null)
    {
        FileExists = fileExists ?? File.Exists;
        IsProcessRunning = isProcessRunning ?? DefaultIsProcessRunning;
        ReadMetadata = readMetadata ?? ProcessFileMetadataReader.Read;
        SystemRoot = systemRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        PackageRoot = packageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
    }

    /// <summary>本机真实环境。</summary>
    public static ProcessRestoreEnvironment Default { get; } = new();

    public Func<string, bool> FileExists { get; }
    public Func<string, bool> IsProcessRunning { get; }
    public Func<string, ProcessFileMetadata> ReadMetadata { get; }

    /// <summary>Windows 目录，用于识别系统组件。</summary>
    public string SystemRoot { get; }

    /// <summary>应用商店应用的包目录。</summary>
    public string PackageRoot { get; }

    /// <summary>
    /// 同名进程是否还在运行。
    ///
    /// 只按进程名判断（与结束进程时的口径一致）：用户可能已经自己把程序打开过一次，
    /// 这时再「恢复」等于多开一份。读不到进程表时按「没在跑」处理——
    /// 宁可尝试一次并如实报告结果，也不要因为一次读取失败就把整份选择作废。
    /// </summary>
    private static bool DefaultIsProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch { return false; }

        try
        {
            foreach (var p in processes)
            {
                try
                {
                    if (!p.HasExited) return true;
                }
                catch
                {
                    // 拿不到状态：按「还在跑」算，避免多开
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }
}

/// <summary>
/// 把「游戏模式结束时记下的进程记录」翻译成「可勾选的恢复候选」。
///
/// 这一层是恢复流程的安全闸门：<b>快照里有记录 ≠ 可以启动</b>。
/// 五道检查依次放行（路径缺失 → 包应用 → 系统组件 → 文件不存在 → 重复 / 已运行），
/// 任何一项过不去就标记成跳过并写清原因，绝不猜一个命令去拉起来。
/// </summary>
public static class ProcessRestorePlanner
{
    /// <summary>可以恢复时的说明。有理由的「可以」，用户才敢勾。</summary>
    public const string RestorableReason = "可按原始路径启动";

    public static IReadOnlyList<ProcessRestoreCandidate> Build(
        IEnumerable<RestoredProcess>? records,
        ProcessRestoreEnvironment? environment = null)
    {
        var env = environment ?? ProcessRestoreEnvironment.Default;
        var result = new List<ProcessRestoreCandidate>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records ?? [])
        {
            if (record is null) continue;

            var path = string.IsNullOrWhiteSpace(record.LaunchPath) ? null : record.LaunchPath!.Trim();
            var skip = RestoreSkipKind.None;
            var reason = RestorableReason;

            if (path is null)
            {
                skip = RestoreSkipKind.MissingLaunchPath;
                reason = "快照未记录可执行文件路径，无法按原路径恢复";
            }
            else if (IsPackagedApp(path, env.PackageRoot))
            {
                skip = RestoreSkipKind.PackagedApp;
                reason = "应用商店应用（WindowsApps），不能直接启动包内 exe";
            }
            else if (EssentialProcessGuard.IsUnderSystemRoot(path, env.SystemRoot))
            {
                skip = RestoreSkipKind.SystemCritical;
                reason = "Windows 系统组件，不属于普通后台程序";
            }
            else if (!env.FileExists(path))
            {
                skip = RestoreSkipKind.MissingFile;
                reason = "可执行文件已不存在（可能已卸载或移动）";
            }
            else if (IsProtectedTarget(record.Name, path))
            {
                skip = RestoreSkipKind.SystemCritical;
                reason = "系统关键进程或本程序自身，不通过恢复队列启动";
            }
            else if (!seenPaths.Add(NormalizePath(path)))
            {
                skip = RestoreSkipKind.Duplicate;
                reason = "与列表中另一项指向同一个程序";
            }
            else if (env.IsProcessRunning(record.Name))
            {
                skip = RestoreSkipKind.AlreadyRunning;
                reason = "该进程已在运行，无需恢复";
            }

            // 元数据只在能拿到路径时读：读 exe 版本资源是纯本地只读操作，失败也只是「未知」
            var metadata = path is null ? ProcessFileMetadata.Empty : env.ReadMetadata(path);
            var hasPresetName = ProcessDisplayNames.HasName(record.Name);
            var identityUnknown = !metadata.HasAny && !hasPresetName;

            result.Add(new ProcessRestoreCandidate(
                Record: record,
                ProcessName: record.Name,
                AppName: BackgroundProcessInspector.ResolveAppName(record.Name, metadata, identityUnknown),
                ExecutableName: ResolveExecutableName(record.Name, path),
                Publisher: metadata.CompanyName,
                Description: metadata.FileDescription,
                LaunchPath: path,
                SkipKind: skip,
                Reason: reason));
        }

        return result;
    }

    /// <summary>exe 文件名：优先用真实路径里的文件名，路径缺失时退回「进程名 + .exe」。</summary>
    internal static string ResolveExecutableName(string processName, string? launchPath)
    {
        if (!string.IsNullOrWhiteSpace(launchPath))
        {
            try
            {
                var name = Path.GetFileName(launchPath);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch
            {
                // 非法字符等：退回进程名
            }
        }

        return string.IsNullOrWhiteSpace(processName) ? "未知程序" : processName + ".exe";
    }

    /// <summary>
    /// 是否是应用商店应用（WindowsApps / MSIX 包目录）。
    ///
    /// 判据用「路径里存在 WindowsApps 这一段」而不是只比对固定前缀：
    /// 应用商店的安装位置用户可以改到别的盘（D:\WindowsApps），只认 C 盘前缀会漏。
    /// 这类 exe 受 ACL 保护、且依赖包身份启动，直接 Process.Start 只会失败或起错东西，
    /// 所以一律跳过并写明原因，不去猜包身份。
    /// </summary>
    internal static bool IsPackagedApp(string? path, string? packageRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalized = path.Replace('/', '\\');

        if (!string.IsNullOrWhiteSpace(packageRoot))
        {
            var root = packageRoot.Replace('/', '\\').TrimEnd('\\');
            if (normalized.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var segment in normalized.Split('\\'))
        {
            if (segment.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
            if (segment.Equals("ModifiableWindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>硬编码保护名单（系统外壳 / 安全软件 / 输入法 / 本程序）里的目标不允许进恢复队列。</summary>
    internal static bool IsProtectedTarget(string processName, string launchPath)
    {
        if (BackgroundProcessWhitelist.IsProtected(processName)) return true;

        try
        {
            var stem = Path.GetFileNameWithoutExtension(launchPath);
            return !string.IsNullOrWhiteSpace(stem)
                   && stem.Equals("GameOptimizer.App", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>路径规范化后再去重：`D:/Apps/a.exe` 与 `d:\apps\A.EXE` 是同一个程序。</summary>
    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('/', '\\').Trim();
        return normalized.TrimEnd('\\');
    }
}
