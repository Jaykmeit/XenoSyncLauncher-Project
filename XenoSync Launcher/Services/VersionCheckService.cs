using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Encapsulates "what version is there, and what do we need to do about it?".
///
/// GetRevampSupportedVersionAsync: there is no official API mapping a Revamp
/// release to the exact Steam ManifestId it needs - SteamDB manifests don't
/// carry the human "1.25.02"-style version label, and Revamp's own page only
/// states that label in prose. The community's own workflow (see e.g. the
/// Steam guide "How to downgrade your precious Xenoverse 2") is to manually
/// read SteamDB's manifest history and note down the matching manifest by date.
///
/// Rather than attempt something fragile (scraping SteamDB, which requires a
/// login to see raw depot data, or parsing Revamp's changelog prose), this
/// service reads a small JSON file (via RemoteConfigService) that maps the
/// current Revamp-supported version to its ManifestId. That file is hosted in
/// this project's own GitHub repo and updated by hand whenever Revamp bumps
/// its supported game version - the same manual step the community already
/// does today, just centralized once instead of every player redoing it.
/// See RemoteVersionMap for the JSON shape.
/// </summary>
public class VersionCheckService
{
    private readonly RemoteConfigService _remoteConfigService;

    public VersionCheckService(RemoteConfigService? remoteConfigService = null)
    {
        _remoteConfigService = remoteConfigService ?? new RemoteConfigService();
    }

    /// <summary>
    /// Fetches the current Revamp-supported game version and its ManifestId
    /// from the hosted mapping file. Falls back to a hardcoded value if the
    /// fetch fails (offline, unexpected JSON shape, etc.) so the rest of the
    /// Wizard/Update flow still has something to work with.
    /// </summary>
    public async Task<VersionInfo> GetRevampSupportedVersionAsync()
    {
        var map = await _remoteConfigService.GetAsync();

        if (map is { GameVersion: not null, ManifestId: not null })
            return new VersionInfo { Label = map.GameVersion, ManifestId = map.ManifestId, BuildId = map.RequiredBuildId };

        // Fallback: last known values as of this writing. Update whenever
        // Revamp announces a new supported version, same as the hosted JSON.
        return new VersionInfo { Label = "1.25.02", ManifestId = null };
    }

    /// <summary>
    /// Detects the installed Xenoverse 2 build via Steam's local appmanifest
    /// (appmanifest_454650.acf), reading the per-depot manifest id under
    /// "InstalledDepots" -&gt; "454651" (Xenoverse 2's main content depot) -&gt;
    /// "manifest". This is the precise, depot-level identity of what's
    /// actually installed - unlike the app-level "buildid" field, which was
    /// confirmed to be shared between 1.25.2 and 1.26.0 despite XV2Patcher
    /// only supporting one of them. The buildid is still read for display
    /// purposes (via KnownBuildVersions), but no longer used for comparison.
    /// </summary>
    public async Task<VersionInfo?> DetectInstalledVersionAsync(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return null;

        var installedManifestId = TryReadInstalledManifestId(installPath);
        var acfBuildId = TryReadBuildIdFromAppManifest(installPath);

        if (installedManifestId is not null)
        {
            var map = await _remoteConfigService.GetAsync();
            var displayLabel = acfBuildId is not null && map?.KnownBuildVersions?.GetValueOrDefault(acfBuildId.Value.ToString()) is { } known
                ? known
                : $"manifest {installedManifestId}";

            return new VersionInfo { Label = displayLabel, ManifestId = installedManifestId, BuildId = acfBuildId };
        }

        if (acfBuildId is not null)
        {
            var map = await _remoteConfigService.GetAsync();
            var displayLabel = map?.KnownBuildVersions?.GetValueOrDefault(acfBuildId.Value.ToString()) ?? $"build {acfBuildId}";
            return new VersionInfo { Label = displayLabel, BuildId = acfBuildId };
        }

        // Last resort: the exe-scan attempt (see TryReadVersionFromExe) - kept
        // in case it does work for some builds, though it did not turn up a
        // match when this was tested against a real 1.26.0 install.
        var exeVersion = TryReadVersionFromExe(installPath);
        if (exeVersion is not null)
            return new VersionInfo { Label = exeVersion.Value.Version, BuildId = exeVersion.Value.BuildId };

        var marker = Path.Combine(installPath, "version.txt");
        if (File.Exists(marker))
        {
            var label = (await File.ReadAllTextAsync(marker)).Trim();
            return new VersionInfo { Label = label };
        }

        return null;
    }

    /// <summary>
    /// Reads the manifest id currently installed for Xenoverse 2's main
    /// content depot (454651) from the ACF's "InstalledDepots" block. This is
    /// the precise, per-depot identity of what's actually installed -
    /// directly comparable against the ManifestId from
    /// GetRevampSupportedVersionAsync, with no version-label ambiguity.
    /// </summary>
    private static string? TryReadInstalledManifestId(string installPath)
    {
        try
        {
            var acfText = TryReadAppManifestText(installPath);
            if (acfText is null) return null;

            var depotBlock = Regex.Match(acfText, "\"454651\"\\s*\\{([^}]*)\\}", RegexOptions.Singleline);
            if (!depotBlock.Success) return null;

            var manifestMatch = Regex.Match(depotBlock.Groups[1].Value, "\"manifest\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
            return manifestMatch.Success ? manifestMatch.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Regex ExeVersionPattern = new(@"Ver\.(\d+(?:\.\d+){1,3})\s*bid\.(\d+)", RegexOptions.Compiled);

    private static (string Version, long BuildId)? TryReadVersionFromExe(string installPath)
    {
        try
        {
            var exePath = Path.Combine(installPath, "bin", "DBXV2.exe");
            if (!File.Exists(exePath)) return null;

            // The string is stored internally as UTF-16 (char16_t*); decoding the
            // whole file as UTF-16 still surfaces it correctly wherever it occurs,
            // since PE string literals are stored 2-byte-aligned.
            var bytes = File.ReadAllBytes(exePath);
            var text = Encoding.Unicode.GetString(bytes);

            var match = ExeVersionPattern.Match(text);
            if (!match.Success) return null;

            return long.TryParse(match.Groups[2].Value, out var buildId)
                ? (match.Groups[1].Value, buildId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads the raw text of appmanifest_454650.acf, or null if it can't be found/read.</summary>
    private static string? TryReadAppManifestText(string installPath)
    {
        try
        {
            // installPath is typically ".../steamapps/common/<game folder>";
            // the manifest lives directly in "steamapps/", two levels up.
            var commonDir = Directory.GetParent(installPath)?.FullName;
            var steamappsDir = commonDir is not null ? Directory.GetParent(commonDir)?.FullName : null;
            if (steamappsDir is null) return null;

            var acfPath = Path.Combine(steamappsDir, "appmanifest_454650.acf");
            return File.Exists(acfPath) ? File.ReadAllText(acfPath) : null;
        }
        catch
        {
            return null;
        }
    }

    private static long? TryReadBuildIdFromAppManifest(string installPath)
    {
        var acfText = TryReadAppManifestText(installPath);
        if (acfText is null) return null;

        var match = Regex.Match(acfText, "\"buildid\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups[1].Value, out var buildId) ? buildId : null;
    }

    /// <summary>
    /// Applies the decision matrix described in the Wizard's design, simplified
    /// to not depend on knowing Steam's "latest" version - which XenoSync
    /// Launcher has no reliable way to check. Any mismatch between what's
    /// installed and what Revamp requires always goes through DepotDownloader,
    /// which fetches the exact required manifest regardless of what Steam
    /// currently serves:
    ///   - No Vanilla installed        -> FreshDownloadRequired
    ///   - Installed == Revamp's build -> NoActionRequired
    ///   - Installed != Revamp's build -> DowngradeRequired
    /// </summary>
    public EvaluationAction Evaluate(VersionInfo? installedVanilla, VersionInfo revampSupported)
    {
        if (installedVanilla is null)
            return EvaluationAction.FreshDownloadRequired;

        return installedVanilla.CompareTo(revampSupported) == 0
            ? EvaluationAction.NoActionRequired
            : EvaluationAction.DowngradeRequired;
    }
}