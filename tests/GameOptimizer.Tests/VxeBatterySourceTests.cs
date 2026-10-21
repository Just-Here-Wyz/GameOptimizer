namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Peripherals;

/// <summary>
/// VXE/ATK（Compx）鼠标电量协议。
/// 协议来自「逆向REA」工作区对 hub.atk.pro 前端 WebHID 驱动的逆向结论，
/// 本机 VXE R1（VID 3554 PID F58A）实测抓包验证：
///   输出 08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49
///   输入 08 04 00 00 00 02 41 00 0F 7F 00 00 00 00 00 00 78
/// 17 字节 = 报告ID(8) + 16字节包；包布局：
/// [0]commandId=4 [5]batteryLevel=0x41(65%) [6]charge=0 [7-8]voltage [15]checkSum。
/// </summary>
public class VxeBatterySourceTests
{
    /// <summary>本机实测抓到的完整响应包（17 字节）。</summary>
    private static byte[] RealResponse =>
    [
        0x08, 0x04, 0x00, 0x00, 0x00, 0x02, 0x41, 0x00, 0x0F, 0x7F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x78,
    ];

    [Fact]
    public void 查询包应与实测抓包字节一致()
    {
        var packet = VxeBatterySource.BuildQuery(4);

        packet.Should().HaveCount(17);
        packet[0].Should().Be(0x08); // 报告 ID
        packet[1].Should().Be(0x04); // commandId = GetBatteryLevel
        packet[16].Should().Be(0x49); // 校验和（实测值）
    }

    [Fact]
    public void 解析实测响应应得到65电量()
    {
        var parsed = VxeBatterySource.ParseResponse(RealResponse);

        parsed.Should().NotBeNull();
        parsed!.Value.Level.Should().Be(0x41); // 65%
        parsed.Value.Charging.Should().BeFalse(); // 包字节6 = 0x00 放电
    }

    /// <summary>修改某个字节后重算校验和，保持包仍合法。</summary>
    private static void SetByte(byte[] input, int index, byte value)
    {
        input[index] = value;
        var sum = 8;
        for (var i = 1; i <= 15; i++) sum += input[i];
        input[16] = (byte)(85 - (sum & 255));
    }

    [Fact]
    public void 充电状态为1时应判为充电中()
    {
        var input = (byte[])RealResponse.Clone();
        SetByte(input, 7, 0x01); // 包偏移6 = 数组索引7

        var parsed = VxeBatterySource.ParseResponse(input);

        parsed.Should().NotBeNull();
        parsed!.Value.Charging.Should().BeTrue();
    }

    [Fact]
    public void 校验和错误时应返回null_不能用脏数据冒充读数()
    {
        var input = (byte[])RealResponse.Clone();
        input[16] = 0x00; // 破坏校验和（包偏移15 = 数组索引16）

        VxeBatterySource.ParseResponse(input).Should().BeNull();
    }

    [Fact]
    public void 命令码不匹配时应返回null()
    {
        var input = (byte[])RealResponse.Clone();
        SetByte(input, 1, 0x63); // 非 GetBatteryLevel

        VxeBatterySource.ParseResponse(input).Should().BeNull();
    }

    [Fact]
    public void 电量越界时应返回null()
    {
        var input = (byte[])RealResponse.Clone();
        SetByte(input, 6, 0xC8); // 200%，非法

        var parsed = VxeBatterySource.ParseResponse(input);
        parsed.Should().NotBeNull("电量越界时电平为 null 但充电状态仍可读");
        parsed!.Value.Level.Should().BeNull();
    }

    [Fact]
    public void 报告长度不足时应返回null()
    {
        VxeBatterySource.ParseResponse([0x08, 0x04]).Should().BeNull();
    }

    [Fact]
    public void 应从接口路径抠出VID()
    {
        VxeBatterySource.ExtractVid(@"\\?\hid#vid_3554&pid_f58a&mi_01&col05#7&3a0b1379&0&0004#{...}")
            .Should().Be("3554");
        VxeBatterySource.ExtractVid("VID_046D&PID_C338").Should().Be("046D");
        VxeBatterySource.ExtractVid(null).Should().BeNull();
        VxeBatterySource.ExtractVid("没有vid").Should().BeNull();
    }

    [Fact]
    public void 应按VID判断是否COMPX鼠标()
    {
        VxeBatterySource.IsCompxMouse(@"HID\VID_3554&PID_F58A&MI_02\7&11A065B0&0&0000", "3554")
            .Should().BeTrue();
        VxeBatterySource.IsCompxMouse(@"HID\VID_046D&PID_C338&MI_01", "3554")
            .Should().BeFalse();
        VxeBatterySource.IsCompxMouse(null, "3554").Should().BeFalse();
    }

    [Fact]
    public void 真实设备应能读到电量()
    {
        // 本机 VXE R1 挂在 FF02 集合上，实测返回 65%（v2.1.3 实测 0x50 = 80%）。
        // 换机器（没有 COMPX 鼠标）时返回 null，不算失败。
        var level = VxeBatterySource.ReadBatteryPercent();
        if (level is not null)
            level.Should().BeInRange(0, 100);
    }

    /// <summary>
    /// v2.1.3 事故：同时存在两台 Compx 设备时，电量从「能读」退化成「未知」。
    ///
    /// 实测环境：pid_f58a（真鼠标，应答 0x50=80%）与 pid_f58c（残留枚举项，
    /// CreateFile 报 ERROR_FILE_NOT_FOUND=2）都带 col05，而枚举顺序把 f58c 排在前面。
    /// 旧实现只取 FirstOrDefault，恰好选中打不开的那台，整个读取返回 null。
    /// </summary>
    [Fact]
    public void 第一台设备打不开时应继续试下一台()
    {
        // 第一台返回 null 模拟「CreateFile 失败」，第二台给出真实读数
        var tried = new List<string>();
        var paths = new[] { @"\\?\hid#vid_3554&pid_f58c&col05", @"\\?\hid#vid_3554&pid_f58a&col05" };

        var result = VxeBatterySource.FirstAnswering(paths, p =>
        {
            tried.Add(p);
            return p.Contains("f58a") ? 80 : (int?)null;
        });

        result.Should().Be(80, "旧实现只问第一台就放弃，会返回 null 让界面显示「未知」");
        tried.Should().HaveCount(2, "第一台失败后必须继续试第二台");
    }

    [Fact]
    public void 全部设备都不应答时应返回null而不是报错()
    {
        var tried = new List<string>();
        var result = VxeBatterySource.FirstAnswering(
            new[] { "a", "b", "c" },
            p => { tried.Add(p); return (int?)null; });

        result.Should().BeNull("读不到就是读不到，不能编造数字");
        tried.Should().HaveCount(3, "所有候选都应试过");
    }

    [Fact]
    public void 第一台就应答时不应再去打扰后面的设备()
    {
        // 每次查询要 sleep 330ms，无谓的探测会让外设页明显变慢
        var tried = new List<string>();
        var result = VxeBatterySource.FirstAnswering(
            new[] { "a", "b" },
            p => { tried.Add(p); return (int?)80; });

        result.Should().Be(80);
        tried.Should().ContainSingle();
    }

    [Fact]
    public void 键盘集合应排在鼠标集合之后()
    {
        var paths = VxeBatterySource.FindCompxPaths();
        if (paths.Count < 2) return;

        // 带 \kbd 的是键盘集合，排后面可以少试一次无效设备
        var list = paths.ToList();
        var firstKbdIndex = list.FindIndex(p => p.Contains("\\kbd", StringComparison.OrdinalIgnoreCase));
        if (firstKbdIndex < 0) return;

        list.Skip(firstKbdIndex)
            .Should().OnlyContain(p => p.Contains("\\kbd", StringComparison.OrdinalIgnoreCase),
                "非键盘集合必须排在键盘集合之前");
    }
}
