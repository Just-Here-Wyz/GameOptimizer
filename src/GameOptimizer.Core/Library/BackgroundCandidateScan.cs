namespace GameOptimizer.Core.Library;

using System.Diagnostics;

/// <summary>
/// 一个「正在运行、可以列入白名单」的应用。
/// </summary>
/// <param name="ProcessName">进程名（不含 .exe），结束与恢复都用它。</param>
/// <param name="InPreset">是否属于内置预设名单。预设外的项默认不勾选。</param>
public sealed record BackgroundCandidate(string ProcessName, bool InPreset);

/// <summary>
/// 扫描判据所需的进程事实。全部是纯数据，便于单测。
/// </summary>
public sealed record ProcessFacts(
    string Name,
    int SessionId,
    string? ExecutablePath,
    bool IsService,
    bool HasMainWindow,
    bool InPreset)
{
    public bool HasPath => !string.IsNullOrWhiteSpace(ExecutablePath);
}

/// <summary>扫描时的环境常量，抽出来是为了让判据可测。</summary>
public sealed record ScanEnvironment(
    IReadOnlySet<string>? ServiceNames,
    IReadOnlySet<string>? GameProcessStems,
    int CurrentSessionId,
    string? SystemRoot = null);

/// <summary>
/// 找出「本机正在运行、可以交给用户勾选结束后台」的进程。
///
/// 旧实现只遍历内置预设名单（约 50 个硬编码进程名）去 `GetProcessesByName`。
/// 结果：装了名单外软件的用户，白名单里永远是空的或严重缺项——
/// 用户明明开着音乐客户端、同步盘、厂商工具，列表里一个都不显示，
/// 于是「结束后台」实际上什么都没做，界面却报「已开启」。
///
/// 现在的候选来源有两条：
/// 1. **有可见主窗口的用户级进程**——这才是「后台干扰」的主体，装机各不相同，
///    写死名单永远追不上；
/// 2. 预设名单里那些没有窗口的常驻后台（壁纸服务、同步服务等）。
///
/// 五道闸门保证不会误伤：
/// · 只取当前会话（服务在 Session 0，天然排除）；
/// · 排除已注册服务与 Windows 目录下的组件；
/// · 排除保护名单（系统外壳、安全软件、输入法、本程序）；
/// · 排除游戏进程——把要玩的游戏列进去，一勾就等于自杀；
/// · **读不到 exe 路径的一律不列**：结束后无法按原路径恢复，
///   列出来就是给用户挖坑（反作弊/高权限进程正属此类）。
/// </summary>
public static class BackgroundCandidateScan
{
    /// <summary>
    /// 这个进程要不要出现在白名单里。纯函数，判据顺序即优先级。
    /// </summary>
    public static bool ShouldOffer(ProcessFacts facts, ScanEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(facts.Name)) return false;

        // 保护名单：系统外壳、安全软件、输入法、本程序自身
        if (BackgroundProcessWhitelist.IsProtected(facts.Name)) return false;

        // 只处理当前会话：Session 0 是服务会话，其他会话是别的登录用户
        if (facts.SessionId != env.CurrentSessionId) return false;

        // 已注册服务：结束会牵动服务栈
        if (facts.IsService) return false;
        if (env.ServiceNames?.Contains(facts.Name) == true) return false;

        // Windows 自带组件（System32、WinSxS 等）
        if (EssentialProcessGuard.IsUnderSystemRoot(facts.ExecutablePath, env.SystemRoot ?? DefaultSystemRoot))
            return false;

        // 游戏本体与反作弊：绝不能出现在「一键结束」的候选里
        if (MatchesGame(facts.Name, env.GameProcessStems)) return false;

        // 读不到路径 = 结束后拉不回来，不列
        if (!facts.HasPath) return false;

        // 走到这里：有可见窗口 = 用户自己开的桌面程序，正是要关的那类
        if (facts.HasMainWindow) return true;

        // 没有窗口的只放预设名单内的常驻后台（壁纸服务、同步盘等）
        return facts.InPreset;
    }

    /// <summary>
    /// 进程名是否属于某个已安装游戏。按前缀匹配：
    /// 三角洲实际启动的是 DeltaForceClient-Win64-Shipping，静态扫描只认 DeltaForceClient。
    /// </summary>
    internal static bool MatchesGame(string processName, IReadOnlySet<string>? gameStems)
    {
        if (gameStems is null || gameStems.Count == 0) return false;

        foreach (var stem in gameStems)
        {
            // 太短的主名（如 a、qq）会造成大面积误判
            if (stem.Length < 4) continue;
            if (processName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string DefaultSystemRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// 扫描本机，返回可以列入白名单的进程名（按名字去重）。
    /// </summary>
    /// <param name="gameProcessStems">已安装游戏的 exe 主名，用于排除游戏进程。</param>
    public static IReadOnlyList<BackgroundCandidate> Scan(IEnumerable<string>? gameProcessStems = null)
    {
        var preset = new HashSet<string>(BackgroundProcessWhitelist.Strong, StringComparer.OrdinalIgnoreCase);
        var games = new HashSet<string>(
            (gameProcessStems ?? []).Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.OrdinalIgnoreCase);

        var serviceNames = EssentialProcessGuard.LoadServiceImageNames();

        int session;
        string selfName;
        try
        {
            using var self = Process.GetCurrentProcess();
            session = self.SessionId;
            selfName = self.ProcessName;
        }
        catch
        {
            // 连自己都读不到（几乎不可能）：退化为「拿不到会话号就不按会话过滤」
            session = -1;
            selfName = "GameOptimizer.App";
        }

        var env = new ScanEnvironment(serviceNames, games, session);
        var found = new Dictionary<string, BackgroundCandidate>(StringComparer.OrdinalIgnoreCase);

        // 一次拿到「有顶层窗口的进程」整张表，
        // 避免逐个进程读 MainWindowHandle（两百多次跨进程调用，慢且偶发卡住）
        var windowPids = TopLevelWindowIndex.Collect();

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return []; }

        foreach (var p in all)
        {
            try
            {
                var identity = EssentialProcessGuard.Identify(p);
                if (string.Equals(identity.Name, selfName, StringComparison.OrdinalIgnoreCase)) continue;

                var facts = new ProcessFacts(
                    identity.Name,
                    identity.SessionId,
                    identity.ExecutablePath,
                    identity.IsService,
                    windowPids.Contains(p.Id),
                    preset.Contains(identity.Name));

                if (!ShouldOffer(facts, env)) continue;

                // 同名多进程（浏览器会开十几个）只算一项
                found.TryAdd(identity.Name, new BackgroundCandidate(identity.Name, facts.InPreset));
            }
            catch
            {
                // 单个进程读取失败（退出中/权限）不该让整轮扫描失败
            }
            finally { p.Dispose(); }
        }

        return [.. found.Values];
    }
}
