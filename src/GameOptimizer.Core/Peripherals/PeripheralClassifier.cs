namespace GameOptimizer.Core.Peripherals;

/// <summary>
/// 把设备树里的零散信息归类成「鼠标 / 键盘 / 耳机」。
///
/// 全部是纯函数，方便用真机抓到的实例 ID 写成回归用例：
/// 本机的 `HID\VID_3554&amp;PID_F58A&amp;MI_02` 其实是鼠标，
/// 而 `FTCS100` 触摸板绝不能算进鼠标。
/// </summary>
public static class PeripheralClassifier
{
    /// <summary>HID 用途页：0x01 通用桌面，0x0C 消费类，0x0D 数字化仪。</summary>
    private const string GenericDesktopPage = "UP:0001";
    private const string ConsumerPage = "UP:000C";
    private const string DigitizerPage = "UP:000D";

    /// <summary>
    /// 从 HID 硬件 ID 判断类别。
    /// 触摸屏 / 触摸板走 0x0D 数字化仪页，不算鼠标；手柄走 0x05 游戏控制器页，同样排除。
    /// </summary>
    public static PeripheralKind? FromHidHardwareIds(IEnumerable<string> hardwareIds, string className = "")
    {
        var ids = hardwareIds.Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();

        // 设备树里的「鼠标 / 键盘」类设备优先按类判定：有些节点的硬件 ID 读不到，
        // 但类名可靠（实测 HID\VID_3554&PID_F58A&MI_02 的 HardwareIds 为空）。
        if (className.Equals("Mouse", StringComparison.OrdinalIgnoreCase)) return PeripheralKind.Mouse;
        if (className.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)) return PeripheralKind.Keyboard;

        if (ids.Length == 0) return null;

        var joined = string.Join("|", ids);

        // 数字化仪（触摸屏、触摸板、笔）一律排除
        if (joined.Contains(DigitizerPage, StringComparison.OrdinalIgnoreCase)) return null;
        // 游戏控制器 / 摇杆不是本页关心的外设
        if (joined.Contains("UP:0005", StringComparison.OrdinalIgnoreCase)) return null;

        // 设备树上「鼠标」类设备：主类 0x01 用法 0x02；键盘：用法 0x06；小键盘 0x07
        if (joined.Contains("UP:0001_U:0002", StringComparison.OrdinalIgnoreCase)) return PeripheralKind.Mouse;
        if (joined.Contains("UP:0001_U:0004", StringComparison.OrdinalIgnoreCase)) return PeripheralKind.Mouse;
        if (joined.Contains("UP:0001_U:0006", StringComparison.OrdinalIgnoreCase)) return PeripheralKind.Keyboard;
        if (joined.Contains("UP:0001_U:0007", StringComparison.OrdinalIgnoreCase)) return PeripheralKind.Keyboard;

        if (joined.Contains("MOUSE", StringComparison.OrdinalIgnoreCase)) return PeripheralKind.Mouse;
        if (joined.Contains("KEYBOARD", StringComparison.OrdinalIgnoreCase)) return PeripheralKind.Keyboard;

        // 消费类用法（音量键、播放键）**不**能代表耳机：
        // 键盘上的媒体键同样带 0x0C 用途页，实测会把 G610 误判成耳机。
        // 耳机只从音频端点识别（见 PeripheralAnalyzer.BuildHeadset）。
        return null;
    }

    /// <summary>端点名字是否属于耳机可用的音频端点。</summary>
    public static bool IsHeadsetEndpoint(string? endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName)) return false;

        return endpointName.Contains("耳机", StringComparison.Ordinal)
            || endpointName.Contains("耳麦", StringComparison.Ordinal)
            || endpointName.Contains("Headphone", StringComparison.OrdinalIgnoreCase)
            || endpointName.Contains("Headset", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从设备实例 ID 判断连接方式。
    ///
    /// 蓝牙枚举出来的设备必然是无线；USB 设备只能是「有线」或「走 USB 接收器的无线」，
    /// 后者 Windows 不暴露任何信号可区分，因此这里只按硬件连接给出有线/蓝牙无线，其余留给电量信息补判。
    /// </summary>
    public static PeripheralConnection FromBus(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return PeripheralConnection.Unknown;

        if (instanceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)
            || instanceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase))
            return PeripheralConnection.Wireless;

        if (instanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            || instanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase)
            || instanceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)
            // 笔记本内置键鼠、板载声卡（圆孔耳机）都是直连的
            || instanceId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)
            || instanceId.StartsWith("INTELAUDIO", StringComparison.OrdinalIgnoreCase)
            || instanceId.StartsWith("HDAUDIO", StringComparison.OrdinalIgnoreCase)
            || instanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            return PeripheralConnection.Wired;

        return PeripheralConnection.Unknown;
    }

    /// <summary>
    /// 读到电量说明设备自带电池 —— 接收器式无线鼠标正是这种情况：
    /// 系统把它挂在 USB 下（看起来像有线），却有剩余电量。
    /// 这条规则就是「有线 / 无线」判定的最终依据。
    /// </summary>
    public static PeripheralConnection Resolve(
        PeripheralConnection byBus,
        int? batteryPercent,
        bool hasBatteryDeviceNode)
    {
        if (batteryPercent is not null || hasBatteryDeviceNode) return PeripheralConnection.Wireless;
        return byBus;
    }

    /// <summary>
    /// 产品名里的连接线索。
    ///
    /// 实测本机的 `VXE NordicMouse 1K Dongle` 挂在 USB 下、读不到电量，
    /// 但名字里的 Dongle 说明它是一只走 USB 接收器的无线鼠标 ——
    /// 这时如果老实按总线判成「有线」，界面就会把一只无线鼠标标成有线，属于错误信息。
    /// </summary>
    public static PeripheralConnection? FromProductName(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName)) return null;

        string[] wirelessHints =
        [
            "Dongle", "Receiver", "接收器", "Wireless", "无线", "BT", "Bluetooth",
            "2.4G", "Wi-Fi", "WIFI", "Air", "TWS",
        ];

        foreach (var hint in wirelessHints)
        {
            // BT 太短，容易撞到别的单词，只匹配独立词
            if (hint == "BT")
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(productName, @"\bBT\b")) return PeripheralConnection.Wireless;
                continue;
            }

            if (productName.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return PeripheralConnection.Wireless;
        }

        // 名字里明确写「有线」「Wired」时才判有线
        if (productName.Contains("Wired", StringComparison.OrdinalIgnoreCase)
            || productName.Contains("有线", StringComparison.Ordinal))
            return PeripheralConnection.Wired;

        return null;
    }

    /// <summary>
    /// 正则化型号名：去掉驱动加的前缀与多余空白。
    /// 实测 `Gaming Keyboard G610` 要显示成 `G610`，用户才知道是哪一款。
    /// 安装包设备描述形如 `@oem141.inf,%g610name%;G610`，取最后一段。
    /// </summary>
    public static string CleanModel(string? raw, string fallback = "未知型号")
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;

        var text = raw.Trim().TrimStart('@');

        if (text.Contains(';'))
        {
            var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) text = parts[^1];
        }

        text = text.Replace("符合 HID 标准的", "", StringComparison.Ordinal)
                   .Replace("HID-compliant", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("Gaming Keyboard", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("Keyboard Device", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("Composite Device", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("USB Input Device", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("USB 输入设备", "", StringComparison.Ordinal)
                   .Replace("  ", " ")
                   .Trim(' ', '-', '_');

        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    /// <summary>
    /// 从音频端点名里取型号。
    /// 实测 `耳机 (DM02)` → `DM02`；`圆孔耳机 (Audio Device)` → `圆孔耳机`（括号里是驱动描述，不是型号）。
    /// </summary>
    public static string HeadsetModel(string? endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName)) return "未知型号";

        var text = endpointName.Trim();
        var open = text.IndexOf('(');
        var close = text.IndexOf(')');
        if (open > 0 && close > open)
        {
            var inner = text[(open + 1)..close].Trim();
            // 括号里是通用驱动描述时，退回括号外的名字
            if (!string.IsNullOrWhiteSpace(inner)
                && !inner.Contains("Audio Device", StringComparison.OrdinalIgnoreCase)
                && !inner.Contains("音频设备", StringComparison.Ordinal))
            {
                return inner;
            }
            text = text[..open].Trim();
        }

        return string.IsNullOrWhiteSpace(text) ? "未知型号" : text;
    }

    /// <summary>
    /// 耳机厂商：只能从型号猜，猜不到就留空。
    /// `DM02` 这类纯型号无法反查品牌，界面显示「—」比编一个品牌诚实。
    /// </summary>
    public static string? HeadsetManufacturer(string? endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName)) return null;

        var known = new (string Keyword, string Brand)[]
        {
            ("AirPods", "Apple"),
            ("Sony", "Sony"),
            ("WH-", "Sony"),
            ("WF-", "Sony"),
            ("Bose", "Bose"),
            ("HyperX", "HyperX"),
            ("Razer", "Razer"),
            ("SteelSeries", "SteelSeries"),
            ("JBL", "JBL"),
            ("漫步者", "漫步者"),
            ("EDIFIER", "漫步者"),
            ("小米", "Xiaomi"),
            ("华为", "HUAWEI"),
            ("HUAWEI", "HUAWEI"),
            ("Redmi", "Redmi"),
            ("SoundCore", "Anker"),
            ("QCY", "QCY"),
        };

        foreach (var (keyword, brand) in known)
        {
            if (endpointName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return brand;
        }

        return null;
    }

    /// <summary>去掉宽度为零、位置随机的设备实例后缀，避免同一台设备被算成两台。</summary>
    public static string StableKey(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return "";

        var text = instanceId.Trim().ToUpperInvariant();

        // 键盘有 \kbd 之类的子接口后缀
        var kbd = text.IndexOf(@"\KBD", StringComparison.Ordinal);
        if (kbd > 0) text = text[..kbd];

        // COL01 / COL02 是同一个物理设备的不同集合
        var col = text.IndexOf("&COL", StringComparison.Ordinal);
        if (col > 0) text = text[..col];

        // MI_0x 是复合设备的接口号，同一只鼠标会拆成 MI_00 / MI_01 / MI_02
        var mi = text.IndexOf("&MI_", StringComparison.Ordinal);
        if (mi > 0) text = text[..mi];

        return text;
    }
}
