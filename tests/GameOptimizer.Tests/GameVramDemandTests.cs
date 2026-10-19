namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Analysis;
using GameOptimizer.Core.Models;

/// <summary>
/// 游戏显存需求表与推断的用例。
/// 需求表是估值的核心，写错了会让「够用」与「不足」整体偏移。
/// </summary>
public class GameVramDemandTests
{
    [Fact]
    public void 画质越高需求越高()
    {
        var low = GameVramDemand.For("三角洲行动", 90, QualityPreset.Low, ResolutionPreset.P1080);
        var medium = GameVramDemand.For("三角洲行动", 90, QualityPreset.Medium, ResolutionPreset.P1080);
        var high = GameVramDemand.For("三角洲行动", 90, QualityPreset.High, ResolutionPreset.P1080);
        var ultra = GameVramDemand.For("三角洲行动", 90, QualityPreset.Ultra, ResolutionPreset.P1080);

        low.RequiredMb.Should().BeLessThan(medium.RequiredMb);
        medium.RequiredMb.Should().BeLessThan(high.RequiredMb);
        high.RequiredMb.Should().BeLessThan(ultra.RequiredMb);
    }

    [Fact]
    public void 分辨率按像素数放大需求()
    {
        GameVramDemand.ResolutionFactor(ResolutionPreset.P1080).Should().Be(1.00);
        GameVramDemand.ResolutionFactor(ResolutionPreset.P1440).Should().Be(1.40);
        GameVramDemand.ResolutionFactor(ResolutionPreset.P4K).Should().Be(2.20);
    }

    [Fact]
    public void 名称匹配应忽略大小写空格与标点()
    {
        GameVramDemand.LookUp("Delta Force").Should().NotBeNull();
        GameVramDemand.LookUp("DELTAFORCE").Should().NotBeNull();
        GameVramDemand.LookUp("三角洲行动").Should().NotBeNull();
        GameVramDemand.LookUp("三角洲行动：黑鹰坠落").Should().NotBeNull();
    }

    [Theory]
    [InlineData("无畏契约")]
    [InlineData("VALORANT")]
    [InlineData("永劫无间")]
    [InlineData("NARAKA: BLADEPOINT")]
    public void 本机已装的游戏都应在需求表里(string name)
    {
        // 本机实测装的就是这几款；漏收录会让用户看到「按体积推算」的估算值
        GameVramDemand.LookUp(name).Should().NotBeNull($"{name} 应该有实测需求数据");
    }

    [Fact]
    public void 未收录游戏按体积推断并标注依据()
    {
        var small = GameVramDemand.For("未知 A", 10, QualityPreset.High, ResolutionPreset.P1080);
        var big = GameVramDemand.For("未知 B", 120, QualityPreset.High, ResolutionPreset.P1080);

        small.IsEstimated.Should().BeTrue();
        big.IsEstimated.Should().BeTrue();
        small.RequiredMb.Should().BeLessThan(big.RequiredMb);
        small.Basis.Should().Contain("10GB");
    }

    [Fact]
    public void 体积未知时仍应给出需求而不崩溃()
    {
        var result = GameVramDemand.For("完全未知", 0, QualityPreset.High, ResolutionPreset.P1080);

        result.RequiredMb.Should().BeGreaterThan(0);
        result.IsEstimated.Should().BeTrue();
        result.Basis.Should().Contain("同类");
    }

    [Fact]
    public void 推断的浮动幅度应有上限()
    {
        // 体积只允许带来 ±35% 的浮动，否则一个巨大的游戏会被高估成荒谬的数字
        var tiny = GameVramDemand.For("未知", 1, QualityPreset.High, ResolutionPreset.P1080);
        var huge = GameVramDemand.For("未知", 500, QualityPreset.High, ResolutionPreset.P1080);
        var baseMb = GameVramDemand.InferUnknown(0, QualityPreset.High);

        tiny.RequiredMb.Should().BeGreaterThan(baseMb * 0.6);
        huge.RequiredMb.Should().BeLessThan(baseMb * 1.4);
    }

    [Fact]
    public void 预算应标明是实测还是估算()
    {
        var measured = new VramBudget(8192, false, true, "RTX 4060");
        measured.Describe().Should().Contain("实测");

        var estimated = VramBudget.Fallback(16384, isIntegrated: true);
        estimated.IsMeasured.Should().BeFalse();
        estimated.Describe().Should().NotContain("实测");
    }

    [Fact]
    public void 独显预算应取专用显存而不是内存的一半()
    {
        // 这是 bug 的核心：8GB 显卡 + 32GB 内存，旧逻辑会得出 16GB 预算
        var discrete = new GameOptimizer.Core.Collection.GpuMemoryInfo.Snapshot(
            "测试独显", DedicatedVideoMb: 8192, SharedSystemMb: 16000);

        discrete.IsDiscrete.Should().BeTrue();
        VramBudget.From(discrete).BudgetMb.Should().Be(8192);
    }

    [Fact]
    public void 核显预算应算上共享显存()
    {
        // 实测本机 Iris Xe：专用 128MB + 共享 8054MB
        var integrated = new GameOptimizer.Core.Collection.GpuMemoryInfo.Snapshot(
            "Intel Iris Xe", DedicatedVideoMb: 128, SharedSystemMb: 8054);

        integrated.IsDiscrete.Should().BeFalse();
        VramBudget.From(integrated).BudgetMb.Should().BeApproximately(8182, 1);
    }

    [Fact]
    public void 判定紧张的门槛应是百分之八十五()
    {
        // 留 15% 给系统占用；写死这个值方便以后调整时看清单点
        GameVramDemand.TightRatio.Should().Be(0.85);
    }
}
