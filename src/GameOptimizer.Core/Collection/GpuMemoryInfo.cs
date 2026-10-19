namespace GameOptimizer.Core.Collection;

using System.Runtime.InteropServices;

/// <summary>
/// 显卡显存容量。走 DXGI 的 IDXGIAdapter1::GetDesc1，拿到的是驱动上报的
/// DedicatedVideoMemory 与 SharedSystemMemory，而不是猜一个值。
///
/// 为什么不再用「物理内存的一半」：那个估算对核显勉强能用，对独显完全失真——
/// 8GB 显卡 + 32GB 内存会被算成 16GB 预算，于是任何游戏都显示「显存充足」。
/// </summary>
public static class GpuMemoryInfo
{
    private const double BytesPerMb = 1024.0 * 1024.0;

    /// <summary>小于这个值视为核显（Intel 核显通常只报 128MB 专用）。</summary>
    private const double DiscreteThresholdMb = 2048;

    public sealed record Snapshot(string Name, double DedicatedVideoMb, double SharedSystemMb)
    {
        /// <summary>独显只看专用显存；核显的显存来自内存，要算上共享额度。</summary>
        public bool IsDiscrete => DedicatedVideoMb >= DiscreteThresholdMb;

        /// <summary>这块显卡实际能用的显存预算（MB）。取不到时返回 0。</summary>
        public double UsableBudgetMb => IsDiscrete
            ? DedicatedVideoMb
            : DedicatedVideoMb + SharedSystemMb;
    }

    /// <summary>本次进程内的探测结果，探测失败为 null（调用方据此降级，不要假装读到了）。</summary>
    public static Snapshot? Current { get; } = Query();

    private static Snapshot? Query()
    {
        try
        {
            return Probe() ?? ProbeViaRegistry();
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long Luid;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumAdapters();
        [PreserveSig] int MakeWindowAssociation();
        [PreserveSig] int GetWindowAssociation();
        [PreserveSig] int CreateSwapChain();
        [PreserveSig] int CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumOutputs();
        [PreserveSig] int GetDesc();
        [PreserveSig] int CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    /// <summary>
    /// 枚举适配器，挑「专用显存最大且不是软件适配器」的那块。
    /// 软件适配器（Microsoft Basic Render Driver）的 Flags 含 2（SOFTWARE），必须排除，
    /// 否则在虚拟机/远程桌面里会读到 0MB 并据此报「显存不足」。
    /// </summary>
    private static Snapshot? Probe()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref iid, out var raw) != 0) return null;

        var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(raw);
        try
        {
            AdapterDesc1? best = null;
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter) != 0) break;

                try
                {
                    if (adapter.GetDesc1(out var desc) != 0) continue;
                    if ((desc.Flags & 2) != 0) continue;          // 软件适配器
                    if ((desc.Flags & 1) != 0) continue;          // 远程桌面推断适配器
                    if (string.IsNullOrWhiteSpace(desc.Description)) continue;

                    if (best is null || Total(best.Value) < Total(desc)) best = desc;
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }

            if (best is null) return null;

            var s = best.Value;
            return new Snapshot(
                s.Description,
                s.DedicatedVideoMemory.ToUInt64() / BytesPerMb,
                s.SharedSystemMemory.ToUInt64() / BytesPerMb);
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static ulong Total(AdapterDesc1 d)
        => d.DedicatedVideoMemory.ToUInt64() + d.SharedSystemMemory.ToUInt64();

    /// <summary>
    /// DXGI 拿不到时的退路：显卡驱动在注册表里写了 qwMemorySize（字节）。
    /// 只有少数驱动会写，读不到就返回 null，绝不编一个数出来。
    /// </summary>
    private static Snapshot? ProbeViaRegistry()
    {
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(classKey);
        if (root is null) return null;

        foreach (var sub in root.GetSubKeyNames())
        {
            if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;

            using var key = root.OpenSubKey(sub);
            if (key?.GetValue("HardwareInformation.qwMemorySize") is not long bytes || bytes <= 0) continue;

            var mb = bytes / BytesPerMb;
            if (mb < DiscreteThresholdMb) continue;   // 注册表里的小值不可信

            var desc = key.GetValue("DriverDesc") as string ?? "显卡";
            return new Snapshot(desc, mb, 0);
        }

        return null;
    }
}
