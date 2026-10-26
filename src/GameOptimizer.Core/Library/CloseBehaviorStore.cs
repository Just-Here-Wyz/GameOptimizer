namespace GameOptimizer.Core.Library;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>一次读取的结果：读到的行为 + 需要让用户知道的说明或错误。</summary>
/// <param name="Behavior">读到的行为；任何异常情况都是 <see cref="CloseBehavior.Ask"/>。</param>
/// <param name="Notice">不是错误、但值得说一声的情况（旧格式、认不出的值）。</param>
/// <param name="Error">读取 / 解析失败原因；为 null 表示读取正常。</param>
public sealed record CloseBehaviorLoadResult(CloseBehavior Behavior, string? Notice, string? Error)
{
    /// <summary>默认结果：没有配置 = 每次询问，不需要向用户解释什么。</summary>
    public static CloseBehaviorLoadResult Ask { get; } = new(CloseBehavior.Ask, null, null);
}

/// <summary>
/// 「点 × 时怎么处理」的本地持久化。
///
/// <b>文件格式（v1）</b>：
/// <code>
/// {
///   "schemaVersion": 1,
///   "behavior": "minimizeToTray"   // ask / minimizeToTray / exit
/// }
/// </code>
/// 路径与其余配置一致：<c>%LocalAppData%\GameOptimizer\close-behavior.json</c>。
/// 刻意不写进安装目录——那里需要管理员权限，而且升级 / 卸载会被整目录带走。
///
/// <b>为什么存字符串而不是数字</b>：这份文件是用户可能打开来改的，
/// <c>"minimizeToTray"</c> 比 <c>1</c> 自解释；将来加行为也不用重排编号。
///
/// <b>失败一律回退到「每次询问」</b>：读不出来就多问一次，代价是点一次按钮；
/// 猜错方向的代价是程序自己退出（用户以为崩了）或者关不掉（用户以为卡死）。
/// 这个不对称决定了默认值只能是 Ask。
/// </summary>
public sealed class CloseBehaviorStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>默认路径：%LocalAppData%\GameOptimizer\close-behavior.json</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameOptimizer",
        "close-behavior.json");

    private readonly string _path;

    public CloseBehaviorStore(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>实际使用的配置文件路径，设置页要显示出来方便用户核对。</summary>
    public string FilePath => _path;

    /// <summary>
    /// 读取保存的行为。文件不存在返回 Ask（不是错误）；
    /// 损坏 / 无权限 / 值认不出来时返回 Ask，并把原因或说明带回给界面。
    /// </summary>
    public CloseBehaviorLoadResult Load()
    {
        if (!File.Exists(_path)) return CloseBehaviorLoadResult.Ask;

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception ex)
        {
            return new CloseBehaviorLoadResult(
                CloseBehavior.Ask, null, $"读取关闭行为配置失败（已按每次询问处理）：{ex.Message}");
        }

        BehaviorFile? file;
        try
        {
            file = JsonSerializer.Deserialize<BehaviorFile>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            return new CloseBehaviorLoadResult(
                CloseBehavior.Ask, null, $"关闭行为配置无法解析（已按每次询问处理）：{ex.Message}");
        }

        if (file is null)
        {
            return new CloseBehaviorLoadResult(
                CloseBehavior.Ask, "关闭行为配置内容为空，已按每次询问处理。", null);
        }

        // 来自更新版本的配置：字段含义可能已经变了，不猜，退回每次询问
        if (file.SchemaVersion > CurrentSchemaVersion)
        {
            return new CloseBehaviorLoadResult(
                CloseBehavior.Ask,
                $"关闭行为配置来自更新的版本（schemaVersion {file.SchemaVersion}），已按每次询问处理；" +
                "在设置页重新选择一次即可写回当前格式。", null);
        }

        if (string.IsNullOrWhiteSpace(file.Behavior))
        {
            return new CloseBehaviorLoadResult(
                CloseBehavior.Ask, "关闭行为配置里没有可用取值，已按每次询问处理。", null);
        }

        if (TryParseBehavior(file.Behavior, out var behavior)) return new CloseBehaviorLoadResult(behavior, null, null);

        return new CloseBehaviorLoadResult(
            CloseBehavior.Ask,
            $"关闭行为配置里的取值「{file.Behavior}」无法识别，已按每次询问处理。", null);
    }

    /// <summary>
    /// 保存行为。返回是否成功，失败原因交给调用方显示（不抛异常：
    /// 一个写不进去的配置不该让用户连「退出程序」都点不了）。
    /// </summary>
    public bool Save(CloseBehavior behavior, out string? error)
    {
        error = null;

        if (!TryFormatBehavior(behavior, out var text))
        {
            error = $"无法识别的关闭行为：{behavior}";
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var payload = new BehaviorFile { SchemaVersion = CurrentSchemaVersion, Behavior = text };
            var json = JsonSerializer.Serialize(payload, JsonOptions);

            // 先写 .tmp 再同目录改名：断电时要么是旧文件、要么是完整的新文件
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>枚举 → 配置文件里的字符串。认不出的值返回 false（不写盘）。</summary>
    internal static bool TryFormatBehavior(CloseBehavior behavior, out string text)
    {
        switch (behavior)
        {
            case CloseBehavior.Ask: text = "ask"; return true;
            case CloseBehavior.MinimizeToTray: text = "minimizeToTray"; return true;
            case CloseBehavior.Exit: text = "exit"; return true;
            default: text = ""; return false;
        }
    }

    /// <summary>
    /// 配置文件里的字符串 → 枚举。大小写不敏感（手改配置的人不该被大小写坑到），
    /// 同步接受枚举名本身（如 "MinimizeToTray"）。
    /// </summary>
    internal static bool TryParseBehavior(string? text, out CloseBehavior behavior)
    {
        behavior = CloseBehavior.Ask;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.Equals("ask", StringComparison.OrdinalIgnoreCase))
        {
            behavior = CloseBehavior.Ask;
            return true;
        }

        if (trimmed.Equals("minimizeToTray", StringComparison.OrdinalIgnoreCase))
        {
            behavior = CloseBehavior.MinimizeToTray;
            return true;
        }

        if (trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            behavior = CloseBehavior.Exit;
            return true;
        }

        return false;
    }

    private sealed class BehaviorFile
    {
        public int SchemaVersion { get; set; }

        /// <summary>ask / minimizeToTray / exit。用字符串以便用户手改时看得懂。</summary>
        public string? Behavior { get; set; }
    }
}
