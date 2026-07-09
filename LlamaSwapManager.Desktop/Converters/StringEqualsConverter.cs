using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LlamaSwapManager.Converters;

/// <summary>
/// Returns true when value.ToString() equals ConverterParameter (ordinal ignore case).
/// Use for sidebar nav selection / view visibility without Boolean properties for every page.
/// </summary>
public class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var left = value?.ToString() ?? string.Empty;
        var right = parameter?.ToString() ?? string.Empty;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
