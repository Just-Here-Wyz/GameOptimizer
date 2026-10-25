namespace GameOptimizer.Core.Library;

/// <summary>
/// 一个进程在「保护」这件事上的状态。
///
/// 语义定义（本项目唯一口径，界面 / 存储 / 执行都必须按它来）：
/// <list type="bullet">
/// <item><see cref="Protected"/>：用户勾选了保护 → 游戏模式<b>绝不</b>结束它；</item>
/// <item><see cref="Unprotected"/>：没有主动保护 → 只是「有资格」进入候选，
///       <b>不等于</b>会被结束；</item>
/// <item><see cref="SystemProtected"/>：系统关键进程 / 已注册服务 / 系统目录组件
///       → 由独立安全机制拦截，用户勾不勾选都不许结束。</item>
/// </list>
/// </summary>
public enum ProtectionState
{
    Unprotected = 0,
    Protected = 1,
    SystemProtected = 2,
}

/// <summary>为什么一个进程<em>不会</em>被结束。每一种都要能说清原因。</summary>
public enum KillBlockReason
{
    /// <summary>通过全部检查（但仍要等用户启动游戏模式才会执行）。</summary>
    None = 0,

    /// <summary>没被标记为结束目标。</summary>
    NotMarked = 1,

    /// <summary>用户勾选了保护。</summary>
    ProtectedByUser = 2,

    /// <summary>系统关键进程 / 服务 / 系统组件。</summary>
    SystemProtected = 3,

    /// <summary>读不到可执行文件路径，结束后拉不回来。</summary>
    MissingPath = 4,

    /// <summary>游戏本体或反作弊进程。</summary>
    GameProcess = 5,
}

/// <summary>一个候选进程的客观事实。抽成纯数据，判据才能单测。</summary>
public sealed record BackgroundTargetFacts(
    string Name,
    string? ExecutablePath,
    bool IsProtectedByUser,
    bool IsEndTarget,
    bool IsSystemEssential,
    bool IsIdentityUnknown,
    bool MatchesGame = false)
{
    public bool HasPath => !string.IsNullOrWhiteSpace(ExecutablePath);
}

/// <summary>一次判定的结果：能不能结束、为什么。</summary>
public sealed record KillVerdict(bool Eligible, KillBlockReason Reason, string Explanation);

/// <summary>
/// 「选中即保护」的唯一判据。
///
/// 这个类存在的理由：旧实现里「勾选」等于「要结束」，语义反了，而且没有单独一处
/// 能回答「它到底会不会被关」。现在把所有判断收在这里，界面状态、存储内容、
/// 结束动作的目标集合全部从同一处推导，避免出现「界面写受保护、实际仍然被结束」。
///
/// 判定顺序即优先级：先说「你保护了」，再说「系统不让」，再说「拉不回来」，
/// 最后才说「没标记」——这样界面上的解释永远是最贴近用户意图的那条。
/// </summary>
public static class BackgroundProtectionPolicy
{
    /// <summary>给界面用的状态文案。</summary>
    public static string Describe(ProtectionState state, bool identityUnknown) => state switch
    {
        ProtectionState.Protected => "受保护",
        ProtectionState.SystemProtected => "系统关键进程",
        _ => identityUnknown ? "无法识别" : "未保护",
    };

    /// <summary>状态徽章配色（与 App.xaml 的四档色一致）。</summary>
    public static string BadgeHex(ProtectionState state) => state switch
    {
        ProtectionState.Protected => "#FF34C759",
        ProtectionState.SystemProtected => "#FFFF9F0A",
        _ => "#FF83868D",
    };

    /// <summary>
    /// 算状态。<paramref name="isSystemEssential"/> 来自 <see cref="EssentialProcessGuard"/>，
    /// 它优先于用户勾选：系统关键进程即使被勾选也标注为系统关键（保护是叠加的）。
    /// </summary>
    public static ProtectionState StateOf(bool isProtectedByUser, bool isSystemEssential)
    {
        if (isProtectedByUser) return ProtectionState.Protected;
        return isSystemEssential ? ProtectionState.SystemProtected : ProtectionState.Unprotected;
    }

    /// <summary>
    /// 能不能被标记为「结束目标」。界面据此决定按钮是否可点，
    /// 这就是需求里「只有通过现有安全检查的进程才有资格成为结束候选」。
    /// </summary>
    public static KillVerdict DecideMarkable(BackgroundTargetFacts facts)
    {
        if (facts.IsProtectedByUser)
            return new(false, KillBlockReason.ProtectedByUser, "已勾选保护，不能同时标记为结束目标");

        if (facts.IsSystemEssential)
            return new(false, KillBlockReason.SystemProtected, "系统关键进程，不允许标记为结束目标");

        if (!facts.HasPath)
            return new(false, KillBlockReason.MissingPath, "读不到可执行文件路径，结束后无法自动恢复");

        if (facts.MatchesGame)
            return new(false, KillBlockReason.GameProcess, "这是游戏本体或反作弊进程，不在结束范围内");

        return new(true, KillBlockReason.None, "可以标记为结束目标（仍需你启动游戏模式才会执行）");
    }

    /// <summary>
    /// 最终判定：这个进程在本次游戏模式里会不会被结束。
    /// <b>只有 Eligible 为 true 的名字才会进入 <c>EndProcessAction</c>。</b>
    /// </summary>
    public static KillVerdict Decide(BackgroundTargetFacts facts)
    {
        if (facts.IsProtectedByUser)
            return new(false, KillBlockReason.ProtectedByUser, "你已勾选保护，游戏模式不会结束它");

        if (facts.IsSystemEssential)
            return new(false, KillBlockReason.SystemProtected, "系统关键进程，由独立安全机制阻止结束");

        if (facts.MatchesGame)
            return new(false, KillBlockReason.GameProcess, "游戏本体 / 反作弊进程，不在结束范围");

        if (!facts.HasPath)
            return new(false, KillBlockReason.MissingPath, "读不到可执行文件路径，结束后无法自动恢复");

        if (!facts.IsEndTarget)
            return new(false, KillBlockReason.NotMarked, "未标记为结束目标，不会被结束");

        return new(true, KillBlockReason.None, "已标记结束且通过全部安全检查；只有启动游戏模式时才会执行");
    }

    /// <summary>
    /// 路径规范化：只用于<b>比较与去重</b>，不用于显示。
    /// 统一斜杠、折叠重复分隔符、去掉尾部反斜杠、忽略大小写——
    /// 否则 <c>d:/Apps/x.exe</c> 与 <c>D:\Apps\X.EXE</c> 会被当成两个进程（踩过一次的坑）。
    /// </summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        var text = path.Trim().Replace('/', '\\');
        while (text.Contains(@"\\", StringComparison.Ordinal))
            text = text.Replace(@"\\", @"\", StringComparison.Ordinal);

        return text.TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>两个路径是否指向同一个可执行文件（已规范化）。</summary>
    public static bool SamePath(string? a, string? b)
    {
        var na = NormalizePath(a);
        return na.Length > 0 && na == NormalizePath(b);
    }
}

/// <summary>管理浮窗里的状态筛选项。</summary>
public enum BackgroundListFilter
{
    All = 0,
    Protected = 1,
    Unprotected = 2,
    SystemProtected = 3,
    Unknown = 4,
}

/// <summary>
/// 列表搜索与筛选的判据（纯函数）。
///
/// 放在 Core 而不是 ViewModel 里，是为了能用测试钉住两条容易出错的口径：
/// 「未保护」不含系统关键进程（它由安全机制管理，不是「用户没保护」），
/// 以及搜索要能命中路径与发布者（排查时用户往往只记得路径）。
/// </summary>
public static class BackgroundProcessFilter
{
    /// <summary>搜索用的合并文本：应用名 / 进程名 / 路径 / 用途 / 发布者。</summary>
    public static string SearchTextOf(BackgroundProcessInfo info) => string.Join(' ',
        info.AppName, info.ProcessName, info.ExecutableName, info.ExecutablePath,
        info.Purpose, info.Publisher, info.FileDescription, info.ProductName);

    /// <summary>这一行是否应出现在当前筛选 + 搜索条件下。</summary>
    public static bool Matches(
        BackgroundProcessInfo info,
        bool isProtected,
        BackgroundListFilter filter,
        string? query)
    {
        var stateOk = filter switch
        {
            BackgroundListFilter.Protected => isProtected,
            BackgroundListFilter.Unprotected => !isProtected && !info.IsSystemEssential,
            BackgroundListFilter.SystemProtected => info.IsSystemEssential,
            BackgroundListFilter.Unknown => info.IsIdentityUnknown,
            _ => true,
        };
        if (!stateOk) return false;

        var text = query?.Trim() ?? "";
        return text.Length == 0
               || SearchTextOf(info).Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 从一批候选里算出「本次游戏模式真正会结束的名字」。
///
/// 单独拎出来是为了让「界面显示」与「实际执行」共用同一个结果：
/// 界面展示的结束目标数 == 传给 <see cref="GameMode.EndProcessAction"/> 的名单长度。
/// </summary>
public static class BackgroundKillPlan
{
    /// <summary>可以结束的进程名（保持输入顺序，已按名字去重）。</summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<BackgroundTargetFacts> targets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var t in targets)
        {
            if (!BackgroundProtectionPolicy.Decide(t).Eligible) continue;
            if (string.IsNullOrWhiteSpace(t.Name)) continue;
            if (!seen.Add(t.Name)) continue;
            result.Add(t.Name);
        }

        return result;
    }

    /// <summary>
    /// 要显式交给结束动作的「受保护名单」，作为执行侧的最后一道防线。
    /// 只收<b>用户勾选</b>的项：系统关键进程由 <see cref="EssentialProcessGuard"/> 用更准确的理由拦下，
    /// 混进这里会让界面显示成「你勾选了保护」，与事实不符。
    /// </summary>
    public static IReadOnlyList<string> ProtectedNames(IEnumerable<BackgroundTargetFacts> targets)
        => targets.Where(t => t.IsProtectedByUser)
                  .Select(t => t.Name)
                  .Where(n => !string.IsNullOrWhiteSpace(n))
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();
}
