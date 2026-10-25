namespace GameOptimizer.Core.Library;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 白名单的持久化状态。
///
/// <b>只存一件事：哪些进程被勾选「保护」。</b>
/// 其余进程默认就是「会被结束」，不需要也不应该再存一份名单——
/// 存了反而会和界面上的实时扫描结果对不上（关掉的软件、新开的软件都不在旧名单里）。
/// </summary>
public sealed record BackgroundProtectionState(IReadOnlyList<string> Protected)
{
    public static BackgroundProtectionState Empty { get; } = new([]);
}

/// <summary>一次读取的结果，含迁移与失败说明。</summary>
/// <param name="State">读到的状态（失败时为空，绝不回落到任何默认名单）。</param>
/// <param name="MigratedFromLegacy">是否从旧版格式迁移而来。</param>
/// <param name="Notice">给用户看的说明（迁移提示 / 结构无法识别的保守处理）。</param>
/// <param name="Error">读取失败原因；为 null 表示读取正常。</param>
public sealed record BackgroundProtectionLoadResult(
    BackgroundProtectionState State,
    bool MigratedFromLegacy,
    string? Notice,
    string? Error)
{
    public static BackgroundProtectionLoadResult Empty { get; } =
        new(BackgroundProtectionState.Empty, false, null, null);
}

/// <summary>
/// 用户自定义的后台进程保护名单，持久化到本地 JSON。
///
/// <b>文件格式（v3）</b>：
/// <code>
/// {
///   "schemaVersion": 3,
///   "protected": ["WeChat"]   // 勾选了保护：绝不结束。其余进程默认会被结束
/// }
/// </code>
///
/// <b>旧版格式</b>：
/// <list type="bullet">
/// <item>v1 <c>{ "processes": ["wallpaper64"] }</c> —— 那时勾选的含义是「要结束」；</item>
/// <item>v2 <c>{ "protected": [...], "endTargets": [...] }</c> —— 需要显式标记结束目标。</item>
/// </list>
///
/// <b>迁移策略</b>：只保留 <c>protected</c>，两份旧名单都<b>丢弃</b>并给出提示。
/// 理由：新语义下「除保护项外都会结束」，旧的两份名单无论怎么映射都不会改变最终行为，
/// 硬留着只会让人以为它们还在起作用。唯一真正影响行为的只有保护名单，那份一定保留。
/// 特别注意<b>绝不把旧的 endTargets / processes 倒成保护</b>——
/// 那会让用户以为「游戏模式还在关那些程序」，实际一个都没关。
///
/// <b>无法判断时的保守策略</b>：结构认不出来就当作空配置（保护为空 = 默认结束所有候选），
/// 同时给出提示，让用户有机会先去勾保护再启动游戏模式。
/// </summary>
public sealed class BackgroundWhitelistStore
{
    private const int CurrentSchemaVersion = 3;


    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // 不写 null 字段：v3 只该出现 schemaVersion 与 protected，
        // 留着 "endTargets": null 会让用户以为这个字段还在起作用
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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
    /// 读取保护名单。
    /// 文件不存在返回空状态（<b>不是默认保护名单</b>，即默认所有候选都会被结束）；
    /// 损坏/无权限时返回空状态 + <see cref="BackgroundProtectionLoadResult.Error"/>。
    /// </summary>
    public BackgroundProtectionLoadResult LoadState()
    {
        if (!File.Exists(_path)) return BackgroundProtectionLoadResult.Empty;

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception ex)
        {
            return new BackgroundProtectionLoadResult(
                BackgroundProtectionState.Empty, false, null, $"读取白名单配置失败：{ex.Message}");
        }

        try
        {
            var saved = JsonSerializer.Deserialize<WhitelistFile>(json, JsonOptions);
            if (saved is null)
            {
                return new BackgroundProtectionLoadResult(
                    BackgroundProtectionState.Empty, false,
                    "白名单配置内容为空，已按空保护名单处理（未勾选保护的进程都会被结束）。", null);
            }

            var v1 = saved.Processes is not null;
            var v2 = saved.EndTargets is not null;
            var modern = saved.Protected is not null;

            if (!v1 && !v2 && !modern)
            {
                return new BackgroundProtectionLoadResult(
                    BackgroundProtectionState.Empty, false,
                    "白名单配置结构无法识别，已按空保护名单处理（未勾选保护的进程都会被结束），请重新设置保护项后保存。", null);
            }

            var protectedNames = Normalize(saved.Protected ?? []);
            var state = new BackgroundProtectionState(protectedNames);

            if (v1)
            {
                // 旧版勾选 = 要结束。新语义下「没勾保护就会结束」，旧名单已经不起作用，
                // 但它恰恰说明这些进程**原本就在结束范围**里——与默认行为一致，所以丢弃是安全的。
                var dropped = Normalize(saved.Processes ?? []);
                var notice = dropped.Count == 0
                    ? "检测到旧版白名单配置（其中没有可用项），已按新规则处理。"
                    : $"检测到旧版白名单配置：旧清单里的 {dropped.Count} 项在新规则下本来就属于结束范围" +
                      "（现在除勾选保护的程序外都会结束），因此清单已丢弃，保护名单不变。请核对保护项后再启动游戏模式。";

                return new BackgroundProtectionLoadResult(state, true, notice, null);
            }

            if (v2)
            {
                var dropped = Normalize(saved.EndTargets ?? []);
                var notice = dropped.Count == 0
                    ? "检测到旧版「结束目标」配置，已按新规则处理。"
                    : $"旧版的 {dropped.Count} 项「结束目标」已并入默认行为：现在未勾选保护的程序都会被结束，" +
                      "这份清单不再需要，已丢弃。保护名单保持不变。";

                return new BackgroundProtectionLoadResult(state, true, notice, null);
            }

            return new BackgroundProtectionLoadResult(state, false, null, null);
        }
        catch (Exception ex)
        {
            return new BackgroundProtectionLoadResult(
                BackgroundProtectionState.Empty, false, null,
                $"白名单配置无法解析（按空保护名单处理）：{ex.Message}");
        }
    }

    /// <summary>保存保护名单。返回是否成功，失败原因交给调用方显示。</summary>
    public bool SaveState(BackgroundProtectionState state, out string? error)
    {
        error = null;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new WhitelistFile
            {
                SchemaVersion = CurrentSchemaVersion,
                Protected = Normalize(state.Protected),
            };

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
        /// <summary>v2 起写入；v1 文件里没有这个字段（读出来是 0）。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>v2 起：勾选保护的进程名。v3 只写这一个字段。</summary>
        public List<string>? Protected { get; set; }

        /// <summary>v2：显式标记为结束目标的进程名。仅用于识别旧格式，不再写入。</summary>
        public List<string>? EndTargets { get; set; }

        /// <summary>v1：勾选即「会结束」。仅用于识别旧格式，不再写入。</summary>
        public List<string>? Processes { get; set; }
    }
}
