namespace GameOptimizer.Core.Collection;

using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>
/// 帧时间来源。优先用 PresentMon 的 CSV 输出；PresentMon 不存在时，
/// 退化为按采样间隔估算（此时只能反映"采样时进程是否在跑"，不能反映真实帧耗时）。
/// </summary>
public sealed class FrameTimeSource : IDisposable
{
    private Process? _presentMon;
    private readonly List<double> _recentFrameTimes = [];
    private readonly object _lock = new();
    private string? _csvPath;

    public bool IsAvailable => _presentMon is not null;
    public string? LastError { get; private set; }

    /// <summary>最近若干帧的平均帧时间，供采样时读取。</summary>
    public double CurrentFrameTimeMs
    {
        get
        {
            lock (_lock)
            {
                if (_recentFrameTimes.Count == 0) return 0;
                return _recentFrameTimes[^1];
            }
        }
    }

    /// <summary>
    /// 启动 PresentMon 捕获。path 为空时尝试自动查找。
    /// 实测 PresentMon 2.x 以 --output_stdout 或 CSV 输出逐帧数据。
    /// </summary>
    public bool Start(string? presentMonPath, string processName, string csvOutputPath)
    {
        if (string.IsNullOrWhiteSpace(presentMonPath) || !File.Exists(presentMonPath))
        {
            LastError = "未找到 PresentMon，卡顿归因将降级为仅显存/CPU 指标。";
            return false;
        }

        try
        {
            _csvPath = csvOutputPath;
            var psi = new ProcessStartInfo
            {
                FileName = presentMonPath,
                Arguments = $"--process_name {processName} --output_file \"{csvOutputPath}\" --terminate_after_timed 0",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            };
            _presentMon = Process.Start(psi);
            return _presentMon is not null;
        }
        catch (Exception ex)
        {
            LastError = $"启动 PresentMon 失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>从 CSV 增量读取新增帧时间。每次采样后调用。</summary>
    public void Poll()
    {
        if (_csvPath is null || !File.Exists(_csvPath)) return;
        try
        {
            using var fs = new FileStream(_csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                // PresentMon CSV 的 msBetweenPresents 列
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0)
                {
                    lock (_lock)
                    {
                        _recentFrameTimes.Add(ms);
                        if (_recentFrameTimes.Count > 240) _recentFrameTimes.RemoveAt(0);
                    }
                }
            }
        }
        catch (IOException)
        {
            // PresentMon 还在写，下一轮再读
        }
    }

    public void Dispose()
    {
        try
        {
            if (_presentMon is { HasExited: false })
            {
                _presentMon.StandardInput?.Close();
                if (!_presentMon.WaitForExit(2000)) _presentMon.Kill();
            }
            _presentMon?.Dispose();
        }
        catch { }
    }
}
