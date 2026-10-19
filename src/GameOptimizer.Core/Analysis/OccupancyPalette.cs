namespace GameOptimizer.Core.Analysis;

/// <summary>占用率的颜色档位。仪表盘与进度条共用同一套口径。</summary>
public enum OccupancyLevel
{
    /// <summary>0% ~ 30%，余量充足。</summary>
    Low = 0,
    /// <summary>31% ~ 60%，正常。</summary>
    Normal = 1,
    /// <summary>61% ~ 80%，偏高。</summary>
    High = 2,
    /// <summary>81% ~ 100%，吃紧。</summary>
    Critical = 3,
}

/// <summary>
/// 占用率 → 颜色档位。
///
/// 用户约定：0~30 绿、31~60 蓝、61~80 橙、81~100 红。
/// 分档写在 Core 里而不是 XAML 触发器里，这样能直接单测，
/// 也不会出现「首页仪表绿、别处进度条红」这种两套口径。
/// </summary>
public static class OccupancyPalette
{
    /// <summary>绿色档的上限（含）。</summary>
    public const double LowMax = 30;

    /// <summary>蓝色档的上限（含）。</summary>
    public const double NormalMax = 60;

    /// <summary>橙色档的上限（含）。</summary>
    public const double HighMax = 80;

    /// <summary>
    /// 判定档位。入参先钳制到 [0,100]：PDH 在读数间隔异常时会算出 123%，
    /// 非有限值按 0 处理，避免界面上出现荒谬的颜色。
    /// </summary>
    public static OccupancyLevel Level(double percent)
    {
        var p = Clamp(percent);

        if (p <= LowMax) return OccupancyLevel.Low;
        if (p <= NormalMax) return OccupancyLevel.Normal;
        if (p <= HighMax) return OccupancyLevel.High;
        return OccupancyLevel.Critical;
    }

    /// <summary>档位对应的十六进制颜色，与 App 里的画刷资源一一对应。</summary>
    public static string ColorHex(OccupancyLevel level) => level switch
    {
        OccupancyLevel.Low => "#34C759",
        OccupancyLevel.Normal => "#4C8DFF",
        OccupancyLevel.High => "#FF9F0A",
        _ => "#FF453A",
    };

    /// <summary>钳制后的百分比（取整前的值，保留一位小数便于显示）。</summary>
    public static double Clamp(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent)) return 0;
        return Math.Clamp(percent, 0, 100);
    }
}
