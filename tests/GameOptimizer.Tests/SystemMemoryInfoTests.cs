namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;

public class SystemMemoryInfoTests
{
    [Theory]
    [InlineData(16384.0, 16)]   // 16GB 条实际可用
    [InlineData(16108.0, 16)]   // 实测本机 15.73GB → 应显示 16
    [InlineData(32768.0, 32)]
    [InlineData(8120.0, 8)]
    [InlineData(12288.0, 12)]
    [InlineData(65536.0, 64)]
    public void 实际可用量应归一到标称容量(double actualMb, int expectedGb)
    {
        // 用户心智里是"我装了16G"，不该显示 15.7
        SystemMemoryInfo.NominalFrom(actualMb).Should().Be(expectedGb);
    }

    [Fact]
    public void 超出标准规格时应向上取整()
    {
        // 100GB 没有对应标准容量，向上取到 128
        SystemMemoryInfo.NominalFrom(102400).Should().Be(128);
    }

    [Fact]
    public void 极小内存不应崩溃()
    {
        SystemMemoryInfo.NominalFrom(100).Should().Be(4);
    }

    [Fact]
    public void 取不到时应返回0而不是猜一个值()
    {
        SystemMemoryInfo.NominalFrom(0).Should().Be(0);
        SystemMemoryInfo.NominalFrom(-1).Should().Be(0);
    }

    [Fact]
    public void 真实环境应能读到物理内存总量()
    {
        SystemMemoryInfo.TotalPhysicalMb.Should().BeGreaterThan(0);
        SystemMemoryInfo.NominalGb.Should().BeGreaterThanOrEqualTo(4);
    }
}
