namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Peripherals;

/// <summary>
/// 外设合并逻辑。节点数据全部取自本机真实设备树（保留原样），
/// 这些用例锁死的是「界面上到底会显示几台设备、显示成什么」。
/// </summary>
public class PeripheralAnalyzerTests
{
    private static PeripheralNode Node(
        string instanceId,
        string className,
        string? friendly = null,
        string? hidProduct = null,
        string? busReported = null,
        string? parent = null,
        Guid? container = null,
        int? battery = null,
        string? manufacturer = null,
        params string[] hardwareIds)
        => new(instanceId, className, friendly, busReported, friendly, hardwareIds,
               parent, container, battery, battery is null ? null : "蓝牙电量上报", hidProduct, manufacturer);

    /// <summary>本机实测：VXE 接收器鼠标 + 罗技 G610 + 触摸板 + PS/2 键盘 + 虚拟 HID 框架。</summary>
    private static List<PeripheralNode> RealMachineNodes() =>
    [
        // 接收器鼠标：鼠标接口、键盘接口、消费类集合分属不同容器
        Node(@"HID\VID_3554&PID_F58A&MI_02\7&11A065B0&0&0000", "Mouse",
            hidProduct: "VXE NordicMouse 1K Dongle", manufacturer: "Compx",
            parent: @"USB\VID_3554&PID_F58A&MI_02\6&4d8272f&0&0002",
            container: Guid.Parse("fc73bbae-6a47-11f1-bcbe-806e6f6e6963")),
        Node(@"HID\VID_3554&PID_F58A&MI_00\7&26DAF73B&0&0000", "Keyboard",
            hidProduct: "VXE NordicMouse 1K Dongle", manufacturer: "Compx",
            container: Guid.Parse("fc73bbae-6a47-11f1-bcbe-806e6f6e6963")),
        Node(@"HID\VID_3554&PID_F58A&MI_01&COL03\7&3A0B1379&0&0002", "HIDClass",
            hidProduct: "VXE NordicMouse 1K Dongle", manufacturer: "Compx",
            hardwareIds: ["HID\\VID_3554&UP:000C_U:0001"]),

        // 罗技 G610：两个键盘集合共用一个容器
        Node(@"HID\VID_046D&PID_C338&MI_01&COL01\7&1C2F338D&0&0000", "Keyboard",
            hidProduct: "Gaming Keyboard G610", manufacturer: "Logitech",
            parent: @"USB\VID_046D&PID_C338&MI_01\6&3758ce25&0&0001",
            container: Guid.Parse("838be7a9-3545-52ad-a3df-372364c4f4a3")),
        Node(@"HID\VID_046D&PID_C338&MI_00\7&46BA748&0&0000", "Keyboard",
            hidProduct: "Gaming Keyboard G610", manufacturer: "Logitech",
            container: Guid.Parse("838be7a9-3545-52ad-a3df-372364c4f4a3")),

        // 触摸板：Windows 归到 Mouse 类，父设备是 ACPI
        Node(@"HID\FTCS100&COL01\4&233BF7F9&0&0000", "Mouse",
            hidProduct: "HIDI2C Device", manufacturer: "Microsoft",
            parent: @"ACPI\FTCS100\3&11583659&1",
            hardwareIds: ["HID\\FTCS100&Col01", "HID\\VID_2808&UP:0001_U:0002"]),

        // 板载 PS/2 键盘：父设备是 ACPI，但它是真键盘
        Node(Ps2KeyboardId, "Keyboard", friendly: "PS/2 标准键盘",
            parent: @"PCI\VEN_8086&DEV_519D&SUBSYS_3E6119E5&REV_01\3&11583659&1&F8"),

        // 虚拟 HID 框架：早期版本会把 HID VHF Driver 当成键盘
        Node(@"HID\HID_DEVICE_SYSTEM_VHF\7&513DC07&0&0000", "HIDClass",
            friendly: "HID-compliant device", hidProduct: "HID VHF Driver",
            hardwareIds: ["HID_DEVICE_SYSTEM_KEYBOARD", "HID\\VID_0000&UP:0001_U:0006"]),
        Node(@"ROOT\HIDCLASS\0000", "HIDClass", friendly: "Virtual HID Device",
            hidProduct: "UMDF Virtual hid device Product string"),

        // 耳机：3.5mm 圆孔端点
        Node(@"SWD\MMDEVAPI\{0.0.1.00000000}.{D199669A}", "AudioEndpoint",
            friendly: "圆孔耳机 (Audio Device)", parent: OnboardAudioId),
        // 虚拟音频设备：必须被排除
        Node(@"SWD\MMDEVAPI\{0.0.1.00000000}.{36C89010}", "AudioEndpoint",
            friendly: "Dubbing AI Speaker (Dubbing AI Virtual Device)", parent: @"ROOT\MEDIA\0000"),
    ];

    private const string Ps2KeyboardId = @"ACPI\MSFT0001\4&2D89D205&0";
    private const string OnboardAudioId = @"INTELAUDIO\FUNC_01&VEN_14F1&DEV_1F87&SUBSYS_19E53289&REV_1001\5&3f94d5f&0&0001";

    [Fact]
    public void 本机设备树应解析出鼠标键盘耳机三类()
    {
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());

        devices.Should().Contain(d => d.Kind == PeripheralKind.Mouse);
        devices.Should().Contain(d => d.Kind == PeripheralKind.Keyboard);
        devices.Should().Contain(d => d.Kind == PeripheralKind.Headset);
    }

    [Fact]
    public void 触摸板不能算成鼠标()
    {
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());

        devices.Should().NotContain(d => d.Model.Contains("HIDI2C", StringComparison.OrdinalIgnoreCase));
        devices.Should().NotContain(d => d.Model.Contains("触摸", StringComparison.Ordinal));
    }

    [Fact]
    public void 虚拟HID框架不能被当成键盘()
    {
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());

        devices.Should().NotContain(d => d.Model.Contains("VHF", StringComparison.OrdinalIgnoreCase));
        devices.Should().NotContain(d => d.Model.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 复合接收器只应算作一台鼠标_不应因为带键盘集合多出一台()
    {
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());
        var vxe = devices.Where(d => d.Model.Contains("VXE", StringComparison.OrdinalIgnoreCase)).ToList();

        vxe.Should().HaveCount(1);
        vxe[0].Kind.Should().Be(PeripheralKind.Mouse);
    }

    [Fact]
    public void 同一只键盘的多个集合应合并成一台()
    {
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());
        var logitech = devices.Where(d => d.Model.Contains("G610", StringComparison.Ordinal)).ToList();

        logitech.Should().HaveCount(1);
        logitech[0].Manufacturer.Should().Be("Logitech");
    }

    [Fact]
    public void 型号应优先用设备自报的字符串()
    {
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());
        var keyboard = devices.Single(d => d.Kind == PeripheralKind.Keyboard && d.Manufacturer == "Logitech");

        // 驱动描述只有「USB 输入设备」，型号来自 HidD_GetProductString
        keyboard.Model.Should().Be("G610");
    }

    [Fact]
    public void 接收器鼠标应判为无线而不是有线()
    {
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());
        var mouse = devices.Single(d => d.Kind == PeripheralKind.Mouse);

        // 名字里的 Dongle 说明它走 USB 接收器，按总线判会错成「有线」
        mouse.Connection.Should().Be(PeripheralConnection.Wireless);
        mouse.BatteryText.Should().Be("未知");
    }

    [Fact]
    public void 有线设备电量应显示有线而不是数字()
    {
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());
        var keyboard = devices.Single(d => d.Kind == PeripheralKind.Keyboard && d.Manufacturer == "Logitech");

        keyboard.IsWired.Should().BeTrue();
        keyboard.BatteryText.Should().Be("有线");
        keyboard.BatteryPercent.Should().BeNull();
    }

    [Fact]
    public void 虚拟音频设备不能算成耳机()
    {
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());
        devices.Count(d => d.Kind == PeripheralKind.Headset).Should().Be(1);

        var headset = devices.Single(d => d.Kind == PeripheralKind.Headset);
        headset.Model.Should().Be("圆孔耳机");
        headset.IsWired.Should().BeTrue();
    }

    [Fact]
    public void 蓝牙耳机的电量应从同容器设备上取到()
    {
        var container = Guid.Parse("262e1d0b-cd6d-575e-af4a-6a0b25ff0a76");
        var nodes = new List<PeripheralNode>
        {
            // 端点本身不报电量
            Node(@"SWD\MMDEVAPI\{0.0.1.00000000}.{68D89CED}", "AudioEndpoint",
                friendly: "耳机 (DM02)", parent: BluetoothHeadsetId, container: container),
            // 同容器的蓝牙主设备才报电量
            Node(@"BTHENUM\DEV_8DB64D46D6DB\7&E233738", "Bluetooth",
                friendly: "DM02", container: container, battery: 64),
        };

        var headset = PeripheralAnalyzer.Build(nodes).Single(d => d.Kind == PeripheralKind.Headset);

        headset.BatteryPercent.Should().Be(64);
        headset.Connection.Should().Be(PeripheralConnection.Wireless);
        headset.BatteryText.Should().Be("64%");
    }

    [Fact]
    public void 无线且读不到电量时应如实显示未知()
    {
        var nodes = new List<PeripheralNode>
        {
            Node(@"BTHENUM\{0000110B}\7&E233738", "AudioEndpoint", friendly: "耳机 (DM02)"),
        };

        var headset = PeripheralAnalyzer.Build(nodes).Single(d => d.Kind == PeripheralKind.Headset);

        headset.BatteryPercent.Should().BeNull();
        headset.BatteryText.Should().Be("未知");
        headset.Connection.Should().Be(PeripheralConnection.Wireless);
    }

    [Fact]
    public void 输出顺序应为鼠标键盘耳机()
    {
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());

        for (var i = 1; i < devices.Count; i++)
            ((int)devices[i].Kind).Should().BeGreaterThanOrEqualTo((int)devices[i - 1].Kind);
    }

    [Fact]
    public void 空设备树不应抛异常()
        => PeripheralAnalyzer.Build([]).Should().BeEmpty();

    private const string BluetoothHeadsetId =
        @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID&000105d6_PID&000a\7&E233738&0&8DB64D46D6DB_C00000000";
}
