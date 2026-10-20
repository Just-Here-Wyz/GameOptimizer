namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Models;

/// <summary>
/// 游戏内悬浮窗的数据模型。
///
/// 设计约束（来自用户明确要求「保证账号安全」）：
/// · 只取系统级性能计数器，不接触游戏进程任何信息
/// · 不含帧率（帧率必须用 PresentMon 定向监控游戏进程，是反作弊敏感点）
/// </summary>
public class OverlayContentTests
{
    private static SampleFrame Frame(
        double committedMb = 2000,
        double availableRamMb = 8192,
        double gpu3D = 40,
        double pCore = 30)
        => new(0, 0, 0, committedMb, availableRamMb, gpu3D, pCore)
        {
            PerformanceCorePercent = pCore,
        };

    [Fact]
    public void 应给出显存内存GPUCPU四项()
    {
        var c = OverlayContent.From(Frame(), budgetMb: 8192, totalMemoryGb: 16);

        c.Metrics.Select(m => m.Label).Should().Equal("显存", "内存", "GPU", "CPU");
        c.HasWarning.Should().BeFalse();
    }

    [Fact]
    public void 不含帧率指标()
    {
        // 帧率必须定向监控游戏进程，与「保证账号安全」冲突，明确不做
        var c = OverlayContent.From(Frame(), 8192, 16);

        c.Metrics.Should().NotContain(m => m.Label.Contains("帧") || m.Label.Contains("FPS"));
    }

    [Fact]
    public void 显存应按预算算百分比()
    {
        var c = OverlayContent.From(Frame(committedMb: 4096), budgetMb: 8192, totalMemoryGb: 16);

        c.Metrics.Single(m => m.Label == "显存").Value.Should().Be("50%");
    }

    [Fact]
    public void 显存预算未知时只显示绝对量而不是编一个百分比()
    {
        // 预算读不到时给个 0% 会让人以为显存空闲
        var c = OverlayContent.From(Frame(committedMb: 2048), budgetMb: 0, totalMemoryGb: 16);

        var vram = c.Metrics.Single(m => m.Label == "显存");
        vram.Value.Should().Be("2048MB");
        vram.Value.Should().NotContain("%");
    }

    [Fact]
    public void 内存应按已用与标称计算()
    {
        // 16GB 标称，可用 8GB → 已用 8GB → 50%
        var c = OverlayContent.From(Frame(availableRamMb: 8192), 8192, 16);

        c.Metrics.Single(m => m.Label == "内存").Value.Should().Be("8.0GB");
    }

    [Fact]
    public void 内存容量未知时显示可用量并注明()
    {
        var c = OverlayContent.From(Frame(availableRamMb: 8192), 8192, totalMemoryGb: 0);

        var mem = c.Metrics.Single(m => m.Label == "内存");
        mem.Value.Should().Contain("可用");
        mem.Percent.Should().Be(0);
    }

    [Fact]
    public void CPU应取性能核峰值而不是全局峰值()
    {
        // E 核被后台挤满不代表游戏有事；悬浮窗上必须显示和主界面同一口径
        var frame = new SampleFrame(0, 0, 0, 2000, 8192, 40, 95)
        {
            PerformanceCorePercent = 25,
            EfficiencyCorePercent = 95,
        };

        var c = OverlayContent.From(frame, 8192, 16);

        var cpu = c.Metrics.Single(m => m.Label == "CPU");
        cpu.Value.Should().Be("25%");
        cpu.Value.Should().NotBe("95%", "全局峰值会把小核的负载算进来");
    }

    [Fact]
    public void 磁盘吞吐可用时应追加为第五项()
    {
        var c = OverlayContent.From(Frame(), 8192, 16, diskBytesPerSec: 12.5 * 1024 * 1024);

        c.Metrics.Should().HaveCount(5);
        c.Metrics.Last().Label.Should().Be("磁盘");
        // 12.5 按 F0（银行家舍入）是 12，不是 13
        c.Metrics.Last().Value.Should().Be("12 MB/s");
    }

    [Fact]
    public void 磁盘吞吐不可用时不应出现该项()
    {
        // 传入负数表示该项不可用（类别名写错那次就是这种情况）
        var c = OverlayContent.From(Frame(), 8192, 16, diskBytesPerSec: -1);

        c.Metrics.Should().HaveCount(4);
        c.Metrics.Should().NotContain(m => m.Label == "磁盘");
    }

    [Fact]
    public void 磁盘吞吐的颜色按100MB每秒归一()
    {
        // 日常小 IO 不该显示成红色
        var low = OverlayContent.From(Frame(), 8192, 16, diskBytesPerSec: 5.0 * 1024 * 1024);
        var high = OverlayContent.From(Frame(), 8192, 16, diskBytesPerSec: 200.0 * 1024 * 1024);

        low.Metrics.Last().Percent.Should().BeLessThan(30);
        high.Metrics.Last().Percent.Should().BeGreaterThan(80);
    }

    [Fact]
    public void 采样帧为空时应给出异常提示而不是空白()
    {
        // 悬浮窗突然变空白会让人以为程序挂了
        var c = OverlayContent.From(null, 8192, 16);

        c.Metrics.Should().BeEmpty();
        c.HasWarning.Should().BeTrue();
        c.Hint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 正常采集时不应有警告提示()
    {
        var c = OverlayContent.From(Frame(), 8192, 16);

        c.HasWarning.Should().BeFalse();
        c.Hint.Should().BeEmpty();
    }

    [Fact]
    public void 颜色应与仪表盘同一套四档口径()
    {
        var low = OverlayContent.From(Frame(gpu3D: 10), 8192, 16);
        var critical = OverlayContent.From(Frame(gpu3D: 95), 8192, 16);

        low.Metrics.Single(m => m.Label == "GPU").ColorHex
            .Should().Be(OccupancyPalette.ColorHex(OccupancyLevel.Low));
        critical.Metrics.Single(m => m.Label == "GPU").ColorHex
            .Should().Be(OccupancyPalette.ColorHex(OccupancyLevel.Critical));
    }

    [Fact]
    public void 超范围读数应被钳制()
    {
        // PDH 曾给出 123%
        var frame = new SampleFrame(0, 0, 0, 9000, 1024, 123, 130)
        {
            PerformanceCorePercent = 130,
        };

        var c = OverlayContent.From(frame, budgetMb: 8192, totalMemoryGb: 16);

        c.Metrics.Single(m => m.Label == "GPU").Percent.Should().BeLessThanOrEqualTo(100);
        c.Metrics.Single(m => m.Label == "CPU").Percent.Should().BeLessThanOrEqualTo(100);
        c.Metrics.Single(m => m.Label == "显存").Percent.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void 单行摘要应包含全部指标()
    {
        var c = OverlayContent.From(Frame(), 8192, 16);

        var line = c.ToSingleLine();
        line.Should().Contain("显存").And.Contain("内存").And.Contain("GPU").And.Contain("CPU");
    }
}
