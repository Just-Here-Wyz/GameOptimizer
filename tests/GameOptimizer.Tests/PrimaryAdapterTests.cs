namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;

/// <summary>
/// 主显卡选择逻辑的回归用例。
/// 实测踩到的 bug：程序显示"显存 12MB"而真实值是 1553MB——
/// 因为它把 Total Committed 读自第一块适配器、Shared Usage 读自另一块，
/// 12MB 是虚拟显示器适配器的占用。本机存在多块适配器，极易复现。
/// </summary>
public class PrimaryAdapterTests
{
    [Fact]
    public void 应选出占用最大的适配器作为主显卡()
    {
        // 本机实测数据：c71f 是 Iris Xe（1553MB），cb02 是虚拟适配器（12MB）
        var samples = new (string Name, double Committed)[]
        {
            ("luid_0x00000000_0x0000c71f_phys_0", 1553.3),
            ("luid_0x00000000_0x0000cb02_phys_0", 12.1),
        };

        var primary = PdhSampler.SelectPrimaryAdapter(samples);

        primary.Should().Be("luid_0x00000000_0x0000c71f_phys_0");
    }

    [Fact]
    public void 虚拟适配器不应被误选为主显卡()
    {
        var samples = new (string Name, double Committed)[]
        {
            ("Todesk Virtual Display", 0),
            ("OrayIddDriver Device", 0),
            ("luid_0x00000000_0x0000c71f_phys_0", 1553.3),
        };

        PdhSampler.SelectPrimaryAdapter(samples).Should().Be("luid_0x00000000_0x0000c71f_phys_0");
    }

    [Fact]
    public void 顺序与占用不一致时应按数值而非顺序选()
    {
        // 这里是原 bug 的核心：FirstOrDefault 会返回第一个（小值），必须按最大值
        var samples = new (string Name, double Committed)[]
        {
            ("small", 10),
            ("big", 9000),
            ("medium", 500),
        };

        PdhSampler.SelectPrimaryAdapter(samples).Should().Be("big");
    }

    [Fact]
    public void 全部为0时应退化为第一个而不是null()
    {
        var samples = new (string Name, double Committed)[]
        {
            ("first", 0),
            ("second", 0),
        };

        PdhSampler.SelectPrimaryAdapter(samples).Should().Be("first");
    }

    [Fact]
    public void 空列表应返回null()
    {
        PdhSampler.SelectPrimaryAdapter([]).Should().BeNull();
    }

    [Fact]
    public void 真实环境下主适配器与共享显存应来自同一块显卡()
    {
        using var sampler = new PdhSampler();
        if (!sampler.Initialize()) return;
        sampler.Warmup(200);

        var frame = sampler.Sample(0, 0);

        // 同一块显卡上，Commited 必然 >= Shared（共享是其中一部分）
        frame.CommittedMb.Should().BeGreaterThanOrEqualTo(frame.SharedUsedMb);

        // 若两者来自不同适配器，常见结果是 Committed 很小而 Shared 正常，或反之
        if (frame.SharedUsedMb > 0)
            frame.CommittedMb.Should().BeGreaterThan(frame.SharedUsedMb * 0.5);
    }

    [Fact]
    public void 主适配器应携带有效的LUID()
    {
        using var sampler = new PdhSampler();
        if (!sampler.Initialize()) return;

        sampler.PrimaryLuid.Should().NotBeNull();
        sampler.PrimaryLuid.Should().Contain("luid_");
    }
}
