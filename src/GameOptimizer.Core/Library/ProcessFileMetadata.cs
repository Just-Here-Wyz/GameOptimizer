namespace GameOptimizer.Core.Library;

using System.Diagnostics;

/// <summary>
/// 从可执行文件里读出来的版本信息。全部字段都可能为 null——
/// 无版本资源、文件被占用、路径在受保护目录下都会读不到，这不是错误，
/// 只是「拿不到就以拿不到的方式显示」。
/// </summary>
public sealed record ProcessFileMetadata(
    string? FileDescription,
    string? ProductName,
    string? CompanyName,
    string? FileVersion)
{
    public static ProcessFileMetadata Empty { get; } = new(null, null, null, null);

    public bool HasAny =>
        FileDescription is not null || ProductName is not null || CompanyName is not null || FileVersion is not null;
}

/// <summary>
/// 进程可执行文件 → 版本信息（文件描述 / 产品名称 / 公司名 / 版本）。
///
/// 为什么走版本资源而不是查表：进程名（cc-switch、HwMdcCenter、0dcloud 这类）
/// 对人毫无意义，而 exe 自带的版本资源是<b>厂商自己写进去的事实</b>，
/// 比按名字猜用途可靠得多。
///
/// 三条硬约束：
/// 1. **绝不抛异常**：读不到就返回 <see cref="ProcessFileMetadata.Empty"/>，
///    一个进程读失败不能让整个白名单加载失败；
/// 2. **不做签名校验**：验签要走 WinVerifyTrust 与证书链，成本高且要联网，
///    本次只展示「产品信息里的公司名」，界面会标明来源是产品信息而不是签名；
/// 3. **按路径缓存**：同一 exe 被反复读取时不再碰磁盘。
/// </summary>
public static class ProcessFileMetadataReader
{
    private static readonly Dictionary<string, ProcessFileMetadata> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>读取版本信息；任何失败都退化为空对象。</summary>
    public static ProcessFileMetadata Read(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return ProcessFileMetadata.Empty;

        lock (Gate)
        {
            if (Cache.TryGetValue(executablePath, out var cached)) return cached;
        }

        var metadata = ReadCore(executablePath);

        lock (Gate)
        {
            Cache[executablePath] = metadata;
        }

        return metadata;
    }

    private static ProcessFileMetadata ReadCore(string path)
    {
        try
        {
            // File.Exists 对受保护目录也可能返回 false，此时直接放弃，不抛
            if (!File.Exists(path)) return ProcessFileMetadata.Empty;

            var info = FileVersionInfo.GetVersionInfo(path);
            return new ProcessFileMetadata(
                Clean(info.FileDescription),
                Clean(info.ProductName),
                Clean(info.CompanyName),
                Clean(info.FileVersion));
        }
        catch
        {
            // 文件被独占、权限不足、不是合法 PE：都按「读不到」处理
            return ProcessFileMetadata.Empty;
        }
    }

    /// <summary>去掉空白；空串归为 null，界面据此决定是否显示该行。</summary>
    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>清空缓存。仅供测试使用。</summary>
    internal static void ClearCache()
    {
        lock (Gate) Cache.Clear();
    }
}
