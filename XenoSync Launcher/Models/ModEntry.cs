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
    private bool _isDownloading;
    private double _downloadPercent;
    private bool _isExpanded = true;
    private bool _isVisibleInTree = true;

    /// <summary>Stable identifier used to persist which mods are enabled.</summary>
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Short description shown in the mod details panel.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Credited author/community member, shown in Mod Details and the hover preview. Empty if not curated in the catalog.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Curated screenshots for the hover preview slideshow. Empty for mods that haven't had any added to the catalog yet.</summary>
    public System.Collections.Generic.IReadOnlyList<string> ScreenshotUrls { get; init; } = System.Array.Empty<string>();

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

    /// <summary>True if at least one other mod in the catalog declares this one as its parent. Drives whether the expand/collapse arrow shows.</summary>
    public bool HasChildren { get; init; }

    /// <summary>0 for a top-level mod, 1 for a mod nested under a parent. Only one level of nesting is currently supported by the catalog schema.</summary>
    public int IndentLevel { get; init; }

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

    /// <summary>True while this mod (or the parent it's cascading through) is actively being downloaded. Drives the inline progress bar in the tree.</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading == value) return;
            _isDownloading = value;
            OnPropertyChanged();
        }
    }

    /// <summary>0-100. Only meaningful while IsDownloading is true.</summary>
    public double DownloadPercent
    {
        get => _downloadPercent;
        set
        {
            if (_downloadPercent.Equals(value)) return;
            _downloadPercent = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether this mod's children (if HasChildren) are currently shown. Toggled by the expand/collapse arrow. Only meaningful when HasChildren is true.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    /// <summary>False hides this row entirely - used for a child mod whose parent is currently collapsed.</summary>
    public bool IsVisibleInTree
    {
        get => _isVisibleInTree;
        set
        {
            if (_isVisibleInTree == value) return;
            _isVisibleInTree = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}