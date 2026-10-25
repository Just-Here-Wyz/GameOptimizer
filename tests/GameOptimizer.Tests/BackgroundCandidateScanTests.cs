namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// 后台程序候选扫描的判据。
///
/// 这里每一条「不该列」都对应一次真实误伤：
/// 列出 Session 0 的服务 → 结束会牵动服务栈；
/// 列出游戏本体 → 用户勾上后「开启游戏模式」会顺手关掉要玩的游戏；
/// 列出读不到路径的进程 → 结束后拉不回来，等于制造无法回滚的改动。
/// 白名单"显示不全"的修复不能以引入这些风险为代价。
/// </summary>
public class BackgroundCandidateScanTests
{
    private static readonly string SystemRoot = @"C:\Windows";
    private static readonly ScanEnvironment Env = new(
        ServiceNames: new HashSet<string>(["SomeVendorService"], StringComparer.OrdinalIgnoreCase),
        GameProcessStems: new HashSet<string>(["DeltaForceClient", "NarakaBladepoint"], StringComparer.OrdinalIgnoreCase),
        CurrentSessionId: 1,
        SystemRoot: SystemRoot);

    private static ProcessFacts Facts(
        string name,
        bool hasWindow = true,
        string? path = @"D:\Apps\Demo\demo.exe",
        int session = 1,
        bool isService = false,
        bool inPreset = false)
        => new(name, session, path, isService, hasWindow, inPreset);

    [Fact]
    public void 有窗口的用户程序应列入()
    {
        BackgroundCandidateScan.ShouldOffer(Facts("cloudmusic"), Env).Should().BeTrue();
    }

    [Fact]
    public void 预设名单内没有窗口的后台也应列入()
    {
        // 壁纸服务、同步盘这类没有主窗口，但确实常驻抢资源
        BackgroundCandidateScan.ShouldOffer(Facts("SyncService", hasWindow: false, inPreset: true), Env)
            .Should().BeTrue();
    }

    [Fact]
    public void 预设外且没有窗口的不列入()
    {
        // 没有窗口又不是已知大户：无法判断它是什么，不该给用户一个「随手关掉」的选项
        BackgroundCandidateScan.ShouldOffer(Facts("whateverbg", hasWindow: false, inPreset: false), Env)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("explorer")]        // 系统外壳
    [InlineData("MsMpEng")]         // 安全软件
    [InlineData("ctfmon")]          // 输入法
    [InlineData("GameOptimizer.App")] // 本程序自己
    public void 保护名单内的进程不得列入(string name)
    {
        BackgroundCandidateScan.ShouldOffer(Facts(name), Env).Should().BeFalse();
    }

    [Fact]
    public void 会话0的服务进程不得列入()
    {
        BackgroundCandidateScan.ShouldOffer(Facts("wallpaperservice64", session: 0, inPreset: true), Env)
            .Should().BeFalse();
    }

    [Fact]
    public void 其他登录会话的进程不得列入()
    {
        BackgroundCandidateScan.ShouldOffer(Facts("otherUserApp", session: 3), Env).Should().BeFalse();
    }

    [Fact]
    public void 已注册服务不得列入()
    {
        BackgroundCandidateScan.ShouldOffer(Facts("SomeVendorService"), Env).Should().BeFalse();
        BackgroundCandidateScan.ShouldOffer(Facts("AnotherService", isService: true), Env).Should().BeFalse();
    }

    [Fact]
    public void Windows目录下的组件不得列入()
    {
        BackgroundCandidateScan.ShouldOffer(Facts("SomeTool", path: @"C:\Windows\System32\tool.exe"), Env)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("DeltaForceClient-Win64-Shipping")] // UE 游戏实际进程名带后缀
    [InlineData("NarakaBladepoint")]
    public void 游戏进程不得列入(string name)
    {
        BackgroundCandidateScan.ShouldOffer(Facts(name), Env).Should().BeFalse();
    }

    [Fact]
    public void 太短的进程名不应被游戏前缀误伤()
    {
        // 主名短于 4 个字符时不参与前缀匹配，否则 qq / we 这类会大面积误判
        var env = Env with { GameProcessStems = new HashSet<string>(["qq"], StringComparer.OrdinalIgnoreCase) };
        BackgroundCandidateScan.ShouldOffer(Facts("qqmusic"), env).Should().BeTrue();
    }

    [Fact]
    public void 读不到可执行路径的一律不列入()
    {
        // 没有路径 = 结束后无法按原路径恢复，列出来就是给用户挖坑
        BackgroundCandidateScan.ShouldOffer(Facts("mystery", path: null, inPreset: true), Env)
            .Should().BeFalse();
    }
}
