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
///   - mod.MergeTargetSubfolder is set -> merged into that existing
///     subfolder of the Modded folder instead of the root (see
///     InstallMergeIntoSubfolder) - used for mods whose real install
///     instructions are "drag this folder's contents into the matching
///     folder that's already there" (e.g. InviernoCreations' Chi-Chi DYT
///     pack, merged into "data/chara/CHI").
///   - .x2m file(s) present  -> installed via XV2INS (requires XV2Patcher
///     already installed - XV2INS relies on files it sets up). Some mods
///     (e.g. King Piccolo) are hosted as a raw .x2m file directly, with
///     nothing to extract at all - see EnsureExtractedAsync's URL-extension
///     check for how that's detected before archive handling even begins.
///   - .x2s file(s), no .x2m -> copied directly into "data/", flat (e.g.
///     Revamp Organized Slots) - not routed through XV2INS at all; Revamp
///     reads .x2s files straight out of data/ itself.
///   - .exe file(s), no .x2m/.x2s -> run as a self-installer.
///   - none of the above     -> "loose files" mod: every extracted file is
///     just copied directly into the Modded folder (the old behavior, still
///     correct for mods that ship as plain drop-in files).
///
/// For the .exe/.x2m cases we don't get a manifest of what was written, so a
/// snapshot of the Modded folder is taken before and after running the
/// installer and diffed - the touched files become mod.InstalledRelativeFiles,
/// same as the loose-files case, so Disable() (and therefore Uninstall) works
/// identically no matter which install method was used. That diff counts a
/// path as touched if it's new OR if it already existed but its last-write
/// time changed - see DiffAddedOrChangedFiles for why a "compatibility"/patch
/// mod that only overwrites existing files needs the latter half of that, and
/// SnapshotDiffWithRetryAsync for why it's retried with a short delay rather
/// than taken exactly once.
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
        Action<string>? onStatus, CancellationToken cancellationToken, bool isRepair = false)
    {
        var (extracted, extractError) = await EnsureExtractedAsync(mod, moddedPath, downloadProgress, speedLimitMbps, onStatus, cancellationToken);
        if (!extracted) return (false, extractError);

        return await InstallExtractedModAsync(mod, mod.RepositoryFolder!, moddedPath, onStatus, cancellationToken, isRepair);
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

            var partPath = FinalizePartFileName(scratchPath, url);
            partFiles.Add(partPath);
        }

        var primaryPart = ChoosePrimaryArchivePart(partFiles);

        // Some mods (e.g. King Piccolo) are hosted as a raw .x2m file
        // directly - not wrapped in a zip/rar at all. FinalizePartFileName
        // already trusts the source URL's own ".x2m" extension over
        // magic-byte detection for exactly this case (a .x2m isn't a zip/rar,
        // so DetectKind would otherwise just fall through to its ".rar"
        // default and mislabel it). There's nothing to "extract" here: the
        // downloaded file already IS the artifact XV2INS installs directly,
        // so it's copied straight into the repository folder instead of
        // being run through ArchiveExtractionService at all.
        if (string.Equals(Path.GetExtension(primaryPart), ".x2m", StringComparison.OrdinalIgnoreCase))
        {
            var x2mRepositoryFolder = RepositoryFolderFor(moddedPath, mod.Id);
            Directory.CreateDirectory(x2mRepositoryFolder);
            var x2mDestination = Path.Combine(x2mRepositoryFolder, Path.GetFileName(primaryPart));
            System.IO.File.Copy(primaryPart, x2mDestination, overwrite: true);
            mod.RepositoryFolder = x2mRepositoryFolder;
            return (true, null);
        }

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
    /// list) instead of one XV2INS confirmation per mod. Loose-files,
    /// merge-into-subfolder, .x2s, and .exe-installer mods are still
    /// installed one at a time since batching only helps with XV2INS's own
    /// per-launch confirmation dialog.
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
    /// <param name="isRepair">
    /// True when this batch is running as part of a Repair (see
    /// MainWindow.MarkAllEnabledModsForReinstall/StartUpdate), rather than a
    /// first-time install - passed through to InstallX2mGroupAsync, which
    /// only runs a mod's companion .exe (such as Lazybones' "Revamp Dynamic
    /// Hair Repairer") when this is true, UNLESS that mod is one that always
    /// needs its companion .exe run regardless (currently just Sparking
    /// Pack's UI preset installer - see IsSparkingPack). See
    /// InstallX2mGroupAsync's remarks for why.
    /// </param>
    public async Task<Dictionary<string, (bool Success, string? ErrorMessage)>> InstallBatchAsync(
        List<ModRecord> mods, string moddedPath, IProgress<DownloadProgressInfo>? downloadProgress, double? speedLimitMbps,
        Action<string>? onStatus, CancellationToken cancellationToken, bool isRepair = false)
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

                // Catalog-declared "merge this into an existing subfolder"
                // mods (e.g. Chi-Chi's DYT pack, merged into
                // data/chara/CHI) bypass the x2m/x2s/exe/loose-files
                // detection entirely - the catalog already says exactly how
                // they need to be installed, so there's nothing to infer
                // from what's inside the extracted folder.
                if (!string.IsNullOrWhiteSpace(mod.MergeTargetSubfolder))
                {
                    onStatus?.Invoke($"Merging {mod.Title} into '{mod.MergeTargetSubfolder}'...");
                    results[mod.Id] = InstallMergeIntoSubfolder(mod, mod.RepositoryFolder!, moddedPath);
                    continue;
                }

                var method = DetectInstallMethod(mod.RepositoryFolder!, out var installerFiles);
                if (method == ModInstallMethod.X2M)
                {
                    if (IsLazybones(mod)) installerFiles = SelectLazybonesX2mFiles(mod.RepositoryFolder!);
                    else if (IsSparkingPack(mod)) installerFiles = SelectSparkingPackX2mFiles(mod.RepositoryFolder!);
                }

                switch (method)
                {
                    case ModInstallMethod.X2M:
                        x2mGroup.Add((mod, installerFiles, mod.RepositoryFolder!));
                        break;
                    case ModInstallMethod.X2S:
                        onStatus?.Invoke($"Installing {mod.Title} into 'data/'...");
                        results[mod.Id] = InstallX2sFiles(mod, installerFiles, moddedPath);
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
                var batchResults = await InstallX2mGroupAsync(x2mGroup, moddedPath, onStatus, cancellationToken, isRepair);
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

    /// <summary>
    /// The actual shared-XV2INS-call logic used by InstallBatchAsync for
    /// every mod whose install method is X2M.
    /// </summary>
    /// <param name="isRepair">
    /// Gates whether each mod's companion .exe (e.g. Lazybones' "Revamp
    /// Dynamic Hair Repairer LBNT Colors.exe") runs after the shared XV2INS
    /// pass. That repairer is meant to fix up dynamic-hair state that only
    /// needs correcting on a Repair/reinstall pass - running it on a
    /// genuinely first-time install has nothing to "repair" yet and isn't
    /// wanted there, so it's skipped entirely unless isRepair is true.
    /// Sparking Pack's companion .exe (a UI preset installer, not a
    /// "repairer") is the exception - it's meant to run every time
    /// regardless, per IsSparkingPack below.
    /// </param>
    private async Task<Dictionary<string, (bool Success, string? ErrorMessage)>> InstallX2mGroupAsync(
        List<(ModRecord Mod, List<string> X2mFiles, string ExtractedFolder)> group, string moddedPath, Action<string>? onStatus, CancellationToken token, bool isRepair)
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

            // XV2INS's exit code is deliberately not treated as a
            // success/failure signal here - it's an older Windows installer
            // that doesn't reliably return 0 even on a completely normal
            // close, and trusting it produced false failures for batches
            // that had actually installed correctly. The newFiles check
            // below (did anything actually get written?) is the real,
            // reliable signal for whether this batch succeeded.
        }

        // Companion .exe(s) per-mod, run after the shared XV2INS pass. See
        // this method's <param> remarks: gated on isRepair for a mod like
        // Lazybones (whose companion .exe is a repair-only step), but always
        // run for a mod like Sparking Pack (whose companion .exe is a normal
        // installer step, not something that only makes sense post-repair).
        // Searches the whole extracted tree, not just the top level: some
        // mods' installer sits inside its own subfolder (e.g. Sparking
        // Pack's "3. Installer (Presets for UI-Sign and UI)/...exe").
        foreach (var (mod, _, extractedFolder) in group)
        {
            if (!isRepair && !IsSparkingPack(mod)) continue;

            foreach (var exe in Directory.GetFiles(extractedFolder, "*.exe", SearchOption.AllDirectories))
            {
                onStatus?.Invoke($"Running {Path.GetFileName(exe)} for {mod.Title}...");
                using var exeProcess = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                if (exeProcess is not null) await exeProcess.WaitForExitAsync(token);
            }
        }

        var newFiles = await SnapshotDiffWithRetryAsync(moddedPath, before);
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

    /// <summary>
    /// Which install method a mod's extracted files call for. LooseFiles/
    /// Executable/X2M/X2S are detected by what's actually inside the
    /// extracted folder (see DetectInstallMethod) - MergeIntoSubfolder is
    /// never returned by that detection; it's decided directly from the
    /// catalog's ModRecord.MergeTargetSubfolder before DetectInstallMethod is
    /// even called (see InstallExtractedModAsync/InstallBatchAsync), since
    /// there's nothing about the extracted content itself that reliably
    /// signals "merge me into an existing subfolder" the way an .x2m, .x2s,
    /// or .exe does.
    /// </summary>
    public enum ModInstallMethod { LooseFiles, Executable, X2M, X2S, MergeIntoSubfolder }

    /// <summary>Looks at what's inside an already-extracted mod folder to decide how it needs to be installed.</summary>
    public static ModInstallMethod DetectInstallMethod(string extractedFolder, out List<string> installerFiles)
    {
        var x2mFiles = Directory.GetFiles(extractedFolder, "*.x2m", SearchOption.AllDirectories).ToList();
        if (x2mFiles.Count > 0)
        {
            installerFiles = x2mFiles;
            return ModInstallMethod.X2M;
        }

        // .x2s files (e.g. Revamp Organized Slots) don't go through XV2INS at
        // all - they're copied straight into "data/" (see InstallX2sFiles).
        var x2sFiles = Directory.GetFiles(extractedFolder, "*.x2s", SearchOption.AllDirectories).ToList();
        if (x2sFiles.Count > 0)
        {
            installerFiles = x2sFiles;
            return ModInstallMethod.X2S;
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
        ModRecord mod, string extractedFolder, string moddedPath, Action<string>? onStatus, CancellationToken cancellationToken, bool isRepair = false)
    {
        // Night Conton City's archive has an "Install First"/"Install Second"
        // structure - see InstallNightContonCityAsync for the two-step,
        // Halloween-asset-filtered install.
        if (IsNightContonCity(mod))
            return await InstallNightContonCityAsync(mod, extractedFolder, moddedPath, onStatus, cancellationToken);

        // Catalog-declared "merge this into an existing subfolder" mods
        // (e.g. Chi-Chi's DYT pack, merged into data/chara/CHI) bypass the
        // x2m/x2s/exe/loose-files detection entirely - see ModInstallMethod's
        // remarks on why this can't be inferred from the extracted content.
        if (!string.IsNullOrWhiteSpace(mod.MergeTargetSubfolder))
        {
            onStatus?.Invoke($"Merging {mod.Title} into '{mod.MergeTargetSubfolder}'...");
            return InstallMergeIntoSubfolder(mod, extractedFolder, moddedPath);
        }

        var method = DetectInstallMethod(extractedFolder, out var installerFiles);

        if (method == ModInstallMethod.X2M)
        {
            // Lazybones' and Sparking Pack's archives both ship far more
            // .x2m variants than should actually be installed - see each
            // curation method's own remarks for exactly which ones and why.
            if (IsLazybones(mod)) installerFiles = SelectLazybonesX2mFiles(extractedFolder);
            else if (IsSparkingPack(mod)) installerFiles = SelectSparkingPackX2mFiles(extractedFolder);
        }

        return method switch
        {
            ModInstallMethod.X2M => await InstallViaX2mAsync(mod, installerFiles, extractedFolder, moddedPath, onStatus, cancellationToken, isRepair),
            ModInstallMethod.X2S => InstallX2sFiles(mod, installerFiles, moddedPath),
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

        var step1Files = await SnapshotDiffWithRetryAsync(moddedPath, beforeStep1);
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
            // XV2INS's exit code isn't a reliable success/failure signal -
            // see InstallX2mGroupAsync's equivalent remark. The newFiles
            // check below is what actually confirms this step's result.
        }

        var step2Files = await SnapshotDiffWithRetryAsync(moddedPath, beforeStep2);

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
    /// installed. Confirmed against a real extracted copy
    /// ("lazybones-revamp-patch-3"): only two things should go in -
    ///   1. The "LB Dependencies"/"Install First" prerequisite package - a
    ///      base-assets package required for the transformations to work at
    ///      all, not a transformation variant itself.
    ///   2. The "Dynamic Transformations" variant set specifically.
    /// Everything else in the archive (the plain baseline, "No Health
    /// Requirement", "Moveset Swap" bundles, "Patches (OLD) - use at your
    /// own risk", etc.) is deliberately left out entirely - only one
    /// transformation style is meant to be active at a time, and Dynamic
    /// Transformations is the one to install for now.
    /// </summary>
    private static List<string> SelectLazybonesX2mFiles(string extractedFolder)
    {
        var allX2m = Directory.GetFiles(extractedFolder, "*.x2m", SearchOption.AllDirectories);

        var dependencies = allX2m.Where(f =>
            f.Contains("LB Dependencies", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(f).Contains("Install First", StringComparison.OrdinalIgnoreCase)).ToList();

        var dynamicTransformations = allX2m
            .Except(dependencies)
            .Where(f => f.Contains("Dynamic Transformations", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return dependencies.Concat(dynamicTransformations).ToList();
    }

    private static bool IsSparkingPack(ModRecord mod) =>
        mod.Id.Contains("sparking-pack", StringComparison.OrdinalIgnoreCase) ||
        mod.Title.Contains("Sparking Pack", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Curates which of Sparking Pack's .x2m files actually get installed.
    /// Confirmed against a real extracted copy: the archive ships two
    /// parallel .x2m sets under near-identical top-level folders -
    /// "1. X2M [Vanilla Style]" and "1. X2M [XV2 Revamp 5.0 ONLY]" - plus a
    /// third, Vanilla-only "2. [Vanilla Only] Parallel Quests and Expert
    /// Missions" folder whose own readme warns it can crash the game under
    /// Revamp. Only the Revamp-labelled .x2m set is installed; both Vanilla
    /// folders are skipped entirely.
    ///
    /// The "Revamp" check is deliberately done against the path RELATIVE to
    /// extractedFolder, not the file's full absolute path - the Modded
    /// folder itself is very commonly named something like
    /// "DB Xenoverse 2 REVAMP", so a plain Contains("Revamp") against the
    /// absolute path matched every single .x2m in the archive (Vanilla
    /// folders included), which is exactly what produced duplicate/
    /// conflicting character installs.
    ///
    /// Also excludes Sparking Pack's own "Vegeta (Ultra Ego).x2m" - a
    /// better version of that character already exists elsewhere in the
    /// catalog (kept there since Parallel Quests likely depends on it), and
    /// this specific copy has a known "Ultimate Charge" bug (a static pose
    /// while charging Ki). The exclusion only matches that exact character
    /// swap, not "Vegeta Wig (Ultra Ego).x2m" (a cosmetic accessory) or the
    /// "Ultra Ego for CaC/CAC" Create-a-Character presets, which are kept.
    ///
    /// The archive's separate "3. Installer (Presets for UI-Sign and UI)"
    /// folder holds a companion .exe (UI presets) that isn't a .x2m at all -
    /// see IsSparkingPack's use in InstallViaX2mAsync/InstallX2mGroupAsync
    /// for where that gets run, unconditionally rather than only on Repair.
    /// </summary>
    private static List<string> SelectSparkingPackX2mFiles(string extractedFolder) =>
        Directory.GetFiles(extractedFolder, "*.x2m", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(extractedFolder, f).Contains("Revamp", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Contains("Vegeta (Ultra Ego)", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// The original behavior: every extracted file is just copied as-is into
    /// the Modded folder. Correct for mods that ship as plain drop-in files,
    /// no installer. Fails explicitly (rather than reporting a false
    /// success) if the extracted folder turned out to have nothing to copy -
    /// an empty/corrupt archive or an unexpected layout would otherwise
    /// silently "succeed" with zero files actually installed, which for a
    /// XenoSyncCore (mandatory) mod is worse than a visible failure: since
    /// InstalledRelativeFiles stays empty, the next mod-catalog reload
    /// re-treats it as "never installed" rather than "broken", resetting
    /// NeedsUpdate back to false and letting Run report everything as
    /// up to date even though nothing was actually placed on disk.
    /// </summary>
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

        if (written.Count == 0)
            return (false, $"{mod.Title}'s extracted folder had no files to copy - its archive may be empty, or its layout may have changed.");

        mod.InstalledRelativeFiles = written;
        mod.IsEnabled = true;
        return (true, null);
    }

    /// <summary>
    /// Installs .x2s file(s) (e.g. Revamp Organized Slots) by copying them
    /// directly into "&lt;ModdedPath&gt;/data/", flat - not preserving
    /// whatever subfolder structure the source archive used, and not routed
    /// through XV2INS/.x2m tooling at all: Revamp reads .x2s files straight
    /// out of data/ itself.
    /// </summary>
    private static (bool Success, string? ErrorMessage) InstallX2sFiles(ModRecord mod, List<string> x2sFiles, string moddedPath)
    {
        var dataDir = Path.Combine(moddedPath, "data");
        Directory.CreateDirectory(dataDir);

        var written = new List<string>();
        foreach (var file in x2sFiles)
        {
            var destination = Path.Combine(dataDir, Path.GetFileName(file));
            System.IO.File.Copy(file, destination, overwrite: true);
            written.Add(Path.GetRelativePath(moddedPath, destination));
        }

        if (written.Count == 0)
            return (false, $"{mod.Title}'s extracted archive had no .x2s files to install.");

        mod.InstalledRelativeFiles = written;
        mod.IsEnabled = true;
        return (true, null);
    }

    /// <summary>
    /// Installs a mod by merging its extracted content into an existing
    /// subfolder of the Modded folder (mod.MergeTargetSubfolder), instead of
    /// dropping the raw extracted files at the Modded root or trying to
    /// infer an x2m/x2s/exe/loose-files method from what's inside. Used for
    /// mods whose real, manual install instructions amount to "drag this
    /// folder's contents into the matching folder that's already there" -
    /// e.g. InviernoCreations' Chi-Chi DYT pack, whose "CHI" folder needs to
    /// be merged into the already-installed "data/chara/CHI", not extracted
    /// as a sibling "CHI" folder next to the game's own bin/data.
    ///
    /// If the extracted archive wraps everything in a single top-level
    /// folder (a common "the whole mod lives inside one folder" archive
    /// layout), that wrapper is flattened first - mirroring the same
    /// flattening MainWindow's own install pipeline applies to Revamp/XV2INS
    /// - so the mod's actual payload lands directly under
    /// MergeTargetSubfolder instead of one level too deep.
    /// </summary>
    private static (bool Success, string? ErrorMessage) InstallMergeIntoSubfolder(ModRecord mod, string extractedFolder, string moddedPath)
    {
        var targetSubfolder = mod.MergeTargetSubfolder!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var targetDir = Path.Combine(moddedPath, targetSubfolder);
        Directory.CreateDirectory(targetDir);

        var source = FlattenSingleWrapperFolder(extractedFolder);

        var written = new List<string>();
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativeToSource = Path.GetRelativePath(source, file);
            var destination = Path.Combine(targetDir, relativeToSource);
            var relativeToModded = Path.GetRelativePath(moddedPath, destination);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            System.IO.File.Copy(file, destination, overwrite: true);
            written.Add(relativeToModded);
        }

        if (written.Count == 0)
            return (false, $"{mod.Title}'s extracted files were empty - nothing was merged into '{targetSubfolder}'.");

        mod.InstalledRelativeFiles = written;
        mod.IsEnabled = true;
        return (true, null);
    }

    /// <summary>
    /// If <paramref name="dir"/> contains exactly one entry and it's a
    /// subfolder (the typical "everything wrapped in one top folder" archive
    /// layout), returns that subfolder's path instead, so callers merge its
    /// *contents* rather than re-creating that wrapper folder inside the
    /// destination. Otherwise returns <paramref name="dir"/> unchanged.
    /// </summary>
    private static string FlattenSingleWrapperFolder(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            return entries[0];

        return dir;
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

        var newFiles = await SnapshotDiffWithRetryAsync(moddedPath, before);
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
    /// Afterward, runs any companion .exe anywhere inside the extracted
    /// archive (not just its top level - e.g. Sparking Pack's UI preset
    /// installer sits in its own subfolder) if either isRepair is true, or
    /// this mod is one that always needs its companion .exe run regardless
    /// (currently just Sparking Pack - see IsSparkingPack). Some mods
    /// (Lazybones' "Revamp Dynamic Hair Repairer") ship a finishing step
    /// that only makes sense as a *repair* pass over existing content - a
    /// first-time install has nothing yet for it to fix, so that kind is
    /// skipped unless isRepair is true. Others (Sparking Pack's UI presets)
    /// are a normal installer step that's meant to run every time.
    /// </summary>
    private async Task<(bool Success, string? ErrorMessage)> InstallViaX2mAsync(
        ModRecord mod, List<string> x2mFiles, string extractedFolder, string moddedPath, Action<string>? onStatus, CancellationToken token, bool isRepair)
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
        using (var process = Process.Start(new ProcessStartInfo(xv2insPath, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = moddedPath
        }))
        {
            if (process is null) return (false, $"Couldn't start XV2INS for {mod.Title}.");
            await process.WaitForExitAsync(token);
            // XV2INS's exit code isn't a reliable success/failure signal -
            // see InstallX2mGroupAsync's equivalent remark. The newFiles
            // check below is what actually confirms this install's result.
        }

        if (isRepair || IsSparkingPack(mod))
        {
            foreach (var exe in Directory.GetFiles(extractedFolder, "*.exe", SearchOption.AllDirectories))
            {
                onStatus?.Invoke($"Running {Path.GetFileName(exe)}...");
                using var exeProcess = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                if (exeProcess is not null) await exeProcess.WaitForExitAsync(token);
            }
        }

        var newFiles = await SnapshotDiffWithRetryAsync(moddedPath, before);
        if (newFiles.Count == 0)
            return (false, $"XV2INS closed, but no new files showed up for {mod.Title} - the install may not have completed.");

        mod.InstalledRelativeFiles = newFiles;
        mod.IsEnabled = true;
        return (true, null);
    }

    /// <summary>
    /// Snapshot of every file currently in moddedPath, as relative path ->
    /// last-write time (UTC). Used to detect what an opaque installer
    /// (.exe/.x2m via XV2INS) actually did, since neither hands back a
    /// manifest - tracking write times (not just which paths exist) matters
    /// because some mods are "compatibility"/patch mods that deliberately
    /// overwrite files a previous mod (or the base game) already put there,
    /// rather than adding anything new. A plain "which paths are new"
    /// diff sees zero changes for that kind of install and wrongly reports
    /// it as having failed, even though it genuinely patched every file it
    /// was supposed to - see DiffAddedOrChangedFiles.
    /// </summary>
    private static Dictionary<string, DateTime> SnapshotRelativeFiles(string moddedPath)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(moddedPath)) return result;

        foreach (var file in Directory.GetFiles(moddedPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                result[Path.GetRelativePath(moddedPath, file)] = System.IO.File.GetLastWriteTimeUtc(file);
            }
            catch
            {
                // Deleted/renamed mid-scan, or a transient access issue - skip it, not fatal.
            }
        }

        return result;
    }

    /// <summary>
    /// A relative path counts as touched by the install if it either wasn't
    /// there "before" at all, or was there but its last-write time changed -
    /// covering both a mod that adds brand new files and one that overwrites
    /// existing ones in place (a "compatibility"/patch mod).
    /// </summary>
    private static List<string> DiffAddedOrChangedFiles(Dictionary<string, DateTime> before, Dictionary<string, DateTime> after)
    {
        var touched = new List<string>();

        foreach (var (relativePath, writeTime) in after)
        {
            if (!before.TryGetValue(relativePath, out var previousWriteTime) || previousWriteTime != writeTime)
                touched.Add(relativePath);
        }

        return touched;
    }

    /// <summary>
    /// Diffs the Modded folder against a "before" snapshot right after an
    /// opaque installer (XV2INS, a mod's own .exe) reports having closed,
    /// retrying with a short pause if nothing shows up yet before concluding
    /// the install produced nothing.
    ///
    /// Confirmed against a real batched XV2INS run: the exact same batch
    /// (same mods, same .x2m files, same everything) failed with "no new
    /// files showed up" on one Update pass, then succeeded outright on the
    /// very next Update pass with no other change - i.e. XV2INS's process
    /// genuinely can report itself closed (WaitForExitAsync returns) a beat
    /// before whatever it triggered actually finishes writing files to disk,
    /// rather than the install having silently done nothing. Diffing exactly
    /// once immediately after the process exits can catch that in-between
    /// window and wrongly report failure for an install that was actually
    /// about to succeed a moment later.
    /// </summary>
    private static async Task<List<string>> SnapshotDiffWithRetryAsync(string moddedPath, Dictionary<string, DateTime> before, int maxAttempts = 4, int delayMs = 1000)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var diff = DiffAddedOrChangedFiles(before, SnapshotRelativeFiles(moddedPath));
            if (diff.Count > 0 || attempt == maxAttempts) return diff;
            await Task.Delay(delayMs);
        }

        return new List<string>();
    }

    /// <summary>
    /// Renames a just-downloaded part (still using its scratch ".download"
    /// name) to the correct extension based on its real content. A ".x2m"
    /// URL is trusted directly over magic-byte detection: a .x2m file isn't
    /// a zip/rar at all, so DetectKind's own check for that (PK/Rar! magic
    /// bytes) can't recognize it and would otherwise fall through to the
    /// ".rar" default below - mislabeling it and making downstream code
    /// treat a mod hosted as a raw .x2m (e.g. King Piccolo) as a corrupt/
    /// unrecognized archive instead of the direct-install .x2m it actually
    /// is (see EnsureExtractedAsync's handling of that case). Keeps the
    /// "{id}.partNN" prefix either way so multi-volume RAR detection still
    /// works for genuine archives.
    /// </summary>
    private string FinalizePartFileName(string scratchPath, string sourceUrl)
    {
        string ext;

        var urlPath = sourceUrl;
        try { urlPath = new Uri(sourceUrl).AbsolutePath; } catch { /* malformed/relative URL - fall back to the raw string */ }

        if (urlPath.EndsWith(".x2m", StringComparison.OrdinalIgnoreCase))
        {
            ext = ".x2m";
        }
        else
        {
            var kind = _archiveExtractionService.DetectKind(scratchPath);
            ext = kind == ArchiveKind.Zip ? ".zip" : ".rar"; // defaults to .rar: every multi-part mod seen so far is RAR
        }

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

    /// <summary>Deletes exactly the files this mod is recorded as having written, then clears that record. Works the same regardless of which install method wrote them (loose files, .exe, .x2m, .x2s, or merge-into-subfolder), since all of them end up recorded the same way.</summary>
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
    /// this reports that instead of silently doing nothing). Always passes
    /// isRepair: true to InstallExtractedModAsync - "Reinstall" of an
    /// already-installed mod is, by definition, a repair pass rather than a
    /// first-time install, so any repair-only companion step (e.g.
    /// Lazybones' hair repairer) is meant to run here.
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> ReinstallAsync(
        ModRecord mod, string moddedPath, Action<string>? onStatus, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mod.RepositoryFolder) || !Directory.Exists(mod.RepositoryFolder))
            return (false, $"{mod.Title}'s extracted files aren't on disk anymore - re-download it instead of reinstalling.");

        Disable(mod, moddedPath);
        return await InstallExtractedModAsync(mod, mod.RepositoryFolder, moddedPath, onStatus, cancellationToken, isRepair: true);
    }
}
