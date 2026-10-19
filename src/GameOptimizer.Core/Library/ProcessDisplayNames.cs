namespace GameOptimizer.Core.Library;

/// <summary>
/// 进程名 → 看得懂的应用名。
///
/// 为什么需要：白名单里显示 wallpaper64、PcOptimizationCenter、telemetry_agent
/// 这种东西，用户根本不知道关掉的是什么、该不该关。
/// 显示成「壁纸引擎 Wallpaper Engine」「华为电脑管家」才做得成决定。
/// 未收录的退回进程名，不编造译名。
/// </summary>
public static class ProcessDisplayNames
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 浏览器
        ["chrome"] = "Chrome 浏览器",
        ["msedge"] = "Edge 浏览器",
        ["msedgewebview2"] = "Edge 组件（WebView2）",
        ["firefox"] = "Firefox 浏览器",
        ["QQBrowser"] = "QQ 浏览器",
        ["opera"] = "Opera 浏览器",
        ["thorium"] = "Thorium 浏览器",

        // 下载
        ["Thunder"] = "迅雷",
        ["qbittorrent"] = "qBittorrent",
        ["uTorrent"] = "uTorrent",
        ["aria2c"] = "aria2 下载器",

        // 录屏 / 远控
        ["obs64"] = "OBS 录制",
        ["obs32"] = "OBS 录制",
        ["TeamViewer"] = "TeamViewer 远控",
        ["AnyDesk"] = "AnyDesk 远控",
        ["ToDesk"] = "ToDesk 远控",
        ["SunloginClient"] = "向日葵远控",

        // 通讯
        ["WeChat"] = "微信",
        ["QQNT"] = "QQ",
        ["DingTalk"] = "钉钉",
        ["feishu"] = "飞书",
        ["Lark"] = "飞书",
        ["KOOK"] = "KOOK 语音",
        ["ttvoice"] = "TT 语音",
        ["TTSpeech"] = "TT 语音",

        // 壁纸 / 桌面美化
        ["wallpaper64"] = "壁纸引擎 Wallpaper Engine",
        ["wallpaper32"] = "壁纸引擎 Wallpaper Engine",
        ["wallpaperservice64"] = "壁纸引擎后台服务",
        ["wallpaperservice32"] = "壁纸引擎后台服务",
        ["webwallpaper32"] = "壁纸引擎网页壁纸",
        ["webwallpaper64"] = "壁纸引擎网页壁纸",
        ["TranslucentTB"] = "任务栏透明美化",
        ["Rainmeter"] = "Rainmeter 桌面小工具",
        ["StartAllBack"] = "StartAllBack 开始菜单",

        // 厂商管家 / 遥测
        ["PcOptimizationCenter"] = "华为电脑管家",
        ["MateBookService"] = "华为 MateBook 服务",
        ["MBAMessageCenter"] = "华为消息中心",
        ["telemetry_agent"] = "遥测上报组件",

        // 音乐
        ["kugou"] = "酷狗音乐",
        ["KwMusic"] = "酷我音乐",
        ["cloudmusic"] = "网易云音乐",
        ["QQMusic"] = "QQ 音乐",
        ["Spotify"] = "Spotify",

        // 网盘 / 同步
        ["BaiduNetdisk"] = "百度网盘",
        ["YunDetectService"] = "百度网盘检测服务",
        ["Nutstore"] = "坚果云",
        ["OneDrive"] = "OneDrive 同步",
        ["SyncService"] = "同步服务",
        ["ima"] = "腾讯 ima",
        ["doubao"] = "豆包",

        // 系统必要项（会出现在保护列表里）
        ["explorer"] = "资源管理器",
        ["dwm"] = "桌面窗口管理器",
        ["svchost"] = "系统服务宿主",
        ["MsMpEng"] = "Microsoft Defender 杀毒",
        ["NisSrv"] = "Defender 网络检测",
        ["SearchHost"] = "Windows 搜索",
        ["StartMenuExperienceHost"] = "开始菜单",
        ["TextInputHost"] = "输入法体验",
        ["ctfmon"] = "输入法框架",
        ["DeepSeek Harness"] = "DeepSeek Harness",
        ["GameOptimizer.App"] = "本程序",
    };

    /// <summary>取应用显示名；未收录时退回进程名。</summary>
    public static string Resolve(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "";
        return Map.TryGetValue(processName.Trim(), out var name) ? name : processName;
    }

    /// <summary>是否收录了友好名（供测试确认覆盖率）。</summary>
    public static bool HasName(string processName)
        => !string.IsNullOrWhiteSpace(processName) && Map.ContainsKey(processName.Trim());

    /// <summary>已收录条目数。</summary>
    public static int Count => Map.Count;
}
