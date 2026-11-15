using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BasaltUI.Converters;

/// <summary>true → Visible.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public static readonly BoolToVisibility Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>true → Collapsed. The other half of a pair with <see cref="BoolToVisibility"/>.</summary>
public sealed class NotBoolToVisibility : IValueConverter
{
    public static readonly NotBoolToVisibility Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>Any items → Visible; used to hide panels that have nothing to say.</summary>
public sealed class CountToVisibility : IValueConverter
{
    public static readonly CountToVisibility Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Shows a card only when the text inside it is not empty.</summary>
public sealed class FilledToVisibility : IValueConverter
{
    public static readonly FilledToVisibility Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// (value, minimum, maximum, width) → the width of the filled part of a slider's track, so the theme
/// template can draw the fill without knowing the control's geometry.
/// </summary>
public sealed class FractionToWidth : IMultiValueConverter
{
    public static readonly FractionToWidth Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not double value || values[1] is not double min ||
            values[2] is not double max || values[3] is not double width)
            return 0.0;
        if (width <= 0 || max <= min)
            return 0.0;
        return Math.Clamp((value - min) / (max - min), 0, 1) * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => [Binding.DoNothing, Binding.DoNothing, Binding.DoNothing, Binding.DoNothing];
}
