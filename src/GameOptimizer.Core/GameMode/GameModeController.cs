namespace GameOptimizer.Core.GameMode;

using System.Diagnostics;
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

/// <summary>设置显卡性能偏好为"高性能"，需要写入注册表，可回滚。</summary>
public sealed class GpuPreferenceAction : IGameModeAction
{
    private const string SubKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private object? _previousValue;

    public string? TargetApp { get; set; }

    public string Name => "显卡性能偏好";
    public string Description => "把游戏的显卡偏好设为高性能，避免被调度到节能模式。";

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

/// <summary>结束白名单内的后台进程。必须在回滚时如实告知"进程无法自动恢复"。</summary>
public sealed class EndProcessAction : IGameModeAction
{
    private readonly List<string> _killed = [];

    public IReadOnlyList<string> Whitelist { get; }
    public string Name => "关闭后台干扰进程";
    public string Description => "结束白名单内的浏览器、下载器等；不会自动重启，需你手动打开。";

    public EndProcessAction(IReadOnlyList<string> whitelist) => Whitelist = whitelist;

    public void Apply()
    {
        _killed.Clear();
        foreach (var name in Whitelist)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); _killed.Add(name); }
                catch { }
                finally { p.Dispose(); }
            }
        }
    }

    /// <summary>被结束的进程无法自动恢复，这里只做记录清理。</summary>
    public void Revert() => _killed.Clear();

    public bool IsActive() => _killed.Count > 0;
}

/// <summary>游戏模式总控：按顺序执行，失败即回滚已执行的，保证不留下半套改动。</summary>
public sealed class GameModeController
{
    private readonly List<IGameModeAction> _actions = [];
    private readonly List<IGameModeAction> _applied = [];

    public bool IsActive { get; private set; }
    public IReadOnlyList<IGameModeAction> Actions => _actions;
    public IReadOnlyList<IGameModeAction> Applied => _applied;

    public void Add(IGameModeAction action) => _actions.Add(action);

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
