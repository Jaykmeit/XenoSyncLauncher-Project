using System;
using System.Globalization;
using System.Windows.Data;

namespace XenoSyncLauncher.MainApp;

/// <summary>True (expanded) -> "▼", False (collapsed) -> "▶". Used for the mod tree's expand/collapse toggle.</summary>
public class BoolToArrowConverter : IValueConverter
{
    public static readonly BoolToArrowConverter Instance = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "\u25BC" : "\u25B6";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}