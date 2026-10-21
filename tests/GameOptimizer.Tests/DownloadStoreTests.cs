namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Download;

/// <summary>任务持久化：程序重启后能不能接着下，全看这里。</summary>
public class DownloadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "go-dl-store-tests", Guid.NewGuid().ToString("N"));

    public DownloadStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string StorePath => Path.Combine(_root, "downloads.json");

    private DownloadTask NewTask(
        DownloadStatus status = DownloadStatus.Downloading,
        IReadOnlyList<(long Start, long End, long Received)>? segments = null)
    {
        var task = new DownloadTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = "https://example.com/file.zip",
            FileName = "file.zip",
            Directory = _root,
            TotalBytes = 1024 * 1024,
            SupportsRanges = true,
            Status = status,
        };

        foreach (var (start, end, received) in segments ?? [(0L, 1024L * 1024L - 1, 4096L)])
            task.AddSegment(new DownloadSegment { Start = start, End = end, Received = received });

        return task;
    }

    [Fact]
    public void 保存后再读取应完整还原断点()
    {
        var store = new DownloadStore(StorePath);
        var task = NewTask();
        task.ExpectedChecksum = new DownloadChecksum("sha1", "abcdef1234567890abcdef1234567890abcdef12");

        store.Save([task], out var error).Should().BeTrue(error);

        var loaded = new DownloadStore(StorePath).Load(out var loadError);
        loadError.Should().BeNull();

        loaded.Should().ContainSingle();
        var restored = loaded[0];

        restored.Id.Should().Be(task.Id);
        restored.Url.Should().Be(task.Url);
        restored.FileName.Should().Be(task.FileName);
        restored.TotalBytes.Should().Be(task.TotalBytes);
        restored.SupportsRanges.Should().BeTrue();
        restored.ExpectedChecksum!.HexValue.Should().Be(task.ExpectedChecksum.HexValue);
        restored.Segments[0].Start.Should().Be(0);
        restored.Segments[0].Received.Should().Be(4096);
    }

    [Fact]
    public void 重启时下载中的任务应降级为暂停()
    {
        // 否则界面会显示「下载中」，但实际没有任何传输在跑 —— 假状态
        var store = new DownloadStore(StorePath);
        store.Save([NewTask(DownloadStatus.Downloading)], out _);

        var loaded = new DownloadStore(StorePath).Load(out _);

        loaded[0].Status.Should().Be(DownloadStatus.Paused);
    }

    [Fact]
    public void 排队中的任务重启后也应降级为暂停()
    {
        var store = new DownloadStore(StorePath);
        store.Save([NewTask(DownloadStatus.Queued)], out _);

        new DownloadStore(StorePath).Load(out _)[0].Status.Should().Be(DownloadStatus.Paused);
    }

    [Fact]
    public void 已完成与失败状态应原样保留()
    {
        var store = new DownloadStore(StorePath);
        var completed = NewTask(DownloadStatus.Completed);
        var failed = NewTask(DownloadStatus.Failed);
        failed.ErrorMessage = "服务器返回 404 Not Found";

        store.Save([completed, failed], out _);

        var loaded = new DownloadStore(StorePath).Load(out _);
        loaded.Should().HaveCount(2);
        loaded[0].Status.Should().Be(DownloadStatus.Completed);
        loaded[1].Status.Should().Be(DownloadStatus.Failed);
        loaded[1].ErrorMessage.Should().Contain("404");
    }

    [Fact]
    public void 文件不存在时应返回空列表而不是抛异常()
    {
        var store = new DownloadStore(Path.Combine(_root, "nope.json"));

        var loaded = store.Load(out var error);

        loaded.Should().BeEmpty();
        error.Should().BeNull();
    }

    [Fact]
    public void 文件损坏时应返回空列表并给出原因()
    {
        File.WriteAllText(StorePath, "{ 这不是 JSON");

        var loaded = new DownloadStore(StorePath).Load(out var error);

        loaded.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace("读不出来必须说清楚，不能假装没有任务");
    }

    [Fact]
    public void 保存时应先写临时文件再改名避免半截文件()
    {
        var store = new DownloadStore(StorePath);
        store.Save([NewTask()], out _);

        File.Exists(StorePath + ".tmp").Should().BeFalse("临时文件必须在改名后消失");
        File.Exists(StorePath).Should().BeTrue();
    }

    [Fact]
    public void 多分片的进度都应逐片保存()
    {
        var store = new DownloadStore(StorePath);
        var task = NewTask(segments:
        [
            (0, 262143, 1000),
            (262144, 524287, 2000),
            (524288, 1048575, 0),
        ]);

        store.Save([task], out _);
        var restored = new DownloadStore(StorePath).Load(out _)[0];

        restored.Segments.Should().HaveCount(3);
        restored.Segments.Select(s => s.Received).Should().Equal(1000, 2000, 0);
        restored.DownloadedBytes.Should().Be(3000);
    }

    [Fact]
    public void 文件名中的非法字符读取时应再次清洗()
    {
        // 配置可能被用户手改过，读回来的名字不能直接拿去落盘
        var store = new DownloadStore(StorePath);
        var dirty = NewTask();
        typeof(DownloadTask).GetProperty(nameof(DownloadTask.FileName))!
            .SetValue(dirty, "a:b*c.zip");

        store.Save([dirty], out _);

        new DownloadStore(StorePath).Load(out _)[0].FileName.Should().Be("a_b_c.zip");
    }

    [Fact]
    public void 保存失败应返回原因而不是抛异常()
    {
        // 用一个「同名目录占住文件路径」构造必然失败的写入：
        // Save 内部会自动 CreateDirectory，所以「目录不存在」不再构成失败条件。
        var blocked = Path.Combine(_root, "blocked.json");
        Directory.CreateDirectory(blocked);

        var store = new DownloadStore(blocked);
        store.Save([NewTask()], out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }
}
