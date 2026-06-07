using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LlamaSwapManager.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var boolValue = value is true;
        var invert = parameter?.ToString()?.Equals("invert", StringComparison.OrdinalIgnoreCase) == true;
        if (invert) boolValue = !boolValue;
        return boolValue ? "Visible" : "Collapsed";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}
