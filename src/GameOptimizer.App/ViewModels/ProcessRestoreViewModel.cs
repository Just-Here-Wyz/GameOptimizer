namespace GameOptimizer.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 「游戏模式已关闭」弹窗里的一行：应用名 / exe 名 / 发布者 / 恢复资格与原因 + 一个勾选框。
///
/// 勾选状态<b>不存这里</b>，一律转发给 Core 的 <see cref="ProcessRestoreSelection"/>：
/// 「全选」「恢复所选」读的是同一份状态，界面项只是它的视图，
/// 不可能出现「看着都勾了、实际只恢复一半」。
/// </summary>
public sealed class ProcessRestoreItem : INotifyPropertyChanged
{
    private readonly ProcessRestoreSelection _selection;
    private readonly int _index;

    public ProcessRestoreItem(ProcessRestoreSelection selection, int index)
    {
        _selection = selection;
        _index = index;
    }

    public ProcessRestoreCandidate Candidate => _selection[_index];

    public string AppName => Candidate.AppName;

    /// <summary>可执行文件名（如 chrome.exe），方便对照任务管理器。</summary>
    public string ExecutableName => Candidate.ExecutableName;

    /// <summary>发布者来自 exe 版本资源里的公司名；读不到就写「未知」，不猜。</summary>
    public string PublisherText => Candidate.Publisher is null
        ? "发布者：未知"
        : $"发布者：{Candidate.Publisher}";

    /// <summary>exe 文件描述作为简短说明；与标题重复时不显示，避免同一句话写两遍。</summary>
    public string DescriptionText => Candidate.Description ?? "";

    public bool HasDescription =>
        !string.IsNullOrWhiteSpace(Candidate.Description)
        && !string.Equals(Candidate.Description, Candidate.AppName, StringComparison.OrdinalIgnoreCase);

    /// <summary>恢复资格的原因：可以恢复就写依据，跳过就写为什么跳过。</summary>
    public string ReasonText => Candidate.Reason;

    public string BadgeText => Candidate.BadgeText;

    /// <summary>可恢复用绿色，跳过用灰色——跳过不是错误，只是这次不处理。</summary>
    public string BadgeHex => Candidate.Eligible ? "#FF34C759" : "#FF83868D";

    public string ReasonHex => Candidate.Eligible ? "#FF9AA0A6" : "#FF83868D";

    /// <summary>不可恢复项禁止勾选（Core 侧也会再拦一次，不依赖界面禁用）。</summary>
    public bool CanSelect => Candidate.Eligible;

    public ImageSource? Icon => AppIconCache.ForPath(Candidate.LaunchPath);

    public string IconFallbackText => (AppName.Length > 0 ? AppName[0] : '?').ToString();

    /// <summary>勾选状态：读写都落到 Core 的选择状态上。</summary>
    public bool IsSelected
    {
        get => _selection.IsSelected(_index);
        set
        {
            var before = _selection.IsSelected(_index);

            // 不可恢复项即使被外部设成 true，Core 也会拒绝——这里按「最终状态」判断是否有变化
            var after = _selection.SetSelected(_index, value);
            if (before == after) return;

            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>批量改状态（全选 / 取消全选）后只刷新本行，不逐行触发聚合计算。</summary>
    internal void RaiseSelectionOnly()
        => PropertyChanged?.Invoke(this, new(nameof(IsSelected)));

    /// <summary>勾选变化：弹窗 ViewModel 据此更新按钮文案与可用性。</summary>
    public event EventHandler? SelectionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 恢复选择弹窗的 ViewModel。
///
/// 职责分工：
/// <list type="bullet">
/// <item>「勾了什么」→ Core <see cref="ProcessRestoreSelection"/>；</item>
/// <item>「怎么恢复」→ Core <see cref="ProcessRestoreRunner"/>（顺序、间隔、防重复、启动后校验）；</item>
/// <item>这里只做界面状态：按钮文案、进度、结果摘要与明细。</item>
/// </list>
///
/// 关闭窗口 ≠ 恢复：只有点了「恢复所选」才会有进程被启动。
/// 因此「取消」不需要任何回滚逻辑，它天然什么也没做。
/// </summary>
public sealed class ProcessRestoreViewModel : INotifyPropertyChanged
{
    private readonly ProcessRestoreRunner _runner;
    private readonly ProcessRestoreSelection _selection;

    public ProcessRestoreViewModel(IReadOnlyList<ProcessRestoreCandidate> candidates, ProcessRestoreRunner runner)
    {
        _runner = runner;
        _selection = new ProcessRestoreSelection(candidates);

        Items = [.. Enumerable.Range(0, _selection.Count)
            .Select(i => new ProcessRestoreItem(_selection, i))];

        foreach (var item in Items) item.SelectionChanged += (_, _) => NotifySelectionChanged();

        ToggleSelectAllCommand = new RelayCommand(_ => ToggleSelectAll(), _ => !IsRunning);
        RestoreSelectedCommand = new RelayCommand(_ => _ = RestoreAsync(), _ => CanRestoreSelected);
        CloseCommand = new RelayCommand(_ => RequestClose(), _ => !IsRunning);

        // 默认全勾「可恢复项」：关闭游戏模式前这些程序本来是开着的，
        // 恢复到原样是最符合预期的默认值；不想恢复哪一项，取消勾选即可。
        _selection.ToggleAll();
        foreach (var item in Items) item.RaiseSelectionOnly();
        NotifySelectionChanged();
    }

    public ObservableCollection<ProcessRestoreItem> Items { get; }

    public ProcessRestoreSelection Selection => _selection;

    /// <summary>顶部一行：可恢复几项、跳过几项。</summary>
    public string HeaderSummary =>
        $"可恢复 {_selection.EligibleCount} 项 · 跳过 {_selection.SkippedCount} 项";

    /// <summary>底部一行：当前勾选了几项、预计耗时（按项间隔估算，逐项启动本来就要等）。</summary>
    public string SelectedSummary
    {
        get
        {
            var count = _selection.SelectedCount;
            if (count == 0) return "未勾选任何进程";
            var seconds = Math.Max(1, (int)Math.Ceiling(_runner.Interval.TotalSeconds * count));
            return $"已勾选 {count} 项 · 逐项启动，约 {seconds} 秒";
        }
    }

    /// <summary>全选 / 取消全选按钮文案，随勾选状态变化。</summary>
    public string ToggleSelectAllText => _selection.ToggleLabel;

    public RelayCommand ToggleSelectAllCommand { get; }
    public RelayCommand RestoreSelectedCommand { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>恢复按钮文案：只有勾了东西才可用，数量直接写在按钮上。</summary>
    public string RestoreButtonText => _selection.SelectedCount > 0
        ? $"恢复所选（{_selection.SelectedCount}）"
        : "恢复所选";

    /// <summary>左下角按钮文案：还没恢复过时是「取消」，恢复完就是「关闭」。</summary>
    public string CloseButtonText => HasResult ? "关闭" : "取消";

    /// <summary>恢复完成后收起「恢复所选」按钮：这一批已经跑过，再点也不该执行第二遍。</summary>
    public bool ShowRestoreButton => !HasResult;

    private bool _isRunning;

    /// <summary>是否有恢复正在执行。执行中禁止重复触发，也不允许关窗。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanInteract));
            ToggleSelectAllCommand.RaiseCanExecuteChanged();
            RestoreSelectedCommand.RaiseCanExecuteChanged();
            CloseCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>勾选项与按钮是否可操作。</summary>
    public bool CanInteract => !IsRunning;

    public bool CanRestoreSelected => !IsRunning && !HasResult && _selection.SelectedCount > 0;

    private string _progressText = "";

    /// <summary>「正在恢复 3/5：chrome.exe」。空串表示当前没有进行中的项。</summary>
    public string ProgressText
    {
        get => _progressText;
        private set { _progressText = value; OnPropertyChanged(); }
    }

    private string _statusText = "";

    /// <summary>状态行：等待选择 / 正在恢复 / 已完成 / 失败。颜色由 <see cref="StatusHex"/> 决定。</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusHex));
        }
    }

    public string StatusHex => HasResult && Report?.FailedCount > 0 ? "#FFFF9F0A" : "#FF83868D";

    private bool _hasResult;

    /// <summary>这批恢复是否已经跑完（跑完就不允许再点「恢复所选」，避免同一批恢复两遍）。</summary>
    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            _hasResult = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusHex));
            OnPropertyChanged(nameof(CloseButtonText));
            OnPropertyChanged(nameof(ShowRestoreButton));
            RestoreSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    private string _resultSummary = "";

    /// <summary>结果摘要，与首页状态行共用 Core 的同一句文案。</summary>
    public string ResultSummary
    {
        get => _resultSummary;
        private set { _resultSummary = value; OnPropertyChanged(); }
    }

    private string _detailedLog = "";

    /// <summary>逐项明细。只写进程名与错误要点，不贴堆栈、不贴完整路径。</summary>
    public string DetailedLog
    {
        get => _detailedLog;
        private set
        {
            _detailedLog = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDetailedLog));
        }
    }

    public bool HasDetailedLog => _detailedLog.Length > 0;

    /// <summary>完整日志文件的落点，明细之外还能翻历史。截图模式下用户目录会被脱敏。</summary>
    public string LogPathText => PathRedactor.Redact(ProcessRestoreLog.FilePath);

    /// <summary>用户是否真的点过「恢复所选」（用于区分「取消」与「恢复完成」）。</summary>
    public bool RestoreAttempted { get; private set; }

    public ProcessRestoreReport? Report { get; private set; }

    /// <summary>窗口该关了（取消 / 关闭按钮）。由 View 层订阅并 Close。</summary>
    public event EventHandler? CloseRequested;

    private void ToggleSelectAll()
    {
        if (IsRunning) return;

        _selection.ToggleAll();
        foreach (var item in Items) item.RaiseSelectionOnly();
        NotifySelectionChanged();
    }

    /// <summary>
    /// 执行「恢复所选」。只把用户勾选的合法目标交给恢复队列；
    /// 一项都没勾时不开工，也不谎报成功。
    /// </summary>
    public async Task RestoreAsync()
    {
        if (IsRunning || HasResult) return;

        var targets = _selection.Selected;
        if (targets.Count == 0)
        {
            StatusText = "没有勾选任何可恢复的进程";
            return;
        }

        RestoreAttempted = true;
        IsRunning = true;
        StatusText = $"正在按顺序恢复 {targets.Count} 项…";
        ProgressText = "准备中…";

        var progress = new Progress<ProcessRestoreProgress>(p =>
            ProgressText = $"正在恢复 {p.Index}/{p.Total}：{p.ProcessName}");

        ProcessRestoreReport? report = null;
        try
        {
            report = await _runner.RunAsync(targets, progress);
        }
        catch (Exception ex)
        {
            // 恢复队列自身出意外（不是单项失败）也要说清楚，不能静默
            StatusText = $"恢复中断：{ex.GetType().Name} — {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            ProgressText = "";
        }

        if (report is null)
        {
            // 恢复队列拒绝了这次请求（同一时刻只允许一批）
            StatusText = "已有一批恢复正在进行，本次未重复执行";
            return;
        }

        Report = report;
        HasResult = true;
        // 摘要按「列表总行数」算：没勾的与不可恢复的也算「跳过」，和用户看到的行数对得上
        ResultSummary = report.SummaryAgainst(_selection.Count);
        StatusText = report.FailedCount > 0
            ? $"恢复完成，{report.FailedCount} 项未成功（明细见下方）"
            : "恢复完成";
        DetailedLog = BuildDetailedLog(report, LogPathText);
        NotifySelectionChanged();
    }

    private static string BuildDetailedLog(ProcessRestoreReport report, string logPath)
    {
        var lines = new List<string>(report.DetailLines);
        if (report.FailedCount > 0) lines.Add($"完整日志：{logPath}");
        return string.Join(Environment.NewLine, lines);
    }

    private void RequestClose()
    {
        if (IsRunning) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(ToggleSelectAllText));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(RestoreButtonText));
        OnPropertyChanged(nameof(CanRestoreSelected));
        RestoreSelectedCommand.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>
/// 「打开恢复选择弹窗」的请求。
///
/// ViewModel 不直接 new Window（离屏渲染等无界面场景不该弹窗），
/// 由 MainWindow 订阅后建窗、ShowDialog，并在窗口关闭时调用 <see cref="Complete"/>；
/// ViewModel 侧用 <see cref="Completion"/> 等它结束，再决定首页摘要文案。
/// </summary>
public sealed class ProcessRestoreRequest : EventArgs
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProcessRestoreRequest(ProcessRestoreViewModel viewModel) => ViewModel = viewModel;

    public ProcessRestoreViewModel ViewModel { get; }

    /// <summary>窗口关闭后完成；结果为「是否真的执行过恢复」。</summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>由 View 层在窗口关闭后调用（无论用户是恢复、取消还是直接关窗）。</summary>
    public void Complete(bool restoredAnything) => _completion.TrySetResult(restoredAnything);
}
