namespace GameOptimizer.Tests;

using GameOptimizer.Core.GameMode;

/// <summary>
/// 恢复相关用例的共用数据与工具。
///
/// 全部用「合成记录」，不结束、不启动任何真实进程：
/// 判定逻辑本身没有理由依赖本机装了什么软件，依赖了就说明逻辑写错了地方。
/// </summary>
internal static class RestoreTestData
{
    /// <summary>一条可恢复的候选。</summary>
    public static ProcessRestoreCandidate Eligible(string name)
        => new(
            new RestoredProcess(name, $@"D:\Apps\{name}\{name}.exe"),
            name,
            name,
            name + ".exe",
            Publisher: null,
            Description: null,
            LaunchPath: $@"D:\Apps\{name}\{name}.exe",
            SkipKind: RestoreSkipKind.None,
            Reason: ProcessRestorePlanner.RestorableReason);

    /// <summary>一条被跳过的候选（文件已不存在）。</summary>
    public static ProcessRestoreCandidate Skipped(string name)
        => new(
            new RestoredProcess(name, $@"D:\Gone\{name}.exe"),
            name,
            name,
            name + ".exe",
            Publisher: null,
            Description: null,
            LaunchPath: $@"D:\Gone\{name}.exe",
            SkipKind: RestoreSkipKind.MissingFile,
            Reason: "可执行文件已不存在（可能已卸载或移动）");
}

/// <summary>
/// 把恢复日志收进内存，避免单元测试往用户真实的 %LocalAppData% 写文件。
/// </summary>
internal sealed class RestoreLogScope : IDisposable
{
    private readonly Action<string> _previous;
    private readonly List<string> _lines = [];

    public RestoreLogScope()
    {
        _previous = ProcessRestoreLog.Sink;
        ProcessRestoreLog.Sink = line => { lock (_lines) _lines.Add(line); };
    }

    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) return [.. _lines]; }
    }

    public void Dispose() => ProcessRestoreLog.Sink = _previous;
}
