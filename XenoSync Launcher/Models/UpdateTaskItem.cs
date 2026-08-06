using System;

namespace XenoSyncLauncher.Models;

public enum UpdatePhase
{
    Download,
    Extract,
    Install
}

/// A single unit of work in the update pipeline. The overall progress bar is
/// driven by counting how many of these have been completed versus how many
/// were planned in total — not by an arbitrary fixed timeline.
public class UpdateTaskItem
{
    /// Stable id used as the key for persisted resume state (e.g. "download-revamp").
    public string Id { get; init; } = string.Empty;

    /// Readable name shown in the task label (e.g. "XV2Patcher", "Xenoverse 2 Revamp").
    public string DisplayName { get; init; } = string.Empty;

    public UpdatePhase Phase { get; init; }

    public bool IsCompleted { get; set; }

    // --- Fields only meaningful for Phase == Download, used for resumability ---

    /// The version this download corresponds to. Used to detect staleness on resume.
    public string? TargetVersionLabel { get; init; }

    /// Where the partial file would be written to during the actual download.
    public string? TempFilePath { get; init; }

    public long ExpectedTotalBytes { get; set; }
    public long BytesDownloaded { get; set; }

    /// Simulated progress counters for non-download phases (Extract/Install), so pausing mid-phase keeps its progress.
    public int SubTicksCompleted { get; set; }
    public int TotalSubTicks { get; init; } = 4;

    // --- Fields only meaningful when IsRealDepotDownload == true ---

    /// True for the one task that runs a real DepotDownloader process instead of the mock simulation.
    public bool IsRealDepotDownload { get; init; }

    public string? DepotAppId { get; init; }
    public string? DepotId { get; init; }
    public string? ManifestId { get; init; }

    /// Set live from DepotDownloaderService's IProgress callback (0..100), used instead of ExpectedTotalBytes/BytesDownloaded.
    public double RealTimeProgressPercent { get; set; }

    /// Fraction (0..1) of this specific task's completion, used to compute smooth overall progress.
    public double FractionComplete
    {
        get
        {
            if (IsCompleted) return 1;
            if (IsRealDepotDownload) return Math.Clamp(RealTimeProgressPercent / 100.0, 0, 1);
            if (Phase == UpdatePhase.Download && ExpectedTotalBytes > 0) return (double)BytesDownloaded / ExpectedTotalBytes;
            return TotalSubTicks > 0 ? (double)SubTicksCompleted / TotalSubTicks : 0;
        }
    }

    public string PhaseLabel => Phase switch
    {
        UpdatePhase.Download => "Downloading",
        UpdatePhase.Extract => "Extracting",
        UpdatePhase.Install => "Installing",
        _ => "Working on"
    };
}