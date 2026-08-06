using System.Threading.Tasks;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Where to download XV2Patcher and Revamp from. Reads the hosted
/// revamp-version-map.json (via RemoteConfigService) so a new release only
/// requires updating that file - falls back to the versions known at the
/// time of writing (XV2Patcher 4.64, Revamp 5.1.1) if the fetch fails.
/// </summary>
public class ComponentDownloadService
{
    private const string FallbackXv2PatcherDownloadUrl =
        "https://uploads.videogamemods.com/communities/the-citadel/mods/eternity-tools-1031725-80ba8c1e-8292-4cad-9c24-e0c7b36585f1/files/1766133083_xv2patcher_4.64.rar";

    private const string FallbackRevampGoogleDriveFileId = "19HCTs0kXGdxGYuNkExKdHoji0eMwjd69";

    /// <summary>
    /// Confirmed by the maintainer (tested in a private/incognito browser
    /// window, no Patreon session) to be a genuinely public direct link.
    /// Used automatically if the Google Drive download fails.
    /// </summary>
    private const string FallbackRevampFallbackDownloadUrl = "https://www.patreon.com/file?h=147073369&m=589192942";

    private readonly RemoteConfigService _remoteConfigService;

    public ComponentDownloadService(RemoteConfigService? remoteConfigService = null)
    {
        _remoteConfigService = remoteConfigService ?? new RemoteConfigService();
    }

    public async Task<string> GetXv2PatcherDownloadUrlAsync()
    {
        var map = await _remoteConfigService.GetAsync();
        return string.IsNullOrWhiteSpace(map?.Xv2PatcherDownloadUrl) ? FallbackXv2PatcherDownloadUrl : map.Xv2PatcherDownloadUrl;
    }

    /// <summary>Google Drive file id extracted from https://drive.google.com/file/d/{id}/view</summary>
    public async Task<string> GetRevampGoogleDriveFileIdAsync()
    {
        var map = await _remoteConfigService.GetAsync();
        return string.IsNullOrWhiteSpace(map?.RevampGoogleDriveFileId) ? FallbackRevampGoogleDriveFileId : map.RevampGoogleDriveFileId;
    }

    /// <summary>Direct URL tried automatically if the Google Drive download fails.</summary>
    public async Task<string> GetRevampFallbackDownloadUrlAsync()
    {
        var map = await _remoteConfigService.GetAsync();
        return string.IsNullOrWhiteSpace(map?.RevampFallbackDownloadUrl) ? FallbackRevampFallbackDownloadUrl : map.RevampFallbackDownloadUrl;
    }
}