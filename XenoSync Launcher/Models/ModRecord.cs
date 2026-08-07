using System.Collections.Generic;

namespace XenoSyncLauncher.Models;

/// <summary>
/// Local record for one mod, stored in mods.json. For XenoSyncCore/Optional
/// mods, most fields (Title/Description/PageUrl/DownloadUrl/Category) are
/// refreshed from the hosted mods catalog on each load - only IsEnabled,
/// RepositoryFolder, and InstalledRelativeFiles are this device's own state
/// and get preserved across catalog refreshes.
/// </summary>
public class ModRecord
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    /// <summary>Download URL(s) for this mod, in part order. A single-file mod just has one entry.</summary>
    public List<string> DownloadUrls { get; set; } = new();

    /// <summary>Curated screenshots for the hover preview / mod details panel.</summary>
    public List<string> ScreenshotUrls { get; set; } = new();

    /// <summary>Id of another mod in this catalog that this one requires to function, if any.</summary>
    public string? ParentId { get; set; }

    public ModCategory Category { get; set; }

    /// <summary>Always true for RevampCore/XenoSyncCore. User-controlled for Optional.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Where this mod's clean extracted copy lives once downloaded (independent of any Modded folder).</summary>
    public string? RepositoryFolder { get; set; }

    /// <summary>Relative paths (within the Modded folder) this mod last copied there. Used to remove exactly those files on disable, without touching other mods' files.</summary>
    public List<string> InstalledRelativeFiles { get; set; } = new();
}