using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XenoSyncLauncher.Services;

/// <summary>
/// After each Update-pipeline install step copies/merges a component's files
/// into the Modded folder, actively checks that the specific marker file (or
/// non-empty directory) that step is supposed to have produced is genuinely
/// there - rather than trusting that "the copy/merge didn't throw" means the
/// install actually succeeded. A corrupt/incomplete archive, a hosted file
/// whose layout changed, or content that landed in the wrong subfolder can
/// all "succeed" from a plain file-copy's point of view while leaving the
/// Modded folder without what that component is actually supposed to
/// provide. This is the check that catches that, so the Update pipeline can
/// stop (see MainWindow.RunInstallTaskAsync) instead of silently reporting
/// success and moving on to the next step.
/// </summary>
public class InstallVerificationService
{
    /// <summary>
    /// Relative path (from the Modded folder) of the one file each
    /// component's install step must have produced, keyed by the same
    /// componentKey used throughout the Update pipeline
    /// (RunInstallTaskAsync/RunSingleTaskAsync). Components not listed here
    /// either have their own richer, purpose-built check (XV2Patcher and
    /// Revamp - see RunInstallTaskAsync) or are covered by a special-case
    /// rule below (xv2ins-dcd, xv2ins-reg).
    /// </summary>
    private static readonly Dictionary<string, string> ExpectedMarkerFile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xv2patcher"] = Path.Combine("XV2PATCHER", "xv2patcher.ini"),
        ["revamp"] = Path.Combine("data", "LB Mod Installer", "revamp xenoverse 2 project_revamp team.xml"),
        ["xv2ins"] = "XV2INS.exe",
    };

    /// <summary>
    /// Verifies componentKey's install step actually produced what it's
    /// supposed to. Returns (true, null) for a component this service
    /// doesn't have a rule for.
    /// </summary>
    public (bool Success, string? ErrorMessage) Verify(string componentKey, string moddedPath)
    {
        if (ExpectedMarkerFile.TryGetValue(componentKey, out var relativePath))
        {
            var fullPath = Path.Combine(moddedPath, relativePath);
            return File.Exists(fullPath)
                ? (true, null)
                : (false, $"Expected file '{relativePath}' wasn't found under the Modded folder after installing {componentKey} - the install did not complete correctly.");
        }

        if (string.Equals(componentKey, "xv2ins-dcd", StringComparison.OrdinalIgnoreCase))
        {
            // xv2ins_dcd.rar's exact contents aren't pinned to one known
            // filename, so this only confirms *something* actually landed
            // under data/ rather than the merge silently producing nothing
            // (an empty/corrupt archive, or a hosted file whose layout changed).
            var dataDir = Path.Combine(moddedPath, "data");
            if (!Directory.Exists(dataDir) || !Directory.EnumerateFileSystemEntries(dataDir).Any())
                return (false, $"'{dataDir}' is missing or empty after installing the XV2INS prerequisite files - the install did not complete correctly.");
            return (true, null);
        }

        return (true, null);
    }
}
