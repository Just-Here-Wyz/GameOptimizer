namespace GameOptimizer.Core.GameMode;

/// <summary>
/// 恢复流程的详细日志。主界面只显示一句摘要，异常类型 / 错误码 / 目标来源都在这里。
///
/// <b>写日志必须自己兜住</b>：磁盘满、目录无权限、文件被占用都可能失败，
/// 但那时进程恢复本身已经完成了——绝不能因为写不进日志就让安全流程半途抛出。
/// 所以对外只有一个永不抛出的 <see cref="Write"/>，内部失败一律吞掉（只丢日志，不丢结论）。
/// </summary>
public static class ProcessRestoreLog
{
    /// <summary>超过这个长度就裁掉前面的旧记录，避免日志无限增长。</summary>
    public const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    /// <summary>默认日志路径：%LocalAppData%\GameOptimizer\restore-log.txt</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameOptimizer",
        "restore-log.txt");

    /// <summary>实际写入的文件路径。</summary>
    public static string FilePath { get; set; } = DefaultPath;

    /// <summary>
    /// 写日志的落点。默认写文件；测试可替换成内存收集器，
    /// 这样断言日志内容时不必碰用户磁盘。
    /// </summary>
    public static Action<string> Sink { get; set; } = WriteToFile;

    /// <summary>追加一行（自动带时间戳）。任何写入失败都不会向外抛。</summary>
    public static void Write(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        try
        {
            Sink($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}");
        }
        catch
        {
            // 日志失败不影响恢复流程：这是本类唯一的对外承诺
        }
    }

    private static void WriteToFile(string line)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        lock (Gate)
        {
            // 先裁剪再追加：单次读取 64KB 够判断上下文，不需要整份读进内存
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
            {
                var text = File.ReadAllText(FilePath);
                var keep = (int)(MaxBytes / 4);
                File.WriteAllText(FilePath, text.Length > keep ? text[^keep..] : text);
            }

            File.AppendAllText(FilePath, line + Environment.NewLine);
        }
    }
}

/// <summary>一次启动尝试的结果。失败时带上异常类型与错误码，便于区分「拒绝访问」和「文件不存在」。</summary>
public sealed record ProcessLaunchResult(
    bool Started,
    string? Error = null,
    string? ErrorType = null,
    int? ErrorCode = null)
{
    public static ProcessLaunchResult Ok { get; } = new(true);

    public static ProcessLaunchResult Fail(string error, string? errorType = null, int? errorCode = null)
        => new(false, error, errorType, errorCode);
}

/// <summary>单项恢复的结果分类。</summary>
public enum ProcessRestoreStatus
{
    /// <summary>启动请求已提交，且确认进程真的在运行。</summary>
    Restored = 0,

    /// <summary>启动请求已提交，但等到超时也没观察到进程——<b>不能算成功</b>。</summary>
    Unconfirmed = 1,

    /// <summary>启动失败（含异常与错误码）。</summary>
    Failed = 2,

    /// <summary>按资格判定跳过，根本没有尝试启动。</summary>
    Skipped = 3,
}

/// <summary>单项恢复结果。</summary>
public sealed record ProcessRestoreOutcome(
    string ProcessName,
    string? LaunchPath,
    ProcessRestoreStatus Status,
    string? Error = null,
    string? ErrorType = null,
    int? ErrorCode = null)
{
    public bool IsSuccess => Status == ProcessRestoreStatus.Restored;
}

/// <summary>恢复进度（第几项 / 共几项 / 正在处理谁），供界面显示「正在恢复 3/5」。</summary>
public sealed record ProcessRestoreProgress(int Index, int Total, string ProcessName);

/// <summary>
/// 一批恢复的汇总。摘要文案只在这里定义一次，弹窗与首页状态行共用，
/// 避免两处措辞不一致导致「弹窗说成功 3 项、首页说 2 项」。
/// </summary>
public sealed record ProcessRestoreReport(IReadOnlyList<ProcessRestoreOutcome> Outcomes)
{
    public static ProcessRestoreReport Empty { get; } = new([]);

    public int Total => Outcomes.Count;

    public int RestoredCount => Outcomes.Count(o => o.Status == ProcessRestoreStatus.Restored);

    /// <summary>失败数含「已提交但未确认运行」——没观察到进程就不许算成功。</summary>
    public int FailedCount => Outcomes.Count(o => o.Status is ProcessRestoreStatus.Failed or ProcessRestoreStatus.Unconfirmed);

    public int SkippedCount => Outcomes.Count(o => o.Status == ProcessRestoreStatus.Skipped);

    /// <summary>形如「已恢复 5 项，跳过 2 项，失败 1 项。」</summary>
    public string Summary => $"已恢复 {RestoredCount} 项，跳过 {SkippedCount} 项，失败 {FailedCount} 项。";

    /// <summary>
    /// 按「整份列表」口径生成摘要：没勾选的项与不可恢复项都算「跳过」。
    ///
    /// 为什么需要两个口径：这批只跑了 5 项时，<see cref="Summary"/> 会说「跳过 0 项」，
    /// 而用户看到的是 7 行里有 2 行没恢复——两个数字对不上就会怀疑统计在骗人。
    /// 弹窗与首页统一用这个口径。
    /// </summary>
    public string SummaryAgainst(int totalCandidates)
    {
        var skipped = Math.Max(0, totalCandidates - RestoredCount - FailedCount);
        return $"已恢复 {RestoredCount} 项，跳过 {skipped} 项，失败 {FailedCount} 项。";
    }

    /// <summary>逐项明细（弹窗「详细日志」用），每行一条，不带完整堆栈。</summary>
    public IReadOnlyList<string> DetailLines =>
    [
        .. Outcomes.Select(o => o.Status switch
        {
            ProcessRestoreStatus.Restored => $"[成功] {o.ProcessName}",
            ProcessRestoreStatus.Unconfirmed => $"[未确认] {o.ProcessName} · {o.Error}",
            ProcessRestoreStatus.Skipped => $"[跳过] {o.ProcessName} · {o.Error}",
            _ => $"[失败] {o.ProcessName} · {DescribeError(o)}",
        })
    ];

    /// <summary>把异常类型与错误码拼进一行：只写「拒绝访问」等于没写，因为根因可能完全不同。</summary>
    internal static string DescribeError(ProcessRestoreOutcome outcome)
    {
        var text = outcome.Error;
        if (string.IsNullOrWhiteSpace(text)) text = "未知错误";

        if (string.IsNullOrWhiteSpace(outcome.ErrorType)) return text;

        var code = outcome.ErrorCode is { } value
            ? $"(0x{value:X})"
            : "";
        return $"{outcome.ErrorType}{code} · {text}";
    }
}

/// <summary>
/// 顺序恢复队列。
///
/// 为什么不是一次性 Process.Start 循环（v2.4.0 之前就是这样）：
/// 一次性拉起十几个应用会同时抢磁盘与 CPU，界面卡死、启动互相拖慢，
/// 单个应用抛异常还会把后面本该恢复的目标一起带走。所以改成
/// <b>逐项启动 + 项间留间隔 + 单项异常隔离 + 完成后回读确认</b>。
///
/// 三个必守规则：
/// <list type="number">
/// <item>只有「可恢复且被选中」的项会被启动，判据在 <see cref="ProcessRestorePlanner"/>；</item>
/// <item>「启动请求提交成功」不等于「已恢复」——启动后要观察到进程才计成功；</item>
/// <item>同一时刻只允许一批恢复在执行，重复触发直接拒绝（返回 null）。</item>
/// </list>
/// </summary>
public sealed class ProcessRestoreRunner
{
    /// <summary>默认项间隔：给上一个应用留出初始化时间，也避免同时抢磁盘。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>启动后确认窗口：足够观察到进程出现，又不至于让整批恢复变慢。</summary>
    public static readonly TimeSpan DefaultVerifyTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan VerifyPollInterval = TimeSpan.FromMilliseconds(150);

    private readonly Func<string, ProcessLaunchResult> _startProcess;
    private readonly Func<string, bool> _isProcessRunning;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan, CancellationToken, Task> _verifyDelay;
    private int _running;

    /// <param name="startProcess">可注入的启动服务：返回成功 / 失败（含错误类型与错误码）。</param>
    /// <param name="isProcessRunning">启动后用来确认「真的起来了」的判据。</param>
    /// <param name="interval">项间隔，默认 400ms。</param>
    /// <param name="verifyTimeout">启动确认超时，默认 1.5s；设为 0 表示不做确认。</param>
    /// <param name="delay">等待实现（测试注入记录器，避免真的 sleep）。</param>
    /// <param name="verifyDelay">确认轮询的等待实现。</param>
    public ProcessRestoreRunner(
        Func<string, ProcessLaunchResult>? startProcess = null,
        Func<string, bool>? isProcessRunning = null,
        TimeSpan? interval = null,
        TimeSpan? verifyTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan, CancellationToken, Task>? verifyDelay = null)
    {
        _startProcess = startProcess ?? DefaultStartProcess;
        _isProcessRunning = isProcessRunning ?? DefaultIsProcessRunning;
        Interval = interval ?? DefaultInterval;
        VerifyTimeout = verifyTimeout ?? DefaultVerifyTimeout;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        _verifyDelay = verifyDelay ?? ((span, ct) => Task.Delay(span, ct));
    }

    /// <summary>项间隔，界面可以据此提示「逐项启动，约每 0.4 秒一个」。</summary>
    public TimeSpan Interval { get; }

    /// <summary>启动确认超时。</summary>
    public TimeSpan VerifyTimeout { get; }

    /// <summary>日志里的目标来源，例如「游戏模式关闭」。</summary>
    public string Origin { get; set; } = "游戏模式关闭";

    /// <summary>当前是否有一批恢复在执行。</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// 执行一批恢复。
    /// </summary>
    /// <returns>
    /// 返回 null 表示<b>已有一批在跑，本次未执行</b>（重复触发保护）；
    /// 否则返回逐项结果——空选择也会返回一份空报告，而不是 null。
    /// </returns>
    public async Task<ProcessRestoreReport?> RunAsync(
        IReadOnlyList<ProcessRestoreCandidate>? selection,
        IProgress<ProcessRestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return null;

        try
        {
            return await RunCoreAsync(selection ?? [], progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<ProcessRestoreReport> RunCoreAsync(
        IReadOnlyList<ProcessRestoreCandidate> targets,
        IProgress<ProcessRestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<ProcessRestoreOutcome>(targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = targets[i];
            progress?.Report(new ProcessRestoreProgress(i + 1, targets.Count, candidate.ProcessName));

            outcomes.Add(await RestoreOneAsync(candidate).ConfigureAwait(false));

            // 最后一项之后不必再等：等待只为了错开启动时机
            if (i < targets.Count - 1) await _delay(Interval, cancellationToken).ConfigureAwait(false);
        }

        Log($"本次结束：{new ProcessRestoreReport(outcomes).Summary}");
        return new ProcessRestoreReport(outcomes);
    }

    private async Task<ProcessRestoreOutcome> RestoreOneAsync(ProcessRestoreCandidate candidate)
    {
        // 兜底再判一次资格：调用方即使直接塞进不可恢复项，也不会被启动
        if (!candidate.Eligible || string.IsNullOrWhiteSpace(candidate.LaunchPath))
        {
            var reason = string.IsNullOrWhiteSpace(candidate.Reason) ? "不具备恢复资格" : candidate.Reason;
            Log($"跳过 {candidate.ExecutableName} · {reason}");
            return new ProcessRestoreOutcome(
                candidate.ProcessName, candidate.LaunchPath, ProcessRestoreStatus.Skipped, Error: reason);
        }

        // 同一条记录已经处理过（成功或失败）就不再启动第二次：
        // 这是「重复点击 / 重复提交」的最后一道防线，界面按钮禁用只是第一道
        if (candidate.Record.Launched)
        {
            const string handled = "本批已处理过，不再重复启动";
            Log($"跳过 {candidate.ExecutableName} · {handled}");
            return new ProcessRestoreOutcome(
                candidate.ProcessName, candidate.LaunchPath, ProcessRestoreStatus.Skipped, Error: handled);
        }

        ProcessLaunchResult launch;
        try
        {
            launch = _startProcess(candidate.LaunchPath!);
        }
        catch (Exception ex)
        {
            // 启动服务实现抛异常也只算这一项失败，后面的目标照常处理
            launch = ProcessLaunchResult.Fail(ex.Message, ex.GetType().Name, ex.HResult);
        }

        if (!launch.Started)
        {
            var reason = string.IsNullOrWhiteSpace(launch.Error) ? "启动失败" : launch.Error;
            candidate.Record.MarkFailed(reason);
            Log($"失败 {candidate.ExecutableName} · {launch.ErrorType ?? "启动器"}" +
                $"{(launch.ErrorCode is { } c ? $"(0x{c:X})" : "")} · {reason} · 来源={Origin}");
            return new ProcessRestoreOutcome(
                candidate.ProcessName, candidate.LaunchPath, ProcessRestoreStatus.Failed,
                Error: reason, ErrorType: launch.ErrorType, ErrorCode: launch.ErrorCode);
        }

        // 关键一步：提交成功不等于恢复成功，必须观察到进程才算数
        if (await VerifyRunningAsync(candidate.ProcessName).ConfigureAwait(false))
        {
            candidate.Record.MarkLaunched();
            Log($"成功 {candidate.ExecutableName} · 来源={Origin}");
            return new ProcessRestoreOutcome(
                candidate.ProcessName, candidate.LaunchPath, ProcessRestoreStatus.Restored);
        }

        var unconfirmed = $"{VerifyTimeout.TotalSeconds:0.#} 秒内未检测到该进程运行";
        candidate.Record.MarkFailed(unconfirmed);
        Log($"未确认 {candidate.ExecutableName} · {unconfirmed} · 来源={Origin}");
        return new ProcessRestoreOutcome(
            candidate.ProcessName, candidate.LaunchPath, ProcessRestoreStatus.Unconfirmed,
            Error: unconfirmed, ErrorType: "VerifyTimeout");
    }

    private async Task<bool> VerifyRunningAsync(string processName)
    {
        if (VerifyTimeout <= TimeSpan.Zero) return false;

        var waited = TimeSpan.Zero;
        while (true)
        {
            if (IsRunningNow(processName)) return true;
            if (waited >= VerifyTimeout) return false;

            await _verifyDelay(VerifyPollInterval, CancellationToken.None).ConfigureAwait(false);
            waited += VerifyPollInterval;
        }
    }

    private bool IsRunningNow(string processName)
    {
        try { return _isProcessRunning(processName); }
        catch { return false; }
    }

    private void Log(string line)
    {
        try { ProcessRestoreLog.Write(line); }
        catch
        {
            // 日志实现自身出错也不能中断恢复队列
        }
    }

    /// <summary>
    /// 默认启动方式：ShellExecute 打开 exe，工作目录设成 exe 所在目录
    /// （很多应用按相对路径找自身资源，工作目录错了会启动即崩）。
    /// </summary>
    private static ProcessLaunchResult DefaultStartProcess(string launchPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(launchPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(launchPath) ?? "",
            };

            return System.Diagnostics.Process.Start(psi) is null
                ? ProcessLaunchResult.Fail("启动请求没有返回进程对象", "InvalidOperation")
                : ProcessLaunchResult.Ok;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // NativeErrorCode 是「系统找不到指定的文件」「拒绝访问」这类根因的唯一区分点
            return ProcessLaunchResult.Fail(ex.Message, nameof(System.ComponentModel.Win32Exception), ex.NativeErrorCode);
        }
        catch (Exception ex)
        {
            return ProcessLaunchResult.Fail(ex.Message, ex.GetType().Name, ex.HResult);
        }
    }

    private static bool DefaultIsProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        System.Diagnostics.Process[] processes;
        try { processes = System.Diagnostics.Process.GetProcessesByName(processName); }
        catch { return false; }

        try
        {
            foreach (var p in processes)
            {
                try
                {
                    if (!p.HasExited) return true;
                }
                catch
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }
}
