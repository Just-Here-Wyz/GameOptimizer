namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// powercfg 输出解析的回归用例。
/// 之前用固定偏移量截取，在中文系统上会把整行当成 GUID，
/// 导致"切换成功却被判定为失败并回滚"——这个 bug 在界面上表现为
/// 电源计划明明切了，程序却报"切换未生效"。
/// </summary>
public class PowerPlanGuidParsingTests
{
    [Theory]
    [InlineData("电源方案 GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (高性能)",
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    [InlineData("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)",
        "381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData("电源方案 GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (平衡)\r\n",
        "381b4222-f694-41f0-9685-ff5bb260df2e")]
    public void 应能从各国语言输出中提取_GUID(string output, string expected)
    {
        PowerPlanAction.ParseGuid(output).Should().Be(expected);
    }

    [Fact]
    public void GUID后带中文括号和空格时不应把尾巴也带上()
    {
        // 这是实测踩到的原始 bug：截取到的是 "8c5e...c635c  (高性能)"，
        // 与纯 GUID 比较永远不相等
        var parsed = PowerPlanAction.ParseGuid("电源方案 GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (高性能)");

        parsed.Should().NotContain("(");
        parsed.Should().NotContain("高性能");
        parsed.Should().HaveLength(36);
    }

    [Fact]
    public void GUID恰好36个字符且符合标准格式()
    {
        var parsed = PowerPlanAction.ParseGuid("GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

        parsed.Should().MatchRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no guid here")]
    [InlineData("电源方案 GUID: (空)")]
    public void 无法解析时应返回_null_而不是乱码字符串(string output)
    {
        PowerPlanAction.ParseGuid(output).Should().BeNull();
    }

    [Fact]
    public void 输出含多个_GUID_时应取第一个()
    {
        const string output = """
            电源方案 GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (高性能)
            电源方案 GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (平衡)
            """;

        PowerPlanAction.ParseGuid(output).Should().Be("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    }

    [Fact]
    public void 真实系统的当前计划应能被解析出来()
    {
        // 集成验证：直接调用 powercfg，确保在本机真实环境下也能解析
        var guid = PowerPlanAction.CurrentPlanGuid();

        guid.Should().NotBeNull();
        guid.Should().HaveLength(36);
        guid.Should().MatchRegex("^[0-9a-fA-F-]{36}$");
    }
}
