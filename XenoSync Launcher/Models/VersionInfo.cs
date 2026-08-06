using System;

namespace XenoSyncLauncher.Models;

/// <summary>
/// Representa una versión de Xenoverse 2 (o de un manifest de Steam),
/// junto con el ManifestId de DepotDownloader necesario para descargarla
/// si se requiere un downgrade.
/// </summary>
public class VersionInfo : IComparable<VersionInfo>
{
    public string Label { get; init; } = string.Empty; // p.ej. "1.22.00"
    public string? ManifestId { get; init; }            // p.ej. "1234567890123456789"
    public DateTime? BuildDate { get; init; }

    /// <summary>
    /// Steam's internal app-level build number (from appmanifest_454650.acf's
    /// "buildid" field). Kept for display/diagnostics only - confirmed
    /// unreliable for comparison, since the same buildid can be shared by
    /// depot manifests that are NOT interchangeable (e.g. 1.25.2 and 1.26.0
    /// share a buildid despite XV2Patcher only supporting one of them).
    /// </summary>
    public long? BuildId { get; init; }

    public int CompareTo(VersionInfo? other)
    {
        if (other is null) return 1;

        // Manifest ids are the precise, depot-level identity of a build - if
        // both sides have one, that's the only thing worth comparing.
        if (!string.IsNullOrEmpty(ManifestId) && !string.IsNullOrEmpty(other.ManifestId))
            return string.Equals(ManifestId, other.ManifestId, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        // Comparación por partes numéricas tipo "1.22.00" -> [1, 22, 0]
        var a = ParseParts(Label);
        var b = ParseParts(other.Label);

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int pa = i < a.Length ? a[i] : 0;
            int pb = i < b.Length ? b[i] : 0;
            if (pa != pb) return pa.CompareTo(pb);
        }
        return 0;
    }

    private static int[] ParseParts(string label)
    {
        var parts = label.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            int.TryParse(parts[i], out result[i]);
        return result;
    }

    public override string ToString() => Label;
}