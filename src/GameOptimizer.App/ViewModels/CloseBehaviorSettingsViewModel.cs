namespace GameOptimizer.App.ViewModels;

using System.ComponentModel;
using GameOptimizer.Core.Library;

/// <summary>
/// 设置页「关闭行为」卡片。
///
/// 三个单选项与弹窗里的两个选项共用 <see cref="CloseBehaviorPolicy"/> 的文案，
/// 区别只在这里是<b>立即生效</b>（选了就写盘），弹窗那边要点「确认」。
///
/// 「每次询问」同时承担「重置」的职责：把它选回来，下次点 × 就会重新弹窗。
/// 没有单独的「恢复默认」按钮——两个入口改同一件事只会让人怀疑哪个才算数。
/// </summary>
public sealed class CloseBehaviorSettingsViewModel : INotifyPropertyChanged
{
    private readonly CloseBehaviorService _service;

    private string _statusText;
    private bool _isError;

    public CloseBehaviorSettingsViewModel(CloseBehaviorService service)
    {
        _service = service;

        // 启动时的读盘提示是最该让用户看见的一件事：配置坏了要说明，不能装作正常
        if (_service.LoadError is not null)
        {
            _statusText = _service.LoadError;
            _isError = true;
        }
        else if (_service.LoadNotice is not null)
        {
            _statusText = _service.LoadNotice;
            _isError = false;
        }
        else
        {
            _statusText = "改动立即生效，不需要重启。";
        }
    }

    /// <summary>选项一：每次询问（也是「重置」）。</summary>
    public bool IsAskSelected
    {
        get => _service.SavedBehavior == CloseBehavior.Ask;
        set { if (value) Apply(CloseBehavior.Ask); }
    }

    /// <summary>选项二：缩小到系统托盘。</summary>
    public bool IsMinimizeSelected
    {
        get => _service.SavedBehavior == CloseBehavior.MinimizeToTray;
        set { if (value) Apply(CloseBehavior.MinimizeToTray); }
    }

    /// <summary>选项三：退出程序。</summary>
    public bool IsExitSelected
    {
        get => _service.SavedBehavior == CloseBehavior.Exit;
        set { if (value) Apply(CloseBehavior.Exit); }
    }

    /// <summary>当前生效行为的说法，例如「当前：每次询问」。</summary>
    public string CurrentText => $"当前：{CloseBehaviorPolicy.Describe(_service.SavedBehavior)}";

    /// <summary>当前行为的后果说明。</summary>
    public string CurrentExplain => CloseBehaviorPolicy.Explain(_service.SavedBehavior);

    /// <summary>配置文件位置：用户要备份或手工改的时候知道去哪找。</summary>
    public string ConfigPathText => $"配置文件：{PathRedactor.Redact(_service.FilePath)}";

    /// <summary>保存结果 / 读盘提示。写盘失败必须在这里说清楚。</summary>
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>状态行颜色：出错用橙色，正常用灰。</summary>
    public string StatusHex => _isError ? "#FFFF9F0A" : "#FF83868D";

    private void Apply(CloseBehavior behavior)
    {
        if (_service.SavedBehavior == behavior)
        {
            // 点到的就是当前值：不重复写盘，但要把选中状态与文案对齐
            NotifyAll();
            return;
        }

        var ok = _service.Save(behavior, out var error);

        _isError = !ok;
        StatusText = ok
            ? $"已保存：{CloseBehaviorPolicy.Describe(behavior)}。{CloseBehaviorPolicy.Explain(behavior)}。"
            : $"配置写入失败（本次运行仍按这个选择处理，重启后会回到磁盘上的旧值）：{error}";

        NotifyAll();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(IsAskSelected));
        OnPropertyChanged(nameof(IsMinimizeSelected));
        OnPropertyChanged(nameof(IsExitSelected));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(CurrentExplain));
        OnPropertyChanged(nameof(StatusHex));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new(name));
}
