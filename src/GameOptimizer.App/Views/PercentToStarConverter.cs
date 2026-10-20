namespace GameOptimizer.App.Views;

using System.Globalization;
using System.Windows;
using System.Windows.Data;

/// <summary>
/// 百分比 → GridLength 星号值，用于条形图按比例分宽度。
///
/// 为什么需要它：条形宽度想表达「占多少百分比」，
/// 若把 0~100 直接绑到 Border.Width，得到的是 0~100 **像素**，
/// 窗口一放大条就全一样长，完全不成比例。
/// 而 GridLength 不能在 XAML 里用绑定算出星号值，只能走转换器。
///
/// 用法：两根列，一根绑 Percent，一根绑 Percent 的补数（100 - Percent）。
/// 补数由 <see cref="Yes"/> 参数的布尔值切换，避免再加一个属性。
/// </summary>
public sealed class PercentToStarConverter : IValueConverter
{
    /// <summary>设为 "inverse" 时返回补数（100 - 值）。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0d,
        };

        if (double.IsNaN(percent) || double.IsInfinity(percent)) percent = 0;
        percent = Math.Clamp(percent, 0, 100);

        var inverse = string.Equals(parameter as string, "inverse", StringComparison.OrdinalIgnoreCase);
        var star = inverse ? 100 - percent : percent;

        // 0 星号列会塌陷成 0 宽，条看起来像没画出来；给一丝最小值。
        // GridLength 的星号值允许小数，0.01* 在视觉上就是「极短但可见」。
        return new GridLength(Math.Max(0.01, star), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
