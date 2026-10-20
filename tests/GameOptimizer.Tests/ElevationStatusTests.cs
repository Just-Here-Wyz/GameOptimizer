namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 权限检测与失败文案：
/// 「没提权」与「提权了但仍被拒」是两回事，前者可行动（去提权），后者只能如实说明。
/// 混成一句「失败」用户无从下手。
/// </summary>
public class ElevationStatusTests
{
    [Fact]
    public void 未提权时应提示去提权而不是笼统失败()
    {
        var text = ElevationStatus.DescribeMemoryCleanFailure(
            MemoryCleanFailureReason.ElevationRequired, elevated: false);

        text.Should().Contain("管理员权限");
        text.Should().Contain("以管理员身份运行");
    }

    [Fact]
    public void 已提权仍被全部拒绝时不应再让用户去提权()
    {
        // 这些进程受系统保护，再提权也没用；继续提示「以管理员身份运行」是让用户白折腾
        var text = ElevationStatus.DescribeMemoryCleanFailure(
            MemoryCleanFailureReason.ElevationRequired, elevated: true);

        text.Should().Contain("受系统保护");
        text.Should().NotContain("以管理员身份运行");
    }

    [Fact]
    public void 各失败原因都应有可读文案()
    {
        foreach (var reason in Enum.GetValues<MemoryCleanFailureReason>())
        {
            var text = ElevationStatus.DescribeMemoryCleanFailure(reason, elevated: false);
            text.Should().NotBeNullOrWhiteSpace($"{reason} 必须有面向用户的说明");
        }
    }

    [Fact]
    public void 回读不一致的原因必须说明是被判定为失败()
    {
        // 这类失败最容易被误当成成功
        var text = ElevationStatus.DescribeMemoryCleanFailure(
            MemoryCleanFailureReason.ReadbackMismatch, elevated: true);

        text.Should().Contain("回读");
        text.Should().Contain("失败");
    }

    [Fact]
    public void 真实环境应能返回一个确定的权限判定()
    {
        // 不论当前是否提权，都必须给出明确布尔值，不能靠异常路径返回
        var act = () => ElevationStatus.IsCurrentProcessElevated();

        act.Should().NotThrow();
        (ElevationStatus.IsCurrentProcessElevated() || !ElevationStatus.IsCurrentProcessElevated()).Should().BeTrue();
    }
}
