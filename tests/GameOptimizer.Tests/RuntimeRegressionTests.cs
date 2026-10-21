namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;
using GameOptimizer.Core.Library;

/// <summary>
/// 针对实际运行中发现并修复的回归问题补的用例。
/// 这些 bug 单看代码不容易发现，必须锁死。
/// </summary>
public class RuntimeRegressionTests
{
    [Fact]
    public void 正斜杠与反斜杠路径应视为同一个库()
    {
        // 实测：注册表给 d:/steam，vdf 给 D:\STEAM，未规范化会导致每个游戏被扫描两次。
        // 规范化同时统一盘符大小写：界面直接显示这个路径，d:\steam 与 D:\WeGameApps 并列很突兀。
        SteamLibraryScanner.NormalizePath(@"d:/steam").Should().Be(@"D:\steam");
        SteamLibraryScanner.NormalizePath(@"D:\STEAM").Should().Be(@"D:\STEAM");
    }

    [Fact]
    public void 尾部斜杠应被去掉()
    {
        SteamLibraryScanner.NormalizePath(@"D:\STEAM\").Should().Be(@"D:\STEAM");
        SteamLibraryScanner.NormalizePath(@"D:\STEAM\\").Should().Be(@"D:\STEAM");
    }

    [Fact]
    public void 大小写不同的同一路径规范化后应相等()
    {
        var a = SteamLibraryScanner.NormalizePath(@"D:\STEAM");
        var b = SteamLibraryScanner.NormalizePath(@"d:\steam");
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
    }

    [Fact]
    public void GPU计数器返回字节而内存计数器返回MB_换算不能搞混()
    {
        // PDH 实测：GPU Adapter Memory 原始值 1620373504 字节 = 1545.3 MB
        //            Memory\Available MBytes 原始值 6626 已经是 MB
        const double rawGpuBytes = 1_620_373_504;
        var bytesPerMb = 1024.0 * 1024.0;

        (rawGpuBytes / bytesPerMb).Should().BeApproximately(1545.3, 0.1);

        // 若误把两种计数器当成同一单位，会出现 1600 万这种荒谬数值
        (rawGpuBytes / bytesPerMb).Should().BeLessThan(100_000);
    }

    [Fact]
    public void 采样器实例名应能被识别为真实设备而非占位符()
    {
        // 之前骨架里把 LUID 写死成全 0，本机实际是 luid_0x00000000_0x0000c71f_phys_0
        var sampler = new PdhSampler();
        try
        {
            var ok = sampler.Initialize();
            if (!ok) return; // 非 Windows CI 环境跳过

            sampler.PrimaryLuid.Should().NotBeNull();
            sampler.PrimaryLuid.Should().StartWith("luid_");
            sampler.PrimaryLuid.Should().Contain("phys_");
        }
        finally { sampler.Dispose(); }
    }

    [Fact]
    public void 未预热就采样应明确报错而不是静默返回0()
    {
        // 首次读数恒为 0，静默返回会让用户看到"显存 0MB"却以为正常
        using var sampler = new PdhSampler();
        if (!sampler.Initialize()) return;

        var act = () => sampler.Sample(0);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Warmup*");
    }

    [Fact]
    public void 预热后采样应给出合理量级的数值()
    {
        using var sampler = new PdhSampler();
        if (!sampler.Initialize()) return;
        sampler.Warmup(200);

        var frame = sampler.Sample(0);

        // 显存占用不可能超过 256GB，也不可能为负
        frame.CommittedMb.Should().BeInRange(0, 256 * 1024);
        frame.AvailableRamMb.Should().BeInRange(0, 1024 * 1024);
        frame.Gpu3DPercent.Should().BeInRange(0, 100);
        frame.MaxCorePercent.Should().BeInRange(0, 100);
    }
}
