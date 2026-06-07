using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LlamaSwapManager.Converters;

public sealed class SelectedModelBrushConverter : IValueConverter
{
    public static readonly SelectedModelBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return new SolidColorBrush(Color.Parse(value is bool b && b ? "#45475A" : "#181825"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
