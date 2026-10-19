namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;

/// <summary>
/// 游戏进程检测。用于"点一下就开始采集帧数据"。
///
/// 不能用进程名匹配游戏名：WeGame 的《三角洲行动》主程序叫 DeltaForceClient.exe，
/// 《无畏契约》叫 VALORANT.exe，与显示名毫无相似性。
/// 所以按可执行文件路径是否落在已扫描的游戏目录来判定。
/// </summary>
public class GameProcessDetectorTests
{
    [Fact]
    public void 没有游戏时不应报错()
    {
        var running = GameProcessDetector.Detect([]);
        running.Should().BeEmpty();
    }

    [Fact]
    public void 可执行文件路径为空时应跳过而不是崩溃()
    {
        var games = new (string Name, string? Exe)[]
        {
            ("没有路径的游戏", null),
            ("空路径", ""),
        };

        var act = () => GameProcessDetector.Detect(games);
        act.Should().NotThrow().Which.Should().BeEmpty();
    }

    [Fact]
    public void 应能检测到当前正在运行的测试宿主进程()
    {
        // 用测试进程自己当"游戏"：把它的 exe 当作已知游戏路径
        var self = Environment.ProcessPath;
        self.Should().NotBeNull();

        var games = new (string Name, string? Exe)[] { ("测试宿主", self) };
        var running = GameProcessDetector.Detect(games);

        running.Should().ContainSingle();
        running[0].DisplayName.Should().Be("测试宿主");
        running[0].ProcessId.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public void 检测结果的进程名不应带exe后缀()
    {
        var self = Environment.ProcessPath!;
        var running = GameProcessDetector.Detect([("测试宿主", self)]);

        running[0].ProcessName.Should().NotEndWith(".exe");
    }

    [Fact]
    public void 路径匹配应忽略大小写()
    {
        // Windows 路径大小写不敏感，若用区分大小写的比较会漏掉
        var self = Environment.ProcessPath!;
        var upper = self.ToUpperInvariant();

        var running = GameProcessDetector.Detect([("大写路径", upper)]);
        running.Should().ContainSingle();
    }

    [Fact]
    public void IsRunning_对不存在的进程应返回false()
    {
        GameProcessDetector.IsRunning("绝对不存在的进程名_xyz_123").Should().BeFalse();
    }

    [Fact]
    public void IsRunning_对当前进程应返回true()
    {
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        GameProcessDetector.IsRunning(name).Should().BeTrue();
    }
}
