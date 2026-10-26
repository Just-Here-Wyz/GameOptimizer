namespace GameOptimizer.Tests;

using FluentAssertions;

/// <summary>
/// 安装脚本守卫。
///
/// 安装器是三处最容易「悄悄做错」的地方：版本号被写死（下次发版忘了改）、
/// 默认全程要管理员（用户只装个程序却过一遍 UAC）、卸载顺手把用户数据删了。
/// 这几条不是风格问题，是把约定钉成测试。
/// </summary>
public class InstallerScriptTests
{
    private static string IssPath => Path.Combine(RepoRoot(), "installer", "GameOptimizer.iss");

    private static string IssText => File.ReadAllText(IssPath);

    [Fact]
    public void 安装脚本应存在()
    {
        File.Exists(IssPath).Should().BeTrue("打包入口是 installer/GameOptimizer.iss");
    }

    [Fact]
    public void 安装脚本不得硬编码版本号()
    {
        var text = IssText;

        // 版本号唯一来源是 Directory.Build.props，由 build-installer.ps1 用 /D 传进来
        text.Should().Contain("AppVersion={#MyAppVersion}");
        text.Should().NotMatchRegex(@"\d+\.\d+\.\d+",
            "脚本里出现形如 2.4.0 的字面量，意味着下次发版会漏改");
    }

    [Fact]
    public void 版本号应从项目配置读取()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        props.Should().Contain("<Version>");

        var script = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "build-installer.ps1"));
        script.Should().Contain("Directory.Build.props", "构建脚本必须从项目配置读版本号");
        script.Should().NotMatchRegex(@"\$version\s*=\s*'[\d.]+'");
    }

    [Fact]
    public void 安装目录应与用户数据目录区分()
    {
        var text = IssText;

        // 程序进程序目录，数据留在 %LocalAppData%\GameOptimizer，两边不许混
        text.Should().Contain("DefaultDirName={autopf}");
        text.Should().NotContain("{app}\\GameOptimizer\\background-whitelist.json");
        text.Should().NotContain("DestDir: \"{localappdata}");
    }

    [Fact]
    public void 安装器不得删除用户数据()
    {
        // 只要没有 [UninstallDelete]、也没有 DeleteFile/DelTree，
        // 卸载就只会删自己装进去的文件与快捷方式；
        // 用户的白名单、下载任务、游戏模式快照一律留在原地。
        var text = IssText;

        text.Should().NotContain("[UninstallDelete]");
        text.Should().NotContain("DeleteFile(");
        text.Should().NotContain("DelTree(");
        text.Should().NotContain("RemoveDir(");
    }

    [Fact]
    public void 应用数据目录只出现在卸载提示里()
    {
        var text = IssText;
        const string dataDir = @"ExpandConstant('{localappdata}\GameOptimizer')";

        text.Should().Contain(dataDir, "卸载后要如实告诉用户数据还在哪");
        text.Should().NotContain(@"DeleteFile(ExpandConstant('{localappdata}");
        text.Should().NotContain(@"DelTree(ExpandConstant('{localappdata}");
    }

    [Fact]
    public void 安装向导不应全程要求管理员()
    {
        var text = IssText;

        // 默认「仅为我安装」→ 向导不提权，只有应用自己启动时才提权；
        // 想装到 Program Files 的用户可以在第一页改选「所有用户」。
        text.Should().Contain("PrivilegesRequired=lowest");
        text.Should().Contain("PrivilegesRequiredOverridesAllowed=dialog");
        text.Should().NotContain("PrivilegesRequired=admin");
    }

    [Fact]
    public void 应识别已运行的应用而不是强杀进程()
    {
        var text = IssText;

        // 与应用里的单实例互斥体同名（改名字要两边一起改）
        text.Should().Contain(GameOptimizer.Core.Library.SingleInstanceGate.DefaultMutexName);
        text.Should().Contain("CheckForMutexes");
        text.Should().Contain("CloseApplications=no", "不得自动结束用户进程");
        text.Should().NotContain("taskkill", "安装器不负责关程序");
        text.Should().NotContain("TerminateProcess");
    }

    [Fact]
    public void 应使用项目现有图标()
    {
        IssText.Should().Contain(@"src\GameOptimizer.App\app.ico");
        IssText.Should().Contain("SetupIconFile=");
        IssText.Should().Contain("UninstallDisplayIcon=");
    }

    [Fact]
    public void 快捷方式应可选且安装后可启动()
    {
        var text = IssText;

        text.Should().Contain("Name: \"desktopicon\"");
        text.Should().Contain("Name: \"startmenuicon\"");
        text.Should().Contain("postinstall", "完成页的「立即启动」是可选项");
    }

    [Fact]
    public void 只应打包发布产物()
    {
        var text = IssText;

        text.Should().Contain("Source: \"{#StageDirAbs}*\"");
        text.Should().Contain("ArchitecturesAllowed=x64compatible");

        // 安装包里不许出现源码、测试工程、开发缓存
        text.Should().NotContain("GameOptimizer.Core");
        text.Should().NotContain("GameOptimizer.Tests");
        text.Should().NotContain("*.cs");
    }

    [Fact]
    public void 脚本不得包含开发者机器的绝对路径()
    {
        var text = IssText;

        text.Should().NotMatchRegex(@"(?<![A-Za-z])[A-Za-z]:\\",
            "路径必须相对脚本解析，否则换台机器就编不过");
        text.Should().NotContain("Users\\");
        text.Should().NotContain("%TEMP%");
    }

    [Fact]
    public void 构建脚本应为UTF8带BOM()
    {
        // Windows PowerShell 5.1 按 GBK 读没有 BOM 的脚本会直接在解析阶段报错（docs/00 硬约定 10）
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), "installer", "build-installer.ps1"));

        bytes.Length.Should().BeGreaterThan(3);
        bytes[0].Should().Be(0xEF);
        bytes[1].Should().Be(0xBB);
        bytes[2].Should().Be(0xBF);
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
