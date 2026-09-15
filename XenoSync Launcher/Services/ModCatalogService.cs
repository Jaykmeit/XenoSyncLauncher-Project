using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Builds the mod list shown in the UI by combining:
///  - A fixed "Revamp Core" entry (whatever Revamp's own installer bundles -
///    not individually enumerated, since we don't parse its internal layout).
///  - XenoSyncCore mods from the hosted catalog: mandatory, always enabled.
///  - Optional mods from the hosted catalog: available, but only enabled if
///    this device has turned them on.
///
/// Every player gets the same curated list of mods (from the same hosted
/// catalog) - only which Optional ones are turned on varies per device. This
/// is what keeps the modded experience consistent between players.
///
/// Local-only state (IsEnabled for Optional mods, RepositoryFolder,
/// InstalledRelativeFiles) is preserved across catalog refreshes by keying
/// off ModRecord.Id and persisted in "&lt;ModdedPath&gt;/XenoSync/mods.json" -
/// i.e. INSIDE the Modded folder itself, not in a single global
/// %APPDATA%-wide file. This matters as soon as more than one Modded folder
/// is ever used on the same Windows profile (e.g. switching the configured
/// Modded path from folder A to folder B and back to A): a single shared
/// mods.json can't tell which folder a given IsEnabled/RepositoryFolder/
/// InstalledRelativeFiles state belongs to, so state written while B was
/// configured (including a mod being auto-flagged NeedsUpdate because its
/// files don't exist under B, and later Disabled because of that) would
/// silently bleed into A's view of things and vice versa - this is what
/// caused mods that were genuinely still installed in A to show up
/// unchecked after a round trip through B. Scoping the file to the Modded
/// folder itself (the same approach InstalledComponentVersionService
/// already uses for the XV2Patcher/Revamp version bookkeeping) means each
/// Modded folder keeps its own, independent record.
/// </summary>
public class ModCatalogService
{
    private readonly RemoteConfigService _remoteConfigService;

    private readonly InstalledComponentVersionService _installedVersionService = new();

    public ModCatalogService(RemoteConfigService? remoteConfigService = null)
    {
        _remoteConfigService = remoteConfigService ?? new RemoteConfigService();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Where a given Modded folder's own mods.json lives - alongside installed-versions.json, inside "&lt;ModdedPath&gt;/XenoSync/".</summary>
    private static string LocalStatePathFor(string moddedPath) => Path.Combine(moddedPath, "XenoSync", "mods.json");

    /// <summary>
    /// Same layout ModInstallService.RepositoryFolderFor uses: where a mod's
    /// extracted files live, inside the Modded folder itself rather than a
    /// launcher-private AppData cache. Duplicated here (rather than shared)
    /// since ModInstallService's copy is private - kept as a one-line combine
    /// so there's nothing meaningful to actually get out of sync between them.
    /// </summary>
    private static string RepositoryFolderFor(string moddedPath, string modId) =>
        Path.Combine(moddedPath, "XenoSync", "DownloadedMods", modId);

    /// <summary>
    /// When moddedPath is null (no Modded folder configured yet - e.g. right
    /// after a fresh install before the Wizard finishes), there's nowhere to
    /// read/write per-folder local state from, so the catalog is built with
    /// no local overrides at all: every mod shows as not-installed until a
    /// Modded folder is actually configured and this is called again with it.
    /// </summary>
    public async Task<List<ModRecord>> LoadAsync(string? moddedPath = null)
    {
        var localById = (moddedPath is not null ? LoadLocalState(moddedPath) : new List<ModRecord>()).ToDictionary(m => m.Id);
        var remoteMods = await _remoteConfigService.GetModsAsync();

        var result = new List<ModRecord>
        {
            BuildRevampCoreEntry(localById, moddedPath)
        };

        var seenIds = new HashSet<string>();

        foreach (var remote in remoteMods)
        {
            if (string.IsNullOrWhiteSpace(remote.Id)) continue;

            // The hosted catalog is hand-edited; guard against a duplicated id
            // (copy-paste mistake) instead of letting it crash mod loading later
            // in ReorderChildrenAfterParents/ToDictionary.
            if (!seenIds.Add(remote.Id))
                continue;

            var category = remote.Category == "XenoSyncCore" ? ModCategory.XenoSyncCore : ModCategory.Optional;
            localById.TryGetValue(remote.Id, out var existing);

            var downloadUrls = remote.DownloadUrls is { Count: > 0 }
                ? remote.DownloadUrls
                : (remote.DownloadUrl is not null ? new List<string> { remote.DownloadUrl } : new List<string>());

            // Don't just trust what this Modded folder's mods.json says was
            // installed - a Modded reinstall, manual cleanup, or a botched
            // previous update could have wiped the actual files without the
            // record ever being updated to reflect that. Verify every
            // recorded file is still there; if it claims enabled but isn't
            // verifiably so, that's a signal it needs a fresh Update/Reinstall
            // (NeedsUpdate), not silent trust that it's actually working.
            bool recordedEnabled = category == ModCategory.XenoSyncCore
                ? existing is { RepositoryFolder: not null } && existing.InstalledRelativeFiles.Count > 0
                : existing?.IsEnabled ?? false;

            // mods.json now lives INSIDE this Modded folder (see class docs),
            // so it travels with it the same way RepositoryFolder does. Still
            // worth tolerating a repository folder that exists on disk but
            // isn't yet reflected in mods.json (e.g. mods.json was deleted
            // manually, or files were copied in from elsewhere) instead of
            // permanently showing "not installed" despite the raw extracted
            // files plainly being present.
            var repositoryFolder = existing?.RepositoryFolder ?? (moddedPath is not null ? RepositoryFolderFor(moddedPath, remote.Id) : null);
            bool repositoryFolderExistsOnDisk = repositoryFolder is not null && Directory.Exists(repositoryFolder);

            if (!recordedEnabled && repositoryFolderExistsOnDisk)
            {
                // We now know the mod's raw extracted files are there, but
                // mods.json has no record of which relative paths were
                // actually copied into the Modded folder itself - flipping
                // straight to IsEnabled=true with an empty
                // InstalledRelativeFiles would make Disable()/Uninstall a
                // silent no-op later. Instead: show it as installed (checkbox
                // checked) but flagged NeedsUpdate, so Update or
                // EnsureMandatoryModsInstalledAsync re-runs
                // InstallExtractedModAsync against this already-extracted
                // repository folder (no re-download needed) to properly
                // (re)populate InstalledRelativeFiles.
                recordedEnabled = true;
            }

            bool filesVerifiedPresent = recordedEnabled && existing is { InstalledRelativeFiles.Count: > 0 } &&
                (moddedPath is null || existing.InstalledRelativeFiles.All(rel => System.IO.File.Exists(Path.Combine(moddedPath, rel))));

            // Can't verify without knowing where to look - only flag a real
            // mismatch, don't punish mods for moddedPath being unknown yet.
            //
            // OR'd with the persisted existing.NeedsUpdate: a Repair
            // (MainWindow.MarkAllEnabledModsForReinstall) deliberately sets
            // NeedsUpdate=true and saves it to mods.json even when the mod's
            // old files are still verifiably present - that's the whole
            // point of a Repair, forcing a reinstall of something that
            // otherwise "looks" fine. Without carrying that persisted flag
            // forward here, this recompute-from-scratch check would silently
            // flip it back to false the very next time mods are loaded
            // (which happens right before EnsureMandatoryModsInstalledAsync
            // runs, in both StartUpdate's no-op branch and
            // FinishUpdateAsync) - discarding the Repair request before it
            // ever got a chance to actually reinstall anything. Once a
            // (re)install genuinely succeeds, EnsureMandatoryModsInstalledAsync
            // persists NeedsUpdate=false itself, so this doesn't loop forever.
            bool needsUpdate = (recordedEnabled && moddedPath is not null && !filesVerifiedPresent) || existing?.NeedsUpdate == true;

            // For XenoSyncCore (locked checkbox, not something the user
            // toggles) the checkbox is meant to answer "is this genuinely
            // installed right now", not "was this ever recorded as
            // installed" - so it's gated on filesVerifiedPresent, same as
            // Revamp Core below. Optional mods keep the old behavior
            // (IsEnabled reflects the user's own toggle/intent, separate
            // from NeedsUpdate) since that checkbox is interactive and
            // flipping it off from under the user just because a repair
            // hasn't run yet would be confusing, not honest.
            bool isActuallyInstalled = category == ModCategory.XenoSyncCore ? filesVerifiedPresent : recordedEnabled;

            result.Add(new ModRecord
            {
                Id = remote.Id,
                Title = remote.Title ?? remote.Id,
                Description = remote.Description ?? string.Empty,
                Author = remote.Author ?? string.Empty,
                PageUrl = remote.PageUrl ?? string.Empty,
                DownloadUrls = downloadUrls,
                ScreenshotUrls = remote.ScreenshotUrls ?? new List<string>(),
                ParentId = remote.Parent,
                Category = category,
                MergeTargetSubfolder = remote.MergeTargetSubfolder,
                IsEnabled = isActuallyInstalled,
                RepositoryFolder = existing?.RepositoryFolder ?? (repositoryFolderExistsOnDisk ? repositoryFolder : null),
                InstalledRelativeFiles = existing?.InstalledRelativeFiles ?? new List<string>(),
                NeedsUpdate = needsUpdate
            });
        }

        result = ReorderChildrenAfterParents(result);

        if (moddedPath is not null) Save(moddedPath, result);
        return result;
    }

    /// <summary>
    /// Moves each mod that declares a "parent" to sit immediately after that
    /// parent in the list, so the grouped UI shows codependent mods next to
    /// each other. Mods without a parent (or whose declared parent isn't in
    /// the catalog) keep their original relative order.
    /// </summary>
    private static List<ModRecord> ReorderChildrenAfterParents(List<ModRecord> mods)
    {
        var byId = mods.ToDictionary(m => m.Id);
        var result = new List<ModRecord>();
        var visited = new HashSet<string>();

        void AddWithChildren(ModRecord mod)
        {
            if (!visited.Add(mod.Id)) return;
            result.Add(mod);

            foreach (var child in mods.Where(m => m.ParentId == mod.Id))
                AddWithChildren(child);
        }

        foreach (var mod in mods.Where(m => m.ParentId is null || !byId.ContainsKey(m.ParentId)))
            AddWithChildren(mod);

        // Safety net: don't silently drop a mod if something odd happened above (e.g. a parent cycle).
        foreach (var mod in mods)
            AddWithChildren(mod);

        return result;
    }

    private ModRecord BuildRevampCoreEntry(Dictionary<string, ModRecord> localById, string? moddedPath)
    {
        localById.TryGetValue("xv2-revamp-core", out var existing);

        bool recordedInstalled = !string.IsNullOrWhiteSpace(moddedPath) &&
                                    _installedVersionService.GetInstalledRevampVersion(moddedPath) is not null;

        // installed-versions.json is the launcher's own bookkeeping, written
        // right after a successful install - it doesn't get updated if the
        // files themselves later disappear (Modded reinstall, manual
        // cleanup...). Verify the same key file used elsewhere to confirm a
        // real Revamp install (see IsRevampInstalledCorrectly in MainWindow).
        //
        // Like Optional/XenoSyncCore mods' own mods.json, installed-versions.json
        // already lives INSIDE the Modded folder (at "<ModdedPath>/XenoSync/"),
        // so it travels with it the same way RepositoryFolder does - no
        // separate on-disk existence check is needed here.
        bool filesVerifiedPresent = recordedInstalled && !string.IsNullOrWhiteSpace(moddedPath) &&
            System.IO.File.Exists(Path.Combine(moddedPath, "data", "LB Mod Installer", "revamp xenoverse 2 project_revamp team.xml"));

        // OR'd with the persisted existing.NeedsUpdate for the same reason as
        // the Optional/XenoSyncCore loop above: a Repair can set this true
        // deliberately even while the old key file is still sitting there
        // from a previous install, and that intent must survive a reload
        // that happens before the reinstall actually runs. Revamp Core is
        // currently skipped by EnsureMandatoryModsInstalledAsync (it has no
        // DownloadUrls - see that method), so this mainly keeps the UI/Run
        // button honest about a pending Revamp repair rather than driving a
        // reinstall by itself.
        bool needsUpdate = (recordedInstalled && !string.IsNullOrWhiteSpace(moddedPath) && !filesVerifiedPresent) || existing?.NeedsUpdate == true;

        // Revamp Core's checkbox is locked (never user-toggleable), so it's
        // purely informational - it must answer "is Revamp genuinely
        // installed right now", not "did installed-versions.json ever record
        // a successful install". Gating on filesVerifiedPresent (instead of
        // the raw bookkeeping flag) is what makes the checkbox honestly go
        // unchecked - and, via NeedsUpdate below, Run correctly disabled -
        // the moment Revamp's key file goes missing, instead of staying
        // checked forever off a install that happened at some point in the
        // past and may no longer reflect what's actually on disk.
        bool isActuallyInstalled = filesVerifiedPresent;

        return new ModRecord
        {
            Id = "xv2-revamp-core",
            Title = "Xenoverse 2 Revamp",
            Description = "Core mod pack bundled with the Revamp installer. Always required by XenoSync Launcher.",
            Author = "Revamp Team",
            PageUrl = "https://www.revampxv2.com/download",
            // Curated from Revamp's official VideogameMods listing (videogamemods.com/.../revamp-xenoverse-2-project-v5-0-0-350530),
            // hosted on VGM's own CDN - loads fine directly, no login/session required.
            // Spread across the full 76-image gallery (not consecutive) for more variety in the slideshow.
            ScreenshotUrls = new List<string>
            {
                "https://uploads.videogamemods.com/communities/the-citadel/mods/revamp-xenoverse-2-project-v5-0-0-350530-cf80dfa2-1255-40fe-8100-c2385c3387e4/images/0_a336004a.webp",
                "https://uploads.videogamemods.com/communities/the-citadel/mods/revamp-xenoverse-2-project-v5-0-0-350530-cf80dfa2-1255-40fe-8100-c2385c3387e4/images/8_54887fa8.webp",
                "https://uploads.videogamemods.com/communities/the-citadel/mods/revamp-xenoverse-2-project-v5-0-0-350530-cf80dfa2-1255-40fe-8100-c2385c3387e4/images/16_bf76a331.webp",
                "https://uploads.videogamemods.com/communities/the-citadel/mods/revamp-xenoverse-2-project-v5-0-0-350530-cf80dfa2-1255-40fe-8100-c2385c3387e4/images/24_e1ff265f.webp",
                "https://uploads.videogamemods.com/communities/the-citadel/mods/revamp-xenoverse-2-project-v5-0-0-350530-cf80dfa2-1255-40fe-8100-c2385c3387e4/images/32_468a01f5.webp",
                "https://uploads.videogamemods.com/communities/the-citadel/mods/revamp-xenoverse-2-project-v5-0-0-350530-cf80dfa2-1255-40fe-8100-c2385c3387e4/images/40_d0d2cc42.webp",
                "https://uploads.videogamemods.com/communities/the-citadel/mods/revamp-xenoverse-2-project-v5-0-0-350530-cf80dfa2-1255-40fe-8100-c2385c3387e4/images/48_7ad65ae7.webp",
                "https://uploads.videogamemods.com/communities/the-citadel/mods/revamp-xenoverse-2-project-v5-0-0-350530-cf80dfa2-1255-40fe-8100-c2385c3387e4/images/56_fbb99e34.webp",
                "https://uploads.videogamemods.com/communities/the-citadel/mods/revamp-xenoverse-2-project-v5-0-0-350530-cf80dfa2-1255-40fe-8100-c2385c3387e4/images/64_c31251b3.webp",
                "https://uploads.videogamemods.com/communities/the-citadel/mods/revamp-xenoverse-2-project-v5-0-0-350530-cf80dfa2-1255-40fe-8100-c2385c3387e4/images/75_30b2c995.webp"
            },
            Category = ModCategory.RevampCore,
            IsEnabled = isActuallyInstalled,
            RepositoryFolder = existing?.RepositoryFolder,
            InstalledRelativeFiles = existing?.InstalledRelativeFiles ?? new List<string>(),
            NeedsUpdate = needsUpdate
        };
    }

    /// <summary>Persists local mod state into the given Modded folder's own "XenoSync/mods.json" - callers must know which Modded folder this state belongs to.</summary>
    public void Save(string moddedPath, List<ModRecord> mods)
    {
        var path = LocalStatePathFor(moddedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(mods, JsonOptions));
    }

    private static List<ModRecord> LoadLocalState(string moddedPath)
    {
        var path = LocalStatePathFor(moddedPath);
        if (!System.IO.File.Exists(path)) return new List<ModRecord>();

        try
        {
            return JsonSerializer.Deserialize<List<ModRecord>>(System.IO.File.ReadAllText(path), JsonOptions) ?? new List<ModRecord>();
        }
        catch
        {
            return new List<ModRecord>();
        }
    }
}
