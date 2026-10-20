namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// 悬浮窗设置的持久化。
/// 重点：读不到/读损坏时返回默认值而不是抛异常——
/// 悬浮窗位置丢了只是回到默认位置，不该拦住整个程序启动。
/// </summary>
public class OverlaySettingsStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"go-overlay-{Guid.NewGuid():N}.json");

    [Fact]
    public void 文件不存在时应返回默认设置()
    {
        var store = new OverlaySettingsStore(TempPath());

        var s = store.Load();

        s.Enabled.Should().BeFalse("用户没要求时不要往游戏画面上盖东西");
        s.Left.Should().BeNull();
        s.Top.Should().BeNull();
    }

    [Fact]
    public void 保存后应能读回()
    {
        var path = TempPath();
        var store = new OverlaySettingsStore(path);
        try
        {
            store.Save(new OverlaySettings { Enabled = true, Left = 120.5, Top = 60.25 }, out var error)
                .Should().BeTrue();
            error.Should().BeNull();

            var s = store.Load();
            s.Enabled.Should().BeTrue();
            s.Left.Should().Be(120.5);
            s.Top.Should().Be(60.25);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void 文件损坏时应返回默认设置而不是抛异常()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ 这不是合法 JSON");

            var act = () => new OverlaySettingsStore(path).Load();

            act.Should().NotThrow();
            act().Enabled.Should().BeFalse();
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void 保存失败应返回原因而不是静默吞掉()
    {
        // 用「同名目录占住文件路径」构造必然失败
        var dir = Path.Combine(Path.GetTempPath(), $"go-overlay-block-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "overlay.json"));
        var store = new OverlaySettingsStore(Path.Combine(dir, "overlay.json"));
        try
        {
            var ok = store.Save(new OverlaySettings { Enabled = true }, out var error);

            ok.Should().BeFalse();
            error.Should().NotBeNullOrWhiteSpace();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 默认路径应在本地应用数据目录下()
    {
        OverlaySettingsStore.DefaultPath.Should().Contain("GameOptimizer");
        OverlaySettingsStore.DefaultPath.Should().EndWith("overlay.json");
    }

    [Fact]
    public void 只保存位置而不动启用状态时不应丢失启用标记()
    {
        // 拖窗口只改 Left/Top，Enabled 必须保持原值
        var path = TempPath();
        var store = new OverlaySettingsStore(path);
        try
        {
            var s = store.Load();
            s.Enabled = true;
            s.Left = 10;
            s.Top = 20;
            store.Save(s, out _);

            store.Load().Enabled.Should().BeTrue();
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
