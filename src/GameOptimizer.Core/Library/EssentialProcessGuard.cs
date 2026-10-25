namespace GameOptimizer.Core.Library;

using System.Diagnostics;

/// <summary>
/// 判断一个进程是不是「系统必要进程」。
///
/// 为什么需要它：写死的保护名单永远会漏。实测本机有 5 个进程以服务身份运行
/// （wallpaperservice64、telemetry_agent、MateBookService、MBAMessageCenter、SyncService），
/// 它们既抢资源又杀不掉，硬编码名单既猜不全也说不清。
///
/// 所以改成按「客观特征」判定，而不是靠名字猜：
///  · Session 0 的进程 = 系统服务宿主，杀了会牵动整个服务栈；
///  · 路径在 %SystemRoot% 下 = Windows 自带组件；
///  · 是已注册服务的主程序（按注册表里的 ImagePath 比对）；
///  · 命中已知的关键/安全/输入法名单（兜底）。
/// 判为必要的自动进保护名单，不参与结束。
/// </summary>
public static class EssentialProcessGuard
{
    /// <summary>
    /// 判定所需的进程身份信息。抽成纯数据是为了能单测——
    /// 直接对 Process 断言需要真实进程，既慢又不稳定。
    /// </summary>
    public sealed record ProcessIdentity(
        string Name,
        int SessionId,
        string? ExecutablePath,
        bool IsService = false)
    {
        public bool HasPath => !string.IsNullOrWhiteSpace(ExecutablePath);
    }

    /// <summary>Windows 系统目录，运行期读取一次。</summary>
    private static readonly string SystemRoot =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// 这个进程是否必须保护。
    /// </summary>
    /// <param name="identity">进程身份。</param>
    /// <param name="serviceNames">已注册服务的主程序名集合（不含 .exe）。</param>
    public static bool IsEssential(ProcessIdentity identity, IReadOnlySet<string>? serviceNames = null)
        => IsEssential(identity, serviceNames, out _);

    /// <summary>
    /// 同上，并给出<b>判定依据</b>。界面要回答「为什么它是系统关键进程」，
    /// 界面文案与判据必须来自同一处，否则迟早对不上。
    /// </summary>
    public static bool IsEssential(
        ProcessIdentity identity,
        IReadOnlySet<string>? serviceNames,
        out string? reason)
    {
        reason = null;

        if (string.IsNullOrWhiteSpace(identity.Name))
        {
            reason = "进程名为空，无法识别";
            return true;
        }

        // 1. 硬编码兜底名单：系统外壳、安全软件、输入法、本程序
        if (BackgroundProcessWhitelist.IsProtected(identity.Name))
        {
            reason = "受保护名单（系统外壳 / 安全软件 / 输入法 / 本程序）";
            return true;
        }

        // 2. Session 0 = 服务会话。桌面应用永远不在这个会话里，
        //    在这里结束任何东西都是在动服务栈。
        if (identity.SessionId == 0)
        {
            reason = "系统服务会话（Session 0）";
            return true;
        }

        // 3. 已知服务的主程序
        if (identity.IsService)
        {
            reason = "已注册的 Windows 服务";
            return true;
        }

        if (serviceNames is not null && serviceNames.Contains(identity.Name))
        {
            reason = "已注册的 Windows 服务";
            return true;
        }

        // 4. Windows 自带组件
        if (IsUnderSystemRoot(identity.ExecutablePath, SystemRoot))
        {
            reason = "Windows 系统组件（位于系统目录）";
            return true;
        }

        return false;
    }

    /// <summary>路径是否落在系统目录下（含子目录）。</summary>
    internal static bool IsUnderSystemRoot(string? path, string systemRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(systemRoot)) return false;

        var normalizedRoot = systemRoot.Replace('/', '\\').TrimEnd('\\');
        var normalizedPath = path.Replace('/', '\\');

        // 必须带分隔符比较，否则 C:\WindowsApps 会被误判成在 C:\Windows 下
        return normalizedPath.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从注册表读出所有已注册服务的可执行文件名（去掉扩展名）。
    /// 读不到时返回空集合——此时仍靠 Session 0 与系统路径兜底。
    /// </summary>
    public static IReadOnlySet<string> LoadServiceImageNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services");
            if (root is null) return names;

            foreach (var sub in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("ImagePath") is not string imagePath || imagePath.Length == 0) continue;

                var exe = ExtractExecutableName(imagePath);
                if (exe is not null) names.Add(exe);
            }
        }
        catch
        {
            // 读不到服务列表不该让游戏模式整个失效
        }

        return names;
    }

    /// <summary>
    /// 从服务的 ImagePath 里抠出 exe 文件名（无扩展名）。
    /// 取值形如：
    ///   C:\Windows\System32\svchost.exe -k netsvcs -p
    ///   "%ProgramFiles%\Huawei\PCManager\PcManagerService.exe" --service
    ///   \SystemRoot\System32\drivers\foo.sys
    /// </summary>
    internal static string? ExtractExecutableName(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;

        var text = imagePath.Trim();

        // 带引号的路径：取第一对引号里的内容
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            text = close > 1 ? text[1..close] : text.Trim('"');
        }
        else
        {
            // 不带引号：在 ".exe" 处截断，否则整条命令行都会被当成路径
            var idx = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            text = idx > 0 ? text[..(idx + 4)] : text.Split(' ')[0];
        }

        var name = Path.GetFileNameWithoutExtension(text.Trim());
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>把一个真实进程转成判定用的身份。</summary>
    public static ProcessIdentity Identify(Process process)
    {
        string? path = null;
        try { path = process.MainModule?.FileName; }
        catch { /* 反作弊/不同账户会拒绝读取路径，此时只能靠 SessionId 判定 */ }

        int session;
        try { session = process.SessionId; }
        catch { session = -1; }

        return new ProcessIdentity(process.ProcessName, session, path);
    }

    /// <summary>这个进程是不是服务宿主（svchost 之外的自研服务也算）。</summary>
    public static bool IsServiceProcess(Process process, IReadOnlySet<string> serviceNames)
        => serviceNames.Contains(process.ProcessName);
}
