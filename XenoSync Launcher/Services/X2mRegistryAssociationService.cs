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
}