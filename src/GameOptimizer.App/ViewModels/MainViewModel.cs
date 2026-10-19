namespace GameOptimizer.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Collection;
using GameOptimizer.Core.GameMode;
using GameOptimizer.Core.Library;
using GameOptimizer.Core.Models;

/// <summary>一局会话的可视化数据，第二页用。裸值序列即可，时间轴等间隔。</summary>
public sealed class SessionChartData
{
    public ObservableCollection<double> VramMb { get; } = [];
    public ObservableCollection<double> RamMb { get; } = [];
    public ObservableCollection<double> GpuPercent { get; } = [];
    public ObservableCollection<double> CpuCorePercent { get; } = [];
    public ObservableCollection<double> FrameMs { get; } = [];
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PdhSampler _sampler;
    private readonly FrameTimeSource _frameTimes = new();
    private readonly GameModeController _gameMode;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly List<SampleFrame> _frames = [];
    private IReadOnlyList<GameEntry> _games = [];

    public MainViewModel(PdhSampler sampler, bool samplerReady, GameModeController gameMode)
    {
        _sampler = sampler;
        _gameMode = gameMode;
        SamplerReady = samplerReady;
        SamplerStatus = samplerReady ? "采集就绪" : $"采集不可用：{sampler.LastError}";

        // 内存分母用标称容量：实测 16GB 条上报 15.73GB，用户心智里是 16
        TotalMemoryGb = SystemMemoryInfo.NominalGb;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => Tick();

        ScanGamesCommand = new RelayCommand(_ => { ScanGames(); RefreshAllGameStatus(); });
        ToggleGameModeCommand = new RelayCommand(_ => ToggleGameMode());
        ToggleMonitorCommand = new RelayCommand(_ => ToggleMonitor());

        ScanGames();
        RefreshAllGameStatus();

        if (samplerReady) _timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new(name));

    public bool SamplerReady { get; }
    public string SamplerStatus { get; }
    public string HostSummary => $"{Environment.MachineName} · {Environment.ProcessorCount} 线程";

    /// <summary>
    /// 核显的显存预算：Windows 默认允许共享约一半物理内存。
    /// 独显场景这里会偏保守，后续可改为读实际可用量。
    /// </summary>
    public double VramBudgetMb { get; } = SystemMemoryInfo.TotalPhysicalMb > 0
        ? SystemMemoryInfo.TotalPhysicalMb / 2
        : 8192;

    public ObservableCollection<GameStatusItem> Games { get; } = [];
    public SessionChartData Charts { get; } = new();
    public ObservableCollection<DiagnosisFinding> Findings { get; } = [];

    public RelayCommand ScanGamesCommand { get; }
    public RelayCommand ToggleGameModeCommand { get; }
    public RelayCommand ToggleMonitorCommand { get; }

    private double _vramUsedMb;
    public double VramUsedMb { get => _vramUsedMb; private set { _vramUsedMb = value; OnPropertyChanged(); OnPropertyChanged(nameof(VramText)); } }

    public string VramText => $"{VramUsedMb:F0} / {VramBudgetMb:F0} MB";

    private double _ramFreeMb;
    public double RamFreeMb { get => _ramFreeMb; private set { _ramFreeMb = value; OnPropertyChanged(); OnPropertyChanged(nameof(RamText)); } }

    /// <summary>内存总量（标称 GB）。</summary>
    public int TotalMemoryGb { get; }

    /// <summary>已用内存（GB）= 总量 - 可用量。</summary>
    public double RamUsedGb => TotalMemoryGb > 0
        ? Math.Max(0, TotalMemoryGb - RamFreeMb / 1024.0)
        : 0;

    /// <summary>内存占用/总量，例如 "9.4 / 16 GB"。</summary>
    public string RamText => $"{RamUsedGb:F1} / {TotalMemoryGb} GB";

    private double _gpuPercent;
    public double GpuPercent { get => _gpuPercent; private set { _gpuPercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(GpuText)); } }
    public string GpuText => $"{GpuPercent:F0}%";

    private double _cpuCorePercent;
    public double CpuCorePercent { get => _cpuCorePercent; private set { _cpuCorePercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(CpuText)); } }
    public string CpuText => $"最高单核 {CpuCorePercent:F0}%";

    private double _fps;
    public double Fps { get => _fps; private set { _fps = value; OnPropertyChanged(); OnPropertyChanged(nameof(FpsText)); } }
    public string FpsText => Fps > 0 ? $"{Fps:F0} FPS" : "—";

    private string _gameModeStatus = "游戏模式：未开启";
    public string GameModeStatus { get => _gameModeStatus; private set { _gameModeStatus = value; OnPropertyChanged(); } }

    private bool _isGameModeOn;
    public bool IsGameModeOn { get => _isGameModeOn; private set { _isGameModeOn = value; OnPropertyChanged(); OnPropertyChanged(nameof(GameModeButtonText)); } }
    public string GameModeButtonText => IsGameModeOn ? "退出游戏模式" : "开启游戏模式";

    private bool _isMonitoring = true;
    public bool IsMonitoring { get => _isMonitoring; private set { _isMonitoring = value; OnPropertyChanged(); OnPropertyChanged(nameof(MonitorButtonText)); } }
    public string MonitorButtonText => IsMonitoring ? "暂停采集" : "开始采集";

    private string _verdictText = "尚未采集";
    public string VerdictText { get => _verdictText; private set { _verdictText = value; OnPropertyChanged(); } }

    private void Tick()
    {
        if (!SamplerReady || !IsMonitoring) return;

        try
        {
            var ts = _clock.ElapsedMilliseconds;

            // 从 PresentMon CSV 拉取这一秒内新增的帧，取平均作为本采样点的帧时间
            var newFrameTimes = _frameTimes.PollNewFrameTimes();
            if (newFrameTimes.Count > 0)
            {
                _recentFrameTimes.AddRange(newFrameTimes);
                if (_recentFrameTimes.Count > 240)
                    _recentFrameTimes.RemoveRange(0, _recentFrameTimes.Count - 240);
                UpdateFrameTimeStatus();
            }

            var frameTimeMs = newFrameTimes.Count > 0 ? newFrameTimes.Average() : 0;
            var frame = _sampler.Sample(ts, frameTimeMs);
            _frames.Add(frame);

            VramUsedMb = frame.CommittedMb;
            RamFreeMb = frame.AvailableRamMb;
            OnPropertyChanged(nameof(RamUsedGb));
            OnPropertyChanged(nameof(RamText));
            GpuPercent = frame.Gpu3DPercent;
            CpuCorePercent = frame.MaxCorePercent;

            Charts.VramMb.Add(frame.CommittedMb);
            Charts.RamMb.Add(frame.AvailableRamMb);
            Charts.GpuPercent.Add(frame.Gpu3DPercent);
            Charts.CpuCorePercent.Add(frame.MaxCorePercent);
            // 没有帧数据时不补 0，否则曲线会出现假的"帧时间归零"
            if (newFrameTimes.Count > 0) Charts.FrameMs.Add(frameTimeMs);

            ChartSampleCount = Charts.VramMb.Count;

            // 只保留最近 120 个点，避免长时间挂机把内存吃满
            Trim(Charts.VramMb); Trim(Charts.RamMb); Trim(Charts.GpuPercent);
            Trim(Charts.CpuCorePercent); Trim(Charts.FrameMs);
        }
        catch (Exception ex)
        {
            VerdictText = $"采集异常：{ex.Message}";
        }
    }

    private static void Trim(ObservableCollection<double> points)
    {
        while (points.Count > 120) points.RemoveAt(0);
    }

    private readonly List<double> _recentFrameTimes = [];

    private string _frameTimeStatus = "帧时间采集未启动。检测到游戏进程后会自动接入 PresentMon。";
    public string FrameTimeStatus
    {
        get => _frameTimeStatus;
        private set
        {
            _frameTimeStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FrameTimeBannerVisibility));
        }
    }

    /// <summary>没拿到帧数据时始终显示提示条，避免用户误以为"曲线平的就是不卡"。</summary>
    public System.Windows.Visibility FrameTimeBannerVisibility =>
        _recentFrameTimes.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    private void UpdateFrameTimeStatus()
    {
        if (_recentFrameTimes.Count == 0) return;

        var stats = FrameTimeAnalyzer.Analyze(_recentFrameTimes);
        FrameTimeStatus =
            $"帧时间采样中：最近 {_recentFrameTimes.Count} 帧，平均 {stats.AverageFps:F0} FPS，" +
            $"1% Low {stats.OnePercentLowFps:F0} FPS，卡顿尖峰 {stats.SpikeCount} 次。";
        OnPropertyChanged(nameof(FrameTimeBannerVisibility));
    }

    /// <summary>尝试为指定游戏启动 PresentMon 采集。</summary>
    public bool TryStartFrameCapture(string processName, int seconds = 900, string? explicitPath = null)
    {
        if (_frameTimes.IsRunning) return true;

        var csv = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameOptimizer", "captures", $"{processName}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var ok = _frameTimes.Start(processName, csv, seconds, explicitPath);
        FrameTimeStatus = ok
            ? $"已连接 PresentMon，正在采集 {processName}。（若长时间无数据，检查是否具备 ETW 权限）"
            : _frameTimes.LastError ?? "启动帧时间采集失败。";
        return ok;
    }

    private int _chartSampleCount;
    public int ChartSampleCount
    {
        get => _chartSampleCount;
        private set
        {
            _chartSampleCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChartSampleText));
        }
    }

    public string ChartSampleText => ChartSampleCount == 0
        ? "尚无数据，等待首次采样"
        : $"已采集 {ChartSampleCount} 个点";

    private void RefreshAllGameStatus()
    {
        Games.Clear();
        var assessor = new VramAssessor();
        foreach (var g in _games)
        {
            // 没有实测数据时，用"当前可用显存"给出保守估计
            var status = _frames.Count > 0
                ? assessor.Assess(_frames, VramBudgetMb)
                : new VramAssessment(VramVerdict.Unknown, 0, 0, RamFreeMb, []);

            Games.Add(new GameStatusItem(g, status));
        }
    }

    private void ScanGames()
    {
        // 聚合 Steam + WeGame，并剔除 Wallpaper Engine 这类非游戏
        _games = NonGameFilter.Apply(GameLibrary.ScanAll());

        if (_games.Count == 0)
        {
            GameLibrarySummary = "未发现已安装的游戏（支持 Steam 与 WeGame）";
            return;
        }

        var byPlatform = _games
            .GroupBy(g => g.Source)
            .Select(g => $"{g.Key} {g.Count()} 款");

        GameLibrarySummary = $"已扫描到 {_games.Count} 款游戏（{string.Join("，", byPlatform)}）";
    }

    private string _gameLibrarySummary = "";
    public string GameLibrarySummary { get => _gameLibrarySummary; private set { _gameLibrarySummary = value; OnPropertyChanged(); } }

    private void ToggleGameMode()
    {
        if (IsGameModeOn)
        {
            var errors = _gameMode.ExitWithReport();
            IsGameModeOn = false;
            GameModeStatus = errors.Count == 0
                ? "游戏模式：已关闭，改动已还原"
                : "⚠ 游戏模式已关闭，但有项未还原：" + string.Join("；", errors);
            return;
        }

        var enterErrors = _gameMode.Enter();
        IsGameModeOn = _gameMode.IsActive;
        GameModeStatus = enterErrors.Count == 0
            ? "游戏模式：已开启"
            : "游戏模式：开启失败并已回滚 — " + string.Join("；", enterErrors);
    }

    private void ToggleMonitor()
    {
        IsMonitoring = !IsMonitoring;
        if (IsMonitoring) _clock.Start(); else _clock.Stop();
    }

    /// <summary>停止采集并出报告。关窗口时调用，保证游戏模式一定被还原。</summary>
    public void Shutdown()
    {
        _timer.Stop();
        _frameTimes.Dispose();

        if (_gameMode.IsActive)
        {
            var errors = _gameMode.ExitWithReport();
            if (errors.Count > 0)
                GameModeStatus = "⚠ 还原未完成：" + string.Join("；", errors);
        }

        if (_frames.Count == 0) return;

        var findings = new DiagnosisEngine().Diagnose(_frames, VramBudgetMb);
        Findings.Clear();
        foreach (var f in findings) Findings.Add(f);

        var stats = FrameTimeAnalyzer.Analyze(_frames.Select(f => f.FrameTimeMs).ToArray());
        VerdictText = $"共 {_frames.Count} 个样本，平均 {stats.AverageFps:F0} FPS";
    }
}

/// <summary>游戏库列表项，含显存达标状态。</summary>
public sealed class GameStatusItem(GameEntry entry, VramAssessment assessment)
{
    public string Name => entry.Name;
    public string SizeText => $"{entry.SizeOnDiskGb:F1} GB";
    public string Path => entry.InstallPath;
    public string VerdictText => assessment.Verdict switch
    {
        VramVerdict.Fine => "显存充足",
        VramVerdict.Tight => "显存偏紧",
        VramVerdict.Insufficient => "显存不足",
        _ => "待测",
    };
    public string VerdictColor => assessment.Verdict switch
    {
        VramVerdict.Fine => "#34C759",
        VramVerdict.Tight => "#FF9F0A",
        VramVerdict.Insufficient => "#FF453A",
        _ => "#9AA0A6",
    };
}

/// <summary>最小命令实现，避免为一个按钮引入 MVVM 框架。</summary>
public sealed class RelayCommand(Action<object?> execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}
