using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSyncLauncher.Services;

/// Downloads and installs DepotDownloader (https://github.com/SteamRE/DepotDownloader)
/// to a fixed default location, so the Wizard can offer a one-click "install it
/// for me" option instead of requiring the user to find and download it manually.
/// The user can still Browse to a different existing copy instead, both here
/// and later in Settings.
///
/// TODO: this matches release assets by looking for "win-x64"/"windows-x64" in
/// the file name, based on DepotDownloader's current release naming. If the
/// project renames its release assets, this matching logic will need updating.
public class DepotDownloaderInstaller
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/SteamRE/DepotDownloader/releases/latest";

    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XenoSyncLauncher", "DepotDownloader");

    public static string DefaultExecutablePath => Path.Combine(DefaultInstallDirectory, "DepotDownloader.exe");

    public bool IsInstalledAt(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public async Task<(bool Success, string? ErrorMessage)> InstallDefaultAsync(IProgress<string>? statusProgress, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(DefaultInstallDirectory);

            using var http = new HttpClient();
            // GitHub's API requires a User-Agent header on every request.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("XenoSyncLauncher");

            statusProgress?.Report("Checking latest DepotDownloader release...");
            var releaseJson = await http.GetStringAsync(LatestReleaseApiUrl, cancellationToken);

            using var doc = JsonDocument.Parse(releaseJson);
            var assets = doc.RootElement.GetProperty("assets").EnumerateArray();

            var asset = assets.FirstOrDefault(a =>
            {
                var name = a.GetProperty("name").GetString() ?? string.Empty;
                return name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("windows-x64", StringComparison.OrdinalIgnoreCase);
            });

            if (asset.ValueKind == JsonValueKind.Undefined)
                return (false, "Could not find a Windows x64 build in the latest DepotDownloader release. You may need to download it manually.");

            var downloadUrl = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(downloadUrl))
                return (false, "The matched release asset had no download URL.");

            statusProgress?.Report("Downloading DepotDownloader...");
            var zipPath = Path.Combine(Path.GetTempPath(), "XenoSyncLauncher_DepotDownloader.zip");

            await using (var responseStream = await http.GetStreamAsync(downloadUrl, cancellationToken))
            await using (var fileStream = File.Create(zipPath))
            {
                await responseStream.CopyToAsync(fileStream, cancellationToken);
            }

            statusProgress?.Report("Extracting DepotDownloader...");
            ZipFile.ExtractToDirectory(zipPath, DefaultInstallDirectory, overwriteFiles: true);
            File.Delete(zipPath);

            if (!File.Exists(DefaultExecutablePath))
                return (false, $"Extraction finished but '{DefaultExecutablePath}' was not found. The release archive's layout may have changed.");

            statusProgress?.Report("DepotDownloader installed.");
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "Installation cancelled.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
