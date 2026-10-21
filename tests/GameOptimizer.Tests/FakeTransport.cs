namespace GameOptimizer.Tests;

using GameOptimizer.Core.Download;

/// <summary>
/// 假的 HTTP 层：把「服务端行为」做成可编程的开关。
///
/// 能模拟的真实场景：
/// <list type="bullet">
/// <item>是否支持 Range（不支持时一律回 200 + 整份数据）；</item>
/// <item>探测阶段说支持、真取分片时又回 200（现实中 CDN 回源不一致就会这样）；</item>
/// <item>传输到第 N 个字节后卡住（模拟断线 / 暂停点）；</item>
/// <item>前 N 次请求直接失败（模拟连接被拒，验证退避重试）；</item>
/// <item>第一轮数据被弄坏（验证校验失败重下）。</item>
/// </list>
/// </summary>
internal sealed class FakeTransport : IDownloadTransport
{
    private readonly byte[] _payload;
    private int _requestCount;

    public FakeTransport(byte[] payload) => _payload = payload;

    public string Url(string fileName) => $"https://fake.local/{fileName}";

    public bool SupportsRanges { get; set; } = true;

    /// <summary>探测之后所有分片请求都无视 Range、回整份文件。</summary>
    public bool IgnoreRangeAfterProbe { get; set; }

    /// <summary>传输超过这个字节数后直接停滞（返回的流不再产生数据）。</summary>
    public long StallAfterBytes { get; set; } = long.MaxValue;

    /// <summary>前 N 次请求抛连接异常。</summary>
    public int FailFirstRequests { get; set; }

    /// <summary>第一遍完整数据改动一个字节（模拟落盘损坏 / 传输错位）。</summary>
    public bool CorruptFirstPass { get; set; }

    public string? Digest { get; set; }

    public string? SuggestedFileName { get; set; }

    public int RequestCount => Volatile.Read(ref _requestCount);

    private bool _corrupted;
    private bool _probeDone;
    private long _served;

    /// <summary>
    /// 全局放行字节数。返回还能读多少；返回 0 表示「该卡住了」。
    /// 必须按**全局**算：按单个响应算的话，4 个分片各读 512KB 都不到 1MB 的阈值，
    /// 每个分片都能读完，根本停不下来。
    /// </summary>
    private int Budget(int available, long stallAt)
    {
        if (stallAt == long.MaxValue) return available;

        var served = Interlocked.Read(ref _served);
        var remaining = stallAt - served;

        if (remaining <= 0) return 0;

        var allowed = (int)Math.Min(available, remaining);
        Interlocked.Add(ref _served, allowed);
        return allowed;
    }

    public Task<DownloadResponse> OpenAsync(string url, long? rangeStart, long? rangeEnd, CancellationToken token)
    {
        var count = Interlocked.Increment(ref _requestCount);

        if (count <= FailFirstRequests)
            throw new HttpRequestException("模拟连接失败");

        // 探测请求：只要 1 个字节即可拿到总长与是否支持 Range
        var isProbe = rangeStart == 0 && rangeEnd == 0;

        // 探测请求本身不受「传输到一半卡住」影响：暂停点永远是探测之后的事，
        // 否则任务连文件大小都拿不到，测的就不是续传了。
        var stallAt = isProbe ? long.MaxValue : StallAfterBytes;

        List<byte> body;
        int status;
        bool acceptRanges;

        if (!SupportsRanges || (IgnoreRangeAfterProbe && !isProbe))
        {
            body = [.. _payload];
            status = 200;
            acceptRanges = false;
        }
        else if (isProbe)
        {
            body = [.. _payload.Take(1)];
            status = 206;
            acceptRanges = true;
        }
        else
        {
            var start = (int)Math.Max(0, rangeStart ?? 0);
            var end = (int)Math.Min(_payload.Length - 1, rangeEnd ?? _payload.Length - 1);
            body = [.. _payload[start..(end + 1)]];
            status = 206;
            acceptRanges = true;
        }

        if (CorruptFirstPass && !_corrupted && body.Count > 0 && !isProbe)
        {
            _corrupted = true;
            body[^1] ^= 0xFF;
        }

        _probeDone |= isProbe;

        var array = body.ToArray();
        var stream = new ThrottledStream(array, available => Budget(available, stallAt));

        return Task.FromResult(new DownloadResponse
        {
            StatusCode = status,
            ContentLength = body.Count,
            TotalLength = _payload.Length,
            Content = stream,
            AcceptRanges = acceptRanges,
            Checksum = Digest is null ? null : new DownloadChecksum("sha1", Digest),
            SuggestedFileName = SuggestedFileName,
        });
    }

    public bool ProbeDone => _probeDone;

    /// <summary>
    /// 内存流 + 「读到额度用完就永远等下去」。
    /// 用无限等待而不是读完就结束：后者会让服务以为分片正常完成，
    /// 而我们想模拟的是「连接还在但没数据」。
    /// </summary>
    private sealed class ThrottledStream : Stream
    {
        private readonly byte[] _data;
        private readonly Func<int, int> _budget;
        private int _position;

        public ThrottledStream(byte[] data, Func<int, int> budget)
        {
            _data = data;
            _budget = budget;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length) return 0;

            var available = Math.Min(buffer.Length, _data.Length - _position);
            var allowed = _budget(available);

            if (allowed <= 0) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            _data.AsMemory(_position, allowed).CopyTo(buffer);
            _position += allowed;

            // 让出线程，避免把服务里的并发逻辑测成同步执行
            await Task.Yield();
            return allowed;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
