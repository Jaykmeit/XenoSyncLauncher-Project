using System;
using System.IO;

namespace XenoSyncLauncher.Services;

/// <summary>
/// XV2INS doesn't use the folder it's actually running from to find the
/// Xenoverse 2 installation - it looks the install up itself (via Steam's
/// own records), which for a separate-directory install always resolves to
/// the Vanilla folder, never the Modded one XV2INS.exe was actually placed
/// in. Since the Vanilla folder isn't (and can't safely become) the
/// downgraded build XV2INS expects, its first-run initialization fails
/// outright once it detects that mismatch.
///
/// This service works around that by briefly making the *Modded* folder's
/// content sit at the Vanilla folder's exact location (the one path Steam -
/// and therefore XV2INS - will actually find), just long enough to run
/// XV2INS's first-launch initialization against it, then swapping
/// everything back exactly the way it was:
///
///   1. Swap(): renames the Vanilla folder to a temporary name
///      ("temporary_Xenoverse2") in the same parent directory, then renames
///      the Modded folder to take over the Vanilla folder's original path.
///   2. (Caller runs XV2INS against what is now, as far as Steam/XV2INS is
///      concerned, the Vanilla location - but is really the Modded folder's
///      content.)
///   3. Restore(): renames the folder currently sitting at the Vanilla
///      path (the Modded content) back to the Modded folder's original
///      path, then renames the temporary folder back to the Vanilla path.
///
/// Only relevant for a separate-directory install where Vanilla and Modded
/// are genuinely different folders - an Over-Vanilla install already has
/// VanillaPath == ModdedPath, so XV2INS already finds the right content and
/// there's nothing to swap (callers should check for this before calling
/// Swap, though Swap itself also no-ops in that case).
///
/// Assumes Vanilla and Modded live on the same volume (Directory.Move can't
/// move across drives) - true for the common case of both being Steam
/// library folders, or a Modded folder placed as a sibling of Vanilla.
/// </summary>
public class DirectorySwapService
{
    private const string TemporaryVanillaFolderName = "temporary_Xenoverse2";

    /// <summary>
    /// Parks the real Vanilla folder under a temporary name, then moves the
    /// Modded folder's content into the Vanilla folder's original location.
    /// Returns the state needed to reverse this via Restore(), or null if
    /// there's nothing to swap (Vanilla/Modded are the same folder, or
    /// either path is missing/empty).
    /// </summary>
    public DirectorySwapState? Swap(string vanillaPath, string moddedPath)
    {
        if (string.IsNullOrWhiteSpace(vanillaPath) || string.IsNullOrWhiteSpace(moddedPath))
            return null;

        if (string.Equals(vanillaPath, moddedPath, StringComparison.OrdinalIgnoreCase))
            return null; // Over-Vanilla install - Vanilla and Modded are already the same folder.

        if (!Directory.Exists(vanillaPath) || !Directory.Exists(moddedPath))
            return null;

        var vanillaParent = Path.GetDirectoryName(vanillaPath);
        if (string.IsNullOrWhiteSpace(vanillaParent)) return null;

        var temporaryVanillaPath = Path.Combine(vanillaParent, TemporaryVanillaFolderName);

        // Don't silently overwrite/merge into a leftover from a previous run
        // that failed to restore itself - that would risk losing the real
        // Vanilla folder. Surface it loudly instead so it can be fixed by hand.
        if (Directory.Exists(temporaryVanillaPath))
        {
            throw new InvalidOperationException(
                $"A leftover '{TemporaryVanillaFolderName}' folder already exists at '{temporaryVanillaPath}' from a previous " +
                "run that didn't finish restoring itself. Please rename or remove it manually before continuing.");
        }

        Directory.Move(vanillaPath, temporaryVanillaPath);

        try
        {
            Directory.Move(moddedPath, vanillaPath);
        }
        catch
        {
            // Couldn't complete the second half of the swap - put the real
            // Vanilla folder back where it was rather than leaving it parked
            // under the temporary name while Modded is untouched.
            Directory.Move(temporaryVanillaPath, vanillaPath);
            throw;
        }

        return new DirectorySwapState
        {
            OriginalVanillaPath = vanillaPath,
            OriginalModdedPath = moddedPath,
            TemporaryVanillaPath = temporaryVanillaPath
        };
    }

    /// <summary>
    /// Undoes Swap(): moves the content currently sitting at the Vanilla
    /// path (the Modded folder's content) back to the Modded folder's
    /// original path, then restores the real Vanilla folder from its
    /// temporary name. Safe to call even if only one half of the swap
    /// actually happened - each step only runs if its source folder exists.
    /// </summary>
    public void Restore(DirectorySwapState state)
    {
        if (Directory.Exists(state.OriginalVanillaPath))
            Directory.Move(state.OriginalVanillaPath, state.OriginalModdedPath);

        if (Directory.Exists(state.TemporaryVanillaPath))
            Directory.Move(state.TemporaryVanillaPath, state.OriginalVanillaPath);
    }
}

/// <summary>Captures what DirectorySwapService.Swap() did, so Restore() can undo it precisely.</summary>
public class DirectorySwapState
{
    public required string OriginalVanillaPath { get; init; }
    public required string OriginalModdedPath { get; init; }
    public required string TemporaryVanillaPath { get; init; }
}
