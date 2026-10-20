namespace GameOptimizer.Core.GameMode;

using System.Text.Json;

/// <summary>
/// 游戏模式活动状态的持久化快照，用于崩溃 / 任务管理器强杀后的兜底还原。
///
/// 程序被强杀时 OnExit 不会执行，电源计划可能永久停在高性能、
/// 被结束的进程也永远不会被拉起。进入游戏模式时把「回滚所需的全部信息」
/// 落盘，下次启动检测到这份文件就能提示用户还原。
///
/// 路径约定与 BackgroundWhitelistStore 一致：%LocalAppData%\GameOptimizer\。
/// </summary>
public sealed class GameModeStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>默认路径：%LocalAppData%\GameOptimizer\active-state.json</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameOptimizer",
        "active-state.json");

    private readonly string _path;

    public GameModeStateStore(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>实际使用的快照文件路径，供界面显示。</summary>
    public string FilePath => _path;

    /// <summary>快照文件是否存在（存在即意味着上次退出不干净）。</summary>
    public bool Exists() => File.Exists(_path);

    /// <summary>
    /// 读取快照。文件不存在返回 null；文件损坏时抛异常——
    /// 兜底信息不可信时必须让调用方看见，不能装作没有残留。
    /// </summary>
    public GameModeSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<GameModeSnapshot>(File.ReadAllText(_path), JsonOptions);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"游戏模式残留状态文件已损坏（{_path}），请手动确认电源计划与进程。", ex);
        }
    }

    /// <summary>保存快照。失败抛异常：兜底信息写不进去必须让上层知道。</summary>
    public void Save(GameModeSnapshot snapshot)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    /// <summary>清除快照。文件已不存在时静默；写入类失败抛异常。</summary>
    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>游戏模式活动状态的落盘快照。</summary>
public sealed class GameModeSnapshot
{
    /// <summary>进入游戏模式前正在使用的电源计划 GUID。</summary>
    public string? PreviousPowerPlanGuid { get; set; }

    /// <summary>被结束、需要在崩溃恢复时重新拉起的进程。</summary>
    public List<GameModeKilledProcess> Killed { get; set; } = [];
}

/// <summary>崩溃恢复要重新拉起的进程：名字 + 可执行文件完整路径。</summary>
public sealed class GameModeKilledProcess
{
    public string Name { get; set; } = "";
    public string LaunchPath { get; set; } = "";
}
