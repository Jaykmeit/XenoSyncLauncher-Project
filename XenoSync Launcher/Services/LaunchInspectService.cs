using System.IO;
using System.Threading.Tasks;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Backs the "Launch Inspect" panel: compares locally installed Revamp/XV2Patcher
/// versions against the latest ones available online, and checks whether
/// Xenoverse 2 is "Live" (playable) in the Modded folder.
///
/// TODO: GetLatestRevampVersionAsync/GetLatestXv2PatcherVersionAsync are still
/// stubs. Ideally these would come from the same hosted JSON VersionCheckService
/// reads (revamp-version-map.json), rather than being hardcoded here separately.
/// </summary>
public class LaunchInspectService
{
    private readonly InstalledComponentVersionService _installedVersionService = new();

    public Task<string> GetLatestRevampVersionAsync() => Task.FromResult("5.1.1");

    public Task<string> GetLatestXv2PatcherVersionAsync() => Task.FromResult("4.64");

    public Task<string?> GetInstalledRevampVersionAsync(string moddedPath) =>
        Task.FromResult(_installedVersionService.GetInstalledRevampVersion(moddedPath));

    public Task<string?> GetInstalledXv2PatcherVersionAsync(string moddedPath) =>
        Task.FromResult(_installedVersionService.GetInstalledXv2PatcherVersion(moddedPath));

    /// <summary>
    /// "Live" means DBXV2.exe is present under "&lt;moddedPath&gt;/bin/", i.e. the
    /// game is actually launchable from the configured Modded folder.
    /// </summary>
    public bool IsGameLive(string? moddedPath)
    {
        if (string.IsNullOrWhiteSpace(moddedPath)) return false;
        return File.Exists(Path.Combine(moddedPath, "bin", "DBXV2.exe"));
    }
}