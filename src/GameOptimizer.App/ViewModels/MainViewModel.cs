namespace GameOptimizer.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Data;
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

    /// <summary>
    /// 游戏模式动作的互斥锁：开启/关闭已挪到后台线程，而托盘退出会在 UI 线程同时还原，
    /// 两个线程碰同一份动作列表就会出现「半边回滚」。所有进入点都必须拿这把锁。
    /// </summary>
    private readonly object _gameModeGate = new();
    private readonly BackgroundWhitelistStore _whitelistStore;
    private readonly PeripheralScanner _peripherals = new();
    private readonly MemoryCleanAction _memoryClean = new();
    private readonly DispatcherTimer _timer;
    private readonly GameVramAssessor _assessor = new();
    private IReadOnlyList<GameEntry> _games = [];

    /// <summary>CPU 负载诊断规则（Core 纯逻辑）：每秒喂一帧，它负责平均、趋势与连续负载判定。</summary>
    private readonly CpuLoadAnalyzer _cpuAnalyzer = new();

    /// <summary>进程级 CPU 占用采集（差分口径）。只在 CPU 页可见时跑。</summary>
    private readonly ProcessCpuSampler _processCpu = new();

    /// <summary>快速优化执行器：只做「结束用户确认过的进程」这一件会改系统的事。</summary>
    private readonly CpuQuickOptimizeRunner _cpuOptimize = new();

    /// <summary>已安装游戏的 exe 主名，用于把游戏本体排除在「可结束」候选之外。</summary>
    private IReadOnlySet<string> _gameStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <param name="downloadStore">
    /// 下载任务存储位置。默认用 %LocalAppData% 下的真实记录；
    /// 离屏渲染截图时传一个临时文件，避免把使用者本机的下载任务拍进图片。
    /// </param>
    /// <param name="whitelistStore">
    /// 保护名单存储位置。默认用 %LocalAppData% 下的真实配置；
    /// 离屏渲染时传临时文件，避免截图过程改动使用者真实的勾选状态。
    /// </param>
    public MainViewModel(
        PdhSampler sampler,
        bool samplerReady,
        GameModeController gameMode,
        DownloadStore? downloadStore = null,
        BackgroundWhitelistStore? whitelistStore = null)
    {
        _sampler = sampler;
        _gameMode = gameMode;
        _whitelistStore = whitelistStore ?? new BackgroundWhitelistStore();
        SamplerReady = samplerReady;
        SamplerStatus = samplerReady ? "采集就绪" : $"采集不可用：{sampler.LastError}";

        // 下载器（v2.2.0）：服务本身在 Core，这里只建一个实例并交给下载页的 ViewModel。
        // 下载与采集互不影响：上传/下载不动 PDH 计数器，共用事件循环也不会互相拖慢。
        Downloads = new DownloadViewModel(
            new DownloadService(store: downloadStore), Dispatcher.CurrentDispatcher)
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
        ToggleGameModeCommand = new RelayCommand(
            _ => ToggleGameMode(),
            _ => GameModeButtonPresentation.CanToggle(IsGameModeToggling));

        // 后台进程管理（v2.4）：总览页只留入口，全部进程挪进独立浮窗。
        // 浮窗由 View 层创建（VM 不碰 Window），这里只发一个请求事件。
        OpenBackgroundManagerCommand = new RelayCommand(_ => OpenBackgroundManager());
        RescanBackgroundCommand = new RelayCommand(_ => LoadBackgroundProcesses());
        SaveBackgroundListCommand = new RelayCommand(_ => SaveBackgroundList());
        ProtectAllBackgroundCommand = new RelayCommand(_ => SetAllProtected(true));
        UnprotectAllBackgroundCommand = new RelayCommand(_ => SetAllProtected(false));

        ScanPeripheralsCommand = new RelayCommand(_ => RefreshPeripherals());
        OptimizeMemoryCommand = new RelayCommand(_ => OptimizeMemory(), _ => !IsMemoryCleaning);

        // 首页 CPU 卡片：点非按钮区域进诊断页；「CPU 优化」按钮走独立的快速优化流程。
        // 两者是**并列**的两个控件（不是嵌套按钮），点优化按钮不会连带触发导航。
        OpenCpuDetailCommand = new RelayCommand(_ => NavigateToCpu(CpuView.Diagnosis));
        ShowCpuDiagnosisCommand = new RelayCommand(_ => NavigateToCpu(CpuView.Diagnosis));
        ShowCpuAdvancedCommand = new RelayCommand(_ => NavigateToCpu(CpuView.Advanced));
        CloseCpuDetailCommand = new RelayCommand(_ =>
        {
            if (_router.CloseCpuDetail()) RaisePageVisibilityChanged();
        });

        OpenCpuOptimizeCommand = new RelayCommand(_ => OpenCpuOptimize(), _ => !IsCpuOptimizeRunning);
        CloseCpuOptimizeCommand = new RelayCommand(_ => CloseCpuOptimize());
        RunCpuOptimizeCommand = new RelayCommand(_ => RunCpuOptimize(), _ => CanRunCpuOptimize);
        RevertCpuOptimizeCommand = new RelayCommand(_ => RevertCpuOptimize(),
            _ => HasCpuOptimizeChanges && !IsCpuOptimizeRunning);

        // 先扫游戏库：白名单扫描要用游戏 exe 名排除游戏进程，
        // 顺序反了会把正在玩的游戏列进「可结束」候选里
        ScanGames();

        BackgroundProcessesView = CollectionViewSource.GetDefaultView(BackgroundProcesses);
        BackgroundProcessesView.Filter = MatchesBackgroundFilter;
        _selectedBackgroundFilter = BackgroundFilters[0];
        LoadBackgroundProcesses();

        RefreshAllGameStatus();
        RefreshPeripherals();

        if (samplerReady) _timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new(name));

    public bool SamplerReady { get; }
    public string SamplerStatus { get; }

    /// <summary>
    /// 左下角与设置页显示的版本号（如 <c>v2.3.0</c>）。
    ///
    /// 从程序集信息读，不在 XAML 里写死：只要 `Directory.Build.props` 升了版本，
    /// 界面立刻跟着变，不存在「代码 2.4.0、界面还写 2.3.0」这种对不上的情况。
    /// 程序集信息里若带 <c>+commit</c> 后缀（SourceLink 会加），这里只留版本号本身。
    /// </summary>
    public static string AppVersionText { get; } = BuildVersionText(compact: true);

    /// <summary>设置页用：带文件版本，方便排查「装的是不是这个包」。</summary>
    public static string AppVersionDetail { get; } = BuildVersionText(compact: false);

    private static string BuildVersionText(bool compact)
    {
        var assembly = typeof(MainViewModel).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var text = informational;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var plus = text.IndexOf('+');
            if (plus > 0) text = text[..plus];
        }
        else
        {
            var v = assembly.GetName().Version;
            text = v is null ? "未知" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        if (compact) return $"v{text}";

        var fileVersion = assembly.GetName().Version;
        var file = fileVersion is null ? "未知" : $"{fileVersion.Major}.{fileVersion.Minor}.{fileVersion.Build}.{fileVersion.Revision}";
        return $"v{text}（文件版本 {file}）";
    }

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

    /// <summary>总览页「管理后台进程」：请求打开独立的保护浮窗（由 View 层实现）。</summary>
    public RelayCommand OpenBackgroundManagerCommand { get; }

    /// <summary>重新扫描本机后台进程（浮窗里的「刷新」）。</summary>
    public RelayCommand RescanBackgroundCommand { get; }

    /// <summary>手动保存（切换即时保存失败后，用它重试并看到结果）。</summary>
    public RelayCommand SaveBackgroundListCommand { get; }

    /// <summary>把当前列出的进程全部勾选为受保护。</summary>
    public RelayCommand ProtectAllBackgroundCommand { get; }

    /// <summary>取消全部保护勾选。<b>只改保护状态，不会结束任何进程</b>；下次启动游戏模式时它们会被结束。</summary>
    public RelayCommand UnprotectAllBackgroundCommand { get; }

    public RelayCommand ScanPeripheralsCommand { get; }

    /// <summary>首页内存仪表盘旁的「优化」按钮：触发 <see cref="MemoryCleanAction"/>。</summary>
    public RelayCommand OptimizeMemoryCommand { get; }

    /// <summary>点击首页 CPU 卡片（非按钮区域）进入 CPU 诊断页。</summary>
    public RelayCommand OpenCpuDetailCommand { get; }

    /// <summary>CPU 页二级导航：切到诊断视图。</summary>
    public RelayCommand ShowCpuDiagnosisCommand { get; }

    /// <summary>CPU 页二级导航：切到高级详情视图。</summary>
    public RelayCommand ShowCpuAdvancedCommand { get; }

    /// <summary>CPU 页左上角「← 返回」。</summary>
    public RelayCommand CloseCpuDetailCommand { get; }

    /// <summary>首页「CPU 优化」按钮：进入独立的快速优化流程。</summary>
    public RelayCommand OpenCpuOptimizeCommand { get; }

    /// <summary>快速优化流程里的「取消 / 返回诊断」。</summary>
    public RelayCommand CloseCpuOptimizeCommand { get; }

    /// <summary>快速优化流程里的「执行选中项」。未勾选任何项时不可用。</summary>
    public RelayCommand RunCpuOptimizeCommand { get; }

    /// <summary>执行后按原路径重新拉起被结束的进程。</summary>
    public RelayCommand RevertCpuOptimizeCommand { get; }

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

    /// <summary>总览页主体是否可见（CPU 页展开或切到其它分区时收起）。</summary>
    public bool IsOverviewVisible => _router.IsOverviewVisible;

    /// <summary>CPU 页容器是否可见。切到外设 / 设置时必须跟着收起。</summary>
    public bool IsCpuDetailVisible => _router.IsCpuDetailVisible;

    /// <summary>CPU 诊断视图是否可见（默认视图）。</summary>
    public bool IsCpuDiagnosisVisible => _router.IsCpuDiagnosisVisible;

    /// <summary>CPU 高级详情视图是否可见。</summary>
    public bool IsCpuAdvancedVisible => _router.IsCpuAdvancedVisible;

    /// <summary>快速优化流程是否可见（寄居在诊断视图里）。</summary>
    public bool IsCpuOptimizeVisible => _router.IsCpuOptimizeVisible;

    /// <summary>二级导航「CPU 诊断」的高亮判定。</summary>
    public bool IsCpuDiagnosisTab => _router.CpuTab == CpuView.Diagnosis;

    /// <summary>二级导航「高级详情」的高亮判定。</summary>
    public bool IsCpuAdvancedTab => _router.CpuTab == CpuView.Advanced;

    /// <summary>诊断页签底色：选中为主色，未选中为普通按钮灰。</summary>
    public string CpuDiagnosisTabHex => IsCpuDiagnosisTab ? "#FF4C8DFF" : "#FF26282F";

    /// <summary>高级详情页签底色。</summary>
    public string CpuAdvancedTabHex => IsCpuAdvancedTab ? "#FF4C8DFF" : "#FF26282F";

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

    /// <summary>CPU 页的全部可见性属性一起通知。它们总是同时变化，分开通知会漏。</summary>
    private void RaisePageVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsCpuDetailOpen));
        OnPropertyChanged(nameof(IsOverviewVisible));
        OnPropertyChanged(nameof(IsCpuDetailVisible));
        OnPropertyChanged(nameof(IsCpuDiagnosisVisible));
        OnPropertyChanged(nameof(IsCpuAdvancedVisible));
        OnPropertyChanged(nameof(IsCpuOptimizeVisible));
        OnPropertyChanged(nameof(IsCpuDiagnosisTab));
        OnPropertyChanged(nameof(IsCpuAdvancedTab));
        OnPropertyChanged(nameof(CpuDiagnosisTabHex));
        OnPropertyChanged(nameof(CpuAdvancedTabHex));
        OnPropertyChanged(nameof(IsPeripheralsVisible));
        OnPropertyChanged(nameof(IsDownloadVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));

        // 离开 CPU 页就丢掉采样历史：中间那段确实没采（页面不可见时按约定停采），
        // 把新旧数据拼成一条「连续的 30 秒」是伪造，下次进来重新累计才对。
        if (!_router.IsCpuDetailVisible) ResetCpuSampling();
    }

    /// <summary>清空 CPU 诊断的采样历史与进程排名（页面不可见时调用）。</summary>
    private void ResetCpuSampling()
    {
        _cpuAnalyzer.Reset();
        _processCpu.Reset();
        CpuProcesses.Clear();
        _lastRanking = null;
        CpuProcessStatus = "采样中（页面可见后开始采样）";
        CpuReport = CpuDiagnosis.Empty("尚未开始采样");
    }

    /// <summary>切到 CPU 页的某个二级视图（诊断 / 高级详情）。</summary>
    private void NavigateToCpu(CpuView view)
    {
        if (_router.OpenCpuDetail(view)) RaisePageVisibilityChanged();

        // 立刻补一帧：采集是按需的，不主动填一次会让新页面第一秒全是空
        Tick();
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

    private double? _totalCpuPercent;

    /// <summary>
    /// 整机 CPU **总体占用率**（PDH `Processor\% Processor Time\_Total`）。
    ///
    /// 首页仪表显示它，CPU 页的「总 CPU」也用它——同一个计数器每秒只读一次，
    /// 不重复采集，也不会出现「首页 42%、详情页 37%」这种两套口径。
    ///
    /// null 表示这一秒没采到：界面显示「未知」，绝不用 0 冒充满载或空闲。
    /// </summary>
    public double? TotalCpuPercent
    {
        get => _totalCpuPercent;
        private set
        {
            if (_totalCpuPercent == value) return;
            _totalCpuPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CpuLoadPercent));
            OnPropertyChanged(nameof(HasCpuLoad));
            OnPropertyChanged(nameof(CpuLoadText));
            OnPropertyChanged(nameof(CpuGaugeText));
        }
    }

    /// <summary>仪表环的取值。读不到时给 0，但环中心写「未知」——环上不存在假数字。</summary>
    public double CpuLoadPercent => TotalCpuPercent ?? 0;

    /// <summary>是否采到了总 CPU 读数。</summary>
    public bool HasCpuLoad => TotalCpuPercent.HasValue;

    /// <summary>仪表下方那行数字：总体占用率，读不到写「未知」。</summary>
    public string CpuLoadText => TotalCpuPercent is { } v ? $"{v:F0}%" : "未知";

    /// <summary>环中心文字：有数时留空（环里自己画百分比），无数时写「未知」。</summary>
    public string CpuGaugeText => HasCpuLoad ? "" : "未知";

    /// <summary>本机是否为混合架构（有大小核）。高级详情里据此决定是否展示 P/E 核分组。</summary>
    public bool IsHybridCpu => _sampler.Topology.IsHybrid;

    /// <summary>CPU 拓扑的可读描述，例如「4 大核 8 线程 · 8 小核」。</summary>
    public string CpuTopologyText => _sampler.Topology.Describe();

    /// <summary>CPU 型号（注册表 ProcessorNameString），读不到为「未知」。</summary>
    public string CpuModelText => CpuIdentity.ModelName;

    /// <summary>核心 / 线程数：物理核按 CoreIndex 去重，线程取逻辑处理器数。</summary>
    public string CpuCoreCountText => $"{_sampler.Topology.PhysicalCoreCount} 核 / {_sampler.Topology.Processors.Count} 线程";

    // ---------- 系统压力：磁盘吞吐 + 总 CPU（补单核峰值的盲区） ----------

    private string _pressureStatus = "";
    /// <summary>实际采到哪几项。采不到的项目被列出，而不是显示 0 冒充读数。</summary>
    public string PressureStatus
    {
        get => _pressureStatus;
        private set { _pressureStatus = value; OnPropertyChanged(); }
    }

    private double? _diskBytesPerSec;

    /// <summary>磁盘吞吐（各物理盘求和）。null = 这一秒没采到，界面写「未知」。</summary>
    public double? DiskBytesPerSec
    {
        get => _diskBytesPerSec;
        private set
        {
            if (_diskBytesPerSec == value) return;
            _diskBytesPerSec = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiskText));
        }
    }

    /// <summary>
    /// 磁盘吞吐。贴图流式加载卡不卡在磁盘上看这个。
    /// 它是**系统压力**指标，不是 CPU 指标——界面文案不能混着写。
    /// </summary>
    public string DiskText => _diskBytesPerSec is { } v ? ThroughputText.Format(v) : "未知";

    private string _gameModeStatus = "游戏模式：未开启";
    public string GameModeStatus { get => _gameModeStatus; private set { _gameModeStatus = value; OnPropertyChanged(); } }

    /// <summary>状态行颜色：进行中蓝、已开启绿、失败红、空闲灰。</summary>
    public string GameModeStatusHex => GameModeButtonPresentation.StatusHex(GameModePhase);

    private GameModePhase _gameModePhase = GameModePhase.Idle;
    public GameModePhase GameModePhase
    {
        get => _gameModePhase;
        private set
        {
            if (_gameModePhase == value) return;
            _gameModePhase = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameModeStatusHex));
        }
    }

    private bool _isGameModeOn;
    public bool IsGameModeOn
    {
        get => _isGameModeOn;
        private set
        {
            if (_isGameModeOn == value) return;
            _isGameModeOn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameModeButtonText));
            OnPropertyChanged(nameof(GameModeButtonHex));
        }
    }

    private bool _isGameModeToggling;

    /// <summary>
    /// 正在开启/关闭。开启要切电源计划并结束后台进程，实测以秒计；
    /// 这期间按钮禁用并显示「开启中…」，否则界面冻结会被当成「点了没反应」。
    /// </summary>
    public bool IsGameModeToggling
    {
        get => _isGameModeToggling;
        private set
        {
            if (_isGameModeToggling == value) return;
            _isGameModeToggling = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGameModeBusy));
            OnPropertyChanged(nameof(GameModeButtonText));
            OnPropertyChanged(nameof(GameModeButtonHex));
            ToggleGameModeCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>按钮可用性的反向只读视图，XAML 里绑 IsEnabled 用。</summary>
    public bool IsGameModeBusy => _isGameModeToggling;

    /// <summary>处理中的方向：true = 正在开启。决定「开启中…」还是「关闭中…」。</summary>
    private bool _gameModeEntering;

    public string GameModeButtonText =>
        GameModeButtonPresentation.ButtonText(_isGameModeToggling, IsGameModeOn, _gameModeEntering);

    /// <summary>开启后按钮变红色（用户要求），处理中压暗，关闭态恢复蓝色主按钮色。</summary>
    public string GameModeButtonHex =>
        GameModeButtonPresentation.ButtonBackgroundHex(_isGameModeToggling, IsGameModeOn);

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

    // ---------- 后台进程保护（v2.4：勾选即保护） ----------

    /// <summary>
    /// 扫描到的后台进程。总览页与独立浮窗<b>读同一份</b>——
    /// 不再另建一个 ViewModel 缓存同样的状态，避免两处显示不一致。
    /// </summary>
    public ObservableCollection<BackgroundProcessEntry> BackgroundProcesses { get; } = [];

    /// <summary>搜索 / 状态筛选后的视图，浮窗直接绑它。</summary>
    public ICollectionView BackgroundProcessesView { get; }

    /// <summary>
    /// 状态筛选档。系统关键进程由扫描阶段直接剔除（不展示），
    /// 因此这里不再提供「系统关键」一档——留一个永远筛不出东西的按钮只会让人困惑。
    /// </summary>
    public IReadOnlyList<BackgroundFilterOption> BackgroundFilters { get; } =
    [
        new(BackgroundListFilter.All, "全部"),
        new(BackgroundListFilter.Protected, "受保护"),
        new(BackgroundListFilter.Unprotected, "未保护"),
        new(BackgroundListFilter.Unknown, "无法识别"),
    ];

    private BackgroundFilterOption _selectedBackgroundFilter = null!;
    public BackgroundFilterOption SelectedBackgroundFilter
    {
        get => _selectedBackgroundFilter;
        set
        {
            if (value is null || ReferenceEquals(_selectedBackgroundFilter, value)) return;
            _selectedBackgroundFilter = value;
            OnPropertyChanged();
            BackgroundProcessesView?.Refresh();
        }
    }

    private string _backgroundSearchText = "";
    public string BackgroundSearchText
    {
        get => _backgroundSearchText;
        set
        {
            if (_backgroundSearchText == value) return;
            _backgroundSearchText = value ?? "";
            OnPropertyChanged();
            BackgroundProcessesView?.Refresh();
        }
    }

    /// <summary>配置文件真实路径，界面显示出来方便用户手改。</summary>
    public string BackgroundConfigPath => _whitelistStore.FilePath;

    /// <summary>列表顶部的计数：总数 / 受保护 / 会被结束 / 无法识别。</summary>
    public string BackgroundCountText
    {
        get
        {
            var total = BackgroundProcesses.Count;
            var protectedCount = BackgroundProcesses.Count(e => e.IsProtected);
            var killable = BackgroundProcesses.Count(e => e.Verdict.Eligible);
            var blocked = total - protectedCount - killable;
            var unknown = BackgroundProcesses.Count(e => e.IsIdentityUnknown);

            var text = $"共 {total} 项 · 受保护 {protectedCount} · 会被结束 {killable}";
            if (blocked > 0) text += $" · 安全检查拦下 {blocked}";
            return text + $" · 无法识别 {unknown}";
        }
    }

    /// <summary>总览页一行摘要：用户只关心「保护了几个、会关几个」。</summary>
    public string BackgroundSummaryText
    {
        get
        {
            var protectedCount = _protectedNames.Count;
            var text = $"受保护 {protectedCount} 项";

            // 没打开过浮窗时列表还没扫描，此时说「会结束 0 项」是误导，直接说明未统计
            if (BackgroundProcesses.Count == 0) return text + " · 打开「管理后台进程」可查看将结束的项目";

            var killable = CurrentEndTargets().Count;
            return killable == 0
                ? text + " · 不会被结束的进程"
                : text + $" · 启动游戏模式时将结束 {killable} 项";
        }
    }

    /// <summary>浮窗底部：此刻启动游戏模式，到底会结束哪些（按「此刻正在运行」算）。</summary>
    public string BackgroundKillPreviewText
    {
        get
        {
            var targets = CurrentEndTargets();
            if (targets.Count == 0)
            {
                return BackgroundProcesses.Count == 0
                    ? "当前没有可管理的进程"
                    : "没有进程会被结束（都已勾选保护，或被安全检查拦下）";
            }

            var preview = string.Join("、", targets.Take(5));
            if (targets.Count > 5) preview += $" 等 {targets.Count} 项";
            return $"启动游戏模式时将结束 {targets.Count} 项：{preview}";
        }
    }

    private string _backgroundStatusText = "";
    /// <summary>保存 / 扫描结果。失败必须在这里看得见。</summary>
    public string BackgroundStatusText
    {
        get => _backgroundStatusText;
        private set
        {
            _backgroundStatusText = value;
            OnPropertyChanged();
        }
    }

    private bool _hasBackgroundStatusError;
    /// <summary>状态行是否为错误（界面用红色显示，不静默）。</summary>
    public bool HasBackgroundStatusError
    {
        get => _hasBackgroundStatusError;
        private set
        {
            _hasBackgroundStatusError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BackgroundStatusHex));
        }
    }

    public string BackgroundStatusHex => _hasBackgroundStatusError ? "#FFFF453A" : "#FF83868D";

    private string _backgroundNoticeText = "";
    /// <summary>旧配置迁移提示 / 读取失败说明。有内容时界面必须显示。</summary>
    public string BackgroundNoticeText
    {
        get => _backgroundNoticeText;
        private set
        {
            _backgroundNoticeText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasBackgroundNotice));
        }
    }

    public bool HasBackgroundNotice => _backgroundNoticeText.Length > 0;

    /// <summary>
    /// 受保护进程名（跨次运行保留）。这是<b>唯一</b>持久化的用户输入：
    /// 列表只显示「此刻正在运行」的进程，而保护意图必须记住没运行的那些。
    /// 其余进程默认都是结束目标，不需要存。
    /// </summary>
    private readonly HashSet<string> _protectedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>批量改状态时压住逐条保存，最后只写一次盘。</summary>
    private bool _suppressBackgroundSave;

    /// <summary>打开管理浮窗（由 MainWindow 订阅，VM 不直接创建 Window）。</summary>
    public event EventHandler? OpenBackgroundManagerRequested;

    /// <summary>当前真正会被结束的进程名（与传给结束动作的名单同源）。</summary>
    private IReadOnlyList<string> CurrentEndTargets()
        => BackgroundKillPlan.Resolve(BackgroundProcesses.Select(e => e.ToFacts(_gameStems)));

    private void OpenBackgroundManager()
    {
        // 每次打开都重新扫描：进程开关很频繁，用旧列表管理没有意义
        LoadBackgroundProcesses();
        OpenBackgroundManagerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 重新扫描并重建列表。勾选状态从磁盘读回，而不是沿用内存里的旧条目：
    /// 刷新必须反映「现在到底存了什么」，否则用户手改过文件也看不到。
    /// </summary>
    private void LoadBackgroundProcesses()
    {
        var load = _whitelistStore.LoadState();

        // 读取失败也不能让整个列表空着不给看：进程信息照样展示，
        // 只是提示里说明「配置没读出来」——此时保护名单为空，等于所有候选都会被结束，
        // 所以必须把这件事写在红字里，而不是让用户以为「没设置 = 什么都不关」。
        _protectedNames.Clear();
        foreach (var name in load.State.Protected) _protectedNames.Add(name);

        foreach (var e in BackgroundProcesses) e.PropertyChanged -= OnBackgroundEntryChanged;
        BackgroundProcesses.Clear();

        List<BackgroundProcessInfo> entries;
        int autoDetected;
        try
        {
            var scanned = BackgroundCandidateScan.ScanEntries(CurrentGameStems());

            // 元数据读取 / 用途识别都在 Inspect 内部逐项降级；这里再兜一层，
            // 保证「扫描或建模整体失败」也只是列表空 + 明确报错，而不是把界面炸掉。
            entries = scanned
                .Select(entry => BackgroundProcessInspector.Inspect(
                    entry, _protectedNames.Contains(entry.ProcessName)))
                .OrderBy(info => info.State == ProtectionState.SystemProtected ? 1 : 0)
                .ThenBy(info => info.AppName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            autoDetected = scanned.Count(s =>
                s.Kind == BackgroundCandidateKind.Endable && !s.InPreset);
        }
        catch (Exception ex)
        {
            // 扫描整体失败：列表清空 + 明确报错，不能让用户以为「没有进程」
            BackgroundNoticeText = "";
            BackgroundStatusText = $"扫描失败：{ex.Message}";
            HasBackgroundStatusError = true;
            NotifyBackgroundChanged();
            return;
        }

        foreach (var info in entries)
        {
            // 结束与否由 Core 判据即时算出（未保护即默认结束），不再需要从磁盘恢复标记
            var entry = new BackgroundProcessEntry(info);
            entry.PropertyChanged += OnBackgroundEntryChanged;
            BackgroundProcesses.Add(entry);
        }

        BackgroundNoticeText = load.Error is not null
            ? $"读取配置失败：{load.Error}（保护名单为空，所有候选都会被结束，请先核对保护项）"
            : load.Notice ?? "";

        HasBackgroundStatusError = load.Error is not null;

        BackgroundStatusText = BackgroundProcesses.Count == 0
            ? "当前没有可管理的后台进程"
            : load.Error is not null
                ? $"已加载 {BackgroundProcesses.Count} 项，但配置读取失败，保护名单按空处理"
                : $"当前运行 {BackgroundProcesses.Count} 项" +
                  (autoDetected > 0 ? $"（其中 {autoDetected} 项为内置名单外的自动检测项）" : "");

        NotifyBackgroundChanged();
    }

    /// <summary>已安装游戏的 exe 主名：游戏本体与反作弊不能成为结束目标。</summary>
    private List<string> CurrentGameStems()
        => _games
            .Select(g => string.IsNullOrWhiteSpace(g.ExecutablePath)
                ? null
                : Path.GetFileNameWithoutExtension(g.ExecutablePath))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();

    /// <summary>
    /// 列表视图的筛选：先按状态，再按搜索词。
    /// 判据在 <see cref="BackgroundProcessFilter"/>（Core 纯函数），界面只负责传参。
    /// </summary>
    private bool MatchesBackgroundFilter(object? item)
    {
        if (item is not BackgroundProcessEntry entry) return false;

        return BackgroundProcessFilter.Matches(
            entry.Info,
            entry.IsProtected,
            SelectedBackgroundFilter?.Value ?? BackgroundListFilter.All,
            BackgroundSearchText);
    }

    private void OnBackgroundEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(BackgroundProcessEntry.IsProtected)
            or nameof(BackgroundProcessEntry.IsEndTarget))) return;

        if (sender is not BackgroundProcessEntry entry) return;

        if (entry.IsProtected) _protectedNames.Add(entry.ProcessName);
        else _protectedNames.Remove(entry.ProcessName);

        NotifyBackgroundChanged();

        // 即时持久化：改一次就存一次，用户不必记得点保存。
        // 批量操作时由调用方统一保存，避免几十次写盘。
        if (!_suppressBackgroundSave) SaveBackgroundList();
    }

    /// <summary>保存保护名单。失败必须写进状态行。</summary>
    private void SaveBackgroundList()
    {
        var state = new BackgroundProtectionState(
            _protectedNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());

        if (_whitelistStore.SaveState(state, out var error))
        {
            HasBackgroundStatusError = false;
            BackgroundStatusText = state.Protected.Count == 0
                ? "已保存：没有保护项，启动游戏模式时将结束所有通过安全检查的候选"
                : $"已保存：受保护 {state.Protected.Count} 项 · 其余候选都会被结束";
            BackgroundNoticeText = "";
        }
        else
        {
            HasBackgroundStatusError = true;
            BackgroundStatusText = $"保存失败：{error}（本次改动没有写入磁盘，可点「保存」重试）";
        }

        NotifyBackgroundChanged();
    }

    /// <summary>全部勾选 / 全部取消保护。<b>不会结束任何进程</b>，只改状态。</summary>
    private void SetAllProtected(bool protect)
    {
        _suppressBackgroundSave = true;
        try
        {
            foreach (var entry in BackgroundProcesses.ToList())
            {
                if (entry.IsSystemCritical) continue;
                entry.IsProtected = protect;
            }
        }
        finally
        {
            _suppressBackgroundSave = false;
        }

        SaveBackgroundList();
    }

    private void NotifyBackgroundChanged()
    {
        OnPropertyChanged(nameof(BackgroundCountText));
        OnPropertyChanged(nameof(BackgroundSummaryText));
        OnPropertyChanged(nameof(BackgroundKillPreviewText));
        BackgroundProcessesView?.Refresh();
    }

    /// <summary>
    /// 把当前结束名单同步到游戏模式的结束进程动作上。
    /// 名单来自 <see cref="BackgroundKillPlan.Resolve"/>：未勾保护 + 通过全部安全检查的进程。
    /// </summary>
    private void ApplyWhitelistToGameMode()
    {
        var facts = BackgroundProcesses.Select(e => e.ToFacts(_gameStems)).ToList();
        var targets = BackgroundKillPlan.Resolve(facts);
        var protectedNames = BackgroundKillPlan.ProtectedNames(facts);

        // 受保护名单一并交给动作层，作为执行侧的最后一道防线：
        // 即使目标名单被外部改动，这些名字也不会被结束。
        _gameMode.Replace<EndProcessAction>(new EndProcessAction(targets, protectedNames));
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
        }
        catch (Exception ex)
        {
            PeripheralStatus = $"采集异常：{ex.Message}";
        }

        // 总 CPU：首页仪表与 CPU 页共用同一个计数器，每秒只读一次。
        // 拆开读是为了避免「同一秒读两次同一个计数器」——第二次算的是极短区间，数字会失真。
        UpdateTotalCpu();

        // 磁盘吞吐是系统压力指标，只在 CPU 页可见时才采（各物理盘各读一次，不是零成本）
        if (IsCpuDetailVisible) UpdateDiskThroughput();

        // CPU 诊断：只有诊断视图或快速优化流程可见时才喂数据。
        // 用「可见」而不是「展开」——用户切到高级详情后，诊断窗口的数据不该继续算。
        if (IsCpuDiagnosisVisible || IsCpuOptimizeVisible) UpdateCpuDiagnosis(frame);

        // 逐核分组只服务高级详情视图
        if (IsCpuAdvancedVisible && frame is not null)
            CpuDetail = GameOptimizer.Core.Analysis.CpuDetail.From(
                frame, _sampler.Topology, TotalCpuPercent);
    }

    /// <summary>读一次总 CPU。计数器不可用时置 null，界面显示「未知」。</summary>
    private void UpdateTotalCpu()
    {
        if (!_pressureReady || !_pressure.HasTotalCpu)
        {
            TotalCpuPercent = null;
            return;
        }

        try
        {
            TotalCpuPercent = _pressure.ReadTotalCpuPercent();
        }
        catch (Exception ex)
        {
            PressureStatus = $"总 CPU 采集异常：{ex.Message}";
            TotalCpuPercent = null;
        }
    }

    /// <summary>读一次磁盘吞吐（仅 CPU 页可见时）。</summary>
    private void UpdateDiskThroughput()
    {
        if (!_pressureReady || !_pressure.HasDisk) return;

        try
        {
            DiskBytesPerSec = _pressure.ReadDiskBytesPerSec();
        }
        catch (Exception ex)
        {
            PressureStatus = $"磁盘吞吐采集异常：{ex.Message}";
            DiskBytesPerSec = null;
        }
    }

    // ---------- CPU 诊断 ----------

    private CpuDiagnosis _cpuReport = CpuDiagnosis.Empty("尚未开始采样");

    /// <summary>
    /// 诊断结果（负载档位、30 秒平均、趋势、建议、采样状态）。
    /// 规则全部在 Core 的 <see cref="CpuLoadAnalyzer"/> 里，这里只负责喂数据与通知界面。
    /// </summary>
    public CpuDiagnosis CpuReport
    {
        get => _cpuReport;
        private set
        {
            _cpuReport = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CpuTrendPoints));
            OnPropertyChanged(nameof(CpuLevelHex));
        }
    }

    /// <summary>趋势图数据（最近 30 秒，只含有效帧）。</summary>
    public IReadOnlyList<CpuTrendPoint> CpuTrendPoints => _cpuReport.Trend;

    /// <summary>负载状态的颜色：高负载红、关注橙、正常绿、采样中蓝、未知灰。</summary>
    public string CpuLevelHex => _cpuReport.Level switch
    {
        CpuLoadLevel.HighLoad => "#FFFF453A",
        CpuLoadLevel.Watch => "#FFFF9F0A",
        CpuLoadLevel.Normal => "#FF34C759",
        CpuLoadLevel.Sampling => "#FF4C8DFF",
        _ => "#FF5C5F66",
    };

    /// <summary>高占用进程列表（只在能可靠算出时才展示，算不出时列表为空且状态行说明原因）。</summary>
    public ObservableCollection<ProcessCpuItem> CpuProcesses { get; } = [];

    private string _cpuProcessStatus = "采样中（页面可见后开始采样）";

    /// <summary>进程列表的采样状态：采样中 / 统计了多少个 / 几个读不到。</summary>
    public string CpuProcessStatus
    {
        get => _cpuProcessStatus;
        private set { _cpuProcessStatus = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 进程 CPU 的口径说明。界面必须展示——用户要知道这个百分比是怎么算的。
    /// </summary>
    public string CpuProcessCaliber =>
        $"口径：进程处理器时间增量 ÷ 墙钟时间 ÷ {_processCpu.LogicalProcessorCount} 个逻辑处理器，" +
        "全机进程合计不会超过 100%；受保护进程读不到处理器时间，会计入「读不到」而不是当作 0%。";

    private ProcessCpuRanking? _lastRanking;

    /// <summary>优化流程打开时进程占用还没算出来（需要两次采样），等第一份排名到了再补检测。</summary>
    private bool _cpuOptimizeAwaitingData;

    /// <summary>每秒喂一帧：先出诊断，再更新进程排名。</summary>
    private void UpdateCpuDiagnosis(SampleFrame? frame)
    {
        var now = Environment.TickCount64;

        _cpuAnalyzer.Add(new CpuLoadSample(now, TotalCpuPercent, frame?.PerCorePercent));
        CpuReport = _cpuAnalyzer.Build();

        RefreshProcessRanking(now);

        // 进入流程的第一帧通常还没有进程差分基准（要两次采样）。
        // 如果不在这里补一次，用户会一直看到「数据尚不可用」直到自己退出重进。
        if (IsCpuOptimizeVisible && !IsCpuOptimizeRunning
            && _cpuOptimizeAwaitingData && _lastRanking is { IsReady: true })
        {
            _cpuOptimizeAwaitingData = false;
            BuildCpuOptimizePlan();
        }
    }

    private void RefreshProcessRanking(long now)
    {
        try
        {
            var ranking = _processCpu.Sample(now, _gameStems, top: 6);
            _lastRanking = ranking;
            CpuProcessStatus = ranking.Note;
            SyncProcessItems(ranking.Top);
        }
        catch (Exception ex)
        {
            // 失败必须可见：列表停在上一帧，但状态行说明为什么不再更新
            _lastRanking = null;
            CpuProcessStatus = $"进程占用采集异常：{ex.Message}";
        }
    }

    /// <summary>原地更新，避免每秒重建集合导致列表闪烁。</summary>
    private void SyncProcessItems(IReadOnlyList<ProcessCpuUsage> top)
    {
        var sameShape = CpuProcesses.Count == top.Count;
        if (sameShape)
        {
            for (var i = 0; i < top.Count; i++)
            {
                if (CpuProcesses[i].ProcessId == top[i].ProcessId) continue;
                sameShape = false;
                break;
            }
        }

        if (!sameShape)
        {
            CpuProcesses.Clear();
            foreach (var p in top) CpuProcesses.Add(new ProcessCpuItem(p));
            return;
        }

        for (var i = 0; i < top.Count; i++) CpuProcesses[i].Update(top[i]);
    }

    // ---------- CPU 快速优化 ----------

    private CpuOptimizePlan _cpuOptimizePlan = CpuOptimizePlan.Empty("尚未检测");

    /// <summary>可勾选的优化项。默认**全部不勾选**：结束进程属于破坏性操作。</summary>
    public ObservableCollection<CpuOptimizeItemView> CpuOptimizeItems { get; } = [];

    /// <summary>本次检测的汇总（有几个候选、合计多少占用）。</summary>
    public string CpuOptimizeSummary => _cpuOptimizePlan.Summary;

    /// <summary>检测附注：被排除的原因、口径限制、不承诺的事项。</summary>
    public IReadOnlyList<string> CpuOptimizeNotes => _cpuOptimizePlan.Notes;

    /// <summary>是否存在可执行的候选。false 时界面只展示诊断，不给「执行」入口。</summary>
    public bool HasCpuOptimizeAction => _cpuOptimizePlan.HasExecutableAction;

    /// <summary>检测依据说明：让用户知道建议是从哪几个数推出来的。</summary>
    public string CpuOptimizeBasis
    {
        get
        {
            var level = _cpuReport.Level switch
            {
                CpuLoadLevel.HighLoad => "持续高负载",
                CpuLoadLevel.Watch => "持续偏高",
                CpuLoadLevel.Sampling => "采样中",
                CpuLoadLevel.Normal => "负载正常",
                _ => "数据不可用",
            };

            var avg = _cpuReport.Window.AveragePercent is { } a ? $"{a:F0}%" : "未知";
            return $"检测依据：负载状态「{level}」，30 秒平均 {avg}，当前 {_cpuReport.CurrentText}；" +
                   "进程占用取自最近一次差分采样。";
        }
    }

    private CpuOptimizePhase _cpuOptimizePhase = CpuOptimizePhase.Idle;

    public CpuOptimizePhase CpuOptimizePhase
    {
        get => _cpuOptimizePhase;
        private set
        {
            if (_cpuOptimizePhase == value) return;
            _cpuOptimizePhase = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CpuOptimizeStatusHex));
        }
    }

    /// <summary>状态行颜色：空闲灰、待确认蓝、执行中蓝、完成绿、失败红。</summary>
    public string CpuOptimizeStatusHex => _cpuOptimizePhase switch
    {
        CpuOptimizePhase.Running => "#FF4C8DFF",
        CpuOptimizePhase.Done => "#FF34C759",
        CpuOptimizePhase.Failed => "#FFFF453A",
        CpuOptimizePhase.Ready => "#FF4C8DFF",
        _ => "#FF5C5F66",
    };

    private string _cpuOptimizeStatus = "尚未检测";

    /// <summary>流程状态行：检测结果 / 执行中 / 实际结果。失败原因写在这里，不只写「失败」。</summary>
    public string CpuOptimizeStatus
    {
        get => _cpuOptimizeStatus;
        private set { _cpuOptimizeStatus = value; OnPropertyChanged(); }
    }

    private string _cpuOptimizeVerifyText = "";

    /// <summary>前后对比结论（同口径、同窗口）。无法对比时如实说明原因。</summary>
    public string CpuOptimizeVerifyText
    {
        get => _cpuOptimizeVerifyText;
        private set { _cpuOptimizeVerifyText = value; OnPropertyChanged(); }
    }

    /// <summary>口径说明，与对比结论一起展示。</summary>
    public string CpuOptimizeCaliberNote => CpuOptimizeVerification.CaliberNote;

    private bool _hasCpuOptimizeChanges;

    /// <summary>是否真的结束过进程。只有为 true 才允许「恢复被结束的进程」。</summary>
    public bool HasCpuOptimizeChanges
    {
        get => _hasCpuOptimizeChanges;
        private set
        {
            if (_hasCpuOptimizeChanges == value) return;
            _hasCpuOptimizeChanges = value;
            OnPropertyChanged();
            RevertCpuOptimizeCommand?.RaiseCanExecuteChanged();
        }
    }

    private bool _isCpuOptimizeRunning;

    public bool IsCpuOptimizeRunning
    {
        get => _isCpuOptimizeRunning;
        private set
        {
            if (_isCpuOptimizeRunning == value) return;
            _isCpuOptimizeRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRunCpuOptimize));
            OpenCpuOptimizeCommand?.RaiseCanExecuteChanged();
            RunCpuOptimizeCommand?.RaiseCanExecuteChanged();
            RevertCpuOptimizeCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>勾选摘要：让用户在执行前再看一眼「会关几个」。</summary>
    public string CpuOptimizeSelectionText
    {
        get
        {
            var on = CpuOptimizeItems.Count(i => i.Selected);
            if (CpuOptimizeItems.Count == 0) return "没有可执行项";
            return on == 0 ? "尚未勾选，执行按钮不可用" : $"已勾选 {on} 项";
        }
    }

    /// <summary>未勾选任何项时不可执行——这是「未经确认不结束进程」的最后一道闸门。</summary>
    public bool CanRunCpuOptimize => !IsCpuOptimizeRunning && CpuOptimizeItems.Any(i => i.Selected);

    private void OpenCpuOptimize()
    {
        if (_router.OpenCpuOptimize()) RaisePageVisibilityChanged();

        // 补一帧：进入流程时必须有当前读数与进程排名，否则「轻量检测」没依据
        Tick();
        BuildCpuOptimizePlan();

        // 第一帧通常还没算出进程占用（差分需要两次采样），标记等待并在下一次采样后补检测
        _cpuOptimizeAwaitingData = !_cpuOptimizePlan.HasExecutableAction;

        CpuOptimizeVerifyText = "";
        HasCpuOptimizeChanges = _cpuOptimize.HasChanges;
    }

    private void CloseCpuOptimize()
    {
        if (IsCpuOptimizeRunning) return; // 执行中不让退，避免用户以为已经撤销了改动
        if (_router.CloseCpuOptimize()) RaisePageVisibilityChanged();
    }

    /// <summary>
    /// 轻量检测：把现有诊断与进程排名整理成带依据的建议。
    /// **不做任何系统改动**，也不假装「扫描」了几秒。
    /// </summary>
    private void BuildCpuOptimizePlan()
    {
        _cpuOptimizePlan = CpuOptimizePlanner.Build(
            _cpuReport, _lastRanking?.Top, _processCpu.LogicalProcessorCount);

        foreach (var item in CpuOptimizeItems) item.PropertyChanged -= OnCpuOptimizeItemChanged;
        CpuOptimizeItems.Clear();

        foreach (var item in _cpuOptimizePlan.ExecutableItems)
        {
            var view = new CpuOptimizeItemView(item);
            view.PropertyChanged += OnCpuOptimizeItemChanged;
            CpuOptimizeItems.Add(view);
        }

        OnPropertyChanged(nameof(CpuOptimizeSummary));
        OnPropertyChanged(nameof(CpuOptimizeNotes));
        OnPropertyChanged(nameof(CpuOptimizeBasis));
        OnPropertyChanged(nameof(HasCpuOptimizeAction));
        OnPropertyChanged(nameof(CpuOptimizeSelectionText));
        OnPropertyChanged(nameof(CanRunCpuOptimize));
        RunCpuOptimizeCommand.RaiseCanExecuteChanged();

        CpuOptimizePhase = _cpuOptimizePlan.HasExecutableAction
            ? CpuOptimizePhase.Ready
            : CpuOptimizePhase.Idle;

        CpuOptimizeStatus = _cpuOptimizePlan.HasExecutableAction
            ? "轻量检测完成：以下各项默认不勾选，请确认后再执行"
            : "轻量检测完成：没有可安全执行的 CPU 优化动作，仅提供诊断与建议";
    }

    private void OnCpuOptimizeItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CpuOptimizeItemView.Selected)) return;

        OnPropertyChanged(nameof(CpuOptimizeSelectionText));
        OnPropertyChanged(nameof(CanRunCpuOptimize));
        RunCpuOptimizeCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 执行勾选项 → 等 5 秒 → 用同一口径回读对比。
    ///
    /// 实测结论只写实测到的东西：数字变化、观察窗口、进程是否真的退出。
    /// 不写「已提升性能」，也不提帧率。
    /// </summary>
    private async void RunCpuOptimize()
    {
        if (!CanRunCpuOptimize) return;

        var names = CpuOptimizeItems.Where(i => i.Selected).Select(i => i.ProcessName).ToList();
        if (names.Count == 0) return;

        // 执行前的窗口必须现在就取：执行之后取不到「执行前」的状态
        var before = _cpuAnalyzer.StatsOver(CpuOptimizeVerification.CompareWindowSeconds);

        IsCpuOptimizeRunning = true;
        CpuOptimizePhase = CpuOptimizePhase.Running;
        CpuOptimizeStatus = "正在结束所选应用…";
        CpuOptimizeVerifyText = "";

        try
        {
            var result = await Task.Run(() => _cpuOptimize.Execute(names));

            HasCpuOptimizeChanges = _cpuOptimize.HasChanges;
            CpuOptimizeStatus = result.Summary;

            if (!result.DidChangeAnything)
            {
                CpuOptimizePhase = CpuOptimizePhase.Failed;
                CpuOptimizeVerifyText = "没有发生系统改动，因此没有前后对比。";
                return;
            }

            CpuOptimizeStatus = $"{result.Summary} 正在观察 {CpuOptimizeVerification.CompareWindowSeconds:F0} 秒后回读…";

            // 界面每秒仍在采样，这里只等窗口填满
            await Task.Delay(TimeSpan.FromSeconds(CpuOptimizeVerification.CompareWindowSeconds + 1));

            var after = _cpuAnalyzer.StatsOver(CpuOptimizeVerification.CompareWindowSeconds);
            CpuOptimizeVerifyText = CpuOptimizeVerification.DescribeComparison(before, after);

            CpuOptimizePhase = CpuOptimizePhase.Done;
            CpuOptimizeStatus = "已执行完成，结果以回读数据为准";
        }
        catch (Exception ex)
        {
            CpuOptimizePhase = CpuOptimizePhase.Failed;
            CpuOptimizeStatus = $"执行失败：{ex.Message}";
            CpuOptimizeVerifyText = "执行过程中出现异常，未生成对比结论。";
        }
        finally
        {
            IsCpuOptimizeRunning = false;

            // 重新检测：被结束的应用已经从进程列表里消失，旧的建议不再成立
            BuildCpuOptimizePlan();
        }
    }

    /// <summary>回读验证失败或用户反悔时，按原路径重新拉起被结束的进程。</summary>
    private void RevertCpuOptimize()
    {
        var failures = _cpuOptimize.Revert();
        HasCpuOptimizeChanges = _cpuOptimize.HasChanges;

        if (failures.Count == 0)
        {
            CpuOptimizePhase = CpuOptimizePhase.Done;
            CpuOptimizeStatus = "已按原路径重新拉起被结束的进程";
            CpuOptimizeVerifyText = "恢复只保证「重新启动该程序」，不保证恢复窗口、未保存内容与登录状态。";
            return;
        }

        CpuOptimizePhase = CpuOptimizePhase.Failed;
        CpuOptimizeStatus = "恢复未完成：" + string.Join("；", failures);
        CpuOptimizeVerifyText = "部分进程未能自动恢复，请手动重新打开。";
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

        // 游戏 exe 主名供两处复用：白名单排除游戏进程、进程占用列表标记「游戏本体」。
        // 正在玩的游戏绝不能被列为「可以一键结束」的候选。
        _gameStems = new HashSet<string>(
            _games.Select(g => string.IsNullOrWhiteSpace(g.ExecutablePath)
                    ? null
                    : Path.GetFileNameWithoutExtension(g.ExecutablePath))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!),
            StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 开启 / 关闭游戏模式。
    ///
    /// 必须在后台线程执行：powercfg 要起两次进程（各等 3 秒上限），
    /// 结束白名单进程时每个还要等 5 秒确认退出，内存清理还要遍历全机进程。
    /// 同步跑会把 UI 线程按在地上，按钮连按下的视觉反馈都画不出来——
    /// 用户以为没点上，恢复后队列里的点击会连开带关，状态彻底乱掉。
    /// </summary>
    private async void ToggleGameMode()
    {
        if (!GameModeButtonPresentation.CanToggle(IsGameModeToggling)) return;

        var entering = !IsGameModeOn;

        // 白名单同步必须在 UI 线程做：它读的是界面上的 ObservableCollection
        if (entering) ApplyWhitelistToGameMode();

        _gameModeEntering = entering;
        IsGameModeToggling = true;
        GameModePhase = GameModePhase.Working;
        GameModeStatus = GameModeButtonPresentation.WorkingStatus(entering);

        try
        {
            var outcome = await Task.Run(() => entering ? EnterGameMode() : ExitGameMode());

            IsGameModeOn = outcome.Active;
            GameModeStatus = outcome.Status;
            GameModePhase = outcome.Phase;
        }
        catch (Exception ex)
        {
            // 各动作内部异常已由 GameModeController 收集；走到这里说明是更外层的意外，
            // 同样不能沉默——按钮必须回到可点状态，并说清哪一步炸了。
            GameModePhase = GameModePhase.Failed;
            GameModeStatus = $"游戏模式：{(entering ? "开启" : "关闭")}时出现异常 — {ex.Message}";
        }
        finally
        {
            IsGameModeToggling = false;
        }

        // 结果落在仪表盘上：切换结束后用户能立刻看到内存/CPU 变化。
        // 采样失败不影响切换结论，所以单独兜住——不能让刷新异常冒泡成崩溃。
        try { Tick(); } catch { /* 采样异常已在 Tick 内记录，这里只防万一 */ }
    }

    /// <summary>一次切换的最终结果，回 UI 线程再灌进绑定属性。</summary>
    private sealed record GameModeOutcome(bool Active, string Status, GameModePhase Phase);

    /// <summary>在后台线程执行开启，返回值只含纯数据，不碰 UI。</summary>
    private GameModeOutcome EnterGameMode()
    {
        // 与 Shutdown（托盘退出）互斥：否则「点完开启立刻退出」会让两个线程同时改动作列表
        lock (_gameModeGate) return EnterGameModeCore();
    }

    private GameModeOutcome EnterGameModeCore()
    {
        var errors = _gameMode.Enter();
        if (errors.Count > 0)
            return new(false, "游戏模式：开启失败并已回滚 — " + string.Join("；", errors), GameModePhase.Failed);

        // 不能只说「已开启」：必须让用户看到结束了几个进程。
        var killed = _gameMode.Actions.OfType<EndProcessAction>().FirstOrDefault();
        var detail = killed is null ? "" : $"；{killed.Summary}";

        // 可选动作（内存清理等）没生效时同样要说出来：
        // 静默跳过会让用户以为那一项做了，属于本项目明确禁止的「假成功」。
        if (_gameMode.Warnings.Count > 0)
            detail += "；" + string.Join("；", _gameMode.Warnings);

        return new(_gameMode.IsActive,
            $"游戏模式：已开启{detail}；关闭时自动恢复这些进程",
            GameModePhase.On);
    }

    /// <summary>在后台线程执行关闭：按原路径恢复被结束的进程，失败项逐条上报。</summary>
    private GameModeOutcome ExitGameMode()
    {
        lock (_gameModeGate) return ExitGameModeCore();
    }

    private GameModeOutcome ExitGameModeCore()
    {
        var errors = _gameMode.ExitWithReport().ToList();

        var restored = 0;
        var action = _gameMode.Actions.OfType<EndProcessAction>().FirstOrDefault();
        if (action is not null)
        {
            restored = action.RestoredCount;
            errors.AddRange(action.FailedRestores.Select(r =>
                $"{ProcessDisplayNames.Resolve(r.Name)}恢复失败：{r.Error}"));
        }

        if (errors.Count > 0)
            return new(false, "⚠ 游戏模式已关闭，但有项未还原：" + string.Join("；", errors), GameModePhase.Failed);

        return new(false,
            restored > 0
                ? $"游戏模式：已关闭，已恢复 {restored} 个进程"
                : "游戏模式：已关闭，改动已还原",
            GameModePhase.Idle);
    }

    /// <summary>
    /// 界面是否对用户可见。关窗口收进托盘、最小化时都应传 false。
    ///
    /// 为什么必须停：本程序常驻托盘，用户关掉窗口后可能一整天不再打开。
    /// 采样器每秒要为每个逻辑核 + 每个 GPU 3D 引擎实例各读一次 PDH 计数器
    /// （24 核机器就是 24 次起步，GPU 实例多时上百次），没人看的时候这些读数全是白烧。
    /// </summary>
    public void SetSamplingActive(bool active)
    {
        if (!SamplerReady) return;

        if (active)
        {
            if (!_timer.IsEnabled) _timer.Start();
            // 立刻补一帧：否则恢复窗口后的第一秒显示的是离开时的旧数据
            Tick();
        }
        else
        {
            _timer.Stop();
            // 下载页的 500ms 刷新同样只在看得见时才有意义
            Downloads.SetActive(false);
        }
    }

    /// <summary>停止采集。关窗口时调用，保证游戏模式一定被还原。</summary>
    public void Shutdown()
    {        _timer.Stop();

        // 下载页停刷新并让服务落一次盘：不做的话「退出前最后几秒的进度」会丢
        Downloads.Shutdown();

        if (_gameMode.IsActive)
        {
            // 与后台的开启/关闭互斥：用户可能「点完开启立刻托盘退出」，
            // 不等那一轮跑完就会对着半套状态做还原。lock 会把退出拖到本轮结束，值得。
            lock (_gameModeGate)
            {
                if (!_gameMode.IsActive) return;

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

    /// <summary>游戏 exe 的图标；扫描不到 exe 路径时为 null，界面退化为首字符色块。</summary>
    public System.Windows.Media.ImageSource? Icon => AppIconCache.ForPath(entry.ExecutablePath);

    /// <summary>首字符兜底：游戏名第一个字符。</summary>
    public string IconFallbackText => (entry.Name.Length > 0 ? entry.Name[0] : '?').ToString();

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

/// <summary>筛选标签的一项。<see cref="Text"/> 直接显示在界面上。</summary>
public sealed record BackgroundFilterOption(BackgroundListFilter Value, string Text);

/// <summary>
/// 后台进程管理里的一行。
///
/// <b>语义（v2.4 第二次调整，全项目唯一）</b>：
/// <list type="bullet">
/// <item><see cref="IsProtected"/> 勾选 = <b>保护</b>，游戏模式绝不结束它；</item>
/// <item>没勾选的<b>默认就是结束目标</b>——用户只需挑出「不能动的那几个」；</item>
/// <item>是否真的会被结束仍以 <see cref="BackgroundProtectionPolicy.Decide"/> 为准：
///       系统关键、游戏本体、读不到路径的即使没勾保护也不会被结束。</item>
/// </list>
/// 判断依据全部来自 <see cref="BackgroundProtectionPolicy"/>，界面只负责显示，
/// 避免「界面说会结束、执行时却没结束」或反过来。
/// </summary>
public sealed class BackgroundProcessEntry : INotifyPropertyChanged
{
    private readonly BackgroundProcessInfo _info;
    private readonly bool _matchesGame;

    public BackgroundProcessEntry(BackgroundProcessInfo info, bool matchesGame = false)
    {
        _info = info;
        _matchesGame = matchesGame;

        // 必须把「已保存的保护状态」落到字段上：
        // 只靠 info.State 展示会让复选框显示为未勾选，而状态徽章又写着「受保护」，
        // 更糟的是用户再点一下就把保护取消了（界面与存储不一致）。
        _isProtected = info.State == ProtectionState.Protected;
    }

    public string ProcessName => _info.ProcessName;
    public string? ExecutablePath => _info.ExecutablePath;

    /// <summary>友好应用名；读不出任何身份信息时是「未知应用」。</summary>
    public string AppName => _info.AppName;

    /// <summary>与进程名不同时才有值，用于在应用名下面补一行「进程名：xxx.exe」。</summary>
    public string ProcessNameText => $"进程名：{_info.ExecutableName}";

    public string Purpose => _info.Purpose;
    public string PurposeBasis => _info.PurposeBasis;

    /// <summary>用途文字颜色：识别出来是灰的，认不出来用警示色提醒「这是未知」。</summary>
    public string PurposeHex => _info.PurposeKnown ? "#FF83868D" : "#FFFF9F0A";
    public string? Publisher => _info.Publisher;
    public string PublisherText => _info.Publisher is null ? "发布者：未知（产品信息里没有公司名）" : $"发布者：{_info.Publisher}";

    public string? FileDescription => _info.FileDescription;
    public string? ProductName => _info.ProductName;
    public string? FileVersion => _info.FileVersion;

    /// <summary>完整路径；读不到时明确写出来，界面不做留白。</summary>
    public string PathText => _info.ExecutablePath ?? "（读不到可执行文件路径）";
    public bool HasPath => _info.ExecutablePath is not null;

    public bool IsIdentityUnknown => _info.IsIdentityUnknown;
    public bool IsSystemCritical => _info.IsSystemEssential;
    public string? SystemReason => _info.SystemReason;
    public bool InPreset => _info.InPreset;

    /// <summary>状态徽章文案：受保护 / 未保护 / 系统关键进程 / 无法识别。</summary>
    public string StateText => BackgroundProtectionPolicy.Describe(State, IsIdentityUnknown);
    public string StateHex => BackgroundProtectionPolicy.BadgeHex(State);

    public ProtectionState State => BackgroundProtectionPolicy.StateOf(IsProtected, IsSystemCritical);

    /// <summary>系统关键进程的冻结理由；普通进程为空。</summary>
    public string ProtectionReasonText => _info.SystemReason ?? "";

    /// <summary>本行最终的判定结果：会结束 / 不结束，以及原因。界面显示与执行侧共用同一套判据。</summary>
    public KillVerdict Verdict => BackgroundProtectionPolicy.Decide(Facts);

    /// <summary>卡片右侧的结果标签：明确告诉用户「这个会不会被关」。</summary>
    public string KillBadgeText => Verdict.Eligible ? "会结束" : "不会结束";

    /// <summary>会结束用琥珀色（需要留意），不会结束用灰色。</summary>
    public string KillBadgeHex => Verdict.Eligible ? "#FFFF9F0A" : "#FF5C5F66";

    /// <summary>详情区一行「为什么这样判定」。直接取 Core 的解释文案，保证两处口径一致。</summary>
    public string DecisionText => Verdict.Explanation;

    /// <summary>搜索用的合并文本（应用名 / 进程名 / 路径 / 用途 / 发布者）。</summary>
    public string SearchText => BackgroundProcessFilter.SearchTextOf(_info);

    /// <summary>Core 侧的展示模型（筛选判据要用）。</summary>
    internal BackgroundProcessInfo Info => _info;

    public System.Windows.Media.ImageSource? Icon => AppIconCache.ForPath(_info.ExecutablePath);

    /// <summary>首字符色块兜底：显示名第一个字符。</summary>
    public string IconFallbackText => (AppName.Length > 0 ? AppName[0] : '?').ToString();

    /// <summary>展开详情（路径 / 文件描述 / 识别依据）。</summary>
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
        }
    }

    private bool _isProtected;
    /// <summary>勾选 = 保护。唯一的用户输入；取消勾选立即回到「默认会被结束」。</summary>
    public bool IsProtected
    {
        get => _isProtected;
        set
        {
            if (_isProtected == value) return;
            _isProtected = value;
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// 是否在结束名单里（派生值，不再单独存储）。
    /// 没勾保护、且通过系统关键 / 游戏 / 路径检查的，就是结束目标。
    /// </summary>
    public bool IsEndTarget => Verdict.Eligible;

    /// <summary>本行的客观事实，供 Core 判据使用。</summary>
    public BackgroundTargetFacts ToFacts(IReadOnlySet<string>? gameStems)
        => new(
            ProcessName,
            ExecutablePath,
            IsProtected,
            IsSystemCritical,
            IsIdentityUnknown,
            _matchesGame || BackgroundCandidateScan.MatchesGame(ProcessName, gameStems));

    /// <summary>当前行的判定结果（界面显示与执行侧共用同一套判据）。</summary>
    public BackgroundTargetFacts Facts
        => new(ProcessName, ExecutablePath, IsProtected, IsSystemCritical, IsIdentityUnknown, _matchesGame);

    private void RaiseStateChanged()
    {
        PropertyChanged?.Invoke(this, new(nameof(IsProtected)));
        PropertyChanged?.Invoke(this, new(nameof(IsEndTarget)));
        PropertyChanged?.Invoke(this, new(nameof(Verdict)));
        PropertyChanged?.Invoke(this, new(nameof(KillBadgeText)));
        PropertyChanged?.Invoke(this, new(nameof(KillBadgeHex)));
        PropertyChanged?.Invoke(this, new(nameof(State)));
        PropertyChanged?.Invoke(this, new(nameof(StateText)));
        PropertyChanged?.Invoke(this, new(nameof(StateHex)));
        PropertyChanged?.Invoke(this, new(nameof(DecisionText)));
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

/// <summary>CPU 快速优化流程的阶段。只影响状态行颜色与文案，不影响任何系统状态。</summary>
public enum CpuOptimizePhase
{
    /// <summary>尚未检测（或没有可执行项）。</summary>
    Idle = 0,

    /// <summary>已生成建议，等待用户勾选确认。</summary>
    Ready = 1,

    /// <summary>正在执行。</summary>
    Running = 2,

    /// <summary>执行完成并已回读。</summary>
    Done = 3,

    /// <summary>执行或恢复失败。</summary>
    Failed = 4,
}

/// <summary>
/// 高占用进程列表项。数字每秒原地更新，避免整表重建造成闪烁。
///
/// 「受保护」不是建议，而是硬约束：这类进程界面不提供结束入口，
/// 也不进 <see cref="CpuOptimizePlanner"/> 的候选。
/// </summary>
public sealed class ProcessCpuItem(ProcessCpuUsage usage) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public int ProcessId { get; } = usage.ProcessId;
    public string Name { get; } = usage.Name;

    /// <summary>看得懂的应用名；没有映射时退回进程名。</summary>
    public string DisplayName { get; } = ProcessDisplayNames.Resolve(usage.Name);

    private double _percent = usage.CpuPercent;
    private bool _isProtected = usage.IsProtected;
    private string? _reason = usage.ProtectionReason;

    public string PercentText => $"{_percent:F1}%";
    public string ColorHex => OccupancyPalette.ColorHex(OccupancyPalette.Level(_percent));

    public string StatusText => _isProtected
        ? $"不提供结束操作：{_reason}"
        : "可在「CPU 优化」里确认后结束";

    public string StatusHex => _isProtected ? "#FF5C5F66" : "#FFFF9F0A";

    /// <summary>进程名与显示名不同时才附注，避免重复显示。</summary>
    public string NameHint => ProcessDisplayNames.HasName(Name) ? Name : "";

    public void Update(ProcessCpuUsage next)
    {
        if (Math.Abs(next.CpuPercent - _percent) >= 0.05)
        {
            _percent = next.CpuPercent;
            PropertyChanged?.Invoke(this, new(nameof(PercentText)));
            PropertyChanged?.Invoke(this, new(nameof(ColorHex)));
        }

        if (next.IsProtected == _isProtected && next.ProtectionReason == _reason) return;

        _isProtected = next.IsProtected;
        _reason = next.ProtectionReason;
        PropertyChanged?.Invoke(this, new(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new(nameof(StatusHex)));
    }
}

/// <summary>
/// 一条待确认的优化项。**默认不勾选**——结束进程属于破坏性操作，
/// 必须由用户逐项主动选择（v0.5.1 的「默认全选」教训）。
/// </summary>
public sealed class CpuOptimizeItemView(CpuOptimizeItem item) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => item.Title;
    public string ProcessName => item.ProcessName ?? "";
    public string Evidence => item.Evidence;
    public string Impact => item.Impact;
    public string RevertText => item.RevertText;

    private bool _selected;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new(nameof(Selected)));
        }
    }
}
