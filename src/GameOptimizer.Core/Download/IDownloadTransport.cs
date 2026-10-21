namespace GameOptimizer.Core.Download;

/// <summary>
/// 实际发送 HTTP 请求的那一层，抽成接口只为**可测**：
/// 服务端不给 Range、中途断流、返回 416、校验值对不上这些路径，
/// 靠真网络复现代价极高，而重试与分片的逻辑恰恰全在这些边缘路径上。
///
/// 实现方（<see cref="HttpDownloadTransport"/>）只负责「发请求、返回响应头 + 一个流」，
/// 分片区间、写盘、重试、校验都在 <see cref="DownloadService"/> 里，因此可单测。
/// </summary>
public interface IDownloadTransport
{
    /// <summary>
    /// 打开一个分片请求。
    /// <paramref name="rangeStart"/> 为 null 表示不要 Range（探测/单流下载）。
    /// 返回的响应流只包含本次请求的字节，调用方负责释放。
    /// </summary>
    Task<DownloadResponse> OpenAsync(
        string url,
        long? rangeStart,
        long? rangeEnd,
        CancellationToken token);
}

/// <summary>一次请求的响应。流由调用方释放。</summary>
public sealed class DownloadResponse : IDisposable
{
    public required int StatusCode { get; init; }

    /// <summary>响应体长度；未知时为 null。</summary>
    public long? ContentLength { get; init; }

    /// <summary>
    /// 完整文件长度（来自 206 响应的 <c>Content-Range: bytes 0-0/1234</c> 尾部）。
    /// 分片规划靠它，不能拿 ContentLength 顶替——那只代表本次区间。
    /// </summary>
    public long? TotalLength { get; init; }

    /// <summary>响应体流。</summary>
    public required Stream Content { get; init; }

    public string? ETag { get; init; }
    public string? LastModified { get; init; }

    /// <summary>响应声明的 SHA1 / SHA-256（有则用于校验，没有则如实标注「服务端未提供」）。</summary>
    public DownloadChecksum? Checksum { get; init; }

    /// <summary>Content-Disposition 里的建议文件名；没有则为 null。</summary>
    public string? SuggestedFileName { get; init; }

    public bool AcceptRanges { get; init; }
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>416：本地记录的断点已经和服务端对不上（文件变了或区间越界）。</summary>
    public bool IsRangeNotSatisfiable => StatusCode == 416;

    public void Dispose() => Content.Dispose();
}
