using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Downloads, extracts, and installs a mod into a Modded folder.
///
/// Extraction and installation are two separate steps: extraction just gets
/// the mod's raw files onto disk (into moddedPath/XenoSync/DownloadedMods/{id},
/// so they live alongside the actual game install and can be reused for a
/// later Reinstall without re-downloading). What happens next depends on
/// what's actually inside:
///   - .x2m file(s) present  -> installed via XV2INS (requires XV2Patcher
///     already installed - XV2INS relies on files it sets up).
///   - .exe file(s), no .x2m -> run as a self-installer.
///   - neither                -> "loose files" mod: every extracted file is
///     just copied directly into the Modded folder (the old behavior, still
///     correct for mods that ship as plain drop-in files).
///
/// For the .exe/.x2m cases we don't get a manifest of what was written, so a
/// snapshot of the Modded folder's file list is taken before and after
/// running the installer and diffed - the new files become
/// mod.InstalledRelativeFiles, same as the loose-files case, so Disable()
/// (and therefore Uninstall) works identically no matter which install
/// method was used.
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

    /// <summary>Where a mod's extracted files live, inside the actual Modded folder rather than a launcher-private cache - keeps them catalogued/reusable for Reinstall without re-downloading.</summary>
    private static string RepositoryFolderFor(string moddedPath, string modId) =>
        Path.Combine(moddedPath, "XenoSync", "DownloadedMods", modId);

    /// <summary>Downloads+extracts the mod if needed (all parts, for multi-volume archives), then copies its files into moddedPath.</summary>
    public async Task<(bool Success, string? ErrorMessage)> EnableAsync(
        ModRecord mod, string moddedPath, IProgress<DownloadProgressInfo>? downloadProgress, double? speedLimitMbps,
        Action<string>? onStatus, CancellationToken cancellationToken)
    {
        var (extracted, extractError) = await EnsureExtractedAsync(mod, moddedPath, downloadProgress, speedLimitMbps, onStatus, cancellationToken);
        if (!extracted) return (false, extractError);

        return await InstallExtractedModAsync(mod, mod.RepositoryFolder!, moddedPath, onStatus, cancellationToken);
    }

    /// <summary>The download+extract half of EnableAsync, split out so InstallBatchAsync can extract every pending mod first and only then decide how to install them (grouping the .x2m ones together).</summary>
    private async Task<(bool Success, string? ErrorMessage)> EnsureExtractedAsync(
        ModRecord mod, string moddedPath, IProgress<DownloadProgressInfo>? downloadProgress, double? speedLimitMbps,
        Action<string>? onStatus, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(mod.RepositoryFolder) && Directory.Exists(mod.RepositoryFolder))
            return (true, null);

        if (mod.DownloadUrls.Count == 0)
            return (false, "No download URL is configured for this mod.");

        var partsDir = Path.Combine(Path.GetTempPath(), "XenoSyncLauncher", "Mods", mod.Id);
        Directory.CreateDirectory(partsDir);
        var partFiles = new List<string>();

        for (int i = 0; i < mod.DownloadUrls.Count; i++)
        {
            var url = mod.DownloadUrls[i];

            if (MediaFireLinkResolver.IsShareLink(url))
            {
                onStatus?.Invoke(mod.DownloadUrls.Count > 1
                    ? $"Resolving MediaFire link for part {i + 1} of {mod.DownloadUrls.Count}..."
                    : "Resolving MediaFire download link...");

                var resolvedUrl = await MediaFireLinkResolver.ResolveDirectDownloadUrlAsync(url, cancellationToken, onStatus);
                if (resolvedUrl is null)
                    return (false, $"Couldn't resolve the MediaFire download link for {mod.Title} (part {i + 1}). The mod may have been taken down, or MediaFire changed their page - this needs a fresh link in the catalog.");

                url = resolvedUrl;
            }

            if (mod.DownloadUrls.Count > 1)
                onStatus?.Invoke($"Downloading part {i + 1} of {mod.DownloadUrls.Count}...");

            // Don't trust the URL for the filename: many hosts (e.g. Patreon's
            // /file?h=..&m=.. links) resolve every part to the exact same path,
            // which would make every part overwrite the previous one and leave
            // SharpCompress trying to read a "multi-volume" RAR that's really
            // just the last part's bytes under one name (IncompleteArchiveException).
            // Download to a scratch name first, then rename using the part index
            // and the file's real detected kind, so we get a guaranteed-unique
            // "{id}.partNN.rar" per part - the pattern ChoosePrimaryArchivePart
            // and SharpCompress's multi-volume RAR reader both expect.
            var scratchPath = Path.Combine(partsDir, $"{mod.Id}.part{i + 1:00}.download");

            var (downloaded, error) = await _httpDownloadService.DownloadAsync(
                url, scratchPath, downloadProgress ?? new Progress<DownloadProgressInfo>(), speedLimitMbps, cancellationToken);

            if (!downloaded) return (false, $"Failed to download part {i + 1} of {mod.DownloadUrls.Count}: {error}");

            var partPath = FinalizePartFileName(scratchPath);
            partFiles.Add(partPath);
        }

        var primaryPart = ChoosePrimaryArchivePart(partFiles);
        var kind = _archiveExtractionService.DetectKind(primaryPart);
        if (kind == ArchiveKind.Unknown)
            return (false, "The downloaded mod file(s) don't look like a recognized ZIP/RAR archive.");

        var repositoryFolder = RepositoryFolderFor(moddedPath, mod.Id);
        onStatus?.Invoke("Extracting...");
        await Task.Run(() => _archiveExtractionService.Extract(primaryPart, repositoryFolder, (_, _) => { }), cancellationToken);
        mod.RepositoryFolder = repositoryFolder;
        return (true, null);
    }

    /// <summary>
    /// Extracts every mod first, then installs them - grouping every mod
    /// whose install method turns out to be X2M into a single shared XV2INS
    /// invocation (all their .x2m files passed as one combined argument
    /// list) instead of one XV2INS confirmation per mod. Loose-files and
    /// .exe-installer mods are still installed one at a time since batching
    /// only helps with XV2INS's own per-launch confirmation dialog.
    ///
    /// Trade-off: XV2INS doesn't tell us which resulting file came from
    /// which .x2m, so a single before/after snapshot around the whole batch
    /// is the best available signal - every mod in that batch gets recorded
    /// as having written the *same* combined set of new files. This means
    /// Uninstalling any one mod from a batch removes every file the whole
    /// batch produced together, not just that mod's own share. Install mods
    /// separately (one at a time) instead of via this batch path if you need
    /// precise per-mod Uninstall.
    /// </summary>
    public async Task<Dictionary<string, (bool Success, string? ErrorMessage)>> InstallBatchAsync(
        List<ModRecord> mods, string moddedPath, IProgress<DownloadProgressInfo>? downloadProgress, double? speedLimitMbps,
        Action<string>? onStatus, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, (bool, string?)>();
        var x2mGroup = new List<(ModRecord Mod, List<string> X2mFiles, string ExtractedFolder)>();

        foreach (var mod in mods)
        {
            try
            {
                var (extracted, extractError) = await EnsureExtractedAsync(mod, moddedPath, downloadProgress, speedLimitMbps, onStatus, cancellationToken);
                if (!extracted)
                {
                    results[mod.Id] = (false, extractError);
                    continue;
                }

                if (IsNightContonCity(mod))
                {
                    results[mod.Id] = await InstallNightContonCityAsync(mod, mod.RepositoryFolder!, moddedPath, onStatus, cancellationToken);
                    continue;
                }

                var method = DetectInstallMethod(mod.RepositoryFolder!, out var installerFiles);
                if (method == ModInstallMethod.X2M && IsLazybones(mod))
                    installerFiles = SelectLazybonesX2mFiles(mod.RepositoryFolder!);

                switch (method)
                {
                    case ModInstallMethod.X2M:
                        x2mGroup.Add((mod, installerFiles, mod.RepositoryFolder!));
                        break;
                    case ModInstallMethod.Executable:
                        results[mod.Id] = await InstallViaExecutableAsync(mod, installerFiles, moddedPath, onStatus, cancellationToken);
                        break;
                    default:
                        results[mod.Id] = InstallLooseFiles(mod, mod.RepositoryFolder!, moddedPath);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One mod's bug/timeout/unexpected exception must not abort
                // the rest of the batch - every other mod (and the x2m group
                // that runs after this loop) still needs its own chance.
                results[mod.Id] = (false, $"Unexpected error installing {mod.Title}: {ex.Message}");
            }
        }

        if (x2mGroup.Count > 0)
        {
            try
            {
                var batchResults = await InstallX2mGroupAsync(x2mGroup, moddedPath, onStatus, cancellationToken);
                foreach (var (id, result) in batchResults)
                    results[id] = result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var error = $"Unexpected error installing the .x2m batch: {ex.Message}";
                foreach (var (mod, _, _) in x2mGroup)
                    results[mod.Id] = (false, error);
            }
        }

        return results;
    }

    /// <summary>The actual shared-XV2INS-call logic used by InstallBatchAsync for every mod whose install method is X2M.</summary>
    private async Task<Dictionary<string, (bool Success, string? ErrorMessage)>> InstallX2mGroupAsync(
        List<(ModRecord Mod, List<string> X2mFiles, string ExtractedFolder)> group, string moddedPath, Action<string>? onStatus, CancellationToken token)
    {
        var results = new Dictionary<string, (bool, string?)>();

        var xv2insPath = Path.Combine(moddedPath, "XV2INS.exe");
        if (!System.IO.File.Exists(xv2insPath))
        {
            const string error = "XV2INS isn't installed in the Modded folder - it's required to install .x2m mods. Run an Update first to set it up.";
            foreach (var (mod, _, _) in group) results[mod.Id] = (false, error);
            return results;
        }

        if (!X2mRegistryAssociationService.IsX2mAssociated())
        {
            const string error = "The .x2m file association isn't registered (x2i7394.tmp.reg may not have been imported yet, or was reset). Run an Update first to set it up.";
            foreach (var (mod, _, _) in group) results[mod.Id] = (false, error);
            return results;
        }

        if (!System.IO.File.Exists(Path.Combine(moddedPath, "XV2PATCHER", "xv2patcher.ini")))
        {
            foreach (var (mod, _, _) in group) results[mod.Id] = (false, $"{mod.Title} needs XV2Patcher installed first.");
            return results;
        }

        var before = SnapshotRelativeFiles(moddedPath);

        var allX2mFiles = group.SelectMany(g => g.X2mFiles).ToList();
        onStatus?.Invoke($"Installing {allX2mFiles.Count} .x2m file(s) across {group.Count} mod(s) in one XV2INS pass...");

        var arguments = string.Join(' ', allX2mFiles.Select(f => $"\"{f}\""));
        using (var process = Process.Start(new ProcessStartInfo(xv2insPath, arguments) { UseShellExecute = true, WorkingDirectory = moddedPath }))
        {
            if (process is null)
            {
                const string error = "Couldn't start XV2INS.";
                foreach (var (mod, _, _) in group) results[mod.Id] = (false, error);
                return results;
            }
            await process.WaitForExitAsync(token);
        }

        // Companion .exe(s) (e.g. Lazybones' hair repairer) are per-mod, run after the shared XV2INS pass.
        foreach (var (mod, _, extractedFolder) in group)
        {
            foreach (var exe in Directory.GetFiles(extractedFolder, "*.exe", SearchOption.TopDirectoryOnly))
            {
                onStatus?.Invoke($"Running {Path.GetFileName(exe)} for {mod.Title}...");
                using var exeProcess = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = extractedFolder });
                if (exeProcess is not null) await exeProcess.WaitForExitAsync(token);
            }
        }

        var newFiles = SnapshotRelativeFiles(moddedPath).Except(before).ToList();
        if (newFiles.Count == 0)
        {
            const string error = "XV2INS closed, but no new files showed up for this batch - the install may not have completed.";
            foreach (var (mod, _, _) in group) results[mod.Id] = (false, error);
            return results;
        }

        // See the class-level trade-off note on InstallBatchAsync: every mod
        // in this batch is recorded as having written the same shared set of
        // new files, since XV2INS doesn't tell us which came from which .x2m.
        foreach (var (mod, _, _) in group)
        {
            mod.InstalledRelativeFiles = newFiles;
            mod.IsEnabled = true;
            results[mod.Id] = (true, null);
        }

        return results;
    }

    /// <summary>Which install method a mod's extracted files call for, detected by what's actually in them (see class docs).</summary>
    public enum ModInstallMethod { LooseFiles, Executable, X2M }

    /// <summary>Looks at what's inside an already-extracted mod folder to decide how it needs to be installed.</summary>
    public static ModInstallMethod DetectInstallMethod(string extractedFolder, out List<string> installerFiles)
    {
        var x2mFiles = Directory.GetFiles(extractedFolder, "*.x2m", SearchOption.AllDirectories).ToList();
        if (x2mFiles.Count > 0)
        {
            installerFiles = x2mFiles;
            return ModInstallMethod.X2M;
        }

        var exeFiles = Directory.GetFiles(extractedFolder, "*.exe", SearchOption.AllDirectories).ToList();
        if (exeFiles.Count > 0)
        {
            installerFiles = exeFiles;
            return ModInstallMethod.Executable;
        }

        installerFiles = new List<string>();
        return ModInstallMethod.LooseFiles;
    }

    /// <summary>
    /// Installs a mod from its already-extracted folder - the real
    /// "installation" step, as opposed to just having the files on disk.
    /// Used both right after a fresh extraction and for a standalone
    /// "Reinstall" (re-running install against files that are already there,
    /// no re-download/re-extract needed).
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> InstallExtractedModAsync(
        ModRecord mod, string extractedFolder, string moddedPath, Action<string>? onStatus, CancellationToken cancellationToken)
    {
        // Night Conton City's archive has an "Install First"/"Install Second"
        // structure - see InstallNightContonCityAsync for the two-step,
        // Halloween-asset-filtered install.
        if (IsNightContonCity(mod))
            return await InstallNightContonCityAsync(mod, extractedFolder, moddedPath, onStatus, cancellationToken);

        var method = DetectInstallMethod(extractedFolder, out var installerFiles);

        // Lazybones' archive has dozens of .x2m variants with duplicate
        // filenames spread across several folders (baseline, "No Health
        // Requirement", "Dynamic Transformations", "Moveset Swap" bundles,
        // and a "Patches (OLD) - use at your own risk" tree) - installing
        // everything would both take forever (constant XV2INS confirmations)
        // and install mutually-exclusive variants of the same transformation
        // on top of each other. See SelectLazybonesX2mFiles for the actual
        // curation rules.
        if (method == ModInstallMethod.X2M && IsLazybones(mod))
            installerFiles = SelectLazybonesX2mFiles(extractedFolder);

        return method switch
        {
            ModInstallMethod.X2M => await InstallViaX2mAsync(mod, installerFiles, extractedFolder, moddedPath, onStatus, cancellationToken),
            ModInstallMethod.Executable => await InstallViaExecutableAsync(mod, installerFiles, moddedPath, onStatus, cancellationToken),
            _ => InstallLooseFiles(mod, extractedFolder, moddedPath)
        };
    }

    private static bool IsNightContonCity(ModRecord mod) =>
        mod.Id.Contains("night-conton-city", StringComparison.OrdinalIgnoreCase) ||
        (mod.Title.Contains("Conton City", StringComparison.OrdinalIgnoreCase) && mod.Title.Contains("Night", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Night Conton City ships a two-step manual install, marked by
    /// "Install First"/"Install Second" folders in its archive:
    ///   1. "Install First" contains a nested .rar (with an .exe installer -
    ///      "Halloween in Conton City.exe" - plus a .installinfo file
    ///      alongside it). Extract that .rar, then run its .exe.
    ///   2. "Install Second" contains a .x2m fix ("Conton City (Night)_Fix
    ///      4.x2m") that must be installed via XV2INS *after* step 1
    ///      finishes, not before.
    /// </summary>
    /// <summary>
    /// Night Conton City's archive has "Install First"/"Install Second"
    /// folders, but "Install First" (a nested .rar with "Halloween in Conton
    /// City.exe") turned out to add unwanted seasonal Halloween assets - so
    /// it's deliberately skipped. Only "Install Second" (the actual
    /// "Conton City (Night)_Fix 4.x2m" fix) gets installed.
    /// </summary>
    /// <summary>
    /// Night Conton City's "Install First" (a nested .rar with "Halloween in
    /// Conton City.exe") is needed for the mod to work fully, but some of
    /// what it installs is Halloween-seasonal assets the user doesn't want.
    /// Snapshots step 1 (the exe) separately from step 2 (the .x2m fix) so
    /// UnwantedNightContonCityAssets can be applied to just step 1's output -
    /// matching files get deleted right back out and excluded from
    /// InstalledRelativeFiles, without discarding the rest of what step 1
    /// installed (which the mod still needs).
    /// </summary>
    private async Task<(bool Success, string? ErrorMessage)> InstallNightContonCityAsync(
        ModRecord mod, string extractedFolder, string moddedPath, Action<string>? onStatus, CancellationToken token)
    {
        var installFirstDir = Directory.GetDirectories(extractedFolder, "Install First", SearchOption.AllDirectories).FirstOrDefault();
        var installSecondDir = Directory.GetDirectories(extractedFolder, "Install Second", SearchOption.AllDirectories).FirstOrDefault();
        if (installFirstDir is null || installSecondDir is null)
            return (false, $"{mod.Title}'s archive doesn't have the expected 'Install First'/'Install Second' folders - its layout may have changed.");

        if (!System.IO.File.Exists(Path.Combine(moddedPath, "XV2PATCHER", "xv2patcher.ini")))
            return (false, $"{mod.Title} needs XV2Patcher installed first.");

        var xv2insPath = Path.Combine(moddedPath, "XV2INS.exe");
        if (!System.IO.File.Exists(xv2insPath))
            return (false, "XV2INS isn't installed in the Modded folder - it's required to install .x2m mods. Run an Update first to set it up.");

        if (!X2mRegistryAssociationService.IsX2mAssociated())
            return (false, "The .x2m file association isn't registered (x2i7394.tmp.reg may not have been imported yet, or was reset). Run an Update first to set it up.");

        // Step 1: extract the nested .rar, then run the .exe installer inside it.
        var nestedRar = Directory.GetFiles(installFirstDir, "*.rar", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (nestedRar is null)
            return (false, $"Couldn't find the nested .rar inside {mod.Title}'s 'Install First' folder.");

        var step1ExtractDir = Path.Combine(Path.GetTempPath(), "XenoSyncLauncher", "Mods", mod.Id, "InstallFirst");
        onStatus?.Invoke($"Extracting {Path.GetFileName(nestedRar)}...");
        await Task.Run(() => _archiveExtractionService.Extract(nestedRar, step1ExtractDir, (_, _) => { }), token);

        var installerExe = Directory.GetFiles(step1ExtractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (installerExe is null)
            return (false, $"Couldn't find an .exe installer inside {mod.Title}'s extracted 'Install First' archive.");

        var beforeStep1 = SnapshotRelativeFiles(moddedPath);
        onStatus?.Invoke($"Running {Path.GetFileName(installerExe)}...");
        using (var process = Process.Start(new ProcessStartInfo(installerExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installerExe) }))
        {
            if (process is null) return (false, $"Couldn't start '{Path.GetFileName(installerExe)}'.");
            await process.WaitForExitAsync(token);
        }

        var step1Files = SnapshotRelativeFiles(moddedPath).Except(beforeStep1).ToList();
        onStatus?.Invoke($"'{Path.GetFileName(installerExe)}' wrote {step1Files.Count} file(s): {string.Join(", ", step1Files.Take(20))}{(step1Files.Count > 20 ? ", ..." : "")}");

        var hstDir = Path.Combine(moddedPath, "data", "chara", "HST");
        if (Directory.Exists(hstDir))
        {
            onStatus?.Invoke("Removing unwanted Halloween character assets (data/chara/HST)...");
            try { Directory.Delete(hstDir, recursive: true); }
            catch (Exception ex) { onStatus?.Invoke($"Couldn't fully remove data/chara/HST: {ex.Message}"); }
        }

        var keptStep1Files = step1Files.Where(f => !IsUnwantedNightContonCityAsset(f)).ToList();

        // Step 2: install the .x2m fix from "Install Second", via XV2INS - after step 1, never before.
        var x2mFiles = Directory.GetFiles(installSecondDir, "*.x2m", SearchOption.AllDirectories).ToList();
        if (x2mFiles.Count == 0)
            return (false, $"Couldn't find a .x2m file inside {mod.Title}'s 'Install Second' folder.");

        var beforeStep2 = SnapshotRelativeFiles(moddedPath); // taken after step 1's cleanup, so deleted files don't get re-counted
        onStatus?.Invoke($"Installing {string.Join(", ", x2mFiles.Select(Path.GetFileName))}...");
        var arguments = string.Join(' ', x2mFiles.Select(f => $"\"{f}\""));
        using (var xv2insProcess = Process.Start(new ProcessStartInfo(xv2insPath, arguments) { UseShellExecute = true, WorkingDirectory = moddedPath }))
        {
            if (xv2insProcess is null) return (false, $"Couldn't start XV2INS for {mod.Title}.");
            await xv2insProcess.WaitForExitAsync(token);
        }
        var step2Files = SnapshotRelativeFiles(moddedPath).Except(beforeStep2).ToList();

        var newFiles = keptStep1Files.Concat(step2Files).ToList();
        if (newFiles.Count == 0)
            return (false, $"{mod.Title}'s install finished, but no new files showed up in the Modded folder - it may not have completed.");

        mod.InstalledRelativeFiles = newFiles;
        mod.IsEnabled = true;
        return (true, null);
    }

    /// <summary>
    /// Halloween-seasonal character assets ("HST") from "Install First" that
    /// the user confirmed they don't want - the whole data/chara/HST folder
    /// gets filtered out of what's kept/tracked, everything else "Install
    /// First" writes is kept since the mod still needs it to function.
    /// </summary>
    private static bool IsUnwantedNightContonCityAsset(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith("data/chara/HST/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("data/chara/HST", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLazybones(ModRecord mod) =>
        mod.Id.Contains("lazybones", StringComparison.OrdinalIgnoreCase) ||
        mod.Title.Contains("lazybones", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Curates which of Lazybones' many .x2m variants actually get
    /// installed, per two rules:
    ///   1. "Install First (LB Dependencies!).x2m" always goes in, first -
    ///      it's a prerequisite package, not a transformation variant.
    ///   2. For every other .x2m, when the same filename exists in more than
    ///      one folder (a "duplicate"), pick the best copy - see
    ///      LazybonesVariantRank for the actual priority order.
    /// "Patches (OLD) - use at your own risk" and "Moveset Swap" content are
    /// excluded entirely - only one moveset swap can actually be active
    /// in-game at a time, so it's not something to install automatically.
    /// </summary>
    private static List<string> SelectLazybonesX2mFiles(string extractedFolder)
    {
        var allX2m = Directory.GetFiles(extractedFolder, "*.x2m", SearchOption.AllDirectories);

        var eligible = allX2m.Where(f =>
            !f.Contains("Patches (OLD)", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains("Moveset Swap", StringComparison.OrdinalIgnoreCase)).ToList();

        var installFirst = eligible.Where(f => Path.GetFileName(f).Contains("Install First", StringComparison.OrdinalIgnoreCase)).ToList();

        var bestPerName = eligible.Except(installFirst)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(LazybonesVariantRank).First());

        return installFirst.Concat(bestPerName).ToList();
    }

    /// <summary>
    /// Priority order for duplicate-named .x2m variants: "Dynamic
    /// Transformations" (affects hair dynamically) is preferred over the
    /// plain baseline; "No Health Requirement" is a secondary preference on
    /// top of that.
    /// </summary>
    private static int LazybonesVariantRank(string path)
    {
        var rank = 0;
        if (path.Contains("Dynamic Transformations", StringComparison.OrdinalIgnoreCase)) rank += 2;
        if (path.Contains("No Health Requirement", StringComparison.OrdinalIgnoreCase)) rank += 1;
        return rank;
    }

    /// <summary>The original behavior: every extracted file is just copied as-is into the Modded folder. Correct for mods that ship as plain drop-in files, no installer.</summary>
    private (bool Success, string? ErrorMessage) InstallLooseFiles(ModRecord mod, string extractedFolder, string moddedPath)
    {
        var written = new List<string>();

        foreach (var file in Directory.GetFiles(extractedFolder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extractedFolder, file);
            var destination = Path.Combine(moddedPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            System.IO.File.Copy(file, destination, overwrite: true);
            written.Add(relative);
        }

        mod.InstalledRelativeFiles = written;
        mod.IsEnabled = true;
        return (true, null);
    }

    /// <summary>Runs a mod's self-installer .exe(s), then diffs the Modded folder's file list before/after to learn what it actually wrote (installers don't hand back a manifest).</summary>
    private async Task<(bool Success, string? ErrorMessage)> InstallViaExecutableAsync(
        ModRecord mod, List<string> exeFiles, string moddedPath, Action<string>? onStatus, CancellationToken token)
    {
        var before = SnapshotRelativeFiles(moddedPath);

        foreach (var exe in exeFiles)
        {
            onStatus?.Invoke($"Running installer: {Path.GetFileName(exe)}...");
            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            });
            if (process is null) return (false, $"Couldn't start '{Path.GetFileName(exe)}'.");
            await process.WaitForExitAsync(token);
        }

        var newFiles = SnapshotRelativeFiles(moddedPath).Except(before).ToList();
        if (newFiles.Count == 0)
            return (false, $"{mod.Title}'s installer closed, but no new files showed up in the Modded folder - the install may not have completed.");

        mod.InstalledRelativeFiles = newFiles;
        mod.IsEnabled = true;
        return (true, null);
    }

    /// <summary>
    /// Installs .x2m file(s) via a single XV2INS.exe invocation with every
    /// file passed as its own argument - dragging multiple .x2m files onto
    /// XV2INS triggers its batch/"silent mod" install mode for all of them
    /// at once (one confirmation instead of one per file), and passing them
    /// as separate command-line arguments is the same thing a drag-and-drop
    /// does under the hood.
    ///
    /// Used for the single-mod path (EnableAsync/ReinstallAsync via a
    /// checkbox toggle or the Reinstall button). InstallBatchAsync has its
    /// own equivalent (InstallX2mGroupAsync) that batches *across* mods too.
    ///
    /// Afterward, runs any companion .exe sitting at the archive's top level
    /// (not nested in a data/support subfolder) - some mods (Lazybones'
    /// "Revamp Dynamic Hair Repairer") ship a finishing step that needs to
    /// run once the .x2m content is actually in place, rather than being an
    /// installer for separate content of its own.
    /// </summary>
    private async Task<(bool Success, string? ErrorMessage)> InstallViaX2mAsync(
        ModRecord mod, List<string> x2mFiles, string extractedFolder, string moddedPath, Action<string>? onStatus, CancellationToken token)
    {
        var xv2insPath = Path.Combine(moddedPath, "XV2INS.exe");
        if (!System.IO.File.Exists(xv2insPath))
            return (false, "XV2INS isn't installed in the Modded folder - it's required to install .x2m mods. Run an Update first to set it up.");

        if (!X2mRegistryAssociationService.IsX2mAssociated())
            return (false, "The .x2m file association isn't registered (x2i7394.tmp.reg may not have been imported yet, or was reset). Run an Update first to set it up.");

        if (!System.IO.File.Exists(Path.Combine(moddedPath, "XV2PATCHER", "xv2patcher.ini")))
            return (false, $"{mod.Title} needs XV2Patcher installed first.");

        var before = SnapshotRelativeFiles(moddedPath);

        onStatus?.Invoke(x2mFiles.Count > 1
            ? $"Installing {x2mFiles.Count} .x2m files for {mod.Title}..."
            : $"Installing {Path.GetFileName(x2mFiles[0])}...");

        var arguments = string.Join(' ', x2mFiles.Select(f => $"\"{f}\""));
        using var process = Process.Start(new ProcessStartInfo(xv2insPath, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = moddedPath
        });
        if (process is null) return (false, $"Couldn't start XV2INS for {mod.Title}.");
        await process.WaitForExitAsync(token);

        foreach (var exe in Directory.GetFiles(extractedFolder, "*.exe", SearchOption.TopDirectoryOnly))
        {
            onStatus?.Invoke($"Running {Path.GetFileName(exe)}...");
            using var exeProcess = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = extractedFolder });
            if (exeProcess is not null) await exeProcess.WaitForExitAsync(token);
        }

        var newFiles = SnapshotRelativeFiles(moddedPath).Except(before).ToList();
        if (newFiles.Count == 0)
            return (false, $"XV2INS closed, but no new files showed up for {mod.Title} - the install may not have completed.");

        mod.InstalledRelativeFiles = newFiles;
        mod.IsEnabled = true;
        return (true, null);
    }

    /// <summary>Relative paths of every file currently in moddedPath - used to diff what an opaque installer (.exe/.x2m via XV2INS) actually wrote, since neither hands back a manifest.</summary>
    private static HashSet<string> SnapshotRelativeFiles(string moddedPath)
    {
        if (!Directory.Exists(moddedPath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return Directory.GetFiles(moddedPath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(moddedPath, f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renames a just-downloaded part (still using its scratch ".download" name)
    /// to the correct extension based on its real content, detected via magic
    /// bytes rather than trusted from the URL/server. Keeps the "{id}.partNN"
    /// prefix so multi-volume RAR detection still works.
    /// </summary>
    private string FinalizePartFileName(string scratchPath)
    {
        var kind = _archiveExtractionService.DetectKind(scratchPath);
        var ext = kind == ArchiveKind.Zip ? ".zip" : ".rar"; // defaults to .rar: every multi-part mod seen so far is RAR

        var finalPath = Path.Combine(
            Path.GetDirectoryName(scratchPath)!,
            Path.GetFileNameWithoutExtension(scratchPath) + ext);

        if (System.IO.File.Exists(finalPath)) System.IO.File.Delete(finalPath);
        System.IO.File.Move(scratchPath, finalPath);
        return finalPath;
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

    /// <summary>Deletes exactly the files this mod is recorded as having written, then clears that record. Works the same regardless of which install method wrote them (loose files, .exe, or .x2m), since all three end up recorded the same way.</summary>
    public void Disable(ModRecord mod, string moddedPath)
    {
        foreach (var relative in mod.InstalledRelativeFiles)
        {
            var path = Path.Combine(moddedPath, relative);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }

        mod.InstalledRelativeFiles = new List<string>();
        mod.IsEnabled = false;
    }

    /// <summary>"Uninstall" button: same as Disable, just named for what a mod that came from a real installer more naturally reads as.</summary>
    public void Uninstall(ModRecord mod, string moddedPath) => Disable(mod, moddedPath);

    /// <summary>
    /// "Reinstall" button: removes whatever files are currently recorded for
    /// this mod, then re-runs installation from its already-extracted folder
    /// (no re-download/re-extract, unless that folder is missing - e.g. the
    /// user deleted XenoSync/DownloadedMods/{id} manually - in which case
    /// this reports that instead of silently doing nothing).
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> ReinstallAsync(
        ModRecord mod, string moddedPath, Action<string>? onStatus, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mod.RepositoryFolder) || !Directory.Exists(mod.RepositoryFolder))
            return (false, $"{mod.Title}'s extracted files aren't on disk anymore - re-download it instead of reinstalling.");

        Disable(mod, moddedPath);
        return await InstallExtractedModAsync(mod, mod.RepositoryFolder, moddedPath, onStatus, cancellationToken);
    }
}