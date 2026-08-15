using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using XenoSyncLauncher.Services;

namespace XenoSyncLauncher.Settings;

public partial class SettingsWindow : Window
{
    /// <summary>Populated only when the user clicks "Save" (DialogResult == true).</summary>
    public LauncherSettings ResultSettings { get; private set; }

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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        double speedLimit = double.TryParse(SpeedLimitTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        ResultSettings = new LauncherSettings
        {
            VanillaPath = string.IsNullOrWhiteSpace(VanillaPathTextBox.Text) ? null : VanillaPathTextBox.Text,
            ModdedPath = ModdedPathTextBox.Text,
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
            RequiredManifestId = ResultSettings.RequiredManifestId
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