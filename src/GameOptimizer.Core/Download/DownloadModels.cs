namespace GameOptimizer.Core.Download;

/// <summary>
/// 下载任务的六种状态。刻意把「排队中」和「下载中」分开：
/// 用户需要知道一个任务到底是正在跑，还是在等其它任务腾出并发名额。
/// </summary>
public enum DownloadStatus
{
    /// <summary>已入队，等并发名额。</summary>
    Queued = 0,

    /// <summary>正在传输。</summary>
    Downloading = 1,

    /// <summary>用户暂停（或程序退出时自动暂停），断点保留。</summary>
    Paused = 2,

    /// <summary>已完成并通过校验。</summary>
    Completed = 3,

    /// <summary>失败。失败原因写在 <see cref="DownloadTask.ErrorMessage"/>，不静默。</summary>
    Failed = 4,

    /// <summary>用户取消，分片文件已删除。</summary>
    Canceled = 5,
}

/// <summary>状态的可读文案。界面不自己拼字符串，保证测试与界面口径一致。</summary>
public static class DownloadStatusText
{
    public static string Describe(DownloadStatus status) => status switch
    {
        DownloadStatus.Queued => "排队中",
        DownloadStatus.Downloading => "下载中",
        DownloadStatus.Paused => "已暂停",
        DownloadStatus.Completed => "已完成",
        DownloadStatus.Failed => "下载失败",
        DownloadStatus.Canceled => "已取消",
        _ => "未知",
    };

    /// <summary>是否处在「队列里」：排队中与下载中都算。</summary>
    public static bool IsInQueue(DownloadStatus status)
        => status is DownloadStatus.Queued or DownloadStatus.Downloading;

    /// <summary>是否可以继续（暂停 / 失败 / 取消后都能接着下）。</summary>
    public static bool CanResume(DownloadStatus status)
        => status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Canceled;
}

/// <summary>
/// 下载器的可配置项。全部集中在这里，界面只改这一份，不散落在服务内部。
/// <see cref="Normalized"/> 负责把用户乱填的数字夹回安全范围。
/// </summary>
public sealed class DownloadSettings
{
    public const int MinParallelSegments = 1;
    public const int MaxParallelSegments = 16;
    public const int MinParallelTasks = 1;
    public const int MaxParallelTasks = 8;
    public const int MaxRetries = 10;
    public const int MinReadStallTimeoutMs = 1_000;
    public const int MaxReadStallTimeoutMs = 120_000;

    /// <summary>单个任务内的分片并发数，默认 4（通用分片下载器的常见默认值）。</summary>
    public int ParallelSegments { get; set; } = 4;

    /// <summary>同时下载的任务数，默认 2。分片并发是「一个任务内」，这个是「任务之间」。</summary>
    public int MaxConcurrentTasks { get; set; } = 2;

    /// <summary>全局限速（字节/秒）。0 = 不限速。所有任务共享同一个令牌桶。</summary>
    public long SpeedLimitBytesPerSecond { get; set; }

    /// <summary>网络异常重试次数（不含首次）。指数退避：1×、2×、4× 基准间隔。</summary>
    public int Retries { get; set; } = 3;

    /// <summary>退避基准间隔。测试里会调小到几十毫秒，正式运行是 500ms。</summary>
    public int RetryBaseDelayMs { get; set; } = 500;

    /// <summary>单次读取的停滞超时。超过这个时间没收到任何字节就按超时重试。</summary>
    public int ReadStallTimeoutMs { get; set; } = 30_000;

    /// <summary>校验不通过时最多再重下几轮（每轮只重下哈希对不上的分片）。</summary>
    public int IntegrityRetries { get; set; } = 1;

    /// <summary>启动时是否自动续传未完成的任务。</summary>
    public bool AutoResume { get; set; } = true;

    /// <summary>新建任务的默认保存目录。</summary>
    public string DefaultDirectory { get; set; } = "";

    /// <summary>系统「下载」文件夹；不存在时退回用户目录。</summary>
    public static string DefaultDirectoryPath
    {
        get
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile)) return Path.GetTempPath();

            var downloads = Path.Combine(profile, "Downloads");
            return Directory.Exists(downloads) ? downloads : profile;
        }
    }

    /// <summary>
    /// 就地纠正非法值并返回自身（配置读写、界面输入都走这里）。
    /// 夹范围而不是抛异常：用户输入 999 个分片不该让整页崩掉。
    /// </summary>
    public static DownloadSettings Normalized(DownloadSettings? source)
    {
        var settings = source ?? new DownloadSettings();

        settings.ParallelSegments = Math.Clamp(settings.ParallelSegments, MinParallelSegments, MaxParallelSegments);
        settings.MaxConcurrentTasks = Math.Clamp(settings.MaxConcurrentTasks, MinParallelTasks, MaxParallelTasks);
        settings.Retries = Math.Clamp(settings.Retries, 0, MaxRetries);
        settings.RetryBaseDelayMs = Math.Clamp(settings.RetryBaseDelayMs, 0, 60_000);
        settings.ReadStallTimeoutMs = Math.Clamp(
            settings.ReadStallTimeoutMs, MinReadStallTimeoutMs, MaxReadStallTimeoutMs);
        settings.IntegrityRetries = Math.Clamp(settings.IntegrityRetries, 0, 5);
        if (settings.SpeedLimitBytesPerSecond < 0) settings.SpeedLimitBytesPerSecond = 0;

        if (string.IsNullOrWhiteSpace(settings.DefaultDirectory))
            settings.DefaultDirectory = DefaultDirectoryPath;

        return settings;
    }
}

/// <summary>下载链接的基本校验。纯函数，界面在「添加下载」前先用它拦一道。</summary>
public static class DownloadUrl
{
    /// <summary>
    /// 是否为可下载的 http / https 绝对地址。
    /// 没写协议的（<c>www.example.com/a.zip</c>）判为不合法：
    /// 猜测协议会让「用户填错了」变成「下了一份奇怪的东西」。
    /// </summary>
    public static bool IsValid(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && !string.IsNullOrEmpty(uri.Host);
}

/// <summary>底部统计栏需要的四个数，一次算全，避免界面分开算出现对不上的瞬间。</summary>
public readonly record struct DownloadStatistics(
    double TotalBytesPerSecond,
    int RunningTasks,
    int ActiveTasks,
    int TotalTasks)
{
    public static DownloadStatistics Empty => new(0, 0, 0, 0);
}
