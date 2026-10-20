namespace GameOptimizer.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Collection;
using GameOptimizer.Core.GameMode;
using GameOptimizer.Core.Library;
using GameOptimizer.Core.Peripherals;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PdhSampler _sampler;
    private readonly GameModeController _gameMode;
    private readonly BackgroundWhitelistStore _whitelistStore = new();
    private readonly PeripheralScanner _peripherals = new();
    private readonly DispatcherTimer _timer;
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
        ToggleWhitelistPanelCommand = new RelayCommand(_ => IsWhitelistPanelOpen = !IsWhitelistPanelOpen);
        SelectAllWhitelistCommand = new RelayCommand(_ => SetAllWhitelist(true));
        ClearWhitelistCommand = new RelayCommand(_ => SetAllWhitelist(false));
        ReloadWhitelistCommand = new RelayCommand(_ => LoadWhitelistFromDisk());
        ScanPeripheralsCommand = new RelayCommand(_ => RefreshPeripherals());

        LoadWhitelistFromDisk();
        ScanGames();
        RefreshAllGameStatus();
        RefreshPeripherals();

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
    /// 于是所有游戏都显示「显存充足」。
    /// </summary>
    public VramBudget Budget { get; } = VramBudget.Detect();

    public double VramBudgetMb => Budget.BudgetMb;

    /// <summary>预算来源说明，界面必须如实标注是实测还是估算。</summary>
    public string VramBudgetText => Budget.Describe();

    public ObservableCollection<GameStatusItem> Games { get; } = [];

    public RelayCommand ScanGamesCommand { get; }
    public RelayCommand ToggleGameModeCommand { get; }
    public RelayCommand ToggleWhitelistPanelCommand { get; }
    public RelayCommand SelectAllWhitelistCommand { get; }
    public RelayCommand ClearWhitelistCommand { get; }
    public RelayCommand ReloadWhitelistCommand { get; }
    public RelayCommand ScanPeripheralsCommand { get; }

    // ---------- 首页百分比仪表盘 ----------

    private double _vramPercent;
    public double VramPercent
    {
        get => _vramPercent;
        private set
        {
            _vramPercent = OccupancyPalette.Clamp(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(VramText));
        }
    }

    /// <summary>仪表中心只显示关键信息，详细数字放在下面一行小字。</summary>
    public string VramText => $"{VramUsedMb:F0} / {VramBudgetMb:F0} MB";

    private double _vramUsedMb;
    public double VramUsedMb
    {
        get => _vramUsedMb;
        private set { _vramUsedMb = value; OnPropertyChanged(); OnPropertyChanged(nameof(VramText)); }
    }

    private double _ramPercent;
    public double RamPercent
    {
        get => _ramPercent;
        private set { _ramPercent = OccupancyPalette.Clamp(value); OnPropertyChanged(); OnPropertyChanged(nameof(RamText)); }
    }

    private double _ramFreeMb;
    public double RamFreeMb
    {
        get => _ramFreeMb;
        private set
        {
            _ramFreeMb = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RamUsedGb));
            OnPropertyChanged(nameof(RamText));
        }
    }

    /// <summary>内存总量（标称 GB）。</summary>
    public int TotalMemoryGb { get; }

    /// <summary>已用内存（GB）= 总量 - 可用量。</summary>
    public double RamUsedGb => TotalMemoryGb > 0
        ? Math.Max(0, TotalMemoryGb - RamFreeMb / 1024.0)
        : 0;

    /// <summary>内存占用/总量，例如 "9.4 / 16 GB"。</summary>
    public string RamText => $"{RamUsedGb:F1} / {TotalMemoryGb} GB";

    private double _gpuPercent;
    public double GpuPercent
    {
        get => _gpuPercent;
        private set { _gpuPercent = OccupancyPalette.Clamp(value); OnPropertyChanged(); }
    }

    private double _cpuCorePercent;
    public double CpuCorePercent
    {
        get => _cpuCorePercent;
        private set { _cpuCorePercent = OccupancyPalette.Clamp(value); OnPropertyChanged(); }
    }

    private string _gameModeStatus = "游戏模式：未开启";
    public string GameModeStatus { get => _gameModeStatus; private set { _gameModeStatus = value; OnPropertyChanged(); } }

    private bool _isGameModeOn;
    public bool IsGameModeOn
    {
        get => _isGameModeOn;
        private set
        {
            _isGameModeOn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameModeButtonText));
            OnPropertyChanged(nameof(GameModeButtonHex));
        }
    }

    public string GameModeButtonText => IsGameModeOn ? "关闭游戏模式" : "开启游戏模式";

    /// <summary>开启后按钮变红色（用户要求），关闭态恢复蓝色主按钮色。</summary>
    public string GameModeButtonHex => IsGameModeOn ? "#FFFF453A" : "#FF4C8DFF";

    /// <summary>白名单设置面板的展开状态。</summary>
    private bool _isWhitelistPanelOpen;
    public bool IsWhitelistPanelOpen
    {
        get => _isWhitelistPanelOpen;
        private set
        {
            _isWhitelistPanelOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WhitelistPanelButtonText));
        }
    }

    /// <summary>白名单设置按钮的文案：展开时提示收起。</summary>
    public string WhitelistPanelButtonText => IsWhitelistPanelOpen ? "收起白名单" : "白名单设置";

    // ---------- 外设页 ----------

    public ObservableCollection<PeripheralItem> Peripherals { get; } = [];

    private string _peripheralSummary = "正在扫描外设…";
    public string PeripheralSummary { get => _peripheralSummary; private set { _peripheralSummary = value; OnPropertyChanged(); } }

    private string _peripheralStatus = "";
    public string PeripheralStatus { get => _peripheralStatus; private set { _peripheralStatus = value; OnPropertyChanged(); } }

    /// <summary>电量读不到时的统一说明，写清楚为什么，避免用户以为是 bug。</summary>
    public string PeripheralBatteryHint =>
        "电量来自 Windows 的蓝牙电量上报。走厂商私有协议（罗技 HID++、VXE 私有命令）的无线键鼠，" +
        "系统不提供统一接口，只能显示「未知」——这不是没读到，而是系统层面拿不到。";

    /// <summary>重新扫描外设。插拔设备后手动触发。</summary>
    public void RefreshPeripherals()
    {
        var result = _peripherals.Scan();

        Peripherals.Clear();
        foreach (var d in result.Devices)
            Peripherals.Add(new PeripheralItem(d));

        if (result.Error is not null)
        {
            PeripheralSummary = "外设扫描失败";
            PeripheralStatus = result.Error;
            return;
        }

        var wired = result.Devices.Count(d => d.IsWired);
        var wireless = result.Devices.Count - wired;

        PeripheralSummary = result.Devices.Count == 0
            ? "未发现外设"
            : $"已发现 {result.Devices.Count} 个外设 · 有线 {wired} · 无线 {wireless}";

        PeripheralStatus = $"上次刷新 {DateTime.Now:HH:mm:ss}";
    }

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

    private void Tick()
    {
        if (!SamplerReady) return;

        try
        {
            var frame = _sampler.Sample(Environment.TickCount64);

            VramUsedMb = frame.CommittedMb;
            VramPercent = VramBudgetMb > 0 ? frame.CommittedMb / VramBudgetMb * 100 : 0;

            RamFreeMb = frame.AvailableRamMb;
            RamPercent = TotalMemoryGb > 0
                ? RamUsedGb / TotalMemoryGb * 100
                : 0;

            GpuPercent = frame.Gpu3DPercent;
            CpuCorePercent = frame.MaxCorePercent;
        }
        catch (Exception ex)
        {
            PeripheralStatus = $"采集异常：{ex.Message}";
        }
    }

    private void RefreshAllGameStatus()
    {
        Games.Clear();
        var assessor = new GameVramAssessor();

        foreach (var g in _games)
        {
            // 每款游戏按「这款游戏自己要多少显存」单独判定，
            // 而不是拿同一个全局占用率套到所有游戏头上。
            Games.Add(new GameStatusItem(g, assessor.Assess(g.Name, g.SizeOnDiskGb, Budget)));
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
    }

    private string _gameLibrarySummary = "";
    public string GameLibrarySummary { get => _gameLibrarySummary; private set { _gameLibrarySummary = value; OnPropertyChanged(); } }

    private void ToggleGameMode()
    {
        if (IsGameModeOn)
        {
            var errors = _gameMode.ExitWithReport().ToList();
            IsGameModeOn = false;

            // 关闭时按原路径恢复被结束的进程（用户要求），结果必须如实上报
            var restored = 0;
            var action = _gameMode.Actions.OfType<EndProcessAction>().FirstOrDefault();
            if (action is not null)
            {
                restored = action.RestoredCount;
                errors.AddRange(action.FailedRestores.Select(r =>
                    $"{ProcessDisplayNames.Resolve(r.Name)}恢复失败：{r.Error}"));
            }

            if (errors.Count == 0)
            {
                GameModeStatus = restored > 0
                    ? $"游戏模式：已关闭，已恢复 {restored} 个进程"
                    : "游戏模式：已关闭，改动已还原";
            }
            else
            {
                GameModeStatus = "⚠ 游戏模式已关闭，但有项未还原：" + string.Join("；", errors);
            }
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
        var killed = _gameMode.Actions.OfType<EndProcessAction>().FirstOrDefault();
        var detail = killed is null ? "" : $"；{killed.Summary}";
        GameModeStatus = $"游戏模式：已开启{detail}；关闭时自动恢复这些进程";
    }

    /// <summary>停止采集。关窗口时调用，保证游戏模式一定被还原。</summary>
    public void Shutdown()
    {
        _timer.Stop();

        if (_gameMode.IsActive)
        {
            // 退出时同样触发进程恢复；失败与恢复数量如实写入状态行
            var errors = _gameMode.ExitWithReport().ToList();
            var action = _gameMode.Actions.OfType<EndProcessAction>().FirstOrDefault();
            if (action is not null)
            {
                errors.AddRange(action.FailedRestores.Select(r =>
                    $"{ProcessDisplayNames.Resolve(r.Name)}恢复失败：{r.Error}"));
            }

            if (errors.Count > 0)
                GameModeStatus = "⚠ 还原未完成：" + string.Join("；", errors);
        }
    }
}

/// <summary>外设卡片的数据项：把 Core 的模型转成界面直接能绑的文本与颜色。</summary>
public sealed class PeripheralItem(PeripheralDevice device)
{
    public string KindText => device.KindText;
    public string Model => device.Model;

    public string ManufacturerText => string.IsNullOrWhiteSpace(device.Manufacturer)
        ? "型号来自设备自报"
        : device.Manufacturer;

    public string ConnectionText => device.ConnectionText;

    /// <summary>连接方式标签的底色：有线 = 蓝色主题（用户要求），无线 = 绿色。</summary>
    public string ConnectionAccentHex => device.IsWired ? "#FF4C8DFF" : "#FF34C759";

    /// <summary>整张卡的主题色：有线一律蓝，无线按电量危险程度。</summary>
    public string ThemeHex => device.IsWired
        ? "#FF4C8DFF"
        : device.BatteryPercent is { } p
            ? OccupancyPalette.ColorHex(p <= 20 ? OccupancyLevel.Critical
                                     : p <= 40 ? OccupancyLevel.High
                                     : OccupancyLevel.Low)
            : "#FF5C5F66";

    public int? BatteryPercent => device.BatteryPercent;
    public bool IsWired => device.IsWired;
    public string BatteryText => device.BatteryText;

    /// <summary>环下方的状态说明。有线与未知都要说清楚，别只留一个百分比。</summary>
    public string StatusText => device.IsWired
        ? "有线连接 · 无需充电"
        : device.BatteryPercent is { } p
            ? p switch
            {
                <= 20 => "电量偏低，尽快充电",
                <= 40 => "电量中等",
                _ => "电量充足",
            }
            : "电量未知";

    /// <summary>环右侧的副标题：连接方式 + 电量来源，一行说清楚这个数字从哪来。</summary>
    public string BatterySubText => IsWired
        ? "插线供电"
        : BatteryPercent is not null
            ? "剩余电量"
            : "系统未上报";

    /// <summary>卡片底部：只留「为什么是这个结论」，不再重复上面已有的「有线」。</summary>
    public string DetailText => IsWired
        ? "USB / 板载接口直连"
        : BatteryPercent is not null
            ? device.BatterySource ?? "蓝牙电量上报"
            : "厂商私有协议，Windows 读不到电量";
}

/// <summary>
/// 游戏库列表项。判定按「这款游戏 × 当前画质 vs 本机显存」独立计算。
/// </summary>
public sealed class GameStatusItem(GameEntry entry, GameVramAssessment assessment)
{
    public string Name => entry.Name;
    public string SizeText => $"{entry.SizeOnDiskGb:F1} GB";
    public string Path => entry.InstallPath;

    /// <summary>这款游戏正常配置下需要的显存，与占比。</summary>
    public string DemandText => assessment.Verdict == GameVramVerdict.Unknown
        ? $"{assessment.Requirement.SettingsText} 约需 {assessment.Requirement.RequiredMb:F0} MB"
        : $"{assessment.Requirement.SettingsText} 约需 {assessment.Requirement.RequiredMb:F0} MB" +
          $" / 本机 {assessment.BudgetMb:F0} MB（占用 {assessment.Ratio:P0}）";

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
        _ => "#83868D",
    };
}

/// <summary>白名单中的一个可勾选进程名。</summary>
public sealed class WhitelistEntry(string name) : INotifyPropertyChanged
{
    public string Name { get; } = name;

    /// <summary>看得懂的应用名。白名单里显示这个，而不是 wallpaper64 这种进程名。</summary>
    public string DisplayName { get; } = ProcessDisplayNames.Resolve(name);

    /// <summary>与进程名不同时才在界面附注，避免重复显示。</summary>
    public string NameHint { get; } = ProcessDisplayNames.HasName(name) ? name : "";

    /// <summary>应用图标（来自进程可执行文件）；提取失败时为 null，界面显示首字符色块。</summary>
    public System.Windows.Media.ImageSource? Icon => AppIconCache.For(name);

    /// <summary>首字符色块兜底：显示名第一个字符（或进程名首字符）。</summary>
    public string IconFallbackText => (DisplayName.Length > 0 ? DisplayName[0] : '?').ToString();

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
