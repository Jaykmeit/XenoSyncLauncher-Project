using System;
using System.IO;

namespace XenoSyncLauncher.Services;

/// <summary>Plain recursive folder copy, with optional (done, total) progress reporting.</summary>
public class DirectoryCopyService
{
    public void CopyAll(string sourceDir, string destinationDir, Action<int, int>? onProgress = null)
    {
        Directory.CreateDirectory(destinationDir);

        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        int done = 0;

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);

            done++;
            onProgress?.Invoke(done, files.Length);
        }
    }
}
