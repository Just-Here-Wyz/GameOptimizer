namespace GameOptimizer.App.Views;

using System.Globalization;
using System.Windows;
using System.Windows.Data;

/// <summary>
/// 空字符串 → 收起。用于「未提权提示」「优化结果」这类有内容才占位的文字，
/// 避免界面上留一块空白行。
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
