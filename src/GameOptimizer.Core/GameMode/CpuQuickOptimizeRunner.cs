namespace GameOptimizer.Core.GameMode;

using GameOptimizer.Core.Library;

/// <summary>一次快速优化的执行结果。全部来自 <see cref="EndProcessAction"/> 的实测回读。</summary>
public sealed record CpuQuickOptimizeResult(
    IReadOnlyList<KilledProcess> Killed,
    IReadOnlyList<BlockedProcess> Blocked,
    IReadOnlyList<BlockedProcess> Skipped,
    string? Error)
{
    /// <summary>是否真的结束了进程。false 时界面必须说明「没有发生任何改动」。</summary>
    public bool DidChangeAnything => Killed.Count > 0;

    /// <summary>如实汇总：结束了几个、跳过几个、被拒几个。</summary>
    public string Summary
    {
        get
        {
            if (Error is not null) return $"执行失败：{Error}";

            if (Killed.Count == 0)
            {
                var tail = Skipped.Count + Blocked.Count == 0
                    ? ""
                    : $"（跳过 {Skipped.Count} 项、拒绝访问 {Blocked.Count} 项）";
                return "没有进程被结束，系统状态未改变" + tail + "。";
            }

            var text = $"已结束 {Killed.Count} 个进程：" +
                       string.Join("、", Killed.Select(k => k.Name));

            if (Skipped.Count > 0) text += $"；跳过 {Skipped.Count} 项系统必要进程";
            if (Blocked.Count > 0) text += $"；{Blocked.Count} 项未能结束（{Blocked[0].Reason}）";
            return text + "。";
        }
    }
}

/// <summary>
/// CPU 快速优化的执行器：**只做一件会改系统的事——结束用户确认过的进程**。
///
/// 为什么直接用 <see cref="EndProcessAction"/> 而不是 <see cref="GameModeController"/>：
///
/// · `EndProcessAction` 内部已经复用 <see cref="EssentialProcessGuard"/>（Session 0 /
///   注册服务 / 系统目录）与 <see cref="BackgroundProcessWhitelist"/>（硬编码兜底名单），
///   保护逻辑不必重写第二套；
/// · 它记录了每个被结束进程的**可执行文件路径**，回滚时按原路径拉起；
/// · 而 `GameModeController.Enter()` 会写「崩溃兜底快照」，那份快照是**游戏模式专用**的，
///   快速优化借道会覆盖它，导致下次启动弹出错误的还原提示。所以这里只用动作，不用总控。
///
/// 另一条底线：**没有确认就没有调用**。传入空名单时本类什么也不做。
/// </summary>
public sealed class CpuQuickOptimizeRunner
{
    private EndProcessAction? _action;

    /// <summary>本次实际提交结束的进程名（已过滤保护名单）。</summary>
    public IReadOnlyList<string> ExecutedNames => _action?.Whitelist ?? [];

    /// <summary>是否存在可回滚的改动（真的结束过进程）。</summary>
    public bool HasChanges => _action is { Killed.Count: > 0 };

    /// <summary>
    /// 执行确认过的结束进程操作。空名单 = 什么都不做。
    /// </summary>
    public CpuQuickOptimizeResult Execute(IReadOnlyList<string>? processNames)
    {
        // 纵深防御：即使界面层漏了过滤，这里也不碰保护名单
        var safe = (processNames ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Where(n => !BackgroundProcessWhitelist.IsProtected(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (safe.Count == 0)
            return new CpuQuickOptimizeResult([], [], [], null);

        var action = new EndProcessAction(safe);
        try
        {
            action.Apply();
        }
        catch (Exception ex)
        {
            // 失败必须可见：抛出与返回都要带原因，不能静默当作「没结束」
            return new CpuQuickOptimizeResult(action.Killed, action.Blocked, action.Skipped, ex.Message);
        }

        _action = action;
        return new CpuQuickOptimizeResult(action.Killed, action.Blocked, action.Skipped, null);
    }

    /// <summary>
    /// 回滚：按原路径重新拉起被结束的进程。返回失败项（空列表表示全部成功或本来就没改动）。
    /// </summary>
    public IReadOnlyList<string> Revert()
    {
        if (_action is null) return [];

        try
        {
            _action.Revert();
        }
        catch (Exception ex)
        {
            return [$"恢复进程时出现异常：{ex.Message}"];
        }

        var failures = _action.FailedRestores
            .Select(r => $"{r.Name} 恢复失败：{r.Error}")
            .ToList();

        _action = null;
        return failures;
    }
}
