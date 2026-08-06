using System.Collections.Generic;

namespace XenoSyncLauncher.Models;

/// <summary>
/// One entry from the hosted mods-catalog.json. This is the curated list of
/// XenoSyncCore (mandatory) and Optional mods offered through the launcher -
/// end users don't add their own entries.
///
/// Expected JSON shape (a top-level array). Use "downloadUrl" for a single
/// file, or "downloadUrls" (an array, in part order) for mods split across
/// multiple archive parts (e.g. Night Conton City's .part1.rar/.part2.rar):
/// [
///   {
///     "id": "some-slug",
///     "title": "Mod Title",
///     "description": "What it does.",
///     "pageUrl": "https://videogamemods.com/...",
///     "downloadUrl": "https://.../mod.zip",
///     "category": "XenoSyncCore"   // or "Optional"
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
///   }
/// ]
/// </summary>
public class RemoteModDefinition
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? PageUrl { get; set; }

    /// <summary>Single-file mods. Ignored if DownloadUrls is also set.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Multi-part mods, in part order (part1, part2, ...).</summary>
    public List<string>? DownloadUrls { get; set; }

    /// <summary>Id of another mod in this catalog that this one requires to function. Optional.</summary>
    public string? Parent { get; set; }

    public string? Category { get; set; }
}