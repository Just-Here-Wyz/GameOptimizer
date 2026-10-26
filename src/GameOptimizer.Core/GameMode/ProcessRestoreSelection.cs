namespace GameOptimizer.Core.GameMode;

/// <summary>
/// 恢复选择状态的<b>唯一真源</b>。
///
/// 为什么不是「每个界面项自己存一个 bool」：那样「全选」要遍历界面项、
/// 「恢复所选」又要回头收集一遍，两边一错位就会变成「看着都勾了、实际只恢复了一半」。
/// 这里按下标存勾选状态，界面每一项只是它的一个视图，判断口径永远只有一份。
///
/// 关键约束：<b>不可恢复的项不能被勾上</b>。全选只覆盖可恢复项，用户手动点也不生效——
/// 这条在 <see cref="SetSelected"/> 里无条件执行，不依赖界面是否把复选框禁用了。
/// </summary>
public sealed class ProcessRestoreSelection
{
    private readonly List<ProcessRestoreCandidate> _candidates;
    private readonly bool[] _selected;

    public ProcessRestoreSelection(IReadOnlyList<ProcessRestoreCandidate>? candidates)
    {
        _candidates = candidates is null ? [] : [.. candidates];
        _selected = new bool[_candidates.Count];
    }

    public IReadOnlyList<ProcessRestoreCandidate> Candidates => _candidates;

    public int Count => _candidates.Count;

    public ProcessRestoreCandidate this[int index] => _candidates[index];

    /// <summary>可以恢复的项数（不含被跳过的）。</summary>
    public int EligibleCount => _candidates.Count(c => c.Eligible);

    /// <summary>被跳过的项数。</summary>
    public int SkippedCount => _candidates.Count(c => !c.Eligible);

    /// <summary>已勾选且确实可恢复的项数。</summary>
    public int SelectedCount => _selected.Count(s => s);

    public bool IsSelected(int index)
        => index >= 0 && index < _candidates.Count && _selected[index];

    /// <summary>
    /// 勾选 / 取消勾选。不可恢复项一律强制为未勾选，返回它最终的状态。
    /// </summary>
    public bool SetSelected(int index, bool selected)
    {
        if (index < 0 || index >= _candidates.Count) return false;

        var allowed = selected && _candidates[index].Eligible;
        _selected[index] = allowed;
        return allowed;
    }

    /// <summary>是否所有可恢复项都已勾选（可恢复项为 0 时算「未全选」，按钮才会显示「全选」）。</summary>
    public bool AllEligibleSelected
    {
        get
        {
            var eligible = 0;
            for (var i = 0; i < _candidates.Count; i++)
            {
                if (!_candidates[i].Eligible) continue;
                eligible++;
                if (!_selected[i]) return false;
            }
            return eligible > 0;
        }
    }

    /// <summary>是否至少勾了一项。</summary>
    public bool AnySelected => _selected.Any(s => s);

    /// <summary>真正要交给恢复队列的目标，永远只有「可恢复 + 已勾选」。</summary>
    public IReadOnlyList<ProcessRestoreCandidate> Selected
        => [.. _candidates.Where((c, i) => c.Eligible && _selected[i])];

    /// <summary>
    /// 全选 / 取消全选。返回操作后是否处于「全选」状态，供界面决定按钮文案。
    /// 只动可恢复项；一个可恢复项都没有时是无操作。
    /// </summary>
    public bool ToggleAll()
    {
        var selectAll = !AllEligibleSelected;

        for (var i = 0; i < _candidates.Count; i++)
        {
            _selected[i] = selectAll && _candidates[i].Eligible;
        }

        return AllEligibleSelected;
    }

    public void ClearSelection() => Array.Clear(_selected);

    /// <summary>按钮文案：全部勾上时提示「取消全选」，否则提示「全选」。</summary>
    public string ToggleLabel => AllEligibleSelected ? "取消全选" : "全选";
}
