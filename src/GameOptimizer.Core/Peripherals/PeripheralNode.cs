namespace GameOptimizer.Core.Peripherals;

/// <summary>外设扫描的原始素材：设备树里的一条记录。</summary>
public sealed record PeripheralNode(
    string InstanceId,
    string ClassName,
    string? FriendlyName,
    string? BusReportedDesc,
    string? DeviceDesc,
    IReadOnlyList<string> HardwareIds,
    string? ParentInstanceId = null,
    Guid? ContainerId = null,
    int? BatteryPercent = null,
    string? BatterySource = null,
    string? HidProduct = null,
    string? Manufacturer = null);

/// <summary>原始扫描结果，扫描器与分析逻辑分开，便于单测。</summary>
public sealed record PeripheralScanResult(
    IReadOnlyList<PeripheralDevice> Devices,
    string? Error)
{
    public static PeripheralScanResult Failed(string error) => new([], error);
    public bool IsEmpty => Devices.Count == 0;
}
