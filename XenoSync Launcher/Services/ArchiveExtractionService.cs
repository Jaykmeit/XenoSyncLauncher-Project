using System;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace XenoSyncLauncher.Services;

public enum ArchiveKind
{
    Zip,
    Rar,
    /// <summary>Not a recognized archive format - most likely a self-extracting installer .exe.</summary>
    Unknown
}

public class ArchiveExtractionService
{
    public ArchiveKind DetectKind(string filePath)
    {
        Span<byte> header = stackalloc byte[4];
        using (var stream = File.OpenRead(filePath))
        {
            var read = stream.Read(header);
            if (read < 4) return ArchiveKind.Unknown;
        }

        if (header[0] == 0x50 && header[1] == 0x4B) return ArchiveKind.Zip;              // "PK"
        if (header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21) return ArchiveKind.Rar; // "Rar!"

        return ArchiveKind.Unknown;
    }

    /// <summary>Extracts every file entry to destinationDir, reporting (entriesDone, totalEntries) as it goes.</summary>
    public void Extract(string archivePath, string destinationDir, Action<int, int> onEntryExtracted)
    {
        Directory.CreateDirectory(destinationDir);

        using var archive = ArchiveFactory.Open(archivePath);
        var fileEntries = archive.Entries.Count(e => !e.IsDirectory);
        int done = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;

            entry.WriteToDirectory(destinationDir, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            done++;
            onEntryExtracted(done, fileEntries);
        }
    }
}
