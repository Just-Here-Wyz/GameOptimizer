namespace GameOptimizer.Core.Collection;

using System.Diagnostics;
using GameOptimizer.Core.Models;

/// <summary>
/// PDH 采集器：显存（按 LUID 枚举真实适配器）、GPU 3D 利用率、CPU 单核峰值、可用内存。
/// 全部走 WDDM 标准计数器，不注入游戏进程、不需要管理员权限。
/// 注意：实例名与 LUID 在本机实测为运行时枚举得到，不能写死。
/// </summary>
public sealed class PdhSampler : IDisposable
{
    /// <summary>判断"这块显卡是不是核显"：专用显存为 0 即视为核显。</summary>
    private readonly List<PerformanceCounter> _gpuEngines = [];
    private readonly List<PerformanceCounter> _cpuCores = [];
    private readonly List<PerformanceCounter> _adapterCommitted = [];
    private PerformanceCounter? _availableRam;
    private PerformanceCounter? _sharedUsage;
    /// <summary>主适配器的 Total Committed 计数器，与 <see cref="_sharedUsage"/> 必须是同一块显卡。</summary>
    private PerformanceCounter? _primaryCommitted;
    private bool _warmed;

    public bool IsReady { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>主适配器的 LUID，用于把进程级计数器对上号。</summary>
    public string? PrimaryLuid { get; private set; }

    public bool Initialize()
    {
        try
        {
            _availableRam = new PerformanceCounter("Memory", "Available MBytes", true);

            // 显存：按适配器实例枚举。必须让 Total Committed 与 Shared Usage 指向同一块显卡，
            // 否则会出现"占用 1553MB 却显示共享 12MB"这种自相矛盾的读数。
            var adapterCat = new PerformanceCounterCategory("GPU Adapter Memory");
            var adapterInstances = adapterCat.GetInstanceNames();

            string? primary = null;
            double bestCommitted = -1;
            PerformanceCounter? primaryCounter = null;
            var probes = new List<(string Name, double Committed)>();
            foreach (var inst in adapterInstances)
            {
                var c = new PerformanceCounter("GPU Adapter Memory", "Total Committed", inst, true);
                var probe = SafeRead(c);
                probes.Add((inst, probe));
                if (probe > bestCommitted)
                {
                    bestCommitted = probe;
                    primary = inst;
                    primaryCounter = c;
                }
                _adapterCommitted.Add(c);
            }
            _primaryCommitted = primaryCounter;
            PrimaryLuid = SelectPrimaryAdapter(probes) ?? adapterInstances.FirstOrDefault();

            if (adapterCat.CounterExists("Shared Usage") && PrimaryLuid is not null)
                _sharedUsage = new PerformanceCounter("GPU Adapter Memory", "Shared Usage", PrimaryLuid, true);

            // GPU 3D：实例名形如 pid_x_luid_y_phys_z_eng_n_engtype_3D
            var engineCat = new PerformanceCounterCategory("GPU Engine");
            foreach (var inst in engineCat.GetInstanceNames().Where(i => i.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)))
                _gpuEngines.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true));

            // CPU 单核：实例名形如 "0,0"
            var procCat = new PerformanceCounterCategory("Processor Information");
            foreach (var inst in procCat.GetInstanceNames().Where(i => i.Contains(',') && !i.Contains('_')))
                _cpuCores.Add(new PerformanceCounter("Processor Information", "% Processor Time", inst, true));

            IsReady = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsReady = false;
            return false;
        }
    }

    /// <summary>首次读数恒为 0，必须预热后再采。</summary>
    public void Warmup(int delayMs = 300)
    {
        if (!IsReady) return;
        foreach (var c in _gpuEngines.Concat(_cpuCores).Append(_availableRam!).Where(c => c is not null))
        {
            try { c!.NextValue(); } catch { }
        }
        Thread.Sleep(delayMs);
        _warmed = true;
    }

    /// <summary>采一帧。帧时间采集（PresentMon）已随「数据」页一并移除，这里只采系统指标。</summary>
    public SampleFrame Sample(long timestampMs)
    {
        if (!IsReady)
            throw new InvalidOperationException($"采集器未就绪：{LastError}");
        if (!_warmed)
            throw new InvalidOperationException("必须先调用 Warmup()，否则首次读数恒为 0。");

        double gpuMax = 0;
        foreach (var c in _gpuEngines)
        {
            try { var v = c.NextValue(); if (v > gpuMax) gpuMax = v; }
            catch { /* 进程退出后实例消失，忽略 */ }
        }

        double coreMax = 0;
        foreach (var c in _cpuCores)
        {
            try { var v = c.NextValue(); if (v > coreMax) coreMax = v; }
            catch { }
        }

        return new SampleFrame(
            TimestampMs: timestampMs,
            DedicatedUsedMb: 0, // 核显恒为 0；独显场景由预算判定覆盖
            // GPU 计数器单位是「字节」，必须换算；Memory\Available MBytes 本身就是 MB，切勿再除
            SharedUsedMb: SafeRead(_sharedUsage) / BytesPerMb,
            CommittedMb: SafeRead(_primaryCommitted) / BytesPerMb,
            AvailableRamMb: SafeRead(_availableRam),
            // PDH 在两次读取间隔异常时会算出超过 100 的百分比（实测见过 123%），
            // 利用率语义上不可能越界，必须钳制，否则界面上会出现荒谬数字
            Gpu3DPercent: ClampPercent(gpuMax),
            MaxCorePercent: ClampPercent(coreMax));
    }

    /// <summary>把百分比钳制到 [0,100]，并丢弃非有限值。</summary>
    internal static double ClampPercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        return Math.Clamp(value, 0, 100);
    }

    private const double BytesPerMb = 1024.0 * 1024.0;

    /// <summary>
    /// 从各适配器的占用里选出主显卡：取占用最大者。
    /// 不能用 FirstOrDefault —— 枚举顺序把虚拟显示器/远程桌面适配器排在前面，
    /// 实测会导致"显存显示 12MB 而真实值 1553MB"。
    /// </summary>
    internal static string? SelectPrimaryAdapter(IReadOnlyList<(string Name, double Committed)> adapters)
    {
        if (adapters.Count == 0) return null;

        string? best = null;
        var bestValue = double.NegativeInfinity;
        foreach (var (name, committed) in adapters)
        {
            if (committed > bestValue)
            {
                bestValue = committed;
                best = name;
            }
        }
        return best;
    }

    private static double SafeRead(PerformanceCounter? c)
    {
        if (c is null) return 0;
        try { return c.NextValue(); } catch { return 0; }
    }

    public void Dispose()
    {
        foreach (var c in _gpuEngines) c.Dispose();
        foreach (var c in _cpuCores) c.Dispose();
        foreach (var c in _adapterCommitted) c.Dispose();
        _availableRam?.Dispose();
        _sharedUsage?.Dispose();
        _primaryCommitted?.Dispose();
    }
}
