using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Checks GitHub Releases for a newer build of the launcher itself, downloads
/// and extracts it, then hands off to a small helper .bat script that waits
/// for this process to exit, copies the new files over the install
/// directory, and relaunches - a running .exe can't overwrite its own file
/// on Windows, so this indirect hand-off is the standard way to self-update.
/// </summary>
public class SelfUpdateService
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/Jaykmeit/XenoSync-Launcher/releases/latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static SelfUpdateService()
    {
        // GitHub's API requires a User-Agent header on every request, or it rejects the call outright.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("XenoSyncLauncher");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Returns the latest published release's version (tag with a leading "v"/"V" stripped, if any) and its .zip asset's direct download URL. Null/null if there's no release, no .zip asset, or the check failed.</summary>
    public async Task<(string? Version, string? DownloadUrl)> CheckForUpdateAsync(CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(ReleasesApiUrl, token);
            if (!response.IsSuccessStatusCode) return (null, null);

            var json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return (null, null);

            var version = tag.TrimStart('v', 'V');

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (name is not null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                        break;
                    }
                }
            }

            return (version, downloadUrl);
        }
        catch
        {
            // Network hiccup, rate limit, malformed response, etc. - treat as "couldn't check", not "no update".
            return (null, null);
        }
    }

    /// <summary>Same simple string-equality convention used elsewhere in this codebase for XV2Patcher/Revamp version comparisons - not real semver parsing.</summary>
    public static bool IsNewerVersion(string? latestVersion) =>
        !string.IsNullOrWhiteSpace(latestVersion) &&
        !string.Equals(latestVersion, LauncherVersion.Current, StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the release's .zip and extracts it to a scratch folder. Returns the extracted folder's path, or null on failure.</summary>
    public async Task<string?> DownloadAndExtractAsync(string downloadUrl, IProgress<(long BytesReceived, long? TotalBytes)>? progress, CancellationToken token)
    {
        try
        {
            var scratchDir = Path.Combine(Path.GetTempPath(), "XenoSyncLauncher", "SelfUpdate");
            Directory.CreateDirectory(scratchDir);
            var zipPath = Path.Combine(scratchDir, "update.zip");
            var extractDir = Path.Combine(scratchDir, "extracted");

            using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                if (!response.IsSuccessStatusCode) return null;

                var totalBytes = response.Content.Headers.ContentLength;
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
                await using var contentStream = await response.Content.ReadAsStreamAsync(token);

                var buffer = new byte[81920];
                long bytesReceived = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, token)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), token);
                    bytesReceived += read;
                    progress?.Report((bytesReceived, totalBytes));
                }
            }

            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            return extractDir;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes and launches a helper .bat script that: waits for this process
    /// to exit, copies every file from extractedNewVersionDir over
    /// installDir, relaunches the app, then deletes itself - then exits this
    /// process immediately so the script's wait condition is met.
    /// </summary>
    public void ApplyUpdateAndRestart(string extractedNewVersionDir, string installDir, string exeFileName)
    {
        Directory.CreateDirectory(installDir);

        var scriptDir = Path.Combine(Path.GetTempPath(), "XenoSyncLauncher", "SelfUpdate");
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, "apply_update.bat");
        var currentPid = Environment.ProcessId;
        var exePath = Path.Combine(installDir, exeFileName);

        // /FI "PID eq N" + find keeps looping (with a 1s wait) until tasklist
        // no longer reports this process - i.e. until it's actually exited,
        // not just "about to exit" - before touching any of its files.
        var script = $"""
            @echo off
            :wait
            tasklist /FI "PID eq {currentPid}" 2>nul | find "{currentPid}" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto wait
            )
            xcopy /E /Y /I "{extractedNewVersionDir}\*" "{installDir}\" >nul
            start "" "{exePath}"
            (goto) 2>nul & del "%~f0"
            """;

        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo(scriptPath)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        });

        Environment.Exit(0);
    }
}