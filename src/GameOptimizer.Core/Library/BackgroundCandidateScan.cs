namespace GameOptimizer.Core.Library;

using System.Diagnostics;

/// <summary>
/// 一个「正在运行、可以交给用户管理」的应用。
/// </summary>
/// <param name="ProcessName">进程名（不含 .exe）。结束与恢复都用它。</param>
/// <param name="InPreset">是否属于内置预设名单（已知的后台大户）。</param>
/// <param name="ExecutablePath">可执行文件完整路径；规范化前原样保留，用于显示。</param>
public sealed record BackgroundCandidate(string ProcessName, bool InPreset, string? ExecutablePath = null);

/// <summary>扫描结果的类别：可以结束的候选，还是只用于展示的「系统关键进程」。</summary>
public enum BackgroundCandidateKind
{
    /// <summary>通过五道闸门，可以交给用户保护 / 标记结束。</summary>
    Endable = 0,

    /// <summary>
    /// 系统关键进程：<b>不允许任何结束操作</b>。
    /// 注意：自 2026-10-25 起扫描不再产出这一类（用户要求不显示），
    /// 保留该值是为了让安全判据与已有构造调用仍能表达「被系统保护」这个事实。
    /// </summary>
    SystemProtected = 1,
}

/// <summary>一条扫描结果。</summary>
/// <param name="ProcessName">进程名（不含 .exe）。</param>
/// <param name="ExecutablePath">可执行文件完整路径，读不到时为 null。</param>
/// <param name="InPreset">是否属于内置预设名单。</param>
/// <param name="Kind">可结束候选 / 系统关键进程（只展示）。</param>
/// <param name="SystemReason">被系统保护的依据，仅 <see cref="BackgroundCandidateKind.SystemProtected"/> 有值。</param>
/// <param name="IsService">是否以已注册服务身份运行（影响用途识别）。</param>
public sealed record BackgroundScanEntry(
    string ProcessName,
    string? ExecutablePath,
    bool InPreset,
    BackgroundCandidateKind Kind,
    string? SystemReason,
    bool IsService = false);

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
/// 找出「本机正在运行、可以交给用户管理」的进程。
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
///
/// <b>系统关键进程不进入列表</b>（2026-10-25 调整）：资源管理器、桌面窗口管理器这类
/// 由安全机制拦下的进程，用户既不能保护也不能标记结束，列出来只是徒增噪音——
/// 用户的原话是「这些我根本不会去动，别占地方」。
/// 于是它们现在在扫描阶段就被丢弃：不展示 ≠ 不保护，
/// 拦截仍然由 <see cref="EssentialProcessGuard"/> 在执行侧独立完成，
/// 与界面上是否列出来毫无关系。
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
    public static bool MatchesGame(string processName, IReadOnlySet<string>? gameStems)
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
        => ScanEntries(gameProcessStems)
            .Where(e => e.Kind == BackgroundCandidateKind.Endable)
            .Select(e => new BackgroundCandidate(e.ProcessName, e.InPreset, e.ExecutablePath))
            .ToList();

    /// <summary>
    /// 扫描本机，返回全部条目（含只做展示的系统关键进程）。
    /// 同名多进程合并为一条，路径取第一条能读到的。
    /// </summary>
    public static IReadOnlyList<BackgroundScanEntry> ScanEntries(
        IEnumerable<string>? gameProcessStems = null)
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
        var found = new Dictionary<string, BackgroundScanEntry>(StringComparer.OrdinalIgnoreCase);

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

                var hasWindow = windowPids.Contains(p.Id);
                var inPreset = preset.Contains(identity.Name);

                var facts = new ProcessFacts(
                    identity.Name,
                    identity.SessionId,
                    identity.ExecutablePath,
                    identity.IsService,
                    hasWindow,
                    inPreset);

                if (ShouldOffer(facts, env))
                {
                    // 同名多进程（浏览器会开十几个）只算一项
                    found.TryAdd(identity.Name, new BackgroundScanEntry(
                        identity.Name, identity.ExecutablePath, inPreset,
                        BackgroundCandidateKind.Endable, null,
                        identity.IsService || serviceNames.Contains(identity.Name)));
                }

                // 没通过闸门的（系统关键进程、已注册服务、系统目录组件、
                // 其他会话、游戏本体……）一律不进列表：
                // 它们本来就既不能保护也不能标记结束，列出来只是噪音。
                // 安全语义不受影响 —— 拦截在执行侧由 EssentialProcessGuard 独立完成。
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
