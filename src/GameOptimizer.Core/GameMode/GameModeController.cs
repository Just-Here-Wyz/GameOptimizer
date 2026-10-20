namespace GameOptimizer.Core.GameMode;

using System.Diagnostics;
using GameOptimizer.Core.Library;
using Microsoft.Win32;

/// <summary>一次可回滚的改动。每项都必须带 Apply 与 Revert。</summary>
public interface IGameModeAction
{
    string Name { get; }
    string Description { get; }

    /// <summary>执行前先记录当前状态，用于回滚。</summary>
    void Apply();

    /// <summary>恢复到执行前的状态。</summary>
    void Revert();

    /// <summary>当前是否处于"已优化"状态。</summary>
    bool IsActive();
}

/// <summary>切换电源计划到高性能。可回滚。</summary>
public sealed class PowerPlanAction : IGameModeAction
{
    private const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private string? _previousGuid;

    public string Name => "高性能电源计划";
    public string Description => "把电源计划切到高性能，避免 CPU/GPU 降频。";

    public void Apply()
    {
        _previousGuid = CurrentPlanGuid();
        RunPowerCfg($"/setactive {HighPerformanceGuid}");

        // 关键：powercfg 失败时退出码并不总是可靠，必须回读确认真的切过去了，
        // 否则界面会显示"已开启"而系统毫无变化（实测在受限令牌下正是如此）。
        if (!IsActive())
            throw new InvalidOperationException(
                $"电源计划切换未生效，当前仍为 {CurrentPlanGuid() ?? "未知"}。可能需要管理员权限。");
    }

    public void Revert()
    {
        if (_previousGuid is null) return;
        RunPowerCfg($"/setactive {_previousGuid}");

        if (!CurrentPlanGuid()?.Equals(_previousGuid, StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException($"电源计划还原失败，原计划为 {_previousGuid}。");
    }

    public bool IsActive() => CurrentPlanGuid()?.Equals(HighPerformanceGuid, StringComparison.OrdinalIgnoreCase) == true;

    internal static string? CurrentPlanGuid()
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return ParseGuid(output);
        }
        catch { return null; }
    }

    /// <summary>
    /// 从 powercfg 输出里取第一个 GUID。
    /// 不能用固定偏移量截取：中文系统输出形如「电源方案 GUID: xxxx  (高性能)」，
    /// "GUID:" 后面紧跟的就是空格，偏移法会把整行都当成 GUID，
    /// 导致 IsActive() 永远为 false、进而把刚切好的计划又回滚掉。
    /// </summary>
    internal static string? ParseGuid(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            output, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value : null;
    }

    private static void RunPowerCfg(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", args) { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            // 不再静默吞掉：调用失败必须让上层知道，否则会向用户报虚假成功
            throw new InvalidOperationException($"调用 powercfg {args} 失败：{ex.Message}", ex);
        }
    }
}

/// <summary>
/// 设置显卡性能偏好为"高性能"，需要写入注册表，可回滚。
///
/// 保留这个类只是为了老配置能反序列化；游戏模式已不再启用它。
/// 原因：核显机器上没有可调度的独立 GPU，写这个键不会有任何效果——
/// 实测本机是 Intel Iris Xe，这项动作此前既不生效也没被察觉，
/// 属于「看起来优化了，实际什么都没做」的空转项，已从游戏模式移除。
/// </summary>
public sealed class GpuPreferenceAction : IGameModeAction
{
    private const string SubKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private object? _previousValue;

    public GpuPreferenceAction(string? targetApp = null) => TargetApp = targetApp;

    /// <summary>要设置偏好的 exe 完整路径。为空时本动作不做任何事。</summary>
    public string? TargetApp { get; set; }

    public string Name => "显卡性能偏好";
    public string Description => "把游戏的显卡偏好设为高性能（仅对独显机器有效）。";

    public void Apply()
    {
        if (string.IsNullOrWhiteSpace(TargetApp)) return;

        using var key = Registry.CurrentUser.CreateSubKey(SubKey, writable: true);
        if (key is null) return;

        _previousValue = key.GetValue(TargetApp);
        key.SetValue(TargetApp, "GpuPreference=2;");
    }

    public void Revert()
    {
        if (string.IsNullOrWhiteSpace(TargetApp)) return;
        using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: true);
        if (key is null) return;
        if (_previousValue is null) key.DeleteValue(TargetApp, throwOnMissingValue: false);
        else key.SetValue(TargetApp, _previousValue);
    }

    public bool IsActive()
    {
        if (string.IsNullOrWhiteSpace(TargetApp)) return false;
        using var key = Registry.CurrentUser.OpenSubKey(SubKey);
        return (key?.GetValue(TargetApp) as string)?.Contains("GpuPreference=2") == true;
    }
}

/// <summary>
/// 关闭 Xbox Game Bar 的后台录制（Game DVR）。
///
/// 保留这个类只是为了不丢掉已写好的实现；游戏模式已不再自动启用它。
/// 用户要求游戏模式只做「关闭不必要的后台进程」这一件事，
/// 不掺注册表改动，行为越少越好追溯。
/// </summary>
public sealed class GameDvrAction : IGameModeAction
{
    private const string GameConfigStore = @"System\GameConfigStore";
    private const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

    private int? _prevStoreEnabled;
    private int? _prevAppCapture;
    private int? _prevFseBehavior;

    public string Name => "关闭后台录制";
    public string Description => "关闭 Xbox Game Bar 的 Game DVR 后台录制，退出时恢复原设置。";

    public void Apply()
    {
        // 先记录原值再改，任何一步读不到都让上层知道
        using var store = Registry.CurrentUser.OpenSubKey(GameConfigStore, writable: true)
            ?? throw new InvalidOperationException("读不到 GameConfigStore 注册表项。");
        using var dvr = Registry.CurrentUser.CreateSubKey(GameDvr, writable: true)
            ?? throw new InvalidOperationException("无法打开 GameDVR 注册表项。");

        _prevStoreEnabled = AsInt(store.GetValue("GameDVR_Enabled"));
        _prevFseBehavior = AsInt(store.GetValue("GameDVR_FSEBehavior"));
        _prevAppCapture = AsInt(dvr.GetValue("AppCaptureEnabled"));

        store.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);
        // FSEBehavior=2 让游戏走独占全屏，减少一处 DWM 合成开销
        store.SetValue("GameDVR_FSEBehavior", 2, RegistryValueKind.DWord);
        dvr.SetValue("AppCaptureEnabled", 0, RegistryValueKind.DWord);

        if (!IsActive())
            throw new InvalidOperationException("关闭后台录制后回读不到，可能被组策略接管。");
    }

    public void Revert()
    {
        Restore(GameConfigStore, "GameDVR_Enabled", _prevStoreEnabled);
        Restore(GameConfigStore, "GameDVR_FSEBehavior", _prevFseBehavior);
        Restore(GameDvr, "AppCaptureEnabled", _prevAppCapture);
    }

    public bool IsActive()
    {
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(GameConfigStore);
            using var dvr = Registry.CurrentUser.OpenSubKey(GameDvr);
            return AsInt(store?.GetValue("GameDVR_Enabled")) == 0
                   && AsInt(dvr?.GetValue("AppCaptureEnabled")) == 0;
        }
        catch { return false; }
    }

    private static int? AsInt(object? value) => value is int i ? i : null;

    /// <summary>原值缺失就删掉，不要写一个 0 冒充「原本就是关的」。</summary>
    private static void Restore(string subKey, string name, int? previous)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        if (key is null) return;
        if (previous is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, previous.Value, RegistryValueKind.DWord);
    }
}

/// <summary>结束白名单内的后台进程。必须在回滚时如实告知"进程无法自动恢复"。</summary>
public sealed class EndProcessAction : IGameModeAction
{
    private readonly List<KilledProcess> _killed = [];
    private readonly List<BlockedProcess> _blocked = [];
    private readonly List<BlockedProcess> _skipped = [];
    private readonly List<RestoredProcess> _restored = [];
    private IReadOnlySet<string>? _serviceNames;

    public IReadOnlyList<string> Whitelist { get; }
    public string Name => "关闭后台干扰进程";
    public string Description => "结束白名单内的浏览器、壁纸引擎、管家遥测等；关闭游戏模式时自动恢复。";

    public EndProcessAction(IReadOnlyList<string> whitelist) => Whitelist = whitelist;

    /// <summary>本动作实际结束掉的进程。</summary>
    public IReadOnlyList<KilledProcess> Killed => _killed;

    /// <summary>退出时自动恢复的进程（进程名 + 启动命令）。</summary>
    public IReadOnlyList<RestoredProcess> Restored => _restored;

    /// <summary>
    /// 被判定为系统必要、主动跳过的进程。
    /// 例：以服务身份运行的 wallpaperservice64、telemetry_agent。
    /// 它们本来也杀不掉，但更重要的是「不该杀」——服务被强行终止会牵动整个服务栈。
    /// </summary>
    public IReadOnlyList<BlockedProcess> Skipped => _skipped;

    /// <summary>尝试结束但被拒绝访问的进程。</summary>
    public IReadOnlyList<BlockedProcess> Blocked => _blocked;

    public void Apply()
    {
        _killed.Clear();
        _blocked.Clear();
        _skipped.Clear();
        _restored.Clear();

        _serviceNames ??= EssentialProcessGuard.LoadServiceImageNames();

        foreach (var name in Whitelist)
        {
            // 硬编码兜底名单：连进程都不必枚举
            if (BackgroundProcessWhitelist.IsProtected(name)) continue;

            Process[] targets;
            try { targets = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in targets)
            {
                try
                {
                    // 按客观特征判断必要进程：Session 0、注册服务、系统目录下的组件。
                    // 这类直接跳过，不能只靠名字白名单——写死的名单永远会漏。
                    var identity = EssentialProcessGuard.Identify(p);
                    if (EssentialProcessGuard.IsEssential(identity, _serviceNames))
                    {
                        _skipped.Add(new BlockedProcess(name, DescribeEssential(identity)));
                        continue;
                    }

                    var mb = 0.0;
                    try { mb = p.WorkingSet64 / (1024.0 * 1024.0); } catch { }

                    // 结束前先记下可执行文件路径，退出游戏模式时按它恢复。
                    // 读不到路径的进程不自动恢复——没有可靠的启动入口，
                    // 硬猜一个同名命令可能拉起错误程序，如实告知比猜更诚实。
                    var launchPath = identity.ExecutablePath;

                    p.Kill();
                    p.WaitForExit(1000);

                    // Kill 不抛异常也可能没真的结束，必须回读确认，
                    // 否则会向用户报一次虚假的成功。
                    if (!p.HasExited)
                        _blocked.Add(new BlockedProcess(name, "进程仍在运行"));
                    else
                    {
                        _killed.Add(new KilledProcess(name, mb));
                        if (!string.IsNullOrWhiteSpace(launchPath))
                            _restored.Add(new RestoredProcess(name, launchPath));
                    }
                }
                catch (Exception ex)
                {
                    _blocked.Add(new BlockedProcess(name, ex.Message));
                }
                finally { p.Dispose(); }
            }
        }
    }

    private static string DescribeEssential(EssentialProcessGuard.ProcessIdentity identity)
    {
        if (identity.SessionId == 0) return "系统服务会话";
        if (identity.IsService) return "已注册服务";
        if (EssentialProcessGuard.IsUnderSystemRoot(identity.ExecutablePath,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
            return "Windows 系统组件";
        return "系统必要进程";
    }

    /// <summary>
    /// 退出游戏模式：恢复所有被结束、且可执行文件路径已知的进程。
    /// 恢复失败的进程会被保留在上报列表里，绝不静默假装恢复了。
    /// </summary>
    public void Revert()
    {
        foreach (var r in _restored)
        {
            if (r.Launched) continue;

            try
            {
                Process.Start(new ProcessStartInfo(r.LaunchPath) { UseShellExecute = true });
                r.MarkLaunched();
            }
            catch (Exception ex)
            {
                r.MarkFailed(ex.Message);
            }
        }

        // 已恢复 / 恢复失败的处理完毕，清掉「已结束」状态；
        // _restored 保留到下次 Apply 重置，供界面查询恢复结果
        // （Revert 不重复启动：每个记录带 Launched 标记）
        _killed.Clear();
    }

    /// <summary>退出游戏模式后没能恢复的进程，界面必须告诉用户。</summary>
    public IReadOnlyList<RestoredProcess> FailedRestores
        => _restored.Where(r => r.Failed).ToList();

    /// <summary>退出游戏模式后成功恢复的进程数。</summary>
    public int RestoredCount => _restored.Count(r => r.Launched && !r.Failed);

    /// <summary>只有真的结束过进程才算生效，否则界面会显示「已开启」而实际没做任何事。</summary>
    public bool IsActive() => _killed.Count > 0;

    /// <summary>释放的内存总量（MB），用于告诉用户这一项到底有没有用。</summary>
    public double FreedMb => _killed.Sum(k => k.WorkingSetMb);

    /// <summary>形如「已结束 3 个进程，释放约 246 MB（自动跳过 5 项系统必要进程）」。</summary>
    public string Summary
    {
        get
        {
            var text = _killed.Count == 0
                ? "没有可结束的后台进程"
                : $"已结束 {_killed.Count} 个进程，释放约 {FreedMb:F0} MB";

            if (_skipped.Count > 0)
                text += $"（自动跳过 {_skipped.Count} 项系统必要进程）";
            if (_blocked.Count > 0)
                text += $"（{_blocked.Count} 项拒绝访问）";

            return text;
        }
    }
}

/// <summary>一个被结束的进程，记录名字与占用，便于事后说明。</summary>
public sealed record KilledProcess(string Name, double WorkingSetMb);

/// <summary>
/// 一个在退出游戏模式时要恢复的进程。
/// 只有拿到可执行文件路径的才会创建——恢复必须按原路径拉起，
/// 路径读不到的进程如实告知「未恢复」，不靠猜命令。
/// </summary>
public sealed class RestoredProcess
{
    public RestoredProcess(string name, string launchPath)
    {
        Name = name;
        LaunchPath = launchPath;
    }

    public string Name { get; }

    /// <summary>被结束进程的可执行文件完整路径，恢复时用它拉起。</summary>
    public string LaunchPath { get; }

    /// <summary>恢复已执行（成功或失败都算执行过，避免重复启动）。</summary>
    public bool Launched { get; private set; }

    public bool Failed => !string.IsNullOrEmpty(Error);

    public string? Error { get; private set; }

    internal void MarkLaunched() => Launched = true;

    internal void MarkFailed(string error)
    {
        Launched = true;
        Error = error;
    }
}

/// <summary>结束失败且原因不是「进程不存在」的项。服务进程会拒绝结束。</summary>
public sealed record BlockedProcess(string Name, string Reason);

/// <summary>游戏模式总控：按顺序执行，失败即回滚已执行的，保证不留下半套改动。</summary>
public sealed class GameModeController
{
    private readonly List<IGameModeAction> _actions = [];
    private readonly List<IGameModeAction> _applied = [];

    public bool IsActive { get; private set; }
    public IReadOnlyList<IGameModeAction> Actions => _actions;
    public IReadOnlyList<IGameModeAction> Applied => _applied;

    /// <summary>本次开启时的力度，供界面展示。</summary>
    public GameModeStrength Strength { get; private set; } = BackgroundProcessWhitelist.DefaultStrength;

    public void Add(IGameModeAction action) => _actions.Add(action);

    /// <summary>换掉某个类型的动作（例如按力度重建结束进程动作）。</summary>
    public void Replace<T>(IGameModeAction replacement) where T : IGameModeAction
    {
        for (var i = 0; i < _actions.Count; i++)
        {
            if (_actions[i] is T) { _actions[i] = replacement; return; }
        }
        _actions.Add(replacement);
    }

    /// <summary>按指定力度开启。</summary>
    public IReadOnlyList<string> Enter(GameModeStrength strength)
    {
        Strength = strength;
        return Enter();
    }

    /// <returns>成功返回空列表；失败返回失败原因，且已自动回滚。</returns>
    public IReadOnlyList<string> Enter()
    {
        var errors = new List<string>();
        foreach (var a in _actions)
        {
            try
            {
                a.Apply();
                _applied.Add(a);
            }
            catch (Exception ex)
            {
                errors.Add($"{a.Name} 失败：{ex.Message}");
                Exit(); // 失败即回滚，不留下半套状态
                return errors;
            }
        }
        IsActive = true;
        return errors;
    }

    public void Exit() => ExitWithReport();

    /// <summary>
    /// 退出游戏模式并返回回滚失败项。回滚失败必须让用户看见——
    /// 电源计划没还原却提示"已还原"比不还原更糟。
    /// </summary>
    public IReadOnlyList<string> ExitWithReport()
    {
        var errors = new List<string>();
        for (var i = _applied.Count - 1; i >= 0; i--)
        {
            try { _applied[i].Revert(); }
            catch (Exception ex) { errors.Add($"{_applied[i].Name} 还原失败：{ex.Message}"); }
        }
        _applied.Clear();
        IsActive = false;
        return errors;
    }
}
