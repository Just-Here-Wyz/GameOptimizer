namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Download;

/// <summary>
/// 分片规划。锁死的不变式是「所有分片首尾相接、正好覆盖整个文件」：
/// 少一个字节，最后校验必然失败；多一个字节，写越界。
/// 这两种错误在真机上都是「偶尔校验不过」，靠人工根本定位不了。
/// </summary>
public class DownloadSegmentPlannerTests
{
    [Theory]
    [InlineData(1L, 4)]
    [InlineData(1024L, 4)]
    [InlineData(256L * 1024L, 4)]
    [InlineData(5L * 1024L * 1024L, 4)]
    [InlineData(5L * 1024L * 1024L, 16)]
    [InlineData(97L * 1024L * 1024L + 13, 7)]
    public void 分片必须完整覆盖文件且不重叠(long total, int segments)
    {
        var ranges = DownloadSegmentPlanner.Plan(total, segments);

        ranges.Should().NotBeEmpty();
        ranges[0].Start.Should().Be(0, "第一片必须从 0 开始");
        ranges[^1].End.Should().Be(total - 1, "最后一片必须落到最后一个字节");

        for (var i = 1; i < ranges.Count; i++)
        {
            ranges[i].Start.Should().Be(ranges[i - 1].End + 1,
                "相邻分片之间不能有空洞、也不能重叠");
        }

        var covered = ranges.Sum(r => r.End - r.Start + 1);
        covered.Should().Be(total);
    }

    [Fact]
    public void 请求的分片数只是上限而不是硬指标()
    {
        // 1MB 的文件要 16 片，平均每片 64KB —— 低于最小分片尺寸，应减少片数
        var ranges = DownloadSegmentPlanner.Plan(1024 * 1024, 16);

        ranges.Count.Should().BeLessThan(16);
        ranges.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void 小文件不分片()
    {
        // 分片带来的并发收益在 256KB 以下抵不过额外请求的开销
        var ranges = DownloadSegmentPlanner.Plan(100 * 1024, 8);

        ranges.Should().ContainSingle();
        ranges[0].Should().Be((0L, 100L * 1024 - 1));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void 长度未知或非法时不分片(long total)
    {
        DownloadSegmentPlanner.Plan(total, 4).Should().BeEmpty();
    }

    [Fact]
    public void 分片边界按4KB对齐()
    {
        // 对齐的目的：后续如果改成顺序写也能减少跨扇区的零碎写。
        // 除了最后一片，每片结束偏移都应当是 4096 的整数倍减一。
        var ranges = DownloadSegmentPlanner.Plan(10 * 1024 * 1024, 4);

        foreach (var range in ranges.Take(ranges.Count - 1))
        {
            ((range.End + 1) % DownloadSegmentPlanner.BlockSize).Should().Be(0,
                $"分片结束于 {range.End}，未按 4KB 对齐");
        }
    }

    [Fact]
    public void 分片数量不会超过请求值()
    {
        var ranges = DownloadSegmentPlanner.Plan(64 * 1024 * 1024, 4);
        ranges.Count.Should().BeLessThanOrEqualTo(4);
    }
}
