namespace GameOptimizer.Core.GameMode;

using System.Diagnostics;

/// <summary>
/// 崩溃 / 强杀后的兜底还原：把电源计划切回原计划，并按原路径拉起被结束的进程。
///
/// 还原是独立的、可注入的（Process.Start 与 powercfg 都从外面传进来），
/// 这样 App 层能提供真实的拉起方式，测试能注入假实现验证判定逻辑。
/// </summary>
public sealed class GameModeRecovery
{
    private readonly Func<ProcessStartInfo, bool> _startProcess;

    /// <param name="startProcess">
    /// 拉起进程的函数。返回是否成功启动（不抛异常、Process.Start 返回非 null）。
    /// </param>
    public GameModeRecovery(Func<ProcessStartInfo, bool>? startProcess = null)
        => _startProcess = startProcess ?? DefaultStartProcess;

    /// <summary>默认实现：UseShellExecute 拉起。</summary>
    private static bool DefaultStartProcess(ProcessStartInfo psi)
        => Process.Start(new ProcessStartInfo(psi.FileName) { UseShellExecute = true }) is not null;

    /// <summary>
    /// 执行还原并返回失败项。返回值非空即意味着有东西没还原，调用方必须展示。
    /// </summary>
    /// <param name="snapshot">启动时读到的残留快照。为 null 时不做事，返回空。</param>
    /// <param name="restorePowerPlan">切回原电源计划的函数；null 表示不做电源计划还原。</param>
    /// <param name="powerPlanDisplay">电源计划的可读描述，用于失败信息。</param>
    public IReadOnlyList<string> Restore(
        GameModeSnapshot? snapshot,
        Func<string, string?>? restorePowerPlan,
        string powerPlanDisplay = "原电源计划")
    {
        var errors = new List<string>();
        if (snapshot is null) return errors;

        var guid = snapshot.PreviousPowerPlanGuid;
        if (!string.IsNullOrWhiteSpace(guid))
        {
            if (restorePowerPlan is null)
            {
                errors.Add($"{powerPlanDisplay}未还原：没有可用的还原通道（{guid}）。");
            }
            else
            {
                try
                {
                    var error = restorePowerPlan(guid);
                    if (!string.IsNullOrWhiteSpace(error))
                        errors.Add($"{powerPlanDisplay}还原失败：{error}");
                }
                catch (Exception ex)
                {
                    errors.Add($"{powerPlanDisplay}还原失败：{ex.Message}");
                }
            }
        }

        foreach (var killed in snapshot.Killed ?? [])
        {
            if (string.IsNullOrWhiteSpace(killed.LaunchPath)) continue;
            try
            {
                var ok = _startProcess(new ProcessStartInfo(killed.LaunchPath));
                if (!ok) errors.Add($"进程 {killed.Name} 未能拉起（{killed.LaunchPath}）。");
            }
            catch (Exception ex)
            {
                errors.Add($"进程 {killed.Name} 拉起失败：{ex.Message}");
            }
        }

        return errors;
    }
}
