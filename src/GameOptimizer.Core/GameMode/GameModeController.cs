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

/// <summary>
/// 能提供「待恢复进程」的动作。
///
/// 抽成接口的两个理由：
/// 1. 控制器不必认识 <see cref="EndProcessAction"/> 就能实现「关闭后先不恢复」；
/// 2. 测试不必真的结束一个真实进程，也能验证延迟恢复与快照保留的逻辑。
/// </summary>
public interface IProcessRestoreSource
{
    /// <summary>
    /// 退出时是否自动按原路径拉起进程。
    /// false 表示「先只关游戏模式，恢复哪些交给用户在弹窗里选」。
    /// </summary>
    bool AutoRestoreOnRevert { get; set; }

    /// <summary>已结束、尚未恢复的进程记录。</summary>
    IReadOnlyList<RestoredProcess> PendingRestores { get; }

    /// <summary>全部恢复记录（含已恢复 / 已失败），用于统计与上报。</summary>
    IReadOnlyList<RestoredProcess> Restored { get; }
}

/// <summary>切换电源计划到高性能。可回滚。</summary>
public sealed class PowerPlanAction : IGameModeAction
{
    private const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private string? _previousGuid;

    public string Name => "高性能电源计划";
    public string Description => "把电源计划切到高性能，避免 CPU/GPU 降频。";

    /// <summary>进入游戏模式前的电源计划 GUID，供崩溃兜底快照使用。</summary>
    public string? PreviousGuid => _previousGuid;

    public void Apply()
    {
        _previousGuid = CurrentPlanGuid();
        EnsurePreviousReadable(_previousGuid);
        RunPowerCfg($"/setactive {HighPerformanceGuid}");

        // 关键：powercfg 失败时退出码并不总是可靠，必须回读确认真的切过去了，
        // 否则界面会显示"已开启"而系统毫无变化（实测在受限令牌下正是如此）。
        if (!IsActive())
            throw new InvalidOperationException(
                $"电源计划切换未生效，当前仍为 {CurrentPlanGuid() ?? "未知"}。可能需要管理员权限。");
    }

    /// <summary>回读不到原计划时不允许切换：没有可回滚的起点，失败会留下半套状态。</summary>
    internal static void EnsurePreviousReadable(string? previous)
    {
        if (previous is null)
            throw new InvalidOperationException("无法回读当前电源计划，不能保证可回滚，已取消切换。");
    }

    public void Revert()
    {
        if (_previousGuid is null) return;
        RunPowerCfg($"/setactive {_previousGuid}");

        // 回读失败必须可见：CurrentPlanGuid() 内部会吞掉异常返回 null，
        // null?.Equals(...)==true 为 false 时不抛，用户会以为还原成功而实际没有。
        EnsureReverted(CurrentPlanGuid(), _previousGuid);
    }

    /// <summary>还原后回读校验；任何失败都必须抛错，不能静默假装还原成功。</summary>
    internal static void EnsureReverted(string? current, string previous)
    {
        if (current is null)
            throw new InvalidOperationException($"无法回读当前电源计划，请手动确认是否已还原为 {previous}。");
        if (!current.Equals(previous, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"电源计划还原失败，原计划为 {previous}。");
    }

    /// <summary>
    /// 崩溃恢复用的电源计划还原：切回指定 GUID 并回读验证。
    /// 成功返回 null；回读不到或没切过去时返回原因，绝不报虚假成功。
    /// </summary>
    public static string? RestorePlan(string guid)
    {
        RunPowerCfg($"/setactive {guid}");
        var current = CurrentPlanGuid();
        if (current is null)
            return "无法回读当前电源计划，请手动确认";
        return current.Equals(guid, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"切换未生效，当前仍为 {current}";
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

/// <summary>
/// 结束白名单内的后台进程。必须在回滚时如实告知"进程无法自动恢复"。
///
/// <b>语义</b>（v2.4 起）：「白名单」在这里指<b>用户显式标记的结束目标</b>，
/// 不是「勾选保护的项」。保护项由 <c>ProtectedNames</c> 单独传入并在此处再拦一次——
/// 界面已经把它们排除在目标之外，这里再拦是为了防止任何调用方绕过界面直接结束受保护进程
/// （「界面写受保护、实际仍被结束」是本次改造明确要杜绝的事）。
/// </summary>
public sealed class EndProcessAction : IGameModeAction, IProcessRestoreSource
{
    private readonly List<KilledProcess> _killed = [];
    private readonly List<BlockedProcess> _blocked = [];
    private readonly List<BlockedProcess> _skipped = [];
    private readonly List<RestoredProcess> _restored = [];
    private readonly HashSet<string> _protected;
    private IReadOnlySet<string>? _serviceNames;

    public EndProcessAction(IReadOnlyList<string> whitelist, IEnumerable<string>? protectedNames = null)
    {
        Whitelist = whitelist;
        _protected = new HashSet<string>(
            protectedNames ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Whitelist { get; }

    /// <summary>用户勾选保护的进程名。即使出现在 <see cref="Whitelist"/> 里也一律不结束。</summary>
    public IReadOnlyList<string> ProtectedNames => [.. _protected];

    public string Name => "关闭后台干扰进程";
    public string Description => "结束你标记为结束目标的后台程序；勾选保护的一律不动，关闭游戏模式时由你选择恢复哪些。";

    /// <summary>
    /// 退出时是否自动按原路径拉起被结束的进程。
    ///
    /// 默认 true：托盘退出、崩溃兜底这类没有界面的路径没得选，只能全恢复；
    /// 走恢复选择弹窗时必须先置 false——<b>先关游戏模式，再由用户决定恢复谁</b>，
    /// 否则弹窗还没打开，进程就已经全被拉起来了，选择权形同虚设。
    /// </summary>
    public bool AutoRestoreOnRevert { get; set; } = true;

    /// <summary>
    /// 还没<b>成功</b>恢复的进程：既包括还没处理的，也包括处理过但失败 / 未确认的。
    ///
    /// 失败项必须留在这里，否则「有程序没回来」这件事在下次启动时就彻底没记录了；
    /// 重复启动的风险由恢复资格里的「已在运行」判据挡掉。
    /// </summary>
    public IReadOnlyList<RestoredProcess> PendingRestores
        => [.. _restored.Where(r => !r.Launched || r.Failed)];

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

    /// <summary>
    /// 用户勾选保护、因而被拦下的进程（即使调用方把名字放进了目标列表）。
    /// 正常情况下为空——界面算目标时已排除；非空说明有调用方绕过了界面。
    /// </summary>
    public IReadOnlyList<BlockedProcess> ProtectedSkipped => _protectedSkipped;
    private readonly List<BlockedProcess> _protectedSkipped = [];

    public void Apply()
    {
        _killed.Clear();
        _blocked.Clear();
        _skipped.Clear();
        _restored.Clear();
        _protectedSkipped.Clear();

        _serviceNames ??= EssentialProcessGuard.LoadServiceImageNames();

        foreach (var name in Whitelist)
        {
            // 硬编码兜底名单：连进程都不必枚举
            if (BackgroundProcessWhitelist.IsProtected(name)) continue;

            // 用户勾选保护的项：无论调用方怎么传，都不结束
            if (_protected.Contains(name))
            {
                _protectedSkipped.Add(new BlockedProcess(name, ProtectedReason));
                continue;
            }

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
                    if (EssentialProcessGuard.IsEssential(identity, _serviceNames, out var reason))
                    {
                        _skipped.Add(new BlockedProcess(name, reason ?? "系统必要进程"));
                        continue;
                    }

                    var mb = 0.0;
                    try { mb = p.WorkingSet64 / (1024.0 * 1024.0); } catch { }

                    // 结束前先记下可执行文件路径，退出游戏模式时按它恢复。
                    // 读不到路径的进程不自动恢复——没有可靠的启动入口，
                    // 硬猜一个同名命令可能拉起错误程序，如实告知比猜更诚实。
                    var launchPath = identity.ExecutablePath;

                    // Chrome/WeChat/壁纸引擎这类大应用 1 秒内退不完，WaitForExit(1000)
                    // 会把「还在退出中」误判成结束失败；放宽到 5 秒并补等一次。
                    switch (TryKill(p))
                    {
                        case KillResult.Exited:
                            _killed.Add(new KilledProcess(name, mb));
                            if (!string.IsNullOrWhiteSpace(launchPath))
                                _restored.Add(new RestoredProcess(name, launchPath));
                            break;
                        case KillResult.Stuck:
                            // Kill 没抛异常但进程仍在运行：如实记录，不进 _restored——
                            // 退出游戏模式时不该去拉起一个还活着的进程。
                            _blocked.Add(new BlockedProcess(name, StuckReason));
                            break;
                        case KillResult.Denied:
                            _blocked.Add(new BlockedProcess(name, "拒绝访问"));
                            break;
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

    /// <summary>结束一个进程的结果：已退出 / 超时仍运行 / 被拒绝访问。</summary>
    internal enum KillResult
    {
        Exited,
        Stuck,
        Denied,
    }

    private const string StuckReason = "进程仍在运行";

    private const string ProtectedReason = "已勾选保护";

    /// <summary>
    /// Kill 并回读确认结果。Kill 抛出的异常由调用方 catch 统一处理。
    /// </summary>
    internal static KillResult TryKill(Process p)
    {
        try
        {
            p.Kill();
        }
        catch (UnauthorizedAccessException)
        {
            // 只有这里才是真正的「拒绝访问」；Kill 的其余异常也归 Denied，
            // 原因文本由调用方的 catch 记录。
            return KillResult.Denied;
        }
        catch (InvalidOperationException) when (p.HasExited)
        {
            // 进程在我们动手前已经退出：目标状态达成，不算失败
            return KillResult.Exited;
        }

        // 大应用退出慢，1 秒会误判；5 秒内真退出即为成功，再补等 500ms 仍未退
        // 说明 Kill 没生效（例如被安全软件挂起），不得报成功。
        if (p.WaitForExit(5000)) return KillResult.Exited;
        return p.WaitForExit(500) ? KillResult.Exited : KillResult.Stuck;
    }

    /// <summary>
    /// 退出游戏模式：恢复所有被结束、且可执行文件路径已知的进程。
    /// 恢复失败的进程会被保留在上报列表里，绝不静默假装恢复了。
    ///
    /// <see cref="AutoRestoreOnRevert"/> 为 false 时<b>不拉起任何进程</b>，
    /// 只结束「已激活」状态（清 <c>_killed</c>），恢复记录原样留着，
    /// 等用户在恢复选择弹窗里挑完再走 <see cref="ProcessRestoreRunner"/>。
    /// </summary>
    public void Revert()
    {
        if (AutoRestoreOnRevert)
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

            if (_protectedSkipped.Count > 0)
                text += $"（跳过 {_protectedSkipped.Count} 项你已保护的进程）";

            var stuck = _blocked.Count(b => b.Reason == StuckReason);
            var denied = _blocked.Count - stuck;
            if (stuck > 0)
                text += $"（{stuck} 项未完全退出）";
            if (denied > 0)
                text += $"（{denied} 项拒绝访问）";

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
    private readonly HashSet<IGameModeAction> _optional = [];
    private readonly GameModeStateStore? _stateStore;
    private bool _stateSaved;

    public bool IsActive { get; private set; }
    public IReadOnlyList<IGameModeAction> Actions => _actions;
    public IReadOnlyList<IGameModeAction> Applied => _applied;

    /// <summary>
    /// 可选动作失败时的说明。它们不会中断游戏模式，但也不能沉默——
    /// 界面必须展示，否则用户会以为那一项生效了。
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;
    private readonly List<string> _warnings = [];

    /// <summary>本次开启时的力度，供界面展示。</summary>
    public GameModeStrength Strength { get; private set; } = BackgroundProcessWhitelist.DefaultStrength;

    /// <summary>
    /// 创建总控。传入 stateStore 时开启崩溃兜底：
    /// 进入成功落盘快照、干净退出清除快照；传 null 保持旧行为。
    /// </summary>
    public GameModeController(GameModeStateStore? stateStore = null) => _stateStore = stateStore;

    public void Add(IGameModeAction action) => _actions.Add(action);

    /// <summary>
    /// 注册「尽力而为」的动作：它失败时不回滚整个游戏模式，但失败原因照样进入
    /// <see cref="Warnings"/> 上报界面。
    ///
    /// 为什么需要它：<see cref="MemoryCleanAction"/> 这类动作属于「锦上添花」，
    /// 在受限令牌下可能整体被拒（全部进程 AccessDenied）。若按必需动作处理，
    /// 用户会连电源计划都开不起来——一项可选的收益不该挡住整个游戏模式。
    /// v2.1 的网络参数、CPU 亲和性同属这类：多一处平台差异就可能整体失败。
    /// </summary>
    public void AddOptional(IGameModeAction action)
    {
        _actions.Add(action);
        _optional.Add(action);
    }

    /// <summary>这个动作失败是否会中断游戏模式。</summary>
    public bool IsOptional(IGameModeAction action) => _optional.Contains(action);

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
        _warnings.Clear();

        foreach (var a in _actions)
        {
            try
            {
                a.Apply();
                _applied.Add(a);
            }
            catch (Exception ex)
            {
                // 可选动作失败不拦路，但必须留下可见记录
                if (_optional.Contains(a))
                {
                    _warnings.Add($"{a.Name} 未生效：{ex.Message}");
                    continue;
                }

                errors.Add($"{a.Name} 失败：{ex.Message}");
                Exit(); // 失败即回滚，不留下半套状态
                return errors;
            }
        }
        // 兜底快照写不进去，崩溃后就没有任何恢复依据——视为开启失败，回滚全部改动
        try
        {
            SaveStateSnapshot();
        }
        catch (Exception ex)
        {
            errors.Add($"兜底状态写入失败：{ex.Message}");
            Exit();
            return errors;
        }
        IsActive = true;
        return errors;
    }

    /// <summary>进入成功后落盘兜底快照。</summary>
    private void SaveStateSnapshot()
    {
        if (_stateStore is null) return;
        _stateStore.Save(BuildSnapshot());
        _stateSaved = true;
    }

    /// <summary>用当前动作状态组装快照：原电源计划 + 被结束进程的恢复入口。</summary>
    private GameModeSnapshot BuildSnapshot()
    {
        var snapshot = new GameModeSnapshot();
        var power = _applied.OfType<PowerPlanAction>().FirstOrDefault();
        if (power is not null) snapshot.PreviousPowerPlanGuid = power.PreviousGuid;
        var killer = _applied.OfType<IProcessRestoreSource>().FirstOrDefault();
        if (killer is not null)
        {
            snapshot.Killed = killer.Restored
                .Select(r => new GameModeKilledProcess { Name = r.Name, LaunchPath = r.LaunchPath })
                .ToList();
        }
        return snapshot;
    }

    public void Exit() => ExitWithReport();

    /// <summary>
    /// 退出游戏模式并返回回滚失败项。回滚失败必须让用户看见——
    /// 电源计划没还原却提示"已还原"比不还原更糟。
    /// </summary>
    public IReadOnlyList<string> ExitWithReport() => ExitWithReport(deferProcessRestore: false);

    /// <summary>
    /// 退出游戏模式。
    ///
    /// <paramref name="deferProcessRestore"/> = true 时<b>只还原电源计划等改动，不拉起进程</b>：
    /// 这样界面可以先弹出「选择需要恢复的后台进程」，用户勾完再按顺序恢复。
    /// 顺序很关键——如果先恢复再弹窗，选择就变成了「事后追认」，还白启动一遍。
    ///
    /// 延迟模式下兜底快照会改写成「只剩没恢复的进程、电源计划置空」：
    /// 用户取消恢复也<b>不会删除</b>这份记录，下次启动仍能接着还原。
    /// </summary>
    public IReadOnlyList<string> ExitWithReport(bool deferProcessRestore)
    {
        var errors = new List<string>();
        var source = _applied.OfType<IProcessRestoreSource>().FirstOrDefault();
        var previousAutoRestore = source?.AutoRestoreOnRevert ?? true;

        if (deferProcessRestore && source is not null) source.AutoRestoreOnRevert = false;

        try
        {
            for (var i = _applied.Count - 1; i >= 0; i--)
            {
                try { _applied[i].Revert(); }
                catch (Exception ex) { errors.Add($"{_applied[i].Name} 还原失败：{ex.Message}"); }
            }
        }
        finally
        {
            // 无论回滚是否顺利，动作实例的自动恢复开关都要复位：
            // 托盘退出与崩溃兜底仍走「全恢复」，不能被这次关闭带偏
            if (source is not null) source.AutoRestoreOnRevert = previousAutoRestore;

            _applied.Clear();
            IsActive = false;
        }

        if (!deferProcessRestore)
        {
            // 有还原失败就保留快照：下次启动继续提示用户处理
            if (errors.Count == 0 && _stateSaved) ClearStateSnapshot();
            return errors;
        }

        // 延迟恢复：还原本身成功时，把「还剩哪些没恢复」写进快照；
        // 还原失败则原样保留原快照（电源计划仍未还原，不能丢掉那条信息）
        if (errors.Count == 0)
        {
            _stateSaved = false;
            var snapshotError = SyncPendingRestoreSnapshot();
            if (snapshotError is not null) errors.Add(snapshotError);
        }

        return errors;
    }

    /// <summary>
    /// 把「还没恢复的进程」同步进兜底快照；没有剩余就清掉。
    ///
    /// 恢复选择弹窗关闭后（恢复完成 / 部分失败 / 用户取消）都要调一次：
    /// <list type="bullet">
    /// <item>全部恢复成功 → 快照清空，下次启动不再追问；</item>
    /// <item>部分失败或用户取消 → 快照只留没恢复的项，选择权交给下次启动。</item>
    /// </list>
    ///
    /// 任何失败都只作为返回值上报，<b>绝不抛出</b>：此时游戏模式已经关干净了，
    /// 不能因为写不进去一个兜底文件就把已完成的关闭流程判成失败。
    /// </summary>
    public string? SyncPendingRestoreSnapshot()
    {
        if (_stateStore is null) return null;

        var pending = _actions.OfType<IProcessRestoreSource>().FirstOrDefault()?.PendingRestores ?? [];
        var error = _stateStore.SavePendingProcesses(pending);

        // 写成功后才更新标记；写失败时快照内容未知，下一次仍按「有快照」处理。
        // 有剩余才允许「干净退出时顺手清快照」，不能凭调用方手里那份列表猜。
        if (error is null) _stateSaved = _stateStore.Exists();

        return error;
    }

    /// <summary>干净退出后清除兜底快照；清除失败保留快照（下次启动提示，好过无声丢档）。</summary>
    private void ClearStateSnapshot()
    {
        if (_stateStore is null) return;
        try
        {
            _stateStore.Clear();
        }
        catch
        {
            // 清除失败时快照仍在，下次启动会提示用户确认，不静默
        }
    }
}
