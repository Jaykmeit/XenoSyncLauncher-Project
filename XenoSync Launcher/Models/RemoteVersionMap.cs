using System.Collections.Generic;

namespace XenoSyncLauncher.Models;

/// <summary>
/// Shape of the hosted JSON (revamp-version-map.json) that centralizes
/// everything that changes whenever Revamp/XV2Patcher release a new version:
/// which game version/manifest is required, and where to download the
/// current XV2Patcher/Revamp builds from. Update this one file instead of
/// recompiling XenoSync Launcher when a new version comes out.
/// </summary>
public class RemoteVersionMap
{
    public string? RevampVersion { get; set; }
    public string? GameVersion { get; set; }
    public string? ManifestId { get; set; }
    public string? AppId { get; set; }
    public string? DepotId { get; set; }

    /// <summary>
    /// Steam's real buildid for the game version Revamp currently requires
    /// (find it via SteamDB's manifest history, same manual step as ManifestId).
    /// Setting this makes version comparisons purely numeric and reliable -
    /// there's no public buildid-to-marketing-version mapping, so without this
    /// field comparisons fall back to parsing "gameVersion" as text.
    /// </summary>
    public long? RequiredBuildId { get; set; }

    /// <summary>
    /// Optional buildid -> human version label lookup (e.g. "23988437": "1.26.0"),
    /// maintained by hand as new patches are identified in the community. Purely
    /// cosmetic for display - if a buildid isn't listed here, XenoSync Launcher
    /// just shows "build {id}" instead.
    /// </summary>
    public Dictionary<string, string>? KnownBuildVersions { get; set; }

    /// <summary>Direct download URL for the current XV2Patcher release (.rar).</summary>
    public string? Xv2PatcherDownloadUrl { get; set; }

    /// <summary>Google Drive file id for the current Revamp installer, extracted from .../file/d/{id}/view.</summary>
    public string? RevampGoogleDriveFileId { get; set; }

    /// <summary>
    /// Direct fallback download URL for the current Revamp installer (e.g. a
    /// Patreon mirror), used automatically if the Google Drive download fails
    /// (quota exceeded, confirmation page format changed, etc.). Confirmed
    /// (by the maintainer testing in a private/incognito browser window) to
    /// be a genuinely public direct link that doesn't need a login session.
    /// </summary>
    public string? RevampFallbackDownloadUrl { get; set; }
}