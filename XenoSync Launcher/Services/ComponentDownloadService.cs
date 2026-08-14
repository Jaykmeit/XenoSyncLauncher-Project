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

    // XV2INS itself, and the two prerequisite files that let it install .x2m
    // mods without needing to be run against a real Vanilla Steam install
    // (see ModInstallService.InstallViaX2mAsync for how these get used).
    private const string XV2InsDownloadUrl =
        "https://uploads.videogamemods.com/communities/the-citadel/mods/eternity-tools-1031725-80ba8c1e-8292-4cad-9c24-e0c7b36585f1/files/1762417393_XV2INS.rar";
    private const string XV2InsDcdDownloadUrl =
        "https://github.com/Jaykmeit/XenoSync-Launcher/raw/refs/heads/main/Version/xv2ins_dcd.rar";
    private const string XV2InsRegDownloadUrl =
        "https://raw.githubusercontent.com/Jaykmeit/XenoSync-Launcher/refs/heads/main/Version/x2i7394.tmp.reg";

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

    public Task<string> GetXv2InsDownloadUrlAsync() => Task.FromResult(XV2InsDownloadUrl);
    public Task<string> GetXv2InsDcdDownloadUrlAsync() => Task.FromResult(XV2InsDcdDownloadUrl);
    public Task<string> GetXv2InsRegDownloadUrlAsync() => Task.FromResult(XV2InsRegDownloadUrl);
}