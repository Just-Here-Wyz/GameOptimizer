namespace GameOptimizer.Core.GameMode;

using System.Diagnostics;

/// <summary>
/// 崩溃 / 强杀后的兜底还原：把电源计划切回原计划，并按原路径拉起被结束的进程。
///
/// 还原是独立的、可注入的（Process.Start 与 powercfg 都从外面传进来），
/// 这样 App 层能提供真实的拉起方式，测试能注入假实现验证判定逻辑。
///
/// <b>v2.4.1 起不再无条件启动快照里的每一条</b>：快照是先写到磁盘上的数据，
/// 可能来自半天前、也可能被手工改过。落盘记录 ≠ 现在还能安全启动，
/// 所以先过一遍 <see cref="ProcessRestorePlanner"/> 的资格判定，
/// 不合格的进 <see cref="Skipped"/> 说明原因，绝不硬猜一个命令去拉起。
/// </summary>
public sealed class GameModeRecovery
{
    private readonly Func<ProcessStartInfo, bool> _startProcess;
    private readonly ProcessRestoreEnvironment _environment;
    private readonly List<string> _skipped = [];

    /// <param name="startProcess">
    /// 拉起进程的函数。返回是否成功启动（不抛异常、Process.Start 返回非 null）。
    /// </param>
    /// <param name="environment">
    /// 资格判定要用到的事实（文件是否存在 / 进程是否在跑）。null 表示读本机真实状态；
    /// 测试注入假实现后，判定逻辑就能脱离真实文件系统验证。
    /// </param>
    public GameModeRecovery(
        Func<ProcessStartInfo, bool>? startProcess = null,
        ProcessRestoreEnvironment? environment = null)
    {
        _startProcess = startProcess ?? DefaultStartProcess;
        _environment = environment ?? ProcessRestoreEnvironment.Default;
    }

    /// <summary>
    /// 本次还原中被跳过的目标及原因。
    /// 它们<b>不算「没还原完」</b>：文件已删除、应用商店应用这类目标本来就不可能自动恢复，
    /// 把它们当错误会让快照永远留着、每次启动都追问一遍。
    /// </summary>
    public IReadOnlyList<string> Skipped => _skipped;

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
        _skipped.Clear();
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

        var candidates = ProcessRestorePlanner.Build(
            (snapshot.Killed ?? [])
                .Where(k => k is not null)
                .Select(k => new RestoredProcess(k.Name, k.LaunchPath)),
            _environment);

        foreach (var candidate in candidates)
        {
            if (!candidate.Eligible)
            {
                _skipped.Add($"{candidate.ProcessName}：{candidate.Reason}");
                continue;
            }

            try
            {
                var ok = _startProcess(new ProcessStartInfo(candidate.LaunchPath!));
                if (!ok) errors.Add($"进程 {candidate.ProcessName} 未能拉起（{candidate.LaunchPath}）。");
            }
            catch (Exception ex)
            {
                errors.Add($"进程 {candidate.ProcessName} 拉起失败：{ex.Message}");
            }
        }

        return errors;
    }
}
