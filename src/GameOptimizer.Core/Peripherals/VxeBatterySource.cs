namespace GameOptimizer.Core.Peripherals;

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

/// <summary>
/// VXE/ATK（Compx）鼠标电量读取。通信格式由分析厂商网页驱动（hub.atk.pro，前端 WebHID）
/// 与设备之间的公开报文得到，组包与解析为独立实现，未复制厂商任何代码。
/// 本机 VXE R1 实测：输出 08 04 00..49 → 输入 08 04 00 00 00 02 41 00 0F 7F..78（电量 0x41=65%）。
/// </summary>
public sealed class VxeBatterySource
{
    private const uint GetBatteryLevel = 4;

    private const uint GenericReadWrite = 0xC0000000;
    private const uint FileShareReadWrite = 3;
    private const uint OpenExisting = 3;

    /// <summary>必须带 overlapped，否则 ReadFile 会同步阻塞到设备应答为止（可能永远）。</summary>
    private const uint FileFlagOverlapped = 0x40000000;

    private const int ErrorIoPending = 997;
    private const uint WaitObject0 = 0;

    private const int WriteTimeoutMs = 400;
    private const int ReadTimeoutMs = 600;

    /// <summary>厂商固件实测 250ms 左右回包；先等一小段，剩下的时间交给带超时的读。</summary>
    private const int AnswerDelayMs = 120;

    public static int? ReadBatteryPercent() => Query()?.Level;

    public static bool? ReadChargingStatus() => Query()?.Charging;

    /// <summary>SetOutputReport 让设备进入应答状态，WriteFile 触发查询，ReadFile 取 17 字节响应。</summary>
    private static (int? Level, bool? Charging)? Query()
        => FirstAnswering(FindCompxPaths(), QueryPathWithTimeout);

    /// <summary>
    /// 按顺序尝试每个候选接口，返回第一个给出结果的。
    ///
    /// 抽成纯函数（接受探针委托）是为了能单测「第一台打不开」这条路径——
    /// 那段逻辑依赖真实 HID 设备，不抽出来就只能靠实机碰运气。
    /// </summary>
    internal static T? FirstAnswering<T>(IReadOnlyList<string> paths, Func<string, T?> probe) where T : struct
    {
        foreach (var path in paths)
        {
            var result = probe(path);
            if (result is not null) return result;
        }

        return null;
    }

    /// <summary>对单个接口路径做一次查询；设备不应答时返回 null，交给下一个候选。</summary>
    /// <remarks>
    /// 必须用 overlapped（异步）句柄 + 显式超时读。
    ///
    /// 踩过的坑：原来用同步句柄直接 <c>ReadFile</c>，设备不该答时这个调用**永远不返回**——
    /// 而它是在 UI 线程上（外设页扫描 → 构造函数）跑的，等于整个程序卡死。
    /// 表现是：--render 离屏渲染无输出无产物、dotnet test 跑完用例后进程退不出去。
    /// </remarks>
    private static (int? Level, bool? Charging)? QueryPathWithTimeout(string path)
    {
        var handle = CreateFile(
            path, GenericReadWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;

        var evt = CreateEvent(IntPtr.Zero, true, false, null);
        if (evt == IntPtr.Zero)
        {
            CloseHandle(handle);
            return null;
        }

        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedBuffer>());

        try
        {
            var output = BuildQuery((byte)GetBatteryLevel);

            if (!HidD_SetOutputReport(handle, output, (uint)output.Length)) return null;

            if (!WriteAll(handle, evt, overlapped, output, WriteTimeoutMs)) return null;
            Thread.Sleep(AnswerDelayMs);

            var input = new byte[17];
            if (!ReadExact(handle, evt, overlapped, input, ReadTimeoutMs)) return null;

            return ParseResponse(input);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(overlapped);
            CloseHandle(evt);
            CloseHandle(handle);
        }
    }

    /// <summary>发一包输出报文；超时或出错返回 false，绝不无限等。</summary>
    private static bool WriteAll(IntPtr handle, IntPtr evt, IntPtr overlapped, byte[] buffer, int timeoutMs)
    {
        ResetEvent(evt);
        Marshal.StructureToPtr(new OverlappedBuffer { Event = evt }, overlapped, false);

        if (WriteFile(handle, buffer, (uint)buffer.Length, out var written, overlapped))
            return written == buffer.Length;

        if (Marshal.GetLastWin32Error() != ErrorIoPending) return false;

        return WaitAndFinish(handle, evt, overlapped, buffer.Length, timeoutMs);
    }

    /// <summary>读满一整包；超时或读不够都返回 false。</summary>
    private static bool ReadExact(IntPtr handle, IntPtr evt, IntPtr overlapped, byte[] buffer, int timeoutMs)
    {
        ResetEvent(evt);
        Marshal.StructureToPtr(new OverlappedBuffer { Event = evt }, overlapped, false);

        if (ReadFile(handle, buffer, (uint)buffer.Length, out var read, overlapped))
            return read == buffer.Length;

        if (Marshal.GetLastWin32Error() != ErrorIoPending) return false;

        return WaitAndFinish(handle, evt, overlapped, buffer.Length, timeoutMs);
    }

    private static bool WaitAndFinish(
        IntPtr handle, IntPtr evt, IntPtr overlapped, int expected, int timeoutMs)
    {
        if (WaitForSingleObject(evt, (uint)timeoutMs) != WaitObject0)
        {
            // 超时：撤销这条挂起的 I/O，别把手柄带着未完成请求关掉
            CancelIoEx(handle, overlapped);
            return false;
        }

        return GetOverlappedResult(handle, overlapped, out var transferred, false)
               && transferred == expected;
    }

    internal static byte[] BuildQuery(byte commandId)
    {
        var packet = new byte[16];
        packet[0] = commandId;

        // 校验和：字节15 = 85 - (sum([报告ID8, ...前15字节]) & 255)
        var sum = 8;
        for (var i = 0; i < 15; i++) sum += packet[i];
        packet[15] = (byte)(85 - (sum & 255));

        var output = new byte[17];
        output[0] = 8;
        Array.Copy(packet, 0, output, 1, 16);
        return output;
    }

    /// <summary>16 字节包布局：[0]commandId [5]batteryLevel [6]charge [7-8]voltage [15]checkSum。</summary>
    internal static (int? Level, bool? Charging)? ParseResponse(byte[] input)
    {
        if (input is null || input.Length < 17) return null;
        if (input[0] != 8) return null;
        if (input[1] != (byte)GetBatteryLevel) return null;

        var sum = 8;
        for (var i = 1; i <= 15; i++) sum += input[i];
        if (input[16] != (byte)(85 - (sum & 255))) return null;

        var level = input[6];
        var charging = input[7] == 1 ? true : input[7] == 0 ? false : (bool?)null;

        return level is >= 0 and <= 100
            ? ((int?)level, charging)
            : (null, charging);
    }

    internal static string? ExtractVid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var m = Regex.Match(path, @"vid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    internal static bool IsCompxMouse(string? instanceHint, string? compxVid)
        => !string.IsNullOrWhiteSpace(instanceHint)
           && !string.IsNullOrWhiteSpace(compxVid)
           && instanceHint.Contains("vid_" + compxVid, StringComparison.OrdinalIgnoreCase);

    internal static string? FindCompxVid() => FindCompxPaths().Select(ExtractVid).FirstOrDefault(v => v is not null);

    /// <summary>
    /// 所有 FF02 厂商集合（hid#vid_...&amp;mi_01&amp;col05）接口路径，**非键盘优先**。
    ///
    /// 返回全部候选而不是单个：实测会同时存在多台同 VID 设备（真鼠标 + 残留枚举项），
    /// 谁排第一不保证能应答，调用方必须逐个试。带 <c>\kbd</c> 的是键盘集合，排在后面。
    /// </summary>
    internal static IReadOnlyList<string> FindCompxPaths()
    {
        var result = new List<string>();

        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x10);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; ; i++)
            {
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;

                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
                if (needed <= 0) continue;

                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, needed, out _, IntPtr.Zero)) continue;

                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (path != null && path.Contains("col05", StringComparison.OrdinalIgnoreCase))
                        result.Add(path);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        // 稳定排序：鼠标集合在前、键盘集合在后，让「第一台能应答的」尽量是鼠标
        return [.. result
            .OrderBy(p => p.Contains("\\kbd", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(IntPtr device, byte[] buffer, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        IntPtr device, byte[] buffer, uint length, out uint written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        IntPtr device, byte[] buffer, uint length, out uint read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(
        IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        IntPtr device, IntPtr overlapped, out uint transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr device, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct OverlappedBuffer
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr Event;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid guid, uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData data, IntPtr detail, int size, out int needed, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }
}
