namespace GameOptimizer.Core.Library;

/// <summary>
/// 游戏模式下允许结束的后台进程白名单。只放"关掉不影响系统、且确实抢资源"的常见软件，
/// 不含杀毒、输入法、系统服务等可能被误伤的进程。
/// </summary>
public static class BackgroundProcessWhitelist
{
    public static IReadOnlyList<string> Default { get; } =
    [
        "chrome",           // 浏览器
        "msedge",
        "firefox",
        "QQBrowser",
        "opera",
        "thorium",
        "Thunder",          // 迅雷
        "qbittorrent",      // 下载器
        "uTorrent",
        "aria2c",
        "obs64",            // 录屏推流
        "obs32",
        "TeamViewer",       // 远程桌面
        "AnyDesk",
        "ToDesk",
        "SunloginClient",
        "WeChat",           // 通讯
        "QQNT",
        "DingTalk",
    ];
}
