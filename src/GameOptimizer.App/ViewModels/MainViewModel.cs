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

    /// <summary>核显的显存预算按物理内存的一半估，独显场景这里会偏保守。</summary>
    public double VramBudgetMb { get; private set; } = 8192;

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
    public string RamText => $"{RamFreeMb:F0} MB 可用";

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
            var frame = _sampler.Sample(ts, 0);
            _frames.Add(frame);

            VramUsedMb = frame.CommittedMb;
            RamFreeMb = frame.AvailableRamMb;
            GpuPercent = frame.Gpu3DPercent;
            CpuCorePercent = frame.MaxCorePercent;

            var t = ts / 1000.0;
            Charts.VramMb.Add(frame.CommittedMb);
            Charts.RamMb.Add(frame.AvailableRamMb);
            Charts.GpuPercent.Add(frame.Gpu3DPercent);
            Charts.CpuCorePercent.Add(frame.MaxCorePercent);

            ChartSampleCount = Charts.VramMb.Count;

            // 只保留最近 120 个点，避免长时间挂机把内存吃满
            Trim(Charts.VramMb); Trim(Charts.RamMb); Trim(Charts.GpuPercent); Trim(Charts.CpuCorePercent);
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
        _games = SteamLibraryScanner.Scan();
        GameLibrarySummary = _games.Count == 0
            ? "未发现 Steam 游戏库"
            : $"已扫描到 {_games.Count} 款游戏";
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
