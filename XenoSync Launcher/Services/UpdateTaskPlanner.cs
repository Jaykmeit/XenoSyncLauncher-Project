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

        AddXv2InsPrerequisiteTasks(plan, settings, forceReinstall);

        AddComponentTasks(plan, idPrefix: "revamp", displayName: "Xenoverse 2 Revamp",
            targetVersion: comparison.LatestRevampVersion, isUpToDate: comparison.RevampUpToDate && !forceReinstall);

        return plan;
    }

    /// <summary>
    /// XV2INS + its two prerequisite files (xv2ins_dcd.rar, x2i7394.tmp.reg) -
    /// together these let .x2m mods install automatically (see
    /// ModInstallService.InstallViaX2mAsync) without needing XV2INS run
    /// against a real Vanilla Steam install. Unlike XV2Patcher/Revamp there's
    /// no "latest version" to track for these - they're a one-time setup, so
    /// skipped entirely once XV2INS.exe is already present AND correctly
    /// registered for THIS install's actual path.
    /// </summary>
    private void AddXv2InsPrerequisiteTasks(List<UpdateTaskItem> plan, LauncherSettings? settings, bool forceReinstall)
    {
        if (settings?.ModdedPath is null) return;

        // IsX2mAssociated(moddedPath) - not the parameterless overload - is
        // required here: a stale/mismatched association (e.g. left over from
        // before RegisterAssociation started writing THIS install's real
        // path instead of the hosted x2i7394.tmp.reg's hardcoded one -
        // "C:\Program Files (x86)\Steam\...\DB Xenoverse 2 REVAMP") would
        // otherwise still read as "already associated" via the parameterless
        // check, wrongly skipping this entire block - including the
        // "run-xv2ins-first-launch" task below - even on a machine that
        // genuinely still needs XV2INS re-registered against its actual
        // Modded path. This was confirmed to be exactly why XV2INS's first
        // real run was silently never happening: the check above (only
        // verifying "does .x2m resolve to *some* handler") looked satisfied
        // from an earlier, incorrectly-pathed registration, so the whole
        // prerequisite setup - including ever launching XV2INS once to let
        // it initialize itself - got skipped, and the pipeline moved
        // straight on to downloading Revamp instead.
        var alreadySetUp = !forceReinstall
            && System.IO.File.Exists(Path.Combine(settings.ModdedPath, "XV2INS.exe"))
            && X2mRegistryAssociationService.IsX2mAssociated(settings.ModdedPath);
        if (alreadySetUp) return;

        AddComponentTasks(plan, idPrefix: "xv2ins", displayName: "XV2INS", targetVersion: null, isUpToDate: false);
        AddComponentTasks(plan, idPrefix: "xv2ins-dcd", displayName: "XV2INS prerequisite files", targetVersion: null, isUpToDate: false);
        AddComponentTasks(plan, idPrefix: "xv2ins-reg", displayName: "XV2INS file association", targetVersion: null, isUpToDate: false);

        // XV2INS.exe itself is only ever COPIED to disk by "install-xv2ins"
        // above - none of the three component task groups above actually
        // EXECUTE it. Without this task, XV2INS's first real run wouldn't
        // happen until a mod needed installing via the X2M path (see
        // ModInstallService.InstallViaX2mAsync/InstallX2mGroupAsync), by
        // which point Revamp - and potentially other components - had
        // already been downloaded and installed: XV2INS ended up running
        // completely unattended and unpredictably, deep inside a later
        // automated mod-install pass (with nobody there to respond to any
        // first-run dialog it might show), instead of at a controlled,
        // visible point right here. Placed last, after xv2ins-dcd and
        // xv2ins-reg, so by the time XV2INS actually launches, every
        // prerequisite file AND the (now correctly-targeted) file
        // association are already fully in place.
        plan.Add(new UpdateTaskItem
        {
            Id = "run-xv2ins-first-launch",
            DisplayName = "XV2INS (first-time setup)",
            Phase = UpdatePhase.Install
        });
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