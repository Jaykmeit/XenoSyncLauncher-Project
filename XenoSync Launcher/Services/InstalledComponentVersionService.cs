using System.IO;
using System.Text.Json;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Records which version of XV2Patcher/Revamp XenoSync Launcher itself last
/// installed into a given Modded folder. This is intentionally separate from
/// whatever internal file layout XV2Patcher's/Revamp's own archives use
/// (which hasn't been verified against the real downloads) - it's the
/// launcher's own bookkeeping, written right after a successful install and
/// read back by Launch Inspect.
/// </summary>
public class InstalledComponentVersionService
{
    private class InstalledVersionsFile
    {
        public string? Xv2PatcherVersion { get; set; }
        public string? RevampVersion { get; set; }

        /// <summary>
        /// Which game ManifestId DepotDownloader last successfully fetched
        /// into this Modded folder. Only meaningful for separate-directory
        /// installs, where the Modded folder isn't a Steam library folder and
        /// so has no appmanifest of its own to read the installed version from.
        /// </summary>
        public string? GameManifestId { get; set; }
    }

    private static string GetPath(string moddedPath) => Path.Combine(moddedPath, "XenoSync", "installed-versions.json");

    private InstalledVersionsFile Load(string moddedPath)
    {
        var path = GetPath(moddedPath);
        if (!File.Exists(path)) return new InstalledVersionsFile();

        try
        {
            return JsonSerializer.Deserialize<InstalledVersionsFile>(File.ReadAllText(path)) ?? new InstalledVersionsFile();
        }
        catch
        {
            return new InstalledVersionsFile();
        }
    }

    private void Save(string moddedPath, InstalledVersionsFile data)
    {
        var path = GetPath(moddedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string? GetInstalledXv2PatcherVersion(string moddedPath) => Load(moddedPath).Xv2PatcherVersion;

    public string? GetInstalledRevampVersion(string moddedPath) => Load(moddedPath).RevampVersion;

    public void SetInstalledXv2PatcherVersion(string moddedPath, string version)
    {
        var data = Load(moddedPath);
        data.Xv2PatcherVersion = version;
        Save(moddedPath, data);
    }

    public void SetInstalledRevampVersion(string moddedPath, string version)
    {
        var data = Load(moddedPath);
        data.RevampVersion = version;
        Save(moddedPath, data);
    }

    public string? GetInstalledGameManifestId(string moddedPath) => Load(moddedPath).GameManifestId;

    public void SetInstalledGameManifestId(string moddedPath, string manifestId)
    {
        var data = Load(moddedPath);
        data.GameManifestId = manifestId;
        Save(moddedPath, data);
    }
}