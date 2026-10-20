namespace GameOptimizer.App.Views;

using System.Globalization;
using System.Windows;
using System.Windows.Data;

/// <summary>
/// 图标兜底转换：值为 null 时显示首字符色块（Visible），有图标时收起。
/// 与 BooleanToVisibilityConverter 相反的是它判断 null。
/// </summary>
public sealed class NullToCollapsedInverseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
