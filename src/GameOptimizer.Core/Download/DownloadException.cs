namespace GameOptimizer.Core.Download;

/// <summary>
/// 下载失败的结构化原因。区分「值得重试」与「重试也没用」：
/// 404 / 403 重试三次只是让用户多等 1.5 秒，而 500 / 超时确实该再试。
/// </summary>
public class DownloadException : Exception
{
    public DownloadException(string message, bool isTransient = false, Exception? inner = null)
        : base(message, inner) => IsTransient = isTransient;

    /// <summary>true 表示属于网络抖动，可以按指数退避重试。</summary>
    public bool IsTransient { get; }
}

/// <summary>
/// 服务端对带 Range 的请求回了整份文件（200）。
/// 单独一个类型是因为处理方式完全不同：整个任务退回单流重下，而不是重试某个分片。
/// </summary>
public sealed class RangeIgnoredException : DownloadException
{
    public RangeIgnoredException()
        : base("服务器不支持断点续传（返回完整文件）", isTransient: false)
    {
    }
}

/// <summary>服务端返回了非 2xx。</summary>
public sealed class DownloadHttpException : DownloadException
{
    public DownloadHttpException(int statusCode, string? reason)
        : base(BuildMessage(statusCode, reason), IsRetryableStatus(statusCode))
        => StatusCode = statusCode;

    public int StatusCode { get; }

    /// <summary>408 / 429 / 5xx 可以重试；其余（3xx 落地、4xx）应直接失败并报出状态码。</summary>
    internal static bool IsRetryableStatus(int status)
        => status is 408 or 429 || status >= 500;

    private static string BuildMessage(int status, string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? "" : $" {reason.Trim()}";
        var hint = status switch
        {
            401 or 403 => "，需要登录或没有访问权限",
            404 => "，文件不存在（链接可能已失效）",
            410 => "，文件已被移除",
            416 => "，断点区间与服务器文件不匹配",
            429 => "，请求太频繁被限流",
            >= 500 => "，服务器内部错误",
            _ => "",
        };

        return $"服务器返回 {status}{text}{hint}";
    }
}
