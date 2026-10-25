namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;
using GameOptimizer.Core.Library;

/// <summary>
/// 「选中即保护」的语义用例（v2.4）。
///
/// 这一组锁的是本次改造最核心的一句话：
/// <b>勾选 = 保护；未勾选 ≠ 立即结束</b>。
///
/// 每条用例都对应一个真实的误操作场景：
/// · 勾了保护却仍被结束 → 用户再也不敢用这个功能；
/// · 取消勾选就被结束 → 等于「点一下复选框就杀进程」；
/// · 未勾选就自动结束 → 重演 v0.5.1「默认全选，一点就关了壁纸引擎」；
/// · 系统进程被当成普通项 → 一次误关牵动整个服务栈。
/// </summary>
public class BackgroundProtectionTests
{
    private static BackgroundTargetFacts Facts(
        string name = "cloudmusic",
        string? path = @"D:\Apps\Music\cloudmusic.exe",
        bool isProtected = false,
        bool isEndTarget = true,
        bool systemEssential = false,
        bool identityUnknown = false,
        bool matchesGame = false)
        => new(name, path, isProtected, isEndTarget, systemEssential, identityUnknown, matchesGame);

    // ---------- 1. 选中即保护 ----------

    [Fact]
    public void 勾选保护的进程绝不会被结束()
    {
        // 即使历史配置里它还留着结束标记，保护也必须压过标记
        var facts = Facts(isProtected: true, isEndTarget: true);

        var verdict = BackgroundProtectionPolicy.Decide(facts);

        verdict.Eligible.Should().BeFalse();
        verdict.Reason.Should().Be(KillBlockReason.ProtectedByUser);
        verdict.Explanation.Should().Contain("保护");
        BackgroundKillPlan.Resolve([facts]).Should().BeEmpty();
    }

    [Fact]
    public void 勾选保护后同一进程立即退出结束目标名单()
    {
        var before = Facts(isProtected: false, isEndTarget: true);
        BackgroundKillPlan.Resolve([before]).Should().BeEquivalentTo(["cloudmusic"]);

        var after = before with { IsProtectedByUser = true };
        BackgroundKillPlan.Resolve([after]).Should().BeEmpty();
    }

    // ---------- 2. 取消勾选不等于立即结束 ----------

    [Fact]
    public void 取消勾选不会立即结束进程()
    {
        // 取消保护只是恢复「未保护」，没有任何一条路径会因此去结束它
        var facts = Facts(isProtected: false, isEndTarget: false);

        var verdict = BackgroundProtectionPolicy.Decide(facts);

        verdict.Eligible.Should().BeFalse();
        verdict.Reason.Should().Be(KillBlockReason.NotMarked);
        BackgroundKillPlan.Resolve([facts]).Should().BeEmpty();
    }

    [Fact]
    public void 多个未保护且未标记的进程一个都不会被结束()
    {
        var targets = new[]
        {
            Facts("chrome", isEndTarget: false),
            Facts("WeChat", isEndTarget: false),
            Facts("wallpaper64", isEndTarget: false),
        };

        BackgroundKillPlan.Resolve(targets).Should().BeEmpty();
    }

    // ---------- 3. 未勾选的普通进程仍须通过全部安全检查 ----------

    [Theory]
    [InlineData("nopath", null, false)]          // 读不到路径 → 结束后拉不回来
    [InlineData("DeltaForceClient-Win64", @"D:\Games\DeltaForce\client.exe", true)] // 游戏本体
    public void 未勾选只是有资格_仍须通过安全检查(string name, string? path, bool isGame)
    {
        var facts = Facts(name, path, isProtected: false, isEndTarget: false, matchesGame: isGame);

        var verdict = BackgroundProtectionPolicy.Decide(facts);

        verdict.Eligible.Should().BeFalse();
        verdict.Reason.Should().Be(isGame ? KillBlockReason.GameProcess : KillBlockReason.MissingPath);
    }

    [Fact]
    public void 只有标记过的普通进程才会进入结束名单()
    {
        var facts = new[]
        {
            Facts("chrome", isEndTarget: false),
            Facts("wallpaper64", isEndTarget: true),
        };

        BackgroundKillPlan.Resolve(facts).Should().BeEquivalentTo(["wallpaper64"]);
    }

    // ---------- 4. 必要进程即使未勾选也不能结束 ----------

    [Fact]
    public void 系统必要进程即使未勾选也拦得住()
    {
        // 未勾选保护 + 还留着结束标记，仍然必须由安全机制拦下
        var facts = Facts("SomeVendorService", @"C:\Program Files\Vendor\svc.exe",
            isProtected: false, isEndTarget: true, systemEssential: true);

        var verdict = BackgroundProtectionPolicy.Decide(facts);

        verdict.Eligible.Should().BeFalse();
        verdict.Reason.Should().Be(KillBlockReason.SystemProtected);
        BackgroundKillPlan.Resolve([facts]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("explorer")]      // 系统外壳
    [InlineData("MsMpEng")]       // 安全软件
    [InlineData("ctfmon")]        // 输入法
    public void 硬编码保护名单内的进程不能被标记为结束目标(string name)
    {
        var facts = Facts(name, @"C:\Program Files\App\app.exe", isEndTarget: false);

        BackgroundProtectionPolicy.DecideMarkable(facts with { IsSystemEssential = true })
            .Eligible.Should().BeFalse();
    }

    // ---------- 5. 未知进程默认不勾选且不会被自动结束 ----------

    [Fact]
    public void 未知进程默认既不保护也不结束()
    {
        var facts = Facts("0dcloud", @"D:\Apps\Unknown\0dcloud.exe",
            isProtected: false, isEndTarget: false, identityUnknown: true);

        var verdict = BackgroundProtectionPolicy.Decide(facts);

        verdict.Eligible.Should().BeFalse();
        verdict.Reason.Should().Be(KillBlockReason.NotMarked);
        BackgroundKillPlan.Resolve([facts]).Should().BeEmpty();
    }

    [Fact]
    public void 未知进程同样可以被用户显式标记后再结束()
    {
        // 「未知」不等于「不可管理」，但必须由用户明确点一次
        var facts = Facts("0dcloud", @"D:\Apps\Unknown\0dcloud.exe",
            isProtected: false, isEndTarget: true, identityUnknown: true);

        BackgroundProtectionPolicy.Decide(facts).Eligible.Should().BeTrue();
        BackgroundKillPlan.Resolve([facts]).Should().BeEquivalentTo(["0dcloud"]);
    }

    [Fact]
    public void 读不到路径的未知进程连标记都不允许()
    {
        var facts = Facts("mystery", path: null, isEndTarget: false, identityUnknown: true);

        BackgroundProtectionPolicy.DecideMarkable(facts).Eligible.Should().BeFalse();
        BackgroundProtectionPolicy.DecideMarkable(facts).Reason
            .Should().Be(KillBlockReason.MissingPath);
    }

    // ---------- 状态与去重 ----------

    [Fact]
    public void 受保护优先于系统关键进程的状态展示()
    {
        BackgroundProtectionPolicy.StateOf(isProtectedByUser: true, isSystemEssential: true)
            .Should().Be(ProtectionState.Protected);

        BackgroundProtectionPolicy.StateOf(isProtectedByUser: false, isSystemEssential: true)
            .Should().Be(ProtectionState.SystemProtected);
    }

    [Fact]
    public void 无法识别的进程状态文案必须明确写无法识别()
    {
        BackgroundProtectionPolicy.Describe(ProtectionState.Unprotected, identityUnknown: true)
            .Should().Be("无法识别");
        BackgroundProtectionPolicy.Describe(ProtectionState.Unprotected, identityUnknown: false)
            .Should().Be("未保护");
    }

    [Fact]
    public void 结束名单应按名字去重()
    {
        var facts = new[]
        {
            Facts("wallpaper64", @"D:\Apps\Wallpaper\wallpaper64.exe"),
            Facts("Wallpaper64", @"d:/apps/wallpaper/WALLPAPER64.EXE"),
        };

        BackgroundKillPlan.Resolve(facts).Should().ContainSingle();
    }

    [Fact]
    public void 路径比较前应规范化大小写与斜杠()
    {
        BackgroundProtectionPolicy.SamePath(@"d:/Apps/Tool/tool.exe", @"D:\apps\tool\TOOL.EXE")
            .Should().BeTrue();
        BackgroundProtectionPolicy.NormalizePath(@"D:\Apps\Tool\").Should().Be(@"d:\apps\tool");
        BackgroundProtectionPolicy.SamePath(@"D:\Apps\a.exe", @"D:\Apps2\a.exe").Should().BeFalse();
        BackgroundProtectionPolicy.SamePath(null, @"D:\Apps\a.exe").Should().BeFalse();
    }

    [Fact]
    public void 受保护名单应交给执行侧作为最后一道防线()
    {
        var facts = new[]
        {
            Facts("chrome", isProtected: true, isEndTarget: false),
            Facts("SomeService", isProtected: false, isEndTarget: true, systemEssential: true),
            Facts("wallpaper64", isProtected: false, isEndTarget: true),
        };

        // 系统关键进程不混进这里：它由 EssentialProcessGuard 用更准确的理由拦下
        BackgroundKillPlan.ProtectedNames(facts).Should().BeEquivalentTo(["chrome"]);
        BackgroundKillPlan.Resolve(facts).Should().BeEquivalentTo(["wallpaper64"]);
    }

    // ---------- 元数据读取失败时必须降级 ----------

    [Fact]
    public void 读不到任何文件信息时应显示未知应用而不是抛异常()
    {
        var entry = new BackgroundScanEntry(
            "0dcloud", @"D:\Not\Exists\0dcloud.exe", InPreset: false,
            BackgroundCandidateKind.Endable, null);

        var info = BackgroundProcessInspector.Inspect(entry, isProtectedByUser: false);

        info.AppName.Should().Be(BackgroundProcessInfo.UnknownAppName);
        info.Purpose.Should().Be(ProcessPurpose.UnknownText);
        info.PurposeBasis.Should().NotBeNullOrWhiteSpace("没有依据也必须说清「没有依据」");
        info.IsIdentityUnknown.Should().BeTrue();
        info.Publisher.Should().BeNull();
    }

    [Fact]
    public void 路径完全读不到时也不应崩溃()
    {
        var entry = new BackgroundScanEntry(
            "mystery", null, InPreset: false, BackgroundCandidateKind.Endable, null);

        var info = BackgroundProcessInspector.Inspect(entry, isProtectedByUser: false);

        info.ExecutablePath.Should().BeNull();
        info.AppName.Should().Be(BackgroundProcessInfo.UnknownAppName);
        info.Should().NotBeNull();
    }

    [Fact]
    public void 身份读不出来但用途能按识别表确定时不算未知应用()
    {
        // 识别表里有的进程：即使 exe 读不到版本信息，也明确知道它是什么
        var entry = new BackgroundScanEntry(
            "cloudmusic", @"D:\Not\Exists\cloudmusic.exe", InPreset: true,
            BackgroundCandidateKind.Endable, null);

        var info = BackgroundProcessInspector.Inspect(entry, isProtectedByUser: false);

        info.AppName.Should().Be("网易云音乐");
        info.Purpose.Should().Contain("音乐");
        info.PurposeBasis.Should().Contain("内置识别表");
        info.IsIdentityUnknown.Should().BeFalse();
    }

    [Fact]
    public void 系统目录下的组件应识别为系统组件()
    {
        var entry = new BackgroundScanEntry(
            "sometool", @"C:\Windows\System32\sometool.exe", InPreset: false,
            BackgroundCandidateKind.Endable, null);

        var info = BackgroundProcessInspector.Inspect(
            entry, isProtectedByUser: false, systemRoot: @"C:\Windows");

        info.Purpose.Should().Contain("系统组件");
        info.PurposeBasis.Should().Contain("%SystemRoot%");
    }

    [Fact]
    public void 系统关键进程的判定依据必须写出来()
    {
        var entry = new BackgroundScanEntry(
            "explorer", @"C:\Windows\explorer.exe", InPreset: false,
            BackgroundCandidateKind.SystemProtected, "受保护名单（系统外壳 / 安全软件 / 输入法 / 本程序）");

        var info = BackgroundProcessInspector.Inspect(entry, isProtectedByUser: false);

        info.IsSystemEssential.Should().BeTrue();
        info.SystemReason.Should().Contain("受保护名单");
        info.State.Should().Be(ProtectionState.SystemProtected);
    }

    [Fact]
    public void 既勾了保护又是系统关键进程时系统保护不能被覆盖()
    {
        // 回归用例：曾经把「是否系统关键」从展示状态推导，
        // 用户勾上保护后状态变成「受保护」，系统保护这件事就被抹掉了，
        // 结果这一行反而可以被标记为结束目标。
        var entry = new BackgroundScanEntry(
            "explorer", @"C:\Windows\explorer.exe", InPreset: false,
            BackgroundCandidateKind.SystemProtected, "受保护名单（系统外壳 / 安全软件 / 输入法 / 本程序）");

        var info = BackgroundProcessInspector.Inspect(entry, isProtectedByUser: true);

        info.State.Should().Be(ProtectionState.Protected, "界面上保护优先显示");
        info.IsSystemEssential.Should().BeTrue("系统保护不能因为用户勾选而消失");
    }

    [Fact]
    public void 未知进程的应用名不能被编造()
    {
        var metadata = ProcessFileMetadata.Empty;

        BackgroundProcessInspector.ResolveAppName("0dcloud", metadata, identityUnknown: true)
            .Should().Be(BackgroundProcessInfo.UnknownAppName);
    }

    // ---------- 应用名中文化 ----------

    [Fact]
    public void 文件描述是英文而产品名是中文时显示中文()
    {
        // 真实场景：exe 的 FileDescription 写英文，ProductName 才是中文品牌名
        var metadata = new ProcessFileMetadata(
            "Some Desktop Helper", "某某桌面助手", "某某科技", "3.1.0");

        BackgroundProcessInspector.ResolveAppName("sdhelper", metadata, identityUnknown: false)
            .Should().Be("某某桌面助手");
    }

    [Fact]
    public void 元数据全英文但内置表有中文名时显示中文()
    {
        // Chrome 的版本资源是英文（Google Chrome），直接显示对中文用户不如「Chrome 浏览器」
        var metadata = new ProcessFileMetadata(
            "Google Chrome", "Google Chrome", "Google LLC", "141.0");

        BackgroundProcessInspector.ResolveAppName("chrome", metadata, identityUnknown: false)
            .Should().Be("Chrome 浏览器");
    }

    [Fact]
    public void 文件描述本身就是中文时直接用文件描述()
    {
        var metadata = new ProcessFileMetadata("微信", "WeChat", "腾讯", "4.1.0");

        BackgroundProcessInspector.ResolveAppName("WeChat", metadata, identityUnknown: false)
            .Should().Be("微信");
    }

    [Fact]
    public void 纯英文软件不被编造中文译名()
    {
        // 没有任何来源含中文时保持原文 —— 中文优先 ≠ 一律中文化
        var metadata = new ProcessFileMetadata("Spotify", "Spotify", "Spotify AB", "1.2");

        BackgroundProcessInspector.ResolveAppName("Spotify", metadata, identityUnknown: false)
            .Should().Be("Spotify");
    }

    [Theory]
    [InlineData("网易云音乐", true)]
    [InlineData("Chrome 浏览器", true)]
    [InlineData("Spotify", false)]
    [InlineData("Microsoft Edge", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 中文检测只认汉字(string? text, bool expected)
    {
        BackgroundProcessInspector.ContainsChinese(text).Should().Be(expected);
    }

    [Fact]
    public void 扫描结果不包含系统关键进程()
    {
        // 用户要求这类进程不显示（既不保护也不能标记结束，纯噪音）。
        // 沙箱里进程很少，这条同时锁住契约：只要有结果，就必须都是可管理项。
        var entries = BackgroundCandidateScan.ScanEntries();

        entries.Should().OnlyContain(e => e.Kind == BackgroundCandidateKind.Endable);
        entries.Should().OnlyContain(e => e.SystemReason == null);
    }

    // ---------- 搜索与筛选 ----------

    private static BackgroundProcessInfo Info(
        string name = "cloudmusic",
        string? path = @"D:\Apps\Music\cloudmusic.exe",
        bool systemEssential = false,
        bool unknown = false,
        string appName = "网易云音乐",
        string purpose = "音乐播放客户端",
        string? publisher = "网易")
        => new(
            name, path, appName, null, null, publisher, null,
            purpose, "内置识别表（进程名精确匹配）", !unknown, unknown,
            systemEssential ? ProtectionState.SystemProtected : ProtectionState.Unprotected,
            systemEssential ? "Windows 系统组件" : null,
            InPreset: false,
            IsSystemProtected: systemEssential);

    [Theory]
    [InlineData("cloudmusic")]           // 进程名
    [InlineData("网易云")]                // 应用名
    [InlineData("Music")]                // 路径
    [InlineData("网易")]                  // 发布者
    [InlineData("音乐")]                  // 用途
    public void 搜索应命中应用名进程名路径发布者与用途(string query)
    {
        BackgroundProcessFilter.Matches(Info(), isProtected: false, BackgroundListFilter.All, query)
            .Should().BeTrue($"搜索「{query}」应命中这一行");
    }

    [Fact]
    public void 搜索不区分大小写且空搜索返回全部()
    {
        BackgroundProcessFilter.Matches(Info(), false, BackgroundListFilter.All, "CLOUDMUSIC").Should().BeTrue();
        BackgroundProcessFilter.Matches(Info(), false, BackgroundListFilter.All, "   ").Should().BeTrue();
        BackgroundProcessFilter.Matches(Info(), false, BackgroundListFilter.All, "不存在的词").Should().BeFalse();
    }

    [Fact]
    public void 未保护筛选不含系统关键进程()
    {
        // 系统关键进程不是「用户没保护」，它由安全机制管理，混在一起会误导
        var system = Info("dwm", @"C:\Windows\dwm.exe", systemEssential: true, appName: "桌面窗口管理器");

        BackgroundProcessFilter.Matches(system, false, BackgroundListFilter.Unprotected, null)
            .Should().BeFalse();
        BackgroundProcessFilter.Matches(system, false, BackgroundListFilter.SystemProtected, null)
            .Should().BeTrue();
        BackgroundProcessFilter.Matches(Info(), false, BackgroundListFilter.Unprotected, null)
            .Should().BeTrue();
    }

    [Fact]
    public void 受保护与无法识别筛选各自独立()
    {
        BackgroundProcessFilter.Matches(Info(), isProtected: true, BackgroundListFilter.Protected, null)
            .Should().BeTrue();
        BackgroundProcessFilter.Matches(Info(), isProtected: false, BackgroundListFilter.Protected, null)
            .Should().BeFalse();

        var unknown = Info("0dcloud", appName: BackgroundProcessInfo.UnknownAppName,
            purpose: ProcessPurpose.UnknownText, publisher: null, unknown: true);
        BackgroundProcessFilter.Matches(unknown, false, BackgroundListFilter.Unknown, null)
            .Should().BeTrue();
        BackgroundProcessFilter.Matches(unknown, false, BackgroundListFilter.Unprotected, null)
            .Should().BeTrue("无法识别也是一个未保护的进程，两个筛选维度不互斥");
    }

    // ---------- 执行侧：受保护名单必须拦得住 ----------

    [Fact]
    public void 结束动作应跳过受保护进程并如实上报()
    {
        // 模拟「调用方绕过界面，把受保护的进程塞进目标列表」
        var exe = Path.Combine(Path.GetTempPath(), $"go-protected-{Guid.NewGuid():N}.exe");
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
            var name = proc.ProcessName;

            var action = new EndProcessAction([name], [name]);
            action.Apply();

            action.Killed.Should().BeEmpty("勾选保护的进程不允许被结束");
            action.ProtectedSkipped.Should().ContainSingle()
                .Which.Reason.Should().Contain("保护");
            action.Summary.Should().Contain("已保护");
            proc.HasExited.Should().BeFalse("进程必须还在跑");
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void 未受保护的同一进程在明确标记后仍可被结束()
    {
        // 对照组：证明上一行的「没结束」是因为保护，而不是因为流程根本不工作
        var exe = Path.Combine(Path.GetTempPath(), $"go-target-{Guid.NewGuid():N}.exe");
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
            var action = new EndProcessAction([proc.ProcessName], []);
            action.Apply();

            action.Killed.Should().NotBeEmpty();
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void 扫描失败不应抛出到界面()
    {
        // 扫描内部逐进程容错；整体失败时返回空集合而不是异常
        var act = () => BackgroundCandidateScan.ScanEntries([]);

        act.Should().NotThrow();
        act().Should().NotBeNull();
    }
}
