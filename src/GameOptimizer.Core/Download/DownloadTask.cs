namespace GameOptimizer.Core.Download;

/// <summary>
/// 一个分片：在最终文件里的字节区间 [<see cref="Start"/>, <see cref="End"/>]，两端都含。
///
/// 用「区间 + 已收字节数」而不是「每片一个临时文件」：
/// 分片直接写进预分配好的同一个文件，靠 <c>RandomAccess</c> 按偏移落盘，
/// 断点续传只需要记住每片收到哪，不需要完成后把多个临时文件拼起来。
/// </summary>
public sealed class DownloadSegment
{
    /// <summary>起始偏移（含）。</summary>
    public long Start { get; internal set; }

    /// <summary>结束偏移（含）。-1 表示服务端没给长度（单流下载，不可续传）。</summary>
    public long End { get; internal set; }

    /// <summary>已经落盘的字节数，从 <see cref="Start"/> 往后的前缀。</summary>
    public long Received { get; internal set; }

    /// <summary>
    /// 传输过程中对已收字节算出的 SHA1（小写十六进制）。
    /// 校验阶段会重新读盘比对：对不上说明这片落了坏数据，只重下这一片。
    /// </summary>
    public string? Sha1 { get; internal set; }

    /// <summary>分片总字节数；长度未知时为 0。</summary>
    public long Length => End >= Start ? End - Start + 1 : 0;

    public bool IsComplete => End >= Start && Received >= Length;
}

/// <summary>
/// 一个下载任务。运行时状态由 <see cref="DownloadService"/> 写入，界面只读。
///
/// 字段刻意保持简单（long / 枚举 / 字符串），因为持久化文件是它唯一的跨进程真相：
/// 程序被杀掉后能恢复到「每片收到第几个字节」，靠的就是这些数字。
/// </summary>
public sealed class DownloadTask
{
    private readonly List<DownloadSegment> _segments = [];

    /// <summary>
    /// 分片集合的不可变快照。
    /// 传输线程只在「准备阶段」增删分片，界面线程随时可能枚举，
    /// 直接暴露 List 会在枚举时报「集合已修改」；换快照就绕开了这个竞态。
    /// </summary>
    private volatile DownloadSegment[] _snapshot = [];

    public required string Id { get; init; }

    public required string Url { get; init; }

    /// <summary>落盘文件名（不含目录）。探测到服务端建议的文件名时可能被改写。</summary>
    public string FileName { get; internal set; } = "";

    /// <summary>保存目录（绝对路径）。</summary>
    public string Directory { get; internal set; } = "";

    /// <summary>文件总长度；0 表示还没探测到。</summary>
    public long TotalBytes { get; internal set; }

    /// <summary>服务端是否支持 Range 请求（支持才有分片与断点续传）。</summary>
    public bool SupportsRanges { get; internal set; }

    public DownloadStatus Status { get; internal set; } = DownloadStatus.Queued;

    /// <summary>失败原因 / 校验结论。失败时必须非空。</summary>
    public string? ErrorMessage { get; internal set; }

    /// <summary>网络重试累计次数（只用于展示「已经试了多少次」）。</summary>
    public int Attempts { get; internal set; }

    /// <summary>服务端声明的校验值；没有则为 null（此时只能记录本地哈希，不能声称已校验）。</summary>
    public DownloadChecksum? ExpectedChecksum { get; internal set; }

    /// <summary>校验完成后本地文件（或分段）的 SHA1，小写十六进制。</summary>
    public string? ActualSha1 { get; internal set; }

    /// <summary>校验结论的一句话说明，例如「SHA1 校验通过」/「服务端未提供校验值」。</summary>
    public string? ChecksumNote { get; internal set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; internal set; }

    /// <summary>当前速度（字节/秒），由服务的采样器每秒写一次。</summary>
    public double BytesPerSecond { get; internal set; }

    /// <summary>文件名是不是「根据链接猜的」。用户手改过就置 false，之后的探测不再覆盖它。</summary>
    internal bool FileNameIsAutomatic { get; set; } = true;

    /// <summary>
    /// 本轮是不是用户点了「重新下载」。只有它允许在探测阶段改写文件名：
    /// 续传时服务端换个名字，改了名会让已下的断点永远用不上。
    /// </summary>
    internal bool RestartRequested { get; set; }

    /// <summary>上次运行时是否完成了收尾（用于启动时判断「该不该自动续传」）。</summary>
    internal bool WasInterrupted { get; set; }

    public IReadOnlyList<DownloadSegment> Segments => _snapshot;

    /// <summary>最终文件路径。</summary>
    public string FilePath => Path.Combine(Directory, FileName);

    /// <summary>
    /// 下载中的临时文件：与最终文件同目录同前缀，多一个任务 ID 与后缀。
    ///
    /// 三点考量：
    /// <list type="number">
    /// <item>同目录 → 最后 <c>File.Move</c> 是同卷改名，瞬间完成、不复制数据；</item>
    /// <item>带任务 ID → 两个任务下同一个文件名时不会互相踩断点；</item>
    /// <item>ID 是持久化字段 → 程序重启后仍能找回自己的断点文件。</item>
    /// </list>
    /// </summary>
    public string PartFilePath => $"{FilePath}.{Id}.gpo-part";

    /// <summary>所有分片已收字节之和。</summary>
    public long DownloadedBytes => _segments.Sum(s => s.Received);

    /// <summary>0~100。总长未知时返回 0，界面改用「已下载 X」显示，不编造百分比。</summary>
    public double ProgressPercent
        => TotalBytes > 0 ? Math.Clamp(DownloadedBytes * 100.0 / TotalBytes, 0, 100) : 0;

    /// <summary>还剩多少字节；总长未知时返回 -1。</summary>
    public long RemainingBytes => TotalBytes > 0 ? Math.Max(0, TotalBytes - DownloadedBytes) : -1;

    /// <summary>预估剩余秒数；速度未知或长度未知时返回 null（界面显示「剩余 --」）。</summary>
    public double? RemainingSeconds
    {
        get
        {
            if (RemainingBytes <= 0 || BytesPerSecond <= 1) return null;
            return RemainingBytes / BytesPerSecond;
        }
    }

    internal void ClearSegments()
    {
        _segments.Clear();
        _snapshot = [];
    }

    internal void AddSegment(DownloadSegment segment)
    {
        _segments.Add(segment);
        _snapshot = [.. _segments];
    }

    internal void SetSegments(IEnumerable<DownloadSegment> segments)
    {
        _segments.Clear();
        _segments.AddRange(segments);
        _snapshot = [.. _segments];
    }

    /// <summary>把每个分片的进度清零（分片区间保留），用于「重新下载」或整体重试。</summary>
    internal void ResetSegmentProgress()
    {
        foreach (var segment in _segments)
        {
            segment.Received = 0;
            segment.Sha1 = null;
        }
    }

    /// <summary>
    /// 退回「什么都没探测过」的状态：用来实施「重新下载」。
    /// 保留 <see cref="ExpectedChecksum"/> 没意义吗？有——服务端声明的校验值与本次传输无关，留着能直接复用。
    /// </summary>
    internal void ResetForRestart()
    {
        _segments.Clear();
        _snapshot = [];
        TotalBytes = 0;
        SupportsRanges = false;
        Attempts = 0;
        ErrorMessage = null;
        ActualSha1 = null;
        ChecksumNote = null;
        CompletedAt = null;
        BytesPerSecond = 0;
        Status = DownloadStatus.Queued;
        RestartRequested = true;
    }

    /// <summary>服务端不支持 Range 时退化成「一个覆盖全长的分片」。</summary>
    internal void EnsureSingleSegment()
    {
        if (_segments.Count == 1 && _segments[0].Start == 0) return;

        SetSegments(
        [
            new DownloadSegment
            {
                Start = 0,
                End = TotalBytes > 0 ? TotalBytes - 1 : -1,
            },
        ]);
    }

    /// <summary>
    /// 退化成单流下载（服务端拒绝 Range）。
    ///
    /// 只有「原本就是 0 开头的单分片」才能保留进度：多分片时每片都只写了前缀，
    /// 文件中间是空洞，把字节数加在一起当成连续前缀会拼出错误数据。
    /// </summary>
    internal void CollapseToSingleStream()
    {
        var resumable = _segments.Count == 1 && _segments[0].Start == 0;
        var received = resumable ? Math.Min(DownloadedBytes, Math.Max(0, TotalBytes)) : 0;

        SetSegments(
        [
            new DownloadSegment
            {
                Start = 0,
                End = TotalBytes > 0 ? TotalBytes - 1 : -1,
                Received = received,
            },
        ]);
    }
}

/// <summary>服务端声明的校验值。</summary>
public sealed record DownloadChecksum(string Kind, string HexValue)
{
    /// <summary>规范化成小写十六进制，便于直接比较。</summary>
    public string KindText => Kind.ToLowerInvariant() switch
    {
        "sha-1" => "sha1",
        "sha-256" => "sha256",
        var other => other,
    };

    public static bool EqualsHex(string? left, string? right)
        => left is not null && right is not null
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool Matches(string? hex) => EqualsHex(HexValue, hex);
}

/// <summary>字节量 / 速度 / 剩余时间的显示口径。纯函数，界面与测试共用。</summary>
public static class DownloadText
{
    private const double Kb = 1024d;
    private const double Mb = 1024d * 1024d;
    private const double Gb = 1024d * 1024d * 1024d;

    /// <summary>字节 → 「1.2 GB」/「236 MB」/「512 KB」/「0 B」，与内存释放量的口径一致。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes >= Gb) return $"{bytes / Gb:F1} GB";
        if (bytes >= Mb) return $"{bytes / Mb:F0} MB";
        if (bytes >= Kb) return $"{bytes / Kb:F0} KB";
        return $"{bytes} B";
    }

    /// <summary>字节/秒 → 「1.2 MB/s」。没有速度时返回空串，界面不显示「0 B/s」占位。</summary>
    public static string FormatSpeed(double bytesPerSecond)
        => bytesPerSecond <= 1 ? "" : $"{FormatBytes((long)bytesPerSecond)}/s";

    /// <summary>
    /// 剩余时间 → 「剩余 00:42」/「剩余 1:02:03」。
    /// 速度未知时返回「剩余 --」：不拿「已用时间」之类的推测数字填空。
    /// </summary>
    public static string FormatRemaining(double? seconds)
    {
        if (seconds is null || double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value) || seconds.Value <= 0)
            return "剩余 --";

        var total = seconds.Value;
        if (total < 1) return "即将完成";

        var span = TimeSpan.FromSeconds(Math.Ceiling(total));
        if (span.TotalHours >= 1)
            return $"剩余 {(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
        return $"剩余 {span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>「12 MB / 128 MB」；长度未知时只报已下载量。</summary>
    public static string FormatProgress(long downloadedBytes, long totalBytes)
        => totalBytes > 0
            ? $"{FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes)}"
            : $"已下载 {FormatBytes(downloadedBytes)}";
}
