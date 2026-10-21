namespace GameOptimizer.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using GameOptimizer.Core.Download;

/// <summary>
/// 下载页的 ViewModel。**不含任何下载逻辑**——分片、重试、校验、持久化全在
/// <see cref="DownloadService"/> 里，这里只做三件事：
/// <list type="number">
/// <item>把服务的任务列表投影成界面卡片，按状态分组；</item>
/// <item>把按钮命令翻译成服务调用；</item>
/// <item>每秒刷新一次速度与统计（服务已经按秒采样，这里只是把数字搬到界面）。</item>
/// </list>
///
/// 线程：服务的事件来自后台线程，所有触达 ObservableCollection 的写入都切回 UI 线程。
/// </summary>
public sealed class DownloadViewModel : INotifyPropertyChanged
{
    private readonly DownloadService _service;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;

    private bool _suspendRefresh;

    public DownloadViewModel(DownloadService service, Dispatcher dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;

        Url = "";
        SaveDirectory = service.Settings.DefaultDirectory;
        FileName = "";
        SpeedLimitText = FormatLimit(service.Settings.SpeedLimitBytesPerSecond);
        SegmentText = service.Settings.ParallelSegments.ToString();

        AddTaskCommand = new RelayCommand(_ => AddTask(), _ => !string.IsNullOrWhiteSpace(Url));
        BrowseDirectoryCommand = new RelayCommand(_ => BrowseDirectory());
        PauseAllCommand = new RelayCommand(_ => _service.PauseAll());
        ResumeAllCommand = new RelayCommand(_ => _service.ResumeAll());
        ClearCompletedCommand = new RelayCommand(_ => ClearCompleted(), _ => Completed.Count > 0);
        ApplySpeedLimitCommand = new RelayCommand(_ => ApplySpeedLimit());
        ApplySegmentsCommand = new RelayCommand(_ => ApplySegments());

        _service.TaskChanged += OnTaskChanged;
        _service.TasksChanged += OnTasksChanged;
        _service.Notice += OnNotice;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new(name));

    // ---------- 新建任务栏 ----------

    private string _url = "";
    public string Url
    {
        get => _url;
        set
        {
            if (_url == value) return;
            _url = value;
            OnPropertyChanged();
            AddTaskCommand.RaiseCanExecuteChanged();

            // 粘贴链接自动识别文件名：用户手动改过就不再覆盖
            if (_userEditedFileName) return;

            var guess = DownloadFileName.FromUrl(value);
            if (guess.Length == 0) return;

            _updatingFileName = true;
            FileName = guess;
            _updatingFileName = false;
        }
    }

    private string _saveDirectory = "";
    public string SaveDirectory
    {
        get => _saveDirectory;
        set
        {
            if (_saveDirectory == value) return;
            _saveDirectory = value;
            OnPropertyChanged();
        }
    }

    private string _fileName = "";
    private bool _updatingFileName;
    private bool _userEditedFileName;

    /// <summary>文件名。用户一旦手动改过，就不再被链接自动识别覆盖。</summary>
    public string FileName
    {
        get => _fileName;
        set
        {
            if (_fileName == value) return;
            _fileName = value;
            OnPropertyChanged();

            if (!_updatingFileName) _userEditedFileName = true;
        }
    }

    private string _newTaskStatus = "";
    public string NewTaskStatus
    {
        get => _newTaskStatus;
        private set
        {
            _newTaskStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNewTaskStatus));
        }
    }

    public bool HasNewTaskStatus => !string.IsNullOrWhiteSpace(_newTaskStatus);

    /// <summary>新建任务状态的颜色：成功绿、失败红、普通灰。</summary>
    private string _newTaskStatusHex = "#FF83868D";
    public string NewTaskStatusHex
    {
        get => _newTaskStatusHex;
        private set { _newTaskStatusHex = value; OnPropertyChanged(); }
    }

    // ---------- 设置 ----------

    private string _speedLimitText = "";
    public string SpeedLimitText
    {
        get => _speedLimitText;
        set { _speedLimitText = value; OnPropertyChanged(); }
    }

    private string _segmentText = "";
    public string SegmentText
    {
        get => _segmentText;
        set { _segmentText = value; OnPropertyChanged(); }
    }

    public void ApplySpeedLimit()
    {
        var bytes = ParseSpeedLimit(SpeedLimitText);

        _service.Settings.SpeedLimitBytesPerSecond = bytes;
        _service.Settings = _service.Settings;   // 触发限速器重建

        SpeedLimitText = FormatLimit(bytes);
        NewTaskStatusHex = "#FF34C759";
        NewTaskStatus = bytes == 0 ? "已取消限速" : $"已限速 {FormatLimit(bytes)}";
    }

    public void ApplySegments()
    {
        var count = int.TryParse(SegmentText?.Trim(), out var parsed)
            ? Math.Clamp(parsed, DownloadSettings.MinParallelSegments, DownloadSettings.MaxParallelSegments)
            : 4;

        _service.Settings.ParallelSegments = count;
        _service.Settings = _service.Settings;

        SegmentText = count.ToString();
        NewTaskStatusHex = "#FF34C759";
        NewTaskStatus = $"并发分片数已设为 {count}";
    }

    /// <summary>
    /// 「10MB」「10 MB/s」「1048576」三种写法都认。
    /// 认不出就返回 0（不限速）—— 不猜一个数字出来。
    /// </summary>
    public static long ParseSpeedLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var raw = text.Trim().ToUpperInvariant();
        if (raw is "0" or "不限速" or "无限") return 0;

        double multiplier;
        if (raw.EndsWith("GB/S") || raw.EndsWith("GB")) { multiplier = 1024d * 1024d * 1024d; }
        else if (raw.EndsWith("MB/S") || raw.EndsWith("MB")) { multiplier = 1024d * 1024d; }
        else if (raw.EndsWith("KB/S") || raw.EndsWith("KB")) { multiplier = 1024d; }
        else { multiplier = 1d; }

        var number = new string(raw.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(number, out var value) || value <= 0) return 0;

        return (long)(value * multiplier);
    }

    public static string FormatLimit(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "不限速";

        var mb = bytesPerSecond / (1024d * 1024d);
        return mb >= 1 ? $"{mb:F0} MB/s" : $"{bytesPerSecond / 1024d:F0} KB/s";
    }

    // ---------- 任务分组 ----------

    /// <summary>排队中 + 下载中，按创建时间正序（先排的先跑）。</summary>
    public ObservableCollection<DownloadItem> Active { get; } = [];

    public ObservableCollection<DownloadItem> Completed { get; } = [];

    /// <summary>失败 / 暂停 / 取消，都需要用户处理。</summary>
    public ObservableCollection<DownloadItem> Failed { get; } = [];

    public bool HasActive => Active.Count > 0;
    public bool HasCompleted => Completed.Count > 0;
    public bool HasFailed => Failed.Count > 0;
    public bool IsEmpty => Active.Count == 0 && Completed.Count == 0 && Failed.Count == 0;

    public string ActiveHeader => $"正在下载（{Active.Count}）";
    public string CompletedHeader => $"已完成（{Completed.Count}）";
    public string FailedHeader => $"下载失败（{Failed.Count}）";

    // ---------- 全局统计 ----------

    private string _totalSpeedText = "";
    public string TotalSpeedText
    {
        get => _totalSpeedText;
        private set { _totalSpeedText = value; OnPropertyChanged(); }
    }

    private string _runningText = "0 个任务运行中";
    public string RunningText
    {
        get => _runningText;
        private set { _runningText = value; OnPropertyChanged(); }
    }

    private string _totalText = "共 0 个任务";
    public string TotalText
    {
        get => _totalText;
        private set { _totalText = value; OnPropertyChanged(); }
    }

    // ---------- 命令 ----------

    public RelayCommand AddTaskCommand { get; }
    public RelayCommand BrowseDirectoryCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ResumeAllCommand { get; }
    public RelayCommand ClearCompletedCommand { get; }
    public RelayCommand ApplySpeedLimitCommand { get; }
    public RelayCommand ApplySegmentsCommand { get; }

    /// <summary>
    /// 文件夹选择器由宿主注入：Core 不认识 WPF，ViewModel 也不该直接 new 对话框，
    /// 这样下载页在没有窗口的环境（测试 / 预览）里也能构造。
    /// </summary>
    public Func<string?, string?>? DirectoryPicker { get; set; }

    private void BrowseDirectory()
    {
        var picked = DirectoryPicker?.Invoke(SaveDirectory);
        if (!string.IsNullOrWhiteSpace(picked)) SaveDirectory = picked;
    }

    private void AddTask()
    {
        if (!DownloadUrl.IsValid(Url))
        {
            NewTaskStatusHex = "#FFFF453A";
            NewTaskStatus = "地址不合法：请粘贴 http / https 直链";
            return;
        }

        var directory = string.IsNullOrWhiteSpace(SaveDirectory)
            ? DownloadSettings.DefaultDirectoryPath
            : SaveDirectory.Trim();

        var created = _service.CreateTask(Url, directory, FileName, out var error);

        if (created is null)
        {
            NewTaskStatusHex = "#FFFF453A";
            NewTaskStatus = error ?? "任务创建失败";
            return;
        }

        NewTaskStatusHex = "#FF34C759";
        NewTaskStatus = $"已加入队列：{created.FileName}";

        // 清空输入，但保留保存路径（连续下多个文件时最省事）
        Url = "";
        _userEditedFileName = false;
        FileName = "";
        Refresh();
    }

    private void ClearCompleted()
    {
        var count = _service.ClearCompleted();
        NewTaskStatusHex = "#FF83868D";
        NewTaskStatus = count == 0 ? "没有已完成的任务" : $"已清空 {count} 个已完成任务的记录";
        Refresh();
    }

    /// <summary>打开所在文件夹：由宿主注入（要启动资源管理器），Core 不碰。</summary>
    public Action<DownloadItem>? OpenFolderAction { get; set; }

    /// <summary>启动时载入本地任务（含崩溃后的自动续传）。由宿主在构造后调用一次。</summary>
    public async Task LoadAsync()
    {
        var error = await _service.LoadAsync();

        if (!string.IsNullOrWhiteSpace(error))
        {
            NewTaskStatusHex = "#FFFF453A";
            NewTaskStatus = $"读取本地任务失败：{error}";
        }

        Refresh();
    }

    /// <summary>任务列表刷新。500ms 一次，太快会让界面一直重排。</summary>
    public void Refresh()
    {
        if (_suspendRefresh) return;

        var tasks = _service.Tasks;
        var stats = _service.Statistics;

        var active = new List<DownloadItem>();
        var done = new List<DownloadItem>();
        var failed = new List<DownloadItem>();

        foreach (var task in tasks)
        {
            var item = new DownloadItem(task, this);
            switch (task.Status)
            {
                case DownloadStatus.Completed:
                    done.Add(item);
                    break;
                case DownloadStatus.Failed:
                case DownloadStatus.Paused:
                case DownloadStatus.Canceled:
                    failed.Add(item);
                    break;
                default:
                    active.Add(item);
                    break;
            }
        }

        Replace(Active, active);
        Replace(Completed, done.OrderByDescending(i => i.FinishedAt));
        Replace(Failed, failed);

        TotalSpeedText = DownloadText.FormatSpeed(stats.TotalBytesPerSecond);
        RunningText = $"{stats.RunningTasks} 个任务运行中";
        TotalText = $"共 {stats.TotalTasks} 个任务";

        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ActiveHeader));
        OnPropertyChanged(nameof(CompletedHeader));
        OnPropertyChanged(nameof(FailedHeader));

        ClearCompletedCommand.RaiseCanExecuteChanged();
    }

    private static void Replace(ObservableCollection<DownloadItem> target, IEnumerable<DownloadItem> items)
    {
        var list = items.ToList();

        // 逐个就地更新而不是 Clear + Add：后者会让整列表每次刷新都重建，
        // 卡片上的 ToolTip / 选中状态全部丢失，视觉上也会闪。
        for (var i = 0; i < list.Count; i++)
        {
            if (i < target.Count && target[i].Id == list[i].Id)
            {
                target[i].CopyFrom(list[i]);
            }
            else if (i < target.Count)
            {
                target[i] = list[i];
            }
            else
            {
                target.Add(list[i]);
            }
        }

        while (target.Count > list.Count) target.RemoveAt(target.Count - 1);
    }

    private void OnTaskChanged(object? sender, DownloadTask task) => PostRefresh();
    private void OnTasksChanged(object? sender, EventArgs e) => PostRefresh();

    private void OnNotice(object? sender, string message) => _dispatcher.BeginInvoke(() =>
    {
        NewTaskStatusHex = "#FFFF9F0A";
        NewTaskStatus = message;
    });

    private void PostRefresh()
    {
        if (_dispatcher.CheckAccess())
        {
            Refresh();
            return;
        }

        _dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
    }

    // ---------- 卡片动作：全部转发给服务 ----------

    public void Pause(string id) => _service.Pause(id);
    public void Resume(string id) => _service.Resume(id);
    public void Cancel(string id) => _service.Cancel(id);
    public void Restart(string id) => _service.Restart(id);
    public void Remove(string id) => _service.Remove(id);

    public async Task VerifyAsync(string id)
    {
        NewTaskStatusHex = "#FF83868D";
        NewTaskStatus = await _service.VerifyAsync(id);
        Refresh();
    }

    public void OpenFolder(DownloadItem item) => OpenFolderAction?.Invoke(item);

    /// <summary>页面离开时停表；回来再开。避免后台一直在跑无用的 500ms 刷新。</summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            Refresh();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    /// <summary>
    /// 程序退出：停刷新、退订事件、把最后一次进度落盘。
    ///
    /// 这里同步等一下保存（最多 3 秒）：退出路径上没有下一个「5 秒自动保存」可指望，
    /// 等了才敢说「进度已经存好了」。超时也不阻塞退出——保存失败只会让下次少一点断点。
    /// </summary>
    public void Shutdown()
    {
        _refreshTimer.Stop();
        _suspendRefresh = true;

        _service.TaskChanged -= OnTaskChanged;
        _service.TasksChanged -= OnTasksChanged;
        _service.Notice -= OnNotice;

        _service.SaveAsync();

        try
        {
            _service.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // 退出时的清理异常不影响程序结束
        }
    }
}

/// <summary>
/// 一张任务卡片。所有可读字段都从 <see cref="DownloadTask"/> 投影出来，
/// 界面只绑定这里，不直接碰 Core 的可变对象。
/// </summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    private readonly DownloadViewModel _owner;

    public DownloadItem(DownloadTask task, DownloadViewModel owner)
    {
        _owner = owner;
        Id = task.Id;
        CopyFrom(task);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string FileName { get; private set; } = "";
    public string Directory { get; private set; } = "";
    public string FilePath => Path.Combine(Directory, FileName);

    public long TotalBytes { get; private set; }
    public long DownloadedBytes { get; private set; }
    public double ProgressPercent { get; private set; }
    public double BytesPerSecond { get; private set; }
    public double? RemainingSeconds { get; private set; }

    public DownloadStatus Status { get; private set; }
    public string StatusText { get; private set; } = "";
    public string ErrorMessage { get; private set; } = "";
    public string ChecksumNote { get; private set; } = "";
    public DateTimeOffset? FinishedAt { get; private set; }

    public bool IsRunning => Status == DownloadStatus.Downloading;
    public bool IsQueued => Status == DownloadStatus.Queued;
    public bool IsPaused => Status == DownloadStatus.Paused;
    public bool IsCompleted => Status == DownloadStatus.Completed;
    public bool IsFailed => Status == DownloadStatus.Failed;
    public bool IsCanceled => Status == DownloadStatus.Canceled;

    /// <summary>卡片上「暂停 / 继续」按钮的文案。</summary>
    public string PrimaryActionText => IsRunning || IsQueued ? "暂停" : "继续";

    /// <summary>已完成的任务不再显示暂停键，改显示「校验」。</summary>
    public bool ShowPrimaryAction => !IsCompleted;

    /// <summary>失败原因只在有内容时占位，失败卡片上必须可见。</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>状态标签的底色：下载中蓝、完成绿、失败红、暂停灰、排队深灰。</summary>
    public string StatusColor => Status switch
    {
        DownloadStatus.Downloading => "#FF4C8DFF",
        DownloadStatus.Completed => "#FF34C759",
        DownloadStatus.Failed => "#FFFF453A",
        DownloadStatus.Paused => "#FFFF9F0A",
        DownloadStatus.Canceled => "#FF83868D",
        _ => "#FF5C5F66",
    };

    public string SizeText => TotalBytes > 0
        ? DownloadText.FormatBytes(TotalBytes)
        : "大小未知";

    public string ProgressText => DownloadText.FormatProgress(DownloadedBytes, TotalBytes);
    public string SpeedText => DownloadText.FormatSpeed(BytesPerSecond);
    public string RemainingText => DownloadText.FormatRemaining(RemainingSeconds);
    public string DirectoryText => Directory;

    /// <summary>校验结论只在下载完成后显示，避免下载中挂着一行「未校验」。</summary>
    public string ChecksumText => IsCompleted ? ChecksumNote : "";

    public RelayCommand PauseCommand => _pauseCommand ??= new RelayCommand(_ => _owner.Pause(Id));
    public RelayCommand ResumeCommand => _resumeCommand ??= new RelayCommand(_ => _owner.Resume(Id));
    public RelayCommand CancelCommand => _cancelCommand ??= new RelayCommand(_ => _owner.Cancel(Id));
    public RelayCommand RestartCommand => _restartCommand ??= new RelayCommand(_ => _owner.Restart(Id));
    public RelayCommand RemoveCommand => _removeCommand ??= new RelayCommand(_ => _owner.Remove(Id));
    public RelayCommand OpenFolderCommand => _openFolderCommand ??= new RelayCommand(_ => _owner.OpenFolder(this));
    public RelayCommand VerifyCommand => _verifyCommand ??= new RelayCommand(_ => _ = _owner.VerifyAsync(Id));
    public RelayCommand PrimaryCommand => _primaryCommand ??= new RelayCommand(_ =>
    {
        if (IsRunning || IsQueued) _owner.Pause(Id);
        else _owner.Resume(Id);
    });

    private RelayCommand? _pauseCommand;
    private RelayCommand? _resumeCommand;
    private RelayCommand? _cancelCommand;
    private RelayCommand? _restartCommand;
    private RelayCommand? _removeCommand;
    private RelayCommand? _openFolderCommand;
    private RelayCommand? _verifyCommand;
    private RelayCommand? _primaryCommand;

    /// <summary>从服务的最新状态同步过来。只在真的有变化时发通知，避免界面无谓重绘。</summary>
    internal void CopyFrom(DownloadTask task)
    {
        FileName = task.FileName;
        Directory = task.Directory;
        TotalBytes = task.TotalBytes;
        DownloadedBytes = task.DownloadedBytes;
        ProgressPercent = task.ProgressPercent;
        BytesPerSecond = task.BytesPerSecond;
        RemainingSeconds = task.RemainingSeconds;
        Status = task.Status;
        ErrorMessage = task.ErrorMessage ?? "";
        ChecksumNote = task.ChecksumNote ?? "";
        FinishedAt = task.CompletedAt;
        StatusText = DownloadStatusText.Describe(task.Status);

        RaiseAll();
    }

    internal void CopyFrom(DownloadItem other)
    {
        if (other.FileName == FileName
            && other.Status == Status
            && other.DownloadedBytes == DownloadedBytes
            && Math.Abs(other.BytesPerSecond - BytesPerSecond) < 1
            && other.ErrorMessage == ErrorMessage
            && other.ChecksumNote == ChecksumNote)
        {
            return;
        }

        FileName = other.FileName;
        Directory = other.Directory;
        TotalBytes = other.TotalBytes;
        DownloadedBytes = other.DownloadedBytes;
        ProgressPercent = other.ProgressPercent;
        BytesPerSecond = other.BytesPerSecond;
        RemainingSeconds = other.RemainingSeconds;
        Status = other.Status;
        ErrorMessage = other.ErrorMessage;
        ChecksumNote = other.ChecksumNote;
        FinishedAt = other.FinishedAt;
        StatusText = other.StatusText;

        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(FileName), nameof(Directory), nameof(TotalBytes), nameof(DownloadedBytes),
                     nameof(ProgressPercent), nameof(BytesPerSecond), nameof(RemainingSeconds),
                     nameof(Status), nameof(StatusText), nameof(ErrorMessage), nameof(ChecksumNote),
                     nameof(IsRunning), nameof(IsQueued), nameof(IsPaused), nameof(IsCompleted),
                     nameof(IsFailed), nameof(IsCanceled), nameof(PrimaryActionText),
                     nameof(ShowPrimaryAction), nameof(HasError), nameof(StatusColor),
                     nameof(SizeText), nameof(ProgressText), nameof(SpeedText), nameof(RemainingText),
                     nameof(ChecksumText), nameof(FilePath),
                 })
        {
            PropertyChanged?.Invoke(this, new(name));
        }
    }
}
