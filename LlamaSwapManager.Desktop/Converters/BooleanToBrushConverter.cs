using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LlamaSwapManager.Converters;

public class BooleanToBrushConverter : IValueConverter
{
    public static readonly BooleanToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? new SolidColorBrush(Color.Parse("#A6E3A1")) : Brushes.Transparent;
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
