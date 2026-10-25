namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;

/// <summary>
/// 快速优化的前后对比文案。
///
/// 关键不是措辞，而是**不许糊弄**：采样不够就说「无法对比」，
/// 数字有变化也只说「变化了几个百分点」，绝不暗示帧率。
/// </summary>
public class CpuOptimizeVerificationTests
{
    private static CpuWindowStats Stats(double? average, int samples = 5)
        => new(average, samples, 0, 5, 0);

    [Fact]
    public void 执行前数据不足时应说无法对比()
    {
        var text = CpuOptimizeVerification.DescribeComparison(Stats(60, samples: 2), Stats(30));

        text.Should().Contain("无法对比").And.Contain("执行前");
        text.Should().NotContain("下降");
    }

    [Fact]
    public void 执行后数据不足时同样说无法对比()
    {
        var text = CpuOptimizeVerification.DescribeComparison(Stats(60), Stats(null, samples: 0));

        text.Should().Contain("无法对比").And.Contain("执行后");
        text.Should().Contain("不作数");
    }

    [Fact]
    public void 占用下降应写清前后数字与窗口()
    {
        var text = CpuOptimizeVerification.DescribeComparison(Stats(60), Stats(35));

        text.Should().Contain("60%").And.Contain("35%");
        text.Should().Contain("下降");
        text.Should().Contain("25 个百分点");
        text.Should().Contain("5 帧平均");
    }

    [Fact]
    public void 占用上升也要如实说()
    {
        var text = CpuOptimizeVerification.DescribeComparison(Stats(30), Stats(48));

        text.Should().Contain("上升").And.Contain("18 个百分点");
    }

    [Fact]
    public void 变化很小时应说基本持平()
    {
        var text = CpuOptimizeVerification.DescribeComparison(Stats(40), Stats(40.2));

        text.Should().Contain("基本持平");
    }

    [Fact]
    public void 口径说明必须写明采样源与窗口且提到帧率()
    {
        CpuOptimizeVerification.CaliberNote.Should().Contain("_Total");
        CpuOptimizeVerification.CaliberNote.Should().Contain("5 秒");
        CpuOptimizeVerification.CaliberNote.Should().Contain("不代表游戏帧率提升");
    }

    [Fact]
    public void 对比窗口与最小样本数是同一套常量()
    {
        // 界面文案里写死的「5 秒」必须与这里一致，否则用户看到的口径和实际算法不符
        CpuOptimizeVerification.CompareWindowSeconds.Should().Be(5);
        CpuOptimizeVerification.MinimumSamples.Should().Be(3);
    }
}
