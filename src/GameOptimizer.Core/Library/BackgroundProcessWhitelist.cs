namespace GameOptimizer.Core.Library;

/// <summary>
/// 游戏模式结束后台的力度预设。
///
/// 这里的两个档位只是「起点」：用户可以在界面里勾选 / 取消任意条目，
/// 最终生效的是持久化下来的自定义名单（见 <see cref="BackgroundWhitelistStore"/>）。
/// </summary>
public enum GameModeStrength
{
    /// <summary>只结束浏览、下载、录屏、远控、通讯这类明显抢资源的软件。</summary>
    Standard = 0,

    /// <summary>
    /// 在标准档基础上，再结束壁纸引擎、厂商管家与遥测、任务栏美化、
    /// 音乐社交、网盘同步。对帧率的收益最大，但桌面外观会短暂变化。
    /// </summary>
    Strong = 1,
}

/// <summary>
/// 默认的可结束进程清单。
///
/// 原则：只放「关掉不影响系统、且确实抢资源」的常见软件。
/// 绝不包含杀毒、输入法、系统服务、资源管理器——这些被误伤会直接影响可用性。
///
/// 实测教训：上一版名单只有 18 项浏览器/通讯软件，在本机一个都没命中
/// （用户实际装的是壁纸引擎 + 厂商管家 + 音乐客户端 + 任务栏美化），
/// 于是「结束后台」实际什么都没做，界面却显示已开启。
/// 所以强力档的每一条都来自实机观察到的常驻进程，不是凭想象加的。
/// </summary>
public static class BackgroundProcessWhitelist
{
    /// <summary>标准档。</summary>
    public static IReadOnlyList<string> Standard { get; } =
    [
        "chrome", "msedge", "firefox", "QQBrowser", "opera", "thorium",
        "Thunder", "qbittorrent", "uTorrent", "aria2c",
        "obs64", "obs32",
        "TeamViewer", "AnyDesk", "ToDesk", "SunloginClient",
        "WeChat", "QQNT", "DingTalk",
    ];

    /// <summary>强力档：标准档 + 实测发现的常驻大户。</summary>
    public static IReadOnlyList<string> Strong { get; } =
    [
        .. Standard,

        // 壁纸引擎：动态壁纸持续占用 GPU 与显存，是核显机器上最值得关的一项
        "wallpaper64", "wallpaper32", "wallpaperservice64", "wallpaperservice32", "webwallpaper32", "webwallpaper64",

        // 厂商管家与遥测：华为电脑管家、MateBook 服务、消息中心、遥测上报
        "PcOptimizationCenter", "MateBookService", "MBAMessageCenter", "telemetry_agent",

        // 任务栏美化与桌面常驻小工具
        "TranslucentTB", "Rainmeter", "StartAllBack",

        // 音乐与社交客户端
        "kugou", "KwMusic", "cloudmusic", "QQMusic", "Spotify",
        "feishu", "Lark", "KOOK", "ttvoice", "TTSpeech",

        // 网盘与同步（上传会占满带宽，直接影响在线游戏延迟）
        "BaiduNetdisk", "YunDetectService", "Nutstore", "OneDrive",
        "SyncService", "ima", "doubao",
    ];

    /// <summary>
    /// 无论如何都不结束的进程。即使用户把它写进自定义名单，这里也兜底拦截：
    /// 系统关键进程、杀毒、输入法、以及本程序自身。
    /// </summary>
    public static IReadOnlyList<string> Protected { get; } =
    [
        // 系统与桌面外壳
        "explorer", "dwm", "svchost", "csrss", "winlogon", "wininit", "smss", "services",
        "lsass", "system", "registry", "taskhostw", "sihost", "ShellHost",
        "StartMenuExperienceHost", "SearchHost", "TextInputHost", "RuntimeBroker",
        "conhost", "fontdrvhost", "dllhost", "WmiPrvSE", "WUDFHost",

        // 安全软件：结束后系统防护会静默失效，风险远大于收益
        "MsMpEng", "NisSrv", "SecurityHealthService", "MpDefenderCoreService", "MpCmdRun",

        // 输入法
        "ctfmon", "ChsIME", "SogouCloud", "QQPinyinTSF",

        // 本程序自身：结束自己会让「退出游戏模式」再也点不到
        "GameOptimizer.App",
    ];

    /// <summary>按力度取预设名单。</summary>
    public static IReadOnlyList<string> For(GameModeStrength strength) => strength == GameModeStrength.Strong
        ? Strong
        : Standard;

    /// <summary>默认力度：用户要求「力度上调」，所以默认就是强力档。</summary>
    public static GameModeStrength DefaultStrength => GameModeStrength.Strong;

    /// <summary>默认名单（尚未自定义时使用）。</summary>
    public static IReadOnlyList<string> Default => For(DefaultStrength);

    /// <summary>该进程名是否被保护（不区分大小写）。</summary>
    public static bool IsProtected(string processName)
        => !string.IsNullOrWhiteSpace(processName)
           && Protected.Contains(processName, StringComparer.OrdinalIgnoreCase);

    public static string Describe(GameModeStrength strength) => strength switch
    {
        GameModeStrength.Strong => "强力",
        _ => "标准",
    };
}

public static class GameModeStrengthText
{
    public static string Describe(this GameModeStrength strength) => BackgroundProcessWhitelist.Describe(strength);
}
