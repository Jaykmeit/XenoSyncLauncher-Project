using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace XenoSyncLauncher.Services;

/// Reads and writes individual "key = true/false" lines in xv2patcher.ini
/// using targeted line replacement, so comments, section headers, and any
/// flags XenoSync Launcher doesn't manage are left completely untouched.
/// This mirrors the original design note: find the exact line for a key and
/// only change its value.
public class IniFlagService
{
    public List<string> ReadLines(string iniPath) => File.Exists(iniPath)
        ? new List<string>(File.ReadAllLines(iniPath))
        : new List<string>();

    public void SaveLines(string iniPath, List<string> lines) => File.WriteAllLines(iniPath, lines);

    /// Returns null if the key isn't present anywhere in the file.
    public bool? GetBoolValue(List<string> lines, string key)
    {
        var pattern = BuildKeyPattern(key);

        foreach (var line in lines)
        {
            var match = pattern.Match(line);
            if (!match.Success) continue;

            return match.Groups[1].Value.Trim().ToLowerInvariant() == "true";
        }

        return null;
    }

    /// Replaces the value on the matching "key = ..." line, leaving everything else on that line (and file) as-is.
    public void SetBoolValue(List<string> lines, string key, bool value)
    {
        var pattern = BuildKeyPattern(key);

        for (int i = 0; i < lines.Count; i++)
        {
            var match = pattern.Match(lines[i]);
            if (!match.Success) continue;

            lines[i] = lines[i][..match.Groups[1].Index] + (value ? "true" : "false") + lines[i][(match.Groups[1].Index + match.Groups[1].Length)..];
            return;
        }
    }

    private static Regex BuildKeyPattern(string key) =>
        new($@"^\s*{Regex.Escape(key)}\s*=\s*(true|false)\s*$", RegexOptions.IgnoreCase);
}
