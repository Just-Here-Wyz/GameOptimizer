namespace GameOptimizer.Core.Peripherals;

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

/// <summary>
/// VXE/ATK（Compx）鼠标电量读取。协议来自 hub.atk.pro 前端驱动逆向（逆向REA 工作区），
/// 本机 VXE R1 实测：输出 08 04 00..49 → 输入 08 04 00 00 00 02 41 00 0F 7F..78（电量 0x41=65%）。
/// </summary>
public sealed class VxeBatterySource
{
    private const uint GetBatteryLevel = 4;

    public static int? ReadBatteryPercent() => Query()?.Level;

    public static bool? ReadChargingStatus() => Query()?.Charging;

    /// <summary>SetOutputReport 让设备进入应答状态，WriteFile 触发查询，ReadFile 取 17 字节响应。</summary>
    private static (int? Level, bool? Charging)? Query()
    {
        var path = FindCompxPath();
        if (path is null) return null;

        var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;

        try
        {
            var output = BuildQuery((byte)GetBatteryLevel);

            if (!HidD_SetOutputReport(handle, output, (uint)output.Length)) return null;
            Thread.Sleep(80);
            if (!WriteFile(handle, output, (uint)output.Length, out _, IntPtr.Zero)) return null;
            Thread.Sleep(250);

            var input = new byte[17];
            if (!ReadFile(handle, input, 17, out var read, IntPtr.Zero)) return null;
            if (read < 17) return null;

            return ParseResponse(input);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
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

    internal static string? FindCompxVid()
        => ExtractVid(FindCompxPath());

    /// <summary>FF02 厂商集合接口路径（hid#vid_...&mi_01&col05），优先非键盘（带 \kbd 的是键盘）。</summary>
    internal static string? FindCompxPath()
    {
        var result = new List<string>();

        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x10);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;

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

        return result.FirstOrDefault(p => !p.Contains("\\kbd", StringComparison.OrdinalIgnoreCase))
               ?? result.FirstOrDefault();
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(IntPtr device, byte[] buffer, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr device, byte[] buffer, uint length, out uint written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr device, byte[] buffer, uint length, out uint read, IntPtr overlapped);

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
