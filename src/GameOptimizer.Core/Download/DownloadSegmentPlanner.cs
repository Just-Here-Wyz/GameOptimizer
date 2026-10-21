namespace GameOptimizer.Core.Download;

/// <summary>
/// 把文件切成若干连续分片。纯函数，单独放出来的理由：
/// 「切歪了」的表现是最后 1 个字节没人下（或某段被下两遍），
/// 这种 bug 在真机上极难看出来，必须用属性测试直接锁死。
/// </summary>
public static class DownloadSegmentPlanner
{
    /// <summary>小于这个尺寸不分片：多线程反而更慢，而且没有断点续传的价值。</summary>
    public const long MinTotalBytes = 256L * 1024L;

    /// <summary>分片边界按 4KB 对齐，落盘时不产生跨扇区的零碎写。</summary>
    public const long BlockSize = 4096L;

    /// <summary>
    /// 规划分片区间，保证：首片从 0 开始、末片结束于 <paramref name="totalBytes"/> - 1、
    /// 相邻分片首尾相接且不重叠。
    ///
    /// <paramref name="requestedSegments"/> 只是上限：文件不够大时会减少片数，
    /// 每片不小于 <see cref="MinTotalBytes"/>，避免切出一堆几百字节的请求。
    /// </summary>
    public static IReadOnlyList<(long Start, long End)> Plan(long totalBytes, int requestedSegments)
    {
        if (totalBytes <= 0) return [];

        var maxSegments = (int)Math.Max(1, totalBytes / MinTotalBytes);
        var count = Math.Clamp(requestedSegments, 1, Math.Max(1, Math.Min(maxSegments, 64)));

        if (count == 1 || totalBytes < MinTotalBytes)
            return [(0, totalBytes - 1)];

        var ranges = new List<(long Start, long End)>(count);
        var baseSize = totalBytes / count;
        var remainder = totalBytes % count;

        var start = 0L;
        for (var i = 0; i < count; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            if (i == count - 1)
            {
                // 最后一片吃掉全部剩余，杜绝「四舍五入丢字节」
                ranges.Add((start, totalBytes - 1));
                break;
            }

            // 对齐到块大小。对齐的是**下一片的起点**（= 本片 End + 1），
            // 所以要先算出边界再减一：直接对齐 End 会让区间错开一个字节。
            var next = AlignUp(start + size);
            if (next >= totalBytes)
            {
                ranges.Add((start, totalBytes - 1));
                break;
            }

            ranges.Add((start, next - 1));
            start = next;
        }

        return ranges;
    }

    private static long AlignUp(long offset)
    {
        var remainder = offset % BlockSize;
        return remainder == 0 ? offset : offset + (BlockSize - remainder);
    }
}
