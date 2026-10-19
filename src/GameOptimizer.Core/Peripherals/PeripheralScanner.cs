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

            return new PeripheralScanResult(PeripheralAnalyzer.Build(nodes), null);
        }
        catch (Exception ex)
        {
            return PeripheralScanResult.Failed($"扫描外设失败：{ex.Message}");
        }
    }
}
