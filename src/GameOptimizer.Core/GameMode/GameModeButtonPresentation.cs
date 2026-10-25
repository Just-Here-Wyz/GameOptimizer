namespace GameOptimizer.Core.GameMode;

/// <summary>
/// 游戏模式按钮所处的阶段。界面据此决定文案、底色与状态行颜色。
///
/// 为什么单独建枚举：开启要结束后台进程、切电源计划，实测耗时以秒计。
/// 这期间按钮必须有可见的「正在处理」状态，否则用户会以为没点上而反复点击。
/// </summary>
public enum GameModePhase
{
    /// <summary>未开启，等待用户点击。</summary>
    Idle = 0,

    /// <summary>正在开启或关闭，按钮禁用并显示进行中文案。</summary>
    Working = 1,

    /// <summary>已开启。</summary>
    On = 2,

    /// <summary>失败（开启失败已回滚 / 关闭时有项未还原）。</summary>
    Failed = 3,
}

/// <summary>
/// 游戏模式按钮的呈现规则：文案、底色、可点击性、状态行颜色。
///
/// 抽成纯函数放在 Core 的两个理由：
/// 1. 可单测——「处理中不能重复点击」这类规则不该只存在于 XAML 里；
/// 2. 颜色不写死在 XAML 触发器里，与内存优化按钮保持同一套口径（见 MainViewModel）。
/// </summary>
public static class GameModeButtonPresentation
{
    /// <summary>主色（未开启时的蓝色）。</summary>
    public const string AccentHex = "#FF4C8DFF";

    /// <summary>已开启时的红色：提示再点一下会关闭并恢复进程。</summary>
    public const string DangerHex = "#FFFF453A";

    /// <summary>处理中的压暗色。</summary>
    public const string BusyHex = "#FF2A2C33";

    /// <summary>空闲状态行灰色。</summary>
    public const string MutedHex = "#FF5C5F66";

    /// <summary>成功状态行绿色。</summary>
    public const string OkHex = "#FF34C759";

    /// <summary>失败状态行红色。</summary>
    public const string ErrorHex = "#FFFF453A";

    /// <summary>
    /// 按钮文案。处理中优先于开关状态——正在开启时不该显示「关闭游戏模式」，
    /// 否则用户会以为自己点的方向反了。
    /// </summary>
    public static string ButtonText(bool busy, bool on, bool entering) => busy
        ? (entering ? "开启中…" : "关闭中…")
        : (on ? "关闭游戏模式" : "开启游戏模式");

    /// <summary>按钮底色：处理中压暗，已开启转红，未开启用主色。</summary>
    public static string ButtonBackgroundHex(bool busy, bool on) => busy
        ? BusyHex
        : (on ? DangerHex : AccentHex);

    /// <summary>处理中不允许再次点击，避免连点叠加出「开了又关」。</summary>
    public static bool CanToggle(bool busy) => !busy;

    /// <summary>状态行文字颜色：进行中/成功/失败/空闲四态。</summary>
    public static string StatusHex(GameModePhase phase) => phase switch
    {
        GameModePhase.Working => AccentHex,
        GameModePhase.On => OkHex,
        GameModePhase.Failed => ErrorHex,
        _ => MutedHex,
    };

    /// <summary>处理中的状态行文案，必须说清在等什么，而不是一句「请稍候」。</summary>
    public static string WorkingStatus(bool entering) => entering
        ? "正在开启游戏模式…（切换电源计划并结束后台进程，可能需要几秒）"
        : "正在关闭游戏模式…（正在恢复被结束的进程）";
}
