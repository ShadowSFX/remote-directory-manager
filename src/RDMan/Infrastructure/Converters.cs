using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RDMan.Infrastructure;

/// <summary>true → Visible. Parameter "invert" flips it.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "invert") b = !b;
        // "hidden": keeps the element's layout slot (a hidden session surface still gets its pane's size).
        return b ? Visibility.Visible : parameter as string == "hidden" ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible ^ (parameter as string == "invert");
}

/// <summary>non-null and non-empty string → Visible. Parameter "invert" flips it.</summary>
public sealed class NotEmptyToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => s.Length > 0,
            int i => i > 0,
            _ => true,
        };
        if (parameter as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Enum/string equality for radio buttons: IsChecked="{Binding Mode, Converter={StaticResource Eq}, ConverterParameter=Custom}".</summary>
public sealed class EqualsToBool : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t.IsEnum) return Enum.Parse(t, parameter.ToString()!, ignoreCase: true);
        if (t == typeof(int)) return int.Parse(parameter.ToString()!, CultureInfo.InvariantCulture);
        return parameter.ToString()!;
    }
}

/// <summary>Equality → Visible (tab modes etc.).</summary>
public sealed class EqualsToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class NullToBool : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is not null) ^ (parameter as string == "invert");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Dims "cut" items and hidden files the way Explorer does.</summary>
public sealed class ItemOpacity : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var cut = values.Length > 0 && values[0] is true;
        var hidden = values.Length > 1 && values[1] is true;
        return cut ? 0.45 : hidden ? 0.62 : 1.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>Brush or a theme fallback (identity color strip when a server has no color).</summary>
public sealed class BrushOrFallback : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Brush b) return b;
        return Application.Current.TryFindResource(parameter as string ?? "Line.Strong");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Sort arrow for column headers: binds SortColumn + SortDescending, parameter = column name.</summary>
public sealed class SortGlyph : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] as string != parameter as string) return "";
        return values[1] is true ? "" : "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>Left indent of a TreeViewItem: 16 px per ancestor TreeViewItem (full-row selection needs it in the template).</summary>
public sealed class TreeDepth : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var depth = 0;
        DependencyObject? d = value as DependencyObject;
        while (d is not null)
        {
            d = VisualTreeHelper.GetParent(d);
            if (d is System.Windows.Controls.TreeView) break;
            if (d is System.Windows.Controls.TreeViewItem) depth++;
        }
        return new Thickness(8 + depth * 16, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Depth → left indent for group choices in combo boxes.</summary>
public sealed class Multiply : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Convert.ToDouble(value, culture) * double.Parse(parameter as string ?? "1", CultureInfo.InvariantCulture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
