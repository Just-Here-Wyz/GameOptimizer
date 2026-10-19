namespace GameOptimizer.Core.Peripherals;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// 设备树的底层访问：SetupAPI 枚举设备 + Configuration Manager 属性 + HID 型号字符串。
///
/// 全部走系统自带 DLL，不引 System.Management（那要多一个 NuGet 包），
/// 也不调 PowerShell（起进程慢且输出难解析）。
/// </summary>
internal static class DeviceTree
{
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_ALLCLASSES = 0x04;
    private const int DIGCF_DEVICEINTERFACE = 0x10;

    private const int SPDRP_DEVICEDESC = 0x00;
    private const int SPDRP_CLASS = 0x07;
    private const int SPDRP_FRIENDLYNAME = 0x0C;

    private static readonly Guid HidGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private static readonly Guid DevicePropertiesGuid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private static readonly Guid DeviceProperties2Guid = new("4340A6C5-93FA-4706-972C-7B648008A5A7");
    private static readonly Guid BluetoothGuid = new("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A");
    private static readonly Guid ContainerGuid = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");

    // DEVPKEY_Device_Class / FriendlyName / DeviceDesc / HardwareIds / Parent / ContainerId
    internal static readonly DEVPROPKEY KeyClass = Key(DevicePropertiesGuid, 10);
    internal static readonly DEVPROPKEY KeyFriendlyName = Key(DevicePropertiesGuid, 14);
    internal static readonly DEVPROPKEY KeyDeviceDesc = Key(DevicePropertiesGuid, 2);
    internal static readonly DEVPROPKEY KeyHardwareIds = Key(DevicePropertiesGuid, 3);
    internal static readonly DEVPROPKEY KeyParent = Key(DeviceProperties2Guid, 8);
    internal static readonly DEVPROPKEY KeyContainerId = Key(ContainerGuid, 2);

    /// <summary>DEVPKEY_Device_BusReportedDeviceDesc：USB 设备自报的型号，比驱动描述准。</summary>
    internal static readonly DEVPROPKEY KeyBusReportedDesc = Key(new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), 4);

    /// <summary>DEVPKEY_Bluetooth_Battery：Windows 10+ 给蓝牙外设的 0~100 电量。</summary>
    internal static readonly DEVPROPKEY KeyBluetoothBattery = Key(BluetoothGuid, 4);

    /// <summary>设备节点（已解析好的属性快照）。</summary>
    internal sealed record Node(
        string InstanceId,
        string ClassName,
        string? FriendlyName,
        string? BusReportedDesc,
        string? DeviceDesc,
        IReadOnlyList<string> HardwareIds,
        string? ParentInstanceId,
        Guid? ContainerId,
        int? BluetoothBattery,
        string? HidProduct,
        string? HidManufacturer);

    /// <summary>枚举所有「当前存在」的设备节点。</summary>
    internal static List<Node> Enumerate()
    {
        var result = new List<Node>();

        // HID 型号字符串要单独走设备接口枚举，先建好 实例ID → 型号 的索引
        var hidProducts = ReadHidProductStrings();

        var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            var data = new SP_DEVINFO_DATA();
            data.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();

            for (uint i = 0; ; i++)
            {
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;

                var instanceId = GetInstanceId(set, ref data);
                if (string.IsNullOrWhiteSpace(instanceId)) continue;

                var className = GetRegistryString(set, ref data, SPDRP_CLASS) ?? "";
                var hardwareIds = ReadStringList(instanceId, KeyHardwareIds);

                var hid = hidProducts.GetValueOrDefault(instanceId);

                var container = ReadGuid(instanceId, KeyContainerId);
                // 系统给「无法归组」的设备塞这个占位 GUID，不能当成真实容器
                if (container == new Guid("00000000-0000-0000-FFFF-FFFFFFFFFFFF")) container = null;

                result.Add(new Node(
                    instanceId,
                    className,
                    ReadString(instanceId, KeyFriendlyName) ?? GetRegistryString(set, ref data, SPDRP_FRIENDLYNAME),
                    ReadString(instanceId, KeyBusReportedDesc),
                    ReadString(instanceId, KeyDeviceDesc) ?? GetRegistryString(set, ref data, SPDRP_DEVICEDESC),
                    hardwareIds,
                    ReadString(instanceId, KeyParent),
                    container,
                    ReadInt(instanceId, KeyBluetoothBattery),
                    hid.Product,
                    hid.Manufacturer));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    /// <summary>
    /// HID 设备自己上报的型号字符串。
    /// 实测：`HidD_GetProductString` 给出 `VXE NordicMouse 1K Dongle` / `Gaming Keyboard G610`，
    /// 而驱动描述只有「USB 输入设备」。这条路是拿到具体型号的关键。
    /// </summary>
    private static Dictionary<string, (string? Product, string? Manufacturer)> ReadHidProductStrings()
    {
        var map = new Dictionary<string, (string? Product, string? Manufacturer)>(StringComparer.OrdinalIgnoreCase);

        // SetupDi* 的 ref Guid 参数不能直接传静态只读字段，先拷到局部变量
        var hidGuid = HidGuid;
        var set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return map;

        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA();
            iface.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

            for (uint i = 0; ; i++)
            {
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface)) break;

                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
                if (needed <= 0) continue;

                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6); // cbSize 按位数对齐
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, buffer, needed, out _, IntPtr.Zero)) continue;

                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    var instanceId = HidPathToInstanceId(path);
                    if (string.IsNullOrWhiteSpace(instanceId)) continue;

                    var (product, manufacturer) = ReadHidStrings(path);
                    if (string.IsNullOrWhiteSpace(product) && string.IsNullOrWhiteSpace(manufacturer)) continue;

                    // 同一台设备的多个集合共用同一个型号，这里保留第一个拿到的
                    if (!map.ContainsKey(instanceId)) map[instanceId] = (product, manufacturer);
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

        return map;
    }

    /// <summary>把 `\\?\hid#vid_046d&amp;pid_c338&amp;mi_00#7&amp;46ba748&amp;0&amp;0000#{guid}` 还原成实例 ID。</summary>
    internal static string HidPathToInstanceId(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return "";

        var text = devicePath;
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text[4..];

        // 去掉 {接口 GUID} 与可能的 \kbd 之类子项
        var hash = text.IndexOf('#');
        if (hash < 0) return "";

        var interfaceGuid = text.IndexOf("#{", StringComparison.Ordinal);
        if (interfaceGuid > 0) text = text[..interfaceGuid];

        var parts = text.Split('#');
        if (parts.Length < 3) return "";

        // hid#vid_x&pid_y&mi_z#7&...&0&0000 → HID\VID_X&PID_Y&MI_Z\7&...&0&0000
        return $"{parts[0].ToUpperInvariant()}\\{parts[1].ToUpperInvariant()}\\{parts[2].ToUpperInvariant()}";
    }

    private static (string? Product, string? Manufacturer) ReadHidStrings(string devicePath)
    {
        var handle = CreateFile(devicePath, 0, 3 /*FILE_SHARE_READ|WRITE*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return (null, null);

        try
        {
            var product = new StringBuilder(256);
            var manufacturer = new StringBuilder(256);

            var okProduct = HidD_GetProductString(handle, product, product.Capacity);
            var okMfr = HidD_GetManufacturerString(handle, manufacturer, manufacturer.Capacity);

            return (okProduct ? product.ToString().Trim() : null,
                    okMfr ? manufacturer.ToString().Trim() : null);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// 读设备属性。
    ///
    /// 要点：`CM_Get_DevNode_Property` 要的是 DEVINST（32 位整数），不是实例 ID 字符串。
    /// 之前直接把实例 ID 传进去、把 devInst 填 0，所有属性读取都静默失败，
    /// 结果 HardwareIds / Parent / 蓝牙电量全部为空——正是靠实机输出对比才发现。
    /// 正确做法是先 `CM_Locate_DevNode` 拿到 devInst。
    /// </summary>
    private static bool TryReadProperty(string instanceId, DEVPROPKEY key, byte[] buffer, out int type, out int size)
    {
        type = 0;
        size = buffer.Length;

        if (CM_Locate_DevNodeW(out var devInst, instanceId, 0) != 0) return false;

        var rc = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buffer, ref size, 0);
        return rc == 0;
    }

    private static string? ReadString(string instanceId, DEVPROPKEY key)
    {
        var buffer = new byte[2048];
        if (!TryReadProperty(instanceId, key, buffer, out _, out var size) || size <= 0) return null;

        var length = Math.Min(size, buffer.Length);
        var text = Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static Guid? ReadGuid(string instanceId, DEVPROPKEY key)
    {
        var buffer = new byte[32];
        if (!TryReadProperty(instanceId, key, buffer, out var type, out var size) || size <= 0) return null;

        // DEVPROP_TYPE_GUID = 0x0000000D
        if (type != 0x0D) return null;
        var guid = new Guid(new ReadOnlySpan<byte>(buffer, 0, 16));
        return guid == Guid.Empty ? null : guid;
    }

    private static int? ReadInt(string instanceId, DEVPROPKEY key)
    {
        var buffer = new byte[16];
        if (!TryReadProperty(instanceId, key, buffer, out var type, out var size) || size <= 0) return null;

        return type switch
        {
            // DEVPROP_TYPE_UINT32 = 0x00000007 / DEVPROP_TYPE_BYTE = 0x00000003
            0x07 => BitConverter.ToUInt32(buffer, 0) is var u && u <= 100 ? (int)u : null,
            0x03 => buffer[0] <= 100 ? buffer[0] : null,
            _ => null,
        };
    }

    private static IReadOnlyList<string> ReadStringList(string instanceId, DEVPROPKEY key)
    {
        var buffer = new byte[8192];
        if (!TryReadProperty(instanceId, key, buffer, out _, out var size) || size <= 0) return [];

        var length = Math.Min(size, buffer.Length);
        var text = Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var buffer = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : null;
    }

    private static string? GetRegistryString(IntPtr set, ref SP_DEVINFO_DATA data, int property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, (uint)property, out _, buffer, buffer.Length, out var needed))
            return null;

        var length = needed > 0 ? Math.Min((int)needed, buffer.Length) : buffer.Length;
        var text = Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static DEVPROPKEY Key(Guid fmtid, uint pid) => new() { fmtid = fmtid, pid = pid };

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr set, IntPtr devInfo, ref Guid interfaceClass, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int required, IntPtr devInfo);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr set, ref SP_DEVINFO_DATA info, uint property, out uint regType,
        byte[] buffer, int bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr set, ref SP_DEVINFO_DATA info, StringBuilder instanceId, int bufferSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string instanceId, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_PropertyW(
        uint devInst, ref DEVPROPKEY key, out int type,
        byte[] buffer, ref int bufferSize, int flags);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr handle, StringBuilder buffer, int length);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(IntPtr handle, StringBuilder buffer, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
