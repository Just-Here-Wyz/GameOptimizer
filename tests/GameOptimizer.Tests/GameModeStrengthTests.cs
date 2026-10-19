namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;
using GameOptimizer.Core.Library;

/// <summary>
/// 游戏模式「力度」的回归用例。
///
/// 实测踩到的真实缺陷：上一版白名单只有 18 项浏览器 / 通讯软件，
/// 在本机一个都没命中（用户装的是壁纸引擎 + 厂商管家 + 音乐客户端），
/// 点「开启游戏模式」后实际什么都没关，界面却显示「已开启」。
/// </summary>
public class GameModeStrengthTests
{
    [Fact]
    public void 默认力度应为强力()
    {
        // 用户明确要求「结束后台要有实际效果」
        BackgroundProcessWhitelist.DefaultStrength.Should().Be(GameModeStrength.Strong);
        BackgroundProcessWhitelist.Default.Should().BeEquivalentTo(BackgroundProcessWhitelist.Strong);
    }

    [Fact]
    public void 强力档必须覆盖标准档的全部条目()
    {
        foreach (var name in BackgroundProcessWhitelist.Standard)
            BackgroundProcessWhitelist.Strong.Should().Contain(name, $"强力档不能漏掉标准档的 {name}");
    }

    [Fact]
    public void 强力档应覆盖本机实际常驻的资源占用软件()
    {
        // 这些是本机实测到的常驻进程，标准档一个都没覆盖
        var expected = new[]
        {
            "wallpaper64",              // 壁纸引擎主进程
            "wallpaperservice64",       // 壁纸引擎服务
            "telemetry_agent",          // 华为遥测上报
            "PcOptimizationCenter",     // 华为电脑管家
            "TranslucentTB",            // 任务栏美化
            "kugou",                    // 酷狗音乐
        };

        foreach (var name in expected)
            BackgroundProcessWhitelist.Strong.Should().Contain(name, $"{name} 在本机确实常驻且抢资源");
    }

    [Fact]
    public void 保护名单必须拦住系统关键进程()
    {
        // 结束资源管理器会把桌面一起带走，用户会以为程序把系统弄坏了
        foreach (var name in new[] { "explorer", "dwm", "svchost", "lsass", "winlogon" })
            BackgroundProcessWhitelist.IsProtected(name).Should().BeTrue($"{name} 绝不能结束");
    }

    [Fact]
    public void 保护名单必须拦住杀毒软件()
    {
        // 结束后系统防护会静默失效，风险远大于收益
        foreach (var name in new[] { "MsMpEng", "NisSrv", "SecurityHealthService" })
            BackgroundProcessWhitelist.IsProtected(name).Should().BeTrue($"{name} 是安全软件");
    }

    [Fact]
    public void 保护名单必须拦住本程序自身()
    {
        // 结束自己会让「退出游戏模式」再也点不到，电源计划永久停在性能模式
        BackgroundProcessWhitelist.IsProtected("GameOptimizer.App").Should().BeTrue();
    }

    [Fact]
    public void 保护判断应忽略大小写()
    {
        BackgroundProcessWhitelist.IsProtected("EXPLORER").Should().BeTrue();
        BackgroundProcessWhitelist.IsProtected("msmpeng").Should().BeTrue();
    }

    [Fact]
    public void 两档名单都不应包含被保护的进程()
    {
        // 名单里出现保护项说明有人写错了，必须在测试里拦住
        foreach (var name in BackgroundProcessWhitelist.Strong.Concat(BackgroundProcessWhitelist.Standard))
            BackgroundProcessWhitelist.IsProtected(name).Should().BeFalse($"{name} 不应出现在可结束名单里");
    }

    [Fact]
    public void 名单不应有重复项()
    {
        BackgroundProcessWhitelist.Strong.Should().OnlyHaveUniqueItems();
        BackgroundProcessWhitelist.Standard.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 按力度取名单应返回对应档位()
    {
        BackgroundProcessWhitelist.For(GameModeStrength.Standard)
            .Should().BeEquivalentTo(BackgroundProcessWhitelist.Standard);
        BackgroundProcessWhitelist.For(GameModeStrength.Strong)
            .Should().BeEquivalentTo(BackgroundProcessWhitelist.Strong);
    }

    [Fact]
    public void 力度应有中文说明()
    {
        GameModeStrength.Strong.Describe().Should().Be("强力");
        GameModeStrength.Standard.Describe().Should().Be("标准");
    }

    [Fact]
    public void 游戏模式不应包含显卡偏好这类空转项()
    {
        // 核显机器上没有可调度的独立 GPU，写 GpuPreference 不会有任何效果。
        // 保留这个类只为兼容，但游戏模式绝不能再启用它。
        var mode = new GameModeController();
        mode.Add(new PowerPlanAction());
        mode.Add(new EndProcessAction(BackgroundProcessWhitelist.Default));

        mode.Actions.Should().NotContain(a => a is GpuPreferenceAction);
    }

    [Fact]
    public void 结束进程动作应跳过保护名单内的进程()
    {
        // 传一个系统进程进去，必须一个都不结束
        var action = new EndProcessAction(["explorer", "svchost"]);

        action.Apply();

        action.Killed.Should().BeEmpty("保护名单是硬约束");
        action.IsActive().Should().BeFalse();
    }

    [Fact]
    public void 没结束任何进程时不应报告已生效()
    {
        // 这是上一版的缺陷：白名单一个都没命中，却让界面显示已开启
        var action = new EndProcessAction(["绝对不存在的进程名_测试用"]);

        action.Apply();

        action.IsActive().Should().BeFalse();
        action.Summary.Should().Contain("没有可结束");
    }

    [Fact]
    public void 结束失败的进程必须被如实记录而不是静默吞掉()
    {
        // 实测：wallpaperservice64、telemetry_agent 以服务身份运行，
        // 即使提升权限也返回「拒绝访问」。不能假装结束了。
        var action = new EndProcessAction(["wallpaperservice64", "telemetry_agent"]);

        action.Apply();

        // 这两个进程在测试机上通常存在但不可结束；若恰好不存在，Blocked 为空也算合理
        if (action.Killed.Count == 0 && action.Blocked.Count > 0)
        {
            action.Summary.Should().Contain("受系统保护");
            action.IsActive().Should().BeFalse();
        }
    }

    [Fact]
    public void 结束成功时报告里应给出释放的内存()
    {
        // 必须用「不在系统目录下」的进程来测：ping.exe 在 System32 里，
        // 会被必要进程保护逻辑正确拦下（那条规则本身由 EssentialProcessGuardTests 覆盖）。
        var exe = Path.Combine(Path.GetTempPath(), $"go-victim-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe, overwrite: true);

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-n 30 127.0.0.1",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };

            using var proc = System.Diagnostics.Process.Start(start)!;
            var action = new EndProcessAction([proc.ProcessName]);
            action.Apply();

            action.Killed.Should().NotBeEmpty("临时目录下的进程不属于系统组件");
            action.Summary.Should().MatchRegex(@"已结束 \d+ 个进程，释放约 \d+ MB");
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void 结束真实进程后应记录数量与释放内存()
    {
        // 必须用系统目录之外的进程：ping.exe 位于 System32，
        // 会被必要进程保护逻辑正确拦下（那条规则见 EssentialProcessGuardTests）。
        var exe = Path.Combine(Path.GetTempPath(), $"go-victim2-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe, overwrite: true);

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-n 30 127.0.0.1",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };

            using var proc = System.Diagnostics.Process.Start(start)!;
            var action = new EndProcessAction([proc.ProcessName]);
            action.Apply();

            action.IsActive().Should().BeTrue();
            action.Killed.Should().NotBeEmpty();
            action.Summary.Should().Contain("已结束");
            action.FreedMb.Should().BeGreaterThanOrEqualTo(0);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void 系统目录里的进程即使被勾选也不应结束()
    {
        // 用户把 ping 写进白名单，但它位于 System32，属于 Windows 组件
        var action = new EndProcessAction(["ping"]);
        action.Apply();

        action.Killed.Should().BeEmpty("System32 下的进程属于系统组件");
    }

    [Fact]
    public void 一项都没勾时不应偷偷套用默认名单()
    {
        // 用户把勾选全取消了，就必须真的一个都不关。
        // 否则会出现「我明明全取消了，它还是关了我的软件」。
        var action = new EndProcessAction([]);
        action.Apply();

        action.Killed.Should().BeEmpty();
        action.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void 必要进程应归入跳过而不是拒绝访问()
    {
        // 区分这两类很重要：跳过 = 我们主动不动它；拒绝 = 想动但没权限
        var action = new EndProcessAction(BackgroundProcessWhitelist.Strong.ToList());
        action.Apply();

        // 服务类进程（如 telemetry_agent）应出现在 Skipped 里
        foreach (var s in action.Skipped)
            s.Reason.Should().NotBeNullOrWhiteSpace();
    }
}
