namespace GameOptimizer.Core.Collection;

using System.Diagnostics;

/// <summary>
/// 一次系统压力采样。
///
/// 只保留两项——其余候选指标实测价值不足，理由见各字段注释与 docs/04-已知限制.md。
/// </summary>
public sealed record SystemPressureFrame(
    long TimestampMs,
    double DiskBytesPerSec,
    double TotalCpuPercent);

/// <summary>吞吐量的可读格式化。纯函数，界面与测试共用同一口径。</summary>
public static class ThroughputText
{
    private const double Kb = 1024.0;
    private const double Mb = 1024.0 * 1024.0;
    private const double Gb = 1024.0 * 1024.0 * 1024.0;

    /// <summary>
    /// 字节/秒 → 「1.2 GB/s」/「480 MB/s」/「12 KB/s」/「0 B/s」。
    /// GB 保留一位小数，MB 取整避免假精度。
    /// </summary>
    public static string Format(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || double.IsInfinity(bytesPerSec) || bytesPerSec <= 0) return "0 B/s";
        if (bytesPerSec >= Gb) return $"{bytesPerSec / Gb:F1} GB/s";
        if (bytesPerSec >= Mb) return $"{bytesPerSec / Mb:F0} MB/s";
        if (bytesPerSec >= Kb) return $"{bytesPerSec / Kb:F0} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }
}

/// <summary>
/// 系统压力采集：磁盘吞吐 + 总 CPU 占用。
///
/// **为什么只有两项**（这是实测筛掉的结果，不是没做完）：
///
/// · **总 CPU**：现有 <see cref="PdhSampler"/> 只有「单核峰值」。单核 100% 可能只是
///   某个后台线程在忙，看不出整机是否被吃满。总量补的正是这个盲区。实测有效。
///
/// · **磁盘吞吐**：回答「贴图流式加载卡不卡在磁盘」。实测空闲 0.16 MB/s，
///   一次 200MB 写入时 10.95 MB/s，差 68 倍，信号清晰。
///
/// 被筛掉的（都实测过，记录在 docs/04-已知限制.md）：
///
/// · **处理器队列长度**：`\System\Processor Queue Length` 只统计就绪队列里等 CPU 的线程。
///   实测 24 核机器上跑 20 个满负荷进程、总 CPU 到 79%，队列仍只有 0~2。
///   它只在「少数几个核被抢占」时有信号，绝大多数时候是 0，误读风险远大于价值。
///
/// · **分页文件使用率**：静态指标。本机实测 `% Usage` 25%，同期
///   `\Memory\Pages/sec` 为 0——说明完全没在换页，那个 25% 是历史占用。
///   单看它会把「没压力」读成「内存吃紧」。要它有意义必须配 Pages/sec 一起看，
///   而真要看换页，直接看 Pages/sec 就够了，不必绕道分页文件。
///
/// · **网络吞吐**：能采到（实测一次 HTTP 请求 18 KB/s），但在线游戏的卡顿来自**延迟**，
///   不是吞吐。看吞吐回答不了「为什么延迟高」，价值与成本不匹配。
///
/// **刻意不采温度**：实测 `\Thermal Zone Information` 只能给出 27.9°C 且完全不随负载
/// 变化的 ACPI 环境温度，不是芯片结温。真实核心温度要读 MSR / EC，需内核驱动，
/// 与本项目「不装驱动」的原则冲突。见 docs/04-已知限制.md。
/// </summary>
public sealed class SystemPressureSampler : IDisposable
{
    private readonly List<PerformanceCounter> _disks = [];
    private PerformanceCounter? _totalCpu;
    private bool _warmed;

    public bool IsReady { get; private set; }

    /// <summary>初始化失败的原因。界面据此说明「采不到」，而不是显示 0 冒充读数。</summary>
    public string? LastError { get; private set; }

    /// <summary>实际采到的项目，供界面如实标注哪几项可用。</summary>
    public IReadOnlyList<string> AvailableMetrics => _available;
    private readonly List<string> _available = [];

    public bool Initialize()
    {
        try
        {
            // 两项独立降级：某一个计数器缺失时另一项照常工作
            //
            // 类别名必须是 "PhysicalDisk"（无空格），不是 "Physical Disk"。
            // 实测：Get-Counter 的路径写成 \PhysicalDisk(_Total)\ 能读到数，
            // 但 PerformanceCounterCategory("Physical Disk") 会抛
            // "Category 'Physical Disk' does not exist." ——两者对空格的处理不同。
            // 写错时磁盘项会静默降级为不可用，界面只显示「0 B/s」，极难发现。
            foreach (var inst in SafeInstances(DiskCategory))
            {
                if (inst == "_Total") continue; // 分盘求和，避免 _Total 与明细重复计数
                var c = TryCreate(DiskCategory, "Disk Bytes/sec", inst);
                if (c is not null) _disks.Add(c);
            }

            _totalCpu = TryCreate("Processor", "% Processor Time", "_Total");

            if (_disks.Count > 0) _available.Add("磁盘吞吐");
            if (_totalCpu is not null) _available.Add("总 CPU");

            IsReady = _available.Count > 0;
            if (!IsReady) LastError = "本机没有可用的系统压力计数器";

            return IsReady;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsReady = false;
            return false;
        }
    }

    /// <summary>
    /// 磁盘计数器类别名。**无空格**，与 PDH 路径 \PhysicalDisk(...) 一致。
    /// 写成 "Physical Disk" 会让 <see cref="PerformanceCounterCategory"/> 找不到类别。
    /// </summary>
    internal const string DiskCategory = "PhysicalDisk";

    /// <summary>首次读数恒为 0，必须预热后再采。</summary>
    public void Warmup(int delayMs = 300)
    {
        if (!IsReady) return;

        foreach (var c in AllCounters()) SafeRead(c);
        Thread.Sleep(delayMs);
        _warmed = true;
    }

    /// <summary>采一帧。未初始化或未预热时抛异常，不静默返回 0。</summary>
    public SystemPressureFrame Sample(long timestampMs)
    {
        if (!IsReady) throw new InvalidOperationException($"系统压力采集器未就绪：{LastError}");
        if (!_warmed) throw new InvalidOperationException("必须先调用 Warmup()，否则首次读数恒为 0。");

        // 各物理盘吞吐求和：用户关心的是「整机磁盘有多忙」
        var disk = _disks.Sum(SafeRead);

        return new SystemPressureFrame(
            timestampMs,
            DiskBytesPerSec: NonNegative(disk),
            TotalCpuPercent: PdhSampler.ClampPercent(SafeRead(_totalCpu)));
    }

    /// <summary>
    /// 吞吐量不可能为负；PDH 首次读数或计数器回绕会给出负数，显示成 -3 MB/s 会像 bug。
    /// </summary>
    internal static double NonNegative(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Max(0, value);

    private IEnumerable<PerformanceCounter> AllCounters()
    {
        foreach (var c in _disks) yield return c;
        if (_totalCpu is not null) yield return _totalCpu;
    }

    /// <summary>计数器创建失败返回 null：某一项不存在不该让另一项也不可用。</summary>
    private static PerformanceCounter? TryCreate(string category, string counter, string instance)
    {
        try { return new PerformanceCounter(category, counter, instance, readOnly: true); }
        catch { return null; }
    }

    private static string[] SafeInstances(string category)
    {
        try { return new PerformanceCounterCategory(category).GetInstanceNames(); }
        catch { return []; }
    }

    private static double SafeRead(PerformanceCounter? c)
    {
        if (c is null) return 0;
        try { return c.NextValue(); } catch { return 0; }
    }

    public void Dispose()
    {
        foreach (var c in AllCounters()) c.Dispose();
        _disks.Clear();
    }
}
