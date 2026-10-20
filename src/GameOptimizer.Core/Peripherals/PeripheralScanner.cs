namespace GameOptimizer.Core.Peripherals;

/// <summary>
/// 外设扫描：鼠标 / 键盘 / 耳机的具体型号与剩余电量。
///
/// 只用 Windows 自带接口，不装驱动、不引第三方包：
/// - 设备树与电池属性：`SetupDi*` + `CM_Get_DevNode_Property`（等价于 Get-PnpDevice）
/// - 型号名：`HidD_GetProductString`，设备自己报的型号比驱动描述准
/// - 有线 / 无线：蓝牙枚举 = 无线；读到电量 = 自带电池的无线设备
///
/// 明确做不到的：多数键鼠电量走厂商私有协议（罗技 HID++、VXE 私有命令），
/// Windows 不提供统一接口，读不到就显示「未知」，绝不编造数字。
/// </summary>
public sealed class PeripheralScanner
{
    /// <summary>
    /// 扫描全部外设。任何一步失败都不抛异常，返回带错误说明的结果，
    /// 界面照常显示其余设备。
    /// </summary>
    public PeripheralScanResult Scan()
    {
        try
        {
            var nodes = DeviceTree.Enumerate()
                .Select(n => new PeripheralNode(
                    n.InstanceId,
                    n.ClassName,
                    n.FriendlyName,
                    n.BusReportedDesc,
                    n.DeviceDesc,
                    n.HardwareIds,
                    n.ParentInstanceId,
                    n.ContainerId,
                    n.BluetoothBattery,
                    n.BluetoothBattery is null ? null : "蓝牙电量上报",
                    n.HidProduct,
                    n.HidManufacturer))
                .ToList();

            var devices = PeripheralAnalyzer.Build(nodes);

            // 实时读取 VXE/ATK（Compx）鼠标电量（私有 HID 协议），
            // 覆盖扫描快照里读不到的电量；失败保持原样显示「未知」
            try
            {
                var vxeLevel = VxeBatterySource.ReadBatteryPercent();
                if (vxeLevel is not null)
                {
                    var compxVid = VxeBatterySource.FindCompxVid();
                    devices = devices.Select(d =>
                        d.Kind == PeripheralKind.Mouse
                        && d.BatteryPercent is null
                        && VxeBatterySource.IsCompxMouse(d.InstanceHint, compxVid)
                            ? d with { BatteryPercent = vxeLevel, BatterySource = "Compx 私有协议" }
                            : d).ToList();
                }
            }
            catch
            {
                // 实时读取失败不影响其余外设展示
            }

            return new PeripheralScanResult(devices, null);
        }
        catch (Exception ex)
        {
            return PeripheralScanResult.Failed($"扫描外设失败：{ex.Message}");
        }
    }
}
