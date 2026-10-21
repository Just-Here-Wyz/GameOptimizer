namespace GameOptimizer.App.Views;

using System.Globalization;
using System.Windows;
using System.Windows.Data;

/// <summary>
/// 空字符串 → 显示（与 <see cref="StringToVisibilityConverter"/> 相反）。
/// 输入框的占位符需要「没有内容时才出现」，用它是为了不必改那个已在别处使用的转换器。
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
