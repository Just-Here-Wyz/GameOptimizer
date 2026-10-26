namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;
using GameOptimizer.Core.Library;

/// <summary>
/// 恢复资格判定：快照里有记录 ≠ 可以启动。
///
/// 这一层是恢复流程唯一的「安全闸门」，所以每条跳过理由都要有用例钉住：
/// 文件没了、系统组件、应用商店应用、重复目标、已经在跑——全都不许进恢复队列。
/// </summary>
public class ProcessRestorePlannerTests
{
    private static ProcessRestoreEnvironment Env(
        Func<string, bool>? fileExists = null,
        Func<string, bool>? isProcessRunning = null,
        Func<string, ProcessFileMetadata>? readMetadata = null)
        => new(
            fileExists: fileExists ?? (_ => true),
            isProcessRunning: isProcessRunning ?? (_ => false),
            readMetadata: readMetadata ?? (_ => ProcessFileMetadata.Empty),
            systemRoot: @"C:\Windows",
            packageRoot: @"C:\Program Files\WindowsApps");

    [Fact]
    public void 普通后台程序应可恢复且信息完整()
    {
        var record = new RestoredProcess("chrome", @"D:\Apps\Chrome\chrome.exe");

        var candidate = ProcessRestorePlanner.Build([record], Env(
            readMetadata: _ => new ProcessFileMetadata("Chrome 浏览器", "Google Chrome", "Google LLC", "120.0"))).Single();

        candidate.Eligible.Should().BeTrue();
        candidate.SkipKind.Should().Be(RestoreSkipKind.None);
        candidate.Reason.Should().Be(ProcessRestorePlanner.RestorableReason);
        candidate.AppName.Should().Be("Chrome 浏览器", "有中文名就用中文名");
        candidate.ExecutableName.Should().Be("chrome.exe");
        candidate.Publisher.Should().Be("Google LLC");
        candidate.Record.Should().BeSameAs(record, "执行侧必须拿到同一条记录，恢复结果要写回它");
    }

    [Fact]
    public void 读不到版本信息时应写未知应用而不是编造()
    {
        var candidate = ProcessRestorePlanner.Build(
            [new RestoredProcess("0dcloud", @"D:\Apps\0d\0dcloud.exe")], Env()).Single();

        candidate.Eligible.Should().BeTrue();
        candidate.AppName.Should().Be(BackgroundProcessInfo.UnknownAppName);
        candidate.Publisher.Should().BeNull();
    }

    [Fact]
    public void 缺少可执行文件路径的记录不能恢复()
    {
        var candidate = ProcessRestorePlanner.Build([new RestoredProcess("ghost", "")], Env()).Single();

        candidate.Eligible.Should().BeFalse();
        candidate.SkipKind.Should().Be(RestoreSkipKind.MissingLaunchPath);
        candidate.Reason.Should().Contain("路径");
    }

    [Fact]
    public void 文件已不存在的记录不能恢复()
    {
        var candidate = ProcessRestorePlanner.Build(
            [new RestoredProcess("old", @"D:\Gone\old.exe")],
            Env(fileExists: _ => false)).Single();

        candidate.SkipKind.Should().Be(RestoreSkipKind.MissingFile);
        candidate.Reason.Should().Contain("已不存在");
    }

    [Fact]
    public void 系统目录里的组件不能恢复()
    {
        var candidate = ProcessRestorePlanner.Build(
            [new RestoredProcess("svchost", @"C:\Windows\System32\svchost.exe")], Env()).Single();

        candidate.SkipKind.Should().Be(RestoreSkipKind.SystemCritical);
        candidate.Reason.Should().Contain("系统组件");
    }

    [Fact]
    public void 硬编码保护名单里的进程不能恢复()
    {
        // 路径不在系统目录，但名字属于「系统外壳 / 安全软件 / 输入法 / 本程序」兜底名单
        var candidate = ProcessRestorePlanner.Build(
            [new RestoredProcess("explorer", @"D:\Tools\explorer.exe")], Env()).Single();

        candidate.SkipKind.Should().Be(RestoreSkipKind.SystemCritical);
        candidate.Eligible.Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Vendor.App_1.0.0.0_x64__abc\App.exe")]
    [InlineData(@"D:\WindowsApps\Vendor.App_1.0.0.0_x64__abc\App.exe")]
    public void 应用商店应用不能按包内exe直接启动(string path)
    {
        var candidate = ProcessRestorePlanner.Build([new RestoredProcess("App", path)], Env()).Single();

        candidate.SkipKind.Should().Be(RestoreSkipKind.PackagedApp);
        candidate.Reason.Should().Contain("WindowsApps");
    }

    [Fact]
    public void 重复目标只保留一条可恢复项()
    {
        var candidates = ProcessRestorePlanner.Build(
            [
                new RestoredProcess("app", @"D:\Apps\a.exe"),
                new RestoredProcess("app", "d:/apps/A.EXE"),
            ], Env());

        candidates.Should().HaveCount(2, "重复项要在列表里说明原因，而不是凭空消失");
        candidates[0].Eligible.Should().BeTrue();
        candidates[1].SkipKind.Should().Be(RestoreSkipKind.Duplicate);
        candidates.Count(c => c.Eligible).Should().Be(1);
    }

    [Fact]
    public void 已经在运行的进程不再重复启动()
    {
        var candidate = ProcessRestorePlanner.Build(
            [new RestoredProcess("chrome", @"D:\Apps\chrome.exe")],
            Env(isProcessRunning: name => name.Equals("chrome", StringComparison.OrdinalIgnoreCase))).Single();

        candidate.SkipKind.Should().Be(RestoreSkipKind.AlreadyRunning);
        candidate.Reason.Should().Contain("已在运行");
    }

    [Fact]
    public void 空输入应返回空列表而不是抛异常()
    {
        ProcessRestorePlanner.Build(null, Env()).Should().BeEmpty();
        ProcessRestorePlanner.Build([], Env()).Should().BeEmpty();
    }

    [Fact]
    public void 路径缺失时用进程名补出exe名()
    {
        ProcessRestorePlanner.ResolveExecutableName("foo", null).Should().Be("foo.exe");
        ProcessRestorePlanner.ResolveExecutableName("foo", @"D:\Apps\Foo\foo.exe").Should().Be("foo.exe");
    }
}
