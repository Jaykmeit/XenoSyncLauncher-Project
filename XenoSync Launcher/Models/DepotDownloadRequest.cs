namespace XenoSyncLauncher.Models;

/// Parameters for a single DepotDownloader run. Re-running with the same
/// InstallDirectory + ManifestId lets DepotDownloader resume an interrupted
/// download using its own staging folder (".DepotDownloader" inside
/// InstallDirectory) — XenoSync Launcher does not need to track byte offsets
/// itself for this kind of download.
public class DepotDownloadRequest
{
    public required string AppId { get; init; }

    /// Optional: omit to let DepotDownloader download all depots for the manifest/app.
    public string? DepotId { get; init; }

    public required string ManifestId { get; init; }
    public required string InstallDirectory { get; init; }
    public SteamLoginMethod LoginMethod { get; init; } = SteamLoginMethod.QrCode;

    /// Only used when LoginMethod == Credentials.
    public string? SteamUsername { get; init; }
}

public enum SteamLoginMethod
{
    QrCode,
    Credentials
}

/// Progress update emitted while a DepotDownloader process is running.
public class DepotDownloadProgress
{
    public double PercentComplete { get; init; }
    public string StatusLine { get; init; } = string.Empty;
}