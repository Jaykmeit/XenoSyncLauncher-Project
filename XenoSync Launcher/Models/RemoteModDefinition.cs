using System.Collections.Generic;

namespace XenoSyncLauncher.Models;

/// <summary>
/// One entry from the hosted mods-catalog.json. This is the curated list of
/// XenoSyncCore (mandatory) and Optional mods offered through the launcher -
/// end users don't add their own entries.
///
/// Expected JSON shape (a top-level array). Use "downloadUrl" for a single
/// file, or "downloadUrls" (an array, in part order) for mods split across
/// multiple archive parts (e.g. Night Conton City's .part1.rar/.part2.rar).
/// Use "mergeTargetSubfolder" for a mod whose extracted content must be
/// merged into an existing subfolder of the Modded folder instead of being
/// copied straight to its root (e.g. a DYT pack whose "CHI" folder needs to
/// land inside the already-installed "data/chara/CHI", not next to it):
/// [
///   {
///     "id": "some-slug",
///     "title": "Mod Title",
///     "description": "What it does.",
///     "author": "Author name/handle",
///     "pageUrl": "https://videogamemods.com/...",
///     "downloadUrl": "https://.../mod.zip",
///     "category": "XenoSyncCore",   // or "Optional"
///     "screenshotUrls": ["https://.../shot1.jpg", "https://.../shot2.jpg"]
///   },
///   {
///     "id": "night-conton-city",
///     "title": "Night Conton City",
///     "downloadUrls": [
///       "https://.../night_conton_city.part1.rar",
///       "https://.../night_conton_city.part2.rar"
///     ],
///     "category": "Optional"
///   },
///   {
///     "id": "night-conton-city-addon",
///     "title": "Night Conton City - Extra Pack",
///     "downloadUrl": "https://.../addon.zip",
///     "parent": "night-conton-city",
///     "category": "Optional"
///   },
///   {
///     "id": "chichi-dyt",
///     "title": "Chi-Chi DYT (InviernoCreations)",
///     "downloadUrl": "https://.../chichi_dyt.zip",
///     "category": "Optional",
///     "mergeTargetSubfolder": "data/chara/CHI"
///   }
/// ]
/// </summary>
public class RemoteModDefinition
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? PageUrl { get; set; }

    /// <summary>Single-file mods. Ignored if DownloadUrls is also set.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Multi-part mods, in part order (part1, part2, ...).</summary>
    public List<string>? DownloadUrls { get; set; }

    /// <summary>Id of another mod in this catalog that this one requires to function. Optional.</summary>
    public string? Parent { get; set; }

    public string? Category { get; set; }

    /// <summary>
    /// Curated screenshots (not scraped at runtime - third-party mod pages
    /// vary wildly in markup and some block automated fetching) shown as a
    /// small slideshow in the hover preview and the mod details panel.
    /// </summary>
    public List<string>? ScreenshotUrls { get; set; }

    /// <summary>
    /// Relative path (within the Modded folder, forward slashes, e.g.
    /// "data/chara/CHI") this mod's extracted content must be merged into,
    /// for mods whose real install method is "drop this folder's contents
    /// into an existing one" rather than the automatic x2m/exe/loose-files
    /// detection ModInstallService normally applies. See ModRecord.MergeTargetSubfolder.
    /// </summary>
    public string? MergeTargetSubfolder { get; set; }
}
