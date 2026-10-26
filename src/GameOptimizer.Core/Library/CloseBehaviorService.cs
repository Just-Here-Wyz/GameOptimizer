namespace GameOptimizer.Core.Library;

/// <summary>
/// 「点 × 之后到底该干什么」的唯一判定处，同时负责三件容易被写乱的事：
///
/// <list type="number">
/// <item><b>决策</b>：已保存的行为 → 这次该询问 / 隐藏 / 退出（口径在 <see cref="CloseBehaviorPolicy"/>）；</item>
/// <item><b>重入守卫</b>：弹窗开着时再点一次 × 不能叠出第二个弹窗，也不能把窗口提前藏掉或退出；</item>
/// <item><b>持久化</b>：只在用户勾了「记住」时写盘，取消要原封不动。</item>
/// </list>
///
/// 为什么单独抽一个服务而不是塞在 <c>App.OnWindowClosing</c> 里：
/// 关闭流程牵扯「隐藏 / 退出 / 取消」三态 + 重入 + 落盘，全写在事件处理里就只能靠手点验证；
/// 抽出来以后这些都是纯内存状态，可以脱离桌面单测（见 CloseBehaviorTests）。
///
/// 线程约定：界面主线程调用即可，内部仍加锁——退出路径可能被托盘菜单与窗口关闭同时触发。
/// </summary>
public sealed class CloseBehaviorService
{
    private readonly CloseBehaviorStore _store;
    private readonly object _gate = new();

    private CloseBehavior _saved;
    private bool _promptPending;
    private bool _exiting;

    public CloseBehaviorService(CloseBehaviorStore? store = null)
    {
        _store = store ?? new CloseBehaviorStore();

        // 构造即读盘：读不出来就是 Ask，并把原因留给设置页显示，不打断启动
        var loaded = _store.Load();
        _saved = loaded.Behavior;
        LoadNotice = loaded.Notice;
        LoadError = loaded.Error;
    }

    /// <summary>配置文件位置（设置页显示）。</summary>
    public string FilePath => _store.FilePath;

    /// <summary>当前生效的长期行为。默认 <see cref="CloseBehavior.Ask"/>。</summary>
    public CloseBehavior SavedBehavior
    {
        get { lock (_gate) return _saved; }
    }

    /// <summary>启动读盘时的提示（旧格式 / 认不出的值）；正常为 null。</summary>
    public string? LoadNotice { get; }

    /// <summary>启动读盘失败的原因；正常为 null。失败已自动回退到每次询问。</summary>
    public string? LoadError { get; }

    /// <summary>最近一次写盘失败的原因；成功或从未写过为 null。</summary>
    public string? SaveError { get; private set; }

    /// <summary>是否有一个关闭询问正在等结果（弹窗开着）。</summary>
    public bool PromptPending
    {
        get { lock (_gate) return _promptPending; }
    }

    /// <summary>是否已经进入退出流程（托盘退出，或用户确认过退出）。</summary>
    public bool IsExiting
    {
        get { lock (_gate) return _exiting; }
    }

    /// <summary>
    /// 收到一次「点 ×」的关闭请求。返回这次该怎么处理。
    ///
    /// 三个例外必须先判，顺序不能换：
    /// 已经在退出 → 直接 Exit（不能再问，否则退出流程被自己拦住）；
    /// 弹窗已经开着 → Ignore（重复点 × 不叠窗口）；
    /// 其余才按保存的行为走。
    /// </summary>
    public CloseRoute RequestClose()
    {
        lock (_gate)
        {
            if (_exiting) return CloseRoute.Exit;
            if (_promptPending) return CloseRoute.Ignore;

            var route = CloseBehaviorPolicy.Route(_saved);

            if (route == CloseRoute.Ask) _promptPending = true;
            else if (route == CloseRoute.Exit) _exiting = true;

            return route;
        }
    }

    /// <summary>
    /// 弹窗出结果了。<paramref name="choice"/> 为 null 表示取消（或直接关掉弹窗），
    /// 与点击「取消」完全等价：不改配置、不动窗口、不退出。
    ///
    /// 返回这次该执行的动作；<see cref="CloseRoute.None"/> 表示什么都不做。
    /// </summary>
    public CloseRoute CompletePrompt(CloseBehaviorChoice? choice)
    {
        lock (_gate)
        {
            // 没有待处理的询问：迟到的结果一律不认（例如弹窗开着时从托盘退了程序）
            if (!_promptPending) return CloseRoute.None;
            _promptPending = false;

            if (choice is null) return CloseRoute.None;

            // 弹窗不可能给出 Ask；真给了也不执行——那不是可执行的动作
            if (!choice.Value.IsValid) return CloseRoute.None;

            // 只有勾了「记住」才写盘；没勾时本次照做，原配置一个字都不动
            var persist = CloseBehaviorPolicy.PersistTarget(choice.Value);
            if (persist is not null) SaveCore(persist.Value);

            var route = CloseBehaviorPolicy.Route(choice.Value.Behavior);
            if (route == CloseRoute.Exit) _exiting = true;

            return route;
        }
    }

    /// <summary>
    /// 放弃这次询问（弹窗根本没能打开等异常路径）。
    /// 只清「正在询问」标记，让下次点 × 还能重试；配置与窗口状态都不动。
    /// </summary>
    public void AbandonPrompt()
    {
        lock (_gate) _promptPending = false;
    }

    /// <summary>
    /// 用户从托盘菜单主动退出。标记之后所有关闭请求都直接 Exit，
    /// 不会再弹「你要怎么关闭」——用户已经用行动回答了这个问题。
    /// </summary>
    public void ConfirmExit()
    {
        lock (_gate)
        {
            _exiting = true;
            _promptPending = false;
        }
    }

    /// <summary>
    /// 设置页直接改长期行为（含「重置为每次询问」）。
    /// 写盘失败时内存里的值照旧更新——本次运行按用户刚选的来，
    /// 同时把失败原因交给界面显示，重启后才会回退到磁盘上的旧值。
    /// </summary>
    public bool Save(CloseBehavior behavior, out string? error)
    {
        lock (_gate)
        {
            if (behavior is not (CloseBehavior.Ask or CloseBehavior.MinimizeToTray or CloseBehavior.Exit))
            {
                error = $"无法识别的关闭行为：{behavior}";
                return false;
            }

            return SaveCore(behavior, out error);
        }
    }

    /// <summary>写盘 + 更新内存值。写失败不抛：调用方按返回值/状态显示，本次运行仍按新值走。</summary>
    private bool SaveCore(CloseBehavior behavior) => SaveCore(behavior, out _);

    private bool SaveCore(CloseBehavior behavior, out string? error)
    {
        _saved = behavior;

        if (_store.Save(behavior, out error))
        {
            SaveError = null;
            return true;
        }

        SaveError = error;
        return false;
    }
}
