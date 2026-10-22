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
///    它是一只鼠标，不能因为多了一个键盘集合就变成两台设备。
/// 4. 虚拟 HID 框架（VHF）会把 `HID VHF Driver` 塞进键盘列表，必须剔除。
/// 5. 耳机只从音频端点识别。HID 消费类集合（音量键）不代表耳机，
///    键盘上的媒体键也会带同样的用途页。
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

        // 顺序固定为 鼠标 → 键盘 → 耳机，界面不用再排
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
        // 同一物理设备 → 它上报的所有 (类别, 节点)
        var grouped = new Dictionary<string, List<(PeripheralKind Kind, PeripheralNode Node)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            if (IsTouchDevice(node) || IsVirtualDevice(node)) continue;

            var kind = PeripheralClassifier.FromHidHardwareIds(node.HardwareIds, node.ClassName);
            if (kind is not { } k) continue;

            // 笔记本自带键盘（ACPI 下直连主板的 PS/2 / I2C 键盘）不展示，
            // 外接键盘挂在 USB / BTHENUM 下不受影响
            if (k == PeripheralKind.Keyboard && IsBuiltInKeyboard(node)) continue;

            var key = DeviceKey(node);
            if (!grouped.TryGetValue(key, out var list))
            {
                list = [];
                grouped[key] = list;
            }
            list.Add((k, node));
        }

        var result = new List<PeripheralDevice>();

        foreach (var group in grouped.Values)
        {
            var nodesOfDevice = group.Select(g => g.Node).ToList();
            var kind = ResolveKind(group);

            // 同一物理设备只算一台：优先取「有型号」的节点作代表
            var primary = group
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
            if (string.IsNullOrWhiteSpace(cleaned)) continue;

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

            result.Add(new PeripheralDevice(
                kind,
                cleaned,
                group.Select(g => g.Node.Manufacturer).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                connection,
                battery,
                source,
                primary.InstanceId));
        }

        return result;
    }

    /// <summary>
    /// 一台物理设备同时上报鼠标与键盘时该算哪一类。
    /// 按产品名判断：`VXE NordicMouse 1K Dongle` 带 Mouse → 算鼠标；
    /// `Gaming Keyboard G610` → 算键盘；名字看不出来时按鼠标优先
    /// （复合接收器的键盘集合通常只是快捷键，主用途是鼠标）。
    /// </summary>
    private static PeripheralKind ResolveKind(List<(PeripheralKind Kind, PeripheralNode Node)> group)
    {
        var kinds = group.Select(g => g.Kind).Distinct().ToList();
        if (kinds.Count == 1) return kinds[0];

        var name = string.Join(" ", group.Select(g => g.Node.HidProduct ?? g.Node.FriendlyName ?? ""));
        if (name.Contains("keyboard", StringComparison.OrdinalIgnoreCase)
            || name.Contains("键盘", StringComparison.Ordinal))
            return PeripheralKind.Keyboard;

        if (name.Contains("mouse", StringComparison.OrdinalIgnoreCase)
            || name.Contains("鼠标", StringComparison.Ordinal))
            return PeripheralKind.Mouse;

        return kinds.Contains(PeripheralKind.Mouse) ? PeripheralKind.Mouse : kinds[0];
    }

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
            endpoint.InstanceId);
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
}
