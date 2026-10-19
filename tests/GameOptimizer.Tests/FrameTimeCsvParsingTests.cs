namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.Collection;

/// <summary>
/// PresentMon CSV 解析的回归用例。
/// 实测记录：v2.6.0 的参数是 --process_name/--output_file/--timed，
/// 并不存在 --terminate_after_timed；且 CSV 列名在 v1/v2 指标间不同。
/// </summary>
public class FrameTimeCsvParsingTests
{
    [Fact]
    public void 应识别_v1_指标的_msBetweenPresents()
    {
        // PresentMon v1 指标的典型表头
        string[] header = ["Application", "ProcessID", "SwapChainAddress", "msBetweenPresents"];
        FrameTimeSource.FindFrameTimeColumn(header).Should().Be(3);
    }

    [Fact]
    public void 应识别_v2_指标的_FrameTime()
    {
        // PresentMon 2.x 默认 v2 指标用 FrameTime
        string[] header = ["Application", "ProcessID", "FrameTime", "CPUBusy"];
        FrameTimeSource.FindFrameTimeColumn(header).Should().Be(2);
    }

    [Fact]
    public void 应容忍带单位或空格的表头()
    {
        string[] header = ["Application", "FrameTime (ms)"];
        FrameTimeSource.FindFrameTimeColumn(header).Should().Be(1);
    }

    [Fact]
    public void 表头列名不应被大小写影响()
    {
        string[] header = ["Application", "msbetweenpresents"];
        FrameTimeSource.FindFrameTimeColumn(header).Should().Be(1);
    }

    [Fact]
    public void 没有帧时间列时应返回负一()
    {
        string[] header = ["Application", "ProcessID", "GPU"];
        FrameTimeSource.FindFrameTimeColumn(header).Should().Be(-1);
    }

    [Fact]
    public void 没有安装PresentMon时应给出可操作的提示()
    {
        var source = new FrameTimeSource();
        try
        {
            var ok = source.Start("game", Path.Combine(Path.GetTempPath(), "x.csv"), 5,
                presentMonPath: @"Z:\not-exist\PresentMon-2.6.0-x64.exe");

            ok.Should().BeFalse();
            source.LastError.Should().Contain("PresentMon");
        }
        finally { source.Dispose(); }
    }

    [Fact]
    public void 进程名为空时应拒绝启动而不是静默无输出()
    {
        var source = new FrameTimeSource();
        try
        {
            var ok = source.Start("", Path.Combine(Path.GetTempPath(), "x.csv"), 5);
            ok.Should().BeFalse();
            source.LastError.Should().Contain("进程名");
        }
        finally { source.Dispose(); }
    }

    [Fact]
    public void 未启动时轮询应返回空而不是抛异常()
    {
        var source = new FrameTimeSource();
        try
        {
            source.PollNewFrameTimes().Should().BeEmpty();
        }
        finally { source.Dispose(); }
    }

    [Fact]
    public void 已安装PresentMon时应能在E盘默认位置找到()
    {
        // 按用户要求安装在 E:\dsh-插件\PresentMon
        var path = FrameTimeSource.DefaultPath();

        // 未安装的机器上允许为 null，但不该抛异常
        if (path is not null)
        {
            File.Exists(path).Should().BeTrue();
            Path.GetFileName(path).Should().StartWith("PresentMon-");
        }
    }
}
