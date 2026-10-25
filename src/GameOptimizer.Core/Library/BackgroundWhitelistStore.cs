namespace GameOptimizer.Core.Library;

using System.Text.Json;

/// <summary>
/// 白名单的持久化状态。
///
/// <b>两个集合是两件不同的事，不要混为一谈：</b>
/// <list type="bullet">
/// <item><see cref="Protected"/>：用户勾选「保护」的进程 —— 游戏模式绝不结束它们；</item>
/// <item><see cref="EndTargets"/>：用户<b>显式标记</b>为「结束目标」的进程 ——
///       只有它们才有资格在游戏模式启动时被结束，且仍要通过安全检查。</item>
/// </list>
///
/// 为什么结束目标要单独存一份：如果「没勾选保护」就等于「可以结束」，
/// 那么默认状态下（一个都没勾）所有识别得出的后台程序都会被关掉——
/// 这正是 v0.5.1 的事故（默认全选，用户一点就关掉了壁纸引擎）。
/// 所以「不保护」只是<b>有资格</b>，真要结束必须再明确点一次。
/// </summary>
public sealed record BackgroundProtectionState(
    IReadOnlyList<string> Protected,
    IReadOnlyList<string> EndTargets)
{
    public static BackgroundProtectionState Empty { get; } = new([], []);
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
/// 用户自定义的后台进程保护 / 结束目标名单，持久化到本地 JSON。
///
/// <b>文件格式（v2）</b>：
/// <code>
/// {
///   "schemaVersion": 2,
///   "protected":  ["WeChat"],        // 勾选了保护：绝不结束
///   "endTargets": ["wallpaper64"]    // 显式标记为结束目标：只有它们才会被结束
/// }
/// </code>
///
/// <b>旧版格式（v1）</b>：<code>{ "processes": ["wallpaper64"] }</code>——
/// 那时「勾选」的含义是「要结束」，与本版正好相反。
///
/// <b>迁移策略（不允许反转语义）</b>：旧版 <c>processes</c> 里的项
/// <b>按原语义迁到 <see cref="BackgroundProtectionState.EndTargets"/></b>，
/// 保护名单留空，并给出一条提示让用户核对。把旧勾选直接倒成「受保护」等于
/// 悄悄改掉用户当初的决定——他会以为游戏模式还在关那些程序，实际一个都没关。
///
/// <b>无法判断时的保守策略</b>：结构认不出来就当作空配置（一项都不结束），
/// 同时给出提示；绝不因为「读不懂」而退回任何默认名单。
/// </summary>
public sealed class BackgroundWhitelistStore
{
    private const int CurrentSchemaVersion = 2;

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
    /// 读取保护名单与结束目标。
    /// 文件不存在返回空状态（不是默认名单）；损坏/无权限时返回空状态 + <see cref="BackgroundProtectionLoadResult.Error"/>。
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
                    "白名单配置内容为空，已按空配置处理（不会结束任何进程）。", null);
            }

            var legacy = saved.Processes is not null;
            var modern = saved.Protected is not null || saved.EndTargets is not null;

            // 结构完全认不出来：保守处理，一项都不结束
            if (!legacy && !modern)
            {
                return new BackgroundProtectionLoadResult(
                    BackgroundProtectionState.Empty, false,
                    "白名单配置结构无法识别，已按空配置处理（不会结束任何进程），请重新设置后保存。", null);
            }

            var protectedNames = Normalize(saved.Protected ?? []);
            var endTargets = Normalize(saved.EndTargets ?? []);

            if (legacy)
            {
                // 旧版勾选 = 要结束 → 原语义迁入结束目标，绝不倒成「受保护」
                var migrated = Normalize(saved.Processes ?? []);
                foreach (var name in migrated)
                {
                    if (!endTargets.Contains(name, StringComparer.OrdinalIgnoreCase))
                        endTargets.Add(name);
                }

                var notice = migrated.Count == 0
                    ? "检测到旧版白名单配置（其中没有可用项），已迁移为空配置。"
                    : $"检测到旧版白名单配置：{migrated.Count} 项按原语义保留为「结束目标」" +
                      "（旧版勾选的含义是会被结束），没有自动转成保护。请核对后再保存。";

                return new BackgroundProtectionLoadResult(
                    new BackgroundProtectionState(protectedNames, endTargets), true, notice, null);
            }

            return new BackgroundProtectionLoadResult(
                new BackgroundProtectionState(protectedNames, endTargets), false, null, null);
        }
        catch (Exception ex)
        {
            return new BackgroundProtectionLoadResult(
                BackgroundProtectionState.Empty, false, null,
                $"白名单配置无法解析（不会结束任何进程）：{ex.Message}");
        }
    }

    /// <summary>保存保护名单与结束目标。返回是否成功，失败原因交给调用方显示。</summary>
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
                EndTargets = Normalize(state.EndTargets),
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

        /// <summary>v2：勾选保护的进程名。</summary>
        public List<string>? Protected { get; set; }

        /// <summary>v2：显式标记为结束目标的进程名。</summary>
        public List<string>? EndTargets { get; set; }

        /// <summary>v1：勾选即「会结束」。仅用于迁移，不再写入。</summary>
        public List<string>? Processes { get; set; }
    }
}
