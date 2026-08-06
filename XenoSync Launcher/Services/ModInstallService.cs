using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Applies/removes a mod's files in a Modded folder. If the mod hasn't been
/// downloaded yet (no RepositoryFolder), fetches it from ModRecord.DownloadUrls
/// first and keeps a clean extracted copy independent of any specific Modded
/// folder, so switching/reinstalling Modded doesn't require re-downloading.
///
/// Enabling copies every file from the mod's repository folder into the
/// Modded folder, recording the exact relative paths written. Disabling
/// deletes exactly those recorded paths - never a blind "delete everything
/// this mod might touch" - so other mods' files are never affected.
///
/// TODO / known limitation: if two mods both write the same relative path,
/// disabling whichever one wrote it last will delete the file even though
/// the other mod also "owns" it logically. Detecting and resolving real
/// file-level conflicts between mods is out of scope for now.
/// </summary>
public class ModInstallService
{
    private readonly HttpDownloadService _httpDownloadService;
    private readonly ArchiveExtractionService _archiveExtractionService;

    public ModInstallService(HttpDownloadService? httpDownloadService = null, ArchiveExtractionService? archiveExtractionService = null)
    {
        _httpDownloadService = httpDownloadService ?? new HttpDownloadService();
        _archiveExtractionService = archiveExtractionService ?? new ArchiveExtractionService();
    }

    private static string RepositoryRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XenoSyncLauncher", "ModsRepository");

    /// <summary>Downloads+extracts the mod if needed (all parts, for multi-volume archives), then copies its files into moddedPath.</summary>
    public async Task<(bool Success, string? ErrorMessage)> EnableAsync(
        ModRecord mod, string moddedPath, IProgress<DownloadProgressInfo>? downloadProgress, double? speedLimitMbps,
        Action<string>? onStatus, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mod.RepositoryFolder) || !Directory.Exists(mod.RepositoryFolder))
        {
            if (mod.DownloadUrls.Count == 0)
                return (false, "No download URL is configured for this mod.");

            var partsDir = Path.Combine(Path.GetTempPath(), "XenoSyncLauncher", "Mods", mod.Id);
            Directory.CreateDirectory(partsDir);
            var partFiles = new List<string>();

            for (int i = 0; i < mod.DownloadUrls.Count; i++)
            {
                var url = mod.DownloadUrls[i];
                var fileName = GetFileNameFromUrl(url) ?? $"part{i + 1}";
                var partPath = Path.Combine(partsDir, fileName);

                if (mod.DownloadUrls.Count > 1)
                    onStatus?.Invoke($"Downloading part {i + 1} of {mod.DownloadUrls.Count} ({fileName})...");

                var (downloaded, error) = await _httpDownloadService.DownloadAsync(
                    url, partPath, downloadProgress ?? new Progress<DownloadProgressInfo>(), speedLimitMbps, cancellationToken);

                if (!downloaded) return (false, $"Failed to download '{fileName}': {error}");

                partFiles.Add(partPath);
            }

            var primaryPart = ChoosePrimaryArchivePart(partFiles);
            var kind = _archiveExtractionService.DetectKind(primaryPart);
            if (kind == ArchiveKind.Unknown)
                return (false, "The downloaded mod file(s) don't look like a recognized ZIP/RAR archive.");

            var repositoryFolder = Path.Combine(RepositoryRoot, mod.Id);
            onStatus?.Invoke("Extracting...");
            await Task.Run(() => _archiveExtractionService.Extract(primaryPart, repositoryFolder, (_, _) => { }), cancellationToken);
            mod.RepositoryFolder = repositoryFolder;
        }

        Enable(mod, moddedPath);
        return (true, null);
    }

    private static string? GetFileNameFromUrl(string url)
    {
        try
        {
            var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
            return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Picks which downloaded part to hand to ArchiveFactory.Open - for RAR
    /// multi-volume archives this must be the first volume; SharpCompress then
    /// finds the remaining parts automatically as long as they're all in the
    /// same folder with their original names. Prefers "...part1.rar"-style
    /// naming, falls back to the file that isn't a ".rNN"/".zNN" continuation
    /// part (old-style multivolume), then just picks alphabetically first.
    /// </summary>
    private static string ChoosePrimaryArchivePart(List<string> partFiles)
    {
        if (partFiles.Count == 1) return partFiles[0];

        var part1 = partFiles.FirstOrDefault(f => Regex.IsMatch(Path.GetFileName(f), @"part0*1\b", RegexOptions.IgnoreCase));
        if (part1 is not null) return part1;

        var basePart = partFiles.FirstOrDefault(f => !Regex.IsMatch(Path.GetExtension(f), @"^\.[rz]\d+$", RegexOptions.IgnoreCase));
        if (basePart is not null) return basePart;

        return partFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First();
    }

    /// <summary>Copies every file from the mod's (already downloaded) repository folder into moddedPath, recording exactly what was written.</summary>
    public void Enable(ModRecord mod, string moddedPath)
    {
        if (string.IsNullOrWhiteSpace(mod.RepositoryFolder) || !Directory.Exists(mod.RepositoryFolder))
            throw new InvalidOperationException("This mod hasn't been downloaded yet.");

        var written = new List<string>();

        foreach (var file in Directory.GetFiles(mod.RepositoryFolder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(mod.RepositoryFolder, file);
            var destination = Path.Combine(moddedPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            written.Add(relative);
        }

        mod.InstalledRelativeFiles = written;
        mod.IsEnabled = true;
    }

    /// <summary>Deletes exactly the files this mod is recorded as having written, then clears that record.</summary>
    public void Disable(ModRecord mod, string moddedPath)
    {
        foreach (var relative in mod.InstalledRelativeFiles)
        {
            var path = Path.Combine(moddedPath, relative);
            if (File.Exists(path)) File.Delete(path);
        }

        mod.InstalledRelativeFiles = new List<string>();
        mod.IsEnabled = false;
    }
}