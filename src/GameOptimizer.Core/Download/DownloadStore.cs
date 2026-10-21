namespace GameOptimizer.Core.Download;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 下载任务的本地持久化。
///
/// 落盘的内容必须足够「程序被杀掉后接着下」：
/// 每片的区间、已收字节数、该片的 SHA1、任务状态、期望校验值。
/// 因此这里不是「界面缓存」，而是下载内核的一部分。
///
/// 写入策略：先写 <c>.tmp</c> 再 <c>File.Move(overwrite: true)</c>。
/// 反过来的顺序（直接覆盖）在断电时会留下一个半截的 JSON，
/// 下次启动读不出来，等于所有任务丢失。
/// </summary>
public sealed class DownloadStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>默认位置：%LocalAppData%\GameOptimizer\downloads.json</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameOptimizer",
        "downloads.json");

    private readonly string _path;
    private readonly object _gate = new();

    public DownloadStore(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>实际使用的文件路径，界面要显示出来方便用户备份。</summary>
    public string FilePath => _path;

    /// <summary>
    /// 读取保存的任务。文件不存在或损坏时返回空列表，并把原因写进 <paramref name="error"/>。
    /// 不抛异常：一个坏掉的 JSON 不该让整个程序起不来。
    /// </summary>
    public List<DownloadTask> Load(out string? error)
    {
        error = null;
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return [];

                var json = File.ReadAllText(_path);
                var file = JsonSerializer.Deserialize<DownloadFile>(json, JsonOptions);
                if (file?.Tasks is null) return [];

                return file.Tasks.Where(t => t is not null).Select(ToTask).ToList();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return [];
            }
        }
    }

    /// <summary>保存任务列表。返回是否成功，失败原因交给调用方显示。</summary>
    public bool Save(IEnumerable<DownloadTask> tasks, out string? error)
    {
        error = null;
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var file = new DownloadFile
                {
                    Version = 1,
                    SavedAt = DateTimeOffset.Now,
                    Tasks = tasks.Select(ToRecord).ToList(),
                };

                var json = JsonSerializer.Serialize(file, JsonOptions);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);

                // 同目录改名是原子的：要么是旧文件，要么是完整的新文件，不会出现半截
                File.Move(temp, _path, overwrite: true);

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    private static TaskRecord ToRecord(DownloadTask task) => new()
    {
        Id = task.Id,
        Url = task.Url,
        FileName = task.FileName,
        Directory = task.Directory,
        TotalBytes = task.TotalBytes,
        SupportsRanges = task.SupportsRanges,
        Status = (int)task.Status,
        Error = task.ErrorMessage,
        Attempts = task.Attempts,
        ExpectedChecksum = task.ExpectedChecksum is null
            ? null
            : new ChecksumRecord { Kind = task.ExpectedChecksum.Kind, Hex = task.ExpectedChecksum.HexValue },
        ActualSha1 = task.ActualSha1,
        ChecksumNote = task.ChecksumNote,
        CreatedAt = task.CreatedAt,
        CompletedAt = task.CompletedAt,
        FileNameIsAutomatic = task.FileNameIsAutomatic,
        WasInterrupted = task.WasInterrupted,
        Segments = task.Segments.Select(s => new SegmentRecord
        {
            Start = s.Start,
            End = s.End,
            Received = s.Received,
            Sha1 = s.Sha1,
        }).ToList(),
    };

    private static DownloadTask ToTask(TaskRecord record)
    {
        var task = new DownloadTask
        {
            Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id,
            Url = record.Url ?? "",
            CreatedAt = record.CreatedAt,

            // 目录或文件名缺失的任务落不进任何地方，状态直接判失败而不是显示「已完成」
            FileName = DownloadFileName.Sanitize(record.FileName) ?? "",
            Directory = record.Directory ?? "",
            TotalBytes = Math.Max(0, record.TotalBytes),
            SupportsRanges = record.SupportsRanges,
            Status = Enum.IsDefined(typeof(DownloadStatus), record.Status)
                ? (DownloadStatus)record.Status
                : DownloadStatus.Failed,
            ErrorMessage = record.Error,
            Attempts = Math.Max(0, record.Attempts),
            ExpectedChecksum = record.ExpectedChecksum is null
                ? null
                : new DownloadChecksum(record.ExpectedChecksum.Kind ?? "sha1", record.ExpectedChecksum.Hex ?? ""),
            ActualSha1 = record.ActualSha1,
            ChecksumNote = record.ChecksumNote,
            CompletedAt = record.CompletedAt,
            FileNameIsAutomatic = record.FileNameIsAutomatic,
            WasInterrupted = record.WasInterrupted,
        };

        if (record.Segments is not null)
        {
            foreach (var s in record.Segments)
            {
                task.AddSegment(new DownloadSegment
                {
                    Start = Math.Max(0, s.Start),
                    End = s.End,
                    Received = Math.Max(0, s.Received),
                    Sha1 = s.Sha1,
                });
            }
        }

        // 上次进程结束时还在下载的任务：状态回落成「暂停」，
        // 否则下次启动会显示「下载中」却没有任何传输在跑（假状态）。
        // WasInterrupted 保留原值 —— 服务靠它区分「崩溃中断」与「用户主动暂停」。
        if (task.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
            task.Status = DownloadStatus.Paused;

        return task;
    }

    private sealed class DownloadFile
    {
        public int Version { get; set; }
        public DateTimeOffset SavedAt { get; set; }
        public List<TaskRecord> Tasks { get; set; } = [];
    }

    private sealed class TaskRecord
    {
        public string? Id { get; set; }
        public string? Url { get; set; }
        public string? FileName { get; set; }
        public string? Directory { get; set; }
        public long TotalBytes { get; set; }
        public bool SupportsRanges { get; set; }
        public int Status { get; set; }
        public string? Error { get; set; }
        public int Attempts { get; set; }
        public ChecksumRecord? ExpectedChecksum { get; set; }
        public string? ActualSha1 { get; set; }
        public string? ChecksumNote { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public bool FileNameIsAutomatic { get; set; } = true;

        /// <summary>上次运行时是否是被硬中断的（崩溃 / 强杀），决定启动后要不要自动续传。</summary>
        public bool WasInterrupted { get; set; }
        public List<SegmentRecord>? Segments { get; set; }
    }

    private sealed class SegmentRecord
    {
        public long Start { get; set; }
        public long End { get; set; }
        public long Received { get; set; }
        public string? Sha1 { get; set; }
    }

    private sealed class ChecksumRecord
    {
        public string? Kind { get; set; }
        public string? Hex { get; set; }
    }
}
