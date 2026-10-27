namespace GameOptimizer.Core.Peripherals;

/// <summary>
/// 把设备树里的原始节点整理成「鼠标 / 键盘 / 耳机」，并合并成一台设备。
///
/// 纯逻辑，不碰系统 API，因此可以用真机抓到的节点写成回归用例。
/// 下面每条规则都对应一次实机观察，不是凭想象写的：
///
/// 1. 同一只鼠标会拆成 MI_00 / MI_01 / MI_02 三个接口再加若干 HID 集合，
///    不按容器合并就会在界面上出现三条「鼠标」。
/// 2. 触摸板在 Windows 里同样归到 Mouse 类，硬件 ID 也带 UP:0001_U:0002，
///    只能靠「父设备是 ACPI」把它排除，否则笔记本会凭空多一只鼠标。
/// 3. 复合接收器（VXE NordicMouse）同时上报鼠标和键盘集合。
///    产品名能判定主功能时只出一张卡，判不出来时按接口各出一张 ——
///    但**绝不**默认成鼠标（这条兜底已删除）。
/// 4. 虚拟 HID 框架（VHF）会把 `HID VHF Driver` 塞进键盘列表，必须剔除。
/// 5. 耳机只从音频端点识别。HID 消费类集合（音量键）不代表耳机，
///    键盘上的媒体键也会带同样的用途页。
/// 6. 驱动**软件枚举**出来的设备同样是假的：罗技 G HUB 的
///    `LGHUBDevice\VID_046D&PID_C232`（服务 `logi_joy_vir_hid`）会挂出一个
///    「Logitech G HUB Virtual Keyboard」，子节点与真键盘的硬件 ID 完全一致，
///    只靠 HID 信息分不出来，必须看父设备是不是软件枚举器。
/// </summary>
public static class PeripheralAnalyzer
{
    public static IReadOnlyList<PeripheralDevice> Build(IReadOnlyList<PeripheralNode> nodes)
    {
        // 蓝牙电量挂在「同容器的另一台设备」上很常见：耳机端点自己不报电量，
        // 同容器的 BTHENUM 主设备才报。先把容器 → 电量建成索引。
        var batteryByContainer = BuildBatteryIndex(nodes);

        var devices = new List<PeripheralDevice>();
        devices.AddRange(BuildInputDevices(nodes, batteryByContainer));
        devices.AddRange(BuildHeadsets(nodes, batteryByContainer));

        // 顺序固定为 鼠标 → 键盘 → 耳机 → 未知，界面不用再排
        return devices
            .OrderBy(d => (int)d.Kind)
            .ThenBy(d => d.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<Guid, int> BuildBatteryIndex(IReadOnlyList<PeripheralNode> nodes)
    {
        var map = new Dictionary<Guid, int>();
        foreach (var node in nodes)
        {
            if (node.ContainerId is not { } container) continue;
            if (node.BatteryPercent is not { } battery) continue;

            // 同一容器可能有多个节点，取第一个读到的（都是同一个值）
            map.TryAdd(container, battery);
        }
        return map;
    }

    private static IEnumerable<PeripheralDevice> BuildInputDevices(
        IReadOnlyList<PeripheralNode> nodes,
        IReadOnlyDictionary<Guid, int> batteryByContainer)
    {
        // 同一物理设备 → 它上报的所有 (接口结论, 节点)
        var grouped = new Dictionary<string, List<(PeripheralInterface Interface, PeripheralNode Node)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            if (IsTouchDevice(node) || IsVirtualDevice(node) || IsSoftwareEnumeratedDevice(node)) continue;

            // 类型只由「用途页 / 设备类」这类结构化信息给出；
            // 驱动描述仅在没有结构化证据时用来解释「为什么算未知」。
            var iface = PeripheralClassifier.ClassifyInterface(
                node.HardwareIds, node.ClassName, node.DeviceDesc ?? node.FriendlyName ?? node.BusReportedDesc);

            if (iface is null) continue;

            // 笔记本自带键盘（ACPI 下直连主板的 PS/2 / I2C 键盘）不展示，
            // 外接键盘挂在 USB / BTHENUM 下不受影响
            if (iface.Has(PeripheralKind.Keyboard) && IsBuiltInKeyboard(node)) continue;

            var key = DeviceKey(node);
            if (!grouped.TryGetValue(key, out var list))
            {
                list = [];
                grouped[key] = list;
            }
            list.Add((iface, node));
        }

        var result = new List<PeripheralDevice>();

        foreach (var group in grouped.Values)
            result.AddRange(BuildGroupDevices(group, batteryByContainer));

        return result;
    }

    /// <summary>
    /// 一台物理设备 → 一张或两张卡片。
    ///
    /// - 只有一类接口：一张卡。
    /// - 键盘与鼠标接口同时存在（复合接收器、带指点杆的键盘、带宏接口的游戏键盘）：
    ///   1. 设备**自报的产品名**能判定主功能（`VXE NordicMouse 1K Dongle`、`Gaming Keyboard G610`）
    ///      → 按主功能出**一张**卡，依据里写明它同时提供另一类接口；
    ///   2. 判不出来 → **按接口各出一张**，各自标注类型并说明同属一台复合设备。
    ///
    /// 关键：**没有「默认鼠标」这条兜底了**。早期版本在名字里看不出类型时直接算鼠标，
    /// 结果一只键盘只要多带一个指针集合就被整台改成鼠标（本页最严重的误识别）。
    /// </summary>
    private static IReadOnlyList<PeripheralDevice> BuildGroupDevices(
        List<(PeripheralInterface Interface, PeripheralNode Node)> group,
        IReadOnlyDictionary<Guid, int> batteryByContainer)
    {
        var kinds = group.SelectMany(g => g.Interface.Kinds).Distinct().ToList();

        // 「未知」只是没有证据时的兜底：同一个设备上只要有一条结构性结论，就按结论展示
        var definitive = kinds.Where(k => k != PeripheralKind.Unknown).ToList();

        if (definitive.Count <= 1)
        {
            var kind = definitive.Count == 1 ? definitive[0] : PeripheralKind.Unknown;
            var device = CreateInputDevice(kind, group, batteryByContainer, BasisFor(group, kind), isComposite: false);
            return device is null ? [] : [device];
        }

        // 自报型号是设备自己说的话，可以当作复合设备「主功能」的辅助线索（不用于单接口设备）
        var productNames = group
            .Select(g => g.Node.HidProduct)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hint = productNames.Select(PeripheralClassifier.FromTextHint).FirstOrDefault(k => k is not null);

        if (hint is { } main && definitive.Contains(main))
        {
            var others = definitive.Where(k => k != main).Select(KindName).ToList();
            var basis = $"{BasisFor(group, main)}；同一设备还提供{string.Join(" / ", others)}接口，" +
                        $"按自报型号「{string.Join(" / ", productNames)}」展示主功能";

            var device = CreateInputDevice(main, group, batteryByContainer, basis, isComposite: true);
            return device is null ? [] : [device];
        }

        // 判不出主功能就按接口分别展示：不把整台设备强行归成鼠标
        var sharedBasis = $"复合设备：同时提供{string.Join(" 与 ", definitive.Select(KindName))}接口，" +
                          "没有自报型号可判定主功能，按接口分别展示";

        var split = new List<PeripheralDevice>();
        foreach (var kind in definitive.Where(k => k is PeripheralKind.Keyboard or PeripheralKind.Mouse))
        {
            var device = CreateInputDevice(kind, group, batteryByContainer, sharedBasis, isComposite: true);
            if (device is not null) split.Add(device);
        }

        return split;
    }

    /// <summary>按类别造一台设备。型号 / 电池 / 连接方式优先取该类别对应的节点。</summary>
    private static PeripheralDevice? CreateInputDevice(
        PeripheralKind kind,
        List<(PeripheralInterface Interface, PeripheralNode Node)> group,
        IReadOnlyDictionary<Guid, int> batteryByContainer,
        string basis,
        bool isComposite)
    {
        var candidates = group.Where(g => g.Interface.Has(kind)).ToList();
        if (candidates.Count == 0) candidates = group;

        var nodesOfDevice = candidates.Select(c => c.Node).ToList();

        // 同一物理设备只算一台：优先取「有型号」的节点作代表
        var primary = candidates
            .OrderByDescending(g => !string.IsNullOrWhiteSpace(g.Node.HidProduct))
            .ThenByDescending(g => g.Node.BatteryPercent is not null)
            .First()
            .Node;

        var model = primary.HidProduct
            ?? primary.BusReportedDesc
            ?? primary.FriendlyName
            ?? primary.DeviceDesc;

        // 驱动描述里的「USB 输入设备」不是型号，宁可显示未知也不要糊弄
        var cleaned = PeripheralClassifier.CleanModel(model, fallback: "");
        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        var battery = nodesOfDevice.Select(n => n.BatteryPercent).FirstOrDefault(v => v is not null);
        if (battery is null && primary.ContainerId is { } container)
            battery = batteryByContainer.GetValueOrDefault(container) is var b and > 0 ? b : null;

        var source = battery is null
            ? null
            : (nodesOfDevice.Select(n => n.BatterySource).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
               ?? "蓝牙电量上报");

        var byBus = PeripheralClassifier.FromBus(primary.InstanceId);
        var byName = PeripheralClassifier.FromProductName(primary.HidProduct ?? primary.BusReportedDesc);
        var connection = PeripheralClassifier.Resolve(byName ?? byBus, battery, hasBatteryDeviceNode: false);

        return new PeripheralDevice(
            kind,
            cleaned,
            group.Select(g => g.Node.Manufacturer).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            connection,
            battery,
            source,
            primary.InstanceId,
            basis,
            isComposite);
    }

    /// <summary>取该类别的分类依据：优先用报了这类接口的节点，避免把鼠标节点的依据写到键盘卡上。</summary>
    private static string BasisFor(
        List<(PeripheralInterface Interface, PeripheralNode Node)> group,
        PeripheralKind kind)
    {
        foreach (var (iface, _) in group)
        {
            if (iface.Has(kind)) return iface.Basis;
        }

        return group.Count > 0 ? group[0].Interface.Basis : "";
    }

    private static string KindName(PeripheralKind kind) => kind switch
    {
        PeripheralKind.Mouse => "鼠标",
        PeripheralKind.Keyboard => "键盘",
        PeripheralKind.Headset => "耳机",
        _ => "未知",
    };

    /// <summary>
    /// 耳机：从音频端点推。名字里带「耳机 / 耳麦 / Headphone / Headset」的端点就是耳机，
    /// 型号取端点描述（如「耳机 (DM02)」里的 DM02）。
    ///
    /// 一个端点不等于一台耳机：同一只蓝牙耳机会同时暴露 A2DP 播放端点与免手持（HFP）端点，
    /// 按端点直接列会在界面上出现两台一模一样的耳机。反过来，同时插着 3.5mm 有线耳机
    /// 和蓝牙耳机时两台都必须显示（v2.2.2 修的就是这个：原先只取第一个端点，
    /// 蓝牙耳机一连上，有线耳机就被挤掉了）。
    /// </summary>
    private static IReadOnlyList<PeripheralDevice> BuildHeadsets(
        IReadOnlyList<PeripheralNode> nodes,
        IReadOnlyDictionary<Guid, int> batteryByContainer)
    {
        // 已在列表里的耳机：归并键 + 设备本体
        var found = new List<(string Key, PeripheralDevice Device)>();

        foreach (var endpoint in nodes)
        {
            if (!endpoint.ClassName.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsVirtualDevice(endpoint)) continue;
            // 录音端点（「圆孔耳机」「耳机麦克风」）是耳机的另一半，不是另一只耳机；
            // 本机实测录音与播放端点同名且容器不同，必须靠端点 ID 前缀区分
            if (PeripheralClassifier.IsCaptureEndpoint(endpoint.InstanceId)) continue;
            if (!PeripheralClassifier.IsHeadsetEndpoint(endpoint.FriendlyName)) continue;

            var device = CreateHeadset(endpoint, batteryByContainer);

            // 同一物理设备的多个端点共享容器 ID
            var key = endpoint.ContainerId?.ToString() ?? PeripheralClassifier.StableKey(endpoint.InstanceId);

            var index = found.FindIndex(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = found.FindIndex(f => IsSameHeadset(f.Device, device));

            if (index < 0)
            {
                found.Add((key, device));
                continue;
            }

            // 同一只耳机保留「信息更全」的那条端点（有电量 > 连接方式明确）
            if (IsBetterEndpoint(device, found[index].Device))
                found[index] = (found[index].Key, device);
        }

        return found.Select(f => f.Device).ToList();
    }

    /// <summary>
    /// 容器读不到时（部分蓝牙端点没有容器属性）的兜底判重：
    /// 型号相同，或一个是另一个的前缀（`DM02` 与 `DM02 Hands-Free AG Audio`），
    /// 且连接方式一致，视为同一只耳机。
    /// </summary>
    private static bool IsSameHeadset(PeripheralDevice a, PeripheralDevice b)
    {
        if (a.Connection != b.Connection) return false;

        if (string.Equals(a.Model, b.Model, StringComparison.OrdinalIgnoreCase)) return true;

        var (shorter, longer) = a.Model.Length <= b.Model.Length
            ? (a.Model, b.Model)
            : (b.Model, a.Model);

        return shorter.Length >= 3
            && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>两个端点属于同一只耳机时，谁的结论更可信：有电量 &gt; 连接方式已知。</summary>
    private static bool IsBetterEndpoint(PeripheralDevice candidate, PeripheralDevice current)
    {
        if (candidate.BatteryPercent is not null && current.BatteryPercent is null) return true;
        if (candidate.BatteryPercent is null && current.BatteryPercent is not null) return false;

        return candidate.Connection != PeripheralConnection.Unknown
            && current.Connection == PeripheralConnection.Unknown;
    }

    /// <summary>单个音频端点 → 一台耳机。</summary>
    private static PeripheralDevice CreateHeadset(
        PeripheralNode endpoint,
        IReadOnlyDictionary<Guid, int> batteryByContainer)
    {
        var name = endpoint.FriendlyName ?? "";

        var battery = endpoint.BatteryPercent;
        if (battery is null && endpoint.ContainerId is { } container)
            battery = batteryByContainer.GetValueOrDefault(container) is var b and > 0 ? b : null;

        var byBus = PeripheralClassifier.FromBus(endpoint.ParentInstanceId ?? endpoint.InstanceId);

        // 3.5mm 圆孔 / 板载声卡上的耳机是插线的，直接显示有线；
        // 蓝牙、USB 音频端点则保持总线判定（蓝牙 = 无线，USB = 有线）
        if (byBus == PeripheralConnection.Unknown)
            byBus = PeripheralClassifier.FromBus(endpoint.InstanceId);

        var connection = PeripheralClassifier.Resolve(byBus, battery, hasBatteryDeviceNode: false);

        return new PeripheralDevice(
            PeripheralKind.Headset,
            PeripheralClassifier.HeadsetModel(name),
            PeripheralClassifier.HeadsetManufacturer(name),
            connection,
            battery,
            battery is null ? null : "蓝牙电量上报",
            endpoint.InstanceId,
            $"音频端点「{name}」名称含耳机字样");
    }

    /// <summary>
    /// 同一物理设备的归并键。
    ///
    /// 优先用容器 ID —— 但实测有陷阱：鼠标接口的容器是
    /// `{FC73BBAE-...-806E6F6E6963}`，同设备的键盘接口容器却是
    /// `{00000000-...-FFFFFFFFFFFF}` 这个「无法归组」占位值。
    /// 所以容器要用「硬件 ID 主干」补齐，两条线索一起才能合并成一整台设备。
    /// </summary>
    internal static string DeviceKey(PeripheralNode node)
    {
        // HID\VID_3554&PID_F58A&MI_02\7&11A065B0&0&0000 → HID\VID_3554&PID_F58A
        var stable = PeripheralClassifier.StableKey(node.InstanceId);

        // 容器存在时，用「容器 + 硬件 ID 主干」双键：容器相同的复合设备能合并，
        // 容器被系统判为「无法归组」的接口也能靠硬件 ID 主干并回同一台设备。
        if (node.ContainerId is { } container && !string.IsNullOrWhiteSpace(stable))
            return $"{container}|{stable}";

        return stable.Length > 0 ? stable : node.InstanceId.ToUpperInvariant();
    }

    /// <summary>
    /// 笔记本自带键盘：设备自身挂在 ACPI 树上（如 ACPI\MSFT0001 的 PS/2 键盘），
    /// 或父设备在 ACPI 下。外接键盘实例 ID 是 HID\（USB）或 BTHENUM\（蓝牙），
    /// 永远不会命中。
    /// </summary>
    internal static bool IsBuiltInKeyboard(PeripheralNode node)
        => node.ClassName.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)
           && (node.InstanceId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)
               || (node.ParentInstanceId?.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>
    /// 触摸板 / 触摸屏：Windows 把它们也归到 Mouse 类，必须排除。
    /// 判据（按可靠度排序）：
    /// 1. 硬件 ID 带数字化仪用途页 0x0D；
    /// 2. 父设备是 ACPI —— 外接鼠标永远挂在 USB / BTHENUM 下。
    /// </summary>
    private static bool IsTouchDevice(PeripheralNode node)
    {
        if (node.HardwareIds.Any(id => id.Contains("UP:000D", StringComparison.OrdinalIgnoreCase)
                                    || id.Contains("UP:0001_U:000D", StringComparison.OrdinalIgnoreCase)))
            return true;

        // 只对指针类做父设备判断：ACPI 下还挂着 PS/2 键盘，那是真键盘
        var isPointerClass = node.ClassName.Equals("Mouse", StringComparison.OrdinalIgnoreCase)
            || (node.HidProduct?.Contains("HIDI2C", StringComparison.OrdinalIgnoreCase) ?? false);

        return isPointerClass
            && (node.ParentInstanceId?.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// 纯虚拟设备：虚拟 HID 框架、UMDF 虚拟 hid 设备、虚拟音频设备。
    /// 它们会窜进键盘/耳机列表里（实测 `HID VHF Driver` 被当成键盘），必须剔除。
    /// </summary>
    private static bool IsVirtualDevice(PeripheralNode node)
    {
        var text = string.Join("|",
            node.InstanceId,
            node.FriendlyName ?? "",
            node.HidProduct ?? "",
            node.DeviceDesc ?? "");

        return text.Contains("VHF", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || text.Contains("虚拟", StringComparison.Ordinal)
            || text.Contains("UMDF", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ROOT\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 软件枚举出来的「外设」：父设备是驱动凭空创建的，后面没有一根真实总线。
    ///
    /// 真机实测（2026-10-27）：装了罗技 G HUB 之后，`LGHUBDevice\VID_046D&PID_C232`
    /// （服务 `logi_joy_vir_hid`，上游是 `ROOT\SYSTEM`）挂出一个
    /// 「Logitech G HUB Virtual Keyboard」。它的子节点与真键盘的硬件 ID 一模一样
    /// （`HID_DEVICE_SYSTEM_KEYBOARD` + `UP:0001_U:0006`），自报型号串还只有一个 `HID`，
    /// 于是外设页凭空多出一张「键盘 HID」卡片 —— 用户会以为笔记本自带键盘又冒出来了。
    /// 这种节点只能靠父设备前缀识别：软件枚举器有自己的命名空间。
    ///
    /// 判断用「黑名单」而不是「父设备必须是已知总线」：后者每漏一种总线，
    /// 就会让一只真键盘从页面上消失，代价比多一张卡大得多。
    /// </summary>
    internal static bool IsSoftwareEnumeratedDevice(PeripheralNode node)
    {
        var parent = node.ParentInstanceId;
        if (string.IsNullOrWhiteSpace(parent)) return false;

        return parent.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
            || parent.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)
            || parent.StartsWith("LGHUBDEVICE\\", StringComparison.OrdinalIgnoreCase);
    }
}
