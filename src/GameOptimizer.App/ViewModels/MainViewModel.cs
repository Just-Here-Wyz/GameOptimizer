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
    private readonly BackgroundWhitelistStore _whitelistStore = new();
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
        ToggleFrameCaptureCommand = new RelayCommand(_ => ToggleFrameCapture());
        SelectAllWhitelistCommand = new RelayCommand(_ => SetAllWhitelist(true));
        ClearWhitelistCommand = new RelayCommand(_ => SetAllWhitelist(false));
        ReloadWhitelistCommand = new RelayCommand(_ => LoadWhitelistFromDisk());

        LoadWhitelistFromDisk();
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
    /// 显存预算：从 DXGI 读显卡真实容量——独显取专用显存，核显取专用 + 共享。
    /// 旧实现固定用「物理内存的一半」，独显机器会把 8GB 显卡算成 16GB 预算，
    /// 于是所有游戏都显示「显存充足」。这是本次修复的核心。
    /// </summary>
    public VramBudget Budget { get; } = VramBudget.Detect();

    public double VramBudgetMb => Budget.BudgetMb;

    /// <summary>预算来源说明，界面必须如实标注是实测还是估算。</summary>
    public string VramBudgetText => Budget.Describe();

    public ObservableCollection<GameStatusItem> Games { get; } = [];
    public SessionChartData Charts { get; } = new();
    public ObservableCollection<DiagnosisFinding> Findings { get; } = [];

    public RelayCommand ScanGamesCommand { get; }
    public RelayCommand ToggleGameModeCommand { get; }
    public RelayCommand SelectAllWhitelistCommand { get; }
    public RelayCommand ClearWhitelistCommand { get; }
    public RelayCommand ReloadWhitelistCommand { get; }
    public RelayCommand ToggleMonitorCommand { get; }
    public RelayCommand ToggleFrameCaptureCommand { get; }

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

    // ---------- 结束后台白名单（用户可自定义） ----------

    /// <summary>白名单条目，带勾选状态。勾上 = 开启游戏模式时会结束它。</summary>
    public ObservableCollection<WhitelistEntry> Whitelist { get; } = [];

    /// <summary>白名单配置文件的真实路径，界面要显示出来方便用户手改。</summary>
    public string WhitelistPath => _whitelistStore.FilePath;

    private string _whitelistStatus = "";
    public string WhitelistStatus { get => _whitelistStatus; private set { _whitelistStatus = value; OnPropertyChanged(); } }

    /// <summary>当前勾选数量，让用户一眼看出「到底会关几个」。</summary>
    public string WhitelistSummary
    {
        get
        {
            var on = Whitelist.Count(e => e.Enabled);
            if (on == 0) return "尚未选择，开启游戏模式不会关闭任何程序";
            return $"将结束 {on} / {Whitelist.Count} 项";
        }
    }

    private void LoadWhitelistFromDisk()
    {
        var saved = _whitelistStore.Load();
        foreach (var e in Whitelist) e.PropertyChanged -= OnWhitelistEntryChanged;
        Whitelist.Clear();

        // 只列出「此刻确实在运行」的应用——没打开的程序列出来只会干扰判断。
        // 勾选状态另存为一份名单，与「显示哪些」分开：
        // 否则关掉的应用下次启动又被勾上，用户的取消勾选会丢失。
        var running = DetectRunningCandidates();

        foreach (var name in running.OrderBy(n => ProcessDisplayNames.Resolve(n), StringComparer.OrdinalIgnoreCase))
        {
            var entry = new WhitelistEntry(name) { Enabled = saved.Contains(name) };
            entry.PropertyChanged += OnWhitelistEntryChanged;
            Whitelist.Add(entry);
        }

        WhitelistStatus = Whitelist.Count == 0
            ? "当前没有可结束的后台应用"
            : $"当前运行 {Whitelist.Count} 项";
        OnPropertyChanged(nameof(WhitelistSummary));
    }

    /// <summary>
    /// 找出「本机正在运行、且属于可结束范围」的进程名。
    /// 排除：必要进程（系统服务 / Windows 组件 / 安全软件）、本程序自身。
    /// </summary>
    private static List<string> DetectRunningCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serviceNames = EssentialProcessGuard.LoadServiceImageNames();

        // 预设名单是「可能该关的候选」。不扫描全部进程，
        // 否则会出现一堆用户没装、也不该由我们处置的进程。
        foreach (var name in BackgroundProcessWhitelist.Strong)
        {
            if (BackgroundProcessWhitelist.IsProtected(name)) continue;

            System.Diagnostics.Process[] procs;
            try { procs = System.Diagnostics.Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    var identity = EssentialProcessGuard.Identify(p);
                    if (!EssentialProcessGuard.IsEssential(identity, serviceNames))
                        candidates.Add(name);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        return [.. candidates];
    }

    private void OnWhitelistEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WhitelistEntry.Enabled)) return;
        OnPropertyChanged(nameof(WhitelistSummary));
        SaveWhitelist();
    }

    private void SaveWhitelist()
    {
        // 保存的是「勾选状态」，包含用户主动取消勾选的项吗？不需要——
        // 未勾选就是没勾选，读回来时默认也不勾。这里只存勾上的。
        var names = Whitelist.Where(e => e.Enabled).Select(e => e.Name).ToList();

        WhitelistStatus = _whitelistStore.Save(names, out var error)
            ? $"已保存：{names.Count} 项会在开启时结束"
            : $"保存失败：{error}";
    }

    /// <summary>把当前勾选结果同步到游戏模式的结束进程动作上。</summary>
    private void ApplyWhitelistToGameMode()
    {
        var names = Whitelist.Where(e => e.Enabled).Select(e => e.Name).ToList();

        // 一项都没勾 = 用户不想结束任何进程，此时不要再偷偷套默认名单，
        // 否则会出现「我明明全取消了，它还是关了我的软件」。
        _gameMode.Replace<EndProcessAction>(new EndProcessAction(names));
    }

    private void ResetWhitelistToStrong()
    {
        LoadWhitelistFromDisk();
    }

    private void SetAllWhitelist(bool enabled)
    {
        foreach (var e in Whitelist)
        {
            e.PropertyChanged -= OnWhitelistEntryChanged;
            e.Enabled = enabled;
            e.PropertyChanged += OnWhitelistEntryChanged;
        }

        OnPropertyChanged(nameof(WhitelistSummary));
        SaveWhitelist();
    }

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

            // 采集进程可能因权限等原因静默退出，必须主动检查，
            // 否则界面会一直显示"正在采集"而实际上什么都没采到
            if (IsFrameCapturing && _frameTimes.HasFailed(out var captureError))
            {
                IsFrameCapturing = false;
                FrameTimeStatus = $"帧采集已中断：{captureError}";
                OnPropertyChanged(nameof(FrameTimeBannerVisibility));
            }

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

    private string _frameTimeStatus = "帧时间采集未启动。启动游戏后点「开始采集帧数据」即可接入 PresentMon。";
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

        // CSV 只作为事后留档的说明信息：采集期间 PresentMon 独占该文件，
        // 数据走 stdout 实时读取，不依赖文件轮询。
        var csv = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameOptimizer", "captures", $"{processName}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var ok = _frameTimes.Start(processName, csv, seconds, explicitPath);
        FrameTimeStatus = ok
            ? $"正在采集 {processName} 的帧数据…"
            : _frameTimes.LastError ?? "启动帧时间采集失败。";
        return ok;
    }

    /// <summary>手动开始采集：优先抓正在运行的游戏，没有则提示用户。</summary>
    public bool StartFrameCaptureForRunningGame()
    {
        // 检测同时返回"显示名"和"实际运行的进程名"，二者可能不同：
        // 三角洲行动扫描到 DeltaForceClient.exe，运行时却是 DeltaForceClient-Win64-Shipping.exe
        var running = GameProcessDetector.Detect(_games.Select(g => (g.Name, g.ExecutablePath)));
        if (running.Count == 0)
        {
            FrameTimeStatus = "没有检测到正在运行的游戏。请先启动游戏，再点「开始采集帧数据」。";
            OnPropertyChanged(nameof(FrameTimeBannerVisibility));
            return false;
        }

        var game = running[0];
        // PresentMon 的 --process_name 需要实际进程名
        var ok = TryStartFrameCapture(game.ProcessName);
        if (ok)
            FrameTimeStatus = $"正在采集「{game.DisplayName}」（进程 {game.ProcessName}.exe）的帧数据。";
        return ok;
    }

    /// <summary>停止帧采集。</summary>
    public void StopFrameCapture()
    {
        _frameTimes.Dispose();
        FrameTimeStatus = _recentFrameTimes.Count > 0
            ? $"已停止采集，本次共记录 {_recentFrameTimes.Count} 帧。"
            : "已停止采集，本次没有拿到帧数据。";
        OnPropertyChanged(nameof(FrameTimeBannerVisibility));
    }

    private bool _isFrameCapturing;
    public bool IsFrameCapturing
    {
        get => _isFrameCapturing;
        private set
        {
            _isFrameCapturing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FrameCaptureButtonText));
        }
    }

    public string FrameCaptureButtonText => IsFrameCapturing ? "停止采集帧数据" : "开始采集帧数据";

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
        var assessor = new GameVramAssessor();

        // 实测只算一次：同一会话的采样对所有游戏是同一个观测结果
        var measured = _frames.Count > 0
            ? new VramAssessor().Assess(_frames, VramBudgetMb)
            : null;

        foreach (var g in _games)
        {
            // 每款游戏按「这款游戏自己要多少显存」单独判定，
            // 而不是拿同一个全局占用率套到所有游戏头上。
            Games.Add(new GameStatusItem(g, assessor.Assess(g.Name, g.SizeOnDiskGb, Budget), measured));
        }

        OnPropertyChanged(nameof(GameVramSummary));
    }

    /// <summary>首页顶部的一句话总结：几款充足、几款紧张。</summary>
    public string GameVramSummary
    {
        get
        {
            if (Games.Count == 0) return "";

            var tight = Games.Count(g => g.Verdict == GameVramVerdict.Tight);
            var unknown = Games.Count(g => g.Verdict == GameVramVerdict.Unknown);
            var fine = Games.Count - tight - unknown;

            var text = $"{fine} 款显存充足";
            if (tight > 0) text += $"，{tight} 款显存紧张";
            if (unknown > 0) text += $"，{unknown} 款无法判定";
            return text;
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

        // 显卡偏好需要一个具体的 exe。旧版本从未赋值，这项动作一直在空转，
        // 却让用户以为「已经优化过了」。优先挑体积最大的那款游戏。
        var gpuPref = _gameMode.Actions.OfType<GpuPreferenceAction>().FirstOrDefault();
        var target = _games.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g.ExecutablePath));
        if (gpuPref is not null) gpuPref.TargetApp = target?.ExecutablePath;
        GpuPreferenceTarget = target is null
            ? "显卡偏好：未找到游戏程序"
            : $"显卡偏好目标：{target.Name}";
    }

    private string _gpuPreferenceTarget = "";
    public string GpuPreferenceTarget { get => _gpuPreferenceTarget; private set { _gpuPreferenceTarget = value; OnPropertyChanged(); } }

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

        // 每次开启前把界面上的勾选同步过去，用户刚改的白名单立刻生效
        ApplyWhitelistToGameMode();

        var enterErrors = _gameMode.Enter();
        IsGameModeOn = _gameMode.IsActive;

        if (enterErrors.Count > 0)
        {
            GameModeStatus = "游戏模式：开启失败并已回滚 — " + string.Join("；", enterErrors);
            return;
        }

        // 不能只说「已开启」：必须让用户看到结束了几个进程。
        // 上一版显示已开启，实际一个进程都没关。
        var killed = _gameMode.Actions.OfType<EndProcessAction>().FirstOrDefault();
        var detail = killed is null ? "" : $"；{killed.Summary}";
        GameModeStatus = $"游戏模式：已开启{detail}；关闭时进程需手动重开";
    }

    private void ToggleMonitor()
    {
        IsMonitoring = !IsMonitoring;
        if (IsMonitoring) _clock.Start(); else _clock.Stop();
    }

    private void ToggleFrameCapture()
    {
        if (IsFrameCapturing)
        {
            StopFrameCapture();
            IsFrameCapturing = false;
            return;
        }

        IsFrameCapturing = StartFrameCaptureForRunningGame();
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

/// <summary>
/// 游戏库列表项。判定按「这款游戏 × 当前画质 vs 本机显存」独立计算，
/// 有实测数据时再叠加实测结论。
/// </summary>
public sealed class GameStatusItem(GameEntry entry, GameVramAssessment assessment, VramAssessment? measured)
{
    public string Name => entry.Name;
    public string SizeText => $"{entry.SizeOnDiskGb:F1} GB";
    public string Path => entry.InstallPath;

    /// <summary>这款游戏正常配置下需要的显存，与占比。</summary>
    public string DemandText => assessment.Verdict == GameVramVerdict.Unknown
        ? $"{assessment.Requirement.SettingsText} 约需 {assessment.Requirement.RequiredMb:F0} MB"
        : $"{assessment.Requirement.SettingsText} 约需 {assessment.Requirement.RequiredMb:F0} MB" +
          $" / 本机 {assessment.BudgetMb:F0} MB（占用 {assessment.Ratio:P0}）";

    /// <summary>
    /// 悬停在结论上时展开的完整依据：需求、预算、占比、估算来源。
    /// 列表保持「名称 + 路径 + 体积 + 结论」的清爽排版，详细数字按需查看。
    /// </summary>
    public string VerdictToolTip
    {
        get
        {
            var lines = new List<string> { DemandText };
            if (assessment.Verdict == GameVramVerdict.Unknown)
                lines.Add(assessment.Reason);
            if (MeasuredText.Length > 0) lines.Add(MeasuredText);
            return string.Join("\n", lines);
        }
    }

    public GameVramVerdict Verdict => assessment.Verdict;

    public string VerdictText => assessment.Verdict switch
    {
        GameVramVerdict.Fine => "显存充足",
        GameVramVerdict.Tight => "显存紧张",
        _ => "无法判定",
    };

    public string VerdictColor => assessment.Verdict switch
    {
        GameVramVerdict.Fine => "#34C759",
        GameVramVerdict.Tight => "#FF9F0A",
        _ => "#9AA0A6",
    };

    /// <summary>会话实测结论（只在采到数据后出现）。</summary>
    public string MeasuredText => measured is { HasMeasurement: true }
        ? measured.Verdict switch
        {
            VramVerdict.Fine => "实测：本次会话显存充足",
            VramVerdict.Tight => "实测：本次会话显存偏紧",
            _ => "实测：本次会话显存不足",
        }
        : "";
}

/// <summary>白名单中的一个可勾选进程名。</summary>
public sealed class WhitelistEntry(string name) : INotifyPropertyChanged
{
    public string Name { get; } = name;

    /// <summary>看得懂的应用名。白名单里显示这个，而不是 wallpaper64 这种进程名。</summary>
    public string DisplayName { get; } = ProcessDisplayNames.Resolve(name);

    /// <summary>与进程名不同时才在界面附注，避免重复显示。</summary>
    public string NameHint { get; } = ProcessDisplayNames.HasName(name) ? name : "";

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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
