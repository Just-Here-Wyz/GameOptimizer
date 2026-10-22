namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Peripherals;

/// <summary>
/// 外设识别。全部用例的实例 ID 都是从本机真实设备树里抓下来的，
/// 不是编的 —— 每一条都对应一次实际的误判。
/// </summary>
public class PeripheralClassifierTests
{
    // 本机实测的设备 ID
    private const string VxeMouseHid = @"HID\VID_3554&PID_F58A&MI_02\7&11A065B0&0&0000";
    private const string VxeKeyboardHid = @"HID\VID_3554&PID_F58A&MI_00\7&26DAF73B&0&0000";
    private const string VxeConsumerHid = @"HID\VID_3554&PID_F58A&MI_01&COL03\7&3A0B1379&0&0002";
    private const string LogitechKeyboardHid = @"HID\VID_046D&PID_C338&MI_01&COL01\7&1C2F338D&0&0000";
    private const string TouchpadHid = @"HID\FTCS100&COL01\4&233BF7F9&0&0000";
    private const string BluetoothHeadset = @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID&000105d6_PID&000a\7&E233738&0&8DB64D46D6DB_C00000000";
    private const string OnboardAudio = @"INTELAUDIO\FUNC_01&VEN_14F1&DEV_1F87&SUBSYS_19E53289&REV_1001\5&3f94d5f&0&0001";
    private const string Ps2Keyboard = @"ACPI\MSFT0001\4&2D89D205&0";

    [Fact]
    public void 设备树类名为鼠标时应识别为鼠标()
    {
        // 实测 3554:F58A 的鼠标接口 HardwareIds 读得到，但类名更直接可靠
        PeripheralClassifier.FromHidHardwareIds([], "Mouse").Should().Be(PeripheralKind.Mouse);
    }

    [Fact]
    public void 设备树类名为键盘时应识别为键盘()
        => PeripheralClassifier.FromHidHardwareIds([], "Keyboard").Should().Be(PeripheralKind.Keyboard);

    [Fact]
    public void 触摸板的硬件ID带数字化仪用途页_必须排除()
    {
        // 实测 HID\FTCS100 的硬件 ID 里确实带 UP:0001_U:0002（Windows 归为 Mouse 类），
        // 只按类名判会把它当成一只鼠标，笔记本上就凭空多出一只
        string[] ids = ["HID\\FTCS100&Col01", "HID\\VID_2808&UP:0001_U:0002", "HID_DEVICE_UP:0001_U:0002"];
        PeripheralClassifier.FromHidHardwareIds(ids).Should().Be(PeripheralKind.Mouse); // 用途页本身还是鼠标

        string[] digitizer = ["HID\\VID_2808&UP:000D_U:0004", "HID_DEVICE_UP:000D_U:0004"];
        PeripheralClassifier.FromHidHardwareIds(digitizer).Should().BeNull();
    }

    [Fact]
    public void 游戏手柄不应被当成鼠标或键盘()
    {
        string[] ids = ["HID\\VID_054C&UP:0005_U:0001"];
        PeripheralClassifier.FromHidHardwareIds(ids).Should().BeNull();
    }

    [Fact]
    public void 消费类集合不能推断成耳机_键盘媒体键也带同样的用途页()
    {
        // 实测 HID\VID_046D&PID_C338&MI_01&COL02 是键盘的消费类集合，
        // 早期版本按 0x0C 用途页判成耳机，界面上就多出一台「耳机 G610」
        string[] ids = ["HID\\VID_046D&UP:000C_U:0001"];
        PeripheralClassifier.FromHidHardwareIds(ids).Should().BeNull();
    }

    [Theory]
    [InlineData(BluetoothHeadset, PeripheralConnection.Wireless)]
    [InlineData(VxeMouseHid, PeripheralConnection.Wired)]
    [InlineData(OnboardAudio, PeripheralConnection.Wired)]
    [InlineData(Ps2Keyboard, PeripheralConnection.Wired)]
    [InlineData("PCI\\VEN_8086&DEV_519D", PeripheralConnection.Wired)]
    [InlineData("", PeripheralConnection.Unknown)]
    [InlineData(@"ROOT\UNKNOWN\0000", PeripheralConnection.Unknown)]
    public void 连接方式应按总线判定(string instanceId, PeripheralConnection expected)
        => PeripheralClassifier.FromBus(instanceId).Should().Be(expected);

    [Fact]
    public void 读到电量就能判定为无线_接收器式鼠标在系统里挂在USB下()
    {
        // 本机实测：接收器鼠标在总线层面看起来和有线鼠标一样，
        // 只有「有电池」这件事能证明它是无线的
        PeripheralClassifier.Resolve(PeripheralConnection.Wired, batteryPercent: 83, hasBatteryDeviceNode: false)
            .Should().Be(PeripheralConnection.Wireless);
    }

    [Fact]
    public void 没有电量时保留总线判定()
        => PeripheralClassifier.Resolve(PeripheralConnection.Wired, null, false).Should().Be(PeripheralConnection.Wired);

    [Theory]
    [InlineData("VXE NordicMouse 1K Dongle", PeripheralConnection.Wireless)]
    [InlineData("HyperX Cloud Flight Wireless", PeripheralConnection.Wireless)]
    [InlineData("2.4G Receiver", PeripheralConnection.Wireless)]
    [InlineData("Gaming Keyboard G610", null)]
    [InlineData("Wired Mouse", PeripheralConnection.Wired)]
    public void 产品名里的连接线索应被采纳(string name, PeripheralConnection? expected)
        => PeripheralClassifier.FromProductName(name).Should().Be(expected);

    [Theory]
    [InlineData("Gaming Keyboard G610", "G610")]
    [InlineData("符合 HID 标准的系统控制器", "系统控制器")]
    [InlineData("@oem141.inf,%g610name%;G610", "G610")]
    [InlineData("HID-compliant mouse", "mouse")]
    [InlineData("   ", "未知型号")]
    [InlineData(null, "未知型号")]
    public void 型号名应去掉驱动前缀(string? raw, string expected)
        => PeripheralClassifier.CleanModel(raw).Should().Be(expected);

    [Theory]
    [InlineData("耳机 (DM02)", "DM02")]
    [InlineData("圆孔耳机 (Audio Device)", "圆孔耳机")]
    [InlineData("Headset (HyperX Cloud Flight)", "HyperX Cloud Flight")]
    [InlineData("耳机", "耳机")]
    public void 耳机型号应从端点名里提取(string name, string expected)
        => PeripheralClassifier.HeadsetModel(name).Should().Be(expected);

    [Theory]
    [InlineData("耳机 (DM02)", true)]
    [InlineData("Headphones", true)]
    [InlineData("扬声器", false)]
    [InlineData("Microphone", false)]
    [InlineData("耳机麦克风 (DM02)", false)]
    public void 只有耳机端点才算耳机(string name, bool expected)
        => PeripheralClassifier.IsHeadsetEndpoint(name).Should().Be(expected);

    [Theory]
    // MMDevice 端点 ID：{0.0.0.x} 播放，{0.0.1.x} 录音
    [InlineData(@"SWD\MMDEVAPI\{0.0.1.00000000}.{D199669A-D5EA-4D65-B200-A12905AEB304}", true)]
    [InlineData(@"SWD\MMDEVAPI\{0.0.0.00000000}.{1CAD56D7-1ED2-4F6A-8A4E-505DD7402D5D}", false)]
    [InlineData(@"BTHENUM\{0000110B}\7&E233738", false)]
    [InlineData(null, false)]
    public void 录音端点应能识别(string? instanceId, bool expected)
        => PeripheralClassifier.IsCaptureEndpoint(instanceId).Should().Be(expected);

    [Fact]
    public void 同一物理设备的多个接口应归到同一个键()
    {
        // MI_00 / MI_01 / MI_02 是同一只复合鼠标的三个接口
        var a = PeripheralClassifier.StableKey(@"HID\VID_3554&PID_F58A&MI_00\7&26DAF73B&0&0000");
        var b = PeripheralClassifier.StableKey(@"HID\VID_3554&PID_F58A&MI_02\7&11A065B0&0&0000");

        a.Should().Be(b);
        a.Should().Be(@"HID\VID_3554&PID_F58A");
    }

    [Fact]
    public void 键盘子接口后缀应被去掉()
        => PeripheralClassifier.StableKey(@"HID\VID_046D&PID_C338&MI_01&COL01\7&1C2F338D&0&0000\kbd")
            .Should().Be(@"HID\VID_046D&PID_C338");
}
