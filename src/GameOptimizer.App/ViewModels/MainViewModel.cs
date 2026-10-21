namespace GameOptimizer.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Collection;
using GameOptimizer.Core.Download;
using GameOptimizer.Core.GameMode;
using GameOptimizer.Core.Library;
using GameOptimizer.Core.Models;
using GameOptimizer.Core.Navigation;
using GameOptimizer.Core.Peripherals;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PdhSampler _sampler;
    private readonly SystemPressureSampler _pressure = new();
    private bool _pressureReady;
    private readonly GameModeController _gameMode;
    private readonly BackgroundWhitelistStore _whitelistStore = new();
    private readonly PeripheralScanner _peripherals = new();
    private readonly MemoryCleanAction _memoryClean = new();
    private readonly DispatcherTimer _timer;
    private readonly GameVramAssessor _assessor = new();
    private IReadOnlyList<GameEntry> _games = [];

    public MainViewModel(PdhSampler sampler, bool samplerReady, GameModeController gameMode)
    {
        _sampler = sampler;
        _gameMode = gameMode;
        SamplerReady = samplerReady;
        SamplerStatus = samplerReady ? "采集就绪" : $"采集不可用：{sampler.LastError}";

        // 下载器（v2.2.0）：服务本身在 Core，这里只建一个实例并交给下载页的 ViewModel。
        // 下载与采集互不影响：上传/下载不动 PDH 计数器，共用事件循环也不会互相拖慢。
        Downloads = new DownloadViewModel(new DownloadService(), Dispatcher.CurrentDispatcher)
        {
            DirectoryPicker = DirectoryPicker.AskForFolder,
            OpenFolderAction = item => DirectoryPicker.OpenInExplorer(item.FilePath),
        };
        _ = Downloads.LoadAsync();
        // 内存分母用标称容量：实测 16GB 条上报 15.73GB，用户心智里是 16
        TotalMemoryGb = SystemMemoryInfo.NominalGb;

        // 系统压力采集独立降级：它采不到不该影响显存那套指标
        _pressureReady = _pressure.Initialize();
        if (_pressureReady) _pressure.Warmup();

        PressureStatus = _pressureReady
            ? $"系统压力：{string.Join(" · ", _pressure.AvailableMetrics)}"
            : $"系统压力采集不可用：{_pressure.LastError}";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => Tick();

        ScanGamesCommand = new RelayCommand(_ => { ScanGames(); RefreshAllGameStatus(); });
        ToggleGameModeCommand = new RelayCommand(_ => ToggleGameMode());
        ToggleWhitelistPanelCommand = new RelayCommand(_ => IsWhitelistPanelOpen = !IsWhitelistPanelOpen);
        SelectAllWhitelistCommand = new RelayCommand(_ => SetAllWhitelist(true));
        ClearWhitelistCommand = new RelayCommand(_ => SetAllWhitelist(false));
        ReloadWhitelistCommand = new RelayCommand(_ => LoadWhitelistFromDisk());
        ScanPeripheralsCommand = new RelayCommand(_ => RefreshPeripherals());
        OptimizeMemoryCommand = new RelayCommand(_ => OptimizeMemory(), _ => !IsMemoryCleaning);
        OpenCpuDetailCommand = new RelayCommand(_ =>
        {
            if (_router.OpenCpuDetail()) RaisePageVisibilityChanged();

            // 立刻采一次：采集改成按需后，不主动填一次会让详情页第一秒全是 0
            Tick();
        });
        CloseCpuDetailCommand = new RelayCommand(_ =>
        {
            if (_router.CloseCpuDetail()) RaisePageVisibilityChanged();
        });

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

    /// <summary>设置页的使用说明书。界面不再各页散落说明文字，统一从这里读。</summary>
    public IReadOnlyList<ManualSection> ManualSections => ToolManual.Sections;

    /// <summary>
    /// 权限状态：清单里已声明 requireAdministrator，双击即提权。
    /// 但仍要检测——用户可能通过计划任务、非 UAC 路径启动，此时前面那句提示就不该出现。
    /// </summary>
    public bool IsElevated { get; } = ElevationStatus.IsCurrentProcessElevated();

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

    /// <summary>下载页（v2.2.0）。委托导航项「下载」切过去。</summary>
    public DownloadViewModel Downloads { get; }

    public RelayCommand ScanGamesCommand { get; }
    public RelayCommand ToggleGameModeCommand { get; }
    public RelayCommand ToggleWhitelistPanelCommand { get; }
    public RelayCommand SelectAllWhitelistCommand { get; }
    public RelayCommand ClearWhitelistCommand { get; }
    public RelayCommand ReloadWhitelistCommand { get; }
    public RelayCommand ScanPeripheralsCommand { get; }

    /// <summary>首页内存仪表盘旁的「优化」按钮：触发 <see cref="MemoryCleanAction"/>。</summary>
    public RelayCommand OptimizeMemoryCommand { get; }
    /// <summary>点击 CPU 仪表进入详情页。</summary>
    public RelayCommand OpenCpuDetailCommand { get; }

    /// <summary>详情页左上角「← 返回」。</summary>
    public RelayCommand CloseCpuDetailCommand { get; }

    // ---------- 页面可见性（由 PageRouter 统一决定，界面只做单向绑定） ----------

    /// <summary>
    /// 导航与可见性的唯一真源。
    ///
    /// v2.1.2 之前可见性由「XAML 绑定 + 代码后置 ClearValue」共同决定，两者互相顶掉，
    /// 导致「点外设再回总览」时总览与 CPU 详情同时可见且返回按钮失灵。
    /// 现在代码后置不再碰 Visibility，这里的属性是唯一出口。
    /// </summary>
    private readonly PageRouter _router = new();

    /// <summary>详情页是否展开。收起后每秒采集会自动停下（见 <see cref="Tick"/>）。</summary>
    public bool IsCpuDetailOpen => _router.IsCpuDetailOpen;

    /// <summary>总览页主体是否可见（详情页展开或切到其它分区时收起）。</summary>
    public bool IsOverviewVisible => _router.IsOverviewVisible;

    /// <summary>CPU 详情页是否可见。切到外设 / 设置时必须跟着收起。</summary>
    public bool IsCpuDetailVisible => _router.IsCpuDetailVisible;

    /// <summary>外设页是否可见。</summary>
    public bool IsPeripheralsVisible => _router.IsPeripheralsVisible;

    /// <summary>下载页是否可见（v2.2.0）。</summary>
    public bool IsDownloadVisible => _router.IsDownloadVisible;

    /// <summary>设置页是否可见。</summary>
    public bool IsSettingsVisible => _router.IsSettingsVisible;

    /// <summary>
    /// 当前页面在导航列表里的下标。窗口被销毁后重建（托盘「显示主界面」）时用它恢复高亮，
    /// 否则新建的 ListBox 恒为 0，高亮会落在总览上而实际显示的是外设页。
    /// </summary>
    public int CurrentNavIndex => PageRouter.ToNavIndex(_router.Section);

    /// <summary>
    /// 左侧导航切换。返回 false 表示下标非法（界面来的值，不能直接抛）。
    /// </summary>
    public bool NavigateTo(int navIndex)
    {
        var page = PageRouter.FromNavIndex(navIndex);
        if (page is null) return false;

        if (_router.Navigate(page.Value)) RaisePageVisibilityChanged();

        // 下载页离屏时停掉 500ms 刷新，回来再开：后台一直刷只会白白占 UI 线程
        Downloads.SetActive(_router.IsDownloadVisible);
        return true;
    }

    /// <summary>五个可见性属性一起通知。它们总是同时变化，分开通知会漏。</summary>
    private void RaisePageVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsCpuDetailOpen));
        OnPropertyChanged(nameof(IsOverviewVisible));
        OnPropertyChanged(nameof(IsCpuDetailVisible));
        OnPropertyChanged(nameof(IsPeripheralsVisible));
        OnPropertyChanged(nameof(IsDownloadVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
    }

    private CpuDetail? _cpuDetail;
    /// <summary>详情页数据。进入详情页后每秒刷新。</summary>
    public CpuDetail? CpuDetail
    {
        get => _cpuDetail;
        private set
        {
            _cpuDetail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCpuDetail));
        }
    }

    public bool HasCpuDetail => _cpuDetail is not null;

    // ---------- 首页「优化」内存：执行中 / 成功 / 失败三态 ----------

    private bool _isMemoryCleaning;
    /// <summary>执行中：按钮禁用，避免重复点击叠加两次遍历。</summary>
    public bool IsMemoryCleaning
    {
        get => _isMemoryCleaning;
        private set
        {
            _isMemoryCleaning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OptimizeButtonText));
            OnPropertyChanged(nameof(OptimizeButtonHex));
            OptimizeMemoryCommand.RaiseCanExecuteChanged();
        }
    }

    private string _memoryOptimizeStatus = "点击「优化」释放闲置内存";
    /// <summary>状态行：执行中 / 已释放多少 / 失败原因，三态都写在这里。</summary>
    public string MemoryOptimizeStatus
    {
        get => _memoryOptimizeStatus;
        private set { _memoryOptimizeStatus = value; OnPropertyChanged(); }
    }

    private MemoryOptimizeState _memoryOptimizeState = MemoryOptimizeState.Idle;
    public MemoryOptimizeState MemoryOptimizeState
    {
        get => _memoryOptimizeState;
        private set
        {
            _memoryOptimizeState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MemoryOptimizeStatusHex));
        }
    }

    /// <summary>状态行颜色：空闲灰、进行中蓝、成功绿、失败红。</summary>
    public string MemoryOptimizeStatusHex => MemoryOptimizeState switch
    {
        MemoryOptimizeState.Running => "#FF4C8DFF",
        MemoryOptimizeState.Succeeded => "#FF34C759",
        MemoryOptimizeState.Failed => "#FFFF453A",
        _ => "#FF5C5F66",
    };

    public string OptimizeButtonText => IsMemoryCleaning ? "优化中…" : "优化";

    /// <summary>执行中把按钮压暗，成功后回到主色。颜色不写死在 XAML 触发器里。</summary>
    public string OptimizeButtonHex => IsMemoryCleaning ? "#FF2A2C33" : "#FF4C8DFF";

    /// <summary>权限不足时的常驻提示。有权限时为空字符串，界面不占位。</summary>
    public string ElevationHint => IsElevated
        ? ""
        : "当前未以管理员身份运行，部分进程的内存无法释放。请退出后右键以管理员身份运行。";

    /// <summary>
    /// 执行一次内存清理。
    ///
    /// 在后台线程跑：遍历上百个进程 + 每个都做一次 P/Invoke，同步跑会把界面卡住，
    /// 用户会以为「点了没反应」而重复点击。结果回到 UI 线程再更新绑定属性。
    /// </summary>
    private async void OptimizeMemory()
    {
        if (IsMemoryCleaning) return;

        IsMemoryCleaning = true;
        MemoryOptimizeState = MemoryOptimizeState.Running;
        MemoryOptimizeStatus = "正在释放闲置内存…";

        try
        {
            var report = await Task.Run(() =>
            {
                _memoryClean.Apply();
                return _memoryClean.Report;
            });

            if (report is null)
            {
                // Apply 成功却没留下报告属于实现缺陷，如实报错而不是显示一个假的 0
                MemoryOptimizeState = MemoryOptimizeState.Failed;
                MemoryOptimizeStatus = "内存清理未返回结果，本次不作数。";
                return;
            }

            MemoryOptimizeState = MemoryOptimizeState.Succeeded;
            MemoryOptimizeStatus = MemoryReleaseText.DescribeResult(report);

            // 状态行说完就刷新一次仪表盘，让用户立刻看到内存占用变化
            Tick();
        }
        catch (MemoryCleanException ex)
        {
            // 失败必须可见且可行动：区分「需要提权」与「已提权但仍被拒」
            MemoryOptimizeState = MemoryOptimizeState.Failed;
            MemoryOptimizeStatus = $"优化失败：{ElevationStatus.DescribeMemoryCleanFailure(ex.Reason, IsElevated)}";
        }
        catch (Exception ex)
        {
            MemoryOptimizeState = MemoryOptimizeState.Failed;
            MemoryOptimizeStatus = $"优化失败：{ex.Message}";
        }
        finally
        {
            IsMemoryCleaning = false;
        }
    }

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

    /// <summary>
    /// 当前剩余显存 = 可用容量 − 当前占用。游戏库的「是否充足」按它判定，
    /// 而不是按总容量：总容量再大，被别的程序占满时游戏一样跑不动。
    /// </summary>
    public double VramFreeMb => Math.Max(0, VramBudgetMb - VramUsedMb);

    /// <summary>游戏库抬头那一行：告诉用户结论是按哪个数算出来的。</summary>
    public string VramRemainingText => $"当前剩余 {VramFreeMb:F0} MB / 共 {VramBudgetMb:F0} MB";

    private double _vramUsedMb;
    public double VramUsedMb
    {
        get => _vramUsedMb;
        private set
        {
            _vramUsedMb = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VramText));
            OnPropertyChanged(nameof(VramFreeMb));
            OnPropertyChanged(nameof(VramRemainingText));
        }
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
    /// <summary>
    /// 仪表盘上显示的 CPU 峰值 = **性能核峰值**。
    ///
    /// 不用全局单核峰值：实测本机把 8 个后台进程绑到 E 核满载时，
    /// 全局峰值亮红显示 98.6%，而跑游戏的 P 核只有 29.3%，会让人白白降画质。
    /// </summary>
    public double CpuCorePercent
    {
        get => _cpuCorePercent;
        private set
        {
            _cpuCorePercent = OccupancyPalette.Clamp(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CpuCoreLabel));
        }
    }

    /// <summary>仪表的标题：混合架构机器上写明是「P 核峰值」，避免与总 CPU 混淆。</summary>
    public string CpuCoreLabel => IsHybridCpu ? "CPU P 核峰值" : "CPU 单核峰值";

    /// <summary>仪表下方的说明：讲清这个数是怎么算出来的。</summary>
    public string CpuCoreHint => IsHybridCpu
        ? "性能核中最高的一个（跑游戏的地方）"
        : "占用最高的一个核";

    /// <summary>本机是否为混合架构（有大小核）。</summary>
    public bool IsHybridCpu => _sampler.Topology.IsHybrid;

    /// <summary>CPU 拓扑的可读描述，例如「4 大核 8 线程 · 8 小核」。</summary>
    public string CpuTopologyText => _sampler.Topology.Describe();

    // ---------- 系统压力：磁盘吞吐 + 总 CPU（补单核峰值的盲区） ----------

    private string _pressureStatus = "";
    /// <summary>实际采到哪几项。采不到的项目被列出，而不是显示 0 冒充读数。</summary>
    public string PressureStatus
    {
        get => _pressureStatus;
        private set { _pressureStatus = value; OnPropertyChanged(); }
    }

    private double _diskBytesPerSec;
    public double DiskBytesPerSec
    {
        get => _diskBytesPerSec;
        private set
        {
            _diskBytesPerSec = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiskText));
        }
    }

    /// <summary>磁盘吞吐。贴图流式加载卡不卡在磁盘上，看这个。</summary>
    public string DiskText => ThroughputText.Format(_diskBytesPerSec);

    private double _totalCpuPercent;
    public double TotalCpuPercent
    {
        get => _totalCpuPercent;
        private set
        {
            _totalCpuPercent = OccupancyPalette.Clamp(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalCpuText));
        }
    }

    /// <summary>整机 CPU 占用。仪表盘上的「单核峰值」看不出整机是否被吃满，这项才是。</summary>
    public string TotalCpuText => $"{_totalCpuPercent:F0}%";

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

    private string _peripheralStatus = "";
    public string PeripheralStatus { get => _peripheralStatus; private set { _peripheralStatus = value; OnPropertyChanged(); } }

    /// <summary>重新扫描外设。插拔设备后手动触发。</summary>
    public void RefreshPeripherals()
    {
        var result = _peripherals.Scan();

        Peripherals.Clear();
        foreach (var d in result.Devices)
            Peripherals.Add(new PeripheralItem(d));

        if (result.Error is not null)
        {
            PeripheralStatus = result.Error;
            return;
        }

        if (result.Devices.Count == 0)
        {
            PeripheralStatus = "未发现外设";
            return;
        }

        var wired = result.Devices.Count(d => d.IsWired);
        PeripheralStatus = $"{result.Devices.Count} 个外设 · 有线 {wired} · 无线 {result.Devices.Count - wired}" +
                           $" · 上次刷新 {DateTime.Now:HH:mm:ss}";
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

        SampleFrame? frame = null;

        try
        {
            frame = _sampler.Sample(Environment.TickCount64);

            VramUsedMb = frame.CommittedMb;
            VramPercent = VramBudgetMb > 0 ? frame.CommittedMb / VramBudgetMb * 100 : 0;

            // 显存每秒都在变，游戏库的结论必须跟着实时剩余显存重算
            RefreshGameVerdicts();

            RamFreeMb = frame.AvailableRamMb;
            RamPercent = TotalMemoryGb > 0
                ? RamUsedGb / TotalMemoryGb * 100
                : 0;

            GpuPercent = frame.Gpu3DPercent;
            CpuCorePercent = frame.PerformanceCorePercent;
        }
        catch (Exception ex)
        {
            PeripheralStatus = $"采集异常：{ex.Message}";
        }

        // 系统压力与逐核分组只在详情页**真的看得见**时采集：
        // 它们只在详情页展示，收起状态下每秒读一遍纯属浪费
        // （磁盘计数器要给各物理盘各读一次，不是零成本）。
        // 注意用 IsCpuDetailVisible 而不是 IsCpuDetailOpen：用户展开详情后切到外设页，
        // 详情页已经被藏起来了，这时再采样就是纯浪费。
        if (!IsCpuDetailVisible) return;

        try
        {
            if (_pressureReady)
            {
                var p = _pressure.Sample(Environment.TickCount64);

                DiskBytesPerSec = p.DiskBytesPerSec;
                TotalCpuPercent = p.TotalCpuPercent;
            }
        }
        catch (Exception ex)
        {
            PressureStatus = $"系统压力采集异常：{ex.Message}";
        }

        // 逐核分组。用全限定名：属性也叫 CpuDetail，直接写 CpuDetail.From 会被解析成属性访问。
        if (frame is not null)
            CpuDetail = GameOptimizer.Core.Analysis.CpuDetail.From(
                frame, _sampler.Topology, TotalCpuPercent);
    }

    private void RefreshAllGameStatus()
    {
        Games.Clear();

        foreach (var g in _games)
        {
            // 每款游戏按「这款游戏自己要多少显存」对比本机实时剩余显存单独判定，
            // 而不是拿同一个全局占用率套到所有游戏头上。
            Games.Add(new GameStatusItem(g, Assess(g)));
        }

        OnPropertyChanged(nameof(GameVramSummary));
    }

    /// <summary>每秒重算一次结论。只有结论真的变了才通知界面，避免无谓重绘。</summary>
    private void RefreshGameVerdicts()
    {
        var changed = false;
        foreach (var item in Games) changed |= item.Apply(Assess(item.Entry));

        if (changed) OnPropertyChanged(nameof(GameVramSummary));
    }

    private GameVramAssessment Assess(GameEntry game)
        => _assessor.Assess(game.Name, game.SizeOnDiskGb, Budget, VramFreeMb);

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

        // 可选动作（内存清理等）没生效时同样要说出来：
        // 静默跳过会让用户以为那一项做了，属于本项目明确禁止的「假成功」。
        if (_gameMode.Warnings.Count > 0)
            detail += "；" + string.Join("；", _gameMode.Warnings);

        GameModeStatus = $"游戏模式：已开启{detail}；关闭时自动恢复这些进程";
    }

    /// <summary>停止采集。关窗口时调用，保证游戏模式一定被还原。</summary>
    public void Shutdown()
    {
        _timer.Stop();

        // 下载页停刷新并让服务落一次盘：不做的话「退出前最后几秒的进度」会丢
        Downloads.Shutdown();

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
/// 游戏库列表项。判定按「这款游戏的需求 × 本机实时剩余显存」独立计算，每秒刷新。
/// </summary>
public sealed class GameStatusItem(GameEntry entry, GameVramAssessment assessment) : INotifyPropertyChanged
{
    private GameVramAssessment _assessment = assessment;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameEntry Entry => entry;
    public string Name => entry.Name;
    public string SizeText => $"{entry.SizeOnDiskGb:F1} GB";
    public string Path => entry.InstallPath;

    /// <summary>这款游戏正常配置下需要的显存与当前剩余。</summary>
    public string DemandText => _assessment.Verdict == GameVramVerdict.Unknown
        ? $"{_assessment.Requirement.SettingsText} 约需 {_assessment.Requirement.RequiredMb:F0} MB"
        : $"{_assessment.Requirement.SettingsText} 约需 {_assessment.Requirement.RequiredMb:F0} MB" +
          $" / 剩余 {_assessment.FreeMb:F0} MB（占用 {_assessment.Ratio:P0}）";

    public GameVramVerdict Verdict => _assessment.Verdict;

    public string VerdictText => _assessment.Verdict switch
    {
        GameVramVerdict.Fine => "显存充足",
        GameVramVerdict.Tight => "显存紧张",
        _ => "无法判定",
    };

    public string VerdictColor => _assessment.Verdict switch
    {
        GameVramVerdict.Fine => "#34C759",
        GameVramVerdict.Tight => "#FF9F0A",
        _ => "#83868D",
    };

    /// <summary>
    /// 用新结论刷新。返回结论是否变化（供上层决定要不要更新汇总）。
    /// 需求、剩余这类数字每秒都在动，只有文本真的变了才通知，避免整表重绘。
    /// </summary>
    public bool Apply(GameVramAssessment next)
    {
        var verdictChanged = next.Verdict != _assessment.Verdict;
        var demandBefore = DemandText;

        _assessment = next;

        if (verdictChanged)
        {
            PropertyChanged?.Invoke(this, new(nameof(Verdict)));
            PropertyChanged?.Invoke(this, new(nameof(VerdictText)));
            PropertyChanged?.Invoke(this, new(nameof(VerdictColor)));
        }

        if (DemandText != demandBefore) PropertyChanged?.Invoke(this, new(nameof(DemandText)));

        return verdictChanged;
    }
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
public sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>由 ViewModel 在状态变化时触发（如「优化中」禁用按钮）。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>内存优化按钮的三态。</summary>
public enum MemoryOptimizeState
{
    /// <summary>还没点过。</summary>
    Idle = 0,

    /// <summary>正在执行。</summary>
    Running = 1,

    /// <summary>成功（哪怕释放量很小，只要经过回读验证）。</summary>
    Succeeded = 2,

    /// <summary>失败。失败原因写在状态行里，不让用户猜。</summary>
    Failed = 3,
}
