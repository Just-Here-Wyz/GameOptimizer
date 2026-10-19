namespace GameOptimizer.Core.Collection;

using System.Diagnostics;
using System.Globalization;

/// <summary>
/// 帧时间来源：调用 Intel PresentMon 控制台程序抓逐帧数据。
///
/// 实测要点（PresentMon 2.6.0）：
/// - 参数是 --process_name / --timed / --session_name，不存在 --terminate_after_timed；
/// - 启动 ETW 会话需要管理员或 Performance Log Users 组成员身份，否则退出码 6 + "access denied"；
/// - --output_file 写出的 CSV 是【独占】打开的，采集期间外部连只读都打不开
///   （实测 FileShare.ReadWrite 仍报 in use），因此不能靠轮询文件；
/// - 改用 --output_stdout 流式读取，进程一边跑一边产出 CSV 行。
/// </summary>
public sealed class FrameTimeSource : IDisposable
{
    private Process? _process;
    private readonly List<string> _header = [];
    private readonly List<double> _pending = [];
    private readonly object _lock = new();
    private readonly System.Text.StringBuilder _stderr = new();
    private int _frameTimeIndex = -1;
    private bool _headerParsed;

    public bool IsRunning => _process is { HasExited: false };

    public string? LastError { get; private set; }

    /// <summary>本次采集写入的 CSV 备份路径（便于事后分析）。</summary>
    public string? CsvPath { get; private set; }

    /// <summary>PresentMon 是否已装且可执行。</summary>
    public static string? DefaultPath(string installRoot = @"E:\dsh-插件\PresentMon")
    {
        if (!Directory.Exists(installRoot)) return null;
        return Directory.EnumerateFiles(installRoot, "PresentMon-*.exe", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f)
            .FirstOrDefault();
    }

    /// <summary>
    /// 启动采集。processName 是实际运行的进程名（不含 .exe 也可）。
    /// 用 --timed 自动停止，避免进程残留。
    /// </summary>
    public bool Start(string processName, string? csvBackupPath = null, int seconds = 900, string? presentMonPath = null)
    {
        presentMonPath ??= DefaultPath();
        if (presentMonPath is null || !File.Exists(presentMonPath))
        {
            LastError = "未找到 PresentMon。请把 PresentMon-*-x64.exe 放到 E:\\dsh-插件\\PresentMon\\";
            return false;
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            LastError = "游戏进程名为空，无法采集帧时间。";
            return false;
        }

        try
        {
            lock (_lock)
            {
                _header.Clear();
                _pending.Clear();
                _stderr.Clear();
                _frameTimeIndex = -1;
                _headerParsed = false;
            }
            CsvPath = csvBackupPath;

            var psi = new ProcessStartInfo
            {
                FileName = presentMonPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("--process_name");
            psi.ArgumentList.Add(processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName : processName + ".exe");
            psi.ArgumentList.Add("--output_stdout");
            psi.ArgumentList.Add("--timed");
            psi.ArgumentList.Add(seconds.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--no_console_stats");
            // 避开其他程序已启动的同名会话（实测 --output_file 的残留会话会阻塞新采集）
            psi.ArgumentList.Add("--session_name");
            psi.ArgumentList.Add($"GameOptimizer-{Environment.ProcessId}");

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnStdoutLine;
            _process.ErrorDataReceived += OnStderrLine;

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // 给它一点时间暴露启动期错误（ETW 权限失败会在毫秒级返回）
            if (_process.WaitForExit(StartupGraceMs))
            {
                LastError = DescribeFailure(ReadStderr(), _process.ExitCode);
                Cleanup();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"启动 PresentMon 失败：{ex.Message}";
            Cleanup();
            return false;
        }
    }

    private const int StartupGraceMs = 1500;

    private void OnStdoutLine(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;

        try
        {
            var line = e.Data.TrimEnd('\r');
            lock (_lock)
            {
                if (!_headerParsed)
                {
                    _header.AddRange(line.Split(','));
                    _frameTimeIndex = FindFrameTimeColumn(_header);
                    _headerParsed = true;
                    return;
                }

                if (_frameTimeIndex < 0) return;
                var cols = line.Split(',');
                if (cols.Length <= _frameTimeIndex) return;

                if (double.TryParse(cols[_frameTimeIndex], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var ms) && ms > 0)
                {
                    _pending.Add(ms);
                    // 防止程序卡住时无限堆积
                    if (_pending.Count > 10000) _pending.RemoveRange(0, _pending.Count - 10000);
                }
            }
        }
        catch
        {
            // 单行解析失败不应中断采集
        }
    }

    private void OnStderrLine(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        lock (_lock) { _stderr.AppendLine(e.Data); }
    }

    private string ReadStderr()
    {
        lock (_lock) { return _stderr.ToString(); }
    }

    /// <summary>取走自上次调用以来新到的帧时间（毫秒）。</summary>
    public IReadOnlyList<double> PollNewFrameTimes()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return [];
            var result = _pending.ToArray();
            _pending.Clear();
            return result;
        }
    }

    /// <summary>
    /// 定位帧时间列。v1 指标叫 msBetweenPresents，v2 指标叫 FrameTime，
    /// 不同版本还可能带 FrameTime (ms) 这种带单位的表头。
    /// </summary>
    internal static int FindFrameTimeColumn(IReadOnlyList<string> header)
    {
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Trim();
            if (name.Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase)
                || name.Equals("FrameTime", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("FrameTime", StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>是否已解析出表头（用于判断采集是否真的在产出数据）。</summary>
    public bool HasHeader
    {
        get { lock (_lock) { return _headerParsed && _frameTimeIndex >= 0; } }
    }

    /// <summary>把 PresentMon 的 stderr 翻译成用户能看懂的原因。</summary>
    internal static string DescribeFailure(string stderr, int exitCode)
    {
        if (stderr.Contains("access denied", StringComparison.OrdinalIgnoreCase))
            return "无法启动 ETW 追踪会话（access denied）：需要以管理员身份运行本程序，" +
                   "或把当前账号加入 Performance Log Users 组并重新登录。";

        if (stderr.Contains("already running", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return "已有同名 ETW 追踪会话在运行，请稍后重试（可能是上次采集未正常结束）。";

        if (stderr.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return $"PresentMon 启动失败（退出码 {exitCode}）：{stderr.Trim()}";

        return $"PresentMon 意外退出（退出码 {exitCode}）：{stderr.Trim()}";
    }

    /// <summary>采集过程中是否已异常退出。</summary>
    public bool HasFailed(out string? error)
    {
        error = null;
        if (_process is null) return false;

        try
        {
            if (!_process.HasExited) return false;

            var stderr = ReadStderr();
            if (_process.ExitCode != 0
                || stderr.Contains("access denied", StringComparison.OrdinalIgnoreCase))
            {
                error = DescribeFailure(stderr, _process.ExitCode);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void Cleanup()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
            _process?.Dispose();
        }
        catch { }
        _process = null;
    }

    public void Dispose()
    {
        Cleanup();
    }
}
