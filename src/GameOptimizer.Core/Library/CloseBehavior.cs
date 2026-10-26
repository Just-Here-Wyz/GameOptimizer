namespace GameOptimizer.Core.Library;

/// <summary>
/// 点主窗口右上角 × 时的处理方式。
///
/// <b>Ask 是默认值，也是所有失败路径的落点</b>：配置缺失、写坏、读到不认识的值，
/// 一律退到「每次问一次」，绝不自作主张替用户选「退出」或「缩到托盘」。
/// 前者会让用户以为程序崩了，后者会让用户以为关不掉。
/// </summary>
public enum CloseBehavior
{
    /// <summary>每次点 × 都弹窗询问。</summary>
    Ask = 0,

    /// <summary>隐藏主窗口，程序继续在托盘后台运行。</summary>
    MinimizeToTray = 1,

    /// <summary>按正常退出流程结束程序（还原游戏模式后退出）。</summary>
    Exit = 2,
}

/// <summary>
/// 一次关闭请求的处理结论。由 <see cref="CloseBehaviorService"/> 算出，界面照着做即可。
///
/// <see cref="None"/> 与 <see cref="Ignore"/> 的区别只在语义：
/// 前者是「已经有结论了，结论是什么都不做」（用户取消），
/// 后者是「这个请求根本不该被处理」（弹窗已经开着，第二次点 × 不该叠第二个弹窗）。
/// 两者都必须保持主窗口可见。
/// </summary>
public enum CloseRoute
{
    /// <summary>什么都不做：主窗口保持显示，采样继续。</summary>
    None = 0,

    /// <summary>需要弹窗询问。</summary>
    Ask = 1,

    /// <summary>隐藏主窗口到托盘，后台功能继续跑。</summary>
    HideToTray = 2,

    /// <summary>走正常退出流程。</summary>
    Exit = 3,

    /// <summary>忽略这次请求（正在处理另一次关闭请求）。</summary>
    Ignore = 4,
}

/// <summary>
/// 用户在弹窗里做出的选择：选了哪种行为 + 是否记住。
///
/// <b>「记住」是独立的一件事</b>：没勾选时本次照做，但不改长期默认值；
/// 勾选了才写配置。这两件事混在一起是这类弹窗最常见的 bug——
/// 用户只想「这次退出」，结果下次点 × 直接退出，连问都不问了。
/// </summary>
public readonly record struct CloseBehaviorChoice(CloseBehavior Behavior, bool Remember)
{
    /// <summary>
    /// 弹窗只可能给出「缩小到托盘」或「退出」。
    /// Ask 是「还没决定」，不是一个可以执行的动作，因此不算有效选择。
    /// </summary>
    public bool IsValid => Behavior is CloseBehavior.MinimizeToTray or CloseBehavior.Exit;
}

/// <summary>
/// 关闭行为的口径（纯函数，可脱离界面单测）。
///
/// 口径与实现分离是这个项目的既有约定：改实现不能顺手改口径，
/// 所以「下次该做什么」「要不要写盘」这两条判定都固定在这里。
/// </summary>
public static class CloseBehaviorPolicy
{
    /// <summary>已保存的行为 → 这次点 × 该做什么。<see cref="CloseBehavior.Ask"/>（含任何未知值）一律返回询问。</summary>
    public static CloseRoute Route(CloseBehavior saved) => saved switch
    {
        CloseBehavior.MinimizeToTray => CloseRoute.HideToTray,
        CloseBehavior.Exit => CloseRoute.Exit,

        // Ask 与「认不出来的值」都走这里：宁可多问一次，也不能替用户做破坏性决定
        _ => CloseRoute.Ask,
    };

    /// <summary>
    /// 这次选择要不要写进配置。返回 null 表示<b>不写</b>——没勾「记住」或选择本身无效。
    /// 调用方拿到 null 时必须保持原配置不动，不能顺手清成 Ask。
    /// </summary>
    public static CloseBehavior? PersistTarget(CloseBehaviorChoice choice)
        => choice is { Remember: true } && choice.IsValid ? choice.Behavior : null;

    /// <summary>界面上给用户看的说法，弹窗与设置页共用一份，避免两处文案各说各的。</summary>
    public static string Describe(CloseBehavior behavior) => behavior switch
    {
        CloseBehavior.MinimizeToTray => "缩小到系统托盘",
        CloseBehavior.Exit => "退出程序",
        _ => "每次询问",
    };

    /// <summary>一句话解释这个行为的后果，用在设置页与弹窗副标题。</summary>
    public static string Explain(CloseBehavior behavior) => behavior switch
    {
        CloseBehavior.MinimizeToTray => "隐藏主窗口，程序继续在后台运行，可随时从托盘打开",
        CloseBehavior.Exit => "结束程序，退出前会先还原游戏模式做过的改动",
        _ => "每次点关闭都先问一次，由你当场决定",
    };
}
