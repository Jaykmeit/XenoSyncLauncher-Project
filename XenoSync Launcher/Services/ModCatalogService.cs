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
/// off ModRecord.Id and persisted in mods.json.
/// </summary>
public class ModCatalogService
{
    private readonly RemoteConfigService _remoteConfigService;

    private readonly InstalledComponentVersionService _installedVersionService = new();

    public ModCatalogService(RemoteConfigService? remoteConfigService = null)
    {
        _remoteConfigService = remoteConfigService ?? new RemoteConfigService();
    }

    private static string LocalStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XenoSyncLauncher", "mods.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<List<ModRecord>> LoadAsync(string? moddedPath = null)
    {
        var localById = LoadLocalState().ToDictionary(m => m.Id);
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

            // Don't just trust what mods.json says was installed - a Modded
            // reinstall, manual cleanup, or a botched previous update could
            // have wiped the actual files without the record ever being
            // updated to reflect that. Verify every recorded file is still
            // there; if it claims enabled but isn't verifiably so, that's a
            // signal it needs a fresh Update/Reinstall (NeedsUpdate), not
            // silent trust that it's actually working.
            bool recordedEnabled = category == ModCategory.XenoSyncCore
                ? existing is { RepositoryFolder: not null } && existing.InstalledRelativeFiles.Count > 0
                : existing?.IsEnabled ?? false;

            bool filesVerifiedPresent = recordedEnabled && existing is { InstalledRelativeFiles.Count: > 0 } &&
                (moddedPath is null || existing.InstalledRelativeFiles.All(rel => System.IO.File.Exists(Path.Combine(moddedPath, rel))));

            // Can't verify without knowing where to look - only flag a real
            // mismatch, don't punish mods for moddedPath being unknown yet.
            bool needsUpdate = recordedEnabled && moddedPath is not null && !filesVerifiedPresent;

            bool isActuallyInstalled = recordedEnabled;

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
                IsEnabled = isActuallyInstalled,
                RepositoryFolder = existing?.RepositoryFolder,
                InstalledRelativeFiles = existing?.InstalledRelativeFiles ?? new List<string>(),
                NeedsUpdate = needsUpdate
            });
        }

        result = ReorderChildrenAfterParents(result);

        Save(result);
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
        bool filesVerifiedPresent = recordedInstalled && !string.IsNullOrWhiteSpace(moddedPath) &&
            System.IO.File.Exists(Path.Combine(moddedPath, "data", "LB Mod Installer", "revamp xenoverse 2 project_revamp team.xml"));

        bool needsUpdate = recordedInstalled && !string.IsNullOrWhiteSpace(moddedPath) && !filesVerifiedPresent;
        bool isActuallyInstalled = recordedInstalled;

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

    public void Save(List<ModRecord> mods)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalStatePath)!);
        System.IO.File.WriteAllText(LocalStatePath, JsonSerializer.Serialize(mods, JsonOptions));
    }

    private static List<ModRecord> LoadLocalState()
    {
        if (!System.IO.File.Exists(LocalStatePath)) return new List<ModRecord>();

        try
        {
            return JsonSerializer.Deserialize<List<ModRecord>>(System.IO.File.ReadAllText(LocalStatePath), JsonOptions) ?? new List<ModRecord>();
        }
        catch
        {
            return new List<ModRecord>();
        }
    }
}