namespace GameOptimizer.Core.Collection;

using System.Runtime.InteropServices;

/// <summary>系统物理内存总量。核显的显存来自内存，判显存时必须知道总量。</summary>
public static class SystemMemoryInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX stat);

    private const double BytesPerMb = 1024.0 * 1024.0;

    /// <summary>物理内存总量（MB）。取不到时返回 0。</summary>
    public static double TotalPhysicalMb { get; } = Query();

    /// <summary>
    /// 标称容量（GB）。实测 16GB 内存条的系统上报 15.73GB，
    /// 差值是固件/核显预留。用户心智里的数字是 16，所以向上取整到常见规格。
    /// </summary>
    public static int NominalGb => NominalFrom(TotalPhysicalMb);

    /// <summary>把实际可用总量归一化到标称容量（8/16/32/64...）。</summary>
    internal static int NominalFrom(double totalMb)
    {
        if (totalMb <= 0) return 0;

        var actualGb = totalMb / 1024.0;
        // 固件预留通常在 3% 以内，向上取到最近的「标准容量」
        foreach (var candidate in new[] { 4, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256 })
        {
            if (actualGb <= candidate) return candidate;
        }
        return (int)Math.Ceiling(actualGb);
    }

    private static double Query()
    {
        try
        {
            var stat = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref stat)) return 0;
            return stat.ullTotalPhys / BytesPerMb;
        }
        catch
        {
            return 0;
        }
    }
}
