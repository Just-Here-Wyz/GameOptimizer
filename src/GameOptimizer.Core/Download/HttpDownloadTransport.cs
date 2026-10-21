namespace GameOptimizer.Core.Download;

using System.Net;
using System.Net.Http.Headers;

/// <summary>
/// 标准 HTTP 实现：只发 GET/HEAD，只用 Range 与 Content-Range。
///
/// 刻意不使用任何平台私有接口或加速通道：用户粘贴的是什么地址就下什么地址，
/// 不经过第三方中转（PCL2 镜像加速对 MC 资源站有效，对任意直链没有意义）。
/// </summary>
public sealed class HttpDownloadTransport : IDownloadTransport, IDisposable
{
    private readonly HttpClient _client;

    /// <summary>
    /// 没有超时上限：大文件可以传很久。连接与读取的停滞由 <see cref="DownloadService"/>
    /// 的读停滞超时（<see cref="DownloadSettings.ReadStallTimeoutMs"/>）负责，
    /// 这里若再套一个整请求超时，慢速大文件会被无辜掐断。
    /// </summary>
    public HttpDownloadTransport(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        var configured = timeout ?? Timeout.InfiniteTimeSpan;

        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = configured;

        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "GameOptimizer/2.2 (+https://github.com/Hex-Dragon/PCL2-inspired-downloader)");
        _client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
    }

    public async Task<DownloadResponse> OpenAsync(
        string url,
        long? rangeStart,
        long? rangeEnd,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (rangeStart is not null)
        {
            request.Headers.Range = new RangeHeaderValue(
                rangeStart,
                rangeEnd is not null && rangeEnd >= rangeStart ? rangeEnd : null);
        }

        var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        try
        {
            var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            long? length = response.Content.Headers.ContentLength;

            var status = (int)response.StatusCode;
            long? totalLength = ParseTotalLength(response.Content.Headers.ContentRange);

            if (status is 200 && totalLength is null) totalLength = length;

            return new DownloadResponse
            {
                StatusCode = status,
                ContentLength = length,
                TotalLength = totalLength,
                Content = content,
                ETag = response.Headers.ETag?.Tag,
                LastModified = response.Content.Headers.LastModified?.ToString("R"),
                Checksum = ParseChecksum(response),
                SuggestedFileName = ParseFileName(response.Content.Headers.ContentDisposition),
                AcceptRanges = response.Headers.AcceptRanges.Contains("bytes") || status is 206,
            };
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 服务端自带的完整性声明。优先 SHA-1，其次 SHA-256：
    /// 两者都是标准头，能拿到就拿来校验，拿不到就如实标注「未校验」。
    /// </summary>
    internal static DownloadChecksum? ParseChecksum(HttpResponseMessage response)
        => FromHeaders(
            response.Headers.TryGetValues("Digest", out var digest) ? digest : null,
            response.Headers.TryGetValues("Content-Digest", out var contentDigest) ? contentDigest : null);

    internal static DownloadChecksum? FromHeaders(IEnumerable<string>? digest, IEnumerable<string>? contentDigest)
    {
        foreach (var source in new[] { contentDigest, digest })
        {
            if (source is null) continue;

            foreach (var raw in source)
            {
                var parsed = ParseDigestValue(raw);
                if (parsed is not null) return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// 解析 <c>Digest: sha-1=Base64</c> / <c>sha-256=Base64</c>。
    /// RFC 3230 与 Content-Digest 的语法一致，多个值用逗号分隔。
    /// </summary>
    internal static DownloadChecksum? ParseDigestValue(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0) continue;

            var kind = part[..index].Trim().ToLowerInvariant();
            if (kind is not ("sha-1" or "sha" or "sha-256")) continue;

            var value = part[(index + 1)..].Trim().Trim('"');
            if (value.Length == 0) continue;

            // 有些服务端直接给十六进制，有些按规范给 Base64（含冒号时是 ：「base64」）
            var colon = value.LastIndexOf(':');
            if (colon >= 0) value = value[(colon + 1)..];

            var hex = ToHexOrNull(value);
            if (hex is null) continue;

            return new DownloadChecksum(kind is "sha-256" ? "sha256" : "sha1", hex);
        }

        return null;
    }

    private static string? ToHexOrNull(string value)
    {
        if (value.Length > 0 && value.All(Uri.IsHexDigit))
            return value.ToLowerInvariant();

        try
        {
            var bytes = Convert.FromBase64String(value);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 Content-Disposition 提取文件名。显式声明的文件名比链接末尾那段更可信，
    /// 所以任务建立后用它能覆盖「按链接猜的名字」。
    /// </summary>
    internal static string? ParseFileName(ContentDispositionHeaderValue? disposition)
    {
        if (disposition is null) return null;

        var name = disposition.FileNameStar ?? disposition.FileName;
        if (string.IsNullOrWhiteSpace(name)) return null;

        name = name.Trim().Trim('"');

        // RFC 5987 的 filename* 已由 .NET 解码，剩余百分号编码再兜一次
        if (name.Contains('%'))
        {
            try { name = Uri.UnescapeDataString(name); }
            catch (UriFormatException) { }
        }

        return name;
    }

    /// <summary>从 Content-Range 的尾部取完整文件长度（<c>bytes 0-1023/1048576</c>）。</summary>
    internal static long? ParseTotalLength(ContentRangeHeaderValue? range)
    {
        if (range is null) return null;

        var total = range.Length;
        return total is > 0 ? total : null;
    }

    /// <summary>把带引号的 Content-Disposition 文本解析成响应头对象（仅测试用）。</summary>
    internal static ContentDispositionHeaderValue? ParseDisposition(string value)
        => ContentDispositionHeaderValue.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>主机名可解析性检查：链接填错时尽早报错，而不是等 30 秒超时。</summary>
    internal static bool IsHttpUrl(string? url) => DownloadUrl.IsValid(url);

    public void Dispose() => _client.Dispose();
}
