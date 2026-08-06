using System.Collections.Generic;
using System.IO;

namespace XenoSyncLauncher.Services;

/// Flags XenoSync Launcher sets automatically the moment XV2Patcher finishes
/// installing, before the user ever opens the XV2 Patcher Flags window. Kept
/// as an explicit list so it's easy to see/extend what the launcher changes
/// out of the box versus what stays at XV2Patcher's own defaults.
public static class DefaultPatcherFlags
{
    public static readonly List<(string Key, bool Value)> Overrides = new()
    {
        ("excessive_air_contamination", true), // Online mode enabled by default.
    };

    public static void ApplyTo(string moddedPath, IniFlagService iniFlagService)
    {
        var iniPath = Path.Combine(moddedPath, "XV2PATCHER", "xv2patcher.ini");
        if (!File.Exists(iniPath)) return;

        var lines = iniFlagService.ReadLines(iniPath);

        foreach (var (key, value) in Overrides)
            iniFlagService.SetBoolValue(lines, key, value);

        iniFlagService.SaveLines(iniPath, lines);
    }
}
