using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LlamaSwapManager.Converters;

public class BoolToColorConverter : IValueConverter
{
    public Color TrueColor { get; set; } = Colors.White;
    public Color FalseColor { get; set; } = Colors.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? (Brush)new SolidColorBrush(TrueColor) : (Brush)new SolidColorBrush(FalseColor);
        return new SolidColorBrush(FalseColor);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}
