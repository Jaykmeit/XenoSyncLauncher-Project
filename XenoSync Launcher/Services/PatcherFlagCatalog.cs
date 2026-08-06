using System.Collections.Generic;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Services;

/// The list of xv2patcher.ini boolean flags XenoSync Launcher exposes as
/// toggles in the XV2 Patcher Flags window.
///
/// Deliberately NOT included: "excessive_air_contamination". XenoSync
/// Launcher does not manage, describe, or toggle this flag. Users can still
/// edit it manually in the ini file if they choose to.
public static class PatcherFlagCatalog
{
    public static IReadOnlyList<PatcherFlagDefinition> All { get; } = new List<PatcherFlagDefinition>
    {
        new() { Key = "loose_files", DisplayName = "Loose files", Description = "Lets the game load individual mod files placed loosely on disk instead of only from packed archives." },
        new() { Key = "new_chara", DisplayName = "New characters", Description = "Allows adding brand new characters beyond the base roster." },
        new() { Key = "new_stages", DisplayName = "New stages", Description = "Allows adding brand new stages beyond the base roster." },
        new() { Key = "unlock_chara_all", DisplayName = "Unlock all characters", Description = "Unlocks every character in-game without touching your save file. If enabled, it makes 'Unlock modded characters' redundant." },
        new() { Key = "unlock_chara_mods", DisplayName = "Unlock modded characters", Description = "Unlocks only characters that aren't normally selectable in the base game (including some vanilla ones and modded additions)." },
        new() { Key = "iggy_trace", DisplayName = "Iggy ActionScript trace", Description = "Enables ActionScript3 trace output for the game's UI engine. Mostly useful for UI mod debugging." },
        new() { Key = "iggy_warning", DisplayName = "Iggy warnings", Description = "Enables printing of warnings related to the game's UI engine." },
        new() { Key = "unlock_stage_all", DisplayName = "Unlock all stages", Description = "Unlocks every stage in-game without touching your save file." },
        new() { Key = "offline_mode", DisplayName = "Offline mode", Description = "Disables the game's internet usage entirely." },
        new() { Key = "bac_bcm", DisplayName = "Allow BAC/BCM modding", Description = "Disables the game's built-in protection that otherwise prevents skill files (BAC/BCM) from being modded. Required for skill mods to work." },
        new() { Key = "battle_timer", DisplayName = "Custom battle timer", Description = "Overrides the offline battle timer length (configured separately)." },
        new() { Key = "gdraw_limits_patch", DisplayName = "Increase UI object limits", Description = "Raises internal UI object limits, needed to safely support more character/skill slots without crashing." },
        new() { Key = "ai_extend", DisplayName = "AI awakening skills", Description = "Allows CPU-controlled characters to use awakening skills, with configurable chances." },
        new() { Key = "ui_enemy_portrait", DisplayName = "Show enemy portraits", Description = "Displays portraits for all enemies, not just a limited base-game subset." },
        new() { Key = "dump_auto_gen_portrait", DisplayName = "Dump auto-generated portraits", Description = "Enables exporting auto-generated character portraits (requires the UI extensions to be installed)." },
        new() { Key = "hide_battle_ui", DisplayName = "Toggle battle UI", Description = "Enables a shortcut to hide/show the battle UI (requires the UI extensions to be installed)." },
        new() { Key = "fs_event_offline", DisplayName = "Offline Frieza Saga siege event", Description = "Makes the Frieza Saga siege event possible while playing offline. Requires Offline mode to also be enabled." },
        new() { Key = "dont_untransform_cut_scenes", DisplayName = "Keep transformations in cutscenes", Description = "Prevents certain transformations from being cancelled during cutscenes." },
        new() { Key = "enable_multiselect_expert", DisplayName = "Multi-select in Expert Missions", Description = "Allows selecting multiple allies when starting an Expert Mission." },
        new() { Key = "take_ally_control", DisplayName = "Control CPU allies", Description = "Lets the player take control of CPU-controlled allies (requires the UI extensions)." },
        new() { Key = "mod_hlq_always_in_global_orb", DisplayName = "Show new Expert Missions early", Description = "Makes newly added Expert Missions appear on the global quest board even before their normal unlock point." },
        new() { Key = "stop_controller_not_connected", DisplayName = "Suppress 'controller not connected'", Description = "Prevents a specific disconnect check, useful for automated CPU-vs-CPU setups." },
        new() { Key = "debug", DisplayName = "Enable debug patches", Description = "Master switch for the patcher's debug-only sub-options below." },
        new() { Key = "log_old_open_cpk_file", DisplayName = "Log legacy CPK file access", Description = "Debug-only logging of an older file-access code path. Requires Enable debug patches." },
        new() { Key = "log_encryption", DisplayName = "Log encryption operations", Description = "Debug-only logging of encryption-related operations. Requires Enable debug patches." },
        new() { Key = "log_all_files", DisplayName = "Log all loose file lookups", Description = "Logs every file lookup that goes through the loose-files system, including ones that succeed normally." },
        new() { Key = "log_loose_files", DisplayName = "Log loose file fallbacks only", Description = "Logs only the file lookups that fail and fall back to loose files. Redundant if 'Log all loose file lookups' is on." },
        new() { Key = "apply_to_roster", DisplayName = "Apply cutscene transform rule to roster", Description = "Applies the 'keep transformations in cutscenes' rule to playable roster characters too, not just story-specific ones." },
        new() { Key = "use_mode_6", DisplayName = "6-character Expert Mission select", Description = "Uses the 6-vs-6-style selection screen for Expert Missions instead of the 1-3 character Parallel Quest style." },
        new() { Key = "exception_handler", DisplayName = "Enable exception handler", Description = "Enables the patcher's built-in crash exception handler. Mainly useful for debugging." },
        new() { Key = "excessive_air_contamination", DisplayName = "Online Mode", Description = "Enables online functionalities with Xenoverse 2 Modded. All involved players need to have the same mods in order to play." }
    };
}
