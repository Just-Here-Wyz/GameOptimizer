namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 可控的工作集释放器：真实机器上「权限不足」「API 失败」「回读无变化」这三条路径
/// 无法稳定复现，只能靠假实现把判定逻辑锁死。
/// </summary>
internal sealed class FakeWorkingSetReleaser : IWorkingSetReleaser
{
    /// <summary>释放成功后默认把工作集降到原来的 10%，模拟真实的页面置换。</summary>
    public const double ShrinkRatio = 0.1;

    public List<ProcessMemorySnapshot> Processes { get; } = [];

    /// <summary>按进程号记录当前工作集。Release 会就地改写，供回读使用。</summary>
    public Dictionary<int, long> WorkingSets { get; } = [];

    /// <summary>指定某个进程的释放结果；未指定时按「释放成功」处理。</summary>
    public Dictionary<int, ReleaseOutcome> Outcomes { get; } = [];

    /// <summary>Release 后不缩小的进程号：模拟「API 报成功但实际没变化」。</summary>
    public HashSet<int> NoEffectProcesses { get; } = [];

    /// <summary>回读直接失败的进程号：模拟释放后进程退出。</summary>
    public HashSet<int> VanishedProcesses { get; } = [];

    public long AvailableBytes { get; set; } = 1024L * 1024 * 1024;

    public long GetAvailablePhysicalBytes() => AvailableBytes;

    public Exception? EnumerateFailure { get; set; }

    public List<int> ReleaseCalls { get; } = [];

    public FakeWorkingSetReleaser Add(int pid, string name, long workingSetBytes, int sessionId = 1,
        string? path = @"C:\Tools\demo.exe")
    {
        Processes.Add(new ProcessMemorySnapshot(pid, name, sessionId, workingSetBytes, path));
        WorkingSets[pid] = workingSetBytes;
        return this;
    }

    public IReadOnlyList<ProcessMemorySnapshot> EnumerateProcesses()
        => EnumerateFailure is not null ? throw EnumerateFailure : Processes;

    public ReleaseOutcome Release(int processId)
    {
        ReleaseCalls.Add(processId);

        var outcome = Outcomes.TryGetValue(processId, out var o) ? o : new ReleaseOutcome(ReleaseStatus.Released);
        if (outcome.Status == ReleaseStatus.Released && !NoEffectProcesses.Contains(processId))
            WorkingSets[processId] = (long)(WorkingSets[processId] * ShrinkRatio);

        return outcome;
    }

    public long? TryGetWorkingSet(int processId)
        => VanishedProcesses.Contains(processId) || !WorkingSets.TryGetValue(processId, out var ws) ? null : ws;
}

/// <summary>
/// MemoryCleanAction 的用例：正常释放 / 权限不足 / API 失败 / 回读校验，
/// 以及「绝不结束进程」这条与 EndProcessAction 的分界线。
/// </summary>
public class MemoryCleanActionTests
{
    private const long Mb = 1024L * 1024;

    private static MemoryCleanOptions Options(FakeWorkingSetReleaser fake, int? foreground = null, int selfPid = 0)
        => new()
        {
            MinWorkingSetBytes = 16 * Mb,
            SelfProcessId = selfPid,
            ForegroundProcessId = () => foreground,
            ServiceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };

    private static MemoryReleaseReport Apply(MemoryCleanAction action) => action.Report!;

    // ---------- 正常释放路径 ----------

    [Fact]
    public void 正常释放应把可验证的释放量汇总出来()
    {
        var fake = new FakeWorkingSetReleaser()
            .Add(1001, "game", 200 * Mb)
            .Add(1002, "browser", 300 * Mb);
        var action = new MemoryCleanAction(fake, Options(fake));

        action.Apply();

        var report = Apply(action);
        report.VerifiedCount.Should().Be(2);
        // 每个都降到 10%，共释放 90% × (200 + 300) MB
        report.VerifiedReleasedBytes.Should().Be((long)(450 * Mb));
        report.Summary.Should().StartWith("已释放");
        action.IsActive().Should().BeTrue();
        action.FailureReason.Should().Be(MemoryCleanFailureReason.None);
    }

    [Fact]
    public void 展示口径应优先用系统可用内存实测值()
    {
        // 实测教训：一次清理里逐进程工作集减少合计 2.9 GB，
        // 系统可用内存其实只涨了 461 MB——共享页面被每个进程重复计数。
        // 拿大的那个展示就是虚假宣传。
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        var availableCalls = 0;
        var releaser = new StubAvailableReleaser(fake, () => ++availableCalls == 1 ? 1000L * Mb : 1461L * Mb);
        var action = new MemoryCleanAction(releaser, Options(fake));

        action.Apply();

        var report = action.Report!;
        report.VerifiedReleasedBytes.Should().Be((long)(180 * Mb), "逐进程口径照旧算给明细用");
        report.ReportedReleasedBytes.Should().Be(461 * Mb, "展示必须用系统可用内存的实测差");
        report.ReportedFromSystem.Should().BeTrue();
        report.Summary.Should().Contain("461 MB").And.NotContain("180 MB");
    }

    [Fact]
    public void 系统可用内存测不到时应退化为逐进程口径并注明()
    {
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        var releaser = new StubAvailableReleaser(fake, static () => 0);
        var action = new MemoryCleanAction(releaser, Options(fake));

        action.Apply();

        var report = action.Report!;
        report.ReportedFromSystem.Should().BeFalse();
        report.ReportedReleasedBytes.Should().Be(report.VerifiedReleasedBytes);
        report.Summary.Should().Contain("系统可用内存读数不可用");
    }

    [Fact]
    public void 系统可用内存反而下降时不应报负数()
    {
        // 清理同时别的进程在猛吃内存，可用量可能不升反降。
        // 这时只能说「未明显变化」，绝不能出现「已释放 -200 MB」。
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        var availableCalls = 0;
        var releaser = new StubAvailableReleaser(fake, () => ++availableCalls == 1 ? 1000L * Mb : 800L * Mb);
        var action = new MemoryCleanAction(releaser, Options(fake));

        action.Apply();

        var report = action.Report!;
        report.ReportedReleasedBytes.Should().Be(0);
        report.Summary.Should().NotContain("-");
    }

    [Fact]
    public void 释放量文本应按量级选单位()
    {
        // 用户要求的展示形式就是「已释放 1.2 GB」
        MemoryReleaseText.Format((long)(1.2 * 1024 * 1024 * 1024)).Should().Be("1.2 GB");
        MemoryReleaseText.Format(236 * Mb).Should().Be("236 MB");
        MemoryReleaseText.Format(512 * 1024).Should().Be("512 KB");
        MemoryReleaseText.Format(0).Should().Be("0 B");
        MemoryReleaseText.Format(-1).Should().Be("0 B");
    }

    // ---------- 界面那句话（口径判定放 Core，App 层只做绑定） ----------

    [Fact]
    public void 结果文案应使用系统可用内存口径()
    {
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        var availableCalls = 0;
        var releaser = new StubAvailableReleaser(fake, () => ++availableCalls == 1 ? 1000L * Mb : 1461L * Mb);
        var action = new MemoryCleanAction(releaser, Options(fake));
        action.Apply();

        var text = MemoryReleaseText.DescribeResult(action.Report!);

        text.Should().StartWith("已释放");
        text.Should().Contain("461 MB");
        text.Should().Contain("1 个进程");
        text.Should().NotContain("180 MB", "不能拿逐进程虚高的值展示");
    }

    [Fact]
    public void 系统可用内存没涨时文案应如实说明()
    {
        // 实测会真的出现这种情况：页面从工作集挪到备用列表，可用内存并没变多
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        var releaser = new StubAvailableReleaser(fake, static () => 1000L * Mb);
        var action = new MemoryCleanAction(releaser, Options(fake));
        action.Apply();

        var text = MemoryReleaseText.DescribeResult(action.Report!);

        text.Should().Contain("未明显变化");
        text.Should().NotContain("已释放 0 B", "挂着 0 B 会让人以为功能坏了");
    }

    [Fact]
    public void 没有候选进程时文案应说明内存已充裕()
    {
        var fake = new FakeWorkingSetReleaser().Add(1001, "tiny", 4 * Mb);
        var action = new MemoryCleanAction(fake, Options(fake));
        action.Apply();

        MemoryReleaseText.DescribeResult(action.Report!).Should().Contain("没有需要释放");
    }

    [Fact]
    public void 有拒绝访问时文案应附上数量()
    {
        var fake = new FakeWorkingSetReleaser()
            .Add(1001, "mine", 200 * Mb)
            .Add(1002, "other", 200 * Mb);
        fake.Outcomes[1002] = new ReleaseOutcome(ReleaseStatus.AccessDenied, 5, "拒绝访问");
        var availableCalls = 0;
        var releaser = new StubAvailableReleaser(fake, () => ++availableCalls == 1 ? 1000L * Mb : 1400L * Mb);
        var action = new MemoryCleanAction(releaser, Options(fake));
        action.Apply();

        MemoryReleaseText.DescribeResult(action.Report!).Should().Contain("1 个进程需管理员权限");
    }

    [Fact]
    public void 只动工作集不得结束任何进程()
    {
        // 与 EndProcessAction 的分界线：清理前后目标进程必须仍然存在
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        var action = new MemoryCleanAction(fake, Options(fake));

        action.Apply();

        fake.WorkingSets.Should().ContainKey(1001, "释放工作集不是结束进程");
        action.Report!.Entries.Single().AfterBytes.Should().BeLessThan(action.Report.Entries.Single().BeforeBytes);
    }

    [Fact]
    public void 没有候选进程时应如实说明而不是报释放成功()
    {
        // 内存本来就充裕：不是失败，也绝不能声称释放了多少
        var fake = new FakeWorkingSetReleaser().Add(1001, "tiny", 4 * Mb);
        var action = new MemoryCleanAction(fake, Options(fake));

        action.Apply();

        action.Report!.NothingToDo.Should().BeTrue();
        action.Report.Summary.Should().Contain("没有需要释放");
        fake.ReleaseCalls.Should().BeEmpty();
        action.IsActive().Should().BeFalse();
    }

    // ---------- 权限不足 ----------

    [Fact]
    public void 全部进程拒绝访问应抛出权限原因()
    {
        var fake = new FakeWorkingSetReleaser()
            .Add(1001, "service_host", 200 * Mb)
            .Add(1002, "other_user", 300 * Mb);
        fake.Outcomes[1001] = new ReleaseOutcome(ReleaseStatus.AccessDenied, 5, "拒绝访问");
        fake.Outcomes[1002] = new ReleaseOutcome(ReleaseStatus.AccessDenied, 5, "拒绝访问");

        var action = new MemoryCleanAction(fake, Options(fake));

        var act = () => action.Apply();

        var ex = act.Should().Throw<MemoryCleanException>().Which;
        ex.Reason.Should().Be(MemoryCleanFailureReason.ElevationRequired);
        ex.Message.Should().Contain("管理员");
        action.IsActive().Should().BeFalse();
        action.Report!.VerifiedReleasedBytes.Should().Be(0);
    }

    [Fact]
    public void 部分进程拒绝访问但有成功项时不应报错()
    {
        var fake = new FakeWorkingSetReleaser()
            .Add(1001, "mine", 200 * Mb)
            .Add(1002, "other_user", 300 * Mb);
        fake.Outcomes[1002] = new ReleaseOutcome(ReleaseStatus.AccessDenied, 5, "拒绝访问");

        var action = new MemoryCleanAction(fake, Options(fake));
        action.Apply();

        action.Report!.DeniedCount.Should().Be(1);
        action.Report.Summary.Should().Contain("拒绝访问");
        action.IsActive().Should().BeTrue();
    }

    // ---------- API 调用失败 ----------

    [Fact]
    public void 接口全部失败应抛出ApiFailed原因()
    {
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        fake.Outcomes[1001] = new ReleaseOutcome(ReleaseStatus.ApiFailed, 87, "参数错误");

        var action = new MemoryCleanAction(fake, Options(fake));

        var act = () => action.Apply();

        act.Should().Throw<MemoryCleanException>()
            .Which.Reason.Should().Be(MemoryCleanFailureReason.ApiFailed);
        action.Report!.FailedCount.Should().Be(1);
    }

    [Fact]
    public void 枚举进程失败必须抛错而不是静默报成功()
    {
        var fake = new FakeWorkingSetReleaser { EnumerateFailure = new InvalidOperationException("枚举被拒绝") };
        var action = new MemoryCleanAction(fake, Options(fake));

        var act = () => action.Apply();

        var ex = act.Should().Throw<MemoryCleanException>().Which;
        ex.Reason.Should().Be(MemoryCleanFailureReason.EnumerationFailed);
        ex.Message.Should().Contain("枚举被拒绝");
        action.IsActive().Should().BeFalse();
    }

    // ---------- 释放量回读校验 ----------

    [Fact]
    public void 接口报成功但回读没下降必须判失败()
    {
        // 这是本模块最危险的一类假成功：API 返回 true，内存其实没动，
        // 只看返回值就会向用户报「已释放 1.2 GB」。
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        fake.NoEffectProcesses.Add(1001);

        var action = new MemoryCleanAction(fake, Options(fake));

        var act = () => action.Apply();

        act.Should().Throw<MemoryCleanException>()
            .Which.Reason.Should().Be(MemoryCleanFailureReason.ReadbackMismatch);
        action.Report!.VerifiedCount.Should().Be(0);
        action.Report.VerifiedReleasedBytes.Should().Be(0);
    }

    [Fact]
    public void 释放后进程退出不应算成已释放()
    {
        // 进程在回读前退出：既不是我们释放的，也不能假装清理过
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        fake.VanishedProcesses.Add(1001);

        var action = new MemoryCleanAction(fake, Options(fake));

        var act = () => action.Apply();

        act.Should().Throw<MemoryCleanException>();
        action.Report!.VanishedCount.Should().Be(1);
        action.Report.VerifiedReleasedBytes.Should().Be(0);
    }

    [Fact]
    public void 系统可用内存读不到时不得展示差值()
    {
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        var releaser = new StubAvailableReleaser(fake, static () => 0);
        var action = new MemoryCleanAction(releaser, Options(fake));

        action.Apply();

        action.Report!.HasSystemMeasurement.Should().BeFalse();
        action.Report.SystemAvailableDeltaBytes.Should().Be(0);
        // 逐进程口径仍然可验证
        action.Report.VerifiedReleasedBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void 系统可用内存前后差值应与逐进程口径分开统计()
    {
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        var availableCalls = 0;
        // 第一次 1GB，第二次 1.5GB
        var releaser = new StubAvailableReleaser(fake, () => ++availableCalls == 1 ? 1024L * Mb : 1536L * Mb);
        var action = new MemoryCleanAction(releaser, Options(fake));

        action.Apply();

        action.Report!.SystemAvailableDeltaBytes.Should().Be(512 * Mb);
        action.Report.VerifiedReleasedBytes.Should().Be((long)(180 * Mb));
    }    /// <summary>只替换可用内存读数的包装器。</summary>
    private sealed class StubAvailableReleaser(FakeWorkingSetReleaser inner, Func<long> available) : IWorkingSetReleaser
    {
        public long GetAvailablePhysicalBytes() => available();
        public IReadOnlyList<ProcessMemorySnapshot> EnumerateProcesses() => inner.EnumerateProcesses();
        public ReleaseOutcome Release(int processId) => inner.Release(processId);
        public long? TryGetWorkingSet(int processId) => inner.TryGetWorkingSet(processId);
    }

    // ---------- 跳过策略 ----------

    [Fact]
    public void 应跳过系统必要进程与自身()
    {
        var fake = new FakeWorkingSetReleaser()
            .Add(4, "System", 500 * Mb, sessionId: 0)
            .Add(1000, "svchost", 200 * Mb, sessionId: 0)
            .Add(1001, "GameOptimizer.App", 200 * Mb)
            .Add(1002, "explorer", 200 * Mb)
            .Add(1003, "game", 200 * Mb);
        var action = new MemoryCleanAction(fake, Options(fake, selfPid: 1001));

        action.Apply();

        fake.ReleaseCalls.Should().BeEquivalentTo([1003]);
        action.Report!.Skipped.Should().Be(4);
    }

    [Fact]
    public void 应跳过前台窗口进程避免用户察觉卡顿()
    {
        var fake = new FakeWorkingSetReleaser()
            .Add(1001, "foreground", 200 * Mb)
            .Add(1002, "background", 200 * Mb);
        var action = new MemoryCleanAction(fake, Options(fake, foreground: 1001));

        action.Apply();

        fake.ReleaseCalls.Should().BeEquivalentTo([1002]);
    }

    [Fact]
    public void 关闭前台保护后应清理前台进程()
    {
        var fake = new FakeWorkingSetReleaser().Add(1001, "foreground", 200 * Mb);
        var options = new MemoryCleanOptions
        {
            SelfProcessId = 0,
            ProtectForegroundProcess = false,
            ForegroundProcessId = () => 1001,
            ServiceNames = new HashSet<string>(),
        };

        new MemoryCleanAction(fake, options).Apply();

        fake.ReleaseCalls.Should().Contain(1001);
    }

    [Fact]
    public void 工作集太小的进程不必清理()
    {
        var fake = new FakeWorkingSetReleaser()
            .Add(1001, "small", 8 * Mb)
            .Add(1002, "big", 100 * Mb);
        var action = new MemoryCleanAction(fake, Options(fake));

        action.Apply();

        fake.ReleaseCalls.Should().BeEquivalentTo([1002]);
    }

    // ---------- 与游戏模式框架的协作 ----------

    [Fact]
    public void Revert应为空操作因为工作集无需还原()
    {
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        var action = new MemoryCleanAction(fake, Options(fake));
        action.Apply();

        var act = () => action.Revert();

        act.Should().NotThrow();
        fake.ReleaseCalls.Should().HaveCount(1, "Revert 不应再次释放或结束进程");
    }

    [Fact]
    public void 接入总控后可正常进入退出且不影响其他动作()
    {
        var log = new List<string>();
        var fake = new FakeWorkingSetReleaser().Add(1001, "game", 200 * Mb);
        var memory = new MemoryCleanAction(fake, Options(fake));
        var controller = new GameModeController();
        controller.Add(new RecordingAction("A", log));
        controller.Add(memory);

        controller.Enter().Should().BeEmpty();
        log.Should().Equal("apply:A");
        memory.IsActive().Should().BeTrue();

        controller.ExitWithReport().Should().BeEmpty();
        log.Should().Equal("apply:A", "revert:A");
    }

    [Fact]
    public void 清理失败应让总控回滚已执行的动作()
    {
        // 与现有语义一致：任一动作失败立即回滚，不留半套状态
        var log = new List<string>();
        var fake = new FakeWorkingSetReleaser().Add(1001, "locked", 200 * Mb);
        fake.Outcomes[1001] = new ReleaseOutcome(ReleaseStatus.AccessDenied, 5, "拒绝访问");

        var controller = new GameModeController();
        controller.Add(new RecordingAction("A", log));
        controller.Add(new MemoryCleanAction(fake, Options(fake)));

        var errors = controller.Enter();

        errors.Should().ContainSingle().Which.Should().Contain("释放闲置内存");
        log.Should().Equal("apply:A", "revert:A");
        controller.IsActive.Should().BeFalse();
    }

    // ---------- 真实 Win32 通道 ----------

    [Fact]
    public void 真实释放器应能释放本进程工作集()
    {
        // 用自己当目标：不制造额外进程、不碰别人的内存，验证 P/Invoke 声明与回读是否可用
        var releaser = new Win32WorkingSetReleaser();
        var before = releaser.TryGetWorkingSet(Environment.ProcessId);

        var outcome = releaser.Release(Environment.ProcessId);

        outcome.Status.Should().Be(ReleaseStatus.Released, "对自身进程有完全权限，API 必须成功");
        var after = releaser.TryGetWorkingSet(Environment.ProcessId);
        after.Should().NotBeNull();
        after.Should().BeLessThanOrEqualTo(before ?? long.MaxValue);
    }

    [Fact]
    public void 真实释放器应能读到系统可用内存()
    {
        var bytes = new Win32WorkingSetReleaser().GetAvailablePhysicalBytes();

        bytes.Should().BeGreaterThan(0);
        bytes.Should().BeLessThan((long)GameOptimizer.Core.Collection.SystemMemoryInfo.TotalPhysicalMb * 1024 * 1024);
    }

    [Fact]
    public void 真实释放器枚举到的进程应带工作集与会话号()
    {
        var list = new Win32WorkingSetReleaser().EnumerateProcesses();

        list.Should().NotBeEmpty();
        list.Should().Contain(p => p.ProcessId == Environment.ProcessId);
        list.First(p => p.ProcessId == Environment.ProcessId).WorkingSetBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void 释放不存在的进程应返回已退出而不是抛异常()
    {
        var outcome = new Win32WorkingSetReleaser().Release(int.MaxValue);

        outcome.Status.Should().BeOneOf(ReleaseStatus.Vanished, ReleaseStatus.ApiFailed);
        outcome.Status.Should().NotBe(ReleaseStatus.Released);
    }
}
