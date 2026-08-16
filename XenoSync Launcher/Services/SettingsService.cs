using System;
using System.IO;
using System.Text.Json;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Services;

/// Formato serializable en disco (config.json), separado de WizardContext
/// para no acoplar el fichero de configuración a la forma interna del Wizard.
public class LauncherSettings
{
    public string? VanillaPath { get; set; }
    public string? ModdedPath { get; set; }
    public string InstallType { get; set; } = "NotSelected";

    public double SpeedLimitMbps { get; set; } = 0; // 0 = unlimited
    public bool AutoUpdateEnabled { get; set; } = true;
    public bool UseDInput { get; set; } = false; // false = XInput (default), true = DInput

    // --- DepotDownloader / game-version management ---

    /// Path to DepotDownloader.exe, configured by the user in Settings.
    public string? DepotDownloaderPath { get; set; }

    /// Steam account name used for the DepotDownloader login prompt (Credentials method only).
    public string? SteamUsername { get; set; }

    /// "QrCode" (default, recommended) or "Credentials".
    public string SteamLoginMethod { get; set; } = "QrCode";

    /// Xenoverse 2's Steam AppId (confirmed via SteamDB).
    public string GameAppId { get; set; } = "454650";

    /// Xenoverse 2's main content depot ("SVAC10 Content", confirmed via SteamDB: steamdb.info/app/454650/depots/).
    public string? GameDepotId { get; set; } = "454651";

    /// Set from the Wizard's evaluation result: true when a downgrade or fresh download of the game is required.
    public bool NeedsGameDownload { get; set; }

    /// The ManifestId to fetch via DepotDownloader when NeedsGameDownload is true.>
    public string? RequiredManifestId { get; set; }

    /// Set by the Settings window's Repair button. When true, the next Update
    /// forces XV2Patcher and Revamp to be reinstalled even if they're already
    /// at the latest version, and is cleared automatically once that plan is built.
    public bool ForceReinstallOnNextUpdate { get; set; }

    /// <summary>
    /// Where the launcher's own .exe/files actually live, for self-updating.
    /// Defaults to AppContext.BaseDirectory (wherever it's currently running
    /// from) the first time it's needed - only set explicitly if the launcher
    /// was installed somewhere the self-updater needs to be told about
    /// (rather than assuming "wherever I'm running from right now" is safe,
    /// e.g. if launched via a shortcut from a different working directory).
    /// </summary>
    public string? InstallDirectory { get; set; }
}

public class SettingsService
{
    private static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XenoSyncLauncher");

    private static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool ConfigExists() => File.Exists(ConfigPath);

    public LauncherSettings? Load()
    {
        if (!ConfigExists()) return null;
        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions);
    }

    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    public LauncherSettings FromWizardContext(WizardContext ctx)
    {
        bool needsGameDownload = ctx.EvaluationResult is EvaluationAction.DowngradeRequired or EvaluationAction.FreshDownloadRequired;

        return new LauncherSettings
        {
            VanillaPath = ctx.VanillaPath,
            ModdedPath = ctx.ModdedPath,
            InstallType = ctx.InstallType.ToString(),
            NeedsGameDownload = needsGameDownload,
            RequiredManifestId = needsGameDownload ? ctx.RevampSupportedVersion?.ManifestId : null,
            DepotDownloaderPath = ctx.DepotDownloaderPath,
            // Carried over from DepotDownloaderSetupPage so the very first
            // real Update (which is what actually runs DepotDownloader)
            // already has the login method the user picked during the
            // Wizard, instead of silently falling back to the QrCode
            // default with no username until Settings is visited manually.
            SteamUsername = ctx.SteamUsername,
            SteamLoginMethod = ctx.SteamLoginMethod
        };
    }
}