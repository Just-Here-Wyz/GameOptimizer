namespace GameOptimizer.Tests;

using System.Security.Cryptography;
using FluentAssertions;
using GameOptimizer.Core.Download;

/// <summary>
/// 校验失败后的「坏片定位」行为。
///
/// 为什么单独一个文件：这条路径的难点不是下载，而是**能不能诚实地说清哪一片坏了**。
/// 传输途中混进来的坏字节，片哈希算的就是这些坏字节本身，本地记录定位不出来；
/// 只有「写完之后被改坏」才定位得到。两种结果界面表现完全不同（只重下一片 / 整份重来），
/// 必须分开钉死，否则很容易写出「永远整份重下」却对外宣称「只重下坏片」的代码。
/// </summary>
public class DownloadIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "go-dl-integrity", Guid.NewGuid().ToString("N"));

    public DownloadIntegrityTests() => Directory.CreateDirectory(_root);

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

    private static string Sha1(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private DownloadSettings FastSettings() => new()
    {
        ParallelSegments = 4,
        MaxConcurrentTasks = 2,
        RetryBaseDelayMs = 1,
        ReadStallTimeoutMs = 5_000,
        IntegrityRetries = 1,
        AutoResume = false,
        DefaultDirectory = _root,
    };

    /// <summary>
    /// 造一个「分片全部下完、但盘上字节被改坏了几处」的任务。
    /// 片哈希一律按**正确内容**计算 —— 模拟的就是「写完以后才坏」这类问题。
    /// </summary>
    private static DownloadTask SeedCompletedTask(
        string directory,
        byte[] payload,
        int requestedSegments,
        params long[] corruptOffsets)
    {
        var task = new DownloadTask
        {
            Id = "seed",
            Url = "https://fake.local/seed.bin",
            FileName = "seed.bin",
            Directory = directory,
            TotalBytes = payload.Length,
            SupportsRanges = true,
            Status = DownloadStatus.Paused,
        };

        task.SetSegments(DownloadSegmentPlanner.Plan(payload.Length, requestedSegments)
            .Select(r => new DownloadSegment
            {
                Start = r.Start,
                End = r.End,
                Received = r.End - r.Start + 1,
                Sha1 = Sha1(payload[(int)r.Start..(int)(r.End + 1)]),
            }));

        var onDisk = payload.ToArray();
        foreach (var offset in corruptOffsets) onDisk[offset] ^= 0xFF;

        File.WriteAllBytes(task.PartFilePath, onDisk);
        return task;
    }

    private static async Task<DownloadTask> WaitAsync(
        DownloadService service, string id, Func<DownloadTask, bool> until)
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
    public async Task 坏片定位应只挑出哈希对不上的分片()
    {
        var payload = Pattern(3 * 256 * 1024);
        var ranges = DownloadSegmentPlanner.Plan(payload.Length, 4);
        ranges.Should().HaveCountGreaterThan(1, "单片任务测不出定位能力");

        // 第二片中间改坏一个字节
        var task = SeedCompletedTask(_root, payload, 4, ranges[1].Start + 16);

        var damaged = await DownloadService.FindDamagedSegmentsAsync(task, CancellationToken.None);

        damaged.Should().HaveCount(1);
        damaged[0].Start.Should().Be(ranges[1].Start);
    }

    [Fact]
    public async Task 多片同时损坏时应全部挑出来()
    {
        var payload = Pattern(4 * 256 * 1024);
        var ranges = DownloadSegmentPlanner.Plan(payload.Length, 4);

        var task = SeedCompletedTask(_root, payload, 4, ranges[0].Start + 8, ranges[3].Start + 8);

        var damaged = await DownloadService.FindDamagedSegmentsAsync(task, CancellationToken.None);

        damaged.Select(s => s.Start).Should().Equal(ranges[0].Start, ranges[3].Start);
    }

    [Fact]
    public async Task 定位不到坏片时必须如实返回空集()
    {
        // 单分片：重下这一片与整份重下是同一件事，没必要单独走「只重下坏片」
        var single = SeedCompletedTask(_root, Pattern(300 * 1024), 1, 1024);
        (await DownloadService.FindDamagedSegmentsAsync(single, CancellationToken.None))
            .Should().BeEmpty();

        // 有片没有落盘哈希（断点来自旧版本 / 该片还没下完）：没有比对基准，不能瞎猜一片去重下
        var missing = SeedCompletedTask(_root, Pattern(3 * 256 * 1024), 4, 1024);
        missing.Segments[2].Sha1 = null;

        (await DownloadService.FindDamagedSegmentsAsync(missing, CancellationToken.None))
            .Should().BeEmpty();

        // 盘上没有临时文件：无从比对
        var noFile = SeedCompletedTask(_root, Pattern(3 * 256 * 1024), 4, 1024);
        File.Delete(noFile.PartFilePath);

        (await DownloadService.FindDamagedSegmentsAsync(noFile, CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task 校验失败时应只重下哈希对不上的分片()
    {
        var payload = Pattern(3 * 256 * 1024);
        var server = new FakeTransport(payload) { SupportsRanges = true, Digest = Sha1(payload) };

        // 断点预置成「分片都下完了，第二片被外部改坏」，交给服务去收尾。
        // 期望校验值必须一并预置：分段已齐时服务会跳过探测，
        // 没有期望值就变成「服务端未提供校验值」直接放行，测的就不是校验了。
        var ranges = DownloadSegmentPlanner.Plan(payload.Length, 4);
        var seeded = SeedCompletedTask(_root, payload, 4, ranges[1].Start + 16);
        seeded.ExpectedChecksum = new DownloadChecksum("sha1", Sha1(payload));

        var storePath = Path.Combine(_root, "store.json");
        new DownloadStore(storePath).Save([seeded], out _);

        await using var service = new DownloadService(
            server, new DownloadStore(storePath), FastSettings());

        await service.LoadAsync();
        service.Resume(seeded.Id);

        var done = await WaitAsync(
            service, seeded.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);

        // 关键断言：整份重下会为每个分片各发一次请求。只发出 1 次，说明确实只重下了坏片。
        server.RequestCount.Should().Be(1, "只有一个分片哈希对不上，其余分片不该重下");
    }

    [Fact]
    public async Task 传输途中混入坏数据时无法定位应整份重下并成功()
    {
        // CorruptFirstPass 改的是「被读到的字节」，片哈希算的正是这些坏字节，
        // 因此本地定位不出来 —— 这时必须整份重下，且仍然要能下对。
        var payload = Pattern(3 * 256 * 1024);
        var server = new FakeTransport(payload)
        {
            SupportsRanges = true,
            Digest = Sha1(payload),
            CorruptFirstPass = true,
        };

        await using var service = new DownloadService(
            server, new DownloadStore(Path.Combine(_root, "s.json")), FastSettings());

        var task = service.CreateTask(server.Url("inflight.bin"), _root, "inflight.bin", out _)!;
        var done = await WaitAsync(
            service, task.Id, t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        File.ReadAllBytes(done.FilePath).Should().Equal(payload);
    }
}
