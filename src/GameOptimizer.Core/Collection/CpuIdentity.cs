namespace GameOptimizer.Core.Collection;

using Microsoft.Win32;

/// <summary>
/// CPU 型号名。读注册表 `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0`
/// 的 `ProcessorNameString`——这是 Windows 自己在「系统信息」里显示的那一行，
/// 不需要 WMI、不需要管理员，也不需要引入新包。
///
/// 读不到就返回「未知」：型号属于「读不到就不显示」的信息，
/// 绝不能拿「Intel CPU」这类占位文本糊弄过去。
/// </summary>
public static class CpuIdentity
{
    private static readonly Lazy<string> CachedModel = new(ReadModel, isThreadSafe: true);

    /// <summary>CPU 型号，例如「12th Gen Intel(R) Core(TM) i7-1360P」。读不到为「未知」。</summary>
    public static string ModelName => CachedModel.Value;

    private static string ReadModel()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
                return name.Trim();
        }
        catch
        {
            // 注册表被策略限制时不影响其余功能，界面显示「未知」
        }

        return "未知";
    }
}
