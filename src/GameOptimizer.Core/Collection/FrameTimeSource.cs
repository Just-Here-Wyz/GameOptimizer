namespace GameOptimizer.Core.Collection;

using System.Diagnostics;
using System.Globalization;

/// <summary>
/// 帧时间来源：调用 Intel PresentMon 控制台程序抓逐帧数据。
///
/// 实测要点（PresentMon 2.6.0）：
/// - 参数是 --process_name / --output_file / --timed，不存在 --terminate_after_timed；
/// - 启动 ETW 会话需要管理员或 Performance Log Users 组成员身份，
///   否则退出码 6 并报 "failed to start trace session: access denied"；
/// - CSV 第一列是 Application，msBetweenPresents 在 v1 指标里存在，
///   v2 指标里改叫 FrameTime，两种都要能读。
/// </summary>
public sealed class FrameTimeSource : IDisposable
{
    private Process? _process;
    private string? _csvPath;
    private long _readOffset;
    private readonly List<string> _header = [];
    private bool _headerParsed;
    private long _csvColumns;
    private int _msBetweenIndex = -1;

    public bool IsRunning => _process is { HasExited: false };

    public string? LastError { get; private set; }

    /// <summary>PresentMon 是否已装且可执行。</summary>
    public static string? DefaultPath(string installRoot = @"E:\dsh-插件\PresentMon")
    {
        if (!Directory.Exists(installRoot)) return null;
        return Directory.EnumerateFiles(installRoot, "PresentMon-*.exe", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f)
            .FirstOrDefault();
    }

    /// <summary>
    /// 启动采集。processName 不含 .exe。
    /// 用 --timed 自动停止，避免进程残留。
    /// </summary>
    public bool Start(string processName, string csvPath, int seconds, string? presentMonPath = null)
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
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
            _csvPath = csvPath;
            _readOffset = 0;
            _headerParsed = false;
            _header.Clear();
            _msBetweenIndex = -1;

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
            psi.ArgumentList.Add("--output_file");
            psi.ArgumentList.Add(csvPath);
            psi.ArgumentList.Add("--timed");
            psi.ArgumentList.Add(seconds.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--no_console_stats");

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Start();
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"启动 PresentMon 失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 增量读取 CSV 中新增的帧时间（毫秒）。每次采样后调用。
    /// 只读新增字节，避免每轮重扫整个文件。
    /// </summary>
    public IReadOnlyList<double> PollNewFrameTimes()
    {
        if (_csvPath is null || !File.Exists(_csvPath)) return [];
        try
        {
            using var fs = new FileStream(_csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= _readOffset) return [];

            fs.Seek(_readOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            _readOffset = fs.Position;

            var results = new List<double>();
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0) continue;

                if (!_headerParsed)
                {
                    _header.AddRange(trimmed.Split(','));
                    _csvColumns = _header.Count;
                    _msBetweenIndex = FindFrameTimeColumn(_header);
                    _headerParsed = true;
                    continue;
                }

                if (_msBetweenIndex < 0) continue;
                var cols = trimmed.Split(',');
                if (cols.Length <= _msBetweenIndex) continue;
                if (double.TryParse(cols[_msBetweenIndex], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var ms) && ms > 0)
                {
                    results.Add(ms);
                }
            }
            return results;
        }
        catch (IOException)
        {
            return []; // 文件正被写入，下轮再读
        }
        catch (Exception ex)
        {
            LastError = $"读取帧时间失败：{ex.Message}";
            return [];
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

    /// <summary>等待采集结束并返回最终错误信息（用于把"没有权限"明确告知用户）。</summary>
    public string? WaitForExit(int timeoutMs)
    {
        if (_process is null) return LastError;
        try
        {
            if (!_process.WaitForExit(timeoutMs))
            {
                LastError ??= "PresentMon 未在预期时间内结束。";
                return LastError;
            }

            var stderr = _process.StandardError.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(stderr)
                && stderr.Contains("access denied", StringComparison.OrdinalIgnoreCase))
            {
                LastError = "PresentMon 无权启动追踪：需要管理员身份，或把当前账号加入 Performance Log Users 组（见 tools/enable-presentmon-etw.ps1）。";
            }
            else if (_process.ExitCode != 0)
            {
                LastError = $"PresentMon 退出码 {_process.ExitCode}：{stderr.Trim()}";
            }
            return LastError;
        }
        catch (Exception ex)
        {
            LastError = $"等待 PresentMon 结束失败：{ex.Message}";
            return LastError;
        }
    }

    public void Dispose()
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
}
