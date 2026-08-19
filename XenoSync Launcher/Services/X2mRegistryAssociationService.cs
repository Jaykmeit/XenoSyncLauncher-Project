using System;
using System.IO;
using Microsoft.Win32;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Checks whether the .x2m file extension is actually registered to open via
/// XV2INS - not just that XV2INS.exe and x2i7394.tmp.reg exist on disk, but
/// that importing the .reg actually took effect. HKEY_CLASSES_ROOT is used
/// rather than checking HKCU or HKLM specifically, since it's the merged
/// effective view Windows itself uses to resolve file associations
/// (HKCU\Software\Classes overlaid on HKLM\Software\Classes) - so this works
/// regardless of which hive x2i7394.tmp.reg actually wrote to.
/// </summary>
public static class X2mRegistryAssociationService
{
    /// <summary>True if .x2m resolves to a ProgID that itself has a registered handler. Doesn't attempt to verify the handler specifically points at XV2INS - if a real ProgID with a command exists, that's as far as this can meaningfully check.</summary>
    public static bool IsX2mAssociated()
    {
        try
        {
            using var extensionKey = Registry.ClassesRoot.OpenSubKey(".x2m");
            var progId = extensionKey?.GetValue(null) as string; // the default value holds the ProgID
            if (string.IsNullOrWhiteSpace(progId)) return false;

            using var progIdKey = Registry.ClassesRoot.OpenSubKey(progId);
            using var commandKey = progIdKey?.OpenSubKey(@"shell\open\command");
            var command = commandKey?.GetValue(null) as string;

            return !string.IsNullOrWhiteSpace(command);
        }
        catch
        {
            // Registry access denied, key structure unexpected, etc. - treat as "not associated" rather than crash.
            return false;
        }
    }

    /// <summary>
    /// Stricter version of IsX2mAssociated(): true only if .x2m resolves to a
    /// handler whose command actually references THIS install's xv2ins.exe
    /// (under the given Modded folder) - not just that some handler exists.
    ///
    /// This distinction matters because of exactly what caused today's bug
    /// report: a user who had ever run the OLD flow (importing the hosted,
    /// hardcoded-path x2i7394.tmp.reg - see RegisterAssociation's remarks)
    /// already has SOME .x2m association registered, just pointing at the
    /// wrong path. The parameterless IsX2mAssociated() can't tell the
    /// difference and reports "already associated" - which is exactly what
    /// let UpdateTaskPlanner's prerequisite-skip check wrongly treat XV2INS
    /// as fully set up (including skipping RunXv2InsFirstLaunchAsync) on a
    /// machine that actually still needed re-registering. Any code deciding
    /// whether XV2INS's prerequisite setup can be skipped should use this
    /// overload instead, so a stale/mismatched association (from before this
    /// fix, or from a Modded folder that was later moved) is correctly
    /// treated as "needs re-registering", not silently trusted forever.
    /// </summary>
    public static bool IsX2mAssociated(string moddedPath)
    {
        try
        {
            using var extensionKey = Registry.ClassesRoot.OpenSubKey(".x2m");
            var progId = extensionKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(progId)) return false;

            using var progIdKey = Registry.ClassesRoot.OpenSubKey(progId);
            using var commandKey = progIdKey?.OpenSubKey(@"shell\open\command");
            var command = commandKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(command)) return false;

            var expectedXv2InsPath = Path.Combine(moddedPath, "xv2ins.exe");
            return command.Contains(expectedXv2InsPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the .x2m file association directly using THIS install's actual
    /// xv2ins.exe/xv2characreat.exe paths under the given Modded folder,
    /// mirroring the same X2MMod ProgID shape the hosted x2i7394.tmp.reg
    /// uses - see that file's real content (fetched from
    /// raw.githubusercontent.com/.../Version/x2i7394.tmp.reg):
    ///
    ///   [HKEY_CURRENT_USER\Software\Classes\X2MMod\shell\open\command]
    ///   @="C:\Program Files (x86)\Steam\steamapps\common\DB Xenoverse 2 REVAMP\xv2ins.exe \"%1\""
    ///   [HKEY_CURRENT_USER\Software\Classes\X2MMod\shell\Edit\command]
    ///   @="C:\...\xv2characreat.exe \"%1\""
    ///   [HKEY_CURRENT_USER\Software\Classes\.x2m]
    ///   @="X2MMod"
    ///
    /// That file hardcodes the maintainer's own test install path - importing
    /// it as-is only produces a correct association for a user whose Modded
    /// folder happens to sit at that exact path. For every other Modded
    /// path, the association ends up pointing at a location that doesn't
    /// exist (or exists but isn't THIS install), and XV2INS's own first real
    /// run notices the mismatch between where it's actually running from and
    /// what's registered, showing a blocking "XV2 Installer has noticed a
    /// change in the installer path... register again?" dialog our
    /// unattended pipeline has no way to answer.
    ///
    /// Writing these same keys here, using the real runtime path instead of
    /// importing the static file's content, makes the association correct
    /// from the start regardless of where the user's Modded folder is -
    /// XV2INS never has a reason to think its path "changed" on its very
    /// first real run. Also properly quotes the executable path itself
    /// (the hosted .reg does not, which is fragile for a path like
    /// "DB Xenoverse 2 REVAMP" that genuinely contains spaces).
    ///
    /// Writes to HKEY_CURRENT_USER\Software\Classes specifically (not
    /// HKEY_LOCAL_MACHINE) so this never needs administrator elevation -
    /// consistent with IsX2mAssociated() reading through the merged
    /// HKEY_CLASSES_ROOT view, which overlays HKCU\Software\Classes on top
    /// of HKLM\Software\Classes.
    /// </summary>
    public static void RegisterAssociation(string moddedPath)
    {
        var xv2insPath = Path.Combine(moddedPath, "xv2ins.exe");
        var xv2characreatPath = Path.Combine(moddedPath, "xv2characreat.exe");

        using (var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\X2MMod"))
        {
            using (var openCommandKey = progIdKey.CreateSubKey(@"shell\open\command"))
                openCommandKey.SetValue(null, $"\"{xv2insPath}\" \"%1\"");

            // xv2characreat.exe is optional here - if for some reason it
            // wasn't part of this XV2INS release/archive, still register the
            // main .x2m -> xv2ins.exe association rather than failing outright.
            if (File.Exists(xv2characreatPath))
            {
                using var editCommandKey = progIdKey.CreateSubKey(@"shell\Edit\command");
                editCommandKey.SetValue(null, $"\"{xv2characreatPath}\" \"%1\"");
            }
        }

        using var extensionKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.x2m");
        extensionKey.SetValue(null, "X2MMod");
    }
}