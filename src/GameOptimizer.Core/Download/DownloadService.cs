namespace GameOptimizer.Core.Download;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

/// <summary>
/// 下载内核。**不认识任何界面类型**，只对外暴露任务集合、状态变化事件与几个异步操作，
/// 因此既可以被 WPF 的 ViewModel 包一层，也可以被 CLI / 测试直接驱动。
///
/// 能力对照（PCL2 下载器的核心行为）：
/// <list type="bullet">
/// <item>多线程分片：默认 4 片，按 4KB 对齐，末片吃掉余数；</item>
/// <item>断点续传：每片已收字节 + 该片 SHA1 全部落盘，重启后从断点继续；</item>
/// <item>文件校验：服务端给 Digest 就比对，没给就如实标注「未校验」，绝不假装通过；</item>
/// <item>失败重试：网络类错误指数退避重试；校验失败先用分片哈希定位坏片、只重下坏片，
/// 定位不到（单分片或缺少片哈希）才整份重下；</item>
/// <item>实时统计：速度按 1 秒窗口采样，剩余时间由速度推算；</item>
/// <item>全局限速：令牌桶被所有任务共享，改设置立刻生效。</item>
/// </list>
///
/// 线程模型：任务状态只在内部锁 <see cref="_gate"/> 下改，界面的读都走快照；
/// 传输本身是完全异步的，不会占用 UI 线程。
/// </summary>
public sealed class DownloadService : IAsyncDisposable
{
    private readonly IDownloadTransport _transport;
    private readonly DownloadStore _store;

    /// <summary>任务状态与分片进度的唯一写锁。粒度粗但临界区都只有几行，不会成为瓶颈。</summary>
    private readonly object _gate = new();

    /// <summary>每个任务的取消源：暂停 / 取消 / 「重新下载」都用它打断正在飞的请求。</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();

    /// <summary>正在跑的任务：值在任务彻底停下（含退避等待）后才完成，用来判断并发名额。</summary>
    private readonly ConcurrentDictionary<string, Task> _running = new();

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Throttle _throttle = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private readonly ConcurrentDictionary<string, SpeedSampler> _speeds = new();
    private readonly Timer _speedTimer;
    private readonly Timer _autoSaveTimer;

    private readonly List<DownloadTask> _tasks = [];
    private DownloadSettings _settings;
    private bool _disposed;

    public event EventHandler<DownloadTask>? TaskChanged;
    public event EventHandler? TasksChanged;
    public event EventHandler<string>? Notice;

    public DownloadService(
        IDownloadTransport? transport = null,
        DownloadStore? store = null,
        DownloadSettings? settings = null)
    {
        _transport = transport ?? new HttpDownloadTransport();
        _store = store ?? new DownloadStore();
        _settings = DownloadSettings.Normalized(settings);
        _throttle.Limit = _settings.SpeedLimitBytesPerSecond;

        _speedTimer = new Timer(_ => SampleSpeeds(), null, 1000, 1000);

        // 兜底保存：界面切走、托盘隐藏、异常退出都不会丢进度
        _autoSaveTimer = new Timer(_ => SaveAsync(), null, 5000, 5000);
    }

    /// <summary>下载配置。赋值时自动夹回合法范围，并让限速立刻生效。</summary>
    public DownloadSettings Settings
    {
        get => _settings;
        set
        {
            _settings = DownloadSettings.Normalized(value);
            _throttle.Limit = _settings.SpeedLimitBytesPerSecond;
        }
    }

    /// <summary>任务集合快照（新任务在前）。界面每次刷新都拿一份新数组，避免枚举竞态。</summary>
    public IReadOnlyList<DownloadTask> Tasks
    {
        get
        {
            lock (_gate)
            {
                return [.. _tasks.OrderByDescending(t => t.CreatedAt)];
            }
        }
    }

    public DownloadStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                var total = _tasks.Count;
                var running = _tasks.Count(t => t.Status == DownloadStatus.Downloading);
                var active = _tasks.Count(t => DownloadStatusText.IsInQueue(t.Status) || t.Status == DownloadStatus.Paused);

                var speed = 0d;
                foreach (var task in _tasks)
                    if (task.Status == DownloadStatus.Downloading)
                        speed += task.BytesPerSecond;

                return new DownloadStatistics(speed, running, active, total);
            }
        }
    }

    public DownloadTask? Find(string id)
    {
        lock (_gate)
        {
            return _tasks.FirstOrDefault(t => t.Id == id);
        }
    }

    /// <summary>
    /// 启动时载入本地任务。**只做三件事**：读文件、把「下载中」降级为「暂停」、必要时自动续传。
    /// 返回读取失败的原因（null 表示一切正常）。
    /// </summary>
    public async Task<string?> LoadAsync()
    {
        var tasks = _store.Load(out var error);

        lock (_gate)
        {
            _tasks.Clear();
            _tasks.AddRange(tasks);
        }

        RaiseTasksChanged();

        if (_settings.AutoResume)
        {
            // 只自动续传「上次是被硬中断的」任务：进程退出 / 崩溃时还在下载的，
            // 下次启动接着下。用户主动点过暂停的保持暂停，不能替他恢复。
            foreach (var task in Tasks)
            {
                if (task.Status != DownloadStatus.Paused || !task.WasInterrupted) continue;

                // 先把状态退回排队：Start 只认「排队中 / 下载中」，
                // 直接对 Paused 调它会静默什么都不做（自动续传看起来没生效）。
                lock (_gate)
                {
                    task.Status = DownloadStatus.Queued;
                }

                Start(task);
            }
        }
        else
        {
            // 不自动续传时也要把状态刷一遍：持久化里的「下载中」已经降级为「暂停」
            foreach (var task in Tasks) RaiseTaskChanged(task);
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return error;
    }

    /// <summary>
    /// 新建任务。校验链接与目录，探测文件名后入队。
    /// 返回 null 表示入队失败，原因写在 <paramref name="error"/>。
    /// </summary>
    public DownloadTask? CreateTask(
        string url,
        string directory,
        string? fileName,
        out string? error)
    {
        error = null;

        if (!DownloadUrl.IsValid(url))
        {
            error = "下载地址不合法：只支持 http / https 直链";
            return null;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "保存路径不能为空";
            return null;
        }

        try
        {
            Directory.CreateDirectory(directory.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = $"保存路径不可用：{ex.Message}";
            return null;
        }

        var trimmedUrl = url.Trim();
        var name = DownloadFileName.Sanitize(fileName);

        // 用户没给名字就从链接猜；猜不到就留空，让服务在探测阶段用服务端给的文件名
        var automatic = string.IsNullOrEmpty(name);
        if (automatic) name = DownloadFileName.FromUrl(trimmedUrl);

        var task = new DownloadTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = trimmedUrl,
            FileName = name,
            Directory = directory.Trim(),
            FileNameIsAutomatic = automatic,
            Status = DownloadStatus.Queued,
        };

        lock (_gate)
        {
            _tasks.Add(task);
        }

        RaiseTasksChanged();
        Start(task);

        return task;
    }

    /// <summary>暂停：打断正在飞的请求，保留断点。任务不在运行也没有断点时是空操作。</summary>
    public void Pause(string id)
    {
        var task = Find(id);
        if (task is null) return;

        lock (_gate)
        {
            if (!DownloadStatusText.IsInQueue(task.Status)) return;
            task.Status = DownloadStatus.Paused;
            task.BytesPerSecond = 0;

            // 用户主动暂停：下次启动不该被自动续传
            task.WasInterrupted = false;
        }

        CancelRunner(id);
        RaiseTaskChanged(task);
        SaveAsync();
    }

    /// <summary>继续：把暂停 / 失败 / 取消的任务重新排队（并发满了就是「排队中」）。</summary>
    public void Resume(string id)
    {
        var task = Find(id);
        if (task is null) return;

        lock (_gate)
        {
            if (!DownloadStatusText.CanResume(task.Status)) return;

            // 取消过的任务分片已被清空，重新开始时从零规划
            if (task.Status == DownloadStatus.Canceled) task.ResetForRestart();

            task.Status = DownloadStatus.Queued;
            task.ErrorMessage = null;
        }

        RaiseTaskChanged(task);
        Start(task);
    }

    /// <summary>
    /// 取消：停传输、删临时文件、清空断点。
    /// 保留任务记录（状态 = 已取消），用户还能点「继续」重下——删掉记录反而更难找回链接。
    /// </summary>
    public void Cancel(string id)
    {
        var task = Find(id);
        if (task is null) return;

        CancelRunner(id);

        lock (_gate)
        {
            if (task.Status == DownloadStatus.Completed) return;

            task.Status = DownloadStatus.Canceled;
            task.BytesPerSecond = 0;
            task.ErrorMessage = null;
            task.ResetSegmentProgress();
        }

        TryDeleteFile(task.PartFilePath);
        RaiseTaskChanged(task);
        SaveAsync();
    }

    /// <summary>重新下载：清掉断点与临时文件，从头再来（已存在的同名文件会被替换）。</summary>
    public void Restart(string id)
    {
        var task = Find(id);
        if (task is null) return;

        CancelRunner(id);

        lock (_gate)
        {
            task.ResetForRestart();
            task.Status = DownloadStatus.Queued;
        }

        TryDeleteFile(task.PartFilePath);
        RaiseTaskChanged(task);
        Start(task);
    }

    /// <summary>从列表里移除一条记录（只在任务已结束时有意义）。</summary>
    public void Remove(string id)
    {
        DownloadTask? removed;

        lock (_gate)
        {
            removed = _tasks.FirstOrDefault(t => t.Id == id);
            if (removed is null) return;
            _tasks.Remove(removed);
        }

        CancelRunner(id);
        TryDeleteFile(removed.PartFilePath);

        _speeds.TryRemove(id, out _);
        RaiseTasksChanged();
        SaveAsync();
    }

    /// <summary>批量：全部暂停。</summary>
    public void PauseAll()
    {
        foreach (var task in Tasks)
            if (DownloadStatusText.IsInQueue(task.Status))
                Pause(task.Id);
    }

    /// <summary>批量：全部开始（已完成的反倒不动，避免重下已完成的大文件）。</summary>
    public void ResumeAll()
    {
        foreach (var task in Tasks)
            if (DownloadStatusText.CanResume(task.Status))
                Resume(task.Id);
    }

    /// <summary>批量：清空已完成任务（只删记录，不动已下好的文件）。</summary>
    public int ClearCompleted()
    {
        List<DownloadTask> done;
        lock (_gate)
        {
            done = _tasks.Where(t => t.Status == DownloadStatus.Completed).ToList();
            foreach (var task in done) _tasks.Remove(task);
        }

        if (done.Count == 0) return 0;

        RaiseTasksChanged();
        SaveAsync();
        return done.Count;
    }

    /// <summary>
    /// 校验已完成文件的 SHA1。用户点「校验」、或下载结束后想复核时用。
    /// 返回一句结论（「通过 / 不通过 / 服务端未提供校验值」），不伪造结果。
    /// </summary>
    public async Task<string> VerifyAsync(string id, CancellationToken token = default)
    {
        var task = Find(id);
        if (task is null) return "任务不存在";

        if (task.Status != DownloadStatus.Completed || !File.Exists(task.FilePath))
        {
            const string message = "文件尚未下载完成，无法校验";
            lock (_gate) task.ChecksumNote = message;
            RaiseTaskChanged(task);
            return message;
        }

        string actual;
        try
        {
            actual = await ComputeFileSha1Async(task.FilePath, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"读取文件失败：{ex.Message}";
        }

        string note;
        lock (_gate)
        {
            task.ActualSha1 = actual;

            if (task.ExpectedChecksum is not null)
            {
                note = task.ExpectedChecksum.Matches(actual)
                    ? $"SHA1 校验通过（{Short(actual)}）"
                    : $"SHA1 不一致：期望 {Short(task.ExpectedChecksum.HexValue)}，实际 {Short(actual)}";
            }
            else
            {
                note = $"未校验（服务端未提供校验值），本地 SHA1：{Short(actual)}";
            }

            task.ChecksumNote = note;
        }

        RaiseTaskChanged(task);
        SaveAsync();
        return note;
    }

    // ------------------------------------------------------------------
    // 调度
    // ------------------------------------------------------------------

    /// <summary>
    /// 起一个后台 runner。**方法本身不阻塞**：
    /// 名额满时任务停在「排队中」，等前面的跑完由 <see cref="PumpQueue"/> 再拉起。
    /// </summary>
    private void Start(DownloadTask task)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(task.Id)) return;

            if (DownloadStatusText.IsInQueue(task.Status) is false) return;
            task.Status = DownloadStatus.Queued;
        }

        PumpQueue();
    }

    /// <summary>按并发上限把「排队中」的任务拉起来。</summary>
    private void PumpQueue()
    {
        List<DownloadTask> candidates;
        var toStart = new List<DownloadTask>();

        lock (_gate)
        {
            if (_disposed) return;

            var slots = _settings.MaxConcurrentTasks - _running.Count;
            if (slots <= 0) return;

            candidates = [.. _tasks
                .Where(t => t.Status == DownloadStatus.Queued)
                .OrderBy(t => t.CreatedAt)];

            foreach (var task in candidates)
            {
                if (slots <= 0) break;

                task.Status = DownloadStatus.Downloading;
                task.ErrorMessage = null;

                // 在「标记为下载中」的同一把锁里记下「可能被硬中断」：
                // 进程在这之后被杀掉，下次启动就知道该自动续传。
                task.WasInterrupted = true;

                toStart.Add(task);
                slots--;
            }
        }

        foreach (var task in toStart)
        {
            var cts = new CancellationTokenSource();
            _cancellations[task.Id] = cts;

            var runner = Task.Run(() => RunAsync(task, cts.Token));
            _running[task.Id] = runner;

            RaiseTaskChanged(task);

            // 用 completion 而不是 _ 作参数名：lambda 参数 `_` 会把后面的 `out _` 当成
            // 对同名变量的引用，编译期直接报类型不匹配。
            _ = runner.ContinueWith(completion =>
            {
                _running.TryRemove(task.Id, out _);
                _cancellations.TryRemove(task.Id, out _);
                cts.Dispose();
                PumpQueue();
            }, TaskScheduler.Default);
        }
    }

    private async Task RunAsync(DownloadTask task, CancellationToken token)
    {
        _speeds[task.Id] = new SpeedSampler();

        try
        {
            await ExecuteAsync(task, token).ConfigureAwait(false);

            lock (_gate)
            {
                task.WasInterrupted = false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 用户暂停 / 取消：状态已由对应方法写好，这里不再覆盖
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                task.Status = DownloadStatus.Failed;
                task.BytesPerSecond = 0;
                task.WasInterrupted = false;
                task.ErrorMessage = string.IsNullOrWhiteSpace(ex.Message)
                    ? $"{ex.GetType().Name}（无详细信息）"
                    : ex.Message;
            }

            RaiseTaskChanged(task);
        }
        finally
        {
            _throttle.Limit = _settings.SpeedLimitBytesPerSecond;
            SaveAsync();
        }
    }

    /// <summary>一个任务从探测到校验的完整流程。</summary>
    private async Task ExecuteAsync(DownloadTask task, CancellationToken token)
    {
        // 1) 探测：拿到总长度、是否支持 Range、服务端的文件名/校验值
        if (task.TotalBytes <= 0 || task.Segments.Count == 0)
        {
            await ProbeAsync(task, token).ConfigureAwait(false);
            SaveAsync();
        }

        token.ThrowIfCancellationRequested();

        // 2) 确保临时文件存在，并核对已有断点
        PreparePartFile(task);

        // 3) 传输所有未完成分片。若半途发现服务端其实不支持 Range，
        //    整个任务退回「单流、从零开始」，而不是让各分片各自截断同一个文件。
        try
        {
            await TransferAllAsync(task, token).ConfigureAwait(false);
        }
        catch (RangeIgnoredException)
        {
            lock (_gate)
            {
                task.SupportsRanges = false;
                task.CollapseToSingleStream();
            }

            TryDeleteFile(task.PartFilePath);
            PreparePartFile(task);

            Notice?.Invoke(this, $"{task.FileName}：服务器不支持断点续传，改为单线程从零下载");
            RaiseTaskChanged(task);

            await TransferAllAsync(task, token).ConfigureAwait(false);
        }

        // 4) 落盘改名 + 校验（校验失败重下，最多 IntegrityRetries 轮）
        await FinalizeAsync(task, token).ConfigureAwait(false);
    }

    private async Task ProbeAsync(DownloadTask task, CancellationToken token)
    {
        var response = await WithRetryAsync(
            token => _transport.OpenAsync(task.Url, 0, 0, token),
            task,
            token,
            "探测文件信息").ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccess)
                throw new DownloadHttpException(response.StatusCode, DescribeStatus(response));

            // 什么时候采用服务端给的文件名：
            //   · 链接里根本没提取出名字（只能靠 Content-Disposition）；
            //   · 用户点了「重新下载」（允许重新协商一次）。
            // 续传时**不**改：服务端换个名字会让已下的断点永远用不上。
            var shouldAdopt = string.IsNullOrEmpty(task.FileName) || task.RestartRequested;

            if (shouldAdopt && task.FileNameIsAutomatic
                && response.SuggestedFileName is { Length: > 0 } suggested)
            {
                var cleaned = DownloadFileName.Sanitize(suggested);
                if (!string.IsNullOrEmpty(cleaned)) task.FileName = cleaned;
            }

            // 仍然没有名字：给一个带时间戳的兜底名，否则无法落盘
            if (string.IsNullOrWhiteSpace(task.FileName))
            {
                lock (_gate)
                {
                    task.FileName = "download-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                }
            }

            task.ExpectedChecksum = response.Checksum;

            // 206 + Content-Range: bytes 0-0/1234 → 拿到了精确总长
            var total = response.TotalLength ?? response.ContentLength ?? 0;

            // 长度未知（chunked / 直播流）无法分片、无法断点续传、也无法显示百分比。
            // 与其显示一个永远 0% 的假进度，不如如实拒绝。
            if (total <= 0)
            {
                throw new DownloadException(
                    "服务器未返回文件大小，无法分片与断点续传。请换一个提供 Content-Length 的直链。",
                    isTransient: false);
            }

            var supportsRanges = response.StatusCode == 206 && response.AcceptRanges;

            lock (_gate)
            {
                // 探测结果必须写回任务，否则后续所有分片/校验都以为「长度未知」
                task.TotalBytes = total;
            }

            DownloadSegmentPlanner.Plan(total, _settings.ParallelSegments).ShouldApplyTo(task, supportsRanges);

            task.RestartRequested = false;
        }
    }

    private void PreparePartFile(DownloadTask task)
    {
        var partPath = task.PartFilePath;
        var directory = Path.GetDirectoryName(partPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = new FileStream(
            partPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous);

        // 断点比文件还长（文件被外部删改过）：断点不可信，整体重来
        var existing = stream.Length;
        if (task.DownloadedBytes > existing)
        {
            lock (_gate)
            {
                task.ResetSegmentProgress();
                stream.SetLength(0);
            }
        }

        if (task.TotalBytes > 0 && stream.Length != task.TotalBytes)
        {
            // 预分配：避免传输中反复扩文件导致磁盘碎片
            stream.SetLength(task.TotalBytes);
        }
    }

    /// <summary>并发拉完所有未完成的分片；分片粒度就是并发粒度。</summary>
    private Task TransferAllAsync(DownloadTask task, CancellationToken token)
        => TransferSegmentsAsync(task, [.. task.Segments.Where(s => !s.IsComplete)], token);

    /// <summary>并发拉取指定分片。<paramref name="pending"/> 由调用方挑好（全部未完成 / 只坏片）。</summary>
    private async Task TransferSegmentsAsync(
        DownloadTask task,
        List<DownloadSegment> pending,
        CancellationToken token)
    {
        if (pending.Count == 0) return;

        var parallel = Math.Clamp(_settings.ParallelSegments, 1, Math.Max(1, pending.Count));
        using var limiter = new SemaphoreSlim(parallel);

        var failures = new ConcurrentQueue<Exception>();

        var workers = pending.Select(async segment =>
        {
            await limiter.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await TransferSegmentAsync(task, segment, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
            finally
            {
                limiter.Release();
            }
        });

        await Task.WhenAll(workers).ConfigureAwait(false);

        if (failures.TryDequeue(out var failure)) throw failure;
    }

    /// <summary>
    /// 单个分片：发带 Range 的请求 → 边读边写盘 → 边算该片 SHA1。
    /// 已收字节 > 0 时从 <c>Start + Received</c> 续传。
    ///
    /// <paramref name="task"/>.SupportsRanges 为 false（服务端明确忽略 Range）时，
    /// 本片改为「从 0 单流读到尾」，不再发 Range —— 否则会陷入
    /// 「发 Range → 收 200 → 判定不支持 → 再发 Range」的死循环。
    /// </summary>
    private async Task TransferSegmentAsync(DownloadTask task, DownloadSegment segment, CancellationToken token)
    {
        var knownEnd = segment.End;

        // 只要知道总长就试着带 Range：很多服务器能用 Range 续传却懒得声明 Accept-Ranges。
        // 真遇到无视 Range 的（回 200），单流模式就地从头再来，多分片模式整任务回退。
        var sendRange = knownEnd >= 0;
        var singleStream = segment.Start == 0 && task.Segments.Count == 1;

        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        using var file = new FileStream(
            task.PartFilePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        var written = segment.Start + segment.Received;

        if (segment.Received > 0)
        {
            // 进程重启后该片的哈希已经不在内存里，只能把盘上那段重新喂一遍
            await FeedExistingAsync(task, segment, hash, token).ConfigureAwait(false);
        }

        var buffer = new byte[128 * 1024];
        var attempt = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                using var response = await _transport
                    .OpenAsync(task.Url, sendRange ? written : null, sendRange ? knownEnd : null, token)
                    .ConfigureAwait(false);

                if (response.IsRangeNotSatisfiable)
                {
                    throw new DownloadException(
                        "断点位置超出服务器文件长度（文件可能已更新），请点「重新下载」",
                        isTransient: false);
                }

                if (!response.IsSuccess)
                    throw new DownloadHttpException(response.StatusCode, DescribeStatus(response));

                // 发了 Range 却收到整份文件（200）：
                //   · 多分片 → 不能在这里截断（会把别的分片的进度清掉），
                //     抛给 ExecuteAsync 让整个任务退回单流；
                //   · 单流 → 这是唯一的写者，从头再写一遍即可。
                if (sendRange && response.StatusCode == 200)
                {
                    if (!singleStream) throw new RangeIgnoredException();

                    if (written > 0)
                    {
                        written = 0;
                        hash.Dispose();
                        hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                        file.SetLength(0);

                        lock (_gate)
                        {
                            segment.Received = 0;
                        }
                    }
                }

                file.Position = written;

                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    var chunk = _throttle.ChunkSize;
                    int read;

                    using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        readCts.CancelAfter(_settings.ReadStallTimeoutMs);
                        try
                        {
                            read = await response.Content
                                .ReadAsync(buffer.AsMemory(0, chunk), readCts.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            throw new DownloadException("读取超时：超过指定时间没有收到数据", isTransient: true);
                        }
                    }

                    if (read <= 0) break;

                    if (!await _throttle.WaitAsync(read, token).ConfigureAwait(false))
                        throw new OperationCanceledException(token);

                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);

                    written += read;

                    lock (_gate)
                    {
                        segment.Received = written - segment.Start;
                    }

                    _speeds[task.Id].Add(read);

                    if (knownEnd >= 0 && written > knownEnd + 1)
                        throw new DownloadException("服务器返回的数据超出分片范围（文件可能已变化）");

                    if (task.TotalBytes > 0 && written > task.TotalBytes)
                        throw new DownloadException("服务器返回的数据多于声明的文件长度（文件可能已变化）");
                }

                break; // 本片读完
            }
            catch (Exception ex) when (ShouldRetry(ex, token, ++attempt))
            {
                // 重试前把「这一轮读到的部分」同步到断点，否则下次会从旧位置重读
                lock (_gate)
                {
                    segment.Received = written - segment.Start;
                }

                await DelayBeforeRetryAsync(task, attempt, token).ConfigureAwait(false);
            }
        }

        file.Flush();

        lock (_gate)
        {
            segment.Sha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        hash.Dispose();

        // 数据不够一片的长度：服务端提前关了连接，按可重试错误处理
        if (knownEnd >= 0 && segment.Received < segment.Length)
        {
            throw new DownloadException(
                $"连接提前中断：分片只收到 {segment.Received} / {segment.Length} 字节", isTransient: true);
        }
    }

    /// <summary>续传时把盘上已有的那一段喂给哈希，保证该片 SHA1 覆盖完整区间。</summary>
    private static async Task FeedExistingAsync(
        DownloadTask task,
        DownloadSegment segment,
        IncrementalHash hash,
        CancellationToken token)
    {
        const int bufferSize = 64 * 1024;
        var buffer = new byte[bufferSize];
        var remaining = segment.Received;
        var offset = segment.Start;

        using var file = new FileStream(
            task.PartFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (remaining > 0)
        {
            var want = (int)Math.Min(bufferSize, remaining);
            file.Position = offset;

            var read = await file.ReadAsync(buffer.AsMemory(0, want), token).ConfigureAwait(false);
            if (read <= 0) break;

            hash.AppendData(buffer, 0, read);
            offset += read;
            remaining -= read;
        }
    }

    /// <summary>206 的起点；200（服务端忽略 Range）一律从 0 算。</summary>
    private static long ResponseStart(DownloadResponse response, long requestedStart)
        => response.StatusCode == 206 ? requestedStart : 0;



    /// <summary>
    /// 收尾：确认全部到齐 → 临时文件改名 → 计算 SHA1 → 与期望值比对。
    /// 不一致时先用分片哈希定位坏片、只重下坏片；定位不到才整份重下，
    /// 直到通过或用尽 <see cref="DownloadSettings.IntegrityRetries"/>。
    /// </summary>
    private async Task FinalizeAsync(DownloadTask task, CancellationToken token)
    {
        for (var round = 0; round <= _settings.IntegrityRetries; round++)
        {
            token.ThrowIfCancellationRequested();

            var missing = task.Segments.Where(s => !s.IsComplete).ToList();
            if (missing.Count > 0)
                throw new DownloadException($"仍有 {missing.Count} 个分片未完成，请点「继续」重试");

            var partPath = task.PartFilePath;

            if (!File.Exists(partPath))
                throw new DownloadException("临时文件不存在（可能被清理软件删除），请点「重新下载」");

            var actual = await ComputeFileSha1Async(partPath, token).ConfigureAwait(false);

            var expected = task.ExpectedChecksum;
            var passed = expected is null || expected.Matches(actual);

            if (passed)
            {
                CommitPartFile(task, actual);

                lock (_gate)
                {
                    task.ActualSha1 = actual;
                    task.ChecksumNote = expected is null
                        ? $"未校验（服务端未提供校验值），本地 SHA1：{Short(actual)}"
                        : $"SHA1 校验通过（{Short(actual)}）";
                    task.Status = DownloadStatus.Completed;
                    task.BytesPerSecond = 0;
                    task.CompletedAt = DateTimeOffset.Now;
                    task.ErrorMessage = null;
                }

                RaiseTaskChanged(task);
                SaveAsync();
                return;
            }

            if (round == _settings.IntegrityRetries)
            {
                lock (_gate)
                {
                    task.ActualSha1 = actual;
                    task.Status = DownloadStatus.Failed;
                    task.BytesPerSecond = 0;
                    task.ErrorMessage =
                        $"{expected!.KindText.ToUpperInvariant()} 校验失败：期望 {Short(expected.HexValue)}，" +
                        $"实际 {Short(actual)}。已重试 {_settings.IntegrityRetries} 次仍未通过，请检查下载源。";
                    task.ChecksumNote = task.ErrorMessage;
                }

                RaiseTaskChanged(task);
                SaveAsync();
                return;
            }

            var damaged = await FindDamagedSegmentsAsync(task, token).ConfigureAwait(false);

            if (damaged.Count > 0)
            {
                Notice?.Invoke(this,
                    $"{task.FileName} 校验不一致：第 {damaged.Count} 个分片与落盘哈希对不上，正在重下这些分片");

                await RedownloadSegmentsAsync(task, damaged, token).ConfigureAwait(false);
            }
            else
            {
                // 一片都没定位到：坏数据是在传输过程中混进来的（片哈希算的就是这些坏字节），
                // 光靠本地记录分不出来是哪一片，只能整份重来。
                Notice?.Invoke(this,
                    $"{task.FileName} 校验不一致且无法定位到具体分片，整份重下（第 {round + 1} 次）…");

                await RedownloadMismatchedAsync(task, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 校验失败且定位不到坏片时的兜底：清空全部断点并重传。
    /// </summary>
    private async Task RedownloadMismatchedAsync(DownloadTask task, CancellationToken token)
    {
        lock (_gate)
        {
            task.ResetSegmentProgress();
            task.Attempts++;
        }

        TryDeleteFile(task.PartFilePath);
        PreparePartFile(task);
        await TransferAllAsync(task, token).ConfigureAwait(false);
    }

    /// <summary>只重下坏片：这几片的断点清零，其余片已下的字节原样保留。</summary>
    private async Task RedownloadSegmentsAsync(
        DownloadTask task,
        IReadOnlyList<DownloadSegment> damaged,
        CancellationToken token)
    {
        lock (_gate)
        {
            foreach (var segment in damaged)
            {
                segment.Received = 0;
                segment.Sha1 = null;
            }

            task.Attempts++;
        }

        await TransferSegmentsAsync(task, [.. damaged], token).ConfigureAwait(false);
    }

    /// <summary>
    /// 逐片重算盘上字节的 SHA1，与传输时记下的片哈希比对，挑出对不上的片。
    ///
    /// 返回空集合表示「定位不到」，调用方据此整份重下。两种定位不到的情况：
    /// <list type="bullet">
    /// <item>只有一个分片——重下一片与整份重下是同一件事，没必要走这条路；</item>
    /// <item>有片没有落盘哈希（例如断点来自旧版本、或该片还没下完）——没有比对基准。</item>
    /// </list>
    ///
    /// 能定位到的是「写完之后被改坏」的那类问题（磁盘静默错误、被外部程序改写、
    /// 断点位置与实际字节不符）。传输途中就混进来的坏数据定位不到——片哈希算的
    /// 正是这些坏字节本身，只能靠整份重下兜住。
    /// </summary>
    internal static async Task<List<DownloadSegment>> FindDamagedSegmentsAsync(
        DownloadTask task,
        CancellationToken token)
    {
        var segments = task.Segments;

        if (segments.Count < 2) return [];
        if (segments.Any(s => string.IsNullOrEmpty(s.Sha1) || s.Length <= 0)) return [];
        if (!File.Exists(task.PartFilePath)) return [];

        const int bufferSize = 64 * 1024;
        var buffer = new byte[bufferSize];
        var damaged = new List<DownloadSegment>();

        await using var file = new FileStream(
            task.PartFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        foreach (var segment in segments)
        {
            file.Position = segment.Start;

            var remaining = segment.Length;
            while (remaining > 0)
            {
                var want = (int)Math.Min(bufferSize, remaining);
                var read = await file.ReadAsync(buffer.AsMemory(0, want), token).ConfigureAwait(false);
                if (read <= 0) break;

                sha1.AppendData(buffer, 0, read);
                remaining -= read;
            }

            // 盘上这段比声明的短（文件被截断）也按坏片处理：重下能补齐
            if (remaining > 0)
            {
                damaged.Add(segment);
                sha1.GetHashAndReset();
                continue;
            }

            var actual = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
            if (!DownloadChecksum.EqualsHex(actual, segment.Sha1)) damaged.Add(segment);
        }

        return damaged;
    }

    /// <summary>临时文件改名为最终文件。同名文件直接覆盖（「重新下载」的语义就是替换）。</summary>
    private static void CommitPartFile(DownloadTask task, string sha1)
    {
        var partPath = task.PartFilePath;
        var target = task.FilePath;

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.Move(partPath, target, overwrite: true);

        // 保留一份边车校验文件：下次想复核时不用再问服务端
        try
        {
            File.WriteAllText(target + ".sha1", $"{sha1}  {task.FileName}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 边车文件是可选的，写不进去不影响下载结果
        }
    }

    // ------------------------------------------------------------------
    // 重试、速度、保存
    // ------------------------------------------------------------------

    private bool ShouldRetry(Exception ex, CancellationToken token, int attempt)
    {
        if (token.IsCancellationRequested) return false;
        if (attempt > _settings.Retries) return false;

        return ex switch
        {
            DownloadException download => download.IsTransient,
            HttpRequestException => true,
            IOException => true,
            TaskCanceledException => true,
            _ => false,
        };
    }

    /// <summary>指数退避：基准 × 2^(次数-1)，上限 30 秒。抖动 ±20% 避免多线程同时重试。</summary>
    private async Task DelayBeforeRetryAsync(DownloadTask task, int attempt, CancellationToken token)
    {
        var baseDelay = _settings.RetryBaseDelayMs * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(baseDelay, 30_000);

        var jitter = capped * 0.2 * (Random.Shared.NextDouble() * 2 - 1);
        var delay = Math.Max(0, capped + jitter);

        lock (_gate)
        {
            task.Attempts = Math.Max(task.Attempts, attempt);
        }

        RaiseTaskChanged(task);

        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false);
    }

    /// <summary>对「打开请求」这类一次性操作套用重试。</summary>
    private async Task<T> WithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        DownloadTask task,
        CancellationToken token,
        string description)
    {
        var attempt = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                return await operation(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRetry(ex, token, ++attempt))
            {
                await DelayBeforeRetryAsync(task, attempt, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                throw new DownloadException($"{description}失败：{ex.Message}", false, ex);
            }
        }
    }

    private static async Task<string> ComputeFileSha1Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1024 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read <= 0) break;
            sha1.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>每秒采样一次：把这一秒内的字节数写进任务，界面据此实时显示。</summary>
    private void SampleSpeeds()
    {
        if (_disposed) return;

        var changed = new List<DownloadTask>();

        foreach (var (id, sampler) in _speeds)
        {
            var task = Find(id);
            if (task is null)
            {
                _speeds.TryRemove(id, out _);
                continue;
            }

            var bytes = sampler.TakeBytes();
            double speed;

            lock (_gate)
            {
                // 指数平滑：单窗口采样在分片收尾时会抖动（某秒恰好没数据）
                var previous = task.BytesPerSecond;
                speed = previous <= 0 ? bytes : previous * 0.4 + bytes * 0.6;

                if (task.Status != DownloadStatus.Downloading) speed = 0;

                task.BytesPerSecond = speed;
            }

            changed.Add(task);
        }

        foreach (var task in changed) RaiseTaskChanged(task);
    }

    private void RaiseTaskChanged(DownloadTask task)
        => TaskChanged?.Invoke(this, task);

    private void RaiseTasksChanged()
        => TasksChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 保存任务列表。串行化并做「合并」：连点几次暂停不会排队写五次盘。
    /// </summary>
    public void SaveAsync()
    {
        if (_disposed) return;
        _ = SaveCoreAsync();
    }

    private async Task SaveCoreAsync()
    {
        try
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _store.Save(Tasks, out _);
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch
        {
            // 保存失败不能影响下载本身；下一次保存会再试
        }
    }

    private void CancelRunner(string id)
    {
        if (_cancellations.TryGetValue(id, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // runner 刚好收尾并释放了 cts，属于正常竞态
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件被占用时删不掉：留到下次「继续」时覆盖，不因此让操作失败
        }
    }

    private static string DescribeStatus(DownloadResponse response)
        => response.StatusCode switch
        {
            401 or 403 => "Forbidden",
            404 => "Not Found",
            429 => "Too Many Requests",
            _ => "",
        };

    /// <summary>只显示哈希前 12 位：完整 40 位在卡片上没人看得完。</summary>
    private static string Short(string? hex)
        => string.IsNullOrEmpty(hex) ? "—" : hex.Length <= 12 ? hex : hex[..12] + "…";
    /// <summary>保存前的等待：让 <see cref="DisposeAsync"/> 有机会把最后一次写入落完。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        foreach (var id in _cancellations.Keys) CancelRunner(id);

        // 先把状态落一份再停定时器：否则「关闭时最后一次进度」永远停留在上一个 5 秒点
        await SaveCoreAsync().ConfigureAwait(false);

        _disposed = true;
        _speedTimer.Dispose();
        _autoSaveTimer.Dispose();
        _lifetime.Cancel();

        // 全部 runner 停下后再保存一次，保证「退出时代码」不丢
        try
        {
            await Task.WhenAll(_running.Values).ConfigureAwait(false);
        }
        catch
        {
            // 停止过程中的异常不影响保存
        }

        await SaveCoreAsync().ConfigureAwait(false);

        _saveGate.Dispose();
        _lifetime.Dispose();

        if (_transport is IDisposable disposable) disposable.Dispose();
    }
}

/// <summary>滑动窗口速度采样：只记「上次取数以来收到多少字节」，间隔由定时器保证是 1 秒。</summary>
internal sealed class SpeedSampler
{
    private long _bytes;

    public void Add(int count) => Interlocked.Add(ref _bytes, count);

    public long TakeBytes() => Interlocked.Exchange(ref _bytes, 0);
}

/// <summary>分片规划结果 → 写进任务的小工具（避免在服务里写一长串 setter）。</summary>
internal static class SegmentPlanExtensions
{
    public static void ShouldApplyTo(
        this IReadOnlyList<(long Start, long End)> ranges,
        DownloadTask task,
        bool supportsRanges)
    {
        if (!supportsRanges)
        {
            task.SupportsRanges = false;
            task.EnsureSingleSegment();
            return;
        }

        task.SupportsRanges = true;

        // 已有断点且与新区间一致时保留进度，否则重建
        var existing = task.Segments;
        var sameShape = existing.Count == ranges.Count
                        && existing.Zip(ranges).All(p => p.First.Start == p.Second.Start && p.First.End == p.Second.End);

        if (sameShape) return;

        task.SetSegments(ranges.Select(r => new DownloadSegment { Start = r.Start, End = r.End }));
    }
}
