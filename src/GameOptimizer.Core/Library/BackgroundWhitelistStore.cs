namespace GameOptimizer.Core.Library;

using System.Text.Json;

/// <summary>
/// 用户自定义的「结束后台进程」名单，持久化到本地 JSON。
///
/// 注意这里保存的是<b>勾选状态</b>，不是「显示哪些」。
/// 白名单界面只列当前正在运行的应用，但勾选与否必须独立记住：
/// 否则用户取消了某个应用的勾选，等它下次运行时又会被默认勾上。
///
/// 落盘的原因：默认名单覆盖不到用户机器上真正抢资源的东西
/// （实测是壁纸引擎、厂商管家、任务栏美化），让用户自己增减比我们猜更可靠。
/// </summary>
public sealed class BackgroundWhitelistStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>默认路径：%LocalAppData%\GameOptimizer\background-whitelist.json</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameOptimizer",
        "background-whitelist.json");

    private readonly string _path;

    public BackgroundWhitelistStore(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>实际使用的配置文件路径，供界面显示。</summary>
    public string FilePath => _path;

    /// <summary>
    /// 读取勾选名单。文件不存在或损坏时返回<b>空集合</b>，不抛异常。
    /// </summary>
    /// <remarks>
    /// 默认是空的，不是默认名单：勾选意味着「开启游戏模式时会被强制关闭」，
    /// 这是破坏性操作，必须由用户主动勾选，不能替他决定。
    /// 实测教训：曾经默认全选，结果用户一点「开启游戏模式」就关掉了他正在用的软件。
    /// </remarks>
    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];

            var json = File.ReadAllText(_path);
            var saved = JsonSerializer.Deserialize<WhitelistFile>(json, JsonOptions);
            if (saved is null) return [];

            return Normalize(saved.Processes ?? []);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>保存勾选名单。返回是否成功，失败原因交给调用方显示。</summary>
    public bool Save(IEnumerable<string> processNames, out string? error)
    {
        error = null;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new WhitelistFile { Processes = Normalize(processNames).ToList() };
            File.WriteAllText(_path, JsonSerializer.Serialize(payload, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 归一化：去掉空项、重复项与被保护的系统进程。
    /// 保护名单是硬约束，写进配置也不接受。
    /// </summary>
    internal static List<string> Normalize(IEnumerable<string> names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in names)
        {
            var name = raw?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            // 允许用户直接粘贴 "xxx.exe"，统一去掉扩展名
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4].Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (BackgroundProcessWhitelist.IsProtected(name)) continue;
            if (!seen.Add(name)) continue;

            result.Add(name);
        }

        return result;
    }

    private sealed class WhitelistFile
    {
        public List<string> Processes { get; set; } = [];
    }
}
