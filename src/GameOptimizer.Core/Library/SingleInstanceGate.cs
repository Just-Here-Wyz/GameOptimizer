namespace GameOptimizer.Core.Library;

using System.Runtime.InteropServices;

/// <summary>
/// 单实例闸门 + 唤醒已有实例。
///
/// 为什么需要：本程序会改电源计划、结束进程、常驻托盘。
/// 同时跑两个实例时，第二个实例完全读不到第一个的状态——
/// 它会以为「游戏模式未开启」，重复执行一遍开启流程；
/// 而退出时的还原又各写各的，电源计划与进程恢复都会错乱。
///
/// 实现是「命名互斥体 + 广播窗口消息」：
/// · 互斥体判断是否已有实例（`Local\` 会话级：同一登录会话内唯一）；
/// · 已有实例就广播一条自定义窗口消息，让对方把窗口从托盘里叫出来，
///   然后把本次启动直接退掉——用户看到的是「双击图标，窗口弹到前面」。
/// </summary>
public sealed class SingleInstanceGate : IDisposable
{
    /// <summary>互斥体名。`Local\` 表示当前登录会话内唯一，多用户各自可以开一个。</summary>
    public const string DefaultMutexName = @"Local\GameOptimizer.SingleInstance.v1";

    /// <summary>唤醒消息名。两个进程用同一个字符串注册，会拿到同一个消息 ID。</summary>
    public const string ActivationMessageName = "GameOptimizer.Activate.v1";

    private const int HwndBroadcast = 0xffff;

    private Mutex? _mutex;

    private SingleInstanceGate(Mutex? mutex, bool isFirstInstance)
    {
        _mutex = mutex;
        IsFirstInstance = isFirstInstance;
    }

    /// <summary>是否抢到了第一个实例的位置。为 false 时应唤醒已有实例并退出本次启动。</summary>
    public bool IsFirstInstance { get; }

    /// <summary>
    /// 尝试成为唯一实例。拿不到互斥体（极端权限问题）时按「首个实例」放行，
    /// 宁可多开一次，也不该让用户双击没反应。
    /// </summary>
    public static SingleInstanceGate Acquire(string? mutexName = null)
    {
        var name = mutexName ?? DefaultMutexName;

        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
        }
        catch (UnauthorizedAccessException)
        {
            return new SingleInstanceGate(null, true);
        }

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例崩溃/被强杀，互斥体被遗弃：这次我们合法接管
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return new SingleInstanceGate(null, false);
        }

        return new SingleInstanceGate(mutex, true);
    }

    /// <summary>广播唤醒消息给已在运行的实例。没有实例在跑时什么也不会发生。</summary>
    public static void RequestActivation()
    {
        try
        {
            var message = ActivationMessageId;
            if (message == 0) return;
            PostMessage(new IntPtr(HwndBroadcast), (uint)message, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 唤醒失败只影响「窗口有没有自动弹到前面」，不该让启动流程报错
        }
    }

    /// <summary>收到的窗口消息是不是唤醒消息。窗口过程里用它过滤。</summary>
    public static bool IsActivationMessage(int message)
        => message != 0 && message == ActivationMessageId;

    private static int _activationMessageId;

    /// <summary>自定义消息 ID。同会话内同一个字符串永远返回同一个值，缓存一次即可。</summary>
    private static int ActivationMessageId
    {
        get
        {
            if (_activationMessageId == 0)
            {
                try { _activationMessageId = (int)RegisterWindowMessage(ActivationMessageName); }
                catch { /* 注册失败就当没有唤醒能力 */ }
            }
            return _activationMessageId;
        }
    }

    public void Dispose()
    {
        var mutex = _mutex;
        _mutex = null;
        if (mutex is null) return;

        try { mutex.ReleaseMutex(); } catch { /* 未持有或已释放 */ }
        mutex.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
