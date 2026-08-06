using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Single point of access to the hosted revamp-version-map.json, so
/// VersionCheckService and ComponentDownloadService don't each fetch it
/// separately. Cached in memory for the lifetime of the app; call
/// InvalidateCache() if you ever need to force a re-fetch (e.g. a manual
/// "check for updates" button).
/// </summary>
public class RemoteConfigService
{
    // TODO: same placeholder-turned-real URL used by VersionCheckService.
    private const string RevampVersionMapUrl = "https://raw.githubusercontent.com/Jaykmeit/XenoSync-Launcher/refs/heads/main/Version/revamp-version-map.json";

    // TODO: point this at wherever you host the curated mods catalog, e.g. a
    // sibling file in the same repo (.../main/Version/mods-catalog.json).
    private const string ModsCatalogUrl = "https://raw.githubusercontent.com/Jaykmeit/XenoSync-Launcher/refs/heads/main/Version/mods-catalog.json";

    private RemoteVersionMap? _cachedVersionMap;
    private List<RemoteModDefinition>? _cachedMods;

    public async Task<RemoteVersionMap?> GetAsync()
    {
        if (_cachedVersionMap is not null) return _cachedVersionMap;

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("XenoSyncLauncher");

            var json = await http.GetStringAsync(RevampVersionMapUrl);
            _cachedVersionMap = JsonSerializer.Deserialize<RemoteVersionMap>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            // Network issue, unreachable host, or unexpected JSON shape.
            // Callers fall back to their own hardcoded defaults.
            _cachedVersionMap = null;
        }

        return _cachedVersionMap;
    }

    public async Task<List<RemoteModDefinition>> GetModsAsync()
    {
        if (_cachedMods is not null) return _cachedMods;

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("XenoSyncLauncher");

            var json = await http.GetStringAsync(ModsCatalogUrl);
            _cachedMods = JsonSerializer.Deserialize<List<RemoteModDefinition>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<RemoteModDefinition>();
        }
        catch
        {
            _cachedMods = new List<RemoteModDefinition>();
        }

        return _cachedMods;
    }

    public void InvalidateCache()
    {
        _cachedVersionMap = null;
        _cachedMods = null;
    }
}