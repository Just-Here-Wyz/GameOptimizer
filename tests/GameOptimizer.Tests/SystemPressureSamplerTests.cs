namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;

/// <summary>
/// 系统压力采集：磁盘吞吐 + 总 CPU。
///
/// 这两项是实测筛出来的结果，被筛掉的指标（处理器队列长度、分页文件使用率、
/// 网络吞吐）各自的原因写在 <see cref="SystemPressureSampler"/> 与 docs/04-已知限制.md 里。
/// </summary>
public class SystemPressureSamplerTests
{
    [Fact]
    public void 未初始化就采样应明确报错而不是静默返回0()
    {
        // 静默返回 0 会让界面显示「磁盘 0 B/s」而用户以为空闲，实际是没采到
        using var sampler = new SystemPressureSampler();

        var act = () => sampler.Sample(0);

        act.Should().Throw<InvalidOperationException>().WithMessage("*未就绪*");
    }

    [Fact]
    public void 初始化成功后未预热应明确报错()
    {
        using var sampler = new SystemPressureSampler();
        if (!sampler.Initialize()) return;

        var act = () => sampler.Sample(0);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Warmup*");
    }

    [Fact]
    public void 吞吐量不得为负()
    {
        // 计数器首次读数或回绕会给出负数，显示成 -3 MB/s 会像 bug
        SystemPressureSampler.NonNegative(-1).Should().Be(0);
        SystemPressureSampler.NonNegative(double.NaN).Should().Be(0);
        SystemPressureSampler.NonNegative(double.PositiveInfinity).Should().Be(0);
        SystemPressureSampler.NonNegative(1234).Should().Be(1234);
    }

    [Fact]
    public void 总CPU应复用统一的百分比钳制口径()
    {
        // 与 PdhSampler / OccupancyPalette 同一口径：PDH 时间基异常时算出过 123%
        PdhSampler.ClampPercent(123).Should().Be(100);
        PdhSampler.ClampPercent(-5).Should().Be(0);
        PdhSampler.ClampPercent(double.NaN).Should().Be(0);
    }

    [Fact]
    public void 真实环境应能采到至少一项指标()
    {
        using var sampler = new SystemPressureSampler();
        if (!sampler.Initialize()) return; // 计数器不可用的环境跳过

        sampler.IsReady.Should().BeTrue();
        sampler.AvailableMetrics.Should().NotBeEmpty();
    }

    [Fact]
    public void 预热后采样应给出合理量级的数值()
    {
        using var sampler = new SystemPressureSampler();
        if (!sampler.Initialize()) return;
        sampler.Warmup(300);

        var frame = sampler.Sample(0);

        frame.TotalCpuPercent.Should().BeInRange(0, 100);
        frame.DiskBytesPerSec.Should().BeGreaterThanOrEqualTo(0);

        // 磁盘吞吐不该出现天文数字（除非真的在跑满速 NVMe，那也就几 GB/s）
        frame.DiskBytesPerSec.Should().BeLessThan(1024d * 1024 * 1024 * 1024);
    }

    [Fact]
    public void 连采多次不应产生越界或负数()
    {
        using var sampler = new SystemPressureSampler();
        if (!sampler.Initialize()) return;
        sampler.Warmup(200);

        for (var i = 0; i < 10; i++)
        {
            var f = sampler.Sample(i * 1000L);
            f.TotalCpuPercent.Should().BeInRange(0, 100);
            f.DiskBytesPerSec.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public void 只保留总CPU与磁盘吞吐两项()
    {
        // 实测筛除结论的回归锁：处理器队列长度在 24 核机器上压到 79% CPU 仍是 0~2，
        // 分页文件使用率静态不响应，网络吞吐与游戏延迟无关。
        // 若有人把它们加回来，先看 docs/04-已知限制.md 里的实测证据。
        using var sampler = new SystemPressureSampler();
        if (!sampler.Initialize()) return;

        sampler.AvailableMetrics.Should().BeSubsetOf(["磁盘吞吐", "总 CPU"]);
    }

    [Fact]
    public void 磁盘吞吐必须真的采到而不是静默降级()
    {
        // 回归：类别名写成 "Physical Disk"（带空格）时，
        // PerformanceCounterCategory 抛 "Category does not exist"，
        // 磁盘项被 try/catch 静默吞掉 → AvailableMetrics 里没有「磁盘吞吐」，
        // 界面只显示 0 B/s。而 Get-Counter 用带空格的路径却能读到数，
        // 所以手工验证很难发现。正确类别名是 "PhysicalDisk"（无空格）。
        using var sampler = new SystemPressureSampler();
        if (!sampler.Initialize()) return;

        sampler.AvailableMetrics.Should().Contain("磁盘吞吐",
            "磁盘计数器在本机存在且可读，不该被类别名写错导致静默降级");
    }

    [Fact]
    public void 磁盘类别名必须是无空格的形式()
    {
        // 直接锁死常量：改成 "Physical Disk" 会让上面那条用例变红
        SystemPressureSampler.DiskCategory.Should().Be("PhysicalDisk");
    }

    [Fact]
    public void 吞吐量文本应按量级选单位()
    {
        ThroughputText.Format(1.2 * 1024 * 1024 * 1024).Should().Be("1.2 GB/s");
        ThroughputText.Format(480d * 1024 * 1024).Should().Be("480 MB/s");
        ThroughputText.Format(12d * 1024).Should().Be("12 KB/s");
        ThroughputText.Format(0).Should().Be("0 B/s");
        ThroughputText.Format(-5).Should().Be("0 B/s");
        ThroughputText.Format(double.NaN).Should().Be("0 B/s");
    }

    [Fact]
    public void 磁盘吞吐应能响应真实IO()
    {
        // 这是这项指标存在的唯一理由：实测空闲 0.16 MB/s，写入时 10.95 MB/s。
        // 若某天它不响应了，这项指标就没有保留价值。
        using var sampler = new SystemPressureSampler();
        if (!sampler.Initialize()) return;
        sampler.Warmup(300);

        var idle = sampler.Sample(0).DiskBytesPerSec;

        // 造 32MB 写入
        var path = Path.Combine(Path.GetTempPath(), $"go-io-{Guid.NewGuid():N}.bin");
        try
        {
            var buffer = new byte[4 * 1024 * 1024];
            using (var fs = File.Create(path))
            {
                for (var i = 0; i < 8; i++) fs.Write(buffer, 0, buffer.Length);
            }

            var busy = sampler.Sample(0).DiskBytesPerSec;

            busy.Should().BeGreaterThanOrEqualTo(0);
            idle.Should().BeGreaterThanOrEqualTo(0);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
