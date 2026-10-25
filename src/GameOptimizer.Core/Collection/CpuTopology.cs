namespace GameOptimizer.Core.Collection;

using System.Runtime.InteropServices;

/// <summary>
/// 一个逻辑处理器的拓扑信息。索引与 PDH 的 `Processor Information` 实例名（`组,逻辑核`）对应。
/// </summary>
/// <param name="Group">处理器组，单路机器恒为 0。</param>
/// <param name="Index">组内逻辑处理器序号。</param>
/// <param name="CoreIndex">物理核序号：同一物理核的两个超线程共享它。</param>
/// <param name="EfficiencyClass">
/// 效率等级。**数值越大越强**：Intel 12 代起的混合架构里，
/// 性能核（P-core）为 1，能效核（E-core）为 0；全同构机器上所有核都是 1。
/// </param>
public sealed record LogicalProcessor(int Group, int Index, int CoreIndex, int EfficiencyClass)
{
    /// <summary>PDH `Processor Information` 的实例名，形如 "0,3"。</summary>
    public string InstanceName => $"{Group},{Index}";

    /// <summary>是不是性能核。全同构机器（无混合架构）上恒为 true。</summary>
    public bool IsPerformanceCore { get; init; } = true;
}

/// <summary>
/// CPU 拓扑：哪些逻辑处理器是性能核，哪些是能效核。
///
/// 为什么这件事对游戏监控重要：实测本机（i7-1360P，4 大核 8 线程 + 8 小核）上，
/// 把 8 个后台进程绑到 E 核满载时，**全局单核峰值显示 98.6%（红色报警），
/// 而跑游戏的 P 核最高只有 29.3%**——玩家会白白去降画质。
/// 反过来把负载绑到 P 核，P 核峰值 93.1%，这才是真正影响帧生成的信号。
///
/// 所以界面上的「CPU 峰值」必须取 **P 核峰值**，不能取全局最大值。
/// </summary>
public sealed class CpuTopology
{
    /// <summary>全部逻辑处理器。</summary>
    public IReadOnlyList<LogicalProcessor> Processors { get; }

    private CpuTopology(IReadOnlyList<LogicalProcessor> processors) => Processors = processors;

    /// <summary>是否存在混合架构（既有性能核又有能效核）。</summary>
    public bool IsHybrid => Processors.Select(p => p.IsPerformanceCore).Distinct().Count() > 1;

    /// <summary>性能核（含其超线程）。</summary>
    public IReadOnlyList<LogicalProcessor> PerformanceCores
        => Processors.Where(p => p.IsPerformanceCore).ToList();

    /// <summary>能效核。</summary>
    public IReadOnlyList<LogicalProcessor> EfficiencyCores
        => Processors.Where(p => !p.IsPerformanceCore).ToList();

    /// <summary>性能核数量（逻辑处理器口径）。</summary>
    public int PerformanceProcessorCount => PerformanceCores.Count;

    /// <summary>能效核数量（逻辑处理器口径）。</summary>
    public int EfficiencyProcessorCount => EfficiencyCores.Count;

    /// <summary>
    /// 物理核数量：同一物理核上的两个超线程共享 <see cref="LogicalProcessor.CoreIndex"/>，
    /// 所以去重后才是「几个核」。高级详情页的「核心 / 线程」用它。
    /// </summary>
    public int PhysicalCoreCount => Processors.Select(p => p.CoreIndex).Distinct().Count();

    /// <summary>
    /// 从「逻辑核序号 → 占用率」里取性能核峰值。
    ///
    /// 找不到任何性能核时（拓扑读不到 / 全同构机器的兜底）退化为全局峰值，
    /// 绝不返回 0——返回 0 会让界面显示「CPU 空闲」而实际可能满载。
    /// </summary>
    public double PerformancePeak(IReadOnlyDictionary<int, double> usageByProcessor)
    {
        if (usageByProcessor.Count == 0) return 0;

        var perf = PerformanceCores;
        if (perf.Count == 0) return usageByProcessor.Values.Max();

        double max = 0;
        var found = false;
        foreach (var p in perf)
        {
            if (!usageByProcessor.TryGetValue(p.Index, out var v)) continue;
            found = true;
            if (v > max) max = v;
        }

        return found ? max : usageByProcessor.Values.Max();
    }

    /// <summary>
    /// 从「逻辑核序号 → 占用率」里取能效核峰值。没有能效核时返回 0（不是全局峰值——
    /// 此时 E 核这个概念本身不存在）。
    /// </summary>
    public double EfficiencyPeak(IReadOnlyDictionary<int, double> usageByProcessor)
    {
        double max = 0;
        foreach (var p in EfficiencyCores)
        {
            if (!usageByProcessor.TryGetValue(p.Index, out var v)) continue;
            if (v > max) max = v;
        }
        return max;
    }

    /// <summary>
    /// 构造一个「全是性能核」的拓扑，用于读不到硬件拓扑时的兜底。
    /// 此时 P 核峰值 == 全局峰值，行为与本功能引入前一致。
    /// </summary>
    public static CpuTopology Uniform(int logicalProcessorCount)
    {
        var list = Enumerable.Range(0, Math.Max(1, logicalProcessorCount))
            .Select(i => new LogicalProcessor(0, i, i, EfficiencyClass: 1))
            .ToList();
        return new CpuTopology(list);
    }

    /// <summary>用给定的逻辑处理器列表构造拓扑。测试用，也可供以后从配置注入。</summary>
    public static CpuTopology FromProcessors(IReadOnlyList<LogicalProcessor> processors)
        => new(processors);

    /// <summary>
    /// 读本机真实拓扑。读不到（旧系统 / 调用失败）时退化为 <see cref="Uniform"/>，
    /// 保证界面永远有数可用，只是失去大小核区分能力。
    /// </summary>
    public static CpuTopology Detect()
    {
        try
        {
            var raw = CpuSetNative.Query();
            if (raw.Count > 0)
            {
                // 效率等级取本机出现过的最大值作为「性能核」基准：
                // 全同构机器所有核相同，全部判为性能核，行为与优化前一致。
                var maxClass = raw.Max(r => r.EfficiencyClass);
                var list = raw
                    .Select(r => new LogicalProcessor(r.Group, r.Index, r.CoreIndex, r.EfficiencyClass)
                    {
                        IsPerformanceCore = r.EfficiencyClass >= maxClass,
                    })
                    .OrderBy(p => p.Index)
                    .ToList();
                return new CpuTopology(list);
            }
        }
        catch
        {
            // 读不到拓扑不该让整个监控失效
        }

        return Uniform(Environment.ProcessorCount);
    }

    /// <summary>P / E 核的可读描述，例如「4 大核 8 线程 · 8 小核」。供界面标注口径。</summary>
    public string Describe()
    {
        if (!IsHybrid) return $"{Processors.Count} 线程（同构）";

        var pPhysical = PerformanceCores.Select(p => p.CoreIndex).Distinct().Count();
        var ePhysical = EfficiencyCores.Select(p => p.CoreIndex).Distinct().Count();
        return $"{pPhysical} 大核 {PerformanceProcessorCount} 线程 · {ePhysical} 小核";
    }
}

/// <summary>P/Invoke `GetSystemCpuSetInformation`，拿效率等级以区分大小核。</summary>
internal static class CpuSetNative
{
    /// <summary>`SYSTEM_CPU_SET_INFORMATION` 的 Type 字段：0 = CpuSetInformation。</summary>
    private const int CpuSetInformation = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemCpuSetInformation
    {
        public uint Size;
        public int Type;
        public uint Id;
        public ushort Group;
        public byte LogicalProcessorIndex;
        public byte CoreIndex;
        public byte LastLevelCacheIndex;
        public byte NumaNodeIndex;
        public byte EfficiencyClass;
        public byte AllFlags;
        public uint Reserved;
        public ulong AllocationTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information, uint informationLength, out uint returnedLength, IntPtr process, uint flags);

    internal sealed record RawProcessor(int Group, int Index, int CoreIndex, int EfficiencyClass);

    /// <summary>枚举逻辑处理器。失败返回空列表，由调用方兜底。</summary>
    internal static IReadOnlyList<RawProcessor> Query()
    {
        var result = new List<RawProcessor>();

        // 第一次用空缓冲探测所需长度。注意这次调用**必然返回 false**
        // （ERROR_INSUFFICIENT_BUFFER = 122），所以不能拿返回值当失败判据，
        // 只能看它有没有吐出长度——踩过这个坑：按返回值判断会直接返回空列表，
        // 于是本机的 8 P 核 / 8 E 核被误判成「16 线程同构」，功能静默失效。
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out var needed, IntPtr.Zero, 0);
        if (needed == 0) return result;

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, needed, out var returned, IntPtr.Zero, 0))
                return result;

            var size = Marshal.SizeOf<SystemCpuSetInformation>();
            var offset = 0;
            while (offset + size <= returned)
            {
                var entry = Marshal.PtrToStructure<SystemCpuSetInformation>(IntPtr.Add(buffer, offset));
                if (entry.Size == 0) break; // 防御：结构体长度异常时停止，避免死循环

                if (entry.Type == CpuSetInformation)
                {
                    result.Add(new RawProcessor(entry.Group, entry.LogicalProcessorIndex,
                        entry.CoreIndex, entry.EfficiencyClass));
                }

                offset += (int)entry.Size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }
}
