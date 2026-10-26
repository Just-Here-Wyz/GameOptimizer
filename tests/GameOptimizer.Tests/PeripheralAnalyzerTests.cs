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

        // 耳机：3.5mm 圆孔播放端点（真机容器 7131B19F…）
        Node(@"SWD\MMDEVAPI\{0.0.0.00000000}.{1CAD56D7}", "AudioEndpoint",
            friendly: "圆孔耳机 (Audio Device)", parent: OnboardAudioId,
            container: Guid.Parse("7131b19f-5d4b-5198-9292-4fe19270a314")),
        // 同名录音端点：真机容器不同（7C62E129…），不能因此多出一台耳机
        Node(@"SWD\MMDEVAPI\{0.0.1.00000000}.{D199669A}", "AudioEndpoint",
            friendly: "圆孔耳机 (Audio Device)", parent: OnboardAudioId,
            container: Guid.Parse("7c62e129-f31d-55ef-aa23-303a98b2a7da")),
        // 虚拟音频设备：必须被排除
        Node(@"SWD\MMDEVAPI\{0.0.0.00000000}.{36C89010}", "AudioEndpoint",
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

    /// <summary>复合接收器：一个容器里既有键盘接口又有鼠标接口，且读不到自报型号。</summary>
    private static List<PeripheralNode> UnnamedComboDongle()
    {
        var container = Guid.Parse("0bda1100-1111-2222-3333-444455556666");

        return
        [
            Node(@"HID\VID_0BDA&PID_1100&MI_00\7&1A2B3C&0&0000", "Keyboard",
                busReported: "USB Receiver", container: container,
                hardwareIds: ["HID\\VID_0BDA&PID_1100&MI_00", "HID_DEVICE_UP:0001_U:0006", "HID_DEVICE_SYSTEM_KEYBOARD"]),
            Node(@"HID\VID_0BDA&PID_1100&MI_01\7&1A2B3C&0&0001", "Mouse",
                busReported: "USB Receiver", container: container,
                hardwareIds: ["HID\\VID_0BDA&PID_1100&MI_01", "HID_DEVICE_UP:0001_U:0002", "HID_DEVICE_SYSTEM_MOUSE"]),
        ];
    }

    [Fact]
    public void 判不出主功能的复合设备应按接口分别展示_不能整台算成鼠标()
    {
        // 修复前：名字里看不出类型 → 默认鼠标，键盘直接消失。
        // 现在改为按接口各出一张卡，两张都标为复合设备。
        var input = PeripheralAnalyzer.Build(UnnamedComboDongle())
            .Where(d => d.Kind is PeripheralKind.Mouse or PeripheralKind.Keyboard)
            .ToList();

        input.Should().HaveCount(2);
        input.Should().Contain(d => d.Kind == PeripheralKind.Keyboard);
        input.Should().Contain(d => d.Kind == PeripheralKind.Mouse);
        input.Should().OnlyContain(d => d.IsComposite);
        input.Should().OnlyContain(d => d.Basis.Contains("复合设备"));
    }

    [Fact]
    public void 键盘带指针接口时不能被整台变成鼠标()
    {
        // 游戏键盘多一个指针/宏集合很常见，自报型号说得很清楚是 Keyboard
        var container = Guid.Parse("046dc338-0000-0000-0000-000000000000");
        var nodes = new List<PeripheralNode>
        {
            Node(@"HID\VID_046D&PID_C338&MI_00\7&46BA748&0&0000", "Keyboard",
                hidProduct: "Gaming Keyboard G610", manufacturer: "Logitech", container: container,
                hardwareIds: ["HID_DEVICE_UP:0001_U:0006"]),
            Node(@"HID\VID_046D&PID_C338&MI_02\7&46BA749&0&0000", "Mouse",
                hidProduct: "Gaming Keyboard G610", manufacturer: "Logitech", container: container,
                hardwareIds: ["HID_DEVICE_UP:0001_U:0002"]),
        };

        var devices = PeripheralAnalyzer.Build(nodes).Where(d => d.Kind != PeripheralKind.Headset).ToList();

        devices.Should().HaveCount(1);
        devices[0].Kind.Should().Be(PeripheralKind.Keyboard);
        devices[0].IsComposite.Should().BeTrue();
    }

    [Fact]
    public void 每台设备的类型结论都应带分类依据()
    {
        // 依据是给用户复核用的：只说「这是键盘」而说不出凭什么，分类错了没人查得到
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());

        devices.Should().OnlyContain(d => d.Basis.Length > 0);
        var keyboard = devices.Single(d => d.Kind == PeripheralKind.Keyboard && d.Manufacturer == "Logitech");
        keyboard.Basis.Should().Contain("设备类 Keyboard");
    }

    [Fact]
    public void 只剩描述文字的设备应显示为未知设备而不是被丢掉()
    {
        // 属性读取失败（硬件 ID 与类名都空）时不能凭名字里的 Keyboard 冒充键盘
        var nodes = new List<PeripheralNode>
        {
            Node(@"HID\VID_1234&PID_5678\7&1&0&0000", "", friendly: "HID Keyboard Device"),
        };

        var device = PeripheralAnalyzer.Build(nodes).Single(d => d.Kind == PeripheralKind.Unknown);

        device.KindText.Should().Be("未知设备");
        device.Basis.Should().Contain("描述文字");
    }

    [Fact]
    public void 同一只键盘的多个集合应合并成一台()    {
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
            Node(@"SWD\MMDEVAPI\{0.0.0.00000000}.{68D89CED}", "AudioEndpoint",
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
    public void 笔记本自带键盘不应出现在外设列表()
    {
        // 用户要求（v1.0.1）：外设页不显示笔记本自带键盘。
        // 板载 PS/2 键盘父设备是 ACPI，外接键盘挂在 USB / BTHENUM 下不受影响。
        var devices = PeripheralAnalyzer.Build(RealMachineNodes());

        var keyboards = devices.Where(d => d.Kind == PeripheralKind.Keyboard).ToList();
        keyboards.Should().NotContain(d => d.Model.Contains("PS/2", StringComparison.OrdinalIgnoreCase));
        keyboards.Should().NotContain(d => d.Model.Contains("标准键盘", StringComparison.Ordinal));
        // 外接的罗技 G610 必须还在
        keyboards.Should().Contain(d => d.Model == "G610");
    }

    [Fact]
    public void 外接USB键盘不应被误判为笔记本自带键盘()
    {
        // USB 键盘的父设备是 USB\...，不满足「父设备是 ACPI」的内置判据
        var node = Node(@"HID\VID_046D&PID_C338&MI_01&COL01\7&1C2F338D&0&0000", "Keyboard",
            hidProduct: "Gaming Keyboard G610", manufacturer: "Logitech",
            parent: @"USB\VID_046D&PID_C338&MI_01\6&3758ce25&0&0001",
            container: Guid.Parse("838be7a9-3545-52ad-a3df-372364c4f4a3"));

        PeripheralAnalyzer.IsBuiltInKeyboard(node).Should().BeFalse();
    }

    [Fact]
    public void 蓝牙耳机连着时有线耳机也要显示()
    {
        // v2.2.2：原先只取第一个耳机端点，蓝牙耳机一连上，3.5mm 有线耳机就被挤掉。
        var container = Guid.Parse("262e1d0b-cd6d-575e-af4a-6a0b25ff0a76");
        var nodes = new List<PeripheralNode>
        {
            Node(@"SWD\MMDEVAPI\{0.0.0.00000000}.{68D89CED}", "AudioEndpoint",
                friendly: "耳机 (DM02)", parent: BluetoothHeadsetId, container: container),
            Node(@"BTHENUM\DEV_8DB64D46D6DB\7&E233738", "Bluetooth",
                friendly: "DM02", container: container, battery: 64),
            // 有线耳机：播放端点 + 同名的录音端点（真机容器不同，不能多出一台）
            Node(@"SWD\MMDEVAPI\{0.0.0.00000000}.{1CAD56D7}", "AudioEndpoint",
                friendly: "圆孔耳机 (Audio Device)", parent: OnboardAudioId,
                container: Guid.Parse("7131b19f-5d4b-5198-9292-4fe19270a314")),
            Node(@"SWD\MMDEVAPI\{0.0.1.00000000}.{D199669A}", "AudioEndpoint",
                friendly: "圆孔耳机 (Audio Device)", parent: OnboardAudioId,
                container: Guid.Parse("7c62e129-f31d-55ef-aa23-303a98b2a7da")),
        };

        var headsets = PeripheralAnalyzer.Build(nodes).Where(d => d.Kind == PeripheralKind.Headset).ToList();

        headsets.Should().HaveCount(2);
        headsets.Should().Contain(d => d.Model == "DM02" && d.Connection == PeripheralConnection.Wireless);
        headsets.Should().Contain(d => d.Model == "圆孔耳机" && d.Connection == PeripheralConnection.Wired);
    }

    [Fact]
    public void 同一只蓝牙耳机的麦克风端点不应多出一台耳机()
    {
        var container = Guid.Parse("262e1d0b-cd6d-575e-af4a-6a0b25ff0a76");
        var nodes = new List<PeripheralNode>
        {
            Node(@"SWD\MMDEVAPI\{0.0.0.00000000}.{68D89CED}", "AudioEndpoint",
                friendly: "耳机 (DM02)", parent: BluetoothHeadsetId, container: container),
            Node(@"SWD\MMDEVAPI\{0.0.1.00000000}.{0E5A2C11}", "AudioEndpoint",
                friendly: "耳机麦克风 (DM02)", parent: BluetoothHeadsetId, container: container),
        };

        PeripheralAnalyzer.Build(nodes).Count(d => d.Kind == PeripheralKind.Headset).Should().Be(1);
    }

    [Fact]
    public void 同一只耳机的播放与免手持端点应合并成一台()
    {
        // A2DP 与 HFP 端点容器不同、型号一个带 Hands-Free 后缀，仍属同一只耳机
        var nodes = new List<PeripheralNode>
        {
            Node(@"SWD\MMDEVAPI\{0.0.0.00000000}.{68D89CED}", "AudioEndpoint",
                friendly: "耳机 (DM02)", parent: BluetoothHeadsetId,
                container: Guid.Parse("262e1d0b-cd6d-575e-af4a-6a0b25ff0a76")),
            Node(@"SWD\MMDEVAPI\{0.0.0.00000000}.{A1B2C3D4}", "AudioEndpoint",
                friendly: "耳机 (DM02 Hands-Free AG Audio)", parent: BluetoothHeadsetId,
                container: Guid.Parse("9b7f5c62-1d3e-5a4b-8c11-3d2e5f6a7b8c")),
        };

        var headsets = PeripheralAnalyzer.Build(nodes).Where(d => d.Kind == PeripheralKind.Headset).ToList();

        headsets.Should().HaveCount(1);
        headsets[0].Model.Should().Be("DM02");
    }

    [Fact]
    public void 空设备树不应抛异常()
        => PeripheralAnalyzer.Build([]).Should().BeEmpty();

    private const string BluetoothHeadsetId =
        @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID&000105d6_PID&000a\7&E233738&0&8DB64D46D6DB_C00000000";
}
