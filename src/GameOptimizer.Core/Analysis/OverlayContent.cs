namespace GameOptimizer.Core.Analysis;

using GameOptimizer.Core.Collection;
using GameOptimizer.Core.Models;

/// <summary>悬浮窗上的一行指标。</summary>
/// <param name="Label">短标签，悬浮窗空间小，只用两个字。</param>
/// <param name="Value">显示值，如 "45%" / "7.4GB"。</param>
/// <param name="Percent">对应占用率，已钳制到 [0,100]，着色与条形宽度都靠它。</param>
public sealed record OverlayMetric(string Label, string Value, double Percent)
{
    /// <summary>
    /// 钳制后的占用率。构造时就要钳，否则会出现「颜色对、数字错」——
    /// PDH 实测给过 123%，不钳的话界面会显示 123% 却配红色。
    /// </summary>
    public double Percent { get; init; } = OccupancyPalette.Clamp(Percent);

    /// <summary>与仪表盘同一套四档配色。</summary>
    public string ColorHex => OccupancyPalette.ColorHex(OccupancyPalette.Level(Percent));
}

/// <summary>
/// 游戏内悬浮窗的数据模型。
///
/// **只显示硬件占用，不显示帧率**：帧率要靠 PresentMon 定向监控游戏进程，
/// 那正是 ACE / Vanguard 的敏感点。这里所有指标都来自系统级性能计数器，
/// 不接触游戏进程任何信息，因此不会被判为异常。
///
/// **只对窗口化 / 无边框窗口化游戏可见**：普通置顶窗口会被独占全屏画面盖住。
/// 要盖住独占全屏只能注入或 Hook D3D，与「不注入游戏进程」原则冲突。
/// 详见 docs/04-已知限制.md。
///
/// 放 Core 的原因与 <see cref="CpuDetail"/> 一样：取哪些指标、怎么格式化、
/// 什么时候该警告，都是可测逻辑；界面只负责画一个半透明小窗。
/// </summary>
public sealed class OverlayContent
{
    private OverlayContent(IReadOnlyList<OverlayMetric> metrics, string hint, bool hasWarning)
    {
        Metrics = metrics;
        Hint = hint;
        HasWarning = hasWarning;
    }

    /// <summary>要显示的指标行。</summary>
    public IReadOnlyList<OverlayMetric> Metrics { get; }

    /// <summary>底部提示（例如采集异常），无内容时为空串，界面据此收起。</summary>
    public string Hint { get; }

    /// <summary>是否处于异常状态（提示用红色）。</summary>
    public bool HasWarning { get; }

    /// <summary>
    /// 由一帧采样构造悬浮窗内容。
    /// </summary>
    /// <param name="frame">采样帧。为 null 表示这一秒没采到。</param>
    /// <param name="budgetMb">显存预算（MB）。&lt;= 0 时不显示显存占用率，只显示绝对量。</param>
    /// <param name="totalMemoryGb">内存标称容量（GB）。</param>
    /// <param name="diskBytesPerSec">磁盘吞吐；&lt; 0 表示该项不可用，不显示。</param>
    public static OverlayContent From(
        SampleFrame? frame,
        double budgetMb,
        int totalMemoryGb,
        double diskBytesPerSec = -1)
    {
        if (frame is null)
            return Unavailable("采集异常");

        var metrics = new List<OverlayMetric>(4);

        // 显存：有预算时显示百分比，没有就只给绝对量，不编造一个分母
        var vramPercent = budgetMb > 0 ? frame.CommittedMb / budgetMb * 100 : 0;
        metrics.Add(new OverlayMetric(
            "显存",
            budgetMb > 0
                ? $"{OccupancyPalette.Clamp(vramPercent):F0}%"
                : $"{frame.CommittedMb:F0}MB",
            vramPercent));

        // 内存：用「已用 / 标称」口径，与主界面一致（15.73GB 显示成 16GB）
        var usedGb = totalMemoryGb > 0
            ? Math.Max(0, totalMemoryGb - frame.AvailableRamMb / 1024.0)
            : 0;
        var ramPercent = totalMemoryGb > 0 ? usedGb / totalMemoryGb * 100 : 0;
        metrics.Add(new OverlayMetric(
            "内存",
            totalMemoryGb > 0 ? $"{usedGb:F1}GB" : $"{frame.AvailableRamMb / 1024.0:F1}GB 可用",
            ramPercent));

        metrics.Add(new OverlayMetric("GPU", $"{frame.Gpu3DPercent:F0}%", frame.Gpu3DPercent));

        // CPU 用 P 核峰值：E 核被后台占满不代表游戏有事（实测能差 70 个百分点）
        metrics.Add(new OverlayMetric(
            "CPU",
            $"{frame.PerformanceCorePercent:F0}%",
            frame.PerformanceCorePercent));

        // 磁盘吞吐不是百分比，只在可用时追加；把它硬塞进 Percent 会让颜色失真
        if (diskBytesPerSec >= 0)
        {
            metrics.Add(new OverlayMetric(
                "磁盘",
                ThroughputText.Format(diskBytesPerSec),
                // 颜色按 100 MB/s 归一化：跑满 NVMe 才是红，日常小 IO 保持绿色
                diskBytesPerSec / (100.0 * 1024 * 1024) * 100));
        }

        return new OverlayContent(metrics, "", hasWarning: false);
    }

    /// <summary>没有可用数据时的内容：空白 + 红色提示，界面不能什么都不写。</summary>
    public static OverlayContent Unavailable(string hint)
        => new([], string.IsNullOrWhiteSpace(hint) ? "采集异常" : hint, hasWarning: true);

    /// <summary>把指标压成一行，供日志或托盘提示使用。</summary>
    public string ToSingleLine() => Metrics.Count == 0
        ? Hint
        : string.Join("  ", Metrics.Select(m => $"{m.Label} {m.Value}"));
}
