using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Cachea el tamaño total (bytes) de un depot de Steam, indexado por
/// ManifestId, para que XenoSync Launcher solo le tenga que preguntar a
/// DepotDownloader una vez por manifest (vía
/// DepotDownloaderService.EstimateSizeAsync) en lugar de en cada
/// Update/Resume. El tamaño de un manifest nunca cambia una vez publicado -
/// no hay problema de "quedar desactualizado": solo un ManifestId nuevo
/// (cuando Revamp sube de versión soportada) necesita una medición nueva: las
/// entradas viejas simplemente quedan sin usarse en el archivo de caché, sin
/// causar ningún daño.
/// </summary>
public class DepotSizeCacheService
{
    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XenoSyncLauncher", "depot-size-cache.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// No real Steam depot is smaller than this - matches the same floor
    /// DepotDownloaderService.ParseManifestSizeFile uses. Applied here too
    /// (not just at parse time) so an entry cached before that validation
    /// existed doesn't get trusted forever just because it's already on disk.
    /// </summary>
    private const long MinimumPlausibleSizeBytes = 1_000_000; // 1 MB

    public long? Get(string manifestId)
    {
        var all = LoadAll();
        if (!all.TryGetValue(manifestId, out var size)) return null;

        if (size < MinimumPlausibleSizeBytes)
        {
            // Leftover from a parser bug in an earlier build (or a corrupted
            // write) - self-heal by dropping the bad entry instead of
            // returning it forever. The caller sees this as "not cached" and
            // will trigger a fresh EstimateSizeAsync.
            all.Remove(manifestId);
            Save(all);
            return null;
        }

        return size;
    }

    public void Set(string manifestId, long sizeBytes)
    {
        if (sizeBytes < MinimumPlausibleSizeBytes) return; // never persist an implausible value in the first place

        var all = LoadAll();
        all[manifestId] = sizeBytes;
        Save(all);
    }

    public void Remove(string manifestId)
    {
        var all = LoadAll();
        if (all.Remove(manifestId))
            Save(all);
    }

    private static Dictionary<string, long> LoadAll()
    {
        if (!File.Exists(CachePath)) return new Dictionary<string, long>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(CachePath)) ?? new Dictionary<string, long>();
        }
        catch
        {
            // Archivo de caché corrupto/parcial: tratarlo como vacío en vez de
            // fallar - en el peor caso esto solo dispara una medición de más.
            return new Dictionary<string, long>();
        }
    }

    private static void Save(Dictionary<string, long> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, JsonSerializer.Serialize(all, JsonOptions));
    }
}