namespace GameOptimizer.Core.Peripherals;

using System.Text.RegularExpressions;

/// <summary>
/// 把设备树里的零散信息归类成「鼠标 / 键盘 / 耳机」。
///
/// 全部是纯函数，方便用真机抓到的实例 ID 写成回归用例：
/// 本机的 `HID\VID_3554&amp;PID_F58A&amp;MI_02` 其实是鼠标，
/// 而 `FTCS100` 触摸板绝不能算进鼠标。
///
/// **类型判定只认结构化证据，且按可靠度排序**：
/// 1. HID 硬件 ID 里的「用途页 + 用法」对，例如 `HID_DEVICE_UP:0001_U:0006`；
///    页与用法必须成对解释 —— 只看用法数值（0x06）会把厂商页里的同名用法也算成键盘；
/// 2. HID 系统集合 ID（`HID_DEVICE_SYSTEM_KEYBOARD` / `_MOUSE`），Windows 给启动集合打的标识；
/// 3. 设备类（`SPDRP_CLASS`：Keyboard / Mouse），驱动给的分类同样是结构化信息；
/// 4. 只有描述文字时不许拍板：结论降级为「未知设备」，并把「只有描述文字」写进依据。
///
/// 名称里的 `Mouse` / `Keyboard` 只作为**复合设备判定主功能**的辅助线索，
/// 绝不单独决定类型 —— 一只叫 `Gaming Keyboard` 的键盘带宏接口时不该变成鼠标。
/// </summary>
public static class PeripheralClassifier
{
    /// <summary>HID 用途页：0x01 通用桌面，0x05 游戏控制，0x0C 消费类，0x0D 数字化仪。</summary>
    private const int GenericDesktopPage = 0x01;
    private const int GameControlsPage = 0x05;
    private const int DigitizerPage = 0x0D;

    /// <summary>通用桌面用途：0x01 指针、0x02 鼠标、0x04 摇杆、0x05 手柄、0x06 键盘、0x07 小键盘、0x08 多轴。</summary>
    private const int PointerUsage = 0x01;
    private const int MouseUsage = 0x02;
    private const int JoystickUsage = 0x04;
    private const int GamePadUsage = 0x05;
    private const int KeyboardUsage = 0x06;
    private const int KeypadUsage = 0x07;
    private const int MultiAxisUsage = 0x08;

    /// <summary>硬件 ID 里的用途对：`HID_DEVICE_UP:0001_U:0006` / `HID\VID_046D&amp;UP:0001_U:0006`。</summary>
    private static readonly Regex UsagePattern = new(
        @"UP:([0-9A-Fa-f]{4})_U:([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    /// <summary>一条 HID 用途页 / 用法。两者必须成对理解，不能只比用法数值。</summary>
    public readonly record struct HidUsage(int Page, int Usage)
    {
        public string Describe() => $"用途页 0x{Page:X2} / 用法 0x{Usage:X2}";
    }

    /// <summary>
    /// 从硬件 ID 里取出全部用途对。`HID_DEVICE_UPR:FF00-FFFF` 这类用途范围不会被误匹配。
    /// </summary>
    public static IReadOnlyList<HidUsage> ParseUsages(IEnumerable<string>? hardwareIds)
    {
        if (hardwareIds is null) return [];

        var found = new List<HidUsage>();

        foreach (var id in hardwareIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;

            foreach (Match match in UsagePattern.Matches(id))
            {
                var usage = new HidUsage(
                    Convert.ToInt32(match.Groups[1].Value, 16),
                    Convert.ToInt32(match.Groups[2].Value, 16));

                if (!found.Contains(usage)) found.Add(usage);
            }
        }

        return found;
    }

    /// <summary>
    /// 给单个设备节点定性。返回 null 表示「确定不是本页关心的外设」（触摸板、手柄、虚拟/消费类集合）。
    ///
    /// 参数说明：<paramref name="description"/> 传驱动描述（`DeviceDesc`）优先，
    /// 其次友好名；文字只在完全没有结构化证据时才参与判定，且只会得出「未知设备」。
    /// </summary>
    public static PeripheralInterface? ClassifyInterface(
        IEnumerable<string>? hardwareIds,
        string? className = "",
        string? description = null)
    {
        var usages = ParseUsages(hardwareIds);

        // 数字化仪（触摸板 / 触摸屏 / 笔）一律排除，优先级高于设备类 ——
        // 触摸板在 Windows 里就是 Mouse 类，先看类名会把它算成鼠标。
        if (usages.Any(u => u.Page == DigitizerPage)) return null;

        var keyboard = usages
            .Where(u => u.Page == GenericDesktopPage && u.Usage is KeyboardUsage or KeypadUsage)
            .ToList();

        var mouse = usages
            .Where(u => u.Page == GenericDesktopPage && u.Usage is PointerUsage or MouseUsage)
            .ToList();

        // 手柄 / 摇杆：游戏控制页 0x05，或通用桌面的 0x04 / 0x05 / 0x08。
        // 注意 0x04 是**摇杆**不是鼠标，早期版本把它当成鼠标用过。
        var controller = usages
            .Where(u => u.Page == GameControlsPage
                        || (u.Page == GenericDesktopPage
                            && u.Usage is JoystickUsage or GamePadUsage or MultiAxisUsage))
            .ToList();

        // 键盘上的摇杆集合不该带走整台键盘，所以只有「一条键鼠用法都没有」时才排除
        if (controller.Count > 0 && keyboard.Count == 0 && mouse.Count == 0) return null;

        if (keyboard.Count > 0 || mouse.Count > 0)
        {
            var kinds = new List<PeripheralKind>();
            if (keyboard.Count > 0) kinds.Add(PeripheralKind.Keyboard);
            if (mouse.Count > 0) kinds.Add(PeripheralKind.Mouse);

            var parts = new List<string>();
            if (keyboard.Count > 0) parts.Add($"{keyboard[0].Describe()}（键盘）");
            if (mouse.Count > 0) parts.Add($"{mouse[0].Describe()}（鼠标）");

            var basis = "HID " + string.Join(" + ", parts);

            // 类名与用途冲突时以用途为准，但必须把冲突写进依据，别让界面看起来毫无矛盾
            if (ClassToKind(className) is { } byClass && !kinds.Contains(byClass))
                basis += $"；设备类写的是 {className}，与用途不一致，以用途为准";

            return new PeripheralInterface(kinds, basis);
        }

        // 用途页读不到时，HID 系统集合 ID 同样是结构化信息：
        // `HID_DEVICE_SYSTEM_KEYBOARD` 是 Windows 给「启动键盘」集合打的标识，不是产品名。
        var ids = (hardwareIds ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();
        var systemKeyboard = ids.Any(i => i.Equals("HID_DEVICE_SYSTEM_KEYBOARD", StringComparison.OrdinalIgnoreCase));
        var systemMouse = ids.Any(i => i.Equals("HID_DEVICE_SYSTEM_MOUSE", StringComparison.OrdinalIgnoreCase));

        if (systemKeyboard || systemMouse)
        {
            var kinds = new List<PeripheralKind>();
            var tokens = new List<string>();

            if (systemKeyboard)
            {
                kinds.Add(PeripheralKind.Keyboard);
                tokens.Add("HID_DEVICE_SYSTEM_KEYBOARD");
            }

            if (systemMouse)
            {
                kinds.Add(PeripheralKind.Mouse);
                tokens.Add("HID_DEVICE_SYSTEM_MOUSE");
            }

            return new PeripheralInterface(kinds, $"HID 系统集合 {string.Join(" + ", tokens)}");
        }

        // 用途页读不到时退到设备类：驱动把节点归到 Keyboard / Mouse 类也是可靠信号
        if (ClassToKind(className) is { } classKind)
            return new PeripheralInterface([classKind], $"设备类 {className}（硬件 ID 里没有可解析的用途页）");

        // 只剩描述文字：文字是辅助信息，不足以确认类型，按「未知设备」展示并写明依据
        if (FromTextHint(description) is { } hinted)
            return new PeripheralInterface(
                [PeripheralKind.Unknown],
                $"只有描述文字「{description!.Trim()}」（含{KindText(hinted)}字样），没有用途页或设备类佐证");

        return null;
    }

    /// <summary>
    /// 从文字里取的「功能倾向」，只用于两处：
    /// 复合设备判定主功能、以及无结构化证据时说明「为什么算未知」。
    /// **不得**用它直接定类型。
    /// </summary>
    public static PeripheralKind? FromTextHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text.Contains("keyboard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("键盘", StringComparison.Ordinal))
            return PeripheralKind.Keyboard;

        if (text.Contains("mouse", StringComparison.OrdinalIgnoreCase)
            || text.Contains("鼠标", StringComparison.Ordinal))
            return PeripheralKind.Mouse;

        return null;
    }

    /// <summary>设备类 → 类别。只认键鼠两类，其余类名不代表本页外设。</summary>
    private static PeripheralKind? ClassToKind(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;

        if (className.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)) return PeripheralKind.Keyboard;
        if (className.Equals("Mouse", StringComparison.OrdinalIgnoreCase)) return PeripheralKind.Mouse;

        return null;
    }

    private static string KindText(PeripheralKind kind) => kind switch
    {
        PeripheralKind.Keyboard => "键盘",
        PeripheralKind.Mouse => "鼠标",
        _ => "外设",
    };

    /// <summary>端点名字是否属于耳机可用的音频端点。</summary>
    public static bool IsHeadsetEndpoint(string? endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName)) return false;

        // 录音端点是耳机的「另一半」，不是另一只耳机：
        // 「耳机麦克风 (DM02)」与「耳机 (DM02)」同时存在，不去掉就会多出一台。
        if (endpointName.Contains("麦克风", StringComparison.Ordinal)
            || endpointName.Contains("Microphone", StringComparison.OrdinalIgnoreCase))
            return false;

        return endpointName.Contains("耳机", StringComparison.Ordinal)
            || endpointName.Contains("耳麦", StringComparison.Ordinal)
            || endpointName.Contains("Headphone", StringComparison.OrdinalIgnoreCase)
            || endpointName.Contains("Headset", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 音频端点是否为录音（capture）端点。
    ///
    /// MMDevice 端点 ID 是固定格式：`{0.0.0.00000000}.{GUID}` 为播放，`{0.0.1.00000000}` 为录音。
    /// 实测本机的「圆孔耳机 (Audio Device)」**播放与录音两个端点同名**、容器还不同
    /// （`{7131B19F-…}` / `{7C62E129-…}`），只看名字根本分不出来，只能靠这个前缀。
    /// </summary>
    public static bool IsCaptureEndpoint(string? instanceId)
        => instanceId?.Contains("{0.0.1.00000000}", StringComparison.OrdinalIgnoreCase) ?? false;

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
                if (Regex.IsMatch(productName, @"\bBT\b")) return PeripheralConnection.Wireless;
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

/// <summary>
/// 单个设备节点（一个 HID 接口 / 集合）的分类结论。
///
/// <see cref="Kinds"/> 超过一项表示这个节点同时报告了键盘与鼠标用法 ——
/// 由 <see cref="PeripheralAnalyzer"/> 决定怎么展示，分类器不替它拍板。
/// <see cref="Basis"/> 是人能复核的依据文本，界面会直接显示。
/// </summary>
public sealed record PeripheralInterface(IReadOnlyList<PeripheralKind> Kinds, string Basis)
{
    public PeripheralKind? Single => Kinds.Count == 1 ? Kinds[0] : null;

    public bool Has(PeripheralKind kind) => Kinds.Contains(kind);
}

