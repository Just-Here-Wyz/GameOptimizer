namespace GameOptimizer.Core.Peripherals;

/// <summary>外设类别。只区分用户真正关心的三类。</summary>
public enum PeripheralKind
{
    Mouse = 0,
    Keyboard = 1,
    Headset = 2,
}

/// <summary>连接方式。用户要的是「有线 / 无线」两个明确结论，其余归为未知。</summary>
public enum PeripheralConnection
{
    Unknown = 0,
    Wired = 1,
    Wireless = 2,
}

/// <summary>
/// 一台外设。电量来源必须写清楚：读不到就是 null，界面显示「未知」，
/// 不能拿「上次充满」之类的猜测糊弄。
/// </summary>
public sealed record PeripheralDevice(
    PeripheralKind Kind,
    string Model,
    string? Manufacturer,
    PeripheralConnection Connection,
    int? BatteryPercent,
    string? BatterySource,
    string InstanceHint)
{
    public string KindText => Kind switch
    {
        PeripheralKind.Mouse => "鼠标",
        PeripheralKind.Keyboard => "键盘",
        _ => "耳机",
    };

    public string ConnectionText => Connection switch
    {
        PeripheralConnection.Wired => "有线",
        PeripheralConnection.Wireless => "无线",
        _ => "未知连接",
    };

    public bool IsWired => Connection == PeripheralConnection.Wired;

    /// <summary>
    /// 电量文本：有线接显示「有线」，无线但读不到电量显示「未知」，绝不编造数字。
    /// 无线又未知电量时必须给出原因，否则用户会以为程序坏了。
    /// </summary>
    public string BatteryText => IsWired
        ? "有线"
        : BatteryPercent is { } p ? $"{p}%" : "未知";

    /// <summary>电量数值（0~100），供环形电量图使用；未知时返回 null。</summary>
    public int? BatteryOrNull => IsWired ? null : BatteryPercent;
}
