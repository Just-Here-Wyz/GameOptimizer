namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// 「必要进程自动识别」的回归用例。
///
/// 需求来源：用户要求开启游戏模式时只结束非必要应用，
/// 必要的自动保护且不出现在白名单里。
///
/// 为什么不能只靠硬编码名单：实测本机有 5 个进程以服务身份运行
/// （wallpaperservice64、telemetry_agent、MateBookService 等），
/// 名字看起来像普通软件，实际是系统服务，杀了会牵动服务栈。
/// 名单猜不全，必须按客观特征判定。
/// </summary>
public class EssentialProcessGuardTests
{
    private static EssentialProcessGuard.ProcessIdentity Identity(
        string name, int session = 1, string? path = @"C:\Program Files\Test\app.exe", bool isService = false)
        => new(name, session, path, isService);

    [Fact]
    public void 服务会话中的进程必须保护()
    {
        // Session 0 是服务会话，桌面应用永远不在里面
        EssentialProcessGuard.IsEssential(Identity("anything", session: 0))
            .Should().BeTrue("Session 0 的进程属于服务栈");
    }

    [Fact]
    public void 已注册服务的进程必须保护()
    {
        EssentialProcessGuard.IsEssential(Identity("PcManagerService", isService: true))
            .Should().BeTrue();
    }

    [Fact]
    public void 命中服务名集合的进程必须保护()
    {
        var services = new HashSet<string>(["telemetry_agent"], StringComparer.OrdinalIgnoreCase);
        EssentialProcessGuard.IsEssential(Identity("telemetry_agent"), services)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"C:\Windows\System32\drivers\etc\foo.exe")]
    public void 系统目录下的进程必须保护(string path)
    {
        EssentialProcessGuard.IsEssential(Identity("someprocess", path: path))
            .Should().BeTrue($"{path} 属于 Windows 自带组件");
    }

    [Fact]
    public void 系统目录前缀相似但不是系统目录的路径不应被误判()
    {
        // C:\WindowsApps 不是 C:\Windows 的子目录，只做纯字符串前缀会误伤
        EssentialProcessGuard.IsUnderSystemRoot(@"C:\WindowsApps\foo.exe", @"C:\Windows")
            .Should().BeFalse();
    }

    [Fact]
    public void 普通应用目录下的进程不应被保护()
    {
        EssentialProcessGuard.IsEssential(
            Identity("wallpaper64", path: @"D:\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe"))
            .Should().BeFalse();
    }

    [Fact]
    public void 路径读不到时不应因此误判为必要()
    {
        // 反作弊游戏读不到路径是常态；只要不在 Session 0、不在服务集合里，
        // 就应该允许结束，否则白名单会大面积失效
        EssentialProcessGuard.IsEssential(Identity("SomeGame", path: null))
            .Should().BeFalse();
    }

    [Fact]
    public void 空进程名应保守地判为必要()
    {
        // 名字都拿不到就不要动它
        EssentialProcessGuard.IsEssential(Identity(""))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe -k netsvcs -p", "svchost")]
    [InlineData("\"C:\\Program Files\\App\\service.exe\" --run", "service")]
    [InlineData(@"C:\Program Files\App\app.exe", "app")]
    [InlineData(@"\SystemRoot\System32\foo.exe", "foo")]
    public void 应从服务的ImagePath里正确抠出exe名(string imagePath, string expected)
    {
        EssentialProcessGuard.ExtractExecutableName(imagePath).Should().Be(expected);
    }

    [Fact]
    public void 无扩展名的ImagePath应返回原名()
    {
        EssentialProcessGuard.ExtractExecutableName(@"C:\Tools\svc").Should().Be("svc");
    }

    [Fact]
    public void 空ImagePath应返回null而不是崩()
    {
        EssentialProcessGuard.ExtractExecutableName("").Should().BeNull();
        EssentialProcessGuard.ExtractExecutableName("   ").Should().BeNull();
    }

    [Fact]
    public void 真实环境应能读到服务列表()
    {
        // 读不到服务列表时白名单会大面积失效，这条守住基本可用性
        EssentialProcessGuard.LoadServiceImageNames().Should().NotBeEmpty();
    }

    [Fact]
    public void 真实环境下系统进程应被判为必要()
    {
        // 用本机真实进程验证判定链路，而不只是纯函数
        var explorer = System.Diagnostics.Process.GetProcessesByName("explorer").FirstOrDefault();
        if (explorer is null) return;

        try
        {
            var identity = EssentialProcessGuard.Identify(explorer);
            EssentialProcessGuard.IsEssential(identity).Should().BeTrue("资源管理器永远不该被结束");
        }
        finally { explorer.Dispose(); }
    }
}
