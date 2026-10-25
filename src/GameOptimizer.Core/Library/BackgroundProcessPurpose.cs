namespace GameOptimizer.Core.Library;

/// <summary>
/// 一条「这个进程是干什么的」结论。
/// </summary>
/// <param name="Text">给人看的用途说明；无法确认时固定为「用途未知」。</param>
/// <param name="Basis">识别依据，界面必须显示——没有依据的结论等于猜测。</param>
/// <param name="IsKnown">是否真的识别出来了。</param>
public sealed record ProcessPurpose(string Text, string Basis, bool IsKnown)
{
    /// <summary>无法确认时的固定文案。不允许用「可能是 xx」蒙混过去。</summary>
    public const string UnknownText = "用途未知";

    public static ProcessPurpose Unknown { get; } =
        new(UnknownText, "没有可靠依据（不在识别表内，且产品信息不足以判断）", false);
}

/// <summary>
/// 进程用途识别。**只认可靠依据**，认不出来就明确写「用途未知」。
///
/// 四个依据来源，按可信度排序：
/// <list type="number">
/// <item><b>内置识别表</b>：对已知产品做<b>进程名精确匹配</b>（chrome→Chrome 浏览器）。
///       这是「认出已知应用」，不是「按名字猜用途」——表里每一条都是确定的产品；</item>
/// <item><b>系统目录</b>：路径落在 %SystemRoot% 下，客观事实，不需要猜；</item>
/// <item><b>注册服务</b>：注册表里查得到 ImagePath，同样是事实；</item>
/// <item><b>产品信息</b>：exe 版本资源里的产品名称 / 文件描述含明确关键词
///       （「输入法」「Graphics」「Download」等）。关键词来自厂商自己写的字符串，
///       所以界面会把命中的词一并显示出来，让人自己复核。</item>
/// </list>
///
/// <b>刻意不做的事</b>：拿公司名推用途（华为的进程既可能是管家也可能是驱动）、
/// 拿路径目录名猜厂商、对表外进程给「看起来像 xx」的描述。
/// 认不出来时宁可少说，也不能编——这是本项目「不可观测的不报结论」的同一原则。
/// </summary>
public static class ProcessPurposeCatalog
{
    private const string CatalogBasis = "内置识别表（进程名精确匹配）";

    /// <summary>已知产品 → 用途。键为进程名（不含 .exe），大小写不敏感。</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // 浏览器
        ["chrome"] = "网页浏览器（常驻多进程，占用内存与网络）",
        ["msedge"] = "网页浏览器（常驻多进程，占用内存与网络）",
        ["msedgewebview2"] = "浏览器组件宿主（被其他应用内嵌调用）",
        ["firefox"] = "网页浏览器（常驻多进程，占用内存与网络）",
        ["QQBrowser"] = "网页浏览器（常驻多进程，占用内存与网络）",
        ["opera"] = "网页浏览器（常驻多进程，占用内存与网络）",
        ["thorium"] = "网页浏览器（常驻多进程，占用内存与网络）",

        // 下载
        ["Thunder"] = "下载工具（持续占用带宽与磁盘）",
        ["qbittorrent"] = "BT 下载工具（持续占用带宽与磁盘）",
        ["uTorrent"] = "BT 下载工具（持续占用带宽与磁盘）",
        ["aria2c"] = "命令行下载工具（持续占用带宽与磁盘）",

        // 录屏 / 远控
        ["obs64"] = "录屏 / 直播工具（占用 GPU 编码器）",
        ["obs32"] = "录屏 / 直播工具（占用 GPU 编码器）",
        ["TeamViewer"] = "远程控制工具（常驻后台等待连接）",
        ["AnyDesk"] = "远程控制工具（常驻后台等待连接）",
        ["ToDesk"] = "远程控制工具（常驻后台等待连接）",
        ["SunloginClient"] = "远程控制工具（常驻后台等待连接）",

        // 通讯
        ["WeChat"] = "聊天软件（常驻后台接收消息）",
        ["QQNT"] = "聊天软件（常驻后台接收消息）",
        ["DingTalk"] = "办公通讯软件（常驻后台接收消息）",
        ["feishu"] = "办公通讯软件（常驻后台接收消息）",
        ["Lark"] = "办公通讯软件（常驻后台接收消息）",
        ["KOOK"] = "语音聊天客户端",
        ["ttvoice"] = "语音聊天客户端",
        ["TTSpeech"] = "语音聊天客户端",

        // 壁纸 / 桌面美化
        ["wallpaper64"] = "动态壁纸引擎（持续占用 GPU 与显存）",
        ["wallpaper32"] = "动态壁纸引擎（持续占用 GPU 与显存）",
        ["wallpaperservice64"] = "动态壁纸引擎的后台服务",
        ["wallpaperservice32"] = "动态壁纸引擎的后台服务",
        ["webwallpaper32"] = "动态壁纸引擎的网页壁纸进程",
        ["webwallpaper64"] = "动态壁纸引擎的网页壁纸进程",
        ["TranslucentTB"] = "任务栏透明美化工具",
        ["Rainmeter"] = "桌面小工具（常驻刷新桌面部件）",
        ["StartAllBack"] = "开始菜单美化工具",

        // 厂商管家 / 遥测
        ["PcOptimizationCenter"] = "厂商电脑管家（后台常驻检测与加速）",
        ["MateBookService"] = "厂商电脑管家的后台服务",
        ["MBAMessageCenter"] = "厂商消息推送组件",
        ["telemetry_agent"] = "厂商遥测上报组件",

        // 音乐
        ["kugou"] = "音乐播放客户端",
        ["KwMusic"] = "音乐播放客户端",
        ["cloudmusic"] = "音乐播放客户端",
        ["QQMusic"] = "音乐播放客户端",
        ["Spotify"] = "音乐播放客户端",

        // 网盘 / 同步
        ["BaiduNetdisk"] = "网盘同步客户端（上传会占满带宽）",
        ["YunDetectService"] = "网盘的后台检测服务",
        ["Nutstore"] = "网盘同步客户端（上传会占满带宽）",
        ["OneDrive"] = "云盘同步客户端（上传会占满带宽）",
        ["SyncService"] = "文件同步服务",
        ["ima"] = "知识库 / 笔记客户端（常驻同步）",
        ["doubao"] = "AI 助手客户端（常驻后台）",

        // 系统必要项（会出现在「系统关键进程」里）
        ["explorer"] = "Windows 桌面与文件资源管理器",
        ["dwm"] = "Windows 桌面窗口管理器（负责画面合成）",
        ["svchost"] = "Windows 服务宿主进程",
        ["SearchHost"] = "Windows 搜索界面",
        ["StartMenuExperienceHost"] = "Windows 开始菜单",
        ["TextInputHost"] = "Windows 触摸键盘 / 输入体验",
        ["MsMpEng"] = "Microsoft Defender 杀毒引擎",
        ["NisSrv"] = "Microsoft Defender 网络检测",
        ["ctfmon"] = "Windows 输入法框架",
        ["GameOptimizer.App"] = "本程序自身",
    };

    /// <summary>
    /// 产品信息关键词 → 用途。只匹配 <c>产品名称</c> 与 <c>文件描述</c>，
    /// <b>不匹配公司名</b>（同一家公司既做管家也做驱动，公司名推不出用途）。
    /// </summary>
    private static readonly (string Keyword, string Purpose)[] ProductKeywords =
    [
        ("输入法", "输入法组件"),
        ("Input Method", "输入法组件"),
        ("IME", "输入法组件"),
        ("Graphics", "显卡驱动 / 控制组件"),
        ("显卡", "显卡驱动 / 控制组件"),
        ("Radeon", "显卡驱动 / 控制组件"),
        ("Audio", "音频驱动 / 音效组件"),
        ("音频", "音频驱动 / 音效组件"),
        ("Sound", "音频驱动 / 音效组件"),
        ("Browser", "网页浏览器"),
        ("浏览器", "网页浏览器"),
        ("Download", "下载工具"),
        ("下载", "下载工具"),
        ("Torrent", "BT 下载工具"),
        ("Messenger", "聊天软件"),
        ("Chat", "聊天软件"),
        ("Player", "媒体播放器"),
        ("音乐", "音乐播放客户端"),
        ("AntiVirus", "安全软件组件"),
        ("Antivirus", "安全软件组件"),
        ("Security", "安全软件组件"),
        ("Defender", "安全软件组件"),
        ("安全", "安全软件组件"),
        ("Wallpaper", "动态壁纸组件"),
        ("壁纸", "动态壁纸组件"),
        ("Updater", "自动更新组件"),
        ("Update", "自动更新组件"),
        ("更新", "自动更新组件"),
        ("Driver", "硬件驱动组件"),
        ("驱动", "硬件驱动组件"),
    ];

    /// <summary>
    /// 识别用途。<paramref name="systemRoot"/> 仅供测试注入，默认取环境变量。
    /// </summary>
    public static ProcessPurpose Resolve(
        string processName,
        string? executablePath,
        ProcessFileMetadata metadata,
        bool isService,
        string? systemRoot = null)
    {
        var name = processName?.Trim() ?? "";

        // 1. 内置识别表：已知产品的精确匹配
        if (name.Length > 0 && Known.TryGetValue(name, out var known))
            return new ProcessPurpose(known, CatalogBasis, true);

        // 2. 系统目录下的组件：路径是客观事实
        var root = systemRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (EssentialProcessGuard.IsUnderSystemRoot(executablePath, root))
            return new ProcessPurpose("Windows 系统组件（位于系统目录）", "路径位于系统目录 %SystemRoot%", true);

        // 3. 已注册服务
        if (isService)
            return new ProcessPurpose("已注册的 Windows 服务（后台常驻）", "注册表服务列表里存在该程序", true);

        // 4. 产品信息关键词（只认产品名称 / 文件描述）
        foreach (var (keyword, purpose) in ProductKeywords)
        {
            var hit = MatchField(metadata.ProductName, keyword) ?? MatchField(metadata.FileDescription, keyword);
            if (hit is null) continue;

            return new ProcessPurpose(purpose, $"产品信息匹配（{hit} 含「{keyword}」）", true);
        }

        // 5. 认不出来就说认不出来
        return ProcessPurpose.Unknown;
    }

    /// <summary>字段里是否含关键词；含则返回字段名，用于说明识别依据。</summary>
    private static string? MatchField(string? value, string keyword)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            return $"产品名称 / 文件描述「{value}」";

        return null;
    }

    /// <summary>识别表覆盖的进程数，供测试确认覆盖率。</summary>
    public static int Count => Known.Count;

    /// <summary>识别表是否收录了该进程名。</summary>
    public static bool Knows(string processName)
        => !string.IsNullOrWhiteSpace(processName) && Known.ContainsKey(processName.Trim());
}
