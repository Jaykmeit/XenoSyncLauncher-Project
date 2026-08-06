using System;

namespace XenoSyncLauncher.Models;

/// Snapshot of an in-progress download, persisted to disk so it can survive
/// the user pausing the update or closing XenoSync Launcher entirely.
/// Matched back to a planned <see cref="UpdateTaskItem"/> by <see cref="TaskId"/>;
/// discarded automatically if <see cref="TargetVersionLabel"/> no longer matches
/// the version currently required (i.e. a newer release was detected).
public class DownloadResumeState
{
    public string TaskId { get; set; } = string.Empty;
    public string TaskDisplayName { get; set; } = string.Empty;
    public string TargetVersionLabel { get; set; } = string.Empty;
    public string TempFilePath { get; set; } = string.Empty;
    public long ExpectedTotalBytes { get; set; }
    public long BytesDownloaded { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
}
