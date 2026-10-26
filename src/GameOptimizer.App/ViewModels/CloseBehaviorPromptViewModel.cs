namespace GameOptimizer.App.ViewModels;

using System.ComponentModel;
using GameOptimizer.Core.Library;

/// <summary>
/// 「关闭行为选择」弹窗的 ViewModel。
///
/// 只做三件界面的事：哪个选项被选中、有没有勾「记住」、用户点了确认还是取消。
/// <b>决策与落盘都不在这里</b>：选了之后该隐藏还是退出、要不要写配置，
/// 全由 Core 的 <see cref="CloseBehaviorService"/> 判定，
/// 这样弹窗关掉之后的行为可以脱离界面单测（见 CloseBehaviorTests）。
///
/// 默认选中「缩小到系统托盘」——与 v2.3.0 以来「关窗口进托盘」的老习惯一致，
/// 但<b>只是选中，不执行</b>：没点「确认」之前什么都不发生。
/// </summary>
public sealed class CloseBehaviorPromptViewModel : INotifyPropertyChanged
{
    private CloseBehavior _selected = CloseBehavior.MinimizeToTray;
    private bool _remember;

    public CloseBehaviorPromptViewModel()
    {
        ConfirmCommand = new RelayCommand(_ => Confirm());
        CancelCommand = new RelayCommand(_ => Cancel());
    }

    /// <summary>当前选中的行为。只可能是「缩小到托盘」或「退出程序」。</summary>
    public CloseBehavior Selected
    {
        get => _selected;
        private set
        {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMinimizeSelected));
            OnPropertyChanged(nameof(IsExitSelected));
        }
    }

    /// <summary>选项一：缩小到系统托盘。</summary>
    public bool IsMinimizeSelected
    {
        get => Selected == CloseBehavior.MinimizeToTray;
        set { if (value) Selected = CloseBehavior.MinimizeToTray; }
    }

    /// <summary>选项二：退出程序。</summary>
    public bool IsExitSelected
    {
        get => Selected == CloseBehavior.Exit;
        set { if (value) Selected = CloseBehavior.Exit; }
    }

    /// <summary>「记住我的选择，下次不再提示」。默认不勾。</summary>
    public bool Remember
    {
        get => _remember;
        set
        {
            if (_remember == value) return;
            _remember = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RememberHint));
        }
    }

    /// <summary>勾选与否给一句不同的说明，让用户知道自己正在改什么。</summary>
    public string RememberHint => Remember
        ? "下次点 × 直接执行这个选择，不再询问。随时可以在设置页改回来。"
        : "只对这一次生效。下次点 × 还会再问一次。";

    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// 用户确认的结果；null 表示取消（含直接关窗、按 Esc）。
    /// 由 <see cref="CloseBehaviorService.CompletePrompt"/> 消费。
    /// </summary>
    public CloseBehaviorChoice? Result { get; private set; }

    /// <summary>是否点了「确认」（不是取消）。</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>窗口该关了。由 View 层订阅并 Close。</summary>
    public event EventHandler? CloseRequested;

    private void Confirm()
    {
        Result = new CloseBehaviorChoice(Selected, Remember);
        IsConfirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel()
    {
        // 取消必须完全不改状态：Result 保持 null，调用方据此判定「什么都不做」
        Result = null;
        IsConfirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new(name));
}
