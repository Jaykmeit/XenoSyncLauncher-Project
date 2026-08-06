using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Services;

/// Persists in-progress download state to the OS temp directory so a download
/// survives the user pausing an update or closing the app. Each pending
/// download is stored as its own "{taskId}.json" file so partial state for
/// one component never interferes with another.
public class DownloadResumeService
{
    private static string ResumeDirectory =>
        Path.Combine(Path.GetTempPath(), "XenoSyncLauncher", "Downloads");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string PathFor(string taskId) => Path.Combine(ResumeDirectory, $"{taskId}.json");

    public void Save(DownloadResumeState state)
    {
        Directory.CreateDirectory(ResumeDirectory);
        File.WriteAllText(PathFor(state.TaskId), JsonSerializer.Serialize(state, JsonOptions));
    }

    public DownloadResumeState? Load(string taskId)
    {
        var path = PathFor(taskId);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<DownloadResumeState>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            // Corrupt/partial state file: treat as if there were nothing to resume.
            return null;
        }
    }

    /// Deletes both the resume-state JSON and the partial download file it points to.
    public void Clear(string taskId)
    {
        var state = Load(taskId);

        var jsonPath = PathFor(taskId);
        if (File.Exists(jsonPath)) File.Delete(jsonPath);

        if (state is not null && File.Exists(state.TempFilePath))
            File.Delete(state.TempFilePath);
    }

    public List<DownloadResumeState> ListAll()
    {
        var result = new List<DownloadResumeState>();
        if (!Directory.Exists(ResumeDirectory)) return result;

        foreach (var file in Directory.GetFiles(ResumeDirectory, "*.json"))
        {
            try
            {
                var state = JsonSerializer.Deserialize<DownloadResumeState>(File.ReadAllText(file), JsonOptions);
                if (state is not null) result.Add(state);
            }
            catch
            {
                // Skip unreadable/corrupt entries.
            }
        }

        return result;
    }
}
