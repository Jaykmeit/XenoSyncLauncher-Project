using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XenoSyncLauncher.Models;

/// <summary>
/// Represents a single mod row in the mod tree.
/// Implements INotifyPropertyChanged so the checkbox and any bound label
/// update immediately when toggled from code (e.g. the locked Revamp entry).
/// </summary>
public class ModEntry : INotifyPropertyChanged
{
    private bool _isChecked;

    /// <summary>Stable identifier used to persist which mods are enabled.</summary>
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Short description shown in the mod details panel.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// URL shown/opened when the user clicks or hovers the mod title.
    /// For "Xenoverse 2 Revamp" this points to its VideogameMods platform page
    /// instead of a download page, per the design spec.
    /// </summary>
    public string PageUrl { get; init; } = string.Empty;

    /// <summary>
    /// Which of the three tree groups this mod belongs to (Revamp Core /
    /// XenoSync Core / Optional). Determines whether the checkbox is locked.
    /// </summary>
    public ModCategory Category { get; init; }

    /// <summary>Id of the mod this one requires, if any.</summary>
    public string? ParentId { get; init; }

    /// <summary>Display name of the required mod, for the "Requires: X" note. Null if this mod has no parent.</summary>
    public string? ParentTitle { get; init; }

    /// <summary>Group header text shown above this mod in the tree.</summary>
    public string CategoryGroupName => Category switch
    {
        ModCategory.RevampCore => "Revamp Core",
        ModCategory.XenoSyncCore => "XenoSync Core",
        _ => "Optional"
    };

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The checkbox is only interactive for Optional mods; Revamp Core and XenoSync Core are always locked on.</summary>
    public bool IsCheckboxEnabled => Category == ModCategory.Optional;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}