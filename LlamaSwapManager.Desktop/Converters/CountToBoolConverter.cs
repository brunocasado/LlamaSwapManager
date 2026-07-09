using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LlamaSwapManager.Converters;

/// <summary>
/// true when count/int > 0. Parameter "zero" inverts to true when count == 0.
/// </summary>
public class CountToBoolConverter : IValueConverter
{
    public static readonly CountToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0
        };
        var wantZero = parameter?.ToString()?.Equals("zero", StringComparison.OrdinalIgnoreCase) == true;
        return wantZero ? count == 0 : count > 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
