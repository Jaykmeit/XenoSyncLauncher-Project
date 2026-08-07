using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace XenoSyncLauncher.MainApp;

/// <summary>Turns ModEntry.IndentLevel (0 = top-level, 1 = nested under a parent) into a left margin for the tree row.</summary>
public class IndentLevelToMarginConverter : IValueConverter
{
    public static readonly IndentLevelToMarginConverter Instance = new();

    private const double IndentWidth = 26;

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        return new Thickness(level * IndentWidth, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}