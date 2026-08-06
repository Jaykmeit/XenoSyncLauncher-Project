using System.Collections.Generic;
using System.IO;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Services;

/// Builds the list of <see cref="UpdateTaskItem"/> the Updater needs to run.
///
/// TODO: this is currently a fixed stub plan (XV2Patcher then Revamp, each with
/// Download/Extract/Install). Real implementation should build this dynamically
/// based on: whether a game-version downgrade/fresh-download is required (see
/// VersionCheckService.EvaluationAction), which optional mods are checked in the
/// mod tree, and which components are already up to date (skip tasks entirely
/// for components that don't need touching, instead of always listing all of them).
public class UpdateTaskPlanner
{
    // Simulated download size per component, only used by the mock progress
    // simulation until real DepotDownloader/HTTP downloads are wired in.
    private const long SimulatedDownloadBytes = 100_000_000;

    private static string ResumeTempDirectory =>
        Path.Combine(Path.GetTempPath(), "XenoSyncLauncher", "Downloads");

    public List<UpdateTaskItem> BuildPlan(VersionComparison comparison, LauncherSettings? settings = null)
    {
        var plan = new List<UpdateTaskItem>();

        // The game-version step (downgrade or fresh install) runs first, since
        // XV2Patcher/Revamp are only meaningful once the game itself is at the
        // version Revamp supports. This is the one real (non-simulated) task:
        // it's backed by an actual DepotDownloaderService.RunAsync call.
        if (settings is { NeedsGameDownload: true, ModdedPath: not null, RequiredManifestId: not null })
        {
            plan.Add(new UpdateTaskItem
            {
                Id = "download-game-version",
                DisplayName = "Xenoverse 2 (game files)",
                Phase = UpdatePhase.Download,
                IsRealDepotDownload = true,
                DepotAppId = settings.GameAppId,
                DepotId = settings.GameDepotId,
                ManifestId = settings.RequiredManifestId,
                TargetVersionLabel = settings.RequiredManifestId
            });
        }

        bool forceReinstall = settings?.ForceReinstallOnNextUpdate == true;

        AddComponentTasks(plan, idPrefix: "xv2patcher", displayName: "XV2Patcher",
            targetVersion: comparison.LatestXv2PatcherVersion, isUpToDate: comparison.Xv2PatcherUpToDate && !forceReinstall);

        AddComponentTasks(plan, idPrefix: "revamp", displayName: "Xenoverse 2 Revamp",
            targetVersion: comparison.LatestRevampVersion, isUpToDate: comparison.RevampUpToDate && !forceReinstall);

        return plan;
    }

    private void AddComponentTasks(List<UpdateTaskItem> plan, string idPrefix, string displayName, string? targetVersion, bool isUpToDate)
    {
        // Skip entirely if this component is already at the latest version.
        if (isUpToDate) return;

        var downloadId = $"download-{idPrefix}";

        plan.Add(new UpdateTaskItem
        {
            Id = downloadId,
            DisplayName = displayName,
            Phase = UpdatePhase.Download,
            TargetVersionLabel = targetVersion ?? string.Empty,
            ExpectedTotalBytes = SimulatedDownloadBytes,
            TempFilePath = Path.Combine(ResumeTempDirectory, $"{downloadId}.partial")
        });

        plan.Add(new UpdateTaskItem { Id = $"extract-{idPrefix}", DisplayName = displayName, Phase = UpdatePhase.Extract });
        plan.Add(new UpdateTaskItem { Id = $"install-{idPrefix}", DisplayName = displayName, Phase = UpdatePhase.Install });
    }
}