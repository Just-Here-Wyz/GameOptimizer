namespace GameOptimizer.Tests;

using FluentAssertions;

/// <summary>
/// PowerShell 脚本编码检查。
///
/// 实测踩到的真实问题：tools/enable-presentmon-etw.ps1 保存为无 BOM 的 UTF-8，
/// 用户用 powershell.exe（5.1）执行时按 GBK 解码，中文变乱码，
/// 进而导致引号配对失败、数组索引表达式被吞，报
///   "MemberExpression：数组索引表达式丢失或无效" / "MissingArrayIndexExpression"。
///
/// 5.1 只有在文件带 UTF-8 BOM 时才会正确识别编码，所以这里把 BOM 变成硬性要求。
/// </summary>
public class Ps1EncodingTests
{
    private static string RepoRoot()
    {
        // 从测试程序集位置往上找到含 .sln 的目录
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }

    private static IReadOnlyList<string> AllPs1Files()
    {
        var root = RepoRoot();
        return Directory.EnumerateFiles(root, "*.ps1", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
    }

    [Fact]
    public void 仓库里应该有PowerShell脚本()
    {
        AllPs1Files().Should().NotBeEmpty();
    }

    [Fact]
    public void 所有ps1文件都必须带UTF8_BOM()
    {
        // Windows PowerShell 5.1 靠 BOM 判断编码，缺失时按 ANSI 代码页读，中文必乱码
        foreach (var file in AllPs1Files())
        {
            var bytes = File.ReadAllBytes(file);
            var hasBom = bytes.Length >= 3
                         && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

            hasBom.Should().BeTrue(
                $"{Path.GetFileName(file)} 缺少 UTF-8 BOM，Windows PowerShell 5.1 会按 GBK 读取导致中文乱码");
        }
    }

    [Fact]
    public void 所有ps1文件都应是合法UTF8()
    {
        var strict = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        foreach (var file in AllPs1Files())
        {
            var bytes = File.ReadAllBytes(file);
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

            var act = () => strict.GetString(bytes, offset, bytes.Length - offset);
            act.Should().NotThrow($"{Path.GetFileName(file)} 不是合法的 UTF-8");
        }
    }

    [Fact]
    public void 脚本中的中文不应是乱码()
    {
        foreach (var file in AllPs1Files())
        {
            var text = File.ReadAllText(file, System.Text.Encoding.UTF8);

            // 典型的 GBK 误解码产物：连续的可打印 Latin-1 字符混在中文语境里
            text.Should().NotContain("\uFFFD", $"{Path.GetFileName(file)} 含替换字符，编码已损坏");

            // 我们的脚本都写中文注释，若一个汉字都没有说明被解码成了别的东西
            if (Path.GetFileName(file).Contains("presentmon", StringComparison.OrdinalIgnoreCase))
            {
                text.Any(c => c >= 0x4E00 && c <= 0x9FFF)
                    .Should().BeTrue($"{Path.GetFileName(file)} 里的中文没有正确解码");
            }
        }
    }

    [Fact]
    public void 授权脚本应包含语法自检逻辑()
    {
        // 若编码仍然出问题，脚本应自己认出乱码并停止，而不是抛一堆费解的 ParserError
        var file = AllPs1Files().FirstOrDefault(f =>
            Path.GetFileName(f).Contains("presentmon", StringComparison.OrdinalIgnoreCase));

        file.Should().NotBeNull();

        var text = File.ReadAllText(file!, System.Text.Encoding.UTF8);
        text.Should().Contain("编码自检");
        text.Should().Contain("Length -ne");
    }
}
