using System.Windows;
using System.Windows.Media;

namespace XenoSyncLauncher.Models;

public class LogEntry
{
    public required string Text { get; init; }
    public LogLevel Level { get; init; } = LogLevel.Info;

    /// <summary>Resolved from the app's theme brushes so log colors stay consistent with the rest of the UI.</summary>
    public Brush ForegroundBrush => Level switch
    {
        LogLevel.Warning => (Brush)Application.Current.Resources["BrushWarning"],
        LogLevel.Error => (Brush)Application.Current.Resources["BrushDanger"],
        _ => (Brush)Application.Current.Resources["BrushTextSecondary"]
    };

    public override string ToString() => Text;
}