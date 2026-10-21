namespace GameOptimizer.Core.Download;

using System.Diagnostics;

/// <summary>
/// 全局下载限速器：一个令牌桶，**所有任务、所有分片共享**。
///
/// 为什么不是「每个分片各限 1/N」：那样在分片数变化时总速度会跳变，
/// 且用户看到的是「总速度」这一个数字，限速也必须是同一个口径。
///
/// 令牌桶参数：容量 = 半秒的量（允许一点突发，避免每读一次就卡一次），
/// 补充速率 = 用户设定的每秒字节数。
/// </summary>
public sealed class Throttle
{
    private const int MinChunk = 1024;
    private const int MaxChunk = 81920;

    private readonly object _gate = new();
    private long _capacity;
    private long _bits;            // 放大 1000 倍用整数计，避免长时间累计的浮点误差
    private long _lastTimestamp;
    private long _limit;

    public Throttle(long bytesPerSecond = 0) => Limit = bytesPerSecond;

    /// <summary>限速值（字节/秒）。0 = 不限速。</summary>
    public long Limit
    {
        get
        {
            lock (_gate) return _limit;
        }
        set
        {
            lock (_gate)
            {
                _limit = Math.Max(0, value);
                _capacity = Math.Max(_limit / 2, 1);
                _bits = _capacity * 1000;
                _lastTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>单次读取的建议大小：限速越低读得越细，否则一次读满 80KB 会立刻超限。</summary>
    public int ChunkSize
    {
        get
        {
            var limit = Limit;
            return limit <= 0 ? MaxChunk : (int)Math.Clamp(limit / 8, MinChunk, MaxChunk);
        }
    }

    /// <summary>
    /// 申请 <paramref name="bytes"/> 的令牌，不够就等。
    /// 返回 <c>false</c> 表示被取消，调用方必须原样返回、不写盘。
    /// </summary>
    public async ValueTask<bool> WaitAsync(int bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_limit <= 0) return true;
        }

        while (true)
        {
            long waitMs;
            lock (_gate)
            {
                if (_limit <= 0) return true;

                RefillLocked();

                if (_bits >= (long)bytes * 1000)
                {
                    _bits -= (long)bytes * 1000;
                    return true;
                }

                var need = (long)bytes * 1000 - _bits;
                waitMs = Math.Max(1, need / _limit);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private void RefillLocked()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastTimestamp == 0)
        {
            _lastTimestamp = now;
            return;
        }

        var elapsedMs = (now - _lastTimestamp) * 1000d / Stopwatch.Frequency;
        _lastTimestamp = now;

        if (elapsedMs <= 0) return;

        _bits += (long)(_limit * elapsedMs);
        var cap = _capacity * 1000;
        if (_bits > cap) _bits = cap;
    }
}
