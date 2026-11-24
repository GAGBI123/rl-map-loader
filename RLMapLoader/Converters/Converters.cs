using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RLMapLoader.Converters
{
    /// <summary>true → Visible. Parameter "invert" flips it.</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool b = value is bool v && v;
            if (parameter is string s && s == "invert") b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>non-null / non-empty → Visible. Parameter "invert" flips it.</summary>
    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool has = value != null && !(value is string s && s.Length == 0) && !(value is int i && i == 0);
            if (parameter is string p && p == "invert") has = !has;
            return has ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public sealed class EqualsToBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? parameter ?? Binding.DoNothing : Binding.DoNothing;
    }

    /// <summary>0..1 → percentage text.</summary>
    public sealed class PercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
            => value is double d ? $"{Math.Round(d * 100)}%" : "";

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
