using System;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using XenoSyncLauncher.Services;
namespace XenoSyncLauncher.Settings;
public partial class SettingsWindow : Window
{
    /// <summary>Populated only when the user clicks "Save" (DialogResult == true).</summary>
    public LauncherSettings ResultSettings { get; private set; }

    /// <summary>Set when the user confirms the "Repair on next Update" prompt this session. Merged into ForceReinstallOnNextUpdate on Save.</summary>
    private bool _repairRequested;

    public SettingsWindow(LauncherSettings currentSettings)
    {
        InitializeComponent();
        ResultSettings = currentSettings;
        VanillaPathTextBox.Text = currentSettings.VanillaPath ?? string.Empty;
        ModdedPathTextBox.Text = currentSettings.ModdedPath ?? string.Empty;
        SpeedLimitTextBox.Text = currentSettings.SpeedLimitMbps.ToString(CultureInfo.InvariantCulture);
        AutoUpdateCheckBox.IsChecked = currentSettings.AutoUpdateEnabled;
        DepotDownloaderPathTextBox.Text = currentSettings.DepotDownloaderPath ?? string.Empty;
        SteamUsernameTextBox.Text = currentSettings.SteamUsername ?? string.Empty;
        if (currentSettings.SteamLoginMethod == "Credentials")
            CredentialsLoginRadio.IsChecked = true;
        else
            QrLoginRadio.IsChecked = true;

        // If a repair was already scheduled from a previous Settings visit
        // (Saved but no Update run yet since), reflect that on reopen instead
        // of silently losing the "still pending" indicator.
        if (currentSettings.ForceReinstallOnNextUpdate)
        {
            RepairStatusText.Text = "A repair is already scheduled for the next Update.";
            RepairStatusText.Visibility = Visibility.Visible;
        }
    }
    private void BrowseVanillaButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the Vanilla folder" };
        if (dialog.ShowDialog() == true)
            VanillaPathTextBox.Text = dialog.FolderName;
    }
    private void BrowseModdedButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the Modded folder" };
        if (dialog.ShowDialog() == true)
            ModdedPathTextBox.Text = dialog.FolderName;
    }
    private void BrowseDepotDownloaderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select DepotDownloader.exe",
            Filter = "DepotDownloader executable (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
            DepotDownloaderPathTextBox.Text = dialog.FileName;
    }

    /// <summary>
    /// Opens the advanced xv2patcher.ini flags editor for whatever Modded
    /// path is currently typed into ModdedPathTextBox - deliberately reads
    /// the live textbox value (like the other buttons in this window do)
    /// rather than the last-saved ResultSettings.ModdedPath, so it works
    /// with an edit that hasn't been Saved yet too.
    /// XV2PatcherFlagsWindow itself already handles a missing/not-yet-installed
    /// xv2patcher.ini gracefully (shows a status message and disables Save),
    /// so the only thing guarded against here is an entirely empty path,
    /// where opening the window wouldn't be meaningful at all.
    /// </summary>
    private void XV2PatcherFlagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ModdedPathTextBox.Text))
        {
            MessageBox.Show(this, "Set the Modded folder above before editing XV2Patcher flags.",
                "XV2Patcher Flags", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new XV2PatcherFlagsWindow(ModdedPathTextBox.Text) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Schedules a full repair for the next Update: XV2Patcher/Revamp get
    /// forced through UpdateTaskPlanner (via ForceReinstallOnNextUpdate,
    /// merged into ResultSettings on Save), and every currently-enabled mod
    /// gets marked NeedsUpdate so MainWindow's
    /// EnsureMandatoryModsInstalledAsync reinstalls it too - see
    /// MainWindow.MarkAllEnabledModsForReinstall, which actually applies
    /// that second half once this window reports back a newly-set
    /// ForceReinstallOnNextUpdate. Only takes effect once Save is also
    /// clicked - closing via Cancel after this discards it, matching every
    /// other setting in this window.
    /// </summary>
    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "This will force XV2Patcher, Revamp, and every currently-enabled mod to be reinstalled the next time you click Update. " +
            "This can take a while depending on your connection. Continue?",
            "Repair on next Update", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _repairRequested = true;
        RepairStatusText.Text = "Repair scheduled - click Save, then Update, to apply it.";
        RepairStatusText.Visibility = Visibility.Visible;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        double speedLimit = double.TryParse(SpeedLimitTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        ResultSettings = new LauncherSettings
        {
            VanillaPath = string.IsNullOrWhiteSpace(VanillaPathTextBox.Text) ? null : VanillaPathTextBox.Text,
            ModdedPath = string.IsNullOrWhiteSpace(ModdedPathTextBox.Text) ? null : ModdedPathTextBox.Text,
            InstallType = ResultSettings.InstallType,
            SpeedLimitMbps = speedLimit,
            AutoUpdateEnabled = AutoUpdateCheckBox.IsChecked == true,
            UseDInput = ResultSettings.UseDInput,
            DepotDownloaderPath = string.IsNullOrWhiteSpace(DepotDownloaderPathTextBox.Text) ? null : DepotDownloaderPathTextBox.Text,
            SteamUsername = string.IsNullOrWhiteSpace(SteamUsernameTextBox.Text) ? null : SteamUsernameTextBox.Text,
            SteamLoginMethod = CredentialsLoginRadio.IsChecked == true ? "Credentials" : "QrCode",
            GameAppId = ResultSettings.GameAppId,
            GameDepotId = ResultSettings.GameDepotId,
            NeedsGameDownload = ResultSettings.NeedsGameDownload,
            RequiredManifestId = ResultSettings.RequiredManifestId,
            ForceReinstallOnNextUpdate = _repairRequested || ResultSettings.ForceReinstallOnNextUpdate,
            InstallDirectory = ResultSettings.InstallDirectory
        };
        DialogResult = true;
        Close();
    }
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
    /// <summary>Botón X de la barra de título personalizada: mismo comportamiento que Cancel.</summary>
    private void BtnClose_Click(object sender, RoutedEventArgs e) => CancelButton_Click(sender, e);
}
