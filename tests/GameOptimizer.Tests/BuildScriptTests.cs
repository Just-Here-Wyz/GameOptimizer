namespace GameOptimizer.Tests;

using FluentAssertions;

/// <summary>
/// 「用户到底在跑哪份程序」的守卫。
///
/// v2.5.1 当天连着踩了两次：代码改完、测试全绿、提交也做了，用户双击桌面图标
/// 依然是旧版本 —— 因为快捷方式指向 <c>bin/Release/net8.0-windows</c>，
/// 而当时编的是 Debug / 编到了 .artifacts。这类错误没有任何编译期报错，
/// 只能靠约定 + 脚本 + 这里的断言挡住。
/// </summary>
public class BuildScriptTests
{
    private static string ScriptPath => Path.Combine(RepoRoot(), "tools", "build-app.ps1");

    private static string ScriptText => File.ReadAllText(ScriptPath);

    [Fact]
    public void 一键构建脚本应存在()
    {
        File.Exists(ScriptPath).Should().BeTrue(
            "没有这个脚本，就只能靠人记住「要编 Release 并且原地输出」，这已经错过两次");
    }

    [Fact]
    public void 默认配置必须是Release()
    {
        // 桌面快捷方式指向 bin\Release\...\GameOptimizer.App.exe；
        // 默认值一旦改成 Debug，脚本本身就成了新的陷阱。
        ScriptText.Should().Contain("[string]$Configuration = 'Release'");
    }

    [Fact]
    public void 输出目录必须是快捷方式指向的那一层()
    {
        ScriptText.Should().Contain(@"bin\$Configuration\net8.0-windows");
    }

    [Fact]
    public void 不能指定OutputPath()
    {
        // -p:OutputPath 的相对路径按「各项目目录」解析：写 .artifacts/buildcheck 时
        // Core 会落到 src/GameOptimizer.Core/.artifacts/ 下，仓库根那份永远是旧的，
        // 拿它验证会得到「改完没生效」的假象。
        // 注释里提到 OutputPath 是在解释为什么不用它，所以只断言没有真的传这个参数
        ScriptText.Should().NotContain("-p:OutputPath");
    }

    [Fact]
    public void 编译前必须检查实例是否在运行()
    {
        // 程序在跑时输出目录被锁（MSB3027），不先拦下来只会看到一屏重试日志
        ScriptText.Should().Contain("Get-Process -Name 'GameOptimizer*'");
        ScriptText.Should().Contain("退出程序");
    }

    [Fact]
    public void 编译失败必须抛错而不是静默继续()
    {
        ScriptText.Should().Contain("$LASTEXITCODE");
    }

    [Fact]
    public void 项目约定里必须写明用户跑的是Release版()
    {
        // 新接手的人（或 AI）只有读到这里，才知道 dotnet build 的默认 Debug 不算交付
        var agents = File.ReadAllText(Path.Combine(RepoRoot(), "AGENTS.md"));

        agents.Should().Contain("Release");
        agents.Should().Contain("tools/build-app.ps1");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }
}
