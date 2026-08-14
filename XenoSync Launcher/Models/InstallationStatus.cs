namespace XenoSyncLauncher.Models;

/// Overall state of the update/run action button, mirroring the
/// Update/Pause/Resume/Run swap described in the design spec.
public enum LauncherActivityState
{
    Idle,
    Updating,
    Paused
}

/// The flags that must all be true for the "Run" button to be enabled.

public class InstallationStatus
{
    public bool IsXenoverse2Installed { get; set; }
    public bool IsXv2PatcherUpToDate { get; set; }
    public bool IsRevampUpToDate { get; set; }

    /// <summary>True only if no mod (including Revamp Core) has NeedsUpdate - i.e. every mod recorded as enabled has actually been verified present on disk.</summary>
    public bool AreAllModsUpToDate { get; set; } = true;

    public bool CanRun => IsXenoverse2Installed && IsXv2PatcherUpToDate && IsRevampUpToDate && AreAllModsUpToDate;
}

public class VersionComparison
{
    public string? InstalledRevampVersion { get; set; }
    public string? LatestRevampVersion { get; set; }

    public string? InstalledXv2PatcherVersion { get; set; }
    public string? LatestXv2PatcherVersion { get; set; }

    public bool RevampUpToDate =>
        !string.IsNullOrEmpty(InstalledRevampVersion) &&
        InstalledRevampVersion == LatestRevampVersion;

    public bool Xv2PatcherUpToDate =>
        !string.IsNullOrEmpty(InstalledXv2PatcherVersion) &&
        InstalledXv2PatcherVersion == LatestXv2PatcherVersion;
}