using System;
using System.IO;
using System.Text.RegularExpressions;

namespace XenoSyncLauncher.Services;

/// <summary>
/// XV2INS keeps its own persisted config at
/// "%AppData%/XV2INS/xv2ins.ini", entirely separate from anything
/// XenoSync Launcher writes itself. Its "[General] game_directory" value is
/// what XV2INS actually reads to know which folder to treat as the
/// Xenoverse 2 install it's working against - not the folder its .exe
/// happens to be sitting in, and not anything Steam reports.
///
/// Confirmed directly from a real xv2ins.ini: this value can be empty, or
/// can have been set (manually, or by a previous XV2INS run against a
/// different setup) to the Vanilla folder instead of the Modded one XenoSync
/// actually wants XV2INS to operate on. Either way, XV2INS then installs
/// into - or otherwise acts against - the wrong location, no matter what
/// XenoSync's own Modded path is configured as. This service is the actual
/// fix for that: writing the current Modded path into this file directly,
/// right before every real XV2INS launch (see MainWindow.RunXv2InsFirstLaunchAsync)
/// and whenever the user requests a Repair.
///
/// (An earlier attempt at this problem tried temporarily swapping the
/// Vanilla/Modded folders on disk around the XV2INS launch instead - that
/// never actually addressed this file, so it didn't reliably fix anything.)
/// </summary>
public class Xv2InsConfigService
{
    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XV2INS", "xv2ins.ini");

    /// <summary>Matches an existing "game_directory = "..."" line, wherever it sits in the file.</summary>
    private static readonly Regex GameDirectoryLinePattern = new(
        @"^[ \t]*game_directory[ \t]*=[ \t]*""[^""]*""[ \t]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex GeneralSectionHeaderPattern = new(
        @"\[General\][ \t]*\r?\n", RegexOptions.IgnoreCase);

    /// <summary>
    /// Points xv2ins.ini's "[General] game_directory" at moddedPath,
    /// creating the config file (and its [General] section) if it doesn't
    /// exist yet - XV2INS fills in the rest of its own defaults the next
    /// time it actually runs. Safe to call anytime, including before XV2INS
    /// has ever run once.
    /// </summary>
    public void SetGameDirectory(string moddedPath)
    {
        // XV2INS's own file uses forward slashes and a trailing slash
        // (confirmed from a real xv2ins.ini) - match that format exactly
        // rather than trusting whatever separator moddedPath happens to use.
        var normalizedPath = moddedPath.Replace('\\', '/').TrimEnd('/') + "/";
        var newLine = $"game_directory = \"{normalizedPath}\"";

        var configDir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(configDir);

        var content = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : string.Empty;

        if (GameDirectoryLinePattern.IsMatch(content))
        {
            content = GameDirectoryLinePattern.Replace(content, newLine, 1);
        }
        else if (GeneralSectionHeaderPattern.IsMatch(content))
        {
            // [General] exists but has no game_directory line under it yet - insert right after the header.
            content = GeneralSectionHeaderPattern.Replace(content, m => m.Value + newLine + Environment.NewLine, 1);
        }
        else
        {
            // No config yet, or no [General] section - start with just what's
            // needed and keep anything else that was already there below it.
            content = $"[General]{Environment.NewLine}{newLine}{Environment.NewLine}{content}";
        }

        File.WriteAllText(ConfigPath, content);
    }
}
