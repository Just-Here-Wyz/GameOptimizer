namespace GameOptimizer.Core.Library;

using System.Text.Json;

/// <summary>
/// 悬浮窗设置与位置的持久化。路径约定与其它 Store 一致：
/// %LocalAppData%\GameOptimizer\overlay.json
///
/// 为什么要记住位置：悬浮窗默认放右上角，但不同游戏的 HUD 布局差别很大
/// （血量条、小地图、任务提示各不一样）。用户拖到不挡视线的地方后，
/// 下次开游戏又被丢回右上角，等于每次都白拖一次。
/// </summary>
public sealed class OverlaySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>默认路径：%LocalAppData%\GameOptimizer\overlay.json</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameOptimizer",
        "overlay.json");

    private readonly string _path;

    public OverlaySettingsStore(string? path = null) => _path = path ?? DefaultPath;

    public string FilePath => _path;

    /// <summary>
    /// 读取设置。文件不存在或损坏时返回默认值，不抛异常——
    /// 悬浮窗位置读不回来只是回到默认位置，不该因此拦住整个程序。
    /// </summary>
    public OverlaySettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new OverlaySettings();

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<OverlaySettings>(json, JsonOptions) ?? new OverlaySettings();
        }
        catch
        {
            return new OverlaySettings();
        }
    }

    /// <summary>保存设置。返回是否成功，失败原因交给调用方决定是否提示。</summary>
    public bool Save(OverlaySettings settings, out string? error)
    {
        error = null;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>悬浮窗设置。</summary>
public sealed class OverlaySettings
{
    /// <summary>是否启用悬浮窗。默认关闭：用户没要求时不要往游戏画面上盖东西。</summary>
    public bool Enabled { get; set; }

    /// <summary>窗口左边距（WPF 设备无关单位）。null 表示用默认位置。</summary>
    public double? Left { get; set; }

    /// <summary>窗口上边距。null 表示用默认位置。</summary>
    public double? Top { get; set; }
}
