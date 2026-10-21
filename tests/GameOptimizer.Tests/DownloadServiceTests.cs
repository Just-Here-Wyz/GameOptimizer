namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Download;

/// <summary>
/// 下载内核的行为测试。全部走「假 HTTP 层」<see cref="FakeTransport"/>：
/// 断点续传、Range 被忽略、校验失败重下、指数退避这些路径，
/// 用真网络复现代价极高且不稳定，但恰恰是最容易写错的地方。
/// </summary>
public class DownloadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "go-dl-tests", Guid.NewGuid().ToString("N"));

    public DownloadServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 文件仍被占用时留给系统清理，不影响用例结论
        }
    }

    private static byte[] Pattern(int length, int seed = 0)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)((i * 31 + seed) % 251);
        return data;
    }

    private DownloadSettings FastSettings(Action<DownloadSettings>? tweak = null)
    {
        var settings = new DownloadSettings
        {
            ParallelSegments = 4,
            MaxConcurrentTasks = 2,
            RetryBaseDelayMs = 1,
            ReadStallTimeoutMs = 5_000,
            IntegrityRetries = 1,
            AutoResume = false,
            DefaultDirectory = _root,
        };

        tweak?.Invoke(settings);
        return settings;
    }

    private static async Task<DownloadTask> WaitAsync(DownloadService service, string id, Func<DownloadTask, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var task = service.Find(id);
            if (task is not null && until(task)) return task;
            await Task.Delay(10);
        }

        var last = service.Find(id);
        throw new Xunit.Sdk.XunitException(
            $"等待超时：状态={last?.Status} 错误={last?.ErrorMessage}");
    }

    [Fact]
    public async Task 分片下载应还原出与源文件完全一致的字节()
    {
        var payload = Pattern(1024 * 1024 + 12345);
        var server = new FakeTransport(payload) { SupportsRanges = true };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("game.zip"), _root, "game.zip", out var error);
        error.Should().BeNull();
        task.Should().NotBeNull();

        var done = await WaitAsync(service, task!.Id, t => t.Status == DownloadStatus.Completed);

        done.FileName.Should().Be("game.zip");
        done.TotalBytes.Should().Be(payload.Length);
        File.Exists(done.FilePath).Should().BeTrue();
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);
        done.ProgressPercent.Should().Be(100);
    }

    [Fact]
    public async Task 服务端不支持Range时应退回单线程并仍然下全()
    {
        var payload = Pattern(700 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = false };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("no-range.bin"), _root, "no-range.bin", out _)!;
        var done = await WaitAsync(service, task.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        done.SupportsRanges.Should().BeFalse();
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);
    }

    [Fact]
    public async Task 服务端忽略Range返回整份文件时应整任务回退单流()
    {
        // 探测阶段回了 206（看起来支持分片），真正取分片时又回 200。
        // 旧实现会在分片线程里各自截断共享的临时文件，导致若干分片永远下不完。
        var payload = Pattern(900 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, IgnoreRangeAfterProbe = true };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("liar.bin"), _root, "liar.bin", out _)!;
        var done = await WaitAsync(service, task.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);
    }

    [Fact]
    public async Task 下载完成后应给出SHA1校验结论()
    {
        var payload = Pattern(300 * 1024);
        var sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(payload)).ToLowerInvariant();
        var server = new FakeTransport(payload) { SupportsRanges = true, Digest = sha1 };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("ok.bin"), _root, "ok.bin", out _)!;
        var done = await WaitAsync(service, task.Id, t => t.Status == DownloadStatus.Completed);

        done.ExpectedChecksum.Should().NotBeNull();
        done.ActualSha1.Should().Be(sha1);
        done.ChecksumNote.Should().Contain("校验通过");

        // 边车文件让用户以后能自己复核
        File.Exists(done.FilePath + ".sha1").Should().BeTrue();
    }

    [Fact]
    public async Task 服务端未提供校验值时必须如实说明未校验()
    {
        var payload = Pattern(200 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, Digest = null };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("unknown.bin"), _root, "unknown.bin", out _)!;
        var done = await WaitAsync(service, task.Id, t => t.Status == DownloadStatus.Completed);

        done.ChecksumNote.Should().Contain("未校验");
        done.ActualSha1.Should().NotBeNullOrEmpty("本地哈希仍然要算出来给用户看");
    }

    [Fact]
    public async Task 校验失败且重试用尽后任务必须失败并说明原因()
    {
        var payload = Pattern(400 * 1024);
        var wrong = new string('a', 40);
        var server = new FakeTransport(payload) { SupportsRanges = true, Digest = wrong };

        await using var service = new DownloadService(
            server,
            new DownloadStore(Path.Combine(_root, "s.json")),
            FastSettings(s => { s.IntegrityRetries = 0; }));

        var task = service.CreateTask(server.Url("bad.bin"), _root, "bad.bin", out _)!;
        var done = await WaitAsync(service, task.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.Status.Should().Be(DownloadStatus.Failed);
        done.ErrorMessage.Should().Contain("校验失败");
        File.Exists(done.FilePath).Should().BeFalse("校验不通过的文件不能当成下载成功交出去");
    }

    [Fact]
    public async Task 校验失败但重试一轮后通过时任务应成功()
    {
        var payload = Pattern(400 * 1024);
        var sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(payload)).ToLowerInvariant();

        // 第一次完整返回的数据被故意弄坏一格，第二轮才恢复正常
        var server = new FakeTransport(payload) { SupportsRanges = true, Digest = sha1, CorruptFirstPass = true };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("flaky.bin"), _root, "flaky.bin", out _)!;
        var done = await WaitAsync(service, task.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);
    }

    [Fact]
    public async Task 网络错误应按指数退避重试后成功()
    {
        var payload = Pattern(256 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, FailFirstRequests = 2 };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("retry.bin"), _root, "retry.bin", out _)!;
        var done = await WaitAsync(service, task.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        server.RequestCount.Should().BeGreaterThan(2, "失败两次后必须继续重试");
    }

    [Fact]
    public async Task 重试次数用尽后任务失败并保留连接错误说明()
    {
        var payload = Pattern(128 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, FailFirstRequests = 100 };

        await using var service = new DownloadService(
            server,
            new DownloadStore(Path.Combine(_root, "s.json")),
            FastSettings(s => s.Retries = 2));

        var task = service.CreateTask(server.Url("dead.bin"), _root, "dead.bin", out _)!;
        var done = await WaitAsync(service, task.Id, t => t.Status is DownloadStatus.Failed);

        done.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task 断点续传应只下剩下的字节()
    {
        var payload = Pattern(2 * 1024 * 1024);
        var storePath = Path.Combine(_root, "resume.json");

        // 第一轮：下到一半就暂停。
        // StallAfterBytes 要把探测请求（1 字节）也覆盖进去，所以按「探测 + 半份」算。
        var server = new FakeTransport(payload) { SupportsRanges = true, StallAfterBytes = payload.Length / 2 };

        var first = new DownloadService(server, new DownloadStore(storePath), FastSettings());
        var task = first.CreateTask(server.Url("resume.bin"), _root, "resume.bin", out _)!;

        var paused = await WaitAsync(first, task.Id, t => t.DownloadedBytes >= 256 * 1024);
        first.Pause(paused.Id);
        await WaitAsync(first, paused.Id, t => t.Status == DownloadStatus.Paused);

        await first.DisposeAsync();

        paused.DownloadedBytes.Should().BeGreaterThan(0);
        paused.DownloadedBytes.Should().BeLessThan(payload.Length, "中途暂停不该已经下完");

        // 磁盘上必须留下断点文件
        Directory.GetFiles(_root, "*.gpo-part").Should().NotBeEmpty("暂停后应当保留分片文件用于续传");

        var resumedFrom = paused.DownloadedBytes;

        // 第二轮：重启后继续
        var server2 = new FakeTransport(payload) { SupportsRanges = true };
        await using var second = new DownloadService(server2, new DownloadStore(storePath), FastSettings());
        await second.LoadAsync();

        var loaded = second.Tasks.Should().ContainSingle().Subject;
        loaded.Status.Should().Be(DownloadStatus.Paused, "用户主动暂停的任务重启后必须仍是暂停");
        loaded.DownloadedBytes.Should().Be(resumedFrom, "断点必须逐字节还原");

        second.Resume(loaded.Id);
        var done = await WaitAsync(second, loaded.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);
    }

    [Fact]
    public async Task 被硬中断的任务重启后应自动续传()
    {
        var payload = Pattern(2 * 1024 * 1024);
        var storePath = Path.Combine(_root, "crash.json");

        var server = new FakeTransport(payload) { SupportsRanges = true, StallAfterBytes = payload.Length / 2 };

        // 不用 DisposeAsync（那等于正常退出）：直接把服务丢掉，模拟进程被杀
        var first = new DownloadService(server, new DownloadStore(storePath), FastSettings());
        var task = first.CreateTask(server.Url("crash.bin"), _root, "crash.bin", out _)!;
        await WaitAsync(first, task.Id, t => t.DownloadedBytes >= 256 * 1024);

        first.Find(task.Id)!.WasInterrupted = true;

        // 等定时保存把「下载中 + 被中断」落盘
        first.SaveAsync();
        await WaitUntilAsync(() => File.Exists(storePath) && File.ReadAllText(storePath).Contains("\"wasInterrupted\": true"));

        // 模拟进程被杀：第一个实例必须先停掉（它的分片线程还握着断点文件的写句柄），
        // 但**不**通过它的 DisposeAsync 保存状态 —— 状态早已在上一步落盘。
        await first.DisposeAsync();

        var server2 = new FakeTransport(payload) { SupportsRanges = true };
        await using var second = new DownloadService(
            server2,
            new DownloadStore(storePath),
            FastSettings(s => s.AutoResume = true));

        await second.LoadAsync();

        var loaded = second.Tasks.Should().ContainSingle().Subject;
        loaded.Status.Should().BeOneOf(DownloadStatus.Queued, DownloadStatus.Downloading);

        var done = await WaitAsync(second, loaded.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);
    }

    /// <summary>等一个条件成立（用于等异步保存落盘），超时直接判失败。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException("等待条件成立超时");
    }

    [Fact]
    public async Task 暂停后继续应能完成()
    {
        var payload = Pattern(1536 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, StallAfterBytes = 300 * 1024 };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("pause.bin"), _root, "pause.bin", out _)!;
        var paused = await WaitAsync(service, task.Id, t => t.DownloadedBytes >= 128 * 1024);

        service.Pause(paused.Id);
        await WaitAsync(service, paused.Id, t => t.Status == DownloadStatus.Paused);

        // 放开限速继续
        server.StallAfterBytes = long.MaxValue;
        service.Resume(paused.Id);

        var done = await WaitAsync(service, paused.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);
    }

    [Fact]
    public async Task 取消应删除断点文件并可重新开始()
    {
        var payload = Pattern(1024 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, StallAfterBytes = 256 * 1024 };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("cancel.bin"), _root, "cancel.bin", out _)!;
        var running = await WaitAsync(service, task.Id, t => t.DownloadedBytes > 0);

        service.Cancel(running.Id);
        var canceled = await WaitAsync(service, running.Id, t => t.Status == DownloadStatus.Canceled);

        canceled.DownloadedBytes.Should().Be(0, "取消后断点必须清空，否则下次会接着旧数据写");
        canceled.ErrorMessage.Should().BeNull("用户主动取消不是失败");

        server.StallAfterBytes = long.MaxValue;
        service.Resume(canceled.Id);

        var done = await WaitAsync(service, canceled.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);
    }

    [Fact]
    public async Task 重新下载应覆盖已有文件()
    {
        var payload = Pattern(512 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("again.bin"), _root, "again.bin", out _)!;
        await WaitAsync(service, task.Id, t => t.Status == DownloadStatus.Completed);

        service.Restart(task.Id);
        var done = await WaitAsync(service, task.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);
    }

    [Fact]
    public async Task 并发上限应生效()
    {
        var payload = Pattern(512 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, StallAfterBytes = 128 * 1024 };

        await using var service = new DownloadService(
            server,
            new DownloadStore(Path.Combine(_root, "s.json")),
            FastSettings(s => { s.MaxConcurrentTasks = 1; s.ParallelSegments = 1; }));

        var first = service.CreateTask(server.Url("one.bin"), _root, "one.bin", out _)!;
        var second = service.CreateTask(server.Url("two.bin"), _root, "two.bin", out _)!;

        await WaitAsync(service, first.Id, t => t.Status == DownloadStatus.Downloading);

        var queued = service.Find(second.Id)!;
        queued.Status.Should().Be(DownloadStatus.Queued, "并发上限为 1 时第二个任务必须排队而不是同时开跑");

        // 收尾：放开后应当依次跑完
        server.StallAfterBytes = long.MaxValue;
        await WaitAsync(service, first.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        await WaitAsync(service, second.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);
    }

    [Fact]
    public async Task 全部暂停与全部开始应作用于所有任务()
    {
        var payload = Pattern(512 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, StallAfterBytes = 128 * 1024 };

        await using var service = new DownloadService(
            server, new DownloadStore(Path.Combine(_root, "s.json")),
            FastSettings(s => s.MaxConcurrentTasks = 2));

        var a = service.CreateTask(server.Url("a.bin"), _root, "a.bin", out _)!;
        var b = service.CreateTask(server.Url("b.bin"), _root, "b.bin", out _)!;

        await WaitAsync(service, a.Id, t => t.Status == DownloadStatus.Downloading);
        await WaitAsync(service, b.Id, t => t.Status == DownloadStatus.Downloading);

        service.PauseAll();
        await WaitAsync(service, a.Id, t => t.Status == DownloadStatus.Paused);
        await WaitAsync(service, b.Id, t => t.Status == DownloadStatus.Paused);

        server.StallAfterBytes = long.MaxValue;
        service.ResumeAll();

        await WaitAsync(service, a.Id, t => t.Status == DownloadStatus.Completed);
        await WaitAsync(service, b.Id, t => t.Status == DownloadStatus.Completed);
    }

    [Fact]
    public async Task 清空已完成只移除已完成的任务()
    {
        var payload = Pattern(128 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var done = service.CreateTask(server.Url("done.bin"), _root, "done.bin", out _)!;
        await WaitAsync(service, done.Id, t => t.Status == DownloadStatus.Completed);

        // 第二个任务随便什么状态都行，这里要验的是「清空只动已完成」
        var other = service.CreateTask(server.Url("other.bin"), _root, "other.bin", out _)!;
        service.Pause(other.Id);
        await WaitAsync(service, other.Id, t => t.Status == DownloadStatus.Paused);

        service.ClearCompleted().Should().Be(1);
        service.Tasks.Should().ContainSingle().Which.Id.Should().Be(other.Id);

        // 清空记录不该删掉已下好的文件
        File.Exists(done.FilePath).Should().BeTrue();
    }

    [Fact]
    public async Task 非法链接与空路径应被拒绝并给出原因()
    {
        await using var service = new DownloadService(
            new FakeTransport([]), new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        service.CreateTask("ftp://example.com/a.zip", _root, "a.zip", out var urlError).Should().BeNull();
        urlError.Should().Contain("http");

        service.CreateTask("https://example.com/a.zip", "  ", "a.zip", out var dirError).Should().BeNull();
        dirError.Should().Contain("保存路径");
    }

    [Fact]
    public async Task 统计栏应汇总速度与任务数()
    {
        var payload = Pattern(512 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, StallAfterBytes = 256 * 1024 };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        service.Statistics.TotalTasks.Should().Be(0);

        var task = service.CreateTask(server.Url("stat.bin"), _root, "stat.bin", out _)!;
        await WaitAsync(service, task.Id, t => t.DownloadedBytes > 0);

        var stats = service.Statistics;
        stats.TotalTasks.Should().Be(1);
        stats.RunningTasks.Should().Be(1);

        service.Pause(task.Id);
        await WaitAsync(service, task.Id, t => t.Status == DownloadStatus.Paused);

        service.Statistics.RunningTasks.Should().Be(0);
        service.Statistics.TotalBytesPerSecond.Should().Be(0);
    }

    [Fact]
    public async Task 未提供文件名的链接应采用服务端给的文件名()
    {
        var payload = Pattern(64 * 1024);
        var server = new FakeTransport(payload)
        {
            SupportsRanges = true,
            SuggestedFileName = "官方文件名.zip",
        };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        // 链接里没有文件名（只有目录 + 查询串），只能靠服务端 Content-Disposition
        var task = service.CreateTask(server.Url("download?id=1"), _root, null, out _)!;
        var done = await WaitAsync(service, task.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        done.FileName.Should().Be("官方文件名.zip");
    }

    [Fact]
    public async Task 用户手填的文件名不应被服务端覆盖()
    {
        var payload = Pattern(64 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, SuggestedFileName = "server.bin" };

        await using var service = new DownloadService(server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("x.bin"), _root, "用户起的名字.bin", out _)!;
        var done = await WaitAsync(service, task.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.FileName.Should().Be("用户起的名字.bin");
    }
}
