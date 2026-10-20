namespace GameOptimizer.Core.Peripherals;

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
/// <summary>
/// VXE / ATK（Compx）鼠标电量的私有 HID 协议读取器。
///
/// 协议来源：hub.atk.pro 前端 WebHID 驱动逆向（「逆向REA」工作区，ATK 鼠标电量 HID 协议分析）——
/// Compx 方案在 usagePage=0xFF02 / usage=2 的厂商集合上用 16 字节包通信，
/// 命令 GetBatteryLevel=4。本机 VXE R1（VID 3554 PID F58A）实测验证：
/// 输出 08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49
/// 输入 08 04 00 00 00 02 41 00 0F 7F 00 00 00 00 00 00 78
///   字节5 = 电量 0x41 = 65%（0~100）
///   字节6 = 充电状态（0=放电 1=充电）
///   字节7-8 = 电压（小端）
/// 校验和：字节15 = 85 - (sum(报告ID8 + 前15字节) & 255)，响应同样带校验。
/// </summary>
public sealed class VxeBatterySource
{
    /// <summary>COMPX 方案匹配的 usagePage（FF02=65282）。</summary>
    private const ushort CompxUsagePage = 0xFF02;

    /// <summary>COMPX 方案的 usage（2）。</summary>
    private const ushort CompxUsage = 2;

    private const uint GetBatteryLevel = 4;

    /// <summary>
    /// 在本机设备树里找 VXE/ATK 鼠标的 FF02 厂商集合。
    /// 匹配路径特征 hid#vid_xxxx&pid_xxxx&mi_01&col05（FF02），
    /// 且设备必须是鼠标（其兄弟集合 MI_02 是 HID 鼠标）。
    /// 找不到或打开失败返回 null，调用方显示「未知」而不是编数字。
    /// </summary>
    public static int? ReadBatteryPercent()
    {
        var path = FindCompxPath();
        if (path is null) return null;

        var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;

        try
        {
            var output = BuildQuery((byte)GetBatteryLevel);

            // 实测序列：SetOutputReport 使设备进入应答状态，
            // 随后 WriteFile 触发一次实际查询，ReadFile 取回 17 字节响应
            if (!HidD_SetOutputReport(handle, output, (uint)output.Length)) return null;
            Thread.Sleep(80);
            if (!WriteFile(handle, output, (uint)output.Length, out _, IntPtr.Zero)) return null;
            Thread.Sleep(250);

            var input = new byte[17];
            if (!ReadFile(handle, input, 17, out var read, IntPtr.Zero)) return null;
            if (read < 17) return null;

            var parsed = ParseResponse(input);
            return parsed?.Level;
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

    /// <summary>读取充电状态（0=放电 1=充电），失败返回 null。</summary>
    public static bool? ReadChargingStatus()
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

            return ParseResponse(input)?.Charging;
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

    /// <summary>
    /// 组包：16 字节 Compx 包，报告 ID 8 前置。
    /// 纯函数，供单测用真实抓包字节锁死格式。
    /// </summary>
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

    /// <summary>
    /// 解析响应（17 字节：报告 ID + 16 字节包）。
    /// 16 字节包布局（REA 逆向结论）：[0]commandId [1]status [2-3]eeprom [4]dataValidLen
    /// [5]batteryLevel [6]batteryCharge [7-8]voltage [15]checkSum。
    /// 校验失败、命令码不匹配、电量越界时返回 null——绝不用脏数据冒充读数。
    /// </summary>
    internal static (int? Level, bool? Charging)? ParseResponse(byte[] input)
    {
        if (input is null || input.Length < 17) return null;
        if (input[0] != 8) return null;
        if (input[1] != (byte)GetBatteryLevel) return null;

        // 校验和：checkSum = 85 - (sum([报告ID8, 包字节0..14]) & 255)
        var sum = 8;
        for (var i = 1; i <= 15; i++) sum += input[i];
        var expected = (byte)(85 - (sum & 255));
        if (input[16] != expected) return null;

        var level = input[6];   // 包偏移 5 = 数组索引 6
        var charging = input[7] == 1 ? true : input[7] == 0 ? false : (bool?)null;

        return level is >= 0 and <= 100
            ? ((int?)level, charging)
            : (null, charging);
    }

    /// <summary>从设备接口路径里抠出 VID（如 "3554"）；取不到返回 null。</summary>
    internal static string? ExtractVid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var m = Regex.Match(path, @"vid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>某外设实例 ID 是否属于这台 COMPX 鼠标（按 VID 匹配）。</summary>
    internal static bool IsCompxMouse(string? instanceHint, string? compxVid)
        => !string.IsNullOrWhiteSpace(instanceHint)
           && !string.IsNullOrWhiteSpace(compxVid)
           && instanceHint.Contains("vid_" + compxVid, StringComparison.OrdinalIgnoreCase);

    /// <summary>读这台 COMPX 鼠标的 VID（如 "3554"）；找不到返回 null。</summary>
    internal static string? FindCompxVid()
        => ExtractVid(FindCompxPath());

    /// <summary>找 COMPX FF02 集合的接口路径；找不到返回 null。</summary>
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
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    // 接口路径形如 \\?\hid#vid_3554&pid_f58a&mi_01&col05#...
                    var lower = path.ToLowerInvariant();
                    if (lower.Contains("col05", StringComparison.Ordinal))
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

        // 优先取「非键盘」的那条（键盘接口路径带 \kbd 后缀）
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
