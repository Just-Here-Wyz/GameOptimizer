namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Library;

/// <summary>
/// 单实例闸门。
///
/// 必须用独立互斥体名（GUID）跑，否则会和本机正在运行的程序抢同一个名字，
/// 测试结果就取决于「用户此刻有没有开着这个软件」。
///
/// 注意 Mutex 是线程亲和的：同名第二次获取必须换线程才等价于「第二个进程」，
/// 同线程重复 WaitOne 会递归成功，测出来永远是「首实例」。
/// </summary>
public class SingleInstanceGateTests
{
    private static string UniqueName() => $@"Local\GameOptimizer.Test.{Guid.NewGuid():N}";

    [Fact]
    public void 首次获取应成为唯一实例()
    {
        using var gate = SingleInstanceGate.Acquire(UniqueName());
        gate.IsFirstInstance.Should().BeTrue();
    }

    [Fact]
    public async Task 已存在实例时第二次获取应失败()
    {
        var name = UniqueName();
        using var first = SingleInstanceGate.Acquire(name);

        // 换线程 = 等价于「第二个进程」；同线程会因互斥体可重入而误判
        using var second = await Task.Run(() => SingleInstanceGate.Acquire(name));

        second.IsFirstInstance.Should().BeFalse();
    }

    [Fact]
    public void 释放后可以重新成为唯一实例()
    {
        var name = UniqueName();
        var first = SingleInstanceGate.Acquire(name);
        first.IsFirstInstance.Should().BeTrue();

        first.Dispose();

        using var again = SingleInstanceGate.Acquire(name);
        again.IsFirstInstance.Should().BeTrue();
    }

    [Fact]
    public void 不同名字的实例互不影响()
    {
        using var a = SingleInstanceGate.Acquire(UniqueName());
        using var b = SingleInstanceGate.Acquire(UniqueName());

        a.IsFirstInstance.Should().BeTrue();
        b.IsFirstInstance.Should().BeTrue();
    }

    [Fact]
    public void 重复释放不应抛异常()
    {
        var gate = SingleInstanceGate.Acquire(UniqueName());

        gate.Invoking(g => { g.Dispose(); g.Dispose(); }).Should().NotThrow();
    }
}
