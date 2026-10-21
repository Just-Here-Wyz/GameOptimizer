namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Download;

/// <summary>
/// 从链接提取文件名。用户粘贴的是千奇百怪的直链，
/// 提取错了的表现是「文件下下来叫 12345」，属于一眼就能看见的瑕疵。
/// </summary>
public class DownloadFileNameTests
{
    [Theory]
    [InlineData("https://example.com/files/game.zip", "game.zip")]
    [InlineData("https://example.com/a/b/setup.exe?token=abc", "setup.exe")]
    [InlineData("http://example.com/中文文件.rar", "中文文件.rar")]
    [InlineData("https://example.com/x/%E6%B5%8B%E8%AF%95.7z", "测试.7z")]
    [InlineData("example.com/direct/file.bin", "file.bin")]
    public void 常见直链应能提取出文件名(string url, string expected)
    {
        DownloadFileName.FromUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com/download?filename=mod.jar", "mod.jar")]
    [InlineData("https://example.com/get?name=patch%201.zip", "patch 1.zip")]
    [InlineData("https://example.com/download/12345?file=real.apk", "real.apk")]
    public void 查询串里的显式文件名优先于数字路径(string url, string expected)
    {
        DownloadFileName.FromUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/download/12345")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    public void 提取不到时返回空串而不是编一个名字(string url)
    {
        // 编一个 download.bin 会盖掉服务端 Content-Disposition 给的正确名字
        DownloadFileName.FromUrl(url).Should().BeEmpty();
    }

    [Theory]
    [InlineData("a:b.zip", "a_b.zip")]
    [InlineData("a*b?c.zip", "a_b_c.zip")]
    [InlineData("trailing.dot.", "trailing.dot")]
    [InlineData("  spaced  .zip  ", "spaced  .zip")]
    public void 非法字符应替换为下划线(string raw, string expected)
    {
        DownloadFileName.Sanitize(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL.bin")]
    [InlineData("COM1.zip")]
    [InlineData("LPT9.rar")]
    public void Windows保留设备名必须改名(string raw)
    {
        // 直接落盘会抛异常；这类名字出现在链接里并不罕见（例如 con.txt）
        var result = DownloadFileName.Sanitize(raw);
        result.Should().NotBe(raw);
        result.Should().NotBe(Path.GetFileNameWithoutExtension(raw) + Path.GetExtension(raw),
            "保留设备名必须加后缀才能安全落盘");
    }

    [Fact]
    public void 普通名字不应被改动()
    {
        DownloadFileName.Sanitize("3dmigoto-v1.2.zip").Should().Be("3dmigoto-v1.2.zip");
    }

    [Fact]
    public void 重名时应追加序号()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.zip", "a (1).zip" };

        var name = DownloadFileName.MakeUnique(@"C:\x", "a.zip", p => existing.Contains(Path.GetFileName(p)));

        name.Should().Be("a (2).zip");
    }

    [Fact]
    public void 不重名时保持原名()
    {
        DownloadFileName.MakeUnique(@"C:\x", "b.zip", _ => false).Should().Be("b.zip");
    }

    [Fact]
    public void 空名字应给出兜底名()
    {
        DownloadFileName.MakeUnique(@"C:\x", "", _ => false).Should().Be("download");
    }
}
